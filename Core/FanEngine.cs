// ==================================================
// FanEngine.cs - ファン制御エンジン (専用スレッドで周期実行・プロファイル・仮想センサー・
//                キャリブレーション・ポンプ監視・緊急時全開・BIOS復帰・異常値フィルター・
//                スリープ復帰・共振回避・手動テスト・GPU ファン保護・見張りプロセスへの生存通知・
//                書き込みの確認と再送・故障した部品の切り離しと自動の開き直し)
// Dependencies: LibreHardwareMonitorLib (MPL-2.0), .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.Concurrent;
using System.Diagnostics;

namespace ProFanController;

public enum FanState { Active, DryRun, Bios, Paused, Identify, Calibrating, Boost, Emergency, SensorError, Stall, PumpAlert, NotFound, SensorMissing, Manual, GpuGuard }

public enum AlertLevel { Info, Warning, Danger }

/// <summary>
/// エンジン全体の健康状態（トレイのアイコン・ツールチップ用）。
///   Ok         … ふつうに制御している
///   Degraded   … 一部の部品が読めない・一部のファンでエラー・書き込みが反映されない（ほかのファンは制御を続けている）
///   Recovering … エラーが続いたので全ファンを BIOS 制御に戻し、ハードウェアを開き直そうとしている
///   Suspended  … スリープ中（BIOS 制御）
///   Failed     … エンジンのスレッドが予期しないエラーで止まった
/// </summary>
public enum EngineHealth { Starting, Ok, Degraded, Recovering, Suspended, Failed, Stopped }

/// <param name="Missing">センサーそのものが見つからない（部品を外した・交換した）</param>
public sealed record CurveStatus(string Name, double? Temp, double? EffectiveTemp, double Request, bool Error, bool Missing = false);

public sealed record FanStatus(
    string Name, string ControlId, string Role, FanState State,
    double? Duty, double Target, double? Rpm, IReadOnlyList<CurveStatus> Curves);

/// <param name="Suspects">異常値フィルターで「固着」と判定したセンサー（ID → 理由）</param>
/// <param name="Health">エンジン全体の健康状態</param>
public sealed record EngineSnapshot(
    DateTime Time, string Profile, IReadOnlyList<FanStatus> Fans, IReadOnlyDictionary<string, float?> Values,
    bool Paused, bool DryRun, bool Emergency, DateTime? BoostUntil = null,
    IReadOnlyDictionary<string, string>? Suspects = null, EngineHealth Health = EngineHealth.Ok);

public sealed record CalibrationProgress(string ControlId, string FanName, double Progress, string Text);

/// <summary>
/// ハードウェアへのアクセスはすべてこのクラスの専用スレッドで行う。
/// 画面からの操作はコマンドキュー経由で渡し、結果はイベントで受け取る（イベントはエンジンのスレッドで発生する）。
///
/// 1 周期の流れ（PollingIntervalMs ごと。命令が来ても周期は増やさない）:
///   1. 命令を処理する（手動テスト・識別テストは、出力だけすぐに反映する）
///   2. センサーを読む（読めなかった部品の値は使わない）
///   3. 緊急全開の判定 → ファンごとに要求 Duty を計算 → ポンプから先に書き込む
///   4. 前の周期に書いた値が実機に反映されているか確かめ、反映されていなければ書き直す
/// </summary>
public sealed class FanEngine : IDisposable
{
    /// <summary>ポンプはどんな設定でもこれ未満の Duty にしない（停止＝CPU過熱を防ぐ）</summary>
    public const double PumpHardMinDuty = 40.0;
    private const int IdentifySeconds = 8;
    /// <summary>キャリブレーション中、そのファンの温度（カーブ・目標温度・GPU。なければ緊急全開の判定に使うセンサー）がこれを超えたら中止する</summary>
    private const double CalibrationAbortTemp = 80.0;
    /// <summary>緊急全開を解除するのは、しきい値からこの温度（°C）だけ下がったとき</summary>
    private const double EmergencyReleaseMargin = 5.0;
    /// <summary>スリープから復帰してから、ハードウェアを開き直すまでの待ち時間（ミリ秒。ドライバの準備ができるまで）</summary>
    private const long ResumeDelayMs = 4000;
    /// <summary>起動・スリープ復帰・開き直しの直後、ポンプの回転数が読めなくても警告しない時間（ミリ秒。センサーの準備中）</summary>
    public const long StartupGraceMs = 12_000;
    /// <summary>制御ループ全体のエラーがこの回数続いたら、BIOS 制御に戻してハードウェアを開き直す</summary>
    private const int MaxConsecutiveTickErrors = 5;
    /// <summary>1 つのファンのエラーがこの回数続いたら警告する</summary>
    private const int FanFailureAlertCount = 5;
    /// <summary>この周期の数だけ正常に動いたら、開き直しの待ち時間を最初（5 秒）に戻す</summary>
    private const int HealthyTicksToResetBackoff = 60;
    /// <summary>GPU のファンを制御しているとき、GPU 温度がこれ以上なら（設定に関係なく）そのファンを全開にする</summary>
    public const double GpuGuardCoreTemp = 85.0;
    public const double GpuGuardHotspotTemp = 100.0;
    /// <summary>見張りプロセスに「生きている」ことを知らせる名前付きイベント</summary>
    public const string HeartbeatName = @"Global\Zeph-Iris.Heartbeat";

    /// <summary>Calibrator・SensorGuard には「経過時間だけが意味を持つ時刻」を渡す（時計合わせで時刻が飛んでも狂わないように）</summary>
    private static readonly DateTime MonoEpoch = new(2000, 1, 1);
    private static readonly double TimestampToMs = 1000.0 / Stopwatch.Frequency;

    private sealed class FanRuntime
    {
        public required FanConfig Cfg { get; init; }
        public required List<CurveEvaluator> Curves { get; init; }
        public IFanControl? Control { get; init; }
        /// <summary>制御上の今の Duty（なめらかに変化させる途中の値。書き込んだ値とは 1% 未満ずれることがある）</summary>
        public double? Duty { get; set; }
        public int StallTicks { get; set; }
        public bool StallAlerted { get; set; }
        public int PumpLowTicks { get; set; }
        public int PumpOkTicks { get; set; }
        public bool PumpFailsafe { get; set; }
        /// <summary>目標温度モード: PI 制御の途中状態</summary>
        public PiState Pi { get; set; } = new();
        /// <summary>共振回避帯の中で、どちらの端にいるか（帯の真ん中付近で上下に揺れないように）</summary>
        public int BandSide { get; set; }
        public bool GpuGuard { get; set; }
        /// <summary>GPU 保護の切り替えを確かめている回数（1 回だけの異常な読み取りで切り替えない）</summary>
        public int GpuGuardCount;
        /// <summary>前の周期の状態（状態が変わったときは、変化が小さくても必ず書き込む）</summary>
        public FanState LastState = FanState.Active;
        /// <summary>最後に実機へ書き込んだ Duty（書き込んでいない・BIOS に戻したときは null）</summary>
        public double? LastWritten;
        /// <summary>最後に SetSoftware に渡した値（書き直しのとき、同じ値で省略されないようにずらすため）</summary>
        public float? LastSent;
        /// <summary>書き込んだ値の読み戻しの確認状態</summary>
        public ReadbackCheck Readback;
        /// <summary>回り始めの後押しの状態</summary>
        public SpinUpState SpinUp;
        /// <summary>センサー異常が続いている周期の数</summary>
        public int ErrorTicks;
        /// <summary>このファンの処理で例外が続いた回数</summary>
        public int Failures;
        /// <summary>ポンプの回転数が読めなくなった時刻（ミリ秒。読めているときは -1）</summary>
        public long PumpRpmNullSinceMs = -1;
        /// <summary>ポンプの回転数が読めないことを警告済み</summary>
        public bool PumpRpmLost;
    }

    /// <summary>緊急全開を判定する実センサー（BuildRuntime で作っておく）</summary>
    private sealed class EmergencySensor
    {
        public required string Id { get; init; }
        public string Name { get; set; } = "";
        public double Limit { get; set; }
        public bool NameResolved { get; set; }
    }

    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    /// <summary>このツールが書き込んだ端子 → 最後に書いた値（終了時・設定から外したときに BIOS へ戻すため）</summary>
    private readonly Dictionary<IFanControl, float> _touched = new();
    /// <summary>
    /// BIOS に戻した端子 → (戻す前に書いていた値, 残りの周期数)。
    /// LHM は他のソフトがチップを使用中だと、戻す命令も例外なしに黙って飛ばすため、あとの数周期でもう一度戻し直す
    /// </summary>
    private readonly Dictionary<IFanControl, (float Last, int Ticks)> _pendingRestore = new();
    /// <summary>識別テストの期限（ControlId → 単調時計のミリ秒）</summary>
    private readonly Dictionary<string, long> _identify = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float?> _virtualValues = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>この周期の、異常値フィルターを通した温度（制御に使うセンサーだけ）</summary>
    private readonly Dictionary<string, float?> _guarded = new(StringComparer.OrdinalIgnoreCase);
    private readonly SensorGuard _guard = new();
    /// <summary>手動テスト・自動調整で一時的に固定する Duty（ControlId → (Duty, 期限: 単調時計のミリ秒)）</summary>
    private readonly Dictionary<string, (double Duty, long UntilMs)> _overrides = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>次の周期を待たずに出力だけ反映したい端子（手動テスト・識別テスト）</summary>
    private readonly HashSet<string> _pendingOutput = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>1 周期分の結果（毎周期作り直さないよう使い回す）</summary>
    private readonly Dictionary<FanRuntime, FanStatus> _results = new();
    private readonly Dictionary<string, double> _leaderDuty = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EmergencySensor> _emergencySensors = new();
    /// <summary>今の緊急全開のきっかけになった（しきい値付近まで上がった）センサー</summary>
    private readonly HashSet<string> _emergencyTriggers = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _guardIds = new(StringComparer.OrdinalIgnoreCase);
    private EventWaitHandle? _heartbeat;
    private bool _suspended;
    /// <summary>復帰後にハードウェアを開き直す時刻（単調時計のミリ秒。-1 は未定）</summary>
    private long _resumeAtMs = -1;
    /// <summary>スリープの知らせのあと、復帰の知らせが来ないまま動き続けた周期の数（取りこぼし対策）</summary>
    private int _suspendedTicks;
    /// <summary>開き直しに成功したら Resumed を知らせる</summary>
    private bool _resumePending;
    private bool _catalogGpuFans;
    /// <summary>最後に作ったハードウェアの一覧の温度センサー（緊急全開で必ず見る CPU・GPU の温度を探す）</summary>
    private IReadOnlyList<SensorInfo> _catalogTemps = Array.Empty<SensorInfo>();
    private bool _catalogPublished;
    private volatile TaskCompletionSource<bool>? _suspendWaiter;

    private Thread? _thread;
    private int _stopFlag;
    private volatile bool _restoredOk = true;
    private IFanHardware? _hub;
    private AppConfig _cfg = new();
    private string _profile = "";
    private List<FanRuntime> _fans = new();
    /// <summary>処理する順番（ポンプが先。表示は _fans の順のまま）</summary>
    private FanRuntime[] _order = Array.Empty<FanRuntime>();
    private Calibrator? _calibration;
    /// <summary>
    /// キャリブレーション中に見張る温度（自分の温度カーブがないファン）。始めたときに読めていた、緊急全開で見る温度のすべて。
    /// どれか 1 つでも読めなくなったら中止する（ほかの部品の温度が読めても、代わりにしない）
    /// </summary>
    private List<string> _calibrationWatch = new();
    private bool _paused;
    private bool _emergency;
    private long _emergencySinceMs;
    private bool _emergencyLostAlerted;
    private DateTime? _boostUntil;
    private int _tickErrors;
    private int _commandErrors;
    private int _healthyTicks;
    /// <summary>ハードウェアを開き直した回数（待ち時間 5 → 15 → 60 秒を決める）</summary>
    private int _reopenAttempt;
    private long _reopenAtMs;
    private bool _recoverAlerted;
    /// <summary>この時刻までは、ポンプの回転数が読めない・ファンが止まっていることを警告しない（単調時計のミリ秒）</summary>
    private long _graceUntilMs;
    private long _lastTickMs = -1;
    private double _tickDt = 1.0;
    private volatile int _health = (int)EngineHealth.Starting;

    public event Action<HardwareCatalog>? CatalogReady;
    public event Action<EngineSnapshot>? SnapshotReady;
    public event Action<string, AlertLevel>? Alert;
    public event Action<CalibrationProgress>? CalibrationProgressChanged;
    /// <summary>スリープから復帰して、ハードウェアを開き直した</summary>
    public event Action? Resumed;
    /// <summary>結果（失敗時は null）とメッセージ</summary>
    public event Action<string, CalibrationResult?, string>? CalibrationFinished;
    /// <summary>エンジンの健康状態が変わった（エンジンのスレッドで発生する。理由は画面表示用の短い文）</summary>
    public event Action<EngineHealth, string>? HealthChanged;

    /// <summary>エンジンの健康状態（どのスレッドから読んでもよい）</summary>
    public EngineHealth Health => (EngineHealth)_health;

    /// <summary>テスト用: ハードウェアの作り方（普段は LHM を使う HardwareHub）</summary>
    internal Func<IFanHardware> HardwareFactory { get; set; } = () => new HardwareHub();

    /// <summary>テスト用: 単調に増える時計（ミリ秒）。普段は Stopwatch（時計合わせ・夏時間の影響を受けない）</summary>
    internal Func<long> ClockMs { get; set; } = () => (long)(Stopwatch.GetTimestamp() * TimestampToMs);

    private bool StopRequested => Volatile.Read(ref _stopFlag) != 0;
    private bool IsRunning => _testMode || (_thread is { IsAlive: true } && !StopRequested);
    /// <summary>テストでスレッドを使わずに動かしている</summary>
    private bool _testMode;

    public void Start(AppConfig cfg, string profile)
    {
        _cfg = cfg.Clone();
        _profile = profile;
        _thread = new Thread(Run) { IsBackground = true, Name = "FanEngine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>画面で編集した設定を反映する（呼び出し側でコピーを渡すこと）</summary>
    public void ApplyConfig(AppConfig cfg, string profile) => Enqueue(() =>
    {
        bool profileChanged = profile != _profile;
        _cfg = cfg;
        _profile = profile;
        ApplyDimmSetting();
        if (_hub is not null && _cfg.Safety.AllowGpuFanControl != _catalogGpuFans) PublishCatalog();
        BuildRuntime();
        if (profileChanged) Logger.Info($"プロファイルを切り替え: {profile}");
    });

    /// <summary>
    /// 手動テスト・自動調整: 指定時間だけ、そのファンを決まった Duty に固定する（null で解除）。
    /// 緊急全開とポンプ保護は、これより優先される。固定した値は次の周期を待たずにすぐ書き込む
    /// </summary>
    public void SetOverride(string controlId, double? duty, TimeSpan duration) => Enqueue(() =>
    {
        if (duty is null)
        {
            _overrides.Remove(controlId);
            return;
        }
        // 長すぎる期間（TimeSpan.MaxValue など）で計算があふれないよう、最長 1 日にする
        long ms = (long)Math.Clamp(duration.TotalMilliseconds, 0, 86_400_000);
        _overrides[controlId] = (Math.Clamp(duty.Value, 0, 100), ClockMs() + ms);
        _pendingOutput.Add(controlId);
    });

    public void ClearOverrides() => Enqueue(_overrides.Clear);

    /// <summary>
    /// スリープに入る: 全ファンを BIOS 制御に戻して、復帰まで何もしない。
    /// キャリブレーション・手動テスト・識別テストは中止し、目標温度モードの途中状態も捨てる（復帰後は一から）。
    /// 戻り値の Task は BIOS 制御に戻し終わったら完了する（結果: 全部戻せたら true）。呼び出し側は 2 秒ほど待つとよい
    /// </summary>
    public Task<bool> Suspend()
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!IsRunning)
        {
            done.TrySetResult(true);
            return done.Task;
        }
        _suspendWaiter = done;
        Enqueue(() =>
        {
            bool ok = false;
            try
            {
                _suspended = true;
                _resumeAtMs = -1;
                _suspendedTicks = 0;
                AbortCalibration(L("スリープに入ったため中止しました。"));
                _overrides.Clear();
                _identify.Clear();
                _pendingOutput.Clear();
                foreach (var f in _fans) f.Pi = new PiState();
                ok = RestoreAll();
                SetHealth(EngineHealth.Suspended, L("スリープ中"));
                Logger.Info(ok ? "スリープ: BIOS 制御に戻しました" : "スリープ: BIOS 制御に戻せなかった端子があります");
            }
            finally
            {
                done.TrySetResult(ok);
            }
        });
        return done.Task;
    }

    /// <summary>スリープから復帰: 少し待ってからハードウェアを開き直し、制御を再開する</summary>
    public void Resume() => Enqueue(() =>
    {
        // スリープの知らせを取りこぼしていた場合も、書き込んだままの端子を BIOS に戻してから開き直す
        if (!_suspended && _touched.Count > 0) RestoreAll();
        _suspended = true;
        _resumeAtMs = ClockMs() + ResumeDelayMs;
        SetHealth(EngineHealth.Suspended, L("スリープから復帰中"));
        Logger.Info("スリープから復帰: ハードウェアの準備を待っています");
    });

    /// <summary>
    /// ハードウェアを開き直して、一覧を作り直す（CatalogReady がもう一度発生する）。
    /// LHM は開いたときにしか部品を探さないので、起動直後に部品がまだ見えていなかったとき（HardwareChange の NotReady）に、
    /// 10〜30 秒待ってから呼ぶ。開き直す間（1 周期ほど）は BIOS 制御になる
    /// </summary>
    public void RescanHardware() => Enqueue(() =>
    {
        if (_suspended) return;
        RestoreAll();
        DisposeHub();
        _catalogPublished = false;
        _reopenAttempt = 0;
        _reopenAtMs = ClockMs();
        SetHealth(EngineHealth.Recovering, L("ハードウェアを確認し直しています"));
        Logger.Info("ハードウェアを確認し直します（開き直し）");
    });

    /// <summary>指定した端子を一時的に 100% にして、どのファンかを目視で確認できるようにする</summary>
    public void Identify(string controlId) => Enqueue(() =>
    {
        _identify[controlId] = ClockMs() + IdentifySeconds * 1000L;
        _pendingOutput.Add(controlId);
    });

    /// <summary>一時停止中は全ファンを BIOS 制御に戻す（ポンプの監視と保護は続ける）</summary>
    public void SetPaused(bool paused) => Enqueue(() =>
    {
        _paused = paused;
        Logger.Info(paused ? "一時停止 (BIOS 制御)" : "制御を再開");
    });

    /// <summary>ブースト: 指定時刻まで、ポンプ以外の全ファンを 100% にする（null で解除）</summary>
    public void SetBoost(DateTime? until) => Enqueue(() =>
    {
        _boostUntil = until;
        Logger.Info(until is null ? "ブースト解除" : $"ブースト開始（{until:HH:mm:ss} まで）");
    });

    public void StartCalibration(string controlId) => Enqueue(() =>
    {
        if (_calibration is not null)
        {
            CalibrationFinished?.Invoke(controlId, null, L("別のファンのキャリブレーションが実行中です。"));
            return;
        }
        var fan = FindFan(controlId);
        if (fan?.Control is null || fan.Cfg.RpmSensorId is null)
        {
            CalibrationFinished?.Invoke(controlId, null, L("この端子は回転数を読めないため、キャリブレーションできません。"));
            return;
        }
        if (fan.GpuGuard || fan.PumpFailsafe)
        {
            CalibrationFinished?.Invoke(controlId, null, L("保護機能（GPU 保護・ポンプ保護）が働いているため、今はキャリブレーションできません。"));
            return;
        }
        if (fan.Cfg.IsPump)
        {
            CalibrationFinished?.Invoke(controlId, null, L("ポンプは停止させると危険なため、キャリブレーションの対象外です。"));
            return;
        }
        if (_suspended || _hub is null)
        {
            CalibrationFinished?.Invoke(controlId, null, L("ハードウェアの準備ができていないため、今はキャリブレーションできません。"));
            return;
        }
        if (CalibrationWatch(fan) is not { } watch)
        {
            CalibrationFinished?.Invoke(controlId, null, L("見張る温度を読めないため、キャリブレーションできません。温度センサーが読めるようになってから、もう一度実行してください。"));
            return;
        }
        _calibrationWatch = watch;
        _calibration = new Calibrator(controlId, fan.Cfg.Name, Mono(ClockMs()));
        Logger.Info($"キャリブレーション開始: {fan.Cfg.Name}");
    });

    public void CancelCalibration() => Enqueue(() => _calibration?.Fail(L("中止しました。")));

    /// <summary>
    /// エンジンを止め、触ったすべての端子を BIOS 制御に戻す。何度呼んでもよい。
    /// 5 秒以内に止まり、すべての端子を BIOS 制御に戻せたら true（false なら見張りプロセスに安全な回転数を任せる）
    /// </summary>
    public bool Stop()
    {
        var thread = _thread;
        if (thread is null) return true; // 一度も動いていない（何も書き込んでいない）
        if (Interlocked.Exchange(ref _stopFlag, 1) == 0) _wake.Set();
        if (Thread.CurrentThread == thread) return false; // 自分自身は待てない（止まるのはこのあと）
        return thread.Join(TimeSpan.FromSeconds(5)) && _restoredOk;
    }

    public void Dispose() => Stop();

    private void Enqueue(Action command)
    {
        _commands.Enqueue(command);
        _wake.Set();
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static DateTime Mono(long ms) => MonoEpoch.AddMilliseconds(ms);

    private FanRuntime? FindFan(string? controlId)
    {
        foreach (var f in _fans)
        {
            if (Same(f.Cfg.ControlId, controlId)) return f;
        }
        return null;
    }

    // =====================================================================
    // スレッドの本体
    // =====================================================================

    private void Run()
    {
        try
        {
            try { _heartbeat = new EventWaitHandle(false, EventResetMode.AutoReset, HeartbeatName); }
            catch (Exception ex) { Logger.Warn($"見張りプロセス用のイベントを作れません: {ex.Message}"); }
            HookGuardEvents();

            OpenHardware(ClockMs());
            // 最初の周期でファンに書き込む前に 1 回知らせる（見張りは最初の合図を受けてから止まりを数えるので、
            // 最初の周期の途中で止まったときも、安全な回転数にできるように）
            try { _heartbeat?.Set(); } catch { /* 見張りがいなくても制御は続ける */ }
            long nextTick = ClockMs();
            while (!StopRequested)
            {
                DrainCommands();
                long now = ClockMs();
                int interval = Math.Max(50, _cfg.PollingIntervalMs);
                // 周期を短くする設定変更は、次の周期を待たずに反映する
                if (nextTick - now > interval) nextTick = now + interval;
                if (now >= nextTick)
                {
                    RunOneTick(now);
                    // 見張りプロセスへ「制御ループは動いている」と知らせる（BIOS 制御で開き直しを待っている間も、ループ自体は生きている）
                    try { _heartbeat?.Set(); } catch { /* 見張りがいなくても制御は続ける */ }
                    nextTick = FanSafety.NextTickMs(nextTick, ClockMs(), interval);
                }
                else
                {
                    // 命令で起こされただけのとき: センサーは読まず、手動テストなどの出力だけ反映する
                    RefreshOutputs(now);
                }
                long wait = nextTick - ClockMs();
                if (wait > 0) _wake.WaitOne((int)Math.Min(wait, int.MaxValue));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"エンジンが予期しないエラーで止まりました: {ex}");
            RaiseAlert(L($"ファン制御が予期しないエラーで止まりました。BIOS 制御に戻します: {ex.Message}"), AlertLevel.Danger);
            SetHealth(EngineHealth.Failed, ex.Message);
        }
        finally
        {
            _restoredOk = RestoreAll();
            DisposeHub();
            _heartbeat?.Dispose();
            _suspendWaiter?.TrySetResult(_restoredOk);
            SetHealth(EngineHealth.Stopped, L("停止"));
            Logger.Info("エンジン停止");
        }
    }

    /// <summary>テスト用: スレッドを使わずに、ハードウェアを開いて制御の準備をする</summary>
    internal void InitForTest(AppConfig cfg, string profile)
    {
        _cfg = cfg;
        _profile = profile;
        _testMode = true;
        HookGuardEvents();
        OpenHardware(ClockMs());
    }

    /// <summary>テスト用: 命令を処理してから 1 周期進める（スレッドの 1 周と同じ）</summary>
    internal void StepForTest()
    {
        DrainCommands();
        RunOneTick(ClockMs());
    }

    /// <summary>テスト用: 命令だけ処理する（周期の途中で命令が届いたときと同じ）</summary>
    internal void DrainForTest()
    {
        DrainCommands();
        RefreshOutputs(ClockMs());
    }

    /// <summary>テスト用: 任意の命令を入れる</summary>
    internal void EnqueueForTest(Action command) => Enqueue(command);

    /// <summary>テスト用: 終了時と同じように全端子を BIOS に戻す</summary>
    internal bool RestoreAllForTest() => RestoreAll();

    /// <summary>テスト用: まだ処理していない命令の数</summary>
    internal int PendingCommandCount => _commands.Count;

    private void HookGuardEvents()
    {
        _guard.StuckDetected -= OnStuckDetected;
        _guard.StuckDetected += OnStuckDetected;
        _guard.StuckCleared -= OnStuckCleared;
        _guard.StuckCleared += OnStuckCleared;
    }

    private void OnStuckDetected(string id, string reason)
        => RaiseAlert(L($"温度センサー「{_hub?.NameOf(id) ?? id}」の値が固まっています（{reason}）。このセンサーは異常として扱い、ファンを全開にしています。"), AlertLevel.Warning);

    private static void OnStuckCleared(string id) => Logger.Info($"センサーの固着が解消: {id}");

    /// <summary>
    /// 溜まった命令を順に実行する。1 つの命令が失敗しても、ほかの命令とエンジンは止めない
    /// （以前は 1 つの例外でスレッドごと止まり、ファンが最後の値のまま残っていた）
    /// </summary>
    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var cmd))
        {
            try
            {
                cmd();
            }
            catch (Exception ex)
            {
                Logger.Error($"命令の処理で例外: {ex}");
                // 同じ失敗が続いても通知を出しすぎないよう、最初の 3 回と以降 50 回ごとだけ知らせる
                if (++_commandErrors <= 3 || _commandErrors % 50 == 0)
                    RaiseAlert(L($"操作をファン制御に反映できませんでした: {ex.Message}"), AlertLevel.Warning);
            }
        }
    }

    /// <summary>1 周期分の処理。例外は数えて、続くようならハードウェアを開き直す</summary>
    private void RunOneTick(long nowMs)
    {
        try
        {
            if (_suspended) TickSuspended(nowMs);
            else if (_hub is null) TickRecovering(nowMs);
            else
            {
                Tick(nowMs);
                _tickErrors = 0;
                if (_healthyTicks < int.MaxValue) _healthyTicks++;
                // しばらく正常に動いたら、次に失敗したときの待ち時間を最初（5 秒）からにする
                if (_healthyTicks >= HealthyTicksToResetBackoff) _reopenAttempt = 0;
            }
        }
        catch (Exception ex)
        {
            _healthyTicks = 0;
            _tickErrors++;
            if (_tickErrors == 1 || _tickErrors % 60 == 0) Logger.Error($"制御ループで例外（{_tickErrors} 回連続）: {ex}");
            if (_hub is not null && _tickErrors >= MaxConsecutiveTickErrors)
            {
                // 失敗が続く状態では制御を続けず、いったん安全側（BIOS）に倒してから、ハードウェアを開き直す
                RestoreAll();
                DisposeHub();
                ScheduleReopen(nowMs, ex.Message, alertNow: true);
            }
        }
    }

    /// <summary>メモリの温度を読むかどうかを、開いているハードウェアに伝える</summary>
    private void ApplyDimmSetting()
    {
        if (_hub is not HardwareHub hub) return;
        bool skip = !_cfg.Safety.ReadMemoryTemperature;
        if (hub.SkipDimm == skip) return;
        hub.SkipDimm = skip;
        Logger.Info(skip ? "メモリの温度を読まない設定にしました（SMBus を使いません）" : "メモリの温度を読む設定にしました");
    }

    /// <summary>ハードウェアを開いて制御の準備をする。失敗したら、少し待ってから開き直す予定を入れる</summary>
    private bool OpenHardware(long nowMs)
    {
        IFanHardware? hub = null;
        try
        {
            hub = HardwareFactory();
            _hub = hub;
            ApplyDimmSetting();
            if (!_catalogPublished || _cfg.Safety.AllowGpuFanControl != _catalogGpuFans)
            {
                try { PublishCatalog(); }
                catch (Exception ex) { Logger.Error($"ハードウェアの一覧を作れません: {ex}"); }
            }
            // 新しく開いたハードウェアでは、書き込みの状態を一から始める（スリープ中に BIOS が設定を戻していることもある）
            _touched.Clear();
            _pendingRestore.Clear();
            foreach (var f in _fans)
            {
                ResetOutputState(f);
                f.Duty = null; // 今の実機の回転から滑らかに再開する
            }
            _guard.Reset();
            _lastTickMs = -1;
            _tickErrors = 0;
            _healthyTicks = 0;
            _graceUntilMs = nowMs + StartupGraceMs;
            BuildRuntime();
        }
        catch (Exception ex)
        {
            Logger.Error($"ハードウェアを開けません: {ex}");
            if (hub is not null)
            {
                try { hub.Dispose(); } catch { /* 開きかけのものは捨てるだけ */ }
            }
            _hub = null;
            // スリープ復帰の直後はドライバの準備が遅れることがあるので、3 回続けて失敗するまでは知らせない
            ScheduleReopen(nowMs, ex.Message, alertNow: !_resumePending || _reopenAttempt >= 2);
            return false;
        }

        if (_recoverAlerted)
        {
            _recoverAlerted = false;
            RaiseAlert(L("ハードウェアを開き直し、ファン制御を再開しました。"), AlertLevel.Info);
        }
        SetHealth(EngineHealth.Ok, L("制御中"));
        if (_resumePending)
        {
            _resumePending = false;
            Logger.Info("スリープから復帰: 制御を再開しました");
            Resumed?.Invoke();
        }
        return true;
    }

    /// <summary>開き直しの予定を入れる（待ち時間は 5 → 15 → 60 秒。ファンはその間 BIOS 制御）</summary>
    /// <param name="alertNow">false なら、ログだけ残して通知はまだ出さない</param>
    private void ScheduleReopen(long nowMs, string reason, bool alertNow)
    {
        int delay = FanSafety.ReopenDelayMs(_reopenAttempt);
        _reopenAttempt++;
        _reopenAtMs = nowMs + delay;
        SetHealth(EngineHealth.Recovering, L($"BIOS 制御で待機中（{delay / 1000} 秒後に開き直します）"));
        Logger.Warn($"{delay / 1000} 秒後にハードウェアを開き直します（{_reopenAttempt} 回目）: {reason}");
        if (alertNow && !_recoverAlerted)
        {
            _recoverAlerted = true;
            RaiseAlert(L($"ハードウェアを開けない・読み書きのエラーが続いているため、ファンを BIOS 制御に戻しました。自動で開き直しを試みています（{reason}）"), AlertLevel.Danger);
        }
    }

    /// <summary>ハードウェアを開き直す待ちの間（BIOS 制御のまま）</summary>
    private void TickRecovering(long nowMs)
    {
        if (nowMs < _reopenAtMs) return;
        OpenHardware(nowMs);
    }

    private void DisposeHub()
    {
        var hub = _hub;
        _hub = null;
        _pendingRestore.Clear(); // 端子のオブジェクトは開き直すと別物になる
        if (hub is null) return;
        try { hub.Dispose(); }
        catch (Exception ex) { Logger.Warn($"ハードウェアの解放に失敗: {ex.Message}"); }
    }

    private void PublishCatalog()
    {
        _catalogGpuFans = _cfg.Safety.AllowGpuFanControl;
        var catalog = _hub!.BuildCatalog(_catalogGpuFans);
        _catalogPublished = true;
        _catalogTemps = catalog.Temperatures;
        Logger.Info($"ハードウェア初期化完了: 温度センサー {catalog.Temperatures.Count} 個 / 制御チャネル {catalog.Controls.Count} 個 / PawnIO={catalog.PawnIoInstalled}");
        CatalogReady?.Invoke(catalog);
    }

    /// <summary>スリープ中・復帰直後: ハードウェアに触らず、準備ができたら開き直す</summary>
    private void TickSuspended(long nowMs)
    {
        // スリープが取り消された・復帰の知らせを取りこぼした場合: 起きている時間が 60 周期続いたら自分で復帰する
        if (_resumeAtMs < 0 && ++_suspendedTicks >= 60)
        {
            Logger.Warn("スリープからの復帰の知らせがないまま動いているため、制御を再開します");
            _resumeAtMs = nowMs;
        }
        if (_resumeAtMs < 0 || nowMs < _resumeAtMs) return;
        _resumeAtMs = -1;
        _suspended = false;
        _resumePending = true;
        // スリープ中に BIOS が設定を戻していることがあるので、ドライバごと開き直して一から制御する
        _touched.Clear();
        DisposeHub();
        _reopenAttempt = 0;
        _reopenAtMs = nowMs;
        TickRecovering(nowMs);
    }

    private void BuildRuntime()
    {
        var old = new Dictionary<string, FanRuntime>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in _fans) old.TryAdd(f.Cfg.ControlId, f);
        var list = new List<FanRuntime>();
        var profileFans = _cfg.FindProfile(_profile)?.Fans ?? _cfg.Profiles.FirstOrDefault()?.Fans ?? new();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fc in profileFans)
        {
            if (fc is null) continue;
            // 同じ端子が 2 回出てくる設定（手で編集した・古い版の取り込み）は、最初の 1 つだけ使う
            if (!seen.Add(fc.ControlId))
            {
                Logger.Warn($"'{fc.Name}': 端子 '{fc.ControlId}' が重複しているため、2 つ目以降は使いません。");
                continue;
            }
            // GPU のファンは、設定で許可しているときだけ制御する（オフにしたら GPU ドライバーの自動制御に返す）
            bool gpuBlocked = fc.IsGpuFan && !_cfg.Safety.AllowGpuFanControl;
            var rt = new FanRuntime
            {
                Cfg = fc,
                Control = gpuBlocked || _hub is null ? null : _hub.FindControl(fc.ControlId),
                Curves = fc.Curves.Select(c => new CurveEvaluator(c)).ToList(),
            };
            if (rt.Control is null && _hub is not null)
            {
                Logger.Warn(gpuBlocked
                    ? $"'{fc.Name}': GPU のファンの制御がオフのため、GPU ドライバーに任せます。"
                    : $"'{fc.Name}': ControlId '{fc.ControlId}' が見つかりません。");
            }

            // 設定変更で回転が跳ねないよう、現在の Duty・平滑化状態・ポンプ保護状態・書き込みの確認状態を引き継ぐ
            if (old.TryGetValue(fc.ControlId, out var prev))
            {
                rt.Duty = prev.Duty;
                rt.PumpFailsafe = prev.PumpFailsafe;
                rt.PumpLowTicks = prev.PumpLowTicks;
                rt.PumpOkTicks = prev.PumpOkTicks;
                rt.PumpRpmNullSinceMs = prev.PumpRpmNullSinceMs;
                rt.PumpRpmLost = prev.PumpRpmLost;
                rt.StallAlerted = prev.StallAlerted;
                rt.StallTicks = prev.StallTicks;
                rt.BandSide = prev.BandSide;
                rt.GpuGuard = prev.GpuGuard;
                rt.GpuGuardCount = prev.GpuGuardCount;
                rt.ErrorTicks = prev.ErrorTicks;
                rt.LastState = prev.LastState;
                if (ReferenceEquals(prev.Control, rt.Control))
                {
                    rt.LastWritten = prev.LastWritten;
                    rt.LastSent = prev.LastSent;
                    rt.Readback = prev.Readback;
                }
                if (prev.Cfg.Mode == fc.Mode && prev.Cfg.TargetSensorId == fc.TargetSensorId) rt.Pi = prev.Pi;
                for (int i = 0; i < Math.Min(rt.Curves.Count, prev.Curves.Count); i++)
                {
                    rt.Curves[i].CopyStateFrom(prev.Curves[i]);
                }
            }
            list.Add(rt);
        }

        // 設定から消えた端子は BIOS に返す
        var keep = new HashSet<IFanControl>();
        foreach (var f in list)
        {
            if (f.Control is not null) keep.Add(f.Control);
        }
        foreach (var c in _touched.Keys.Where(c => !keep.Contains(c)).ToList())
        {
            Restore(c);
        }

        _fans = list;
        // ポンプを先に処理する（ほかのファンで何かあっても、ポンプの書き込みだけは先に済ませる）
        _order = list.Where(f => f.Cfg.IsPump).Concat(list.Where(f => !f.Cfg.IsPump)).ToArray();

        // 異常値フィルターをかけるのは、制御に使っている実センサー
        _guardIds = list.SelectMany(f => f.Curves.Select(c => c.Config.SensorId).Append(f.Cfg.Mode == FanModes.Target ? f.Cfg.TargetSensorId : ""))
            .SelectMany(id => _cfg.VirtualSensors.FirstOrDefault(v => Same(v.Id, id)) is { } vs ? vs.Sources : (IEnumerable<string>)new[] { id })
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _guard.Retain(_guardIds);
        // 緊急温度に近い温度では「固着」と判定しない（本当に熱いのに、異常扱いで緊急全開が外れないように）
        _guard.NoStuckAbove = Math.Min(_cfg.EmergencyTemperature, _cfg.EmergencyHotspotTemperature) - FanSafety.StuckGuardMargin;

        BuildEmergencySensors();
    }

    /// <summary>緊急全開を判定する実センサーの一覧を作る（仮想センサーは元になっている実センサーで判定する。差分センサーは温度そのものではないため対象外）</summary>
    private void BuildEmergencySensors()
    {
        var ids = _fans.SelectMany(f => f.Curves).Select(c => c.Config.SensorId)
            .Concat(_fans.Where(f => f.Cfg.Mode == FanModes.Target).Select(f => f.Cfg.TargetSensorId))
            .SelectMany(id => _cfg.VirtualSensors.FirstOrDefault(v => Same(v.Id, id)) is { } vs
                ? (vs.Kind == VirtualSensorKinds.Delta ? Enumerable.Empty<string>() : vs.Sources)
                : new[] { id })
            .Concat(AlwaysWatchedTemps(_catalogTemps))
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _emergencySensors.Clear();
        foreach (var id in ids)
        {
            var s = new EmergencySensor { Id = id };
            ResolveEmergencySensor(s);
            _emergencySensors.Add(s);
        }
        // 設定から外れたセンサーは、緊急全開を続ける理由にしない
        _emergencyTriggers.RemoveWhere(id => !ids.Contains(id, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// カーブで使っていなくても緊急全開で見る温度: CPU の温度と、GPU ごとのコア・Hot Spot。
    /// ラジエーターのファンを水温だけで回す設定などで、ポンプの不調や GPU の高負荷による CPU・GPU の過熱を見逃さないように
    /// （しきい値はカーブで使っているときと同じ）
    /// </summary>
    internal static IEnumerable<string> AlwaysWatchedTemps(IReadOnlyList<SensorInfo> temps)
    {
        if (temps.Count == 0) yield break;
        if (SensorNames.CpuTemperature(temps) is { } cpu) yield return cpu.Id;
        // GPU は機器ごと（センサーの ID の機器の部分）に分ける。同じ型番の GPU を 2 枚使っていても、両方を見る
        var gpus = temps.Where(t => t.HardwareType.StartsWith("Gpu", StringComparison.Ordinal)).ToList();
        foreach (var key in gpus.Select(HardwareKey).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var own = gpus.Where(t => string.Equals(HardwareKey(t), key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (SensorNames.GpuCoreTemperature(own) is { } core) yield return core.Id;
            if (SensorNames.GpuHotSpotTemperature(own) is { } hot) yield return hot.Id;
        }
    }

    /// <summary>
    /// センサーの機器: ID「/gpu-nvidia/0/temperature/2」の「/gpu-nvidia/0」（種類と番号の 2 段を除いたもの）。
    /// その形でない ID は、部品の名前で分ける
    /// </summary>
    internal static string HardwareKey(SensorInfo s)
    {
        var parts = s.Id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? "/" + string.Join('/', parts[..^2]) : "name:" + s.HardwareName;
    }

    /// <summary>センサーの名前と、そのセンサーの緊急温度を決める（Hot Spot は別のしきい値）</summary>
    private void ResolveEmergencySensor(EmergencySensor s)
    {
        string? name = _hub?.NameOf(s.Id);
        s.Name = name ?? s.Id;
        s.NameResolved = name is not null;
        s.Limit = CurvePresets.Classify(s.Id, s.Name) == SensorKind.GpuHotspot
            ? _cfg.EmergencyHotspotTemperature
            : _cfg.EmergencyTemperature;
    }

    // =====================================================================
    // 1 周期の処理
    // =====================================================================

    private void Tick(long nowMs)
    {
        var hub = _hub!;
        int failedHw = hub.Update();
        // 全部の部品が読めないときは、どの値も信用できないので周期ごと失敗にする（続けば開き直す）
        if (hub.HardwareCount > 0 && failedHw >= hub.HardwareCount)
            throw new IOException(L("すべての部品の読み取りに失敗しました"));
        // 前の周期までに BIOS に戻した端子を、念のためもう一度戻す（戻す命令が黙って飛ばされていたときのため）
        RepeatPendingRestores();

        var wallNow = DateTime.Now;
        var mono = Mono(nowMs);
        // 目標温度モードの PI 制御には、実際に経った時間を渡す（周期の遅れ・設定変更に強くする）
        double nominal = _cfg.PollingIntervalMs / 1000.0;
        _tickDt = _lastTickMs < 0 ? nominal : Math.Clamp((nowMs - _lastTickMs) / 1000.0, 0.05, Math.Max(3 * nominal, 1.0));
        _lastTickMs = nowMs;

        UpdateGuardedTemps(mono);
        UpdateVirtualSensors();
        UpdateEmergency(nowMs);
        StepCalibration(mono);
        _pendingOutput.Clear(); // この周期でまとめて反映する

        if (_boostUntil is DateTime b && wallNow >= b) _boostUntil = null;

        // 連動モード（ファングループ）はリーダーの回転が決まってから処理する。連動の連動もたどれるよう数回まわす。
        // ポンプは先に処理する（_order）
        _results.Clear();
        _leaderDuty.Clear();
        for (int pass = 0; pass < 4 && _results.Count < _order.Length; pass++)
        {
            foreach (var fan in _order)
            {
                if (_results.ContainsKey(fan)) continue;
                bool follower = fan.Cfg.Mode == FanModes.Follow;
                bool leaderReady = follower && _leaderDuty.ContainsKey(fan.Cfg.FollowControlId);
                // 最後の周回では、リーダーが見つからない連動ファンも処理する（センサー異常として全開）
                if (follower && !leaderReady && pass < 3) continue;

                var status = SafeTickFan(fan, nowMs, leaderReady ? _leaderDuty[fan.Cfg.FollowControlId] : null);
                _results[fan] = status;
                // 端子が見つからないリーダーの値（0%）は渡さない。連動するファンは「センサー異常」として全開にする
                if (status.State != FanState.NotFound) _leaderDuty[fan.Cfg.ControlId] = status.Duty ?? status.Target;
            }
        }
        var statuses = new List<FanStatus>(_fans.Count);
        bool fanTrouble = false;
        foreach (var f in _fans)
        {
            statuses.Add(_results[f]);
            if (f.Failures > 0 || f.Readback.Alerted) fanTrouble = true;
        }
        TickOrphanIdentify(nowMs);

        if (failedHw > 0) SetHealth(EngineHealth.Degraded, L($"{failedHw} 個の部品が読めません"));
        else if (fanTrouble) SetHealth(EngineHealth.Degraded, L("一部のファンでエラー"));
        else SetHealth(EngineHealth.Ok, L("制御中"));

        var values = hub.SnapshotValues();
        foreach (var (id, v) in _virtualValues) values[id] = v;

        var suspects = _guard.Suspects.Count > 0 ? new Dictionary<string, string>(_guard.Suspects, StringComparer.OrdinalIgnoreCase) : null;
        SnapshotReady?.Invoke(new EngineSnapshot(wallNow, _profile, statuses, values, _paused, _cfg.DryRun, _emergency, _boostUntil, suspects, Health));
    }

    /// <summary>実センサーの生の温度。0 以下・150 以上・127/128°C（「読めない」を表す値）は異常値として扱う</summary>
    private float? ReadRawTemp(string? id)
    {
        float? t = _hub!.Value(id);
        return FanSafety.IsPlausibleTemp(t) ? t : null;
    }

    /// <summary>制御に使うセンサーの値を、異常値フィルターに通しておく（1 周期に 1 回だけ）</summary>
    private void UpdateGuardedTemps(DateTime mono)
    {
        _guarded.Clear();
        if (!_cfg.Safety.SensorFilterEnabled)
        {
            if (_guard.Suspects.Count > 0) _guard.Reset();
            return;
        }
        foreach (var id in _guardIds)
        {
            float? raw = ReadRawTemp(id);
            double? load = _hub!.LoadFor(id);
            double? v = _guard.Filter(id, raw, load, mono);
            _guarded[id] = v is null ? null : (float)v.Value;
        }
    }

    /// <summary>制御に使う実センサーの温度（異常値フィルター済み）。null は取得失敗</summary>
    private float? ReadPhysicalTemp(string? id)
        => id is not null && _guarded.TryGetValue(id, out var v) ? v : ReadRawTemp(id);

    /// <summary>センサーそのものが見つからない（部品を外した・交換した）。仮想センサーは元のセンサーが全部ないとき</summary>
    private bool IsMissing(string? id)
    {
        if (string.IsNullOrEmpty(id)) return true;
        if (_cfg.VirtualSensors.FirstOrDefault(v => Same(v.Id, id)) is { } vs)
            return vs.Sources.Count == 0 || vs.Sources.All(s => !_hub!.Exists(s));
        return !_hub!.Exists(id);
    }

    /// <summary>カーブが参照する温度（仮想センサーを含む）。null は取得失敗</summary>
    private float? ReadTemp(string? id)
        => id is not null && _virtualValues.TryGetValue(id, out var v) ? v : ReadPhysicalTemp(id);

    private void UpdateVirtualSensors()
    {
        _virtualValues.Clear();
        foreach (var vs in _cfg.VirtualSensors)
        {
            var values = vs.Sources.Select(ReadPhysicalTemp).ToList();
            float? result = vs.Kind switch
            {
                VirtualSensorKinds.Delta => values.Count >= 2 && values[0].HasValue && values[1].HasValue ? values[0] - values[1] : null,
                _ when values.All(v => v is null) => null,
                VirtualSensorKinds.Min => values.Where(v => v.HasValue).Min(),
                VirtualSensorKinds.Average => values.Where(v => v.HasValue).Average(),
                _ => values.Where(v => v.HasValue).Max(),
            };
            _virtualValues[vs.Id] = result;
        }
    }

    /// <summary>
    /// カーブで参照している実センサーのどれかが緊急温度に達したら、全ファンを全開にする。
    /// 解除は「しきい値より 5°C 下がった」かつ「発動から 30 秒以上たった」かつ「きっかけのセンサーが読めている」とき。
    /// きっかけになったセンサーが読めなくなった・固まった場合は、本当に下がったのか分からないので全開を続ける
    /// </summary>
    private void UpdateEmergency(long nowMs)
    {
        EmergencySensor? hot = null;
        double hotTemp = 0;
        bool anyAboveRelease = false;
        string? lostTrigger = null;
        foreach (var s in _emergencySensors)
        {
            if (!s.NameResolved) ResolveEmergencySensor(s); // 起動が遅れて見つかったセンサーの名前・しきい値を決め直す
            float? t = ReadPhysicalTemp(s.Id);
            // 保護の判定には、異常値フィルター（急な変化を 1 周期待つ）を通す前の値も使う:
            // 緊急温度を一気に超える上昇（65 → 105°C など）を、確かめを待たずに最初の周期で全開にするため。
            // ありえない値（127°C などの読み取りエラーの印）は ReadRawTemp で除かれる
            if (ReadRawTemp(s.Id) is float raw && raw >= s.Limit && (t is null || raw > t)) t = raw;
            if (t is float v)
            {
                if (v >= s.Limit && hot is null)
                {
                    hot = s;
                    hotTemp = v;
                }
                if (v >= s.Limit - EmergencyReleaseMargin)
                {
                    anyAboveRelease = true;
                    if (_emergency || v >= s.Limit) _emergencyTriggers.Add(s.Id);
                }
            }
            else if (_emergency && _emergencyTriggers.Contains(s.Id))
            {
                lostTrigger ??= s.Name; // 読めない（null・固着・異常値）
            }
        }

        bool emergency = FanSafety.NextEmergency(_emergency, hot is not null, anyAboveRelease, lostTrigger is not null, nowMs - _emergencySinceMs);
        if (emergency && !_emergency)
        {
            _emergencySinceMs = nowMs;
            _emergencyLostAlerted = false;
            RaiseAlert(L($"{hot!.Name} が {hotTemp:F0}°C に達しました（しきい値 {hot.Limit:F0}°C）。全ファンを 100% にしています。"), AlertLevel.Danger);
        }
        else if (emergency && lostTrigger is not null && !anyAboveRelease && !_emergencyLostAlerted)
        {
            _emergencyLostAlerted = true;
            RaiseAlert(L($"緊急全開のきっかけになった温度センサー「{lostTrigger}」が読めなくなりました。温度が下がったか確認できないため、全開を続けています。"), AlertLevel.Warning);
        }
        else if (!emergency && _emergency)
        {
            _emergencyTriggers.Clear();
            RaiseAlert(L("温度が下がったため、通常の制御に戻りました。"), AlertLevel.Info);
        }
        _emergency = emergency;
    }

    private void StepCalibration(DateTime mono)
    {
        if (_calibration is null) return;
        var cal = _calibration;
        var fan = FindFan(cal.ControlId);

        if (fan?.Control is null)
        {
            cal.Fail(L("設定からファンが削除されました。"));
        }
        else if (_emergency)
        {
            cal.Fail(L("緊急温度に達したため中止しました。"));
        }
        else if (fan.GpuGuard || fan.PumpFailsafe)
        {
            cal.Fail(fan.GpuGuard ? L("GPU の温度が高いため中止しました（GPU 保護で全開にします）。") : L("ポンプ保護が働いたため中止しました。"));
        }
        else if (CalibrationTooHot(fan))
        {
            cal.Fail(L($"温度が {CalibrationAbortTemp:F0}°C を超えたため中止しました。負荷の低い状態で実行してください。"));
        }
        else if (CalibrationTempLost(fan))
        {
            // 温度が分からないまま回転を下げ続けるのは危ないので、測定をやめて通常の制御に戻す
            cal.Fail(L("温度が読めなくなったため中止しました。センサーを確認してから、もう一度実行してください。"));
        }
        else
        {
            cal.Step(_hub!.Value(fan.Cfg.RpmSensorId), mono);
        }

        if (cal.IsDone)
        {
            FinishCalibration(cal, fan);
            return;
        }

        CalibrationProgressChanged?.Invoke(new CalibrationProgress(cal.ControlId, cal.FanName, cal.Progress, cal.StatusText));
    }

    /// <summary>
    /// キャリブレーション（回転を 0% 近くまで下げる）を続けると危ない温度か。
    /// カーブのセンサー・目標温度モードのセンサー・GPU のファンなら GPU 自身の温度を見る。
    /// どれもない（固定・連動モードなど）ときは、緊急全開の判定に使うセンサーで判断する
    /// </summary>
    private bool CalibrationTooHot(FanRuntime fan)
    {
        var cfg = fan.Cfg;
        bool hasOwn = false;
        foreach (var c in fan.Curves)
        {
            hasOwn = true;
            if (ReadTemp(c.Config.SensorId) >= CalibrationAbortTemp) return true;
        }
        if (cfg.Mode == FanModes.Target && cfg.TargetSensorId.Length > 0)
        {
            hasOwn = true;
            if (ReadTemp(cfg.TargetSensorId) >= CalibrationAbortTemp) return true;
        }
        if (cfg.IsGpuFan)
        {
            hasOwn = true;
            _hub!.GpuTemps(cfg.ControlId, out float? core, out _);
            if (FanSafety.IsPlausibleTemp(core) && core >= CalibrationAbortTemp) return true;
        }
        if (!hasOwn)
        {
            foreach (var s in _emergencySensors)
            {
                if (ReadPhysicalTemp(s.Id) >= CalibrationAbortTemp) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// キャリブレーションの間に見張る温度が読めなくなったか（CalibrationTooHot と同じセンサーを見る）。
    /// 自分のセンサーがあればそのどれか、なければ緊急全開の判定に使うセンサーがすべて読めないとき
    /// </summary>
    private bool CalibrationTempLost(FanRuntime fan)
    {
        var cfg = fan.Cfg;
        bool hasOwn = false;
        foreach (var c in fan.Curves)
        {
            hasOwn = true;
            if (ReadTemp(c.Config.SensorId) is null) return true;
        }
        if (cfg.Mode == FanModes.Target && cfg.TargetSensorId.Length > 0)
        {
            hasOwn = true;
            if (ReadTemp(cfg.TargetSensorId) is null) return true;
        }
        if (cfg.IsGpuFan)
        {
            hasOwn = true;
            _hub!.GpuTemps(cfg.ControlId, out float? core, out _);
            if (!FanSafety.IsPlausibleTemp(core)) return true;
        }
        if (!hasOwn)
            return _calibrationWatch.Count == 0 || _calibrationWatch.Any(id => ReadPhysicalTemp(id) is null);
        return false;
    }

    /// <summary>
    /// キャリブレーションを始めてよいか: 見張る温度がいま読めるか。
    /// 自分の温度（カーブ・目標温度・GPU のファンなら GPU のコア）があれば、それがすべて読めること（返すのは空の一覧）。
    /// なければ、緊急全開で見る温度のうち、いま読めるものの一覧（1 つもなければ null = 始めない）
    /// </summary>
    private List<string>? CalibrationWatch(FanRuntime fan)
    {
        var cfg = fan.Cfg;
        bool hasOwn = fan.Curves.Count > 0 || (cfg.Mode == FanModes.Target && cfg.TargetSensorId.Length > 0) || cfg.IsGpuFan;
        if (hasOwn) return CalibrationTempLost(fan) ? null : new List<string>();
        var readable = _emergencySensors.Select(s => s.Id).Where(id => ReadPhysicalTemp(id) is not null).ToList();
        return readable.Count > 0 ? readable : null;
    }

    /// <summary>キャリブレーション中なら中止して結果を知らせる（スリープなど）</summary>
    private void AbortCalibration(string reason)
    {
        if (_calibration is not { } cal) return;
        cal.Fail(reason);
        FinishCalibration(cal, FindFan(cal.ControlId));
    }

    private void FinishCalibration(Calibrator cal, FanRuntime? fan)
    {
        _calibration = null;
        var result = cal.BuildResult();
        string message = result is null
            ? L($"キャリブレーションを中止しました: {cal.FailureReason}")
            : L($"キャリブレーション完了: 最大 {result.MaxRpm:N0} rpm / 停止 {result.StopDuty:F0}% 未満 / 始動 {result.StartDuty:F0}%");
        Logger.Info($"{cal.FanName}: {message}");
        CalibrationFinished?.Invoke(cal.ControlId, result, message);
        if (fan is not null) fan.Duty = null; // 通常制御は現在値から滑らかに再開する
    }

    /// <summary>
    /// 1 つのファンの処理。失敗しても、ほかのファン（特にポンプ）の制御は続ける。
    /// 失敗したファンは、このツールが制御していたなら全開にする（全開も書けなければ BIOS に戻す）
    /// </summary>
    private FanStatus SafeTickFan(FanRuntime f, long nowMs, double? leaderDuty)
    {
        try
        {
            var status = TickFan(f, nowMs, leaderDuty);
            if (f.Failures > 0)
            {
                Logger.Info($"'{f.Cfg.Name}' の処理が回復しました（{f.Failures} 回失敗のあと）");
                f.Failures = 0;
            }
            return status;
        }
        catch (Exception ex)
        {
            f.Failures++;
            if (f.Failures == 1 || f.Failures % 60 == 0) Logger.Error($"'{f.Cfg.Name}' の処理で例外（{f.Failures} 回連続）: {ex}");
            if (f.Failures == FanFailureAlertCount)
                RaiseAlert(L($"'{f.Cfg.Name}' の制御でエラーが続いています。安全のため全開（できなければ BIOS 制御）にしています。ログを確認してください。"), f.Cfg.IsPump ? AlertLevel.Danger : AlertLevel.Warning);
            return FanSafeAction(f);
        }
    }

    /// <summary>処理に失敗したファンの安全な動作: 制御中なら全開、全開も書けなければ BIOS に戻す。BIOS 制御中のファンには触らない</summary>
    private FanStatus FanSafeAction(FanRuntime f)
    {
        var cfg = f.Cfg;
        var c = f.Control;
        double? duty = null;
        if (c is not null && (f.LastWritten is not null || _touched.ContainsKey(c)))
        {
            try
            {
                c.SetSoftware(100);
                _touched[c] = 100;
                f.LastWritten = 100;
                f.LastSent = 100;
                f.Duty = 100;
                duty = 100;
            }
            catch (Exception ex)
            {
                Logger.Error($"'{cfg.Name}' を全開にできません。BIOS 制御に戻します: {ex.Message}");
                Restore(c);
                ResetOutputState(f);
            }
        }
        // 表示: 全開にできたら「センサー異常 → 全開」、書いていない（BIOS が制御している）なら「BIOS 制御」
        var state = c is null ? FanState.NotFound : duty is null ? FanState.Bios : FanState.SensorError;
        f.LastState = state;
        return new FanStatus(cfg.Name, cfg.ControlId, cfg.Role, state, duty, 100, null, Array.Empty<CurveStatus>());
    }

    private FanStatus TickFan(FanRuntime f, long nowMs, double? leaderDuty)
    {
        var cfg = f.Cfg;
        var hub = _hub!;
        double? rpm = hub.Value(cfg.RpmSensorId);
        double? hwDuty = hub.Value(cfg.ControlId);

        // ポンプの回転数の監視は、端子が見つからない・制御していないときも続ける
        if (cfg.IsPump) UpdatePumpWatchdog(f, rpm, nowMs);

        if (f.Control is null)
        {
            f.LastState = FanState.NotFound;
            return new FanStatus(cfg.Name, cfg.ControlId, cfg.Role, FanState.NotFound, null, 0, rpm, Array.Empty<CurveStatus>());
        }

        // --- 0. 前の周期に書いた値が、実機に反映されているか（読み戻し）。
        //        LHM は他のソフトがチップを使用中だと書き込みを黙って飛ばし、同じ値の書き込みは省略するので、確かめて書き直す。
        //        GPU のファンはドライバーが値を変える（最低回転数など）ので対象外
        bool reassert = false;
        bool readbackMismatch = false;
        if (f.LastWritten is double lastW && hwDuty is double readback && !cfg.IsGpuFan)
        {
            var action = FanSafety.CheckReadback(ref f.Readback, lastW, readback);
            reassert = action is ReadbackAction.Reassert or ReadbackAction.ReassertAndAlert;
            readbackMismatch = f.Readback.MismatchTicks >= FanSafety.ReadbackMismatchTicks;
            if (action == ReadbackAction.ReassertAndAlert)
                RaiseAlert(L($"'{cfg.Name}' に書き込んだ回転数が反映されていません（指示 {lastW:F0}% / 実際 {readback:F0}%）。ほかのファン制御ソフトが動いていないか確認してください。"),
                    cfg.IsPump ? AlertLevel.Danger : AlertLevel.Warning);
            else if (action == ReadbackAction.Recovered)
                Logger.Info($"'{cfg.Name}' の書き込みが反映されるようになりました");
        }
        else if (f.LastWritten is null)
        {
            f.Readback = default;
        }

        // --- 1. 要求 Duty の計算（BIOS 制御中や DryRun 中も、画面表示のために計算する）
        var curveStatuses = new List<CurveStatus>(f.Curves.Count + 1);
        bool sensorError = false;
        bool sensorMissing = false;
        double missingDuty = _cfg.Safety.MissingSensorDuty;
        double demand = 0;
        foreach (var ev in f.Curves)
        {
            float? temp = ReadTemp(ev.Config.SensorId);
            if (temp is null)
            {
                // センサーごと無くなった（GPU を交換したなど）ときは、全開ではなく安全側の固定値にする。
                // 値が読めない・固まった・おかしい（センサーはある）ときは、何が起きているか分からないので全開
                if (IsMissing(ev.Config.SensorId))
                {
                    // この起動のあいだに読めていたセンサーが消えた（ドライバーの不調・スリープ明けに見つからない など）ときは、
                    // 温度が分からないあいだ、最後に必要だった回転より下げない（熱いときに消えても、回転が落ちていかないように）
                    sensorMissing = true;
                    double hold = Math.Max(missingDuty, ev.LastRequest ?? missingDuty);
                    curveStatuses.Add(new CurveStatus(ev.Config.Name, null, null, hold, false, Missing: true));
                    demand = Math.Max(demand, hold);
                }
                else
                {
                    sensorError = true;
                    curveStatuses.Add(new CurveStatus(ev.Config.Name, null, null, 100, true));
                }
                continue;
            }
            double req = ev.Evaluate(temp.Value, _tickDt);
            curveStatuses.Add(new CurveStatus(ev.Config.Name, temp.Value, ev.EffectiveTemp, req, false));
            demand = Math.Max(demand, req);
        }

        var state = FanState.Active;
        switch (cfg.Mode)
        {
            case FanModes.Fixed:
                demand = cfg.FixedDuty;
                break;

            case FanModes.Target:
            {
                double? output = TargetControl(f, hwDuty);
                float? t = ReadTemp(cfg.TargetSensorId);
                bool missing = output is null && IsMissing(cfg.TargetSensorId);
                // センサーが途中で消えたら、温度が読めていたときの最後の PI の出力より下げない（はじめから無ければ MissingSensorDuty）
                double hold = Math.Max(missingDuty, f.Pi.LastOutput ?? missingDuty);
                curveStatuses.Insert(0, new CurveStatus(L($"目標 {cfg.TargetTemp:F0}°C"), t, f.Pi.Ema, output ?? (missing ? hold : 100), output is null && !missing, missing));
                if (missing) { demand = hold; state = FanState.SensorMissing; }
                else if (output is null) { demand = 100; state = FanState.SensorError; }
                else { demand = output.Value; f.Pi.LastOutput = output.Value; }
                break;
            }

            case FanModes.Follow:
                if (leaderDuty is null)
                {
                    demand = 100; // 連動先が見つからない・連動が循環している
                    state = FanState.SensorError;
                }
                else
                {
                    // 自分のカーブ（例: ケースファンの GPU 温度）が連動先より高く回したいときは、自分のカーブを優先する
                    // （連動はうなりを消すためのもの。連動先が見ていない温度で熱がこもらないように）
                    double follow = FollowDuty(cfg, leaderDuty.Value);
                    curveStatuses.Insert(0, new CurveStatus(L("連動"), null, null, follow, false));
                    demand = Math.Max(follow, demand);
                }
                break;

            default:
                if (sensorError || f.Curves.Count == 0)
                {
                    demand = 100; // 値の異常はフェイルセーフで全開
                    state = FanState.SensorError;
                }
                else if (sensorMissing)
                {
                    state = FanState.SensorMissing;
                }
                break;
        }

        double floor = Math.Max(cfg.MinDuty, cfg.IsPump ? PumpHardMinDuty : 0);
        demand = Math.Max(demand, floor);
        // 共振回避帯の中に要求が入ったら、近いほうの端へ寄せる（最低回転数より下には寄せない。100% の操作は影響しない）
        if (state is FanState.Active or FanState.SensorMissing) demand = AvoidBands(f, demand, floor);

        bool overriding = _overrides.TryGetValue(cfg.ControlId, out var ov) && nowMs < ov.UntilMs;
        if (!overriding) _overrides.Remove(cfg.ControlId);
        if (overriding)
        {
            demand = cfg.IsPump ? Math.Max(ov.Duty, PumpHardMinDuty) : ov.Duty;
            state = FanState.Manual;
        }

        // GPU のファン: GPU が熱すぎるときは、手動テスト中でも全開にする（GPU 自身の保護が効かなくなっているため）
        if (cfg.IsGpuFan && UpdateGpuGuard(f)) { demand = 100; state = FanState.GpuGuard; overriding = false; }
        bool boosting = _boostUntil is not null && !cfg.IsPump && !overriding;
        if (boosting) { demand = 100; state = FanState.Boost; }
        if (_emergency) { demand = 100; state = FanState.Emergency; }
        if (f.PumpFailsafe) { demand = 100; state = FanState.PumpAlert; }

        bool identifying = _identify.TryGetValue(cfg.ControlId, out var until) && nowMs < until;
        if (!identifying) _identify.Remove(cfg.ControlId);
        // GPU 保護・ポンプ保護はキャリブレーションより優先する（キャリブレーションは次の周期の StepCalibration で中止される）
        bool calibrating = _calibration is not null && Same(_calibration.ControlId, cfg.ControlId) && !f.GpuGuard && !f.PumpFailsafe;

        // センサー異常が何周期続いているか（1 周期だけの読み取りミスで全開に跳ねないよう、2 周期続いたら一気に上げる）
        f.ErrorTicks = state == FanState.SensorError ? f.ErrorTicks + 1 : 0;

        // --- 2. 書き込むかどうか（ポンプ保護・識別テスト・キャリブレーションは、BIOS 制御中や一時停止中でも優先する）
        // ブーストはユーザーの明示的な操作なので、BIOS 制御中のファンも全開にする
        bool write = identifying || calibrating || f.PumpFailsafe || boosting || overriding || (cfg.Enabled && !_paused && !_cfg.DryRun);

        double next;
        if (calibrating)
        {
            next = _calibration!.CurrentDuty;
            state = FanState.Calibrating;
        }
        else if (identifying)
        {
            next = 100;
            state = FanState.Identify;
        }
        else if (write)
        {
            // 上げは速く、下げはゆっくり（急な回転変化による「うなり」を防ぐ）。
            // ただし緊急全開・GPU 保護・ポンプ保護・続いているセンサー異常は、最初の周期から一気に全開にする
            double cur = f.Duty ?? hwDuty ?? demand;
            bool direct = FanSafety.IsDirect(state, cfg.Mode, f.ErrorTicks);
            // 上げ・下げの速さは「1 秒あたり」（画面の表示どおり）。実際に経った時間ぶんだけ動かすので、更新間隔を変えても速さは同じ
            next = FanSafety.Slew(cur, demand, cfg.MaxStepUpPerTick * _tickDt, cfg.MaxStepDownPerTick * _tickDt, direct);
            if (state != FanState.Manual)
            {
                // ゆっくり変化している途中でも、共振回避帯の中には留まらず一気に通り過ぎる
                next = PassThroughBands(cfg, cur, next, floor);
                // 下限（ポンプの最低 Duty を含む）は、ゆっくり上げている途中でも守る
                next = Math.Max(next, floor);
                // 止まっているファンは、回り始める回転まで少しの間だけ上げる（測ってあるファンだけ）
                if (_cfg.FindCalibration(cfg.ControlId) is { } cal)
                    next = FanSafety.SpinUp(ref f.SpinUp, next, rpm, cal.StartDuty, cal.StopDuty, nowMs);
            }
            if (f.PumpFailsafe) next = 100;
        }
        else
        {
            // 書き込まない間は実機の値を追いかけておき、制御再開時に今の回転から滑らかにつなぐ
            next = hwDuty ?? demand;
            if (state is FanState.Active or FanState.SensorError or FanState.Emergency or FanState.SensorMissing or FanState.GpuGuard)
                state = _paused ? FanState.Paused : !cfg.Enabled ? FanState.Bios : FanState.DryRun;
        }
        f.Duty = next;

        if (write)
        {
            // 状態が変わったとき・書き直しが必要なとき・初めて書くときは、変化が小さくても必ず書く
            bool force = reassert || state != f.LastState || f.LastWritten is null;
            WriteDuty(f, next, floor, force, reassert);
        }
        else if (_touched.ContainsKey(f.Control))
        {
            Restore(f.Control);
            ResetOutputState(f);
        }
        else if (f.LastWritten is not null)
        {
            ResetOutputState(f);
        }
        f.LastState = state;

        // --- 3. ファン停止の検知（30%以上を指示しているのに 0rpm が 5 回続いたら警告。キャリブレーション中・起動直後は除く）
        if (!cfg.IsPump && rpm.HasValue && !calibrating)
        {
            // 回っているはずの指示: 測ってあるファンは「回り始める回転 + 5%」以上、測っていないファンは 30% 以上
            // （測っていないファンは、低い回転で止まる仕様（0 rpm モード）のこともあるので、これまでどおり 30%）
            double spinExpected = _cfg.FindCalibration(cfg.ControlId) is { StartDuty: > 0 } stallCal
                ? Math.Min(30, Math.Max(stallCal.StartDuty, stallCal.StopDuty) + 5) : 30;
            bool stalled = write && next >= spinExpected && rpm.Value < 1 && nowMs >= _graceUntilMs;
            f.StallTicks = stalled ? f.StallTicks + 1 : 0;
            if (f.StallTicks >= 5)
            {
                state = FanState.Stall;
                if (!f.StallAlerted)
                {
                    f.StallAlerted = true;
                    RaiseAlert(L($"'{cfg.Name}' が回転していません。配線やファンを確認してください。"), AlertLevel.Warning);
                }
            }
            else if (rpm.Value > 100)
            {
                f.StallAlerted = false;
            }
        }

        // 表示: 書き込んでいるときは実機に書いた値。書いた値が反映されていないときは実際の値を見せる
        double? displayDuty = !write ? hwDuty
            : readbackMismatch && hwDuty is double actual ? actual
            : f.LastWritten ?? next;
        return new FanStatus(cfg.Name, cfg.ControlId, cfg.Role, state, displayDuty, demand, rpm, curveStatuses);
    }

    /// <summary>
    /// 実機に Duty を書き込む。1% 未満の変化は書かない（NCT668x は 1 回の書き込みでチップを約 50ms 占有するため）。
    /// 書き直し（reassert）のときは、LHM が同じ値の書き込みを省略しないよう 0.001% だけずらす
    /// </summary>
    private void WriteDuty(FanRuntime f, double duty, double floor, bool force, bool reassert)
    {
        var c = f.Control!;
        if (!double.IsFinite(duty)) duty = 100;   // 計算のどこかで NaN・無限大になっても、低い値を書かない（安全側の全開）
        float value = (float)Math.Clamp(duty, 0, 100);
        if (f.LastWritten is double w && !FanSafety.NeedsWrite(w, value, floor, force)) return;
        float send = value;
        if (reassert && f.LastSent is float last && last == send) send = FanSafety.Nudged(value);
        c.SetSoftware(send);
        _touched[c] = send;
        _pendingRestore.Remove(c);
        f.LastSent = send;
        f.LastWritten = value;
    }

    /// <summary>書き込みの状態を忘れる（BIOS に戻した・ハードウェアを開き直した）</summary>
    private static void ResetOutputState(FanRuntime f)
    {
        f.LastWritten = null;
        f.LastSent = null;
        f.Readback = default;
    }

    /// <summary>
    /// 手動テスト・識別テストの命令を、次の周期を待たずにすぐ反映する（出力だけ。センサーは読まない）。
    /// 緊急全開・ポンプ保護・GPU 保護・キャリブレーション中のファンは、次の周期の通常の判定に任せる
    /// </summary>
    private void RefreshOutputs(long nowMs)
    {
        if (_pendingOutput.Count == 0) return;
        if (_suspended || _hub is null || _emergency)
        {
            _pendingOutput.Clear();
            return;
        }
        foreach (var id in _pendingOutput)
        {
            try { RefreshOutput(id, nowMs); }
            catch (Exception ex) { Logger.Warn($"手動テストの反映に失敗: {id}: {ex.Message}"); }
        }
        _pendingOutput.Clear();
    }

    private void RefreshOutput(string id, long nowMs)
    {
        bool identifying = _identify.TryGetValue(id, out long identifyUntil) && nowMs < identifyUntil;
        var f = FindFan(id);
        if (f is null)
        {
            // 設定に登録していない端子の識別テスト
            if (identifying && _hub!.FindControl(id) is { } orphan)
            {
                orphan.SetSoftware(100);
                _touched[orphan] = 100;
            }
            return;
        }
        if (f.Control is null || f.PumpFailsafe || f.GpuGuard) return;
        if (_calibration is not null && Same(_calibration.ControlId, id)) return;

        double duty;
        FanState state;
        if (identifying)
        {
            duty = 100;
            state = FanState.Identify;
        }
        else if (_overrides.TryGetValue(id, out var ov) && nowMs < ov.UntilMs)
        {
            duty = f.Cfg.IsPump ? Math.Max(ov.Duty, PumpHardMinDuty) : ov.Duty;
            state = FanState.Manual;
        }
        else
        {
            return;
        }
        WriteDuty(f, duty, 0, force: true, reassert: false);
        f.Duty = duty;
        f.LastState = state;
    }

    /// <summary>要求 Duty が共振回避帯の中なら、近いほうの端に寄せる（前回と同じ側を優先して、真ん中で揺れないようにする）</summary>
    private static double AvoidBands(FanRuntime f, double demand, double floor)
    {
        var band = f.Cfg.AvoidBands.FirstOrDefault(b => b.Contains(demand));
        if (band is null)
        {
            f.BandSide = 0;
            return demand;
        }
        // 下の端が最低回転数より下なら、上の端にする（止まる回転数に入らないように）
        if (band.From < floor)
        {
            f.BandSide = 1;
            return band.To;
        }
        double mid = (band.From + band.To) / 2;
        // 真ん中から帯の幅の 1 割以内は、前回の側を保つ
        double margin = (band.To - band.From) * 0.1;
        int side = demand > mid + margin ? 1 : demand < mid - margin ? -1 : f.BandSide != 0 ? f.BandSide : (demand >= mid ? 1 : -1);
        f.BandSide = side;
        return side > 0 ? band.To : band.From;
    }

    /// <summary>今の Duty から次の Duty へ動くとき、共振回避帯の中で止まらないよう帯の反対側の端まで進める</summary>
    public static double PassThroughBands(FanConfig cfg, double current, double next, double floor = 0)
    {
        foreach (var b in cfg.AvoidBands)
        {
            if (!b.Contains(next)) continue;
            if (next > current) return b.To;
            if (next < current) return b.From >= floor ? b.From : b.To;
        }
        return next;
    }

    /// <summary>
    /// GPU のファン: その GPU の温度が保護温度を超えていたら true（解除は 5°C 下がってから）。
    /// 127/128°C などの「読めない」を表す値は使わず、切り替えは 2 回続けて同じ判定になったときだけ（1 回の異常な読み取りで切り替えない）
    /// </summary>
    private bool UpdateGpuGuard(FanRuntime f)
    {
        _hub!.GpuTemps(f.Cfg.ControlId, out float? core, out float? hotSpot);
        if (!FanSafety.IsPlausibleTemp(core)) core = null;
        if (!FanSafety.IsPlausibleTemp(hotSpot)) hotSpot = null;
        // 温度が読めないときは今の状態を保つ（保護中なら全開のまま）
        if (core is null && hotSpot is null) return f.GpuGuard;
        bool hotNow = FanSafety.GpuTooHot(core, hotSpot, f.GpuGuard);
        bool guard = FanSafety.Debounce(f.GpuGuard, hotNow, ref f.GpuGuardCount, FanSafety.GpuGuardConfirmReadings);
        if (guard && !f.GpuGuard)
            RaiseAlert(L($"GPU の温度が高いため、'{f.Cfg.Name}' を 100% にしています（GPU ファンの保護）。"), AlertLevel.Warning);
        f.GpuGuard = guard;
        return guard;
    }

    /// <summary>目標温度モード: センサーが読めなければ null（呼び出し側で全開にする）</summary>
    private double? TargetControl(FanRuntime f, double? hwDuty)
    {
        var cfg = f.Cfg;
        float? t = ReadTemp(cfg.TargetSensorId);
        if (t is null) return null;
        double min = Math.Max(cfg.MinDuty, cfg.IsPump ? PumpHardMinDuty : 0);
        var pi = new PiController(cfg.TargetTemp, cfg.TargetAggressiveness, min);
        return pi.Step(t.Value, _tickDt, f.Duty ?? hwDuty ?? min, f.Pi);
    }

    /// <summary>連動モード: リーダーと同じ回転数（両方キャリブレーション済み）か、同じ Duty ＋ オフセット</summary>
    private double FollowDuty(FanConfig cfg, double leaderDuty)
    {
        if (cfg.FollowMatchRpm
            && _cfg.FindCalibration(cfg.FollowControlId) is { } leaderCal
            && _cfg.FindCalibration(cfg.ControlId) is { } myCal
            && leaderCal.EstimateRpm(leaderDuty) is double rpm
            && myCal.EstimateDuty(rpm) is double duty)
        {
            return Math.Clamp(duty + cfg.FollowOffset, 0, 100);
        }
        return Math.Clamp(leaderDuty + cfg.FollowOffset, 0, 100);
    }

    /// <summary>テストモード（DRY RUN）中に保護のために書き込んだとき、知らせに添える一文</summary>
    private string DryRunNote() => _cfg.DryRun ? L("（テストモード中ですが、ポンプの保護のため 100% を書き込みました）") : "";

    /// <summary>
    /// ポンプの監視（制御しているかどうか・端子が見つかるかどうかに関係なく、毎周期）:
    ///   ・回転数が警告値を 3 回連続で下回ったら、100% に固定して警告する。警告値を 10 回連続で上回ったら解除する。
    ///   ・回転数が 5 秒以上読めないときも、回っているか分からないので 100% にして警告する（起動・復帰の直後は待つ）。
    /// </summary>
    private void UpdatePumpWatchdog(FanRuntime f, double? rpm, long nowMs)
    {
        var cfg = f.Cfg;
        if (string.IsNullOrEmpty(cfg.RpmSensorId)) return; // 回転数センサーを設定していないポンプは監視できない

        if (rpm is null)
        {
            if (nowMs < _graceUntilMs) return; // センサーの準備中かもしれない
            if (f.PumpRpmNullSinceMs < 0) f.PumpRpmNullSinceMs = nowMs;
            f.PumpOkTicks = 0;
            if (!f.PumpRpmLost && nowMs - f.PumpRpmNullSinceMs >= FanSafety.PumpRpmLostMs)
            {
                f.PumpRpmLost = true;
                f.PumpFailsafe = true;
                RaiseAlert(L($"ポンプ '{cfg.Name}' の回転数が読めません。回っているか確認できないため、100% で保護運転にしています。すぐに確認してください。{DryRunNote()}"), AlertLevel.Danger);
            }
            return;
        }
        f.PumpRpmNullSinceMs = -1;
        if (f.PumpRpmLost)
        {
            f.PumpRpmLost = false;
            Logger.Info($"ポンプ '{cfg.Name}' の回転数が読めるようになりました ({rpm:F0} rpm)");
        }

        if (rpm < cfg.PumpAlertRpm)
        {
            f.PumpOkTicks = 0;
            if (++f.PumpLowTicks >= 3 && !f.PumpFailsafe)
            {
                f.PumpFailsafe = true;
                RaiseAlert(L($"ポンプ '{cfg.Name}' の回転数が {rpm:F0} rpm に低下しています。100% で保護運転中です。すぐに確認してください。{DryRunNote()}"), AlertLevel.Danger);
            }
        }
        else
        {
            f.PumpLowTicks = 0;
            if (f.PumpFailsafe && ++f.PumpOkTicks >= 10)
            {
                f.PumpFailsafe = false;
                f.PumpOkTicks = 0;
                RaiseAlert(L($"ポンプ '{cfg.Name}' の回転数が回復しました ({rpm:F0} rpm)。"), AlertLevel.Info);
            }
        }
    }

    /// <summary>設定に登録していない端子の識別テスト（ファン追加前の確認用）</summary>
    private void TickOrphanIdentify(long nowMs)
    {
        if (_identify.Count == 0) return;
        foreach (var (id, until) in _identify.ToList())
        {
            if (FindFan(id) is not null) continue;
            var control = _hub!.FindControl(id);
            if (control is null || nowMs >= until)
            {
                _identify.Remove(id);
                if (control is not null && _touched.ContainsKey(control)) Restore(control);
                continue;
            }
            control.SetSoftware(100);
            _touched[control] = 100;
        }
    }

    /// <summary>
    /// BIOS 制御に戻す。例外が出なくても戻ったとは限らない（LHM は他のソフトがチップを使用中だと、戻す命令を黙って飛ばす）ので、
    /// 「今の値を書く → BIOS に戻す」の切り替えを毎回必ず 3 回くり返す（LHM は「すでに BIOS 制御」と思っていると戻す命令を送らないため、
    /// いったん値を書いてから戻す。同じ値の書き込みも省略されるので、1 回おきに値を少しずらす）。
    /// さらに、あとの数周期でもう一度戻し直す（_pendingRestore）。最後の「戻す」が例外なく通れば true
    /// </summary>
    /// <summary>BIOS の制御に戻せなかったと知らせた端子（同じ端子では 1 回だけ知らせる）</summary>
    private readonly HashSet<string> _restoreWarned = new(StringComparer.OrdinalIgnoreCase);

    private bool Restore(IFanControl c)
    {
        // 最後に書いた値（分からなければ全開。戻す途中で失敗しても、低い回転のまま残らないように）
        float last = _touched.TryGetValue(c, out float v) ? v : 100f;
        bool ok = false;
        int failures = 0;
        for (int attempt = 1; attempt <= FanSafety.RestoreAttempts; attempt++)
        {
            if (attempt > 1 && !_testMode) Thread.Sleep(FanSafety.RestoreGapMs); // チップを使っている他のソフトが離すのを少し待つ
            ok = ToggleRestore(c, last, attempt);
            if (!ok) failures++;
        }
        if (ok)
        {
            Logger.Info($"BIOS 制御に復帰: {c.Id}{(failures > 0 ? $"（{FanSafety.RestoreAttempts} 回中 {failures} 回失敗）" : "")}");
            _restoreWarned.Remove(c.Id);
        }
        else
        {
            Logger.Error($"BIOS 制御に戻せませんでした: {c.Id}");
            // 動いているあいだ（終了・スリープの途中ではないとき）は、画面で知らせる（端子ごとに 1 回。戻せたらまた知らせられるようにする）
            if (Volatile.Read(ref _stopFlag) == 0 && !_suspended && _restoreWarned.Add(c.Id))
            {
                string name = _fans.FirstOrDefault(f => ReferenceEquals(f.Control, c))?.Cfg.Name ?? c.Id;
                RaiseAlert(L($"「{name}」を BIOS の制御に戻せませんでした。いまの回転数のままかもしれません。ほかのファン制御ソフトが動いていないか確かめてください。"), AlertLevel.Warning);
            }
        }
        _touched.Remove(c);
        // 戻す命令が黙って飛ばされていても、次の周期以降にもう一度戻す
        _pendingRestore[c] = (last, FanSafety.RestoreRepeatTicks);
        return ok;
    }

    /// <summary>
    /// 「値を書く → BIOS に戻す」を 1 回だけ行う。値を書くのに失敗しても戻す命令は必ず送る。
    /// 偶数回目は値を少しずらす（LHM が同じ値の書き込みを省略し、モードの切り替えだけで終わらないように）。戻す命令が例外なく通れば true
    /// </summary>
    private static bool ToggleRestore(IFanControl c, float last, int attempt)
    {
        try { c.SetSoftware(attempt % 2 == 0 ? FanSafety.Nudged(last) : last); }
        catch (Exception ex) { Logger.Warn($"BIOS 復帰前の書き込みに失敗（{attempt} 回目）: {c.Id}: {ex.Message}"); }
        try
        {
            c.SetDefault();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"BIOS 復帰に失敗（{attempt} 回目）: {c.Id}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 前の周期までに BIOS に戻した端子を、もう一度戻し直す（周期の最初に呼ぶ）。
    /// その後このツールが書き込んだ端子（_touched にある）は、もう BIOS 制御ではないので対象から外す
    /// </summary>
    private void RepeatPendingRestores()
    {
        if (_pendingRestore.Count == 0) return;
        foreach (var (c, (last, ticks)) in _pendingRestore.ToList())
        {
            if (_touched.ContainsKey(c))
            {
                _pendingRestore.Remove(c);
                continue;
            }
            if (!ToggleRestore(c, last, FanSafety.RestoreRepeatTicks - ticks + 1)) Logger.Warn($"BIOS 制御への戻し直しに失敗: {c.Id}");
            if (ticks <= 1) _pendingRestore.Remove(c);
            else _pendingRestore[c] = (last, ticks - 1);
        }
    }

    /// <summary>触ったすべての端子を BIOS 制御に戻す。全部戻せたら true</summary>
    private bool RestoreAll()
    {
        bool ok = true;
        foreach (var c in _touched.Keys.ToList())
        {
            if (!Restore(c)) ok = false;
        }
        foreach (var f in _fans) ResetOutputState(f);
        return ok;
    }

    private void SetHealth(EngineHealth health, string reason)
    {
        if (_health == (int)health) return;
        _health = (int)health;
        if (health is not (EngineHealth.Ok or EngineHealth.Starting)) Logger.Info($"エンジンの状態: {health}（{reason}）");
        HealthChanged?.Invoke(health, reason);
    }

    private void RaiseAlert(string message, AlertLevel level)
    {
        switch (level)
        {
            case AlertLevel.Danger: Logger.Error(message); break;
            case AlertLevel.Warning: Logger.Warn(message); break;
            default: Logger.Info(message); break;
        }
        Alert?.Invoke(message, level);
    }
}

/// <summary>読み戻しの確認結果</summary>
public enum ReadbackAction
{
    /// <summary>問題なし（または、まだ様子を見る）</summary>
    Ok,
    /// <summary>書き直す</summary>
    Reassert,
    /// <summary>書き直して、ユーザーに知らせる（何度書き直しても反映されない）</summary>
    ReassertAndAlert,
    /// <summary>知らせたあとで、反映されるようになった</summary>
    Recovered,
}

/// <summary>回り始めの後押しの状態（ファンごとに 1 つ）</summary>
public struct SpinUpState
{
    /// <summary>この時刻（ミリ秒）まで、回り始める回転で回す（0 なら後押ししていない）</summary>
    public long UntilMs;
    /// <summary>後押ししても回らなかったとき、この時刻までは次の後押しをしない</summary>
    public long RetryAfterMs;
}

/// <summary>書き込んだ値の読み戻しの確認状態（ファンごとに 1 つ）</summary>
public struct ReadbackCheck
{
    /// <summary>続けて食い違った周期の数</summary>
    public int MismatchTicks;
    /// <summary>書き直しても反映されなかった回数</summary>
    public int Failures;
    /// <summary>ユーザーに知らせ済み</summary>
    public bool Alerted;
    /// <summary>前の周期の読み戻し値（チップ側で少しずつ変えている途中かどうかの判断用）</summary>
    public double LastReadback;
    public bool HasLast;
}

/// <summary>
/// ファン制御の安全に関わる判断（ハードウェアを使わない純粋な計算。テストしやすいよう static にしている）
/// </summary>
public static class FanSafety
{
    /// <summary>緊急全開は、発動してから最低この時間（ミリ秒）続ける（境目で全開と通常を繰り返さない）</summary>
    public const long EmergencyMinHoldMs = 30_000;
    /// <summary>緊急温度からこの温度（°C）以内では、センサーを「固着」と判定しない</summary>
    public const double StuckGuardMargin = 15;
    /// <summary>これ（%）未満の変化は書き込まない</summary>
    public const double WriteDeadband = 1.0;
    /// <summary>書き直すときに値をずらす量（%）。PWM の分解能（約 0.39%）よりずっと小さいので回転は変わらない</summary>
    public const float ReassertNudge = 0.001f;

    /// <summary>
    /// 書き直す値を ReassertNudge だけずらす。100% のときは下へずらす（100% を超える値を書かない。
    /// LHM は丸めるので回転は変わらないが、範囲の外の値を受け付けないチップ・ドライバーがあってもよいように）
    /// </summary>
    public static float Nudged(float value) => value + ReassertNudge <= 100f ? value + ReassertNudge : value - ReassertNudge;
    /// <summary>読み戻した値と書いた値の差がこれ（%）以下なら一致とみなす（PWM は 0〜255 の 256 段階なので丸めの差がある）</summary>
    public const double ReadbackTolerance = 2.0;
    /// <summary>この周期の数だけ続けて食い違ったら書き直す（1 回だけなら、書き込みと読み取りのタイミングのずれかもしれない）</summary>
    public const int ReadbackMismatchTicks = 2;
    /// <summary>書き直してもこの回数反映されなければ知らせる</summary>
    public const int ReadbackAlertFailures = 5;
    /// <summary>
    /// 読み戻した値が、前の周期よりこれ（%）以上書いた値に近づいていれば、まだ動いている途中とみなす
    /// （MSI のマザーボードなど、チップが回転をゆっくり変えるものは 1 周期に 0.5% ほどしか近づかない）
    /// </summary>
    public const double ReadbackApproachStep = 0.25;
    /// <summary>センサー異常がこの周期の数続いたら、ゆっくりではなく一気に全開にする</summary>
    public const int SensorErrorDirectTicks = 2;
    /// <summary>ポンプの回転数がこの時間（ミリ秒）読めなければ危険として知らせる</summary>
    public const long PumpRpmLostMs = 5000;
    /// <summary>GPU 保護は、この回数続けて同じ判定になったら切り替える</summary>
    public const int GpuGuardConfirmReadings = 2;
    /// <summary>BIOS 制御に戻すとき、「値を書く → 戻す」の切り替えをくり返す回数（例外が出なくても毎回この回数行う）</summary>
    public const int RestoreAttempts = 3;
    /// <summary>BIOS 制御に戻す切り替えの間隔（ミリ秒。チップを使っている他のソフトが離すのを待つ）</summary>
    public const int RestoreGapMs = 60;
    /// <summary>BIOS 制御に戻したあと、この周期の数だけ戻し直す（戻す命令が黙って飛ばされていたときのため）</summary>
    public const int RestoreRepeatTicks = 2;

    /// <summary>温度として信用できる値か。0 以下・150 以上と、「センサーがつながっていない」を表す 127/128°C は使わない（−128 は 0 以下で除外）</summary>
    public static bool IsPlausibleTemp(float? t)
        => t is float v && v > 0 && v < 150 && v != 127f && v != 128f;

    /// <summary>この状態のとき、ゆっくり変化させずに要求値をそのまま書くか</summary>
    /// <param name="errorTicks">センサー異常が続いている周期の数</param>
    public static bool IsDirect(FanState state, string mode, int errorTicks) => state switch
    {
        // 安全のための全開は、最初の周期から一気に（以前は 1 周期 8% ずつで、全開まで 10 秒以上かかっていた）
        FanState.Emergency or FanState.GpuGuard or FanState.PumpAlert => true,
        FanState.SensorError => errorTicks >= SensorErrorDirectTicks,
        // 手動テストは指定した値をすぐに。連動はリーダー側でなめらかにしてあるので、そのまま
        FanState.Manual => true,
        FanState.Active => mode == FanModes.Follow,
        _ => false,
    };

    /// <summary>回り始めの後押しで、回り始める回転にのせる余裕（%）</summary>
    public const double SpinUpMargin = 3;
    /// <summary>回り始めの後押しを続ける長さの上限（ミリ秒）。回り出したら、すぐに指示どおりに戻す</summary>
    public const long SpinUpMaxMs = 3000;
    /// <summary>後押ししても回らなかったとき、次に試すまでの間（ミリ秒。壊れたファンで上げ下げを繰り返さないように）</summary>
    public const long SpinUpRetryMs = 30000;

    /// <summary>
    /// 回り始めの後押し: ファンは「止まる回転（stopDuty）」より「回り始める回転（startDuty）」のほうが高い。
    /// 止まっている（0 rpm）ファンに、その間（stopDuty 以上・startDuty 未満）の指示を出すと、回り出さずに止まったままになるので、
    /// 回り出すまで（長くても 3 秒）startDuty + 3% で回す。回り出せば、指示が stopDuty 以上なら回り続ける。
    /// stopDuty 未満の指示（止めたい）・回転数が読めない・測っていないファンは何もしない
    /// </summary>
    public static double SpinUp(ref SpinUpState s, double next, double? rpm, double startDuty, double stopDuty, long nowMs)
    {
        double kick = Math.Min(100, startDuty + SpinUpMargin);
        if (rpm is not double r || startDuty <= 0 || next >= kick)
        {
            s.UntilMs = 0;
            return next;
        }
        bool stopped = r < 1;
        if (s.UntilMs > 0)
        {
            if (stopped && nowMs < s.UntilMs) return kick;
            if (stopped) s.RetryAfterMs = nowMs + SpinUpRetryMs;   // 後押ししても回らなかった（配線・故障。止まっているファンの警告に任せる）
            s.UntilMs = 0;
            return next;
        }
        if (stopped && next >= stopDuty && next > 0 && nowMs >= s.RetryAfterMs)
        {
            s.UntilMs = nowMs + SpinUpMaxMs;
            return kick;
        }
        return next;
    }

    /// <summary>今の Duty から要求 Duty へ 1 周期分だけ近づける（上げは maxUp、下げは maxDown まで。direct なら要求値そのもの）</summary>
    public static double Slew(double current, double demand, double maxUp, double maxDown, bool direct)
    {
        if (direct) return demand;
        double diff = demand - current;
        return diff >= 0 ? current + Math.Min(diff, maxUp) : current - Math.Min(-diff, maxDown);
    }

    /// <summary>
    /// 書き込みが必要か: 前回書いた値から 1% 以上変わった・強制（状態の変化・書き直し）・下限や 100% にぴったり合わせるとき
    /// </summary>
    public static bool NeedsWrite(double lastWritten, double next, double floor, bool force)
    {
        if (force) return true;
        double diff = Math.Abs(next - lastWritten);
        if (diff >= WriteDeadband) return true;
        if (diff < 1e-6) return false;
        // 少し手前で止まったままにしない
        return next >= 100 || next <= floor;
    }

    /// <summary>
    /// 緊急全開を続けるかどうか。
    /// 発動: どれかがしきい値以上。解除: 全部が「しきい値 − 5°C」未満で、きっかけのセンサーが読めていて、30 秒以上たったとき
    /// </summary>
    /// <param name="heldMs">発動してからの時間（ミリ秒）</param>
    public static bool NextEmergency(bool active, bool anyAtLimit, bool anyAboveRelease, bool triggerUnreadable, long heldMs)
    {
        if (!active) return anyAtLimit;
        if (anyAboveRelease || triggerUnreadable) return true;
        return heldMs < EmergencyMinHoldMs;
    }

    /// <summary>ハードウェアを開き直すまでの待ち時間（ミリ秒）。1 回目 5 秒、2 回目 15 秒、それ以降 60 秒</summary>
    public static int ReopenDelayMs(int attempt) => attempt switch
    {
        <= 0 => 5_000,
        1 => 15_000,
        _ => 60_000,
    };

    /// <summary>
    /// 次の周期の時刻。一定の間隔で刻み、処理が遅れて時刻を過ぎていたら、遅れを取り戻そうと連続で回さずに今から 1 周期後にする
    /// </summary>
    public static long NextTickMs(long previousTick, long now, int intervalMs)
    {
        long next = previousTick + intervalMs;
        return next <= now ? now + intervalMs : next;
    }

    /// <summary>
    /// 前の周期に書いた値（written）と、今の読み戻し値（readback）を比べる。
    /// 2% を超える差が 2 周期続いたら書き直し、5 回書き直しても直らなければ知らせる。
    /// 読み戻し値が書いた値へ近づいている途中（チップ側で少しずつ変えている）なら、まだ待つ
    /// </summary>
    public static ReadbackAction CheckReadback(ref ReadbackCheck s, double written, double readback)
    {
        double error = Math.Abs(readback - written);
        bool hadLast = s.HasLast;
        double lastError = Math.Abs(s.LastReadback - written);
        s.LastReadback = readback;
        s.HasLast = true;

        if (error <= ReadbackTolerance)
        {
            bool recovered = s.Alerted;
            s.MismatchTicks = 0;
            s.Failures = 0;
            s.Alerted = false;
            return recovered ? ReadbackAction.Recovered : ReadbackAction.Ok;
        }
        if (hadLast && error < lastError - ReadbackApproachStep) return ReadbackAction.Ok;
        if (++s.MismatchTicks < ReadbackMismatchTicks) return ReadbackAction.Ok;
        s.Failures++;
        if (s.Failures >= ReadbackAlertFailures && !s.Alerted)
        {
            s.Alerted = true;
            return ReadbackAction.ReassertAndAlert;
        }
        return ReadbackAction.Reassert;
    }

    /// <summary>GPU 保護の判定（保護中は 5°C 下がるまで続ける）</summary>
    public static bool GpuTooHot(float? core, float? hotSpot, bool active)
    {
        double margin = active ? 5 : 0;
        return (hotSpot is float h && h >= FanEngine.GpuGuardHotspotTemp - margin)
               || (core is float c && c >= FanEngine.GpuGuardCoreTemp - margin);
    }

    /// <summary>今と違う判定が needed 回続いたら切り替える（1 回だけの異常な値で切り替えない）</summary>
    public static bool Debounce(bool current, bool observed, ref int count, int needed)
    {
        if (observed == current)
        {
            count = 0;
            return current;
        }
        if (++count >= needed)
        {
            count = 0;
            return observed;
        }
        return current;
    }
}

/// <summary>PI 制御の途中状態（ファンごとに 1 つ）</summary>
public sealed class PiState
{
    public double? Ema { get; set; }
    public double Integral { get; set; }
    public bool Ready { get; set; }
    /// <summary>温度が読めていたときに最後に出した値（%）。センサーが途中で消えたとき、これより下げない</summary>
    public double? LastOutput { get; set; }
}

/// <summary>
/// 目標温度モードの PI 制御。
///   出力 = 最低回転数 + Kp × (平滑化した温度 − 目標) + 積分項
/// 温度が目標より高い間は積分項が少しずつ増えて回転が上がり続け、低ければ下がっていく。
/// CPU 温度は 1 秒ごとに数°C 跳ねるので、時定数 約 10 秒で平滑化してから使う（そのままだと回転がふらつく）。
/// </summary>
public readonly struct PiController
{
    private const double EmaAlpha = 0.1;
    private readonly double _target;
    private readonly double _kp;
    private readonly double _ki;
    private readonly double _min;

    /// <param name="aggressiveness">1（ゆったり）〜10（きびきび）。5 が標準</param>
    public PiController(double target, double aggressiveness, double minDuty)
    {
        double k = Math.Clamp(aggressiveness, 1, 10) / 5.0; // 0.2〜2 倍
        _target = target;
        _kp = 2.5 * k;    // 1°C 超えるごとに +2.5%
        _ki = 0.04 * k;   // 1°C 超えた状態が 1 秒続くごとに +0.04%
        _min = minDuty;
    }

    /// <param name="rawTemp">センサーの生の温度</param>
    /// <param name="dt">前回からの経過時間（秒）</param>
    /// <param name="currentDuty">今の回転（初回だけ使う。切り替えた瞬間に回転が跳ねないように）</param>
    public double Step(double rawTemp, double dt, double currentDuty, PiState s)
    {
        // ならし具合は 1 秒あたり（更新間隔を変えても時定数 約 10 秒のまま）
        double alpha = CurveMath.AlphaFor(EmaAlpha, dt);
        s.Ema = s.Ema is null ? rawTemp : alpha * rawTemp + (1 - alpha) * s.Ema.Value;
        double error = s.Ema.Value - _target;
        if (!s.Ready)
        {
            s.Integral = currentDuty - _min - _kp * error;
            s.Ready = true;
        }

        // 目標 ±1°C 以内は積分を止めて、回転が細かく揺れないようにする
        double e = Math.Abs(error) < 1 ? 0 : error;
        double output = _min + _kp * error + s.Integral;
        // 出力が上限・下限に張り付いている間は、それ以上積分しない（ワインドアップ防止）
        if ((output < 100 || e < 0) && (output > _min || e > 0)) s.Integral += _ki * e * dt;
        s.Integral = Math.Clamp(s.Integral, -60, 100);
        return Math.Clamp(_min + _kp * error + s.Integral, _min, 100);
    }
}

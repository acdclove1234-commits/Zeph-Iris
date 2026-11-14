// ==================================================
// Watchdog.cs - 見張りプロセス (本体が強制終了・フリーズしたら、ファンを安全な回転数にして本体を再起動する)
// Dependencies: LibreHardwareMonitorLib (MPL-2.0), .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32.SafeHandles;

namespace ProFanController;

/// <summary>
/// 本体と同じ exe を「--watchdog 本体のプロセスID」で起動すると、この見張り役になる（画面は出さない）。
///
/// なぜ必要か:
///   ファンの回転数はマザーボードのチップに書き込んだ値がそのまま残る。本体が正しく終了すれば BIOS の自動制御に
///   戻すが、強制終了・クラッシュでは後片付けができず、「最後に書いた低い回転数」のまま固定されてしまう。
///   その状態でゲームなどを始めると冷えなくなるので、見張り役が代わりに安全な回転数を書き込む。
///
/// 動き:
///   ・本体が正しく終了した（終了の合図あり）→ 何もせず終了
///   ・本体が合図なしで消えた（強制終了・クラッシュ）→ 安全な回転数にして、設定に従い本体を再起動
///   ・本体の制御ループが 30 秒止まった（フリーズ）→ 安全な回転数にする。90 秒続いたら本体を終了させて再起動
/// </summary>
public static class Watchdog
{
    /// <summary>本体が正しく終了するときにセットする（見張りも終了する）</summary>
    public const string StopEventName = @"Global\Zeph-Iris.WatchdogStop";
    /// <summary>本体が正しく終了するが、BIOS 制御に戻せたか確認できなかったとき（見張りは安全な回転数にして終わる。再起動はしない）</summary>
    public const string StopUnsafeEventName = @"Global\Zeph-Iris.WatchdogStopUnsafe";
    public const string Argument = "--watchdog";
    public const string RecoveredArgument = "--recovered";

    private static readonly TimeSpan HangSafeAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HangRestartAfter = TimeSpan.FromSeconds(90);
    /// <summary>この時間内にこれ以上落ちたら、再起動を繰り返さない</summary>
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(10);
    private const int MaxCrashesInWindow = 3;

    private static string CrashHistoryPath => Path.Combine(Logger.Directory, "crash_history.txt");

    private static EventWaitHandle? _stop;

    /// <summary>見張りからの案内を画面に出す方法（App が設定する。Core は画面の部品を直接使わない）</summary>
    public static Action<string>? ShowMessage { get; set; }

    /// <summary>見張りプロセスが、終わってよいと伝えていないのに終了した（別スレッドから呼ばれる。引数は終了コード）</summary>
    public static event Action<int>? UnexpectedExit;

    private static volatile bool _stopping;

    // ----- 本体側 -----

    /// <summary>本体から呼ぶ: 見張りプロセスを起動する。起動できたら null、できなければ理由</summary>
    public static string? Launch()
    {
        try
        {
            _stop ??= new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
            // 直前に止めた見張りが合図に気づく前に取り消さないよう、少し待つ
            if (_stop.WaitOne(0)) Thread.Sleep(1000);
            _stop.Reset();
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException(L("実行ファイルのパスが分かりません。"));
            _stopping = false;
            var p = Process.Start(new ProcessStartInfo(exe, $"{Argument} {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            if (p is not null)
            {
                // 見張りが勝手に終わってしまったら（誤って終了させられた等）、本体に知らせて起動し直してもらう
                p.EnableRaisingEvents = true;
                p.Exited += (_, _) =>
                {
                    int code = -1;
                    try { code = p.ExitCode; } catch { /* 取れなくてもよい */ }
                    p.Dispose();
                    if (!_stopping) UnexpectedExit?.Invoke(code);
                };
            }
            Logger.Info("見張りプロセスを起動しました");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"見張りプロセスを起動できません: {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>本体から呼ぶ: 正しく終了するが BIOS 制御に戻せたか分からないので、安全な回転数にしてから終わってほしい</summary>
    public static void SignalStopUnsafe()
    {
        try
        {
            _stopping = true;
            using var e = new EventWaitHandle(false, EventResetMode.ManualReset, StopUnsafeEventName);
            e.Set();
            Thread.Sleep(200); // 見張りが合図に気づく前に、本体の終了を「異常終了」と見なされないように少し待つ
        }
        catch (Exception ex)
        {
            Logger.Warn($"見張りプロセスへの合図に失敗: {ex.Message}");
        }
    }

    /// <summary>本体から呼ぶ: 正しく終了するので、見張りも終わってよい</summary>
    public static void SignalStop()
    {
        try
        {
            _stopping = true;
            _stop ??= new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
            _stop.Set();
        }
        catch (Exception ex)
        {
            Logger.Warn($"見張りプロセスへの終了の合図に失敗: {ex.Message}");
        }
    }

    // ----- 見張り側 -----

    /// <summary>見張りの本体。本体が終わるまで戻らない</summary>
    public static void Run(int parentPid)
    {
        Process parent;
        try { parent = Process.GetProcessById(parentPid); }
        catch { return; } // すでに終わっている

        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        using var stopUnsafe = new EventWaitHandle(false, EventResetMode.ManualReset, StopUnsafeEventName);
        stopUnsafe.Reset();
        using var heartbeat = new EventWaitHandle(false, EventResetMode.AutoReset, FanEngine.HeartbeatName);
        using var parentHandle = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(parent.Handle, ownsHandle: false) };
        Logger.Info($"見張り開始（本体 PID {parentPid}）");
        Preload();

        bool seenBeat = false;
        var lastBeat = Stopwatch.StartNew();
        int silentWaits = 0;
        bool safeApplied = false;
        var handles = new WaitHandle[] { stop, parentHandle, heartbeat, stopUnsafe };

        while (true)
        {
            int i = WaitHandle.WaitAny(handles, TimeSpan.FromSeconds(5));
            if (i == 0) { Logger.Info("見張り終了（本体が正しく終了）"); return; }
            if (i == 3)
            {
                Logger.Warn("見張り: 本体は終了しましたが BIOS 制御に戻せたか確認できないため、ファンを安全な回転数にして終了します");
                parent.WaitForExit(10_000);
                ApplySafeDuties();
                return;
            }
            if (i == 1)
            {
                // 終了の合図と本体の終了がほぼ同時のこともあるので、もう一度確かめる
                if (stop.WaitOne(500)) { Logger.Info("見張り終了（本体が正しく終了）"); return; }
                if (stopUnsafe.WaitOne(0)) { ApplySafeDuties(); return; }
                OnParentDied(parent);
                return;
            }
            if (i == 2)
            {
                seenBeat = true;
                silentWaits = 0;
                lastBeat.Restart();
                if (safeApplied)
                {
                    safeApplied = false;
                    Logger.Info("見張り: 本体の制御ループが再開しました");
                }
                continue;
            }

            // タイムアウト。スリープから復帰した直後は時間が飛ぶので、「起きている間に」何回続けて来なかったかで判断する
            if (!seenBeat) continue; // ハードウェアの初期化に失敗している本体は見張らない
            silentWaits++;
            var silent = TimeSpan.FromSeconds(silentWaits * 5);
            // 30 秒止まったら安全な回転数にする。止まり続けているあいだは 30 秒ごとに書き直す
            // （チップを本体が使っている最中で、書き込みが届かなかったときのため。再起動がオフでも、いつかは届くように）
            if (silent >= HangSafeAfter && silentWaits % (int)(HangSafeAfter.TotalSeconds / 5) == 0)
            {
                if (!safeApplied) Logger.Error($"見張り: 本体の制御ループが {silent.TotalSeconds:F0} 秒止まっています。ファンを安全な回転数にします");
                else Logger.Warn($"見張り: 本体の制御ループが {silent.TotalSeconds:F0} 秒止まったままです。安全な回転数を書き直します");
                safeApplied = true;
                ApplySafeDuties();
            }
            if (silent >= HangRestartAfter && LoadConfig().Safety.WatchdogRestart)
            {
                Logger.Error("見張り: 本体が応答しないため、終了させて再起動します");
                try { parent.Kill(); parent.WaitForExit(5000); } catch (Exception ex) { Logger.Warn($"本体を終了できません: {ex.Message}"); }
                // 止まっていた本体が、終了させる直前に別の値を書いていたかもしれないので、もう一度安全な回転数にする
                OnParentDied(parent, alreadySafe: false);
                return;
            }
        }
    }

    private static void OnParentDied(Process parent, bool alreadySafe = false)
    {
        int code = -1;
        try { code = parent.ExitCode; } catch { /* 取得できないこともある */ }
        Logger.Error($"見張り: 本体が予期せず終了しました（終了コード {code}）。ファンを安全な回転数にします");
        Recover(alreadySafe ? null : ApplySafeDuties, LoadConfig, RecordCrash, RestartApp, ShowStopped);
    }

    /// <summary>
    /// 本体が落ちたあとの手順: 安全な回転数にする → 設定を読む → 落ちた回数を記録 → 本体を起動し直す（または案内を出す）。
    /// どの手順が失敗しても次へ進み、例外は外へ出さない。とくに本体の起動し直しは必ず試す
    /// （本体が動き出せば、ファンはすぐにアプリの制御に戻る。見張りまで落ちると、ファンは最後の値のまま誰も触らなくなる）。
    /// 起動し直したら true
    /// </summary>
    internal static bool Recover(Action? applySafe, Func<AppConfig> loadConfig, Func<int> recordCrash,
                                 Action restart, Action<AppConfig, int> showStopped, Action<TimeSpan>? wait = null)
    {
        if (applySafe is not null)
        {
            try { applySafe(); }
            catch (Exception ex) { Logger.Error($"見張り: 安全な回転数を設定できません: {ex.Message}"); }
        }

        AppConfig cfg;
        try { cfg = loadConfig(); }
        catch (Exception ex)
        {
            Logger.Error($"見張り: 設定を読めません。初期の設定で続けます: {ex.Message}");
            cfg = new AppConfig();
        }

        int recent = 1;
        try { recent = recordCrash(); }
        catch (Exception ex) { Logger.Warn($"見張り: 終了の回数を記録できません: {ex.Message}"); }

        if (cfg.Safety.WatchdogRestart && recent <= MaxCrashesInWindow)
        {
            // 続けて落ちたら、起動し直すまで少し待つ（2 回目は 30 秒・3 回目は 60 秒）。
            // グラボのドライバーの入れ替えなどで落ちたとき、すぐ起動し直すとまた同じところで落ちることがある。
            // 待つあいだも、ファンは安全な回転数（ポンプは 100%）のまま
            var delay = RestartDelay(recent);
            if (delay > TimeSpan.Zero)
            {
                Logger.Warn($"見張り: {CrashWindow.TotalMinutes:F0} 分間に {recent} 回終了したので、{delay.TotalSeconds:F0} 秒待ってから起動し直します");
                try { (wait ?? Thread.Sleep)(delay); }
                catch (Exception ex) { Logger.Warn($"見張り: 待てませんでした: {ex.Message}"); }
            }
            try
            {
                restart();
                Logger.Info("見張り: 本体を再起動しました");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"見張り: 本体を再起動できません: {ex.Message}");
                return false;
            }
        }

        try { showStopped(cfg, recent); }
        catch (Exception ex) { Logger.Error($"見張り: 案内を出せません: {ex.Message}"); }
        return false;
    }

    /// <summary>続けて落ちたときに、起動し直すまで待つ時間（1 回目はすぐ、2 回目 30 秒、3 回目 60 秒）</summary>
    internal static TimeSpan RestartDelay(int recent) => recent >= 2 ? TimeSpan.FromSeconds(30 * (recent - 1)) : TimeSpan.Zero;

    private static void RestartApp()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Environment.ProcessPath is null"); // no-loc
        Process.Start(new ProcessStartInfo(exe, $"--minimized {RecoveredArgument}") { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory })?.Dispose();
    }

    private static void ShowStopped(AppConfig cfg, int recent)
    {
        // 見張りが起動したあとに表示言語を選び直していることがあるので、案内を出す前に設定の言語を読み直す
        Localization.Init(cfg.Language);
        string why = cfg.Safety.WatchdogRestart
            ? L($"{CrashWindow.TotalMinutes:F0} 分間に {recent} 回終了したため、自動での再起動はやめました。")
            : L("自動での再起動はオフになっています。");
        ShowMessage?.Invoke(
            L($"Zeph-Iris が予期せず終了しました。\n\nファンは安全な回転数（{cfg.Safety.WatchdogSafeDuty:F0}%、ポンプは 100%）に固定しています。{why}\n") +
            L("PC を再起動すると BIOS の制御に戻ります（Zeph-Iris を起動し直すと、アプリの制御で動きます）。\n\nログ: ") + Logger.Directory +
            RollbackHint(cfg, DateTime.Now));
    }

    /// <summary>更新したばかりの版で落ち続けたときの、前の版に戻す方法（更新したばかりでなければ空）</summary>
    internal static string RollbackHint(AppConfig cfg, DateTime now, Version? current = null)
    {
        try
        {
            if (!AppUpdater.CanRollBack(cfg.Update, current ?? AppUpdater.CurrentVersion, now)) return "";
            string previous = cfg.Update.PreviousVersion ?? "";
            // 更新の前に取っておいた前の版があれば、それをダブルクリックするだけで戻せる（ネットにつながなくてよい）
            string kept = UpdateRecovery.RecoveryPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            if (UpdateRecovery.BackupVersion(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.ToString(3) == previous)
                return "\n\n" + L($"この版には最近更新したばかりです。新しい版で落ち続けるときは、次のファイルをダブルクリックすると、前の版（{previous}）と更新する前の設定に戻せます（いまの設定は backups フォルダーに取っておきます）。\n{kept}");
            return "\n\n" + L($"この版には最近更新したばかりです。新しい版で落ち続けるときは、リリースのページ（{AppUpdater.ReleasesPage}）から {previous} のインストーラー（Zeph-Iris-{previous}-setup.exe）を実行すると、前の版に戻せます。");
        }
        catch
        {
            return ""; // 案内が作れなくても、止まったことは必ず伝える
        }
    }

    /// <summary>見張りの始めに読めた設定（落ちたときに設定を読めなくても、どの端子をどうするか分かるように）</summary>
    private static AppConfig? _lastGoodConfig;

    /// <summary>
    /// 落ちたときに使う部品を、見張りの始めに読み込んでおく。1 つの exe にまとめたアプリは、部品を初めて使うときに
    /// exe から取り出すので、動いている途中で exe が差し替わると（更新の途中など）、後片付けで部品を読めずに失敗する
    /// </summary>
    private static void Preload()
    {
        try { LoadConfig(); }
        catch (Exception ex) { Logger.Warn($"見張り: 始めに設定を読めません: {ex.Message}"); }
        try { _ = typeof(Computer).Assembly.GetName(); }
        catch (Exception ex) { Logger.Warn($"見張り: ハードウェアの部品を読み込めません: {ex.Message}"); }
    }

    /// <summary>直近の異常終了の回数（今回を含む）</summary>
    private static int RecordCrash()
    {
        try
        {
            Directory.CreateDirectory(Logger.Directory);
            var now = DateTime.Now;
            var times = File.Exists(CrashHistoryPath)
                ? File.ReadAllLines(CrashHistoryPath).Select(l => DateTime.TryParse(l, out var t) ? t : DateTime.MinValue).Where(t => now - t < CrashWindow).ToList()
                : new List<DateTime>();
            times.Add(now);
            File.WriteAllLines(CrashHistoryPath, times.Select(t => t.ToString("o")));
            return times.Count;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// 設定を読むだけ（見張りからは設定ファイルを書き換えない。ConfigStore.Load は壊れていたときに復旧して書き戻すため使わない）
    /// </summary>
    private static AppConfig LoadConfig()
    {
        // 更新の見守りから使うときは、まずアプリのフォルダーの設定（強制終了した版が使っていたもの）を読む
        if (_configPath is { } path)
        {
            var own = ReadConfig(() => File.ReadAllText(path), null, out bool ok);
            if (ok)
            {
                _lastGoodConfig = own;
                return own;
            }
        }
        var cfg = ReadConfig(() => File.ReadAllText(ConfigStore.FilePath), _lastGoodConfig, out bool fresh);
        if (fresh) _lastGoodConfig = cfg;
        return cfg;
    }

    /// <summary>
    /// 設定を読む。読めない・壊れているときは、前に読めた設定（なければ初期の設定）を返す。例外は外へ出さない。
    /// fresh = いま読めた設定
    /// </summary>
    internal static AppConfig ReadConfig(Func<string> read, AppConfig? lastGood, out bool fresh)
    {
        fresh = false;
        try
        {
            var cfg = AppConfig.FromJson(read());
            fresh = true;
            return cfg;
        }
        catch (Exception ex)
        {
            Logger.Warn($"見張り: 設定を読めません（{(lastGood is null ? "初期の設定" : "前に読めた設定")}を使います）: {ex.Message}");
        }
        if (lastGood is not null) return lastGood;
        var def = new AppConfig();
        try { def.Normalize(); }
        catch (Exception ex) { Logger.Warn($"見張り: 初期の設定を整えられません: {ex.Message}"); }
        return def;
    }

    /// <summary>
    /// 安全な回転数を書くための Computer（何度も作ると、そのたびにドライバーの資源を使うので使い回す）。
    /// マザーボードの端子用と、グラボのファン用を分ける（グラボのドライバーが不安定なときに見張りまで落ちても、
    /// マザーボードのファンは先に書き終わっているように）
    /// </summary>
    private static Computer? _boardComputer, _gpuComputer;

    /// <summary>設定を読む場所（null なら ConfigStore.FilePath。更新の見守りから使うときは、アプリのフォルダーの設定）</summary>
    private static string? _configPath;

    /// <summary>
    /// 更新の見守り（previous\ZephIrisRecovery.exe）から: 固まった新しい版を強制終了したあと、安全な回転数を書く。
    /// 強制終了した版が使っていた設定（アプリのフォルダーの config.json）の端子に書く。読めなければ previous の設定
    /// </summary>
    internal static void ApplySafeDutiesNow(string appDir)
    {
        _configPath = Path.Combine(appDir, "config.json");
        ApplySafeDuties();
    }

    /// <summary>
    /// 本体が制御していた端子に、安全な回転数を書き込む。マザーボードの端子を先に、グラボのファンをあとに書く。
    /// わざと Computer を閉じない（閉じると LHM が「開いたときの値」＝落ちた本体が最後に書いた値に戻してしまうため）
    /// </summary>
    private static void ApplySafeDuties()
    {
        try
        {
            var cfg = LoadConfig();
            // 端子ごとに 1 つにまとめる。どれかのプロファイルでポンプにしていればポンプとして扱う（100% にする）。
            // GPU ファンの制御を許可していなければ、GPU ファンには触らない
            // 「BIOS に任せる」端子も含める（手動テスト・おまかせ調整・特定の点滅などで本体が書いていることがあるため。
            // 安全な回転数は高めなので、書いても危なくない）
            var fans = cfg.Profiles.SelectMany(p => p.Fans).Where(f => !string.IsNullOrEmpty(f.ControlId))
                .Where(f => !f.IsGpuFan || cfg.Safety.AllowGpuFanControl)
                .GroupBy(f => f.ControlId, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Fan: g.First(), IsPump: g.Any(x => x.IsPump)))
                .ToList();
            if (fans.Count == 0) return;

            // 1. マザーボードの端子（グラボのドライバーを呼ばない）
            var board = fans.Where(f => !f.Fan.IsGpuFan).ToList();
            if (board.Count > 0)
            {
                _boardComputer ??= Open(new Computer { IsMotherboardEnabled = true });
                Write(board, Controls(_boardComputer, updateGpu: false), cfg);
            }
            // 2. グラボのファン（ドライバーを呼ぶ。読み取りの更新はせず、書き込みだけにして呼ぶ回数を減らす）
            var gpu = fans.Where(f => f.Fan.IsGpuFan).ToList();
            if (gpu.Count > 0)
            {
                try
                {
                    _gpuComputer ??= Open(new Computer { IsGpuEnabled = true });
                    Write(gpu, Controls(_gpuComputer, updateGpu: false), cfg);
                }
                catch (Exception ex)
                {
                    Logger.Error($"見張り: グラボのファンに安全な回転数を書けません（グラボは自分の保護で守られます）: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"見張り: 安全な回転数を設定できません: {ex}");
        }
    }

    private static Computer Open(Computer c)
    {
        c.Open();
        return c;
    }

    /// <summary>端子の ID → 書き込み口。グラボは読み取りの更新をしない（updateGpu = false。書き込み口は開いたときからある）</summary>
    private static Dictionary<string, IControl> Controls(Computer computer, bool updateGpu)
    {
        var controls = new Dictionary<string, IControl>(StringComparer.OrdinalIgnoreCase);
        void Collect(IHardware hw)
        {
            bool isGpu = hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
            if (!isGpu || updateGpu) hw.Update();
            foreach (var s in hw.Sensors.Where(s => s.Control is not null)) controls[s.Identifier.ToString()] = s.Control;
            foreach (var sub in hw.SubHardware) Collect(sub);
        }
        foreach (var hw in computer.Hardware) Collect(hw);
        return controls;
    }

    private static void Write(IEnumerable<(FanConfig Fan, bool IsPump)> fans, Dictionary<string, IControl> controls, AppConfig cfg)
    {
        foreach (var (f, isPump) in fans)
        {
            if (!controls.TryGetValue(f.ControlId, out var c)) continue;
            float duty = isPump ? 100f : (float)cfg.Safety.WatchdogSafeDuty;
            try
            {
                // LHM は「前と同じ値」の書き込みを省くので、少しずらした値を書いてから本来の値を書く
                // （本体がその間に別の値を書いていても、必ずチップまで届くように）
                c.SetSoftware(duty >= 100f ? duty - 0.001f : duty + 0.001f);
                c.SetSoftware(duty);
                Logger.Info($"見張り: {f.Name} を {duty:F0}% にしました");
            }
            catch (Exception ex)
            {
                Logger.Error($"見張り: {f.Name} に書き込めません: {ex.Message}");
            }
        }
    }
}

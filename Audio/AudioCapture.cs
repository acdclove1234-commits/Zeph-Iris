// ==================================================
// AudioCapture.cs - 音楽連動のための音の取り込み (スピーカー／ヘッドホンに出ている音を WASAPI ループバックで読み取る。
//                   音が届いた合図ですぐ読み取り、出力先の切り替え・抜き差し・スリープ復帰に自動で追従する。
//                   「特定のアプリだけ」は、そのアプリの音だけを直接取り込む（Windows 11 など）。
//                   できないときは、アプリごとの音量メーターで判定する)
// Dependencies: NAudio.Wasapi (MIT License), .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace ProFanController;

/// <summary>
/// 専用のスレッドで音を取り込み、AudioEngine に渡し続ける。マイクは使わない。
///   ・音が届いた合図（イベント）ですぐ読み取る（10 ミリ秒ごと）。合図が来ない環境では、一定間隔で読みに行く方式に自動で切り替える
///   ・届いた音の 1 つ 1 つに、SampleClock で正確な時刻を付ける
///   ・何も再生していないとき Windows は音を送ってこないので、その間は無音（0）を自分で足して時間を進める
///     （テンポの記録や、光がゆっくり消えていく動きが途切れないように）
///   ・出力先の切り替え・抜き差しは Windows からの知らせで、止まってしまったときは自動で取り込み直す
/// 「特定のアプリだけ」のときは、そのアプリ（とその子プロセス）の音だけを直接取り込む（ProcessLoopbackCapture）。
///   ・アプリが起動していない・この Windows が対応していない・取り込みに失敗したときは、出力先の音をすべて取り込み、
///     全体の音量のうちそのアプリの音量の割合を反応の強さに掛ける（以前からの方式。理由は 1 回だけ記録に残す）
///   ・アプリが起動した・起動し直したときは、3 秒以内に気づいて、そのアプリの音だけの取り込みに切り替える
/// </summary>
public sealed class AudioCapture : IDisposable
{
    /// <summary>音が届かない状態がこれだけ続いたら、無音で時間を進める（秒）</summary>
    private const double SilenceAfter = 0.1;
    /// <summary>無音で埋めるのは「今」からこれだけ前まで（秒。本物の音が遅れて届いたときに重ならないように）</summary>
    private const double SilenceLead = 0.01;
    /// <summary>音が鳴っているのにこれだけ何も届かなければ、取り込みが止まったとみなす（秒）</summary>
    private const double StallAfter = 1.5;
    /// <summary>アプリの音の割合をならす時定数（秒）</summary>
    private const double AppGateTau = 0.08;
    /// <summary>アプリの音のセッション・プロセスを調べ直す間隔（秒）</summary>
    private const double AppScanEvery = 3;
    /// <summary>アプリの音だけの取り込み口ができるまで待つ時間</summary>
    private static readonly TimeSpan ActivateTimeout = TimeSpan.FromSeconds(3);

    private readonly object _feedGate = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _restartRequested;
    /// <summary>いま取り込む音（空ならすべて）。同期テスト中は設定にかかわらず空</summary>
    private volatile string _source = "";
    /// <summary>設定で選んだ反応する音（同期テストが終わったら _source をこれに戻す）</summary>
    private string _configuredSource = "";
    private AudioEngineSettings _settings = AudioEngineSettings.Default;
    private bool _clickDetect;
    private float[] _mono = new float[4096];
    private readonly float[] _zeros = new float[AudioEngine.Hop];
    private SampleClock _clock = new(48000);
    private double _lastArrival = double.NegativeInfinity;
    private double _nextSampleT = double.NegativeInfinity;
    private string _lastLog = "";
    private int _failures;
    /// <summary>出力先の音量メーターの最後の値（0〜1。無音で埋めてよいかの判断に使う）</summary>
    private double _lastPeak;
    /// <summary>合図での読み取りが止まった出力先（次からは一定間隔で読む）</summary>
    private readonly HashSet<string> _pollingEndpoints = new();
    /// <summary>「特定のアプリだけ」で、直接取り込むか・以前からの方式に戻すかの判断（取り込みスレッドだけが使う）</summary>
    private readonly AppCapturePlanner _planner = new();
    /// <summary>直前に記録した、取り込み方の理由（同じ理由を何度も記録しない）</summary>
    private string _planLogged = "";
    /// <summary>出力先の取り込み中に準備できた、アプリの音だけの取り込み（次の回で使う）</summary>
    private OpenedApp? _pending;
    /// <summary>アプリの音だけの取り込みで、最後に 0 でない音が届いた時刻（一時停止中も 0 の音は届き続けるので、届いた時刻とは別に見る）</summary>
    private double _lastSound = double.NegativeInfinity;

    /// <summary>分析エンジン（出力先のサンプリング周波数が変わると作り直す。使うたびにこのプロパティから読む）</summary>
    public AudioEngine Engine { get; private set; } = new() { StatsLogging = true };
    /// <summary>状態（設定画面の表示用）</summary>
    public string Status { get; private set; } = L("停止中");
    /// <summary>いま音を取り込んでいる出力先の名前（スピーカー・ヘッドホンなど）</summary>
    public string EndpointName { get; private set; } = "";
    /// <summary>聞いている出力先の種類と、見つかった音のソフト（音の遅れの補正の案内に使う）</summary>
    public OutputInfo Output { get; private set; } = OutputInfo.None;
    /// <summary>いま音を取り込んでいる出力先の ID</summary>
    public string EndpointId { get; private set; } = "";
    /// <summary>音が届いた合図で読み取っている（false なら一定間隔で読みに行く方式）</summary>
    public bool EventDriven { get; private set; }
    /// <summary>選んだアプリの音だけを直接取り込んでいる（false なら出力先の音をすべて取り込んでいる）</summary>
    public bool AppOnly { get; private set; }
    public bool Running => _thread is not null;

    /// <summary>同期テストのクリック音を記録させる（エンジンを作り直しても引き継ぐ）</summary>
    /// <remarks>
    /// オンの間は、反応する音の設定にかかわらず、このアプリ自身の音（クリック音）だけを取り込む。
    /// 選んだアプリ（Spotify など）の音だけではクリック音が届かず、出力先の音をすべて取り込むと、
    /// 流れている曲のせいでクリックの前の静けさが来ないため、どちらでも LED が光らない。
    /// アプリの音だけを取り込めない Windows・失敗したときは、出力先の音をすべて取り込む（曲を止めれば光る）
    /// </remarks>
    public bool ClickDetect
    {
        get => _clickDetect;
        set
        {
            _clickDetect = value;
            Engine.ClickDetect = value;
            _selfFailed = false;
            _source = value ? SelfSource : _configuredSource;
        }
    }

    /// <summary>同期テスト中の「反応する音」: このアプリ自身の音（アプリの名前と重ならない印）</summary>
    private const string SelfSource = "\u0001sync-test";
    /// <summary>同期テストで、このアプリの音だけを取り込めなかった（テストが終わるまで出力先の音をすべて取り込む）</summary>
    private volatile bool _selfFailed;

    /// <summary>記録に書く、反応する音の名前</summary>
    private static string SourceName(string source) => source == SelfSource ? "同期テストのクリック音（このアプリ）" : source; // no-loc（ログだけ）

    /// <summary>同期テスト中で、このアプリの音だけを取り込める（取り込むべき）か</summary>
    private bool WantsSelf(string source) => source == SelfSource && !_selfFailed && ProcessLoopbackCapture.IsSupported;

    /// <summary>反応する音（空ならすべて、アプリ名ならそのアプリだけ）と分析の設定を変える。すぐに反映する</summary>
    public void Configure(string source, AudioEngineSettings settings)
    {
        // "Spotify.exe" のように拡張子付きで入力されても探せるように
        string s = AppAudioTarget.NormalizeName(source);
        var engine = Engine;
        bool tempoChanged = engine.Settings.TempoFactor != settings.TempoFactor || engine.Settings.TempoMode != settings.TempoMode;
        _configuredSource = s;
        if (!_clickDetect) _source = s; // 同期テスト中は、終わるまでこのアプリの音のまま
        _settings = settings;
        engine.Settings = settings;
        if (tempoChanged) engine.ResetTempo();
    }

    public void Start()
    {
        if (_thread is not null) return;
        _stop = false;
        _restartRequested = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioCapture", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        var thread = _thread;
        if (thread is null) return;
        _stop = true;
        _wake.Set();
        thread.Join(3000);
        _thread = null;
        Status = L("停止中");
        EndpointName = "";
        EndpointId = "";
        Output = OutputInfo.None;
        AppOnly = false;
    }

    /// <summary>取り込みをやり直す（スリープからの復帰後・出力先の切り替えなど）。止まっていれば何もしない</summary>
    public void Restart()
    {
        if (_thread is null) return;
        _restartRequested = true;
        _wake.Set();
    }

    private void Run()
    {
        MMDeviceEnumerator? enumerator = null;
        EndpointNotifier? notifier = null;
        try
        {
            while (!_stop)
            {
                _restartRequested = false;
                try
                {
                    // 起動直後で Windows の音のサービスがまだ準備中のこともあるので、作るところから再試行の対象にする
                    if (enumerator is null)
                    {
                        enumerator = new MMDeviceEnumerator();
                        var n = new EndpointNotifier(this);
                        try { enumerator.RegisterEndpointNotificationCallback(n); notifier = n; }
                        catch (Exception ex) { Log($"音楽連動: 出力先の切り替えの知らせを受け取れません（5 秒ごとに確認します）: {ex.Message}"); }
                    }
                    // 「特定のアプリだけ」: できればそのアプリの音だけを取り込む（できなければ出力先の音をすべて）
                    var app = _pending ?? OpenForSource(enumerator);
                    _pending = null;
                    var result = app is not null ? CaptureApp(enumerator, app) : CaptureOnce(enumerator);
                    _failures = 0;
                    if (result == CaptureResult.SwitchToPolling && EndpointId.Length > 0 && _pollingEndpoints.Add(EndpointId))
                        Log($"音楽連動: 音が届いた合図が来ないため、この出力先（{EndpointName}）は一定間隔で読み取ります");
                }
                catch (Exception ex)
                {
                    _failures++;
                    // 失敗が続くときは、待つ時間を 3 → 10 → 30 秒と延ばす（記録も同じ内容は繰り返さない）
                    int wait = _failures <= 1 ? 3 : _failures == 2 ? 10 : 30;
                    Status = L($"音を取り込めません（{wait} 秒後に再試行）: {Message(ex)}");
                    Log($"音楽連動: 取り込みに失敗: {Message(ex)}");
                    // 取り込みを閉じたときの「止まった」合図が残っていると待たずに再試行してしまうので、消してから待つ
                    if (!_stop && !_restartRequested) _wake.Reset();
                    _wake.WaitOne(TimeSpan.FromSeconds(wait));
                }
            }
        }
        finally
        {
            _pending?.Dispose();
            _pending = null;
            if (notifier is not null)
            {
                try { enumerator?.UnregisterEndpointNotificationCallback(notifier); } catch { /* 終了時は無視 */ }
            }
            try { enumerator?.Dispose(); } catch { /* 終了時は無視 */ }
        }
    }

    private enum CaptureResult { Normal, SwitchToPolling }

    /// <summary>今の出力先から取り込みを 1 回行う（出力先が変わる・止まる・止めるまで戻らない）</summary>
    private CaptureResult CaptureOnce(MMDeviceEnumerator enumerator)
    {
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            Status = L("音の出力先（スピーカー・ヘッドホン）が見つかりません");
            EndpointName = "";
            EndpointId = "";
            _wake.WaitOne(TimeSpan.FromSeconds(3));
            return CaptureResult.Normal;
        }

        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        string id = device.ID;
        bool polling = _pollingEndpoints.Contains(id);
        using var capture = new LowLatencyLoopback(device, eventSync: !polling);
        var format = capture.WaveFormat;
        Guid? sub = format is WaveFormatExtensible ext ? ext.SubFormat : null;
        var kind = PcmConvert.Detect((int)format.Encoding, format.BitsPerSample, sub);
        if (kind == PcmKind.Unsupported || format.SampleRate <= 0)
        {
            throw new NotSupportedException(L($"この音の形式には対応していません（{format.Encoding}・{format.BitsPerSample} bit・{format.SampleRate} Hz）"));
        }
        int channels = Math.Max(1, format.Channels);

        lock (_feedGate)
        {
            if (Engine.SampleRate != format.SampleRate)
            {
                Engine = new AudioEngine(format.SampleRate) { Settings = _settings, ClickDetect = _clickDetect, StatsLogging = true };
            }
            else
            {
                Engine.Reset(); // 出力先が変わった・取り込み直した: テンポの記録などを最初から
            }
            _clock = new SampleClock(format.SampleRate);
            _lastArrival = double.NegativeInfinity;
            _nextSampleT = double.NegativeInfinity;
        }

        Exception? stopped = null;
        bool stoppedFlag = false;
        capture.DataAvailable += (_, e) => Feed(e.Buffer, e.BytesRecorded, kind, channels);
        capture.RecordingStopped += (_, e) => { stopped = e.Exception; Volatile.Write(ref stoppedFlag, true); _wake.Set(); };
        capture.StartRecording();

        EndpointName = device.FriendlyName;
        Output = OutputProbe.Describe(device);
        EndpointId = id;
        EventDriven = !polling;
        Status = L($"音を取り込み中: {device.FriendlyName}");
        Log($"音楽連動: 取り込み開始（{device.FriendlyName}・{format.SampleRate} Hz・{channels} ch・{kind}・{(polling ? "一定間隔" : "合図で読み取り")}）");

        AppOnly = false;
        var result = CaptureResult.Normal;
        using var scan = new AppScan();
        double appMissingSince = double.NaN;
        bool appMissingLogged = false;
        double deviceCheckAt = AudioClock.Now;
        double meterAt = double.NegativeInfinity;
        double started = AudioClock.Now;
        double gate = 1;
        double lastLoop = AudioClock.Now;
        try
        {
            while (!_stop && !_restartRequested && !Volatile.Read(ref stoppedFlag))
            {
                _wake.WaitOne(10);
                double now = AudioClock.Now;
                double dt = Math.Clamp(now - lastLoop, 0, 0.5);
                lastLoop = now;

                FillSilence(now, _lastPeak > 0.001);

                // 出力先が変わっていないか（知らせを受け取れない環境のための予備。5 秒ごと）
                if (now - deviceCheckAt > 5)
                {
                    deviceCheckAt = now;
                    using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (current.ID != id) break;
                }

                // 音が鳴っているのに何も届かない: 取り込みが止まっている（合図が来ない環境なら方式を切り替える）
                if (now - meterAt > 0.1)
                {
                    meterAt = now;
                    double lastData = Volatile.Read(ref _lastArrival);
                    double since = now - (double.IsNegativeInfinity(lastData) ? started : lastData);
                    _lastPeak = device.AudioMeterInformation.MasterPeakValue;
                    if (since > StallAfter && _lastPeak > 0.01)
                    {
                        Log("音楽連動: 音が鳴っているのに取り込めていないため、取り込み直します");
                        result = double.IsNegativeInfinity(lastData) && !polling ? CaptureResult.SwitchToPolling : CaptureResult.Normal;
                        break;
                    }
                }

                // 「特定のアプリだけ」: そのアプリの音の割合（ならして使う）
                string source = _source;
                double target;
                // 同期テストが始まった: このアプリの音だけの取り込みに切り替える
                if (WantsSelf(source)) break;
                if (source.Length == 0 || source == SelfSource)
                {
                    target = 1;
                    ForgetApp();
                }
                else
                {
                    if (scan.Due(source, now))
                    {
                        scan.Refresh(enumerator, device, source, now);
                        // アプリが起動した・試し直す時刻になった: そのアプリの音だけの取り込みを準備できたら切り替える
                        if (TryPrepareApp(scan, source, now)) break;
                    }
                    double total = device.AudioMeterInformation.MasterPeakValue;
                    double app = scan.Peak();
                    target = total < 0.001 ? 0 : Math.Clamp(app / total, 0, 1);
                    // 音は鳴っているのに、選んだアプリの音がどの出力先にも見つからない状態が続いたら、1 回だけ記録に残す
                    if (total > 0.01 && scan.Sessions.Count == 0)
                    {
                        if (double.IsNaN(appMissingSince)) appMissingSince = now;
                        else if (!appMissingLogged && now - appMissingSince > 10)
                        {
                            appMissingLogged = true;
                            Log($"音楽連動: 「特定のアプリだけ」で選んだ {source} の音が見つかりません（ほかの音には反応しません）。アプリが再生中か、選んだアプリが合っているか確認してください");
                        }
                    }
                    else appMissingSince = double.NaN;
                }
                gate += (target - gate) * (1 - Math.Exp(-dt / AppGateTau));
                Engine.AppGate = gate;
            }
        }
        finally
        {
            try { capture.StopRecording(); } catch { /* 止まっている */ }
        }

        if (Volatile.Read(ref stoppedFlag) && stopped is not null)
        {
            Log($"音楽連動: 取り込みが止まりました（取り込み直します）: {Message(stopped)}");
            if (!_stop && !_restartRequested) _wake.Reset();
            _wake.WaitOne(500);
        }
        return result;
    }

    // =====================================================================
    // 「特定のアプリだけ」: そのアプリの音だけを直接取り込む
    // =====================================================================

    /// <summary>準備のできた、アプリの音だけの取り込み（まだ始めていない）と、終了を確かめるためのプロセス</summary>
    private sealed class OpenedApp : IDisposable
    {
        public OpenedApp(ProcessLoopbackCapture capture, SafeProcessHandle process, string source)
        {
            Capture = capture;
            Process = process;
            Source = source;
        }

        public ProcessLoopbackCapture Capture { get; }
        public SafeProcessHandle Process { get; }
        public string Source { get; }
        public int Pid => Capture.ProcessId;

        public void Dispose()
        {
            try { Capture.Dispose(); } catch { /* 終了時は無視 */ }
            Process.Dispose();
        }
    }

    /// <summary>取り込みを始める前に: アプリを選んでいれば、そのアプリの音だけの取り込みを準備する（できなければ null）</summary>
    private OpenedApp? OpenForSource(MMDeviceEnumerator enumerator)
    {
        string source = _source;
        double now = AudioClock.Now;
        if (source == SelfSource) return WantsSelf(source) ? TryOpenSelf() : null;
        if (source.Length == 0)
        {
            ForgetApp();
            return null;
        }
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var scan = new AppScan();
        scan.Refresh(enumerator, device, source, now);
        var decision = Plan(scan, source, now);
        return decision.Mode == AppCaptureMode.Process ? TryOpenApp(source, decision.Pid, now) : null;
    }

    /// <summary>選ぶアプリを外した: 前のアプリの失敗・記録した理由は持ち越さない（配列などを作らない）</summary>
    private void ForgetApp()
    {
        _planner.Decide("", false, 0, 0);
        _planLogged = "";
    }

    /// <summary>出力先の取り込み中に（3 秒ごと）: アプリの音だけを取り込めるようになっていれば準備して true（呼んだ側は今の取り込みを終える）</summary>
    private bool TryPrepareApp(AppScan scan, string source, double now)
    {
        var decision = Plan(scan, source, now);
        if (decision.Mode != AppCaptureMode.Process) return false;
        _pending = TryOpenApp(source, decision.Pid, now);
        return _pending is not null;
    }

    /// <summary>取り込み方を決め、以前からの方式になる理由が変わったときだけ記録に残す</summary>
    private AppCaptureDecision Plan(AppScan scan, string source, double now)
    {
        int target = AppAudioTarget.ChooseTarget(scan.Procs, source, scan.Entries);
        var decision = _planner.Decide(source, ProcessLoopbackCapture.IsSupported, target, now);
        string key = $"{source}|{decision.Mode}|{decision.Why}";
        if (key != _planLogged)
        {
            _planLogged = key;
            if (decision.Why == AppCaptureWhy.Unsupported)
                Log($"音楽連動: この Windows（ビルド {Environment.OSVersion.Version.Build}）は、アプリの音だけを取り込む機能に対応していないため、全体の音のうち {source} の音の割合で反応します（Windows 11 または Windows 10 ビルド {AppAudioTarget.ProcessLoopbackMinBuild} 以降で対応）");
            else if (decision.Why == AppCaptureWhy.NotRunning)
                Log($"音楽連動: {source} が起動していないため、全体の音のうち {source} の音の割合で反応します（起動すると、{source} の音だけを取り込みます）");
            // 失敗したとき（Failed）は、失敗した時点で理由を記録している
        }
        return decision;
    }

    /// <summary>アプリの音だけの取り込みを準備する。失敗したら理由を記録し、試し直すまでは以前からの方式にする</summary>
    private OpenedApp? TryOpenApp(string source, int pid, double now)
    {
        var process = ProcessList.Open(pid, source, out bool gone);
        if (process is null)
        {
            // 調べたあとに終了した: 失敗とはしない（起動し直していれば、次に調べたときに新しいプロセスですぐ試す）
            if (!gone) AppFailed(source, pid, now, L("プロセスを開けません（権限がありません）"));
            return null;
        }
        var capture = new ProcessLoopbackCapture(pid, includeTree: true);
        try
        {
            capture.Open(ActivateTimeout);
            return new OpenedApp(capture, process, source);
        }
        catch (Exception ex)
        {
            capture.Dispose();
            process.Dispose();
            AppFailed(source, pid, now, Message(ex));
            return null;
        }
    }

    /// <summary>同期テスト: このアプリ自身の音（クリック音）だけの取り込みを準備する。失敗したら、テストの間は出力先の音をすべて取り込む</summary>
    private OpenedApp? TryOpenSelf()
    {
        int pid = Environment.ProcessId;
        string name = AppAudioTarget.NormalizeName(Path.GetFileName(Environment.ProcessPath ?? ""));
        var process = ProcessList.Open(pid, name, out _);
        if (process is null)
        {
            SelfFailed("このアプリのプロセスを開けません"); // no-loc（ログだけ）
            return null;
        }
        var capture = new ProcessLoopbackCapture(pid, includeTree: true); // 木を含めない指定は「このアプリ以外」の意味になる
        try
        {
            capture.Open(ActivateTimeout);
            return new OpenedApp(capture, process, SelfSource);
        }
        catch (Exception ex)
        {
            capture.Dispose();
            process.Dispose();
            SelfFailed(Message(ex));
            return null;
        }
    }

    private void SelfFailed(string why)
    {
        _selfFailed = true;
        Log($"音楽連動: 同期テストのクリック音だけを取り込めないため、出力先の音をすべて取り込みます（曲を止めると光ります）: {why}");
    }

    /// <summary>アプリの音だけの取り込みがうまくいかなかった: 理由と、次に試す時期を記録する</summary>
    private void AppFailed(string source, int pid, double now, string why)
    {
        double wait = _planner.Failed(pid, now);
        string retry = double.IsPositiveInfinity(wait)
            ? L($"{source} を起動し直すと、もう一度試します")
            : L($"{(wait >= 60 ? L($"{wait / 60:0} 分") : L($"{wait:0} 秒"))}後にもう一度試します");
        _planLogged = $"{source}|{AppCaptureMode.EndpointGate}|{AppCaptureWhy.Failed}";
        Log($"音楽連動: {source}（PID {pid}）の音だけを取り込めないため、全体の音のうち {source} の音の割合で反応します（{retry}）: {why}");
    }

    /// <summary>
    /// 選んだアプリ（とその子プロセス）の音だけを取り込む（アプリが終了する・別のプロセスに変わる・止まる・止めるまで戻らない）。
    /// 出力先の音量メーターは使わず、アプリの音のセッションのメーターで「音が鳴っているか」を判断する
    /// </summary>
    private CaptureResult CaptureApp(MMDeviceEnumerator enumerator, OpenedApp app)
    {
        using var opened = app;
        var capture = app.Capture;
        string source = app.Source;
        int pid = app.Pid;
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        string id = device.ID;

        lock (_feedGate)
        {
            if (Engine.SampleRate != ProcessLoopbackCapture.SampleRate)
            {
                Engine = new AudioEngine(ProcessLoopbackCapture.SampleRate) { Settings = _settings, ClickDetect = _clickDetect, StatsLogging = true };
            }
            else
            {
                Engine.Reset(); // 取り込み方が変わった: テンポの記録などを最初から
            }
            // ほかのアプリの音は混ざらないので、反応の強さはそのまま（取り込みを始める前に 1 にしておく）
            Engine.AppGate = 1;
            _clock = new SampleClock(ProcessLoopbackCapture.SampleRate);
            _lastArrival = double.NegativeInfinity;
            _nextSampleT = double.NegativeInfinity;
        }
        Volatile.Write(ref _lastSound, double.NegativeInfinity);

        Exception? stopped = null;
        bool stoppedFlag = false;
        capture.DataAvailable = (buffer, bytes) =>
        {
            // アプリが音を出していなくても 0 の音は届き続けるので、0 でない音が届いた時刻を別に覚える（取り込めているかの確認用）
            if (MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytes)).IndexOfAnyExcept(0f) >= 0) Volatile.Write(ref _lastSound, AudioClock.Now);
            Feed(buffer, bytes, PcmKind.Float32, ProcessLoopbackCapture.Channels);
        };
        capture.Stopped = ex => { stopped = ex; Volatile.Write(ref stoppedFlag, true); _wake.Set(); };
        // 同期テスト: このアプリ自身の音だけ（アプリの音のセッションを探す・別のプロセスに移るのを追う必要はない）
        bool self = source == SelfSource;
        using var scan = new AppScan();
        if (!self) scan.Refresh(enumerator, device, source, AudioClock.Now);
        capture.Start();

        // 聞いている出力先: アプリが「いつもの出力先」以外に出しているなら（アプリごとの出力先の設定など）、そちら
        var playing = scan.PlayingElsewhere ?? device;
        EndpointName = playing.FriendlyName;
        Output = OutputProbe.Describe(playing);
        EndpointId = id;
        EventDriven = true;
        AppOnly = true;
        Status = L("アプリの音だけを取り込み中");
        _planLogged = "";
        Log($"音楽連動: {SourceName(source)} の音だけを取り込み開始（PID {pid} とその子プロセス・{ProcessLoopbackCapture.SampleRate} Hz・{ProcessLoopbackCapture.Channels} ch・{PcmKind.Float32}・聞いている出力先: {device.FriendlyName}）");

        double started = AudioClock.Now;
        double deviceCheckAt = started;
        double meterAt = double.NegativeInfinity;
        double aliveAt = started;
        bool healthy = false;
        double appPeak = 0;
        try
        {
            while (!_stop && !_restartRequested && !Volatile.Read(ref stoppedFlag))
            {
                _wake.WaitOne(10);
                double now = AudioClock.Now;

                FillSilence(now, appPeak > AppAudioTarget.SoundingPeak);
                Engine.AppGate = 1;

                // 選ぶアプリが変わった・外した: 取り込み方を決め直す
                if (!string.Equals(_source, source, StringComparison.OrdinalIgnoreCase)) break;

                // 出力先が変わっていないか（聞いている出力先の表示のため。知らせを受け取れない環境のための予備。5 秒ごと）
                if (now - deviceCheckAt > 5)
                {
                    deviceCheckAt = now;
                    using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (current.ID != id) break;
                }

                // アプリが終了した: 取り込み方を決め直す（起動していなければ以前からの方式で、起動し直したらまたこの方式で）
                if (now - aliveAt > 0.5)
                {
                    aliveAt = now;
                    if (!ProcessList.IsAlive(app.Process))
                    {
                        Log($"音楽連動: {source}（PID {pid}）が終了しました");
                        break;
                    }
                }

                // アプリの音が鳴っている（セッションのメーター）のに、0 の音しか届かない: この方法では取り込めていない
                if (now - meterAt > 0.1)
                {
                    meterAt = now;
                    appPeak = scan.Peak();
                    double lastSound = Volatile.Read(ref _lastSound);
                    double since = now - Math.Max(started, lastSound);
                    if (since > StallAfter && appPeak > 0.01)
                    {
                        AppFailed(source, pid, now, double.IsNegativeInfinity(Volatile.Read(ref _lastArrival))
                            ? L("アプリの音が鳴っているのに、音が届きません")
                            : L("アプリの音が鳴っているのに、無音しか届きません"));
                        break;
                    }
                    if (!healthy && !double.IsNegativeInfinity(lastSound) && now - started > AppCapturePlanner.HealthyAfter)
                    {
                        healthy = true;
                        _planner.Healthy(pid);
                    }
                }

                // 3 秒ごと: 音を出しているプロセスが、取り込んでいるプロセスの木から別の木に移っていないか
                if (!self && scan.Due(source, now))
                {
                    scan.Refresh(enumerator, device, source, now);
                    // アプリが出している出力先が変わった（アプリの設定で切り替えた など）: 表示と案内を合わせる
                    var playingNow = scan.PlayingElsewhere ?? device;
                    if (playingNow.FriendlyName != EndpointName)
                    {
                        EndpointName = playingNow.FriendlyName;
                        Output = OutputProbe.Describe(playingNow);
                    }
                    int desired = AppAudioTarget.ChooseTarget(scan.Procs, source, scan.Entries);
                    bool alive = ProcessList.IsAlive(app.Process);
                    if (AppAudioTarget.ShouldRetarget(pid, alive, desired,
                            AppAudioTarget.TreeSounding(scan.Procs, source, pid, scan.Entries),
                            AppAudioTarget.TreeSounding(scan.Procs, source, desired, scan.Entries)))
                    {
                        Log($"音楽連動: {source} の音を出しているプロセスが変わったため、取り込み直します（PID {pid} → {(desired == 0 ? "なし" : desired.ToString())}）");
                        break;
                    }
                }
            }
        }
        finally
        {
            AppOnly = false;
            capture.Stop();
        }

        if (Volatile.Read(ref stoppedFlag) && stopped is not null)
        {
            if (self) SelfFailed($"取り込みが止まりました: {Message(stopped)}"); // no-loc（ログだけ）
            else AppFailed(source, pid, AudioClock.Now, L($"取り込みが止まりました: {Message(stopped)}"));
            if (!_stop && !_restartRequested) _wake.Reset();
            _wake.WaitOne(500);
        }
        return CaptureResult.Normal;
    }

    /// <summary>届いた音（ふつうは 32bit 浮動小数・2ch）をモノラルにして分析に回す（NAudio の取り込みスレッドから呼ばれる）</summary>
    private void Feed(byte[] buffer, int bytes, PcmKind kind, int channels)
    {
        if (bytes <= 0) return;
        double arrival = AudioClock.Now;
        int frames = bytes / (PcmConvert.BytesPerSample(kind) * channels);
        if (frames <= 0) return;
        lock (_feedGate)
        {
            if (_mono.Length < frames) _mono = new float[Math.Max(frames, _mono.Length * 2)];
            int n = PcmConvert.ToMono(buffer.AsSpan(0, bytes), kind, channels, _mono);
            double t = _clock.Observe(n, arrival);
            // 時刻が前の音より戻らないようにする（無音で埋めた直後に本物の音が届いたときなど）。
            // わずかなずれ（5 ミリ秒未満）は少しずつ戻して正しい時刻に近づけ、大きく重なるときは前の音の続きにする
            if (t < _nextSampleT) t = _nextSampleT - t < 0.005 ? Math.Max(t, _nextSampleT - 0.0002) : _nextSampleT;
            Engine.Push(_mono.AsSpan(0, n), t);
            _nextSampleT = t + (double)n / _clock.SampleRate;
            Volatile.Write(ref _lastArrival, arrival);
        }
    }

    /// <summary>音が届いていない間は、無音（0）を足して時間を進める（今から 10 ミリ秒前まで）</summary>
    private void FillSilence(double now, bool soundPlaying)
    {
        // 出力先のメーターが音を示しているなら、本物の音が遅れて届くだけなので埋めない（曲の途中に無音を挟まない）
        if (soundPlaying) return;
        lock (_feedGate)
        {
            double lastData = _lastArrival;
            if (!double.IsNegativeInfinity(lastData) && now - lastData < SilenceAfter) return;
            int rate = _clock.SampleRate;
            // まだ一度も音が届いていなければ、今から少し前を始まりにする
            double from = double.IsNegativeInfinity(_nextSampleT) ? now - SilenceLead : _nextSampleT;
            int frames = (int)Math.Floor((now - SilenceLead - from) * rate);
            if (double.IsNegativeInfinity(_nextSampleT))
            {
                _nextSampleT = from;
                return;
            }
            if (frames <= 0) return;
            // 長く止まっていた（スリープなど）ときは 1 秒分だけ足し、時刻はそこから飛ばす
            if (frames > rate)
            {
                _nextSampleT = now - SilenceLead - 1.0;
                frames = rate;
            }
            while (frames > 0)
            {
                int n = Math.Min(frames, _zeros.Length);
                _clock.AdvanceSilence(n, now);
                double t = _nextSampleT;
                Engine.Push(_zeros.AsSpan(0, n), t);
                _nextSampleT = t + (double)n / rate;
                frames -= n;
            }
        }
    }

    /// <summary>
    /// 選んだアプリのプロセスと、その音のセッション（3 秒ごとに調べ直す）。
    /// セッションのメーターは、出力先の取り込みでは「アプリの音の割合」に、アプリの音だけの取り込みでは「音が鳴っているか」に使う
    /// </summary>
    private sealed class AppScan : IDisposable
    {
        public readonly List<AudioSessionControl> Sessions = new();
        public readonly List<SessionEntry> Entries = new();
        /// <summary>ほかの出力先で見つけたセッションのために開いたままにしている出力先</summary>
        private readonly List<MMDevice> _devices = new();
        private readonly HashSet<uint> _pids = new();
        private string _source = "";
        private double _at = double.NegativeInfinity;

        /// <summary>アプリの音が鳴っている出力先（いつもの出力先にセッションがなく、ほかの出力先で見つけたとき。なければ null）</summary>
        public MMDevice? PlayingElsewhere => _devices.Count > 0 ? _devices[0] : null;

        /// <summary>選んだ名前のプロセス（PID・親の PID）</summary>
        public List<ProcEntry> Procs { get; private set; } = new();

        /// <summary>調べ直す時期か（選ぶアプリが変わった・前に調べてから 3 秒たった）</summary>
        public bool Due(string source, double now) => !string.Equals(source, _source, StringComparison.Ordinal) || now - _at > AppScanEvery;

        public void Refresh(MMDeviceEnumerator enumerator, MMDevice device, string source, double now)
        {
            _at = now;
            _source = source;
            Clear();
            Procs = ProcessList.Snapshot(source);
            if (Procs.Count == 0) return;
            foreach (var p in Procs) _pids.Add((uint)p.Pid);
            FindSessions(enumerator, device, _pids, Sessions, _devices);
            foreach (var s in Sessions)
            {
                try
                {
                    Entries.Add(new SessionEntry((int)s.GetProcessID, s.State == AudioSessionState.AudioSessionStateActive, s.AudioMeterInformation.MasterPeakValue));
                }
                catch { /* アプリが終了した */ }
            }
        }

        /// <summary>選んだアプリのセッションの、いまの音の大きさ（いちばん大きいもの。0〜1。配列などを作らない）</summary>
        public double Peak()
        {
            double app = 0;
            foreach (var s in Sessions)
            {
                try { app = Math.Max(app, s.AudioMeterInformation.MasterPeakValue); }
                catch { /* アプリが終了した */ }
            }
            return app;
        }

        private void Clear()
        {
            DisposeAll(Sessions);
            DisposeDevices(_devices);
            Entries.Clear();
            _pids.Clear();
        }

        public void Dispose() => Clear();
    }

    /// <summary>
    /// 選んだアプリの音のセッションを探す。取り込んでいる出力先にないときは、ほかの出力先も探す
    /// （SteelSeries Sonar・VB-Audio などの仮想の出力先を通すと、アプリの音はそちらのセッションになり、
    ///   実際に鳴っている出力先には見えないため）。ほかの出力先で見つけたときは、その出力先を extra に入れて開いたままにする
    /// </summary>
    private static void FindSessions(MMDeviceEnumerator enumerator, MMDevice device, HashSet<uint> pids, List<AudioSessionControl> list, List<MMDevice> extra)
    {
        if (pids.Count == 0) return;
        AddSessions(device, pids, list);
        if (list.Count > 0) return;
        try
        {
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                bool keep = false;
                try
                {
                    if (d.ID != device.ID)
                    {
                        int before = list.Count;
                        AddSessions(d, pids, list);
                        keep = list.Count > before;
                    }
                }
                catch { /* 途中で外れた出力先 */ }
                if (keep) extra.Add(d);
                else d.Dispose();
            }
        }
        catch { /* 出力先の一覧を取れない */ }
    }

    private static void AddSessions(MMDevice device, HashSet<uint> pids, List<AudioSessionControl> list)
    {
        var manager = device.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            AudioSessionControl? s = null;
            try
            {
                s = sessions[i];
                if (pids.Contains(s.GetProcessID)) { list.Add(s); s = null; }
            }
            catch { /* 途中で終了したセッション */ }
            finally { s?.Dispose(); }
        }
    }

    private static void DisposeDevices(List<MMDevice> devices)
    {
        foreach (var d in devices)
        {
            try { d.Dispose(); } catch { /* 無視 */ }
        }
        devices.Clear();
    }

    private static void DisposeAll(List<AudioSessionControl> sessions)
    {
        foreach (var s in sessions)
        {
            try { s.Dispose(); } catch { /* 無視 */ }
        }
        sessions.Clear();
    }

    private static string Message(Exception ex) => ex is AggregateException { InnerException: { } inner } ? inner.Message : ex.Message;

    /// <summary>同じ内容を何度も記録しない</summary>
    private void Log(string message)
    {
        if (message == _lastLog) return;
        _lastLog = message;
        Logger.Info(message);
    }

    /// <summary>出力先の切り替え・抜き差しの知らせ（Windows の別スレッドから呼ばれるので、合図だけして戻る）</summary>
    private sealed class EndpointNotifier : IMMNotificationClient
    {
        private readonly AudioCapture _owner;
        public EndpointNotifier(AudioCapture owner) => _owner = owner;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) _owner.Restart();
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (deviceId == _owner.EndpointId && newState != DeviceState.Active) _owner.Restart();
        }

        public void OnDeviceRemoved(string deviceId)
        {
            if (deviceId == _owner.EndpointId) _owner.Restart();
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// 遅れの少ないループバック録音。NAudio の WasapiLoopbackCapture は 0.1 秒ずつまとめて渡すので、
/// 音が届いた合図ですぐ読む（10 ミリ秒ずつ）設定の WasapiCapture をループバックとして使う。
/// 合図が来ない古い環境では、一定間隔（20 ミリ秒）で読みに行く
/// </summary>
internal sealed class LowLatencyLoopback : WasapiCapture
{
    public LowLatencyLoopback(MMDevice device, bool eventSync) : base(device, eventSync, eventSync ? 10 : 20) { }

    protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback;
}

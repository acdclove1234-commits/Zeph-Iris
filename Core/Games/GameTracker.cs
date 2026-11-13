// ==================================================
// GameTracker.cs - PresentMon のフレームから「ゲームを遊んでいるあいだ」を見つけて、
//                  FPS と温度の記録（1 回遊ぶごとに 1 件）にまとめる
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>記録の 10 秒ごとの点（遊んでいた時間で 10 秒ごと）</summary>
public sealed class GameSample
{
    public double Fps { get; set; }
    public float? CpuTemp { get; set; }
    public float? GpuTemp { get; set; }
}

/// <summary>1 回ぶんの記録（data\games.jsonl の 1 行）</summary>
public sealed class GameSession
{
    /// <summary>exe の名前（.exe は付けない）</summary>
    public string App { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    /// <summary>描いていた時間（秒。止まっていた時間は入れない）</summary>
    public double PlayedSeconds { get; set; }
    public long Frames { get; set; }
    public double AvgFps { get; set; }
    public double Low1Fps { get; set; }
    public double Low01Fps { get; set; }
    public float? CpuTempAvg { get; set; }
    public float? CpuTempMax { get; set; }
    public float? GpuTempAvg { get; set; }
    public float? GpuTempMax { get; set; }
    public float? CpuLoadAvg { get; set; }
    public float? GpuLoadAvg { get; set; }
    public float? GpuPowerAvg { get; set; }
    public float? FanRpmAvg { get; set; }
    public List<GameSample> Timeline { get; set; } = new();
}

/// <summary>1 秒（くらい）ごとに渡す、PC の様子</summary>
/// <param name="ForegroundPid">いちばん手前のウィンドウのプロセス ID（分からなければ null）</param>
/// <param name="FanRpm">ファンの回転数の平均（rpm）</param>
public sealed record GameTick(DateTime Time, int? ForegroundPid,
    float? CpuTemp = null, float? GpuTemp = null, float? CpuLoad = null, float? GpuLoad = null, float? GpuPower = null, float? FanRpm = null);

/// <summary>
/// ゲームの見つけ方:
///   1. 1 秒に 10 枚以上描いていて、
///   2. いちばん手前のウィンドウで、
///   3. ゲームでないと分かっているアプリ（ブラウザー・チャット・動画プレーヤーなど）でも、記録しないと決めたアプリでもない、
///   ものが合わせて 60 秒続いたら、ゲームとして記録を始める（それまでのフレームも記録に入れる）。
///   条件に合わない 1 秒ごとに 1 秒ずつ戻すので、ときどき手前に来るだけのアプリは記録しない。
/// 60 秒続けて 1 枚も描かなければ（ゲームを終えた）、記録を締めて返す。
/// 描くのが止まっているあいだ（ロード・一時停止・最小化）は、遊んだ時間にも FPS にも入れない。
/// AddFrame は PresentMon を読むスレッドから、Tick は画面のスレッドから呼ぶ
/// </summary>
public sealed class GameTracker
{
    public const double MinBusyFps = 10;
    public const double StartAfterSec = 60;
    public const double EndAfterIdleSec = 60;
    public const double ForgetAfterSec = 30;
    public const double TimelineStepSec = 10;

    /// <summary>描き続けることがあるが、ゲームではないアプリ（小文字・.exe なし）</summary>
    public static readonly IReadOnlySet<string> NotGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Windows
        "dwm", "explorer", "applicationframehost", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "searchapp",
        "textinputhost", "lockapp", "systemsettings", "widgets", "windowsterminal", "wt", "mmc", "taskmgr", "magnify", "snippingtool",
        // ブラウザー
        "chrome", "msedge", "msedgewebview2", "firefox", "opera", "opera_gx", "brave", "vivaldi", "iexplore", "arc", "waterfox", "librewolf",
        // チャット・会議・AI
        "claude", "chatgpt", "discord", "slack", "teams", "ms-teams", "msteams", "zoom", "line", "whatsapp", "telegram", "signal", "skype",
        // 配信・録画・オーバーレイ・ランチャー
        "obs64", "obs32", "streamlabs obs", "xsplit.core", "nvidia overlay", "nvcontainer", "radeonsoftware", "amdrsserv", "gamebar",
        "steamwebhelper", "steam", "epicgameslauncher", "battle.net", "eadesktop", "galaxyclient", "upc", "riotclientux", "riotclientservices",
        // 動画・音楽
        "spotify", "vlc", "mpc-hc64", "mpc-hc", "mpc-be64", "potplayermini64", "potplayermini", "mpv", "wmplayer", "video.ui",
        "microsoft.media.player", "itunes", "amazon music",
        // 制作・開発
        "code", "cursor", "devenv", "rider64", "idea64", "pycharm64", "unity", "unrealeditor", "blender", "photoshop", "afterfx",
        "adobe premiere pro", "resolve", "figma", "notion", "obsidian", "powerpnt",
        // 壁紙・飾り・このアプリ
        "wallpaper32", "wallpaper64", "lively", "rainmeter", "zeph-iris", "openrgb", "signalrgb", "armourycrate", "icue",
    };

    private sealed class Candidate
    {
        public required string App;
        public required int Pid;
        public DateTime FirstSeen;
        public DateTime LastFrame;
        public double Busy;
        public int PendingFrames;
        public double PendingMs;
        public readonly FrameTimes Frames = new();
    }

    private sealed class Active
    {
        public required Candidate Game;
        public readonly SensorStats.Entry Cpu = new(), Gpu = new(), CpuLoad = new(), GpuLoad = new(), GpuPower = new(), Fan = new();
        public readonly List<GameSample> Timeline = new();
        public double StepSec, StepMs;
        public long StepFrames;
        public SensorStats.Entry StepCpu = new(), StepGpu = new();
        public void ResetStep()
        {
            StepSec = 0; StepMs = 0; StepFrames = 0;
            StepCpu = new();
            StepGpu = new();
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, Candidate> _candidates = new();
    private HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private Active? _active;
    private DateTime? _lastTick;
    private double _lastFps = double.NaN;

    public static string Normalize(string app)
    {
        string a = app.Trim();
        return a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a;
    }

    public static bool IsNotGame(string app) => NotGames.Contains(Normalize(app));

    /// <summary>記録しないと決めたアプリ（設定）</summary>
    public void SetExcluded(IEnumerable<string> apps)
    {
        var set = new HashSet<string>(apps.Where(a => !string.IsNullOrWhiteSpace(a)).Select(Normalize), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            _excluded = set;
            foreach (var pid in _candidates.Where(c => set.Contains(c.Value.App) && _active?.Game != c.Value).Select(c => c.Key).ToList())
                _candidates.Remove(pid);
        }
    }

    public void AddFrame(PresentFrame f)
    {
        string app = Normalize(f.App);
        lock (_gate)
        {
            if (!_candidates.TryGetValue(f.ProcessId, out var c))
            {
                if (NotGames.Contains(app) || _excluded.Contains(app)) return;
                DateTime now = _lastTick ?? DateTime.Now;
                c = new Candidate { App = app, Pid = f.ProcessId, FirstSeen = now, LastFrame = now };
                _candidates[f.ProcessId] = c;
            }
            c.PendingFrames++;
            if (f.Ms > 0 && f.Ms <= FrameTimes.MaxFrameMs) c.PendingMs += f.Ms;
            c.Frames.Add(f.Ms);
        }
    }

    /// <summary>いま記録しているゲーム（なければ null）</summary>
    public string? CurrentApp
    {
        get { lock (_gate) return _active?.Game.App; }
    }

    /// <summary>直近の 1 秒（くらい）の FPS（記録中のゲーム。なければ NaN）</summary>
    public double CurrentFps
    {
        get { lock (_gate) return _active is null ? double.NaN : _lastFps; }
    }

    /// <summary>1 秒（くらい）ごとに呼ぶ。ゲームを終えたら、その記録を返す</summary>
    public GameSession? Tick(GameTick t)
    {
        lock (_gate)
        {
            double dt = _lastTick is DateTime last ? Math.Clamp((t.Time - last).TotalSeconds, 0, 5) : 1;
            _lastTick = t.Time;

            foreach (var c in _candidates.Values.ToList())
            {
                bool drew = c.PendingFrames > 0;
                if (drew) c.LastFrame = t.Time;
                bool busy = dt > 0 && c.PendingFrames / dt >= MinBusyFps && t.ForegroundPid == c.Pid;
                c.Busy = busy ? c.Busy + dt : Math.Max(0, c.Busy - dt);

                if (_active?.Game == c)
                {
                    _lastFps = c.PendingMs > 0 ? c.PendingFrames * 1000.0 / c.PendingMs : double.NaN;
                    if (drew) Accumulate(_active, t, dt, c.PendingFrames, c.PendingMs);
                }
                else if ((t.Time - c.LastFrame).TotalSeconds >= ForgetAfterSec)
                {
                    _candidates.Remove(c.Pid);
                }
                c.PendingFrames = 0;
                c.PendingMs = 0;
            }

            if (_active is null)
            {
                var start = _candidates.Values.Where(c => c.Busy >= StartAfterSec).OrderByDescending(c => c.Busy).FirstOrDefault();
                if (start is not null) _active = new Active { Game = start };
                return null;
            }
            if ((t.Time - _active.Game.LastFrame).TotalSeconds >= EndAfterIdleSec) return FinishLocked();
            return null;
        }
    }

    /// <summary>記録をやめるとき・アプリを終えるとき: 記録中のゲームがあれば締めて返す</summary>
    public GameSession? Finish()
    {
        lock (_gate) return _active is null ? null : FinishLocked();
    }

    private static void Accumulate(Active a, GameTick t, double dt, int frames, double ms)
    {
        Add(a.Cpu, t.CpuTemp);
        Add(a.Gpu, t.GpuTemp);
        Add(a.CpuLoad, t.CpuLoad);
        Add(a.GpuLoad, t.GpuLoad);
        Add(a.GpuPower, t.GpuPower);
        Add(a.Fan, t.FanRpm);
        Add(a.StepCpu, t.CpuTemp);
        Add(a.StepGpu, t.GpuTemp);
        a.StepSec += dt;
        a.StepFrames += frames;
        a.StepMs += ms;
        if (a.StepSec >= TimelineStepSec) FlushStep(a);
    }

    private static void FlushStep(Active a)
    {
        if (a.StepSec <= 0) return;
        a.Timeline.Add(new GameSample
        {
            Fps = a.StepMs > 0 ? Math.Round(a.StepFrames * 1000.0 / a.StepMs, 1) : 0,
            CpuTemp = Value(a.StepCpu),
            GpuTemp = Value(a.StepGpu),
        });
        a.ResetStep();
    }

    private GameSession FinishLocked()
    {
        var a = _active!;
        _active = null;
        _lastFps = double.NaN;
        _candidates.Remove(a.Game.Pid);
        FlushStep(a);
        var f = a.Game.Frames;
        return new GameSession
        {
            App = a.Game.App,
            Start = a.Game.FirstSeen,
            End = a.Game.LastFrame,
            PlayedSeconds = Math.Round(f.TotalMs / 1000, 1),
            Frames = f.Count,
            AvgFps = Round(f.AverageFps),
            Low1Fps = Round(f.LowFps(0.01)),
            Low01Fps = Round(f.LowFps(0.001)),
            CpuTempAvg = Value(a.Cpu), CpuTempMax = Max(a.Cpu),
            GpuTempAvg = Value(a.Gpu), GpuTempMax = Max(a.Gpu),
            CpuLoadAvg = Value(a.CpuLoad), GpuLoadAvg = Value(a.GpuLoad),
            GpuPowerAvg = Value(a.GpuPower), FanRpmAvg = Value(a.Fan),
            Timeline = a.Timeline,
        };
    }

    private static void Add(SensorStats.Entry e, float? v)
    {
        if (v is float f) e.Add(f);
    }

    private static float? Value(SensorStats.Entry e) => e.Count > 0 ? MathF.Round(e.Average, 1) : null;
    private static float? Max(SensorStats.Entry e) => e.Count > 0 ? MathF.Round(e.Max, 1) : null;
    private static double Round(double v) => double.IsNaN(v) ? 0 : Math.Round(v, 1);
}

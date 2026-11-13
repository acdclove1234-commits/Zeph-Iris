// ゲームの記録（FrameTimes・PresentMonCsv・GameTracker・GameLog）。アプリの名前は作り物
using ProFanController;

namespace LogicTests;

public static class GameTrackerTests
{
    private static readonly DateTime T0 = new(2026, 11, 13, 20, 0, 0);

    public static void TestFrameTimesLows()
    {
        var f = new FrameTimes();
        for (int i = 0; i < 990; i++) f.Add(10);     // 100 fps
        for (int i = 0; i < 10; i++) f.Add(50);      // ときどき 20 fps に落ちる
        T.Near(f.AverageFps, 1000 * 1000.0 / (990 * 10 + 10 * 50), 0.01, "平均 FPS = フレーム数 ÷ 時間");
        T.Near(f.LowFps(0.01), 1000 / 50.05, 0.1, "1% Low は長いほうから 1% のフレーム");
        T.Near(f.LowFps(0.001), 1000 / 50.05, 0.1, "0.1% Low");
        f.Add(5000);
        f.Add(double.NaN);
        f.Add(-1);
        T.Check(f.Count == 1000, "1 秒より長い（止まっていた）・おかしな値は数えない");
        T.Check(double.IsNaN(new FrameTimes().AverageFps), "空なら NaN");
    }

    public static void TestCsvByHeader()
    {
        var csv = new PresentMonCsv();
        T.Check(!csv.TryParse("warning: PresentMon requires elevated privilege", out _), "警告は読まない");
        T.Check(!csv.TryParse("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents", out _), "見出し");
        T.Check(csv.HasHeader, "1.x の見出しを読めた");
        T.Check(csv.TryParse("Game.exe,1234,0x01,DXGI,0,0,0,0.0153,0.05,6.9707", out var f)
                && f.App == "Game.exe" && f.ProcessId == 1234 && Math.Abs(f.Ms - 6.9707) < 1e-9, "1 フレーム");
        T.Check(!csv.TryParse("<unknown>,99,0x01,DXGI,0,0,0,0.1,0.05,6.0", out _), "名前の分からないプロセスは読まない");
        T.Check(!csv.TryParse("Game.exe,1234,0x01", out _), "途中で切れた行は読まない");

        var v2 = new PresentMonCsv();
        v2.TryParse("Application,ProcessID,SwapChainAddress,PresentRuntime,TimeInMs,MsBetweenPresents,MsCPUBusy", out _);
        T.Check(v2.TryParse("Other.exe,7,0x1,DXGI,1.5,16.6,3.0", out var g) && Math.Abs(g.Ms - 16.6) < 1e-9, "2.x の見出しでも読める");
        T.Check(!new PresentMonCsv().TryParse("Game.exe,1234,0x01,DXGI,0,0,0,0.0153,0.05,6.9", out _), "見出しの前の行は読まない");
    }

    /// <summary>seconds 秒ぶん、fps で描きながら 1 秒ごとに Tick する</summary>
    private static List<GameSession> Play(GameTracker t, ref DateTime now, string app, int pid, double fps, int seconds, int? foreground, float cpu = 60, float gpu = 70)
    {
        var done = new List<GameSession>();
        for (int s = 0; s < seconds; s++)
        {
            for (int i = 0; i < (int)fps; i++) t.AddFrame(new PresentFrame(app, pid, 1000 / fps));
            now = now.AddSeconds(1);
            if (t.Tick(new GameTick(now, foreground ?? pid, cpu, gpu, 50, 90, 200, 1100)) is { } g) done.Add(g);
        }
        return done;
    }

    private static List<GameSession> Idle(GameTracker t, ref DateTime now, int seconds, int? foreground = null)
    {
        var done = new List<GameSession>();
        for (int s = 0; s < seconds; s++)
        {
            now = now.AddSeconds(1);
            if (t.Tick(new GameTick(now, foreground)) is { } g) done.Add(g);
        }
        return done;
    }

    public static void TestSessionStartsAndEnds()
    {
        var t = new GameTracker();
        var now = T0;
        t.Tick(new GameTick(now, null));
        Play(t, ref now, "SpaceGame.exe", 100, 120, 59, null);
        T.Check(t.CurrentApp is null, "60 秒たつまでは記録を始めない");
        Play(t, ref now, "SpaceGame.exe", 100, 120, 2, null);
        T.Check(t.CurrentApp == "SpaceGame", "手前で 60 秒描き続けたらゲームとして記録（.exe は付けない）");
        T.Near(t.CurrentFps, 120, 0.5, "いまの FPS");
        Play(t, ref now, "SpaceGame.exe", 100, 60, 60, null, cpu: 80, gpu: 75);
        var ended = Idle(t, ref now, 61);
        T.Check(ended.Count == 1, "60 秒描かなければ記録を締める");
        var g = ended[0];
        T.Check(g.App == "SpaceGame" && g.Start == T0, "始まりは最初に描いたとき（待っていた 60 秒も入れる）");
        T.Near(g.PlayedSeconds, 121, 1, "描いていた時間");
        T.Near(g.AvgFps, (61 * 120 + 60 * 60) / 121.0, 0.5, "平均 FPS");
        T.Near(g.Low1Fps, 60, 0.5, "1% Low");
        T.Check(g.CpuTempMax == 80 && g.GpuTempMax == 75, "最高温度は記録を始めてから");
        T.Check(g.FanRpmAvg == 1100 && g.GpuLoadAvg == 90, "ファン・GPU 使用率");
        T.Check(g.Timeline.Count >= 6 && g.Timeline[^1].Fps > 55 && g.Timeline[^1].Fps < 65, "10 秒ごとの点");
        T.Check(t.CurrentApp is null && double.IsNaN(t.CurrentFps), "締めたら記録中ではない");
    }

    public static void TestBackgroundAndNonGames()
    {
        var t = new GameTracker();
        var now = T0;
        Play(t, ref now, "SpaceGame.exe", 100, 120, 120, foreground: 555);
        T.Check(t.CurrentApp is null, "手前にないアプリは記録しない");
        Play(t, ref now, "chrome.exe", 200, 60, 120, null);
        Play(t, ref now, "Claude.exe", 201, 165, 120, null);
        T.Check(t.CurrentApp is null, "ブラウザー・チャットのアプリは記録しない");
        Play(t, ref now, "Slideshow.exe", 300, 5, 120, null);
        T.Check(t.CurrentApp is null, "1 秒に 10 枚未満なら記録しない");

        t.SetExcluded(new[] { "Editor.exe" });
        Play(t, ref now, "Editor.exe", 400, 60, 120, null);
        T.Check(t.CurrentApp is null, "記録しないと決めたアプリ");
        T.Check(GameTracker.IsNotGame("MSEDGE.EXE") && !GameTracker.IsNotGame("SpaceGame"), "大文字・.exe があっても同じ");
    }

    public static void TestAltTabKeepsSession()
    {
        var t = new GameTracker();
        var now = T0;
        Play(t, ref now, "SpaceGame.exe", 100, 100, 30, null);
        Play(t, ref now, "SpaceGame.exe", 100, 100, 10, foreground: 9);   // 10 秒だけほかのウィンドウ
        Play(t, ref now, "SpaceGame.exe", 100, 100, 30, null);
        T.Check(t.CurrentApp is null, "手前にない時間は戻す（30+30−10 = 50 秒）");
        Play(t, ref now, "SpaceGame.exe", 100, 100, 15, null);
        T.Check(t.CurrentApp == "SpaceGame", "合わせて 60 秒で記録を始める");
        Play(t, ref now, "SpaceGame.exe", 100, 100, 30, foreground: 9);
        Idle(t, ref now, 30);
        T.Check(t.CurrentApp == "SpaceGame", "記録を始めたら、手前になくても・少し止まっても続ける");
        var g = t.Finish();
        T.Check(g is { App: "SpaceGame" } && t.CurrentApp is null, "記録をやめたら締める");
        T.Check(t.Finish() is null, "記録中でなければ何も返さない");
    }

    public static void TestLogRoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zi-games-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, GameLog.FileName);
        try
        {
            var a = new GameSession { App = "SpaceGame", Start = T0, End = T0.AddHours(1), PlayedSeconds = 3500, Frames = 400000, AvgFps = 114.3, Low1Fps = 71.2, CpuTempMax = 81.5f };
            var b = new GameSession { App = "RaceGame", Start = T0.AddDays(1), End = T0.AddDays(1).AddMinutes(20), PlayedSeconds = 1200, Frames = 72000, AvgFps = 60 };
            GameLog.Append(a, path);
            GameLog.Append(b, path);
            File.AppendAllText(path, "{\"App\":\"Brok");   // 書きかけで切れた行
            var all = GameLog.Load(path);
            T.Check(all.Count == 2 && all[0].App == "RaceGame" && all[1].CpuTempMax == 81.5f, "新しい順に読む・切れた行は飛ばす");
            T.Check(!File.ReadAllText(path).Contains("GpuTempMax"), "ない値は書かない");
            T.Check(GameLog.Rewrite(s => !GameLog.Same(s, a), path) == 1 && GameLog.Load(path).Single().App == "RaceGame", "1 件消す");
            T.Check(GameLog.Load(Path.Combine(dir, "none.jsonl")).Count == 0, "ファイルがなければ空");
            T.Check(!GameLog.WorthKeeping(new GameSession { App = "X", PlayedSeconds = 30, Frames = 10 }) && GameLog.WorthKeeping(a), "短すぎる記録は残さない");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}

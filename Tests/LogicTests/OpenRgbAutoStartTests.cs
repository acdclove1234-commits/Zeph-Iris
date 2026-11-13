// OpenRGB の自動起動（OpenRgbAutoStart）。実際には起動しない（判断と時間の決まりだけを確かめる）
using ProFanController;

namespace LogicTests;

public static class OpenRgbAutoStartTests
{
    private const string Exe = @"C:\Program Files\OpenRGB\OpenRGB.exe";
    private static OpenRgbAutoStart.Facts F(bool service = false, bool disabled = false, bool running = false, string? exe = Exe) =>
        new(service, disabled, running, exe);

    public static void TestDecide()
    {
        var A = OpenRgbAutoStart.Action.None;
        T.Check(OpenRgbAutoStart.Decide(F(running: true)) == A, "動いていれば何もしない");
        T.Check(OpenRgbAutoStart.Decide(F(service: true, running: true)) == A, "サービスが動いていれば何もしない");
        T.Check(OpenRgbAutoStart.Decide(F(service: true)) == OpenRgbAutoStart.Action.StartService, "止まっているサービスは起動する");
        T.Check(OpenRgbAutoStart.Decide(F(service: true, exe: null)) == OpenRgbAutoStart.Action.StartService, "サービスは exe が見つからなくても起動する");
        T.Check(OpenRgbAutoStart.Decide(F(service: true, disabled: true)) == OpenRgbAutoStart.Action.StartExe, "無効のサービスには触らず exe を起動する");
        T.Check(OpenRgbAutoStart.Decide(F(service: true, disabled: true, exe: null)) == A, "無効のサービスで exe もなければ何もしない");
        T.Check(OpenRgbAutoStart.Decide(F()) == OpenRgbAutoStart.Action.StartExe, "サービスがなければ exe を起動する");
        T.Check(OpenRgbAutoStart.Decide(F(exe: null)) == A, "exe が見つからなければ何もしない");
    }

    public static void TestIsLocal()
    {
        foreach (var h in new[] { "127.0.0.1", "localhost", "LOCALHOST", "::1", "[::1]", " 127.0.0.1 ", "", Environment.MachineName })
            T.Check(OpenRgbAutoStart.IsLocal(h), $"同じ PC: \"{h}\"");
        foreach (var h in new[] { "192.168.1.20", "other-pc", "10.0.0.5" })
            T.Check(!OpenRgbAutoStart.IsLocal(h), $"ほかの PC: \"{h}\"");
    }

    public static void TestTiming()
    {
        var a = new OpenRgbAutoStart();
        double up = 3600;
        T.Check(!a.ShouldTry(100, up), "つながらなくなった直後は起動しない");
        T.Check(!a.ShouldTry(100 + OpenRgbAutoStart.FailingBeforeSec - 1, up), "少し待つ");
        T.Check(a.ShouldTry(100 + OpenRgbAutoStart.FailingBeforeSec, up), "つながらない状態が続いたら起動する");

        var b = new OpenRgbAutoStart();
        b.ShouldTry(0, 10);
        T.Check(!b.ShouldTry(30, 30), "Windows の起動直後は、OpenRGB 自身の自動起動を待つ");
        T.Check(b.ShouldTry(70, OpenRgbAutoStart.MinUptimeSec + 10), "起動から時間がたてば起動する");

        // 試したあとは、しばらく待つ。3 回までで止める
        var c = new OpenRgbAutoStart();
        double t = 0;
        c.ShouldTry(t, up);
        t += OpenRgbAutoStart.FailingBeforeSec;
        for (int i = 0; i < OpenRgbAutoStart.MaxAttempts; i++)
        {
            T.Check(c.ShouldTry(t, up), $"{i + 1} 回目は試す");
            c.MarkAttempt(t);
            T.Check(!c.ShouldTry(t + 10, up), $"{i + 1} 回目のすぐあとは試さない");
            t += OpenRgbAutoStart.RetryGapSec;
        }
        T.Check(!c.ShouldTry(t + 1000, up), "決まった回数を試したら、もう起動しない");

        // 「接続テスト」から試したぶんは数えない
        var d = new OpenRgbAutoStart();
        d.ShouldTry(0, up);
        for (int i = 0; i < 5; i++) d.MarkAttempt(0, count: false);
        T.Check(d.ShouldTry(OpenRgbAutoStart.RetryGapSec, up), "接続テストから試した回数は数えない");
    }

    public static void TestCountResetsAfterHealthySession()
    {
        var a = new OpenRgbAutoStart();
        double up = 3600;
        for (int i = 0; i < OpenRgbAutoStart.MaxAttempts; i++) a.MarkAttempt(0);
        a.ShouldTry(0, up);
        T.Check(!a.ShouldTry(10_000, up), "回数を使い切った");

        // すぐ切れた接続では数え直さない
        a.Connected(10_000);
        a.Disconnected(10_005);
        a.ShouldTry(10_005, up);
        T.Check(!a.ShouldTry(10_200, up), "すぐ切れた接続では数え直さない");

        // 長くつながっていたら数え直す（あとで OpenRGB が落ちたときも起動できる）
        a.Connected(20_000);
        a.Disconnected(20_000 + OpenRgbAutoStart.HealthySec);
        a.ShouldTry(20_000 + OpenRgbAutoStart.HealthySec, up);
        T.Check(a.ShouldTry(20_000 + OpenRgbAutoStart.HealthySec + OpenRgbAutoStart.FailingBeforeSec, up), "長くつながっていたら数え直す");

        // つながったら、つながらない時間を数え直す
        var b = new OpenRgbAutoStart();
        b.ShouldTry(0, up);
        b.Connected(5);
        b.Disconnected(6);
        T.Check(!b.ShouldTry(9, up), "つながったら、つながらない時間を数え直す");
    }

    public static void TestOnlyProtectedExe()
    {
        var roots = new[] { @"C:\Program Files", @"C:\Program Files (x86)" };
        T.Check(OpenRgbAutoStart.IsUnder(@"C:\Program Files\OpenRGB\OpenRGB.exe", roots), "Program Files は起動してよい");
        T.Check(OpenRgbAutoStart.IsUnder(@"c:\program files (x86)\OpenRGB\OpenRGB.exe", roots), "大文字・小文字は区別しない");
        T.Check(!OpenRgbAutoStart.IsUnder(@"C:\Program Files Evil\OpenRGB.exe", roots), "名前が似ているだけのフォルダーは不可");
        T.Check(!OpenRgbAutoStart.IsUnder(@"C:\Program Files\..\Temp\OpenRGB.exe", roots), "「..」で外に出るパスは不可");
        T.Check(!OpenRgbAutoStart.IsUnder(@"D:\Tools\OpenRGB\OpenRGB.exe", roots), "zip を展開したフォルダーは不可");
        T.Check(!OpenRgbAutoStart.IsUnder(@"C:\Program Files", roots), "フォルダーそのものは不可");

        T.Check(OpenRgbAutoStart.ExeFromCommand("\"C:\\Program Files\\OpenRGB\\OpenRGB.exe\"") == Exe, "引用符つきのサービスのコマンド");
        T.Check(OpenRgbAutoStart.ExeFromCommand("\"C:\\Program Files\\OpenRGB\\OpenRGB.exe\" --service") == Exe, "引数つき");
        T.Check(OpenRgbAutoStart.ExeFromCommand(@"C:\OpenRGB\OpenRGB.exe --x") == @"C:\OpenRGB\OpenRGB.exe", "引用符なし");
        T.Check(OpenRgbAutoStart.ExeFromCommand("\"broken") is null, "閉じていない引用符");
    }

    public static void TestOffOrRemoteDoesNothing()
    {
        var a = new OpenRgbAutoStart();
        var off = new RgbConfig { AutoStartOpenRgb = false };
        T.Check(a.OnFailure(off, 1000, force: true) is null, "オフなら起動しない");
        var remote = new RgbConfig { Host = "192.168.1.20" };
        T.Check(a.OnFailure(remote, 1000, force: true) is null, "ほかの PC の OpenRGB は起動しない");
    }

    public static void TestConfigDefaultAndRoundTrip()
    {
        T.Check(new RgbConfig().AutoStartOpenRgb, "初期値はオン");
        var cfg = new AppConfig();
        T.Check(AppConfig.FromJson("{}").Rgb.AutoStartOpenRgb, "前の版の設定（項目なし）でもオン");
        cfg.Rgb.AutoStartOpenRgb = false;
        T.Check(!AppConfig.FromJson(cfg.ToJson()).Rgb.AutoStartOpenRgb, "オフが保存される");
    }
}

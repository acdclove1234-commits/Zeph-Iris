// ==================================================
// WatchdogTests.cs - 見張りプロセスが、本体が落ちたあとの後片付けの途中で失敗しても、
//                    本体の起動し直しまで進むことを確かめるロジックテスト
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class WatchdogTests
{
    private sealed class Calls
    {
        public int Restarts, Shown;
        public int LastRecent = -1;
    }

    private static bool Run(Calls c, Action? applySafe = null, Func<AppConfig>? loadConfig = null,
                            Func<int>? recordCrash = null, Action? restart = null, Action<AppConfig, int>? showStopped = null)
        => Watchdog.Recover(
            applySafe ?? (() => { }),
            loadConfig ?? (() => new AppConfig()),
            recordCrash ?? (() => 1),
            restart ?? (() => c.Restarts++),
            showStopped ?? ((_, recent) => { c.Shown++; c.LastRecent = recent; }),
            _ => { });   // テストでは、起動し直すまで待たない

    public static void TestWatchdogRecoverAlwaysTriesRestart()
    {
        var ok = new Calls();
        T.Check(Run(ok) && ok.Restarts == 1 && ok.Shown == 0, "normal crash: the app is restarted");

        // 2026-11 に実際に起きた形: exe が差し替わって、安全な回転数の部品も設定も読めない
        var safeFails = new Calls();
        bool restarted = Run(safeFails, applySafe: () => throw new FileNotFoundException("System.Text.RegularExpressions"));
        T.Check(restarted && safeFails.Restarts == 1, "safe duties fail: the app is still restarted");

        var configFails = new Calls();
        restarted = Run(configFails, loadConfig: () => throw new FileNotFoundException("System.Text.Json"));
        T.Check(restarted && configFails.Restarts == 1, "config cannot be read: restart with the default setting (on)");

        var recordFails = new Calls();
        restarted = Run(recordFails, recordCrash: () => throw new IOException("locked"));
        T.Check(restarted && recordFails.Restarts == 1, "crash history cannot be written: still restarted");

        var allFail = new Calls();
        restarted = Run(allFail,
            applySafe: () => throw new FileNotFoundException("a"),
            loadConfig: () => throw new FileNotFoundException("b"),
            recordCrash: () => throw new IOException("c"));
        T.Check(restarted && allFail.Restarts == 1, "every step before the restart fails: still restarted");

        bool threw = false;
        try { restarted = Run(new Calls(), restart: () => throw new InvalidOperationException("cannot start")); }
        catch { threw = true; }
        T.Check(!threw && !restarted, "restart itself fails: no exception escapes, returns false");
    }

    public static void TestWatchdogRecoverStopsWhenItShould()
    {
        var off = new Calls();
        var noRestart = new AppConfig();
        noRestart.Safety.WatchdogRestart = false;
        bool restarted = Run(off, loadConfig: () => noRestart);
        T.Check(!restarted && off.Restarts == 0 && off.Shown == 1, "restart turned off: the notice is shown instead");

        var many = new Calls();
        restarted = Run(many, recordCrash: () => 4);
        T.Check(!restarted && many.Restarts == 0 && many.Shown == 1 && many.LastRecent == 4,
            "4 crashes in 10 minutes: stops restarting and shows the notice");

        bool threw = false;
        try { Run(new Calls(), loadConfig: () => noRestart, showStopped: (_, _) => throw new InvalidOperationException("no UI")); }
        catch { threw = true; }
        T.Check(!threw, "the notice fails: no exception escapes");

        var skip = new Calls();
        restarted = Watchdog.Recover(null, () => new AppConfig(), () => 1, () => skip.Restarts++, (_, _) => skip.Shown++);
        T.Check(restarted && skip.Restarts == 1, "no safe-duty step (already applied): still restarted");
    }

    public static void TestWatchdogReadConfigNeverThrows()
    {
        var good = new AppConfig();
        good.Safety.WatchdogSafeDuty = 85;
        string json = good.ToJson();

        var cfg = Watchdog.ReadConfig(() => json, null, out bool fresh);
        T.Check(fresh && Math.Abs(cfg.Safety.WatchdogSafeDuty - 85) < 0.01, "readable config: read fresh");

        var last = new AppConfig();
        last.Safety.WatchdogSafeDuty = 90;
        cfg = Watchdog.ReadConfig(() => throw new FileNotFoundException("System.Text.Json"), last, out fresh);
        T.Check(!fresh && ReferenceEquals(cfg, last), "read fails: the config read earlier is used");

        cfg = Watchdog.ReadConfig(() => "{ not json", last, out fresh);
        T.Check(!fresh && ReferenceEquals(cfg, last), "broken file: the config read earlier is used");

        bool threw = false;
        try { cfg = Watchdog.ReadConfig(() => throw new IOException("gone"), null, out fresh); }
        catch { threw = true; }
        T.Check(!threw && !fresh && cfg is not null && cfg.Safety.WatchdogRestart, "nothing read before: default config, restart on");
    }
}

// 更新した版が起動しなかったときの、外からの戻し方（UpdateRecovery）。ファイルは一時フォルダーの作り物
using ProFanController;

namespace LogicTests;

public static class UpdateRecoveryTests
{
    private static readonly Version From = new(6, 15, 0), To = new(6, 16, 0);
    private static readonly UpdatePending P = new("6.15.0", "6.16.0", new DateTime(2026, 11, 13, 21, 0, 0));

    public static void TestDecide()
    {
        var m = TimeSpan.FromMinutes;
        T.Check(UpdateRecovery.Decide(P, To, okMarked: true, appRunning: true, m(1)) == GuardStep.Done, "new version reported control → done");
        T.Check(UpdateRecovery.Decide(P, To, false, true, m(3)) == GuardStep.Wait, "new version installed, still starting → wait");
        T.Check(UpdateRecovery.Decide(P, To, false, true, UpdateRecovery.GuardTimeout) == GuardStep.Restore, "new version never started control in time → restore");
        T.Check(UpdateRecovery.Decide(P, To, false, false, UpdateRecovery.GuardTimeout) == GuardStep.Restore, "new version not even running → restore");
        T.Check(UpdateRecovery.Decide(P, From, false, true, m(1)) == GuardStep.Wait, "old exe still there early on (installer not done) → wait");
        T.Check(UpdateRecovery.Decide(P, From, false, true, UpdateRecovery.NoInstallTimeout) == GuardStep.Nothing, "install did not happen, old version running → nothing");
        T.Check(UpdateRecovery.Decide(P, From, false, false, UpdateRecovery.NoInstallTimeout) == GuardStep.StartOld, "install did not happen and nothing runs → start the old version");
        T.Check(UpdateRecovery.Decide(P, null, false, false, m(2)) == GuardStep.Wait && UpdateRecovery.Decide(P, null, false, false, UpdateRecovery.GuardTimeout) == GuardStep.Restore,
            "exe unreadable (being replaced) → wait, then restore");
    }

    public static void TestPrepareMarkRestore()
    {
        string app = Path.Combine(Path.GetTempPath(), "zi-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(app);
        try
        {
            string exe = UpdateRecovery.AppExe(app);
            File.WriteAllText(exe, "old exe");
            File.WriteAllText(Path.Combine(app, "config.json"), "{\"old\":true}");

            string guard = UpdateRecovery.Prepare(app, exe, From, To, P.Started);
            T.Check(Path.GetFileName(guard) == UpdateRecovery.RecoveryExe && File.ReadAllText(guard) == "old exe", "old exe kept as ZephIrisRecovery.exe (not counted as Zeph-Iris.exe)");
            T.Check(UpdateRecovery.ReadPending(app) is { From: "6.15.0", To: "6.16.0" }, "pending update recorded");
            T.Check(File.ReadAllText(Path.Combine(UpdateRecovery.BackupDir(app), "config.json")) == "{\"old\":true}", "settings before the update kept");

            // 新しい版が入り、設定を書き換えた
            File.WriteAllText(exe, "new exe");
            File.WriteAllText(Path.Combine(app, "config.json"), "{\"new\":true}");
            T.Check(UpdateRecovery.MarkStarted(app, From) && !UpdateRecovery.IsMarkedOk(app, UpdateRecovery.ReadPending(app)!), "another version writes nothing (and need not retry)");
            T.Check(!UpdateRecovery.IsMarkedOk(app, UpdateRecovery.ReadPending(app)!), "not marked yet");
            T.Check(UpdateRecovery.MarkStarted(app, To) && UpdateRecovery.IsMarkedOk(app, UpdateRecovery.ReadPending(app)!), "new version reports control");
            T.Check(UpdateRecovery.MarkStarted(app, To) && UpdateRecovery.IsMarkedOk(app, UpdateRecovery.ReadPending(app)!), "already marked → done (no rewrite needed)");

            // 見守りが戻す
            var res = UpdateRecovery.Restore(app, "test", P.Started.AddMinutes(9));
            T.Check(res is { ExeRestored: true, Problem: null }, "restore succeeds");
            string kept = Path.Combine(app, "backups", $"config_{P.Started.AddMinutes(9):yyyyMMdd_HHmmss}_restore.json");
            T.Check(File.Exists(kept) && File.ReadAllText(kept) == "{\"new\":true}", "the settings from the new version are kept in backups before restoring (listed as 復元前)");
            T.Check(File.ReadAllText(exe) == "old exe" && File.ReadAllText(Path.Combine(app, "config.json")) == "{\"old\":true}", "old exe and old settings are back");
            T.Check(UpdateRecovery.ReadPending(app) is null, "pending record removed");
            var rb = UpdateRecovery.TakeRolledBack(app);
            T.Check(rb is { Reason: "test", Manual: false } && rb.SettingsBackup == kept && UpdateRecovery.TakeRolledBack(app) is null, "rolled-back note read once by the restored version (with where the settings were kept)");
            T.Check(File.Exists(guard), "the kept copy stays for a manual restore");

            // 次の更新の準備で、前の印は消える
            UpdateRecovery.Prepare(app, exe, From, To, P.Started.AddDays(1));
            T.Check(!UpdateRecovery.IsMarkedOk(app, UpdateRecovery.ReadPending(app)!), "a new update starts without the old ok mark");

            Directory.Delete(UpdateRecovery.BackupDir(app), true);
            T.Check(UpdateRecovery.Restore(app, "x", DateTime.Now) is { ExeRestored: false, Problem: not null }, "no kept copy → cannot restore (says why)");
            T.Check(UpdateRecovery.ReadPending(app) is null && UpdateRecovery.MarkStarted(app, To), "no pending update → nothing to report (done)");
        }
        finally
        {
            Directory.Delete(app, true);
        }
    }
}

public static class UpdateRecoveryInstallerTests
{
    public static void TestWaitsWhileInstallerRuns()
    {
        var p = new UpdatePending("6.15.0", "6.16.0", DateTime.Now);
        var old = new Version(6, 15, 0);
        T.Check(UpdateRecovery.Decide(p, old, false, false, UpdateRecovery.NoInstallTimeout, installerRunning: true) == GuardStep.Wait,
            "a slow installer is still running → do not start the old version in the middle of the install");
        T.Check(UpdateRecovery.Decide(p, new Version(6, 16, 0), false, false, UpdateRecovery.GuardTimeout, installerRunning: true) == GuardStep.Wait,
            "installer still running → do not restore yet");
        T.Check(UpdateRecovery.Decide(p, new Version(6, 16, 0), false, false, UpdateRecovery.InstallerMaxWait, installerRunning: true) == GuardStep.Restore,
            "but not forever");
        T.Check(UpdateRecovery.Decide(p, new Version(6, 16, 0), true, true, TimeSpan.Zero, installerRunning: true) == GuardStep.Done, "ok mark wins");
    }
}

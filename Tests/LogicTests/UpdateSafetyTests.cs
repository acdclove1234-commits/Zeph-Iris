// 更新の守り（外部レビューの指摘で直したところ）: 成功の決め方・前の更新の残り・新しい版が入ってから待つ時間・見張りの再起動の間隔
using ProFanController;

namespace LogicTests;

public static class UpdateSafetyTests
{
    /// <summary>続けて ConfirmAfter のあいだ制御していたら成功。途切れたら数え直す。書けなければやり直す</summary>
    public static void TestUpdateConfirmNeedsSustainedControl()
    {
        var c = new UpdateConfirm();
        long t = 1_000_000;
        int tries = 0;
        for (int s = 0; s < 89; s++, t += 1000) if (c.ShouldTry(t)) tries++;
        T.Check(tries == 0, "not confirmed before 90 s of control");
        t += 1000;
        T.Check(c.ShouldTry(t), "confirmed after 90 s of uninterrupted snapshots (Ok or Degraded both count)");

        var gap = new UpdateConfirm();
        t = 0;
        for (int s = 0; s < 60; s++, t += 1000) gap.ShouldTry(t);
        t += 15_000;   // エンジンが 15 秒止まった（開き直し・スリープ）
        bool early = false;
        for (int s = 0; s < 80; s++, t += 1000) early |= gap.ShouldTry(t);
        T.Check(!early, "a gap of more than 10 s restarts the count");

        var retry = new UpdateConfirm { Required = TimeSpan.FromSeconds(5) };
        t = 0;
        int attempts = 0;
        for (int s = 0; s < 40; s++, t += 1000)
        {
            if (!retry.ShouldTry(t)) continue;
            attempts++;
            retry.Result(attempts >= 2);   // 1 回目は書けなかった
        }
        T.Check(attempts == 2 && retry.Done, "a failed write is retried (every 10 s) until it works, then stops");
    }

    /// <summary>インストールが長引いても、新しい版が入ってから 4 分は待つ</summary>
    public static void TestRestoreWaitsAfterNewVersionAppears()
    {
        var p = new UpdatePending("6.15.0", "6.16.0", DateTime.Now);
        var v = new Version(6, 16, 0);
        T.Check(UpdateRecovery.Decide(p, v, false, true, TimeSpan.FromMinutes(9), sinceNewVersion: TimeSpan.FromMinutes(1)) == GuardStep.Wait,
            "the new exe appeared only 1 minute ago → wait even though 9 minutes passed since the guard started");
        T.Check(UpdateRecovery.Decide(p, v, false, true, TimeSpan.FromMinutes(9), sinceNewVersion: TimeSpan.FromMinutes(4)) == GuardStep.Restore,
            "4 minutes after the new exe appeared → restore");
        T.Check(UpdateRecovery.Decide(p, v, false, true, TimeSpan.FromMinutes(8)) == GuardStep.Restore, "unknown appearance time → old rule");
    }

    /// <summary>前の更新の残り（設定）を、新しい控えと組にしない。いまの設定を取っておけないときは、設定を上書きしない</summary>
    public static void TestPrepareAndRestoreKeepSettingsSafe()
    {
        string app = Path.Combine(Path.GetTempPath(), "zi-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(app);
        try
        {
            string exe = UpdateRecovery.AppExe(app);
            File.WriteAllText(exe, "v1");
            string dir = UpdateRecovery.BackupDir(app);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), "{\"stale\":true}");   // 前の更新の残り
            UpdateRecovery.Prepare(app, exe, new Version(6, 15, 0), new Version(6, 16, 0), DateTime.Now);
            T.Check(!File.Exists(Path.Combine(dir, "config.json")), "no config now → the stale copy from an earlier update is removed, not paired with the new exe");

            // backups が書けない（同じ名前のファイルがある）→ いまの設定は上書きしない
            File.WriteAllText(Path.Combine(app, "config.json"), "{\"new\":true}");
            File.WriteAllText(Path.Combine(dir, "config.json"), "{\"old\":true}");
            File.WriteAllText(Path.Combine(app, "backups"), "not a folder");
            var r = UpdateRecovery.Restore(app, "test", DateTime.Now, manual: true);
            T.Check(r.ExeRestored && File.ReadAllText(Path.Combine(app, "config.json")) == "{\"new\":true}",
                "when the current settings cannot be kept, they are not overwritten (the old exe can read them)");
            T.Check(UpdateRecovery.TakeRolledBack(app) is { Manual: true, SettingsBackup: null }, "the note says it was a manual restore without a settings backup");
        }
        finally
        {
            Directory.Delete(app, true);
        }
    }

    /// <summary>見張り: 続けて落ちたら、起動し直すまで待つ（ドライバーの入れ替えなどが終わるのを待つ）</summary>
    public static void TestWatchdogBacksOffRestarts()
    {
        T.Check(Watchdog.RestartDelay(1) == TimeSpan.Zero && Watchdog.RestartDelay(2) == TimeSpan.FromSeconds(30) && Watchdog.RestartDelay(3) == TimeSpan.FromSeconds(60),
            "first restart at once, then 30 s, then 60 s");
        var waited = new List<TimeSpan>();
        int restarts = 0;
        Watchdog.Recover(() => { }, () => new AppConfig(), () => 3, () => restarts++, (_, _) => { }, waited.Add);
        T.Check(restarts == 1 && waited.SequenceEqual(new[] { TimeSpan.FromSeconds(60) }), "the 3rd crash waits 60 s, then restarts");
        waited.Clear();
        Watchdog.Recover(() => { }, () => new AppConfig(), () => 4, () => restarts++, (_, _) => { }, waited.Add);
        T.Check(restarts == 1 && waited.Count == 0, "the 4th crash in 10 minutes stops restarting (no wait)");
    }
}

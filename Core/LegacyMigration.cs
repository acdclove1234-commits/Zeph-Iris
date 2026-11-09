// ==================================================
// LegacyMigration.cs - 前の名前（ProFanController、6.6.1 まで）の版からの引き継ぎ
//   1. 前の版が動いていたら、正しく終了してもらう（2 つのアプリが同じファンを制御しないように。終了するときに設定も書き出される）
//   2. ここに設定がなく、前の版のフォルダー（C:\Tools\ProFanController）に設定があれば、設定と記録を写す
//   3. 前の名前で Windows に登録したもの（自動起動のタスク・ファイアウォールの許可・クラッシュの記録の設定）を消し、
//      自動起動を使っていたなら、新しい名前で登録し直す
//   前の版のフォルダーは消さない（中身を確かめてから、利用者が消す）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using Microsoft.Win32;

namespace ProFanController;

public static class LegacyMigration
{
    private const string ConfigFile = "config.json";

    /// <summary>前の版の、クラッシュしたときの記録の設定（HKLM の下）</summary>
    private static string LegacyCrashDumpKey => InstallerCleanup.CrashDumpKey(AppIdentity.LegacyExeName);

    /// <summary>
    /// 前の版が動いていたら、正しく終了するよう合図して、終わるまで待つ（ファンは BIOS 制御に戻ってから終わる）。
    /// 動いていなかった・時間内に終わったら true、まだ動いていたら false
    /// </summary>
    public static bool StopLegacyApp(TimeSpan timeout)
    {
        if (!LegacyAppRunning()) return true;
        Logger.Info($"前の版（{AppIdentity.LegacyName}）が動いているので、正しく終了するよう合図します");
        try
        {
            if (EventWaitHandle.TryOpenExisting(AppIdentity.LegacyExitSignal, out var exit))
                using (exit) exit.Set();
        }
        catch (Exception ex)
        {
            Logger.Warn($"前の版に終了の合図を送れませんでした: {ex.Message}");
        }
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(250);
            if (!LegacyAppRunning())
            {
                Logger.Info("前の版が終了しました");
                return true;
            }
        }
        Logger.Warn($"前の版が {timeout.TotalSeconds:F0} 秒たっても終了しません");
        return false;
    }

    private static bool LegacyAppRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(AppIdentity.LegacySingleInstance, out var m)) return false;
            m.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; } // 開けないが、ある
        catch (Exception) { return false; }
    }

    /// <summary>
    /// ここ（appDirectory）に設定がなく、前の版のフォルダーに設定があれば、設定と記録を写す。写した元のフォルダーを返す（写さなければ null）
    /// </summary>
    public static string? CopySettingsIfNeeded(string appDirectory, string legacyDirectory = AppIdentity.LegacyFolder)
    {
        string here = Path.GetFullPath(appDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string legacy = Path.GetFullPath(legacyDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(here, legacy, StringComparison.OrdinalIgnoreCase)) return null;
        if (File.Exists(Path.Combine(here, ConfigFile)) || !File.Exists(Path.Combine(legacy, ConfigFile))) return null;
        InstallCheck.CopySettings(legacy, here);
        return legacy;
    }

    /// <summary>
    /// 前の名前で Windows に登録したものを消す。自動起動を前の名前で使っていて、新しい名前でまだ登録していなければ、
    /// registerAutoStart（ふだんは AutoStart.Enable）で登録し直す。消したもの・登録し直したかを log に渡す（ふだんはログに書く）
    /// </summary>
    public static void RemoveLegacyRegistrations(Func<bool> registerAutoStart, Action<string>? log = null)
    {
        var write = log ?? Logger.Info;
        void Log(string message) => write(message); // ログの文（訳さない）
        try
        {
            if (AutoStart.LegacyTaskExists())
            {
                bool removed = AutoStart.RemoveLegacyTask();
                bool registered = !AutoStart.IsEnabled() && registerAutoStart();
                Log($"前の版の自動起動（タスク「{AppIdentity.LegacyName}」）を{(removed ? "消しました" : "消せませんでした")}"
                    + (registered ? $"。新しい名前（{AppIdentity.Name}）で登録し直しました" : ""));
            }
        }
        catch (Exception ex) { Log($"前の版の自動起動を片付けられません: {ex.Message}"); }

        try
        {
            if (WebServer.LegacyFirewallRuleExists())
            {
                WebServer.RemoveLegacyFirewall();
                Log("前の版のファイアウォールの許可を消しました（スマホ監視を使うときは、新しい名前で作り直します）");
            }
        }
        catch (Exception ex) { Log($"前の版のファイアウォールの許可を片付けられません: {ex.Message}"); }

        try
        {
            bool exists;
            using (var key = Registry.LocalMachine.OpenSubKey(LegacyCrashDumpKey)) exists = key is not null;
            if (exists)
            {
                Registry.LocalMachine.DeleteSubKeyTree(LegacyCrashDumpKey, throwOnMissingSubKey: false);
                Log("前の版のクラッシュの記録の設定を消しました");
            }
        }
        catch (Exception ex) { Log($"前の版のクラッシュの記録の設定を片付けられません: {ex.Message}"); }
    }
}

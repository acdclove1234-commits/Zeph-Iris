// ==================================================
// LegacyMigration.cs - 前の名前の版からの引き継ぎ (Fanlume〈6.7.0〜6.7.3〉・ProFanController〈6.6.1 まで〉)
//   1. 前の版が動いていたら、正しく終了してもらう（2 つのアプリが同じファンを制御しないように。終了するときに設定も書き出される）
//   2. ここに設定がなく、前の版のフォルダーに設定があれば、新しい版のものから順に探して、設定と記録を写す
//   3. 前の名前で Windows に登録したもの（自動起動のタスク・ファイアウォールの許可・クラッシュの記録の設定）を消し、
//      自動起動を使っていたなら、新しい名前で登録し直す
//   4. インストーラーで入れていた前の版（Fanlume）は、設定・記録を残すよう伝えて、その版のアンインストーラーで外す
//      （「インストールされているアプリ」に前の名前が残らないように）
//   前の版のフォルダーの設定・記録は消さない（中身を確かめてから、利用者が消す）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using Microsoft.Win32;

namespace ProFanController;

public static class LegacyMigration
{
    private const string ConfigFile = "config.json";

    /// <summary>
    /// 前の版が動いていたら、正しく終了するよう合図して、終わるまで待つ（ファンは BIOS 制御に戻ってから終わる）。
    /// 動いていなかった・時間内に終わったら true、まだ動いていたら false
    /// </summary>
    public static bool StopLegacyApp(TimeSpan timeout)
    {
        var running = AppIdentity.LegacyApps.Where(Running).ToList();
        if (running.Count == 0) return true;
        foreach (var app in running)
        {
            Logger.Info($"前の版（{app.Name}）が動いているので、正しく終了するよう合図します");
            try
            {
                if (EventWaitHandle.TryOpenExisting(app.ExitSignal, out var exit))
                    using (exit) exit.Set();
            }
            catch (Exception ex)
            {
                Logger.Warn($"前の版（{app.Name}）に終了の合図を送れませんでした: {ex.Message}");
            }
        }
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(250);
            if (!running.Any(Running))
            {
                Logger.Info("前の版が終了しました");
                return true;
            }
        }
        Logger.Warn($"前の版が {timeout.TotalSeconds:F0} 秒たっても終了しません");
        return false;
    }

    private static bool Running(LegacyApp app)
    {
        try
        {
            if (!Mutex.TryOpenExisting(app.SingleInstance, out var m)) return false;
            m.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; } // 開けないが、ある
        catch (Exception) { return false; }
    }

    /// <summary>
    /// ここ（appDirectory）に設定がなく、前の版のフォルダー（legacyDirectories の順に探す）に設定があれば、設定と記録を写す。
    /// 写した元のフォルダーを返す（写さなければ null）
    /// </summary>
    public static string? CopySettingsIfNeeded(string appDirectory, IEnumerable<string>? legacyDirectories = null)
    {
        string here = Path.GetFullPath(appDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(here, ConfigFile))) return null;
        foreach (var dir in legacyDirectories ?? AppIdentity.LegacyApps.Select(a => a.Folder))
        {
            string legacy = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(here, legacy, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(legacy, ConfigFile))) continue;
            InstallCheck.CopySettings(legacy, here);
            return legacy;
        }
        return null;
    }

    /// <summary>
    /// 前の名前で Windows に登録したものを消す。自動起動を前の名前で使っていて、新しい名前でまだ登録していなければ、
    /// registerAutoStart（ふだんは AutoStart.Enable）で登録し直す。消したもの・登録し直したかを log に渡す（ふだんはログに書く）
    /// </summary>
    public static void RemoveLegacyRegistrations(Func<bool> registerAutoStart, Action<string>? log = null)
    {
        var write = log ?? Logger.Info;
        void Log(string message) => write(message); // ログの文（訳さない）
        foreach (var app in AppIdentity.LegacyApps)
        {
            try
            {
                if (AutoStart.TaskExists(app.TaskName))
                {
                    bool removed = AutoStart.RemoveTask(app.TaskName);
                    bool registered = !AutoStart.IsEnabled() && registerAutoStart();
                    Log($"前の版の自動起動（タスク「{app.TaskName}」）を{(removed ? "消しました" : "消せませんでした")}"
                        + (registered ? $"。新しい名前（{AppIdentity.Name}）で登録し直しました" : ""));
                }
            }
            catch (Exception ex) { Log($"前の版（{app.Name}）の自動起動を片付けられません: {ex.Message}"); }

            try
            {
                if (WebServer.FirewallRuleExists(app.FirewallRule))
                {
                    WebServer.RemoveFirewallRule(app.FirewallRule);
                    Log($"前の版のファイアウォールの許可（{app.FirewallRule}）を消しました（スマホ監視を使うときは、新しい名前で作り直します）");
                }
            }
            catch (Exception ex) { Log($"前の版（{app.Name}）のファイアウォールの許可を片付けられません: {ex.Message}"); }

            try
            {
                string key = InstallerCleanup.CrashDumpKey(app.ExeName);
                bool exists;
                using (var k = Registry.LocalMachine.OpenSubKey(key)) exists = k is not null;
                if (exists)
                {
                    Registry.LocalMachine.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
                    Log($"前の版（{app.Name}）のクラッシュの記録の設定を消しました");
                }
            }
            catch (Exception ex) { Log($"前の版（{app.Name}）のクラッシュの記録の設定を片付けられません: {ex.Message}"); }
        }
    }

    /// <summary>
    /// インストーラーで入れていた前の版（Fanlume）のアンインストーラーを、設定・ログ・記録を残すよう伝えて実行する
    /// （画面は出さない。前の版のファイルと「インストールされているアプリ」の項目が消える）。実行したら true
    /// </summary>
    public static bool UninstallLegacyInstall(string appDirectory)
    {
        string here = Path.GetFullPath(appDirectory).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var app in AppIdentity.LegacyApps.Where(a => a.InstalledWithInstaller))
        {
            string folder = Path.GetFullPath(app.Folder).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(here, folder, StringComparison.OrdinalIgnoreCase)) continue;
            if (InstallerCleanup.UninstallerPath(folder) is not { } uninstaller) continue;
            try
            {
                Process.Start(new ProcessStartInfo(uninstaller, AppIdentity.FanlumeUninstallKeepData)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Environment.SystemDirectory,
                })?.Dispose();
                Logger.Info($"前の版（{app.Name}）を、設定・記録を残してアンインストールしています: {uninstaller}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"前の版（{app.Name}）のアンインストーラーを起動できません: {ex.Message}");
            }
        }
        return false;
    }
}

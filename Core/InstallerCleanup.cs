// ==================================================
// InstallerCleanup.cs - インストーラー（Inno Setup）で入れたときの、アンインストールの後片付け
//   アンインストーラーは、アプリを正しく終了させて（ファンは BIOS 制御に戻る）から「Zeph-Iris.exe --uninstall-cleanup」を呼ぶ。
//   ここでは、このアプリが Windows に登録したもの（自動起動のタスク・ファイアウォールの許可・クラッシュの記録の設定。前の名前のものも）を消し、
//   選んだときだけ設定・ログ・記録を消す。画面の部品（WPF）は読み込まない（Program.Main から呼ぶ）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using Microsoft.Win32;

namespace ProFanController;

public static class InstallerCleanup
{
    /// <summary>アンインストーラーから呼ばれるときの引数</summary>
    public const string Argument = "--uninstall-cleanup";

    /// <summary>設定・ログ・記録も消すときに、後ろに付ける引数</summary>
    public const string DeleteDataArgument = "--delete-data";

    /// <summary>インストーラーがアプリのフォルダーに置くアンインストーラー</summary>
    public const string UninstallerName = "unins000.exe";

    /// <summary>アンインストーラーに、設定・ログ・記録を消すかを伝える引数（/ZEPHIRISDATA=delete または keep。Installer/Zeph-Iris.iss が読む）</summary>
    public static string UninstallerArguments(bool deleteData) => $"/SILENT /SUPPRESSMSGBOXES /NORESTART /ZEPHIRISDATA={(deleteData ? "delete" : "keep")}";

    /// <summary>インストーラーで入れたときの、アンインストーラーの場所（インストーラーで入れていなければ null）</summary>
    public static string? UninstallerPath(string appDirectory)
    {
        string path = Path.Combine(appDirectory, UninstallerName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>クラッシュしたときの記録（ミニダンプ）を残すよう Windows に頼むレジストリのキー（HKLM の下）</summary>
    public static string CrashDumpKey(string exeName) => $@"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\{exeName}";

    /// <summary>
    /// 「--uninstall-cleanup」で起動されたときの後片付け。0 = すべてできた、1 = できなかったものがある（アンインストールは続ける）。
    /// ログは書かない（このあと消すかもしれない logs フォルダーを作り直さないように）
    /// </summary>
    public static int Run(string[] args)
    {
        int failures = 0;
        void Quiet(Action action)
        {
            try { action(); }
            catch { failures++; }
        }

        Quiet(() => { if (AutoStart.IsEnabled() && !AutoStart.Disable()) failures++; });
        Quiet(() => { if (WebServer.FirewallRuleExists()) WebServer.RemoveFirewall(); });
        Quiet(() => Registry.LocalMachine.DeleteSubKeyTree(CrashDumpKey(AppIdentity.ExeName), throwOnMissingSubKey: false));
        Quiet(() => LegacyMigration.RemoveLegacyRegistrations(registerAutoStart: () => false, log: _ => { }));

        if (args.Contains(DeleteDataArgument, StringComparer.OrdinalIgnoreCase))
        {
            Logger.Flush();
            Quiet(() => { if (CleanUninstall.DeleteData(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Count > 0) failures++; });
        }
        return failures == 0 ? 0 : 1;
    }
}

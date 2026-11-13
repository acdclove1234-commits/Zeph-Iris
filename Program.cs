// ==================================================
// Program.cs - アプリの入口 (見張りプロセスは、画面の部品（WPF）を読み込まずに動かす)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Runtime.InteropServices;

namespace ProFanController;

/// <summary>
/// 入口。ふつうは WPF のアプリ（App）を起動する。
/// 「--watchdog 本体のプロセスID」で起動されたときは、App を作らずに見張りだけを動かす
/// （見張りは画面を出さないのに、App を作るとテーマ・フォント・画面の部品・音や LED の部品まで読み込んで、メモリを 150 MB ほど使っていた）
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        int wd = Array.IndexOf(args, Watchdog.Argument);
        if (wd >= 0 && wd + 1 < args.Length && int.TryParse(args[wd + 1], out int parentPid))
        {
            Logger.FileSuffix = "watchdog"; // 本体と同じログファイルに同時に書かないように
            Localization.Init(ConfigStore.ReadLanguage()); // 見張りが出す案内も、設定した言語で
            AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            {
                Logger.Error($"見張りで致命的な例外: {a.ExceptionObject}");
                Logger.Flush();
            };
            Watchdog.ShowMessage = msg => NativeMessageBox.Warn(msg, L("Zeph-Iris 見張り"));
            Watchdog.Run(parentPid);
            Logger.Flush();
            return 0;
        }

        // アンインストーラー（Inno Setup）から: Windows に登録したものを消す（選んだときは設定・ログ・記録も）。画面は出さない
        if (args.Contains(InstallerCleanup.Argument, StringComparer.OrdinalIgnoreCase)) return InstallerCleanup.Run(args);

        // 更新の見守り・前の版に戻す（previous\ZephIrisRecovery.exe として動く。画面の部品は読み込まない）
        if (RecoveryMode(args) is { } recovery) return recovery;

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>
    /// 「--update-guard アプリのフォルダー」: 更新の見守り。
    /// 「--restore-previous」か、ZephIrisRecovery.exe を引数なしで起動した（ダブルクリック）: 確かめてから前の版に戻す（--yes なら確かめない）。
    /// どちらでもなければ null（ふつうに起動する）
    /// </summary>
    private static int? RecoveryMode(string[] args)
    {
        int g = Array.IndexOf(args, UpdateRecovery.GuardArgument);
        bool asRecovery = string.Equals(Path.GetFileName(Environment.ProcessPath), UpdateRecovery.RecoveryExe, StringComparison.OrdinalIgnoreCase);
        bool restore = args.Contains(UpdateRecovery.RestoreArgument, StringComparer.OrdinalIgnoreCase) || (asRecovery && args.Length == 0);
        if (g < 0 && !restore) return null;

        Logger.FileSuffix = "recovery";
        Localization.Init(ConfigStore.ReadLanguage());
        string caption = L("Zeph-Iris の復旧");
        void Tell(string text) => NativeMessageBox.Warn(text, caption);
        try
        {
            if (g >= 0)
            {
                if (g + 1 >= args.Length || !Directory.Exists(args[g + 1])) return 2;
                return UpdateRecovery.RunGuard(args[g + 1], Logger.Info, Tell);
            }
            // 手で戻す: この exe は アプリのフォルダー\previous にあるはず
            string appDir = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!;
            if (!asRecovery || !File.Exists(UpdateRecovery.AppExe(appDir)))
            {
                Tell(L("前の版に戻せるのは、アプリのフォルダーの previous にある ZephIrisRecovery.exe からだけです。"));
                return 2;
            }
            string previous = UpdateRecovery.BackupVersion(appDir)?.ToString(3) ?? "?";
            string now = InstallCheck.FileVersion(UpdateRecovery.AppExe(appDir))?.ToString(3) ?? "?";
            if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase) &&
                !NativeMessageBox.AskYesNo(L($"Zeph-Iris を前の版 {previous} に戻しますか？（いまの版 {now}）\n\n動いている Zeph-Iris を終了し（ファンは BIOS の設定に戻ります）、前の版の exe と、更新する前の設定に戻してから起動します。"), caption))
                return 0;
            return UpdateRecovery.RestoreAndStart(appDir, L("手で前の版に戻しました。"), Logger.Info, Tell);
        }
        catch (Exception ex)
        {
            Logger.Error($"復旧で例外: {ex}");
            Tell(L($"前の版に戻せませんでした（{ex.Message}）。"));
            return 1;
        }
        finally
        {
            Logger.Flush();
        }
    }
}

/// <summary>Windows の標準のメッセージボックス（WPF を読み込まずに出せる）</summary>
internal static class NativeMessageBox
{
    private const uint MbOk = 0x0, MbIconWarning = 0x30, MbSetForeground = 0x10000, MbTopMost = 0x40000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static void Warn(string text, string caption) => MessageBoxW(IntPtr.Zero, text, caption, MbOk | MbIconWarning | MbSetForeground | MbTopMost);

    private const uint MbYesNo = 0x4, MbIconQuestion = 0x20;
    private const int IdYes = 6;

    public static bool AskYesNo(string text, string caption) =>
        MessageBoxW(IntPtr.Zero, text, caption, MbYesNo | MbIconQuestion | MbSetForeground | MbTopMost) == IdYes;
}

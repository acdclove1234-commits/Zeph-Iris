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
            Watchdog.ShowMessage = msg => NativeMessageBox.Warn(msg, L("Fanlume 見張り"));
            Watchdog.Run(parentPid);
            Logger.Flush();
            return 0;
        }

        // アンインストーラー（Inno Setup）から: Windows に登録したものを消す（選んだときは設定・ログ・記録も）。画面は出さない
        if (args.Contains(InstallerCleanup.Argument, StringComparer.OrdinalIgnoreCase)) return InstallerCleanup.Run(args);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

/// <summary>Windows の標準のメッセージボックス（WPF を読み込まずに出せる）</summary>
internal static class NativeMessageBox
{
    private const uint MbOk = 0x0, MbIconWarning = 0x30, MbSetForeground = 0x10000, MbTopMost = 0x40000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static void Warn(string text, string caption) => MessageBoxW(IntPtr.Zero, text, caption, MbOk | MbIconWarning | MbSetForeground | MbTopMost);
}

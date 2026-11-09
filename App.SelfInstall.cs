// ==================================================
// App.SelfInstall.cs - ダウンロードから起動したときの自動のインストール
//   ダウンロード・デスクトップ・一時フォルダー（zip の中から開いたとき）で起動したら、アプリを C:\Program Files\Fanlume に入れて
//   そこから起動し直す。そこに置いたままだと、整理したときに消える・zip の一時フォルダーが消えて自動起動が壊れる・
//   別の版が増える、などで正しく動かなくなるため。入れる先のほうが新しい版なら、そちらを起動するだけ
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;

namespace ProFanController;

public partial class App
{
    /// <summary>自動で入れたあとに起動し直したときの引数（後ろに、元の場所）</summary>
    private const string InstalledFromArgument = "--installed-from";

    /// <summary>
    /// 置き場所が変わりやすいフォルダーで起動したときは、決まった場所に入れて、そこのアプリを起動する（このアプリは終わる）。
    /// 起動したら true。入れられなかったら false（この場所のまま動かし、あとでお知らせに出す）
    /// </summary>
    private bool RelocateFromUnstableFolder(string[] args)
    {
        if (args.Contains(InstalledFromArgument, StringComparer.OrdinalIgnoreCase)) return false; // 入れた先から、また入れ直さない
        string here = AppContext.BaseDirectory;
        // ソースからビルドしたアプリ（bin の下・デバッガーで動かしているとき）は、入れたアプリを置き換えない
        if (Debugger.IsAttached || here.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) return false;
        string? place = InstallCheck.UnstableFolderName(here);
        if (place is null) return false;
        try
        {
            var result = InstallCheck.InstallFrom(here, InstallCheck.RecommendedFolder, typeof(App).Assembly.GetName().Version);
            Logger.Info($"「{place}」から起動したので、{InstallCheck.RecommendedFolder} に入れて起動し直します"
                        + $"（アプリ: {(result.CopiedApp ? "写した" : "入れる先のほうが新しいので写さない")}・設定: {(result.MovedSettings ? "引き継いだ" : "そのまま")}）: {here}");
            bool minimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
            // 新しいアプリは、このアプリが終わる（二重起動の防止の印を離す）まで待ってから始まる（--restart）
            string from = here.TrimEnd(Path.DirectorySeparatorChar); // 最後の \ があると、引数の " が外れない
            Process.Start(new ProcessStartInfo(result.Exe, $"{(minimized ? "--minimized " : "")}{RestartArgument} {InstalledFromArgument} \"{from}\"")
            {
                UseShellExecute = false,
                WorkingDirectory = InstallCheck.RecommendedFolder,
            })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"{InstallCheck.RecommendedFolder} に入れられませんでした。この場所のまま動かします: {ex}");
            return false;
        }
    }

    /// <summary>自動で入れて起動し直したときの、元の場所（そうでなければ null）</summary>
    private static string? InstalledFrom(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals(InstalledFromArgument, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>前の名前（ProFanController）の版から設定を引き継いだあと: 前の版のフォルダーは消してよいことを伝える</summary>
    private static void AfterLegacyMigration(MainWindow window, string from)
    {
        window.ShowAlert(L($"前の版（ProFanController）の設定と記録を {from} から引き継ぎました。前の版のフォルダーはもう使わないので、中身を確かめてから消してかまいません。"),
            AlertLevel.Info, L("フォルダーを開く"), () => SystemTool.Open(from));
    }

    /// <summary>
    /// 自動で入れて起動し直したあと: お知らせを出し、自動起動が元の場所（ダウンロードなど）を指していれば、ここに登録し直す
    /// </summary>
    private void AfterRelocation(MainWindow window, string from)
    {
        window.ShowAlert(L($"{from} から {InstallCheck.RecommendedFolder} に入れて、そこから起動しました。これからはこの場所のアプリが動きます（ダウンロードしたファイルは消してかまいません）。"),
            AlertLevel.Info, L("フォルダーを開く"), () => SystemTool.Open(InstallCheck.RecommendedFolder));
        string exe = Environment.ProcessPath ?? "";
        _ = Task.Run(() =>
        {
            try
            {
                string? registered = AutoStart.RegisteredPath();
                if (exe.Length == 0 || !InstallCheck.PointsElsewhere(registered, exe)) return;
                bool ok = AutoStart.Enable();
                Logger.Info(ok ? $"自動起動を、入れた場所に登録し直しました: {exe}（前: {registered}）" : "自動起動を、入れた場所に登録し直せませんでした");
            }
            catch (Exception ex)
            {
                Logger.Warn($"自動起動の確認に失敗: {ex.Message}");
            }
        });
    }
}

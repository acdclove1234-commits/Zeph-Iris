// ==================================================
// MainWindow.Support.cs - 「不具合を報告」 (報告用の zip を作り、GitHub の報告ページを開く)
//   中身を作るのは Core/SupportBundle.cs。アプリからはどこにも送らない（添付するのは利用者）
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProFanController;

public partial class MainWindow
{
    /// <summary>報告に入れる、このPCの様子を集める</summary>
    private SupportInfo CollectSupportInfo()
    {
        string version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                         ?? AppUpdater.CurrentVersion.ToString(3);
        var catalog = _catalog;
        return new SupportInfo(
            version,
            SupportBundle.WindowsVersion(),
            Localization.Language,
            catalog?.PawnIoInstalled ?? false,
            catalog?.Devices ?? Array.Empty<DeviceInfo>(),
            _rgbKnownDevices,
            catalog?.AllSensors.ToList() ?? new List<SensorInfo>(),
            _lastSnapshot?.Values ?? new Dictionary<string, float?>());
    }

    private void OnReportProblemClick(object sender, RoutedEventArgs e) => ShowReportDialog();

    /// <summary>画面の外（トレイのメニュー）から報告の画面を開く</summary>
    public void ShowReportDialogFromOutside() => ShowReportDialog();

    /// <summary>
    /// 報告の画面: 何が入るかを見せて、zip を作る・GitHub の報告ページを開く。
    /// 報告ページは版・部品の欄を埋めた状態で開くので、あとは「何が起きたか」を書いて zip を添付するだけ
    /// </summary>
    private void ShowReportDialog()
    {
        var info = CollectSupportInfo();
        var sanitizer = SupportSanitizer.ForThisPc(Config);
        var win = new Window
        {
            Title = L("不具合を報告"),
            Owner = this, Width = 760, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("BgBrush"), ShowInTaskbar = false,
        };
        var root = new DockPanel { Margin = new Thickness(18) };
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("SubTextBrush"), Margin = new Thickness(0, 0, 0, 10),
            Text = L("うまく動かないところを、GitHub の報告ページで知らせてください。① 報告用の zip を作る → ② 報告ページを開いて、何が起きたかを書き、zip をドラッグして添付する、の順です。zip には、下のまとめ・センサーの一覧・設定・直近 3 日のログが入ります。ユーザー名・PC の名前・フォルダーの名前・メールアドレス・IP アドレス・スマホ通知の宛先と合言葉は伏せます（部品や音の出力先の機器名は残ります）。アプリからはどこにも送りません。"),
        };
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = (Brush)FindResource("Accent2Brush") };
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var bottom = new StackPanel();
        bottom.Children.Add(status);
        bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        Button Btn(string text, string style)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 7, 14, 7), Style = (Style)FindResource(style) };
            buttons.Children.Add(b);
            return b;
        }
        var make = Btn(L("① 報告用の zip を作る"), "AccentButton");
        make.Click += async (_, _) =>
        {
            make.IsEnabled = false;
            try
            {
                string folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string path = Path.Combine(folder, SupportBundle.FileName(DateTime.Now));
                var cfg = Config.Clone();
                await Task.Run(() => SupportBundle.Create(path, info, cfg, Logger.Directory, sanitizer, DateTime.Now));
                Logger.Info($"報告用の zip を作りました: {Path.GetFileName(path)}");
                status.Text = L($"{Path.GetFileName(path)} を作りました（エクスプローラーで場所を開きます）。報告ページに、このファイルをドラッグして添付してください。");
                SystemTool.Reveal(path);
            }
            catch (Exception ex)
            {
                Logger.Warn($"報告用の zip を作れません: {ex.Message}");
                status.Text = L($"zip を作れませんでした: {ex.Message}");
            }
            finally
            {
                make.IsEnabled = true;
            }
        };
        var open = Btn(L("② GitHub の報告ページを開く"), "AccentButton");
        open.ToolTip = L("版・マザーボード・CPU・グラボ・Windows の欄を埋めた状態で開きます（GitHub のアカウントが必要です）");
        open.Click += (_, _) =>
        {
            try { SystemTool.OpenUrl(SupportBundle.IssueUrl(info)); }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        var logs = Btn(L("ログフォルダを開く"), "GhostButton");
        logs.Click += (_, _) => SystemTool.Open(Logger.Directory);
        var close = Btn(L("閉じる"), "GhostButton");
        close.Click += (_, _) => win.Close();

        var preview = new TextBox
        {
            Text = sanitizer.Clean(SupportBundle.Summary(info)), IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"), FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        root.Children.Add(preview);
        win.Content = root;
        win.ShowDialog();
    }
}

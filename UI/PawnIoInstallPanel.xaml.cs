// ==================================================
// PawnIoInstallPanel.xaml.cs - PawnIO の状態の表示と、ボタンを押したときだけ winget でインストールする動作
//   (進み具合の表示・失敗したときの公式サイトの案内・終わったあとの確認と、起動し直し／PC の再起動の案内)
//   結果の読み取り方は Core/PawnIoSetup.cs、winget の実行は Core/Winget.cs
// Dependencies: WPF / .NET 8 (MIT License), winget
// See CREDITS.txt for full license details.
// ==================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ProFanController.UI;

public partial class PawnIoInstallPanel : UserControl
{
    /// <summary>どこかの画面でインストール中か（ウィザードと設定画面で、同時に 2 回動かさないため）</summary>
    public static bool Busy { get; private set; }

    /// <summary>インストールが終わった・状態が変わった（ほかの画面の表示も更新するため）</summary>
    private static event Action? StateChanged;

    /// <summary>このアプリが動いている間に、ここからのインストールで PawnIO が入った（起動し直すまで使えない）</summary>
    private static bool _installedThisRun;

    /// <summary>入ったが、仕上げに PC の再起動が要ると言われた</summary>
    private static bool _pcRestartPending;

    /// <summary>
    /// いま動いているアプリが、起動したとき（ハードウェアを開いたとき）に PawnIO を使えていたか（分からなければ null）。
    /// false のまま PawnIO が入ったら、アプリを起動し直すまで使えない
    /// </summary>
    public bool? LoadedAtStartup { get; set; }

    /// <summary>「アプリを起動し直す」が押された（指定がなければ、そのままアプリを起動し直す）</summary>
    public event Action? RestartRequested;

    private bool _mine;
    private PawnIoResult? _result;
    private string _phase = "";
    private DateTime _startedAt;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public PawnIoInstallPanel()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => ShowProgress();
        Loaded += (_, _) =>
        {
            StateChanged -= Refresh;
            StateChanged += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => StateChanged -= Refresh;
    }

    /// <summary>PawnIO が入っているか確かめ直して、表示を合わせる（レジストリを読むだけなので速い）</summary>
    public void Refresh()
    {
        bool installed;
        try { installed = PawnIoSetup.IsInstalled(); }
        catch (Exception ex)
        {
            Logger.Warn($"PawnIO の確認に失敗: {ex.Message}");
            installed = false;
        }
        bool running = Busy;

        InstallButton.IsEnabled = !running;
        InstallButton.Content = running && !_mine ? L("ほかの画面でインストール中です…") : L("PawnIO をインストール");
        RecheckButton.IsEnabled = !running;

        bool needsRestart = LoadedAtStartup == false || _installedThisRun;
        if (installed && !needsRestart)
        {
            SetStatus(L("✔ PawnIO（ファン制御用のドライバー）はインストール済みです。ファンを制御できます。"), Palette.Ok);
            Explanation.Visibility = Visibility.Collapsed;
            InstallButton.Visibility = Visibility.Collapsed;
            SiteButton.Visibility = Visibility.Collapsed;
            RecheckButton.Visibility = Visibility.Collapsed;
            RestartButton.Visibility = Visibility.Collapsed;
        }
        else if (installed)
        {
            // 仕上げに PC の再起動が要るときは、アプリを起動し直しても使えないので、そのボタンは出さない
            SetStatus(_pcRestartPending
                    ? L("PawnIO はインストールされましたが、仕上げに PC の再起動が必要です。PC を再起動すると、ファンを制御できるようになります。")
                    : L("PawnIO はインストールされましたが、まだ読み込まれていません。アプリを起動し直すと、ファンを制御できるようになります。"),
                Palette.Warn);
            Explanation.Visibility = Visibility.Collapsed;
            InstallButton.Visibility = Visibility.Collapsed;
            SiteButton.Visibility = Visibility.Collapsed;
            RecheckButton.Visibility = Visibility.Collapsed;
            RestartButton.Visibility = _pcRestartPending ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            SetStatus(L("✖ PawnIO（ファン制御用のドライバー）が見つかりません。このままでは、温度の表示だけでファンは制御できません。"), Palette.Danger);
            Explanation.Visibility = Visibility.Visible;
            InstallButton.Visibility = Visibility.Visible;
            SiteButton.Visibility = Visibility.Visible;
            RecheckButton.Visibility = Visibility.Visible;
            RestartButton.Visibility = _result?.NeedsAppRestart == true ? Visibility.Visible : Visibility.Collapsed;
        }
        ButtonRow.Visibility = ButtonRow.Children.OfType<UIElement>().Any(b => b.Visibility == Visibility.Visible)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string text, Brush brush)
    {
        StatusLine.Text = text;
        StatusLine.Foreground = brush;
    }

    /// <summary>入ってアプリを起動し直せば使えるとき、自動で起動し直す（初めてのセットアップの画面）</summary>
    public bool AutoRestartAfterInstall { get; set; }

    /// <summary>インストールを始める（初めてのセットアップの画面が、開いたときに呼ぶ）</summary>
    public void StartInstall() => OnInstallClick(this, new RoutedEventArgs());

    /// <summary>
    /// winget で PawnIO をインストールする（ボタンを押したとき・初めてのセットアップの画面を開いたとき）。
    /// 終わったら PawnIO が入ったかを確かめ直し、起動し直し・PC の再起動が要るかを伝える
    /// </summary>
    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        Busy = true;
        _mine = true;
        _result = null;
        ResultText.Visibility = Visibility.Collapsed;
        _phase = L("winget を起動しています…");
        _startedAt = DateTime.Now;
        ProgressText.Visibility = Visibility.Visible;
        ShowProgress();
        _clock.Start();
        StateChanged?.Invoke();
        Logger.Info($"PawnIO のインストールを始めます（winget {PawnIoSetup.InstallArguments}）");
        try
        {
            var run = await Winget.RunAsync(PawnIoSetup.InstallArguments, line =>
            {
                if (Winget.Describe(line) is { } text) Dispatcher.InvokeAsync(() => { _phase = text; });
            }, PawnIoSetup.Timeout);
            _phase = L("PawnIO が入ったか確かめています…");
            ShowProgress();
            bool detected = await Task.Run(PawnIoSetup.IsInstalled);
            // ボタンが出ていた = 押す前は入っていなかったので、いま動いているアプリは PawnIO なしでハードウェアを開いている
            if (detected) _installedThisRun = true;
            var result = PawnIoSetup.InterpretInstall(run, detected, loadedAtStartup: false);
            if (detected && result.Ok && result.NeedsPcRestart) _pcRestartPending = true;
            LogRun(L("インストール"), run, detected, result);
            _result = result;
            ShowResult(result);
        }
        catch (Exception ex)
        {
            Logger.Error($"PawnIO のインストールで例外: {ex}");
            _result = new PawnIoResult(false, L($"インストールの途中でエラーが起きました: {ex.Message}　公式サイトのインストーラーを使ってください。"),
                NeedsAppRestart: false, NeedsPcRestart: false, OfferOfficialSite: true);
            ShowResult(_result);
        }
        finally
        {
            _clock.Stop();
            ProgressText.Visibility = Visibility.Collapsed;
            Busy = false;
            _mine = false;
            StateChanged?.Invoke();
            Refresh();
            // Refresh の後に、結果に合わせたボタンを出す
            if (_result is { } r)
            {
                if (r.NeedsAppRestart) RestartButton.Visibility = Visibility.Visible;
                if (r.OfferOfficialSite) SiteButton.Visibility = Visibility.Visible;
                ButtonRow.Visibility = Visibility.Visible;
                // 初めてのセットアップ: 入って、起動し直せば使えるなら、そのまま起動し直す
                if (AutoRestartAfterInstall && r is { Ok: true, NeedsAppRestart: true } && !_pcRestartPending)
                {
                    Logger.Info("PawnIO が入ったので、セットアップを続けるために自動で起動し直します");
                    OnRestartClick(this, new RoutedEventArgs());
                }
            }
        }
    }

    /// <summary>winget の実行の結果をログに残す（出力は最後のほうだけ）</summary>
    internal static void LogRun(string what, WingetRun run, bool installedAfter, PawnIoResult result)
    {
        Logger.Info($"PawnIO の{what}: 結果 {run.Outcome}（終了コード {run.CodeText}）／ 終わったあとの PawnIO: {(installedAfter ? "あり" : "なし")}"
                    + (run.Error is { } err ? $" ／ {err}" : ""));
        if (run.Output.Count > 0) Logger.Info($"winget の出力（最後の {run.Output.Count} 行）:\n  " + string.Join("\n  ", run.Output));
        if (result.Ok) Logger.Info(result.Message);
        else Logger.Warn(result.Message);
    }

    private void ShowProgress()
    {
        int sec = (int)(DateTime.Now - _startedAt).TotalSeconds;
        ProgressText.Text = L($"{_phase}（{sec} 秒）　画面を閉じずにお待ちください。ほかの操作はできます。");
    }

    private void ShowResult(PawnIoResult r)
    {
        ResultText.Text = r.Message;
        ResultText.Foreground = r.Ok ? (r.NeedsPcRestart || r.NeedsAppRestart ? Palette.Warn : Palette.Ok) : Palette.Danger;
        ResultText.Visibility = Visibility.Visible;
    }

    private void OnRestartClick(object sender, RoutedEventArgs e)
    {
        if (RestartRequested is { } handler) handler();
        else ((App)Application.Current).RestartApplication();
    }

    private void OnSiteClick(object sender, RoutedEventArgs e)
    {
        try { SystemTool.OpenUrl(PawnIoSetup.OfficialSite); }
        catch (Exception ex)
        {
            Logger.Warn($"公式サイトを開けません: {ex.Message}");
            MessageBox.Show(L($"ブラウザーを開けませんでした。{PawnIoSetup.OfficialSite} を開いてください。"), AppIdentity.Name,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnRecheckClick(object sender, RoutedEventArgs e)
    {
        _result = null;
        ResultText.Visibility = Visibility.Collapsed;
        StateChanged?.Invoke();
        Refresh();
        if (!PawnIoSetup.IsInstalled())
        {
            ResultText.Text = L("まだ PawnIO が見つかりません。インストーラーを実行したあとなら、PC を再起動してからもう一度確かめてください。");
            ResultText.Foreground = Palette.Warn;
            ResultText.Visibility = Visibility.Visible;
        }
    }
}

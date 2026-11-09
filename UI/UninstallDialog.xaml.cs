// ==================================================
// UninstallDialog.xaml.cs - アンインストール（お片付け）の確認と進み具合の画面の動作
//   (選んだ内容で手順を並べ直す・押されたら App に手順を行ってもらい、1 つずつ結果を表示する・最後のまとめ)
//   手順の順番と判断は Core/CleanUninstall.cs、実際の操作は App.Uninstall.cs
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace ProFanController.UI;

public partial class UninstallDialog : Window
{
    private readonly CleanupFacts _facts;
    private readonly string _appFolder;
    private readonly Func<IReadOnlyList<CleanupStep>, Action<StepResult>, Task<IReadOnlyList<StepResult>>> _run;
    private readonly ObservableCollection<CleanupStepVm> _rows = new();
    private bool _running;

    /// <summary>行う（行った）手順</summary>
    public IReadOnlyList<CleanupStep> Plan { get; private set; } = Array.Empty<CleanupStep>();

    /// <summary>手順を終えたので、画面を閉じたらアプリを終了する（ファンを戻せずに中止したときは false）</summary>
    public bool ExitRequested { get; private set; }

    public UninstallDialog(CleanupFacts facts, string appFolder,
        Func<IReadOnlyList<CleanupStep>, Action<StepResult>, Task<IReadOnlyList<StepResult>>> run)
    {
        InitializeComponent();
        _facts = facts;
        _appFolder = appFolder.TrimEnd(Path.DirectorySeparatorChar);
        _run = run;
        StepList.ItemsSource = _rows;

        DeleteDataCaption.Text = L($"アプリのフォルダーの中の{CleanUninstall.DataDescription}を、アプリが終了したあとに消します。")
                                 + L("アプリ本体（exe など）と、ほかのファイルは残します。もう一度使うかもしれないときは、オフのままにしてください。");
        if (!facts.PawnIoInstalled)
        {
            PawnIoSwitch.IsEnabled = false;
            PawnIoSwitch.Opacity = 0.5;
            PawnIoCaption.Text = L("PawnIO はこの PC にインストールされていません（何もしません）。");
        }
        FolderNote.Text = L("アプリ本体は動いている間は消せません。終了したあとに、エクスプローラーで次のフォルダーを開くので、フォルダーごと削除してください:\n")
                          + _appFolder;
        BuildRows();
        Closing += OnClosing;
    }

    private CleanupOptions Options => new(DeleteData: DeleteDataSwitch.IsChecked == true, UninstallPawnIo: PawnIoSwitch.IsChecked == true);

    /// <summary>選んだ内容で、行う手順を並べ直す（確認の画面には、実際に行うことだけを出す）</summary>
    private void BuildRows()
    {
        Plan = CleanUninstall.Plan(Options, _facts);
        _rows.Clear();
        foreach (var step in Plan) _rows.Add(new CleanupStepVm(step, CleanUninstall.Describe(step)));
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_running) BuildRows();
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;
        BuildRows();
        DeleteDataSwitch.IsEnabled = false;
        PawnIoSwitch.IsEnabled = false;
        StartButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Collapsed;
        IntroText.Text = L("お片付けをしています。終わるまで、この画面を閉じずにお待ちください（PawnIO のアンインストールには、数十秒〜数分かかることがあります）。");

        IReadOnlyList<StepResult> results;
        try
        {
            results = await _run(Plan, r => Find(r.Step)?.Update(r.Status, r.Message));
        }
        catch (Exception ex)
        {
            // ここに来るのは、手順の外での思いがけない失敗だけ（手順の中の失敗は、手順の結果として返ってくる）。
            // どこまで進んだか分からないので、ふだんの終了（ファンを BIOS に戻す・戻せなければ見張りが安全な回転数にする）で終わる。設定・記録は消さない
            Logger.Error($"お片付けの途中で例外: {ex}");
            _running = false;
            foreach (var row in _rows.Where(r => r.Status is StepStatus.Pending or StepStatus.Running)) row.Update(StepStatus.Skipped, L("行いませんでした"));
            ShowFinished(exit: true, deleteData: false, intro: L("お片付けを最後まで行えませんでした。"),
                summary: L($"思いがけないエラーで、お片付けを最後まで行えませんでした: {ex.Message}\n")
                         + L("閉じると、アプリをふだんどおり終了します（ファンは BIOS の自動制御に戻ります）。設定・ログ・記録は残します。\n")
                         + L($"ログ: {Logger.Directory}"));
            return;
        }
        _running = false;

        bool aborted = CleanUninstall.Aborted(results);
        foreach (var row in _rows.Where(r => r.Status == StepStatus.Pending))
        {
            if (aborted) row.Update(StepStatus.Skipped, L("行いませんでした"));
            else row.Update(StepStatus.Pending, L("「閉じて終了する」を押すと行います"));
        }
        ShowFinished(exit: !aborted, deleteData: !aborted && Plan.Contains(CleanupStep.DeleteData),
            intro: aborted ? L("お片付けを中止しました。") : L("お片付けが終わりました。内容を確かめてから、閉じてください。"),
            summary: CleanUninstall.Summary(results, Plan, _appFolder, byUninstaller: InstallerCleanup.UninstallerPath(_appFolder) is not null));
    }

    /// <summary>閉じたあとに、設定・ログ・記録を消す</summary>
    public bool DeleteDataAfterExit { get; private set; }

    private void ShowFinished(bool exit, bool deleteData, string intro, string summary)
    {
        ExitRequested = exit;
        DeleteDataAfterExit = deleteData;
        StartButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        CloseButton.Content = exit ? L("閉じて終了する") : L("閉じる");
        CloseButton.Visibility = Visibility.Visible;
        OptionsCard.Visibility = Visibility.Collapsed;
        FolderNote.Visibility = Visibility.Collapsed;
        IntroText.Text = intro;
        SummaryText.Text = summary;
        SummaryCard.Visibility = Visibility.Visible;
        SummaryCard.BringIntoView();
    }

    private CleanupStepVm? Find(CleanupStep step) => _rows.FirstOrDefault(r => r.Step == step);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 手順の途中では閉じさせない（途中で閉じても手順は止まらず、結果が見えなくなるだけなので）
        if (_running) e.Cancel = true;
    }
}

/// <summary>お片付けの画面の手順 1 行分</summary>
public sealed class CleanupStepVm : ObservableObject
{
    private StepStatus _status = StepStatus.Pending;
    private string _detail = "";

    public CleanupStepVm(CleanupStep step, string text)
    {
        Step = step;
        Text = text;
    }

    public CleanupStep Step { get; }
    public string Text { get; }
    public StepStatus Status => _status;
    public string Detail => _detail;
    public Visibility DetailVisibility => _detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Glyph => _status switch
    {
        StepStatus.Running => "…",
        StepStatus.Done => "✔",
        StepStatus.Warning => L("！"),
        StepStatus.Failed => "✖",
        StepStatus.Skipped => L("－"),
        _ => L("・"),
    };

    public Brush GlyphBrush => _status switch
    {
        StepStatus.Running => Palette.Accent2,
        StepStatus.Done => Palette.Ok,
        StepStatus.Warning => Palette.Warn,
        StepStatus.Failed => Palette.Danger,
        _ => Palette.Muted,
    };

    public void Update(StepStatus status, string detail)
    {
        _status = status;
        _detail = detail;
        Raise(nameof(Status));
        Raise(nameof(Detail));
        Raise(nameof(DetailVisibility));
        Raise(nameof(Glyph));
        Raise(nameof(GlyphBrush));
    }
}

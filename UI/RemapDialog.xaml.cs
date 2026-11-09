// ==================================================
// RemapDialog.xaml.cs - センサー・端子の対応付け画面の動作 (部品交換の検知・設定の読み込みで共通)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Windows;
using System.Windows.Media;

namespace ProFanController.UI;

public sealed record RemapOption(string Id, string Display);

/// <summary>対応付けの 1 行（"" は「使わない」）</summary>
public sealed class RemapRowVm : ObservableObject
{
    public required RemapItem Item { get; init; }
    public required IReadOnlyList<RemapOption> Options { get; init; }
    public string UsageText => Item.Usage.Length > 0 ? L($"使っている場所: {Item.Usage}") : Item.OldId;
    public SolidColorBrush NoteBrush => Item.NewId is null ? Palette.Warn : Palette.Muted;

    public string Choice
    {
        get => Item.NewId ?? "";
        set
        {
            Item.NewId = string.IsNullOrEmpty(value) ? null : value;
            Raise();
            Raise(nameof(NoteBrush));
        }
    }
}

public sealed class ProfilePickVm : ObservableObject
{
    private bool _selected = true;
    public required string Name { get; init; }
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

public partial class RemapDialog : Window
{
    private readonly List<ProfilePickVm> _profiles = new();

    /// <summary>「セットアップウィザードで最初から設定する」が押された</summary>
    public bool WizardRequested { get; private set; }
    public IEnumerable<string> SelectedProfiles => _profiles.Where(p => p.Selected).Select(p => p.Name);
    public bool ReplaceSameName => ReplaceSwitch.IsChecked == true;

    private RemapDialog(IReadOnlyList<RemapItem> items, HardwareCatalog catalog)
    {
        InitializeComponent();

        var none = new RemapOption("", L("（使わない）"));
        var sensorOptions = new[] { none }.Concat(catalog.Temperatures.Select(t => new RemapOption(t.Id, $"{t.Name}  ·  {t.HardwareName}"))).ToList();
        var controlOptions = new[] { new RemapOption("", L("（このファンを削除する）")) }
            .Concat(catalog.Controls.Select(c => new RemapOption(c.Id, c.Display))).ToList();

        var controls = items.Where(i => i.IsControl).Select(i => new RemapRowVm { Item = i, Options = controlOptions }).ToList();
        var sensors = items.Where(i => !i.IsControl).Select(i => new RemapRowVm { Item = i, Options = sensorOptions }).ToList();
        ControlRows.ItemsSource = controls;
        SensorRows.ItemsSource = sensors;
        ControlSection.Visibility = controls.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SensorSection.Visibility = sensors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>部品の交換を検知したとき</summary>
    public static RemapDialog ForHardwareChange(HardwareChangeReport report, HardwareCatalog catalog)
    {
        var d = new RemapDialog(report.Items, catalog);
        d.Title = L("ハードウェアの変更 - Fanlume");
        d.TitleText.Text = L("ハードウェアの変更を検出しました");
        d.SubText.Text = L("設定で使っていたセンサーや端子が見つかりません。新しいハードウェアで同じ役割のものを自動で選びました。") +
                         L("内容を確認して「置き換えて適用」を押してください（今の設定はバックアップしてから変更します。あとで「元に戻す」もできます）。\n") +
                         L("それまでの間、見つからないセンサーを使っているカーブは、安全のため一定の回転数（設定 →「安全・バックアップ」で変更可）で回しています。");
        if (report.DeviceChanges.Count > 0)
        {
            d.ChangesList.ItemsSource = report.DeviceChanges;
            d.ChangesCard.Visibility = Visibility.Visible;
        }
        d.WizardButton.Visibility = Visibility.Visible;
        return d;
    }

    /// <summary>設定ファイルを読み込むとき</summary>
    public static RemapDialog ForImport(SettingsPackage pkg, IReadOnlyList<RemapItem> items, HardwareCatalog catalog)
    {
        var d = new RemapDialog(items, catalog);
        d.Title = L("設定の読み込み - Fanlume");
        d.HeaderIcon.Text = "";
        d.TitleText.Text = L("設定を読み込む");
        d.SubText.Text = L("ファイルの中のファン端子・温度センサーを、この PC のどれに対応させるか確認してください。") +
                         L("名前が同じものを自動で選んでいます。「（このファンを削除する）」にしたファンは読み込みません。");
        d.ImportSource.Text = L($"書き出した PC: {pkg.Machine}（{pkg.ExportedAt:yyyy/MM/dd HH:mm}）") +
                              (pkg.DevicesText.Length > 0 ? $"\n{pkg.DevicesText}" : "");
        foreach (var p in pkg.Profiles) d._profiles.Add(new ProfilePickVm { Name = p.Name });
        d.ProfilePicks.ItemsSource = d._profiles;
        d.ImportCard.Visibility = Visibility.Visible;
        d.ApplyButton.Content = L("読み込む");
        d.CancelButton.Content = L("キャンセル");
        return d;
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (ImportCard.Visibility == Visibility.Visible && !_profiles.Any(p => p.Selected))
        {
            MessageBox.Show(this, L("読み込むプロファイルを 1 つ以上選んでください。"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void OnWizardClick(object sender, RoutedEventArgs e)
    {
        WizardRequested = true;
        DialogResult = false;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}

// ==================================================
// SetupWizard.xaml.cs - セットアップウィザードの動作と、選択内容からの設定生成（PawnIO がなければ、最初の画面からインストールできる）
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace ProFanController.UI;

public partial class SetupWizard : Window
{
    private readonly AppController _controller;
    private readonly ObservableCollection<WizardChannelVm> _channels = new();
    private readonly IReadOnlyList<SensorOption> _temps;
    private bool _rolesGuessed;
    private int _step = 1;

    public SetupWizard(AppController controller, HardwareCatalog catalog, IReadOnlyList<SensorOption> temps)
    {
        InitializeComponent();
        _controller = controller;
        _temps = temps;

        if (catalog.PawnIoInstalled)
        {
            PawnIoStatus.Text = L($"✔ PawnIO ドライバ検出済み ／ ファン端子 {catalog.Controls.Count} 個を検出しました");
            PawnIoStatus.Foreground = Palette.Ok;
        }
        else
        {
            // PawnIO がないと端子が見つからない。セットアップに必要なので、この画面を開いたらすぐに winget でインストールを始め、
            // 入ったら自動で起動し直す（起動し直したアプリで、もう一度この画面が開く）。
            // 起動し直してもまだ読み込めないとき（PC の再起動が要るなど）は、くり返さずにボタンと公式サイトの案内を残す
            var app = (App)Application.Current;
            PawnIoStatus.Visibility = Visibility.Collapsed;
            PawnIoPanel.LoadedAtStartup = false;
            PawnIoPanel.Visibility = Visibility.Visible;
            PawnIoPanel.RestartRequested += () =>
            {
                DialogResult = false;
                Dispatcher.BeginInvoke(() => app.RestartApplication(App.PawnIoTriedArgument));
            };
            if (!app.PawnIoAutoTried)
            {
                PawnIoPanel.AutoRestartAfterInstall = true;
                Loaded += (_, _) => PawnIoPanel.StartInstall();
            }
        }

        // 既存の設定で名前が付いている端子は、その名前を引き継ぐ
        var existing = controller.Config.Profiles.SelectMany(p => p.Fans)
            .GroupBy(f => f.ControlId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var c in catalog.Controls)
        {
            var vm = new WizardChannelVm { Control = c, Name = c.Name };
            if (existing.TryGetValue(c.Id, out var fan))
            {
                vm.Name = fan.Name;
                vm.Role = fan.Role;
            }
            _channels.Add(vm);
        }
        ChannelList.ItemsSource = _channels;

        var physical = temps.Where(t => !t.Id.StartsWith(VirtualSensorConfig.IdPrefix)).ToList();
        CpuSensorCombo.ItemsSource = physical;
        CpuSensorCombo.SelectedItem = SensorNames.Pick(physical.Where(t => t.Kind == SensorKind.Cpu).ToList(), t => t.Name, SensorNames.CpuTemp)
                                      ?? physical.FirstOrDefault(t => t.Kind == SensorKind.Cpu) ?? physical.FirstOrDefault();
        GpuSensorCombo.ItemsSource = physical;
        // Hot Spot は変動が激しく、ケースファンでは下げにくいので、GPU 温度（Edge = GPU Core）を初期値にする
        GpuSensorCombo.SelectedItem = SensorNames.Pick(physical.Where(t => t.Kind == SensorKind.Gpu).ToList(), t => t.Name, SensorNames.GpuCoreTemp)
                                      ?? physical.FirstOrDefault(t => t.Kind == SensorKind.Gpu);

        ShowStep();
    }

    /// <summary>メイン画面から毎周期呼ばれ、回転数の表示と役割の初期推定を行う</summary>
    public void OnSnapshot(EngineSnapshot s)
    {
        foreach (var ch in _channels)
        {
            ch.UpdateRpm(ch.Control.RpmSensorId is { } id && s.Values.TryGetValue(id, out var v) ? v : null);
        }
        if (_rolesGuessed) return;
        _rolesGuessed = true;

        // 0 rpm は未接続、名前に Pump を含むか 2200 rpm 超はポンプの可能性が高い
        foreach (var ch in _channels.Where(c => !_controller.Config.Profiles.SelectMany(p => p.Fans).Any(f => f.ControlId == c.Control.Id)))
        {
            if (ch.Rpm < 1) ch.Role = WizardChannelVm.RoleUnused;
            else if (ch.Control.Name.Contains("Pump", StringComparison.OrdinalIgnoreCase) || ch.Rpm > 2200) ch.Role = FanRoles.Pump;
        }
    }

    private void ShowStep()
    {
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        var dots = new[] { Step1Dot, Step2Dot, Step3Dot, Step4Dot };
        for (int i = 0; i < dots.Length; i++)
        {
            dots[i].Background = i + 1 == _step ? (System.Windows.Media.Brush)FindResource("AccentGradient")
                : i + 1 < _step ? (System.Windows.Media.Brush)FindResource("CardHoverBrush")
                : (System.Windows.Media.Brush)FindResource("InputBrush");
        }

        BackButton.Visibility = _step > 1 ? Visibility.Visible : Visibility.Hidden;
        NextButton.Content = _step == 4 ? L("完了して適用") : L("次へ");
        if (_step == 4) SummaryText.Text = BuildSummary();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step == 2 && !_channels.Any(c => c.Role != WizardChannelVm.RoleUnused))
        {
            MessageBox.Show(this, L("少なくとも 1 つの端子を「ファン」か「ポンプ」にしてください。"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_step == 3 && CpuSensorCombo.SelectedItem is null)
        {
            MessageBox.Show(this, L("CPU の温度センサーを選んでください。"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_step < 4)
        {
            _step++;
            ShowStep();
            return;
        }

        _controller.ReplaceConfig(BuildConfig());
        DialogResult = true;
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step <= 1) return;
        _step--;
        ShowStep();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnTest(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WizardChannelVm ch }) _controller.Engine.Identify(ch.Control.Id);
    }

    private string SelectedPreset
        => PresetSilent.IsChecked == true ? CurvePresets.Silent
         : PresetPerformance.IsChecked == true ? CurvePresets.Performance
         : CurvePresets.Balanced;

    private static string ProfileNameFor(string preset) => preset switch
    {
        CurvePresets.Silent => L("静音"),
        CurvePresets.Performance => L("パフォーマンス"),
        _ => L("標準"),
    };

    private string BuildSummary()
    {
        var used = _channels.Where(c => c.Role != WizardChannelVm.RoleUnused).ToList();
        var lines = used.Select(c => c.Role == FanRoles.Pump
            ? L($"・{c.Name}（ポンプ）: {(PumpControlSwitch.IsChecked == true ? L("固定 70% で制御") : L("BIOS 制御のまま監視"))}")
            : L($"・{c.Name}（ファン）: {SourceText(c.Source)} に連動"));
        return L($"作成するプロファイル: {ProfileNameFor(CurvePresets.Silent)} ／ {ProfileNameFor(CurvePresets.Balanced)} ／ {ProfileNameFor(CurvePresets.Performance)}\n普段使うプロファイル: {ProfileNameFor(SelectedPreset)}\n\n") +
               string.Join("\n", lines);
    }

    private static string SourceText(string source) => source switch
    {
        WizardChannelVm.SourceGpu => "GPU",
        WizardChannelVm.SourceBoth => L("CPU と GPU"),
        _ => "CPU",
    };

    private AppConfig BuildConfig()
    {
        var cfg = _controller.Config.Clone();
        var cpu = (SensorOption)CpuSensorCombo.SelectedItem;
        var gpu = GpuSensorCombo.SelectedItem as SensorOption;

        cfg.Profiles.Clear();
        foreach (var preset in new[] { CurvePresets.Silent, CurvePresets.Balanced, CurvePresets.Performance })
        {
            cfg.Profiles.Add(new ProfileConfig { Name = ProfileNameFor(preset), Role = ProfileRoles.FromPreset(preset), Fans = BuildFans(preset, cpu, gpu, cfg) });
        }
        cfg.ActiveProfile = ProfileNameFor(SelectedPreset);
        cfg.DashboardCards.Clear();
        cfg.DryRun = StartNowSwitch.IsChecked != true;
        cfg.SetupCompleted = true;
        cfg.Normalize();
        return cfg;
    }

    private ObservableCollection<FanConfig> BuildFans(string preset, SensorOption cpu, SensorOption? gpu, AppConfig cfg)
    {
        var fans = new ObservableCollection<FanConfig>();
        foreach (var ch in _channels.Where(c => c.Role != WizardChannelVm.RoleUnused))
        {
            var fan = new FanConfig
            {
                Name = string.IsNullOrWhiteSpace(ch.Name) ? ch.Control.Name : ch.Name.Trim(),
                ControlId = ch.Control.Id,
                RpmSensorId = ch.Control.RpmSensorId,
                Role = ch.Role,
            };

            if (ch.Role == FanRoles.Pump)
            {
                fan.Enabled = PumpControlSwitch.IsChecked == true;
                fan.UseFixedDuty = true;
                fan.FixedDuty = 70;
                fan.MinDuty = FanEngine.PumpHardMinDuty;
                // 今の回転数の 6 割を下回ったら異常とみなす（50 rpm 単位）
                fan.PumpAlertRpm = ch.Rpm > 0 ? Math.Max(500, Math.Round(ch.Rpm * 0.6 / 50) * 50) : 1000;
                fan.Curves.Add(new CurveConfig { Name = "CPU", SensorId = cpu.Id, Points = CurvePresets.Create(CurvePresets.Balanced, cpu.Kind) });
            }
            else
            {
                // キャリブレーション済みなら「止まらない最低回転」を下限にする
                var cal = cfg.FindCalibration(ch.Control.Id);
                fan.MinDuty = cal is not null ? Math.Min(60, cal.StopDuty + 5) : 25;
                fan.MaxStepUpPerTick = preset == CurvePresets.Silent ? 4 : 8;
                fan.MaxStepDownPerTick = preset == CurvePresets.Silent ? 1.5 : 2;

                bool useGpu = gpu is not null && ch.Source is WizardChannelVm.SourceGpu or WizardChannelVm.SourceBoth;
                bool useCpu = !useGpu || ch.Source == WizardChannelVm.SourceBoth;
                if (useCpu)
                    fan.Curves.Add(new CurveConfig { Name = "CPU", SensorId = cpu.Id, HysteresisCelsius = 5, EmaAlpha = 0.15, Points = CurvePresets.Create(preset, cpu.Kind) });
                if (useGpu)
                    fan.Curves.Add(new CurveConfig { Name = "GPU", SensorId = gpu!.Id, HysteresisCelsius = 4, EmaAlpha = 0.2, Points = CurvePresets.Create(preset, gpu.Kind) });
            }
            fans.Add(fan);
        }
        return fans;
    }
}

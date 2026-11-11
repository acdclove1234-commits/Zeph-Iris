// ==================================================
// MainWindow.AutoTune.cs - 自動セットアップの画面
//   (① 特性を測っていないファンのキャリブレーション → ② CPU・GPU に負荷をかけて測る → ③ 結果のカーブを確かめて適用。
//    手順は Core/AutoSetup.cs、1 回の測定は Core/AutoTuner.cs、GPU の負荷は Gpu/GpuStressLoad.cs)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using ProFanController.UI;
using ProFanController.UI.Controls;

namespace ProFanController;

/// <summary>自動セットアップで選ぶファン</summary>
public sealed class TuneFanVm : ObservableObject
{
    private bool _selected;
    public required string ControlId { get; init; }
    public required string Name { get; init; }
    /// <summary>今つながっている温度と、特性を測ったか（例: 「（CPU 連動・特性は未測定）」）</summary>
    public string Hint { get; init; } = "";
    public SensorKind[] Kinds { get; init; } = Array.Empty<SensorKind>();
    public bool CanCalibrate { get; init; }
    public bool Calibrated { get; init; }
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>測定結果の 1 行</summary>
public sealed class TuneStepVm
{
    public required AutoTuneStep Step { get; init; }
    public required double Target { get; init; }
    public string DutyText => $"{Step.Duty:F0}%";
    public string TempText => $"{Step.SteadyTemp:F1} °C";
    public string RpmText => Step.AvgRpm is double r ? $"{r:N0} rpm" : "";
    public SolidColorBrush Brush => Step.SteadyTemp <= Target - 1.5 ? Palette.Ok : Step.SteadyTemp <= Target + 2 ? Palette.Warn : Palette.Danger;
}

public partial class MainWindow
{
    private readonly ObservableCollection<TuneFanVm> _tuneFans = new();
    /// <summary>実行中の測定（CPU か GPU の 1 回分）</summary>
    private AutoTuner? _tuner;
    private GpuStressLoad? _gpuLoad;
    /// <summary>実行中の自動セットアップの手順（実行していなければ null）</summary>
    private List<SetupStep>? _setupSteps;
    private int _setupIndex;
    /// <summary>自動セットアップの途中で起きたこと（キャリブレーションの結果など。終わったら知らせる）</summary>
    private readonly List<string> _setupNotes = new();
    /// <summary>測定結果（CPU・GPU）。カーブはここから作る</summary>
    private readonly Dictionary<SetupStepKind, AutoTuneResult> _tuneResults = new();
    private readonly Dictionary<SetupStepKind, double> _tuneMinDuties = new();
    private string? _tunePreselect;

    /// <summary>自動セットアップ（キャリブレーション・測定）の途中</summary>
    private bool SetupRunning => _setupSteps is not null;
    private SetupStepKind ShownResult => TuneResultGpu.IsChecked == true ? SetupStepKind.TuneGpu : SetupStepKind.TuneCpu;
    private double TargetFor(SetupStepKind kind) => kind == SetupStepKind.TuneGpu ? TuneGpuTargetSlider.Value : TuneTargetSlider.Value;

    /// <summary>実行中のプロファイルのファン（ポンプ以外）を一覧にする。CPU・GPU につながっているファンを最初から選んでおく</summary>
    private void RebuildTuneFans()
    {
        var keep = _tuneFans.Where(f => f.Selected).Select(f => f.ControlId).ToHashSet();
        bool hadSelection = keep.Count > 0;
        _tuneFans.Clear();
        foreach (var fan in Config.FindProfile(_controller.EffectiveProfile)?.Fans.Where(f => !f.IsPump) ?? Enumerable.Empty<FanConfig>())
        {
            var kinds = fan.Curves.Select(c => FindSensorOption(c.SensorId)?.Kind ?? SensorKind.Other)
                .Append(fan.Mode == FanModes.Target ? FindSensorOption(fan.TargetSensorId)?.Kind ?? SensorKind.Other : SensorKind.Other)
                .Distinct().ToArray();
            string link = string.Join(Localization.ListSeparator, kinds.Where(k => k is SensorKind.Cpu or SensorKind.Gpu or SensorKind.GpuHotspot)
                .Select(k => k == SensorKind.Cpu ? "CPU" : "GPU").Distinct());
            bool canCal = _catalog?.Controls.FirstOrDefault(c => c.Id == fan.ControlId)?.RpmSensorId is not null;
            bool cal = Config.FindCalibration(fan.ControlId) is not null;
            var parts = new List<string>();
            if (link.Length > 0) parts.Add(L($"{link} 連動"));
            if (canCal && !cal) parts.Add(L("特性は未測定"));
            _tuneFans.Add(new TuneFanVm
            {
                ControlId = fan.ControlId,
                Name = fan.Name,
                Hint = parts.Count > 0 ? L($"（{string.Join(Localization.ListSeparator, parts)}）") : "",
                Kinds = kinds,
                CanCalibrate = canCal,
                Calibrated = cal,
                Selected = _tunePreselect is not null ? fan.ControlId == _tunePreselect
                    : hadSelection ? keep.Contains(fan.ControlId) : kinds.Any(k => k is SensorKind.Cpu or SensorKind.Gpu or SensorKind.GpuHotspot),
            });
        }
        _tunePreselect = null;
        TuneProfileCombo.ItemsSource = Config.Profiles;
        TuneProfileCombo.SelectedItem = Config.FindProfile(_controller.EffectiveProfile);
        UpdateGpuAvailability();
        ShowSetupPlan();
    }

    /// <summary>GPU の温度と使用率が読めなければ、GPU は測れない</summary>
    private void UpdateGpuAvailability()
    {
        _roles = ResolveRoles();
        bool ok = _roles.ContainsKey(StatRoles.GpuTemp) && _roles.ContainsKey(StatRoles.GpuLoad);
        TuneGpuCheck.IsEnabled = ok;
        TuneGpuTargetSlider.IsEnabled = ok;
        if (!ok)
        {
            TuneGpuCheck.IsChecked = false;
            TuneGpuCheck.ToolTip = L("GPU の温度か使用率のセンサーが見つからないため、GPU は測れません");
        }
    }

    private List<SetupFan> SelectedSetupFans()
        => _tuneFans.Where(f => f.Selected).Select(f => new SetupFan(f.ControlId, f.Name, f.Kinds, f.CanCalibrate, f.Calibrated)).ToList();

    private List<SetupStep> PlanSetup()
        => AutoSetupPlan.Build(SelectedSetupFans(), TuneCpuCheck.IsChecked == true, TuneGpuCheck.IsChecked == true, TuneCalibrateCheck.IsChecked == true);

    /// <summary>始める前に、これから何をするかを見せる（例: 「キャリブレーション 1 台 → CPU で測る（2 台）→ GPU で測る（1 台）・約 22 分」）</summary>
    private void ShowSetupPlan()
    {
        if (SetupRunning || TuneStatus is null) return;
        var steps = PlanSetup();
        if (steps.Count == 0)
        {
            TuneStatus.Text = L("測るファンと、CPU・GPU のどちらかを選んでください。ポンプは対象外です（いつもどおり動きます）。");
            return;
        }
        var parts = new List<string>();
        int cal = steps.Count(s => s.Kind == SetupStepKind.Calibrate);
        if (cal > 0) parts.Add(L($"特性を測る {cal} 台"));
        foreach (var s in steps.Where(s => s.Kind != SetupStepKind.Calibrate))
            parts.Add(s.Kind == SetupStepKind.TuneGpu ? L($"GPU で測る（{s.Fans.Count} 台）") : L($"CPU で測る（{s.Fans.Count} 台）"));
        TuneStatus.Text = L($"手順: {string.Join(" → ", parts)}・約 {AutoSetupPlan.EstimateMinutes(steps)} 分。ポンプは対象外です。");
    }

    private void OnTuneTargetChanged(object sender, RoutedEventArgs e) => ShowSetupPlan();

    private void OnTuneFanChanged(object sender, RoutedEventArgs e) => ShowSetupPlan();

    private void OnTuneTargetValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TuneTargetText is null || TuneGpuTargetText is null) return;
        TuneTargetText.Text = $"{TuneTargetSlider.Value:F0}°C";
        TuneGpuTargetText.Text = $"{TuneGpuTargetSlider.Value:F0}°C";
        if (_tuneResults.Count > 0) ShowTuneResult();
    }

    private void OnTuneResultSwitch(object sender, RoutedEventArgs e)
    {
        if (TuneResultPanel is not null && _tuneResults.Count > 0) ShowTuneResult();
    }

    /// <summary>ファンカーブ画面の「自動セットアップ…」: そのファンを選んだ状態で開く</summary>
    private void OnGoAutoTuneClick(object sender, RoutedEventArgs e)
    {
        if (SelectedFan is { } fan) _tunePreselect = fan.ControlId;
        if (!SetupRunning) RebuildTuneFans();
        NavTest.IsChecked = true;
    }

    // =====================================================================
    // 実行
    // =====================================================================

    private void OnStartTuneClick(object sender, RoutedEventArgs e)
    {
        if (_catalog is null || SetupRunning) return;
        if (_test is not null || _calibratingId is not null)
        {
            MessageBox.Show(this, L("冷却テストかキャリブレーションが実行中です。終わってから始めてください。"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var steps = PlanSetup();
        if (steps.Count == 0)
        {
            MessageBox.Show(this, L("測るファンを 1 つ以上と、CPU・GPU のどちらかを選んでください。"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _roles = ResolveRoles();
        if (steps.Any(s => s.Kind == SetupStepKind.TuneCpu) && !_roles.ContainsKey(StatRoles.CpuTemp))
        {
            MessageBox.Show(this, L("CPU の温度センサーが見つかりません。"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int cal = steps.Count(s => s.Kind == SetupStepKind.Calibrate);
        bool cpu = steps.Any(s => s.Kind == SetupStepKind.TuneCpu), gpu = steps.Any(s => s.Kind == SetupStepKind.TuneGpu);
        var lines = new List<string>();
        if (cal > 0) lines.Add(L($"・特性を測る（{cal} 台）: ファンが 1 台ずつ全開になったり止まったりします。"));
        if (cpu) lines.Add(L("・CPU で測る: CPU に全力で負荷をかけ、ファンを 100% から少しずつ下げます。"));
        if (gpu) lines.Add(L("・GPU で測る: アプリが GPU に全力で負荷をかけ、同じように測ります（ゲームの起動は要りません）。"));
        lines.Add(L("・ファンの音が大きくなったり小さくなったりします。重いアプリは閉じておいてください。"));
        lines.Add(L($"・CPU は {CpuAbortTemp():F0}°C、GPU は {GpuAbortTemp:F0}°C に達したら、すぐに中止していつもの制御に戻します。"));
        lines.Add(L("・結果は確かめてから適用するので、途中で中止しても設定は変わりません。"));
        var answer = MessageBox.Show(this,
            L($"自動セットアップを始めます（約 {AutoSetupPlan.EstimateMinutes(steps)} 分）。\n\n") + string.Join("\n", lines),
            L("自動セットアップ"), MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK) return;

        _setupSteps = steps;
        _setupIndex = 0;
        _setupNotes.Clear();
        _tuneResults.Clear();
        _tuneMinDuties.Clear();
        StartTuneButton.Visibility = Visibility.Collapsed;
        CancelTuneButton.Visibility = Visibility.Visible;
        StartTestButton.IsEnabled = false;
        TuneSettings.IsEnabled = false;
        TuneProgress.Visibility = Visibility.Visible;
        TuneProgress.Value = 0;
        TuneResultPanel.Visibility = Visibility.Collapsed;
        TuneLive.Text = "";
        Logger.Info($"自動セットアップを開始（{string.Join(" → ", steps.Select(s => $"{s.Kind}: {string.Join(", ", s.Fans.Select(f => f.Name))}"))}）");
        StartSetupStep();
    }

    private double CpuAbortTemp() => Math.Min(Config.EmergencyTemperature - 3, 97);
    private const double GpuAbortTemp = 90;

    /// <summary>今の手順を始める（全部終わったら仕上げる）</summary>
    private void StartSetupStep()
    {
        if (_setupSteps is null) return;
        if (_setupIndex >= _setupSteps.Count)
        {
            FinishSetup(cancelled: false);
            return;
        }
        var step = _setupSteps[_setupIndex];
        UpdateSetupProgress(0);
        if (step.Kind == SetupStepKind.Calibrate)
        {
            var (id, name) = step.Fans[0];
            TuneStatus.Text = StepPrefix() + L($"「{name}」の特性を測っています…");
            _calibratingId = id;
            _controller.Engine.StartCalibration(id);
            return;
        }

        bool isGpu = step.Kind == SetupStepKind.TuneGpu;
        string role = isGpu ? StatRoles.GpuTemp : StatRoles.CpuTemp;
        _roles = ResolveRoles();
        if (!_roles.TryGetValue(role, out var sensorId) || (isGpu && !_roles.ContainsKey(StatRoles.GpuLoad)))
        {
            _setupNotes.Add(isGpu ? L("GPU: 温度か使用率のセンサーが見つからないため、測れませんでした") : L("CPU: 温度センサーが見つからないため、測れませんでした"));
            _setupIndex++;
            StartSetupStep();
            return;
        }
        string? loadId = isGpu ? _roles[StatRoles.GpuLoad] : null;
        // キャリブレーションで最低回転数が上がっていることがあるので、測る直前に読む
        var profileFans = Config.FindProfile(_controller.EffectiveProfile)?.Fans ?? new();
        double minDuty = step.Fans.Select(f => profileFans.FirstOrDefault(p => p.ControlId == f.ControlId)?.MinDuty ?? 25).DefaultIfEmpty(25).Max();
        _tuneMinDuties[step.Kind] = minDuty;
        if (isGpu) _gpuLoad = new GpuStressLoad();
        var list = step.Fans.ToList();
        _tuner = new AutoTuner(_controller.Engine, list, sensorId, FindSensorOption(sensorId)?.Name ?? sensorId, loadId,
            TargetFor(step.Kind), isGpu ? GpuAbortTemp : CpuAbortTemp(), minDuty, _gpuLoad);
        TestChart.Store = _history;
        TestChart.Series = new List<ChartSeries> { new() { Key = sensorId, Name = isGpu ? "GPU" : "CPU", Brush = Palette.Series[0] } }
            .Concat(list.Select((f, i) => new ChartSeries { Key = HistoryStore.RpmKey(f.ControlId), Name = f.Name, IsRpm = true, Brush = Palette.Series[(i + 2) % Palette.Series.Length] }))
            .ToList();
        Logger.Info($"自動セットアップ: {(isGpu ? "GPU" : "CPU")} の測定を開始（目標 {TargetFor(step.Kind):F0}°C・{string.Join(", ", list.Select(f => f.Name))}）");
    }

    /// <summary>「2 / 4: 」のような、今が何番目の手順か</summary>
    private string StepPrefix() => _setupSteps is { Count: > 1 } s ? $"{_setupIndex + 1} / {s.Count}: " : "";

    /// <summary>全体の進み具合（今の手順の中の進み具合 0〜1 から）。手順ごとの目安時間で重みを付ける</summary>
    private void UpdateSetupProgress(double within)
    {
        if (_setupSteps is null) return;
        double Sec(SetupStep s) => s.Kind == SetupStepKind.Calibrate ? AutoSetupPlan.CalibrateSec : AutoSetupPlan.TuneSec;
        double total = _setupSteps.Sum(Sec);
        double done = _setupSteps.Take(_setupIndex).Sum(Sec) + (_setupIndex < _setupSteps.Count ? Sec(_setupSteps[_setupIndex]) * Math.Clamp(within, 0, 1) : 0);
        TuneProgress.Value = total > 0 ? done / total * 100 : 0;
    }

    private SetupStep? CurrentSetupStep => _setupSteps is not null && _setupIndex < _setupSteps.Count ? _setupSteps[_setupIndex] : null;

    /// <summary>キャリブレーションの進み具合（自動セットアップの途中なら、全体の進み具合にする）</summary>
    private void OnSetupCalibrationProgress(CalibrationProgress p)
    {
        if (CurrentSetupStep is not { Kind: SetupStepKind.Calibrate } step || step.Fans[0].ControlId != p.ControlId) return;
        UpdateSetupProgress(p.Progress);
        TuneStatus.Text = StepPrefix() + L($"「{step.Fans[0].Name}」の特性を測っています: {p.Text}");
    }

    /// <summary>キャリブレーションが終わった（自動セットアップの途中なら、最低回転数を安全な値にして次へ）</summary>
    private void OnSetupCalibrationFinished(string controlId, CalibrationResult? result, string message)
    {
        if (CurrentSetupStep is not { Kind: SetupStepKind.Calibrate } step || step.Fans[0].ControlId != controlId) return;
        string name = step.Fans[0].Name;
        if (result is null) _setupNotes.Add(L($"「{name}」の特性を測れませんでした: {message}"));
        else
        {
            // 止まる直前より 5% 上を最低回転数にする（今の値のほうが高ければそのまま）
            double floor = Math.Min(60, result.StopDuty + 5);
            int raised = 0;
            foreach (var f in Config.Profiles.SelectMany(p => p.Fans).Where(f => f.ControlId == controlId && f.MinDuty < floor))
            {
                f.MinDuty = floor;
                raised++;
            }
            _setupNotes.Add(raised > 0
                ? L($"「{name}」: {result.StopDuty:F0}% 未満で止まるため、最低回転数を {floor:F0}% にしました")
                : L($"「{name}」: 特性を測りました（{result.StopDuty:F0}% 未満で停止・最大 {result.MaxRpm:N0} rpm）"));
            if (raised > 0) ScheduleCommit();
        }
        _setupIndex++;
        StartSetupStep();
    }

    private void OnCancelTuneClick(object sender, RoutedEventArgs e)
    {
        if (!SetupRunning) return;
        if (CurrentSetupStep?.Kind == SetupStepKind.Calibrate) _controller.Engine.CancelCalibration();
        _tuner?.Abort(L("ユーザーが中止しました"));
        if (_tuner is not null) CollectTuneResult();
        FinishSetup(cancelled: true);
    }

    private void UpdateTune(EngineSnapshot s)
    {
        if (_tuner is null) return;
        _tuner.OnSnapshot(s);
        UpdateSetupProgress(_tuner.Progress);
        TuneStatus.Text = StepPrefix() + (CurrentSetupStep?.Kind == SetupStepKind.TuneGpu ? "GPU: " : "CPU: ") + _tuner.StatusText;
        TuneLive.Text = _tuner.Current is AutoTuner.Phase.Warmup or AutoTuner.Phase.Steps
            ? L($"現在 {_tuner.LastTemp:F0}°C ／ ファン {_tuner.CurrentDuty:F0}%")
            : L($"現在 {_tuner.LastTemp:F0}°C");
        TestChart.Range = (s.Time - _tuner.StartedAt) + TimeSpan.FromSeconds(10);
        if (TestPage.Visibility == Visibility.Visible) TestChart.Refresh();
        if (_tuner.Current == AutoTuner.Phase.Done)
        {
            CollectTuneResult();
            _setupIndex++;
            StartSetupStep();
        }
    }

    /// <summary>終わった（中止した）測定の結果を受け取り、負荷を片付ける</summary>
    private void CollectTuneResult()
    {
        if (_tuner is null) return;
        var kind = CurrentSetupStep?.Kind ?? SetupStepKind.TuneCpu;
        var result = _tuner.Result;
        _tuner.Dispose();
        _tuner = null;
        _gpuLoad?.Dispose();
        _gpuLoad = null;
        if (result is null) return;
        string what = kind == SetupStepKind.TuneGpu ? "GPU" : "CPU";
        Logger.Info($"自動セットアップ: {what} の結果\n" + result.ToText(TargetFor(kind)));
        if (result.Usable) _tuneResults[kind] = result;
        if (result.AbortReason is not null)
            _setupNotes.Add(result.Usable ? L($"{what}: 途中で終わったため、測れた段だけでカーブを作りました（{result.AbortReason}）") : $"{what}: {result.AbortReason}");
    }

    private void FinishSetup(bool cancelled)
    {
        if (_setupSteps is null) return;
        _setupSteps = null;
        _calibratingId = null;
        StartTuneButton.Visibility = Visibility.Visible;
        CancelTuneButton.Visibility = Visibility.Collapsed;
        StartTestButton.IsEnabled = true;
        TuneSettings.IsEnabled = true;
        TuneProgress.Value = 100;
        TuneLive.Text = "";
        RebuildTuneFans(); // 特性を測ったファンの「未測定」を消す
        string notes = _setupNotes.Count > 0 ? "\n" + string.Join("\n", _setupNotes) : "";
        Logger.Info($"自動セットアップ: {(cancelled ? "中止" : "完了")}{notes}");

        if (_tuneResults.Count == 0)
        {
            TuneResultPanel.Visibility = Visibility.Collapsed;
            TuneStatus.Text = (cancelled ? L("中止しました。いつもの制御に戻しました。") : L("カーブを作れるだけの測定ができませんでした。")) + notes;
            if (!cancelled) ShowAlert(L("自動セットアップ: カーブを作れるだけの測定ができませんでした。") + notes, AlertLevel.Warning);
            return;
        }
        TuneStatus.Text = (cancelled ? L("中止しました。測れた分だけで作ったカーブを確かめて、適用するか選んでください。")
                                     : L("測定が終わりました。カーブを確かめて、適用するプロファイルを選んでください。")) + notes;
        bool both = _tuneResults.ContainsKey(SetupStepKind.TuneCpu) && _tuneResults.ContainsKey(SetupStepKind.TuneGpu);
        TuneResultSwitch.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
        if (both || _tuneResults.ContainsKey(SetupStepKind.TuneCpu)) TuneResultCpu.IsChecked = true;
        else TuneResultGpu.IsChecked = true;
        ShowTuneResult();
        ShowAlert(L("自動セットアップの測定が終わりました。結果のカーブを確かめて適用してください。"), AlertLevel.Info, L("結果を見る"), () => NavTest.IsChecked = true);
    }

    // =====================================================================
    // 季節: 室温が上がったら、測り直しを勧める
    // =====================================================================

    private readonly SeasonCheck _season = new();
    private DateTime _seasonLastFeed = DateTime.MinValue, _seasonNextCheck = DateTime.Now.AddMinutes(10);

    private void UpdateSeasonCheck(EngineSnapshot s)
    {
        if (SetupRunning || _test is not null) return;   // 測っている間の温度は数えない
        double dt = _seasonLastFeed == DateTime.MinValue ? 1 : (s.Time - _seasonLastFeed).TotalSeconds;
        _seasonLastFeed = s.Time;
        double? Read(string role) => _roles.TryGetValue(role, out var id) && s.Values.TryGetValue(id, out var v) && v is float f ? f : null;
        _season.Feed(Read(StatRoles.CpuTemp), Read(StatRoles.CpuLoad), Read(StatRoles.Ambient), dt);
        if (DateTime.Now < _seasonNextCheck) return;
        _seasonNextCheck = DateTime.Now.AddHours(1);
        var b = Config.SetupBaseline;
        // 6.11.1 より前に自動セットアップのカーブを入れた人は、測ったときの様子が残っていない: 今の様子を基準にする
        if (b is null && HasAutoTunedCurves() && _season.TrySeed(DateTime.Now) is { } seeded)
        {
            Config.SetupBaseline = seeded;
            ScheduleCommit();
            Logger.Info($"季節: 自動セットアップのカーブの基準を、今の様子にしました（CPU の休んでいるとき {seeded.CpuIdleTemp:F1}°C・室温 {seeded.RoomTemp?.ToString("F1") ?? "-"}°C）");
            return;
        }
        var (remind, rise, byRoom) = _season.Check(b, DateTime.Now);
        if (!remind || b is null) return;
        b.RemindedAt = DateTime.Now;
        ScheduleCommit();
        Logger.Info($"季節: 自動セットアップのときより {(byRoom ? "室温" : "CPU の休んでいるときの温度")} が {rise:F1}°C 高いので、測り直しを勧めました");
        ShowAlert((byRoom ? L($"室温が、自動セットアップで測ったときより約 {rise:F0}°C 高くなっています。")
                          : L($"CPU が休んでいるときの温度が、自動セットアップで測ったときより約 {rise:F0}°C 高くなっています（室温が上がったようです）。"))
                  + L("もう一度測ると、今の季節でも静かで安全なカーブにできます（約 20〜30 分）。"),
            AlertLevel.Info, L("自動セットアップを開く"), () => NavTest.IsChecked = true);
    }

    /// <summary>自動セットアップで作ったカーブがあるか（名前で見る。日本語と今の言語の両方）</summary>
    private bool HasAutoTunedCurves()
    {
        var names = new HashSet<string> { "CPU（自動調整）", "GPU（自動調整）", L("CPU（自動調整）"), L("GPU（自動調整）") };   // no-loc（探す言葉: 日本語で保存された名前も見る）
        return Config.Profiles.SelectMany(p => p.Fans).SelectMany(f => f.Curves).Any(c => names.Contains(c.Name));
    }

    /// <summary>自動セットアップの結果を適用したとき: 測ったときの様子を覚える（季節の確かめに使う）</summary>
    private void RememberSetupBaseline()
    {
        double? room = null;
        if (_lastSnapshot is { } s && _roles.TryGetValue(StatRoles.Ambient, out var id) && s.Values.TryGetValue(id, out var v) && v is float f) room = f;
        Config.SetupBaseline = new SetupBaselineConfig
        {
            AppliedAt = DateTime.Now,
            CpuIdleTemp = _tuneResults.TryGetValue(SetupStepKind.TuneCpu, out var cpu) ? cpu.IdleTemp : null,
            RoomTemp = room,
        };
    }

    // =====================================================================
    // 結果
    // =====================================================================

    private List<CurvePoint> TuneCurveFor(SetupStepKind kind, double target)
        => AutoTuner.BuildCurve(_tuneResults[kind].Steps, _tuneResults[kind].IdleTemp, target, _tuneMinDuties.GetValueOrDefault(kind, 25));

    private void ShowTuneResult()
    {
        var kind = ShownResult;
        if (!_tuneResults.TryGetValue(kind, out var r))
        {
            kind = _tuneResults.Keys.First();
            r = _tuneResults[kind];
        }
        double target = TargetFor(kind);
        double minDuty = _tuneMinDuties.GetValueOrDefault(kind, 25);
        TuneResultPanel.Visibility = Visibility.Visible;
        TuneIdleText.Text = L($"{r.SensorName} ・ 待機時 {r.IdleTemp:F1}°C ・ 全力で負荷をかけたとき:");
        TuneSteps.ItemsSource = r.Steps.Select(st => new TuneStepVm { Step = st, Target = target }).ToList();
        var (need, insufficient) = AutoTuner.RequiredDuty(r.Steps, target);
        TuneNeedText.Text = insufficient
            ? L($"全開でも {target:F0}°C は守れません（冷却の強化を検討してください）")
            : L($"{target:F0}°C を守るのに必要な回転数: 約 {Math.Max(need, minDuty):F0}%");
        TuneNeedText.Foreground = insufficient ? Palette.Danger : Palette.Ok;
        TuneAbortText.Text = r.AbortReason is null ? "" : L($"※ 途中で終わったため、測れた段だけで作っています（{r.AbortReason}）");
        TuneCurve.Points = TuneCurveFor(kind, target);
        TuneCurve.Calibration = Config.FindCalibration(r.Fans[0].ControlId);
        TuneCurve.SetRange(20, 100);
    }

    private void OnApplyTuneClick(object sender, RoutedEventArgs e)
    {
        if (_tuneResults.Count == 0 || TuneProfileCombo.SelectedItem is not ProfileConfig selected) return;
        // 「元に戻す」・読み込みで設定が丸ごと入れ替わっていても、名前で今の設定のプロファイルを探し直す
        if (Config.FindProfile(selected.Name) is not { } profile)
        {
            ShowAlert(L($"プロファイル「{selected.Name}」が見つかりません。選び直してください。"), AlertLevel.Warning);
            return;
        }
        FlushCommit();
        int n = 0;
        foreach (var kind in _tuneResults.Keys) n += ApplyTune(profile, kind, TargetFor(kind));
        RememberSetupBaseline();
        _controller.Commit();
        ShowAlert(L($"「{profile.Name}」に、自動で作ったカーブを {n} 本入れました（「元に戻す」で戻せます）。"), AlertLevel.Info,
            L("カーブを見る"), () => { NavCurves.IsChecked = true; SelectEditProfile(profile.Name, _tuneResults.Values.First().Fans[0].ControlId, 0); });
    }

    private void OnApplyTuneAllClick(object sender, RoutedEventArgs e)
    {
        if (_tuneResults.Count == 0) return;
        FlushCommit();
        var lines = new List<string>();
        foreach (var p in Config.Profiles)
        {
            double offset = ProfileRoles.TargetOffset(p.Role);
            foreach (var kind in _tuneResults.Keys) ApplyTune(p, kind, TargetFor(kind) + offset);
            lines.Add(string.Join(" ", new[] { $"{p.Name}" }
                .Concat(_tuneResults.Keys.OrderBy(k => k).Select(k => $"{(k == SetupStepKind.TuneGpu ? "GPU" : "CPU")} {TargetFor(k) + offset:F0}°C"))));
        }
        RememberSetupBaseline();
        _controller.Commit();
        ShowAlert(L($"全プロファイルに適用しました（目標: {string.Join(L(" ／ "), lines)}）。「元に戻す」で戻せます。"), AlertLevel.Info);
    }

    /// <summary>
    /// そのプロファイルの測ったファンに、カーブを入れる。測った温度と同じ種類のセンサーのカーブがあれば置き換え、
    /// なければ追加する。目標温度モードなどのファンはカーブモードに切り替える。入れたカーブの本数を返す
    /// </summary>
    private int ApplyTune(ProfileConfig profile, SetupStepKind kindOfRun, double target)
    {
        var r = _tuneResults[kindOfRun];
        var kind = FindSensorOption(r.SensorId)?.Kind ?? (kindOfRun == SetupStepKind.TuneGpu ? SensorKind.Gpu : SensorKind.Cpu);
        int count = 0;
        foreach (var (controlId, _) in r.Fans)
        {
            var fan = profile.Fans.FirstOrDefault(f => f.ControlId == controlId);
            if (fan is null) continue;
            var curve = fan.Curves.FirstOrDefault(c => c.SensorId == r.SensorId)
                        ?? fan.Curves.FirstOrDefault(c => (FindSensorOption(c.SensorId)?.Kind ?? SensorKind.Other) is var k
                                                          && (k == kind || (kind == SensorKind.Gpu && k == SensorKind.GpuHotspot)));
            if (curve is null)
            {
                curve = new CurveConfig { Name = kind == SensorKind.Cpu ? L("CPU（自動調整）") : L("GPU（自動調整）"), HysteresisCelsius = 4, EmaAlpha = 0.15 };
                fan.Curves.Add(curve);
            }
            curve.SensorId = r.SensorId;
            curve.Points = TuneCurveFor(kindOfRun, target);
            fan.Mode = FanModes.Curve;
            count++;
        }
        WatchConfig();
        if (SelectedCurve is { } c2) ShowCurve(c2);
        return count;
    }
}

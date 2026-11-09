// ==================================================
// MainWindow.AutoTune.cs - おまかせ調整の画面 (測定の開始・進捗・結果のカーブのプレビューとプロファイルへの適用)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using ProFanController.UI;
using ProFanController.UI.Controls;

namespace ProFanController;

/// <summary>おまかせ調整で選ぶファン</summary>
public sealed class TuneFanVm : ObservableObject
{
    private bool _selected;
    public required string ControlId { get; init; }
    public required string Name { get; init; }
    /// <summary>今つながっている温度（例: 「CPU・GPU」）</summary>
    public string Hint { get; init; } = "";
    public SensorKind[] Kinds { get; init; } = Array.Empty<SensorKind>();
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
    private AutoTuner? _tuner;
    private AutoTuneResult? _tuneResult;
    private double _tuneMinDuty = 25;
    private string? _tunePreselect;

    private bool TuneIsGpu => TuneGpu.IsChecked == true;

    /// <summary>実行中のプロファイルのファン（ポンプ以外）を一覧にする。冷やす対象に合うファンを最初から選んでおく</summary>
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
            string hint = string.Join(Localization.ListSeparator, kinds.Where(k => k is SensorKind.Cpu or SensorKind.Gpu or SensorKind.GpuHotspot)
                .Select(k => k == SensorKind.Cpu ? "CPU" : "GPU").Distinct());
            _tuneFans.Add(new TuneFanVm
            {
                ControlId = fan.ControlId,
                Name = fan.Name,
                Hint = hint.Length > 0 ? L($"（{hint} 連動）") : "",
                Kinds = kinds,
                Selected = _tunePreselect is not null ? fan.ControlId == _tunePreselect
                    : hadSelection ? keep.Contains(fan.ControlId) : MatchesTarget(kinds),
            });
        }
        _tunePreselect = null;
        TuneProfileCombo.ItemsSource = Config.Profiles;
        TuneProfileCombo.SelectedItem = Config.FindProfile(_controller.EffectiveProfile);
    }

    private bool MatchesTarget(SensorKind[] kinds)
        => TuneIsGpu ? kinds.Any(k => k is SensorKind.Gpu or SensorKind.GpuHotspot) : kinds.Contains(SensorKind.Cpu);

    private void OnTuneTargetChanged(object sender, RoutedEventArgs e)
    {
        if (TuneTargetSlider is null) return;
        TuneTargetSlider.Value = TuneIsGpu ? 75 : 80;
        foreach (var f in _tuneFans) f.Selected = MatchesTarget(f.Kinds);
    }

    private void OnTuneTargetValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TuneTargetText is null) return;
        TuneTargetText.Text = $"{e.NewValue:F0}°C";
        if (_tuneResult is not null) ShowTuneResult();
    }

    /// <summary>ファンカーブ画面の「自動で作る…」: そのファンを選んだ状態で開く</summary>
    private void OnGoAutoTuneClick(object sender, RoutedEventArgs e)
    {
        if (SelectedFan is { } fan)
        {
            var kind = SelectedCurve is { } c ? FindSensorOption(c.SensorId)?.Kind : null;
            if (kind is SensorKind.Gpu or SensorKind.GpuHotspot) TuneGpu.IsChecked = true; else TuneCpu.IsChecked = true;
            _tunePreselect = fan.ControlId;
        }
        RebuildTuneFans();
        NavTest.IsChecked = true;
    }

    private void OnStartTuneClick(object sender, RoutedEventArgs e)
    {
        if (_catalog is null || _tuner is not null) return;
        if (_test is not null || _calibratingId is not null)
        {
            MessageBox.Show(this, L("冷却テストかキャリブレーションが実行中です。終わってから始めてください。"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var fans = _tuneFans.Where(f => f.Selected).ToList();
        if (fans.Count == 0)
        {
            MessageBox.Show(this, L("調整するファンを 1 つ以上選んでください。"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _roles = ResolveRoles();
        string role = TuneIsGpu ? StatRoles.GpuTemp : StatRoles.CpuTemp;
        if (!_roles.TryGetValue(role, out var sensorId))
        {
            MessageBox.Show(this, TuneIsGpu ? L("GPU の温度センサーが見つかりません。") : L("CPU の温度センサーが見つかりません。"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string? loadId = null;
        if (TuneIsGpu && !_roles.TryGetValue(StatRoles.GpuLoad, out loadId))
        {
            MessageBox.Show(this, L("GPU の使用率センサーが見つからないため、GPU の調整はできません。"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        double target = TuneTargetSlider.Value;
        var profileFans = Config.FindProfile(_controller.EffectiveProfile)?.Fans ?? new();
        _tuneMinDuty = fans.Select(f => profileFans.FirstOrDefault(p => p.ControlId == f.ControlId)?.MinDuty ?? 25).DefaultIfEmpty(25).Max();
        double abort = TuneIsGpu ? 90 : Math.Min(Config.EmergencyTemperature - 3, 97);

        string how = TuneIsGpu
            ? L("・開始したら、ゲームやベンチマーク（3DMark・FF ベンチなど）を起動して、GPU に負荷をかけ続けてください。\n")
            : L("・CPU に全力で負荷をかけます（ほかの重いアプリは閉じてください）。\n");
        var answer = MessageBox.Show(this,
            L($"おまかせ調整を始めます（約 {(int)Math.Ceiling(TimeSpan.FromSeconds(80 * 6 + 120).TotalMinutes)} 分）。\n\n") + how +
            L($"・選んだ {fans.Count} 個のファンを 100% から少しずつ下げていきます。音が大きくなったり小さくなったりします。\n") +
            L($"・{(TuneIsGpu ? "GPU" : "CPU")} の温度が {abort:F0}°C に達したら、すぐに中止していつもの制御に戻します。\n") +
            L("・結果は確認してから適用するので、途中で中止しても設定は変わりません。"),
            L("おまかせ調整"), MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK) return;

        var list = fans.Select(f => (f.ControlId, f.Name)).ToList();
        _tuner = new AutoTuner(_controller.Engine, list, sensorId, FindSensorOption(sensorId)?.Name ?? sensorId, loadId, target, abort, _tuneMinDuty);
        _tuneResult = null;
        TestChart.Store = _history;
        TestChart.Series = new List<ChartSeries> { new() { Key = sensorId, Name = TuneIsGpu ? "GPU" : "CPU", Brush = Palette.Series[0] } }
            .Concat(list.Select((f, i) => new ChartSeries { Key = HistoryStore.RpmKey(f.ControlId), Name = f.Name, IsRpm = true, Brush = Palette.Series[(i + 2) % Palette.Series.Length] }))
            .ToList();
        StartTuneButton.Visibility = Visibility.Collapsed;
        CancelTuneButton.Visibility = Visibility.Visible;
        StartTestButton.IsEnabled = false;
        TuneSettings.IsEnabled = false;
        TuneProgress.Visibility = Visibility.Visible;
        TuneProgress.Value = 0;
        TuneResultPanel.Visibility = Visibility.Collapsed;
        Logger.Info($"おまかせ調整を開始（{(TuneIsGpu ? "GPU" : "CPU")}・目標 {target:F0}°C・{string.Join(", ", list.Select(f => f.Name))}）");
    }

    private void OnCancelTuneClick(object sender, RoutedEventArgs e)
    {
        _tuner?.Abort(L("ユーザーが中止しました"));
        FinishTune();
    }

    private void UpdateTune(EngineSnapshot s)
    {
        if (_tuner is null) return;
        _tuner.OnSnapshot(s);
        TuneProgress.Value = Math.Clamp(_tuner.Progress * 100, 0, 100);
        TuneStatus.Text = _tuner.StatusText;
        TuneLive.Text = _tuner.Current is AutoTuner.Phase.Warmup or AutoTuner.Phase.Steps
            ? L($"現在 {_tuner.LastTemp:F0}°C ／ ファン {_tuner.CurrentDuty:F0}%")
            : L($"現在 {_tuner.LastTemp:F0}°C");
        TestChart.Range = (s.Time - _tuner.StartedAt) + TimeSpan.FromSeconds(10);
        if (TestPage.Visibility == Visibility.Visible) TestChart.Refresh();
        if (_tuner.Current == AutoTuner.Phase.Done) FinishTune();
    }

    private void FinishTune()
    {
        if (_tuner is null) return;
        _tuneResult = _tuner.Result;
        _tuner.Dispose();
        _tuner = null;
        StartTuneButton.Visibility = Visibility.Visible;
        CancelTuneButton.Visibility = Visibility.Collapsed;
        StartTestButton.IsEnabled = true;
        TuneSettings.IsEnabled = true;
        TuneProgress.Value = 100;
        if (_tuneResult is null) return;

        TuneStatus.Text = _tuneResult.AbortReason is null ? L("測定が終わりました。カーブを確認して、適用するプロファイルを選んでください。") : L($"中断: {_tuneResult.AbortReason}");
        Logger.Info("おまかせ調整の結果\n" + _tuneResult.ToText(TuneTargetSlider.Value));
        if (!_tuneResult.Usable)
        {
            TuneResultPanel.Visibility = Visibility.Collapsed;
            ShowAlert(L("おまかせ調整: 測定できた段が少ないため、カーブを作れませんでした。") + (_tuneResult.AbortReason ?? ""), AlertLevel.Warning);
            return;
        }
        ShowTuneResult();
        ShowAlert(L("おまかせ調整の測定が終わりました。結果のカーブを確認して適用してください。"), AlertLevel.Info, L("結果を見る"), () => NavTest.IsChecked = true);
    }

    private List<CurvePoint> TuneCurveFor(double target)
        => AutoTuner.BuildCurve(_tuneResult!.Steps, _tuneResult.IdleTemp, target, _tuneMinDuty);

    private void ShowTuneResult()
    {
        if (_tuneResult is null) return;
        double target = TuneTargetSlider.Value;
        TuneResultPanel.Visibility = Visibility.Visible;
        TuneIdleText.Text = L($"{_tuneResult.SensorName} ・ 待機時 {_tuneResult.IdleTemp:F1}°C ・ 全力で負荷をかけたとき:");
        TuneSteps.ItemsSource = _tuneResult.Steps.Select(st => new TuneStepVm { Step = st, Target = target }).ToList();
        var (need, insufficient) = AutoTuner.RequiredDuty(_tuneResult.Steps, target);
        TuneNeedText.Text = insufficient
            ? L($"全開でも {target:F0}°C は守れません（冷却の強化を検討してください）")
            : L($"{target:F0}°C を守るのに必要な回転数: 約 {Math.Max(need, _tuneMinDuty):F0}%");
        TuneNeedText.Foreground = insufficient ? Palette.Danger : Palette.Ok;
        TuneAbortText.Text = _tuneResult.AbortReason is null ? "" : L($"※ 途中で終わったため、測れた段だけで作っています（{_tuneResult.AbortReason}）");
        TuneCurve.Points = TuneCurveFor(target);
        TuneCurve.Calibration = Config.FindCalibration(_tuneResult.Fans[0].ControlId);
        TuneCurve.SetRange(20, 100);
    }

    private void OnApplyTuneClick(object sender, RoutedEventArgs e)
    {
        if (_tuneResult is null || TuneProfileCombo.SelectedItem is not ProfileConfig selected) return;
        // 「元に戻す」・読み込みで設定が丸ごと入れ替わっていても、名前で今の設定のプロファイルを探し直す
        if (Config.FindProfile(selected.Name) is not { } profile)
        {
            ShowAlert(L($"プロファイル「{selected.Name}」が見つかりません。選び直してください。"), AlertLevel.Warning);
            return;
        }
        FlushCommit();
        int n = ApplyTune(profile, TuneTargetSlider.Value);
        _controller.Commit();
        ShowAlert(L($"「{profile.Name}」の {n} 個のファンに、自動で作ったカーブを適用しました（「元に戻す」で戻せます）。"), AlertLevel.Info,
            L("カーブを見る"), () => { NavCurves.IsChecked = true; SelectEditProfile(profile.Name, _tuneResult?.Fans[0].ControlId, 0); });
    }

    private void OnApplyTuneAllClick(object sender, RoutedEventArgs e)
    {
        if (_tuneResult is null) return;
        FlushCommit();
        double target = TuneTargetSlider.Value;
        var lines = new List<string>();
        foreach (var p in Config.Profiles)
        {
            double t = target + ProfileRoles.TargetOffset(p.Role);
            ApplyTune(p, t);
            lines.Add($"{p.Name} {t:F0}°C");
        }
        _controller.Commit();
        ShowAlert(L($"全プロファイルに適用しました（目標: {string.Join(L(" ／ "), lines)}）。「元に戻す」で戻せます。"), AlertLevel.Info);
    }

    /// <summary>
    /// そのプロファイルの選んだファンに、カーブを入れる。測った温度と同じ種類のセンサーのカーブがあれば置き換え、
    /// なければ追加する。目標温度モードなどのファンはカーブモードに切り替える
    /// </summary>
    private int ApplyTune(ProfileConfig profile, double target)
    {
        var r = _tuneResult!;
        var kind = FindSensorOption(r.SensorId)?.Kind ?? SensorKind.Cpu;
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
            curve.Points = TuneCurveFor(target);
            fan.Mode = FanModes.Curve;
            count++;
        }
        WatchConfig();
        if (SelectedCurve is { } c2) ShowCurve(c2);
        return count;
    }
}

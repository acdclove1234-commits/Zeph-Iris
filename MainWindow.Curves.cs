// ==================================================
// MainWindow.Curves.cs - ファンカーブ画面 (プロファイル別編集・プリセット・コピー・数値入力・キャリブレーション・
//                        共振回避帯と回転数の試聴)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ProFanController.UI;

namespace ProFanController;

public partial class MainWindow
{
    private string? _calibratingId;

    private ProfileConfig? EditProfile => EditProfileCombo.SelectedItem as ProfileConfig;
    private FanConfig? SelectedFan => FanList.SelectedItem as FanConfig;
    private CurveConfig? SelectedCurve => CurveTabs.SelectedItem as CurveConfig;

    /// <summary>編集するプロファイルとファン・カーブを選ぶ（コードから選択を復元するとき用）</summary>
    private void SelectEditProfile(string profileName, string? controlId, int curveIndex)
    {
        var profile = Config.FindProfile(profileName) ?? Config.Profiles.FirstOrDefault();
        _syncing = true;
        EditProfileCombo.SelectedItem = profile;
        _syncing = false;
        ShowProfileFans(profile);

        if (profile is null) return;
        var fan = controlId is null ? null : profile.Fans.FirstOrDefault(f => string.Equals(f.ControlId, controlId, StringComparison.OrdinalIgnoreCase));
        FanList.SelectedItem = fan ?? profile.Fans.FirstOrDefault();
        if (SelectedFan is { } f && curveIndex >= 0 && curveIndex < f.Curves.Count) CurveTabs.SelectedIndex = curveIndex;
    }

    private void OnEditProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        // 同じファンを開いたまま、別のプロファイルの設定に切り替える
        string? controlId = SelectedFan?.ControlId;
        int curveIndex = CurveTabs.SelectedIndex;
        ShowProfileFans(EditProfile);
        if (EditProfile is null) return;
        FanList.SelectedItem = EditProfile.Fans.FirstOrDefault(f => f.ControlId == controlId) ?? EditProfile.Fans.FirstOrDefault();
        if (SelectedFan is { } fan && curveIndex >= 0 && curveIndex < fan.Curves.Count) CurveTabs.SelectedIndex = curveIndex;
    }

    private void ShowProfileFans(ProfileConfig? profile)
    {
        FanList.ItemsSource = profile?.Fans;
        if (profile is null || profile.Fans.Count == 0)
        {
            FanEditor.Visibility = Visibility.Collapsed;
            CurvesPlaceholder.Visibility = Visibility.Visible;
            CurvesPlaceholderText.Text = L("左下の「端子を追加」からファンを登録するか、設定画面のセットアップウィザードを使ってください");
        }
    }

    private void OnFanSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopListening();
        var fan = SelectedFan;
        // 連動の相手は、同じプロファイルの自分以外のファン（DataContext より先に候補を入れておく）
        FollowCombo.ItemsSource = fan is null ? null : EditProfile?.Fans.Where(f => f != fan && !f.IsPump).ToList();
        FanEditor.DataContext = fan;
        FanEditor.Visibility = fan is null ? Visibility.Collapsed : Visibility.Visible;
        CurvesPlaceholder.Visibility = fan is null ? Visibility.Visible : Visibility.Collapsed;
        if (fan is null)
        {
            CurvesPlaceholderText.Text = L("左の一覧からファンを選ぶか、端子を追加してください");
            return;
        }
        CurveTabs.SelectedIndex = fan.Curves.Count > 0 ? 0 : -1;
        ShowCurve(SelectedCurve);
        UpdateCalibrationPanel();
        UpdateBandPanel();
        if (_lastSnapshot?.Fans.FirstOrDefault(f => f.ControlId == fan.ControlId)?.Duty is double duty)
        {
            _syncing = true;
            ListenSlider.Value = Math.Round(duty);
            _syncing = false;
        }
    }

    private void OnCurveSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowCurve(SelectedCurve);

    private void ShowCurve(CurveConfig? curve)
    {
        CurveEditorPanel.DataContext = curve;
        CurveEditorPanel.Visibility = curve is null ? Visibility.Collapsed : Visibility.Visible;
        CurveGraph.Points = curve?.Points;
        CurveGraph.LiveTemp = null;
        CurveGraph.Calibration = SelectedFan is { } fan ? Config.FindCalibration(fan.ControlId) : null;
        CurveGraph.AvoidBands = SelectedFan?.AvoidBands;
        PointsList.ItemsSource = curve?.Points;
        if (curve is not null) UpdateCurveRange(curve);
    }

    /// <summary>差分センサーは 0〜30°C、それ以外は 20〜100°C を横軸にする</summary>
    private void UpdateCurveRange(CurveConfig curve)
    {
        bool delta = FindSensorOption(curve.SensorId)?.Kind == SensorKind.Delta;
        if (delta) CurveGraph.SetRange(0, 30); else CurveGraph.SetRange(20, 100);
    }

    private void UpdateCurveLive(EngineSnapshot s)
    {
        if (CurvesPage.Visibility != Visibility.Visible || SelectedFan is not { } fan || CurveTabs.SelectedIndex < 0) return;
        // 編集中のプロファイルが実際に動いているときだけ動作点を出す
        var status = s.Profile == EditProfile?.Name ? s.Fans.FirstOrDefault(f => f.ControlId == fan.ControlId) : null;
        CurveGraph.LiveTemp = status is not null && CurveTabs.SelectedIndex < status.Curves.Count
            ? status.Curves[CurveTabs.SelectedIndex].EffectiveTemp
            : null;
    }

    private void OnCurvePointsChanged(object? sender, EventArgs e)
    {
        PointsList.Items.Refresh();
        WatchConfig(); // 追加された点も監視する
        ScheduleCommit();
    }

    private void OnPointEdited(object sender, RoutedEventArgs e)
    {
        // 数値入力で並び順が変わることがあるので、確定時に並べ直す
        if (SelectedCurve is not { } curve) return;
        foreach (var p in curve.Points)
        {
            p.Duty = Math.Clamp(p.Duty, 0, 100);
        }
        curve.SortPoints();
        CurveGraph.Points = curve.Points;
        ScheduleCommit();
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string preset } || SelectedCurve is not { } curve) return;
        var kind = FindSensorOption(curve.SensorId)?.Kind ?? SensorKind.Cpu;
        curve.Points = CurvePresets.Create(preset, kind);
        ShowCurve(curve);
        WatchConfig();
        ScheduleCommit();
    }

    private void OnCopyCurveClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCurve is not { } curve || SelectedFan is not { } fan || EditProfile is not { } profile) return;

        var menu = new ContextMenu { PlacementTarget = CopyCurveButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var target in profile.Fans.Where(f => f != fan && !f.IsPump))
        {
            var item = new MenuItem { Header = L($"「{target.Name}」にコピー") };
            item.Click += (_, _) => CopyCurveTo(curve, new[] { target });
            menu.Items.Add(item);
        }
        var others = Config.Profiles.Where(p => p != profile)
            .Select(p => p.Fans.FirstOrDefault(f => f.ControlId == fan.ControlId)).OfType<FanConfig>().ToList();
        if (others.Count > 0)
        {
            var all = new MenuItem { Header = L("他のプロファイルの同じファンにコピー") };
            all.Click += (_, _) => CopyCurveTo(curve, others);
            menu.Items.Add(all);
        }
        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = L("コピー先がありません"), IsEnabled = false });
        }
        menu.IsOpen = true;
    }

    /// <summary>同じ名前のカーブがあれば置き換え、なければ追加する</summary>
    private void CopyCurveTo(CurveConfig curve, IEnumerable<FanConfig> targets)
    {
        foreach (var target in targets)
        {
            var copy = curve.DeepCopy();
            int i = target.Curves.ToList().FindIndex(c => c.Name == curve.Name);
            if (i >= 0) target.Curves[i] = copy; else target.Curves.Add(copy);
        }
        WatchConfig();
        ScheduleCommit();
        ShowAlert(L($"カーブ「{curve.Name}」をコピーしました。"), AlertLevel.Info);
    }

    private string DefaultSensorId()
        => _tempCards.FirstOrDefault()?.SensorId ?? _catalog?.Temperatures.FirstOrDefault()?.Id ?? "";

    private void OnAddFanClick(object sender, RoutedEventArgs e)
    {
        if (AddControlCombo.SelectedItem is not ControlInfo ci) return;
        if (Config.Profiles.SelectMany(p => p.Fans).Any(f => string.Equals(f.ControlId, ci.Id, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, L("この端子はすでに追加されています。"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ci.IsGpu)
        {
            var ok = MessageBox.Show(this,
                L($"GPU のファン「{ci.Name}」を追加します（試験的な機能です）。\n\n") +
                L("・制御を始めると、GPU ドライバーの自動制御（アイドル時に止まる 0 RPM モードなど）が止まります。\n") +
                L($"・GPU 温度が {FanEngine.GpuGuardCoreTemp:F0}°C（Hot Spot {FanEngine.GpuGuardHotspotTemp:F0}°C）を超えると、設定に関係なく 100% にします。\n") +
                L("・追加した直後は「このツールで制御する」がオフ（今までどおり）です。カーブを確認してからオンにしてください。"),
                L("GPU のファン"), MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (ok != MessageBoxResult.OK) return;
        }

        // 端子は全プロファイル共通。追加直後は BIOS 制御のまま、カーブを確認してからスイッチを入れてもらう
        // GPU のファンは、その GPU の温度（Core）を最初のカーブにする
        var gpuTemp = ci.IsGpu && _catalog is not null ? SensorNames.GpuCoreTemperature(_catalog.Temperatures, ci.HardwareName)
                                 ?? _catalog?.Temperatures.FirstOrDefault(t => t.HardwareName == ci.HardwareName) : null;
        var sensor = gpuTemp?.Id ?? DefaultSensorId();
        var kind = FindSensorOption(sensor)?.Kind ?? SensorKind.Cpu;
        foreach (var profile in Config.Profiles)
        {
            var preset = ProfileRoles.Preset(profile.Role);
            var fan = new FanConfig { Name = ci.Name, ControlId = ci.Id, RpmSensorId = ci.RpmSensorId, Enabled = false, MinDuty = ci.IsGpu ? 30 : 25 };
            fan.Curves.Add(new CurveConfig { Name = ci.IsGpu ? "GPU" : L("CPU 連動"), SensorId = sensor, Points = CurvePresets.Create(preset, kind) });
            profile.Fans.Add(fan);
        }
        WatchConfig();
        ScheduleCommit();
        FanList.SelectedItem = EditProfile?.Fans.FirstOrDefault(f => f.ControlId == ci.Id);
    }

    private void OnRemoveFanClick(object sender, RoutedEventArgs e)
    {
        if (SelectedFan is not { } fan) return;
        var answer = MessageBox.Show(this, L($"「{fan.Name}」をすべてのプロファイルから削除しますか？\n削除した端子は BIOS 制御に戻ります。"), AppIdentity.Name,
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        foreach (var profile in Config.Profiles)
        {
            foreach (var f in profile.Fans.Where(f => f.ControlId == fan.ControlId).ToList()) profile.Fans.Remove(f);
        }
        WatchConfig();
        ScheduleCommit();
        FanList.SelectedIndex = EditProfile?.Fans.Count > 0 ? 0 : -1;
    }

    private void OnAddCurveClick(object sender, RoutedEventArgs e)
    {
        if (SelectedFan is not { } fan) return;
        var sensor = DefaultSensorId();
        var curve = new CurveConfig
        {
            Name = L($"カーブ {fan.Curves.Count + 1}"),
            SensorId = sensor,
            Points = CurvePresets.Create(CurvePresets.Balanced, FindSensorOption(sensor)?.Kind ?? SensorKind.Cpu),
        };
        fan.Curves.Add(curve);
        WatchConfig();
        ScheduleCommit();
        CurveTabs.SelectedItem = curve;
    }

    private void OnRemoveCurveClick(object sender, RoutedEventArgs e)
    {
        if (SelectedFan is not { } fan || SelectedCurve is not { } curve) return;
        if (fan.Curves.Count <= 1)
        {
            MessageBox.Show(this, L("カーブは最低 1 つ必要です。一定の回転にしたい場合は「固定回転にする」を使ってください。"),
                AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        fan.Curves.Remove(curve);
        WatchConfig();
        ScheduleCommit();
        CurveTabs.SelectedIndex = 0;
    }

    private void OnIdentifyClick(object sender, RoutedEventArgs e)
    {
        if (SelectedFan is not { } fan) return;
        // 端子が見つからない（配線を変えた・BIOS で無効にした など）ときは、押しても何も起きないので知らせる
        if (_catalog is not null && !_catalog.Controls.Any(c => string.Equals(c.Id, fan.ControlId, StringComparison.OrdinalIgnoreCase)))
        {
            ShowAlert(L($"「{fan.Name}」の端子が見つからないため、テスト（全開）できませんでした。配線や BIOS の設定を確かめてください。"), AlertLevel.Warning);
            return;
        }
        _controller.Engine.Identify(fan.ControlId);
    }

    private void OnIdentifyNewClick(object sender, RoutedEventArgs e)
    {
        if (AddControlCombo.SelectedItem is ControlInfo ci) _controller.Engine.Identify(ci.Id);
    }

    // ----- 自動キャリブレーション -----

    private void OnCalibrateClick(object sender, RoutedEventArgs e)
    {
        if (SelectedFan is not { } fan) return;
        if (_calibratingId is not null)
        {
            MessageBox.Show(this, L("ほかのファンのキャリブレーションが実行中です。終わってから始めてください。"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (SetupRunning || _test is not null)
        {
            MessageBox.Show(this, L("自動セットアップか冷却テストが実行中です。終わってから始めてください。"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // エンジンは実行中のプロファイルのファンしか扱わないため（結果は全プロファイル共通で使える）
        if (Config.FindProfile(_controller.EffectiveProfile)?.Fans.Any(f => f.ControlId == fan.ControlId) != true)
        {
            MessageBox.Show(this, L("このファンは実行中のプロファイルに含まれていません。"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(this,
            L($"「{fan.Name}」のキャリブレーションを開始します。\n\n") +
            L("・約 1〜2 分間、ファンが全開になったり止まったりします。\n") +
            L("・CPU/GPU に負荷をかけていない状態で実行してください（温度が 80°C を超えると自動で中止します）。"),
            L("ファンの特性を測る"), MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK) return;

        _calibratingId = fan.ControlId;
        _controller.Engine.StartCalibration(fan.ControlId);
        CalibrateButton.Visibility = Visibility.Collapsed;
        CancelCalibrationButton.Visibility = Visibility.Visible;
        CalibrationProgressBar.Visibility = Visibility.Visible;
        CalibrationProgressBar.Value = 0;
        CalibrationText.Text = L("開始しています…");
    }

    private void OnCancelCalibrationClick(object sender, RoutedEventArgs e) => _controller.Engine.CancelCalibration();

    public void OnCalibrationProgress(CalibrationProgress p)
    {
        _calibratingId = p.ControlId;
        OnSetupCalibrationProgress(p);
        if (SelectedFan?.ControlId != p.ControlId) return;
        CalibrateButton.Visibility = Visibility.Collapsed;
        CancelCalibrationButton.Visibility = Visibility.Visible;
        CalibrationProgressBar.Visibility = Visibility.Visible;
        CalibrationProgressBar.Value = p.Progress * 100;
        CalibrationText.Text = p.Text;
    }

    public void OnCalibrationFinished(string controlId, CalibrationResult? result, string message)
    {
        _calibratingId = null;
        if (result is not null)
        {
            var old = Config.FindCalibration(controlId);
            if (old is not null) Config.Calibrations.Remove(old);
            Config.Calibrations.Add(result);
            ScheduleCommit();
        }
        // 自動セットアップの途中は、1 台ずつ知らせず、終わったときにまとめて知らせる
        if (!SetupRunning) ShowAlert(message, result is null ? AlertLevel.Warning : AlertLevel.Info);
        UpdateCalibrationPanel();
        if (SelectedFan?.ControlId == controlId) CurveGraph.Calibration = result ?? Config.FindCalibration(controlId);
        OnSetupCalibrationFinished(controlId, result, message);
    }

    private void UpdateCalibrationPanel()
    {
        if (SelectedFan is not { } fan) return;
        bool running = _calibratingId == fan.ControlId;
        CalibrateButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        CancelCalibrationButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CalibrationProgressBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        var cal = Config.FindCalibration(fan.ControlId);
        ApplyCalibrationButton.Visibility = cal is not null && !running ? Visibility.Visible : Visibility.Collapsed;
        if (!running)
        {
            CalibrationText.Text = cal is null
                ? L("まだ測定していません。")
                : L($"最大 {cal.MaxRpm:N0} rpm ／ {cal.StopDuty:F0}% 未満で停止 ／ {cal.StartDuty:F0}% で回り始める（{cal.MeasuredAt:yyyy/MM/dd HH:mm} 測定）");
        }

        // 最低回転数が停止域に入っていたら警告する
        if (cal is { StopDuty: > 0 } && fan.MinDuty < cal.StopDuty)
        {
            MinDutyWarning.Text = L($"⚠ このファンは {cal.StopDuty:F0}% 未満で止まります。止めたくない場合は {cal.StopDuty + 5:F0}% 以上にしてください。");
            MinDutyWarning.Visibility = Visibility.Visible;
        }
        else
        {
            MinDutyWarning.Visibility = Visibility.Collapsed;
        }
    }

    private void OnApplyCalibrationClick(object sender, RoutedEventArgs e)
    {
        if (SelectedFan is not { } fan || Config.FindCalibration(fan.ControlId) is not { } cal) return;
        // 止まる直前より 5% 上を、全プロファイルの最低回転数にする
        double min = Math.Min(60, cal.StopDuty + 5);
        foreach (var f in Config.Profiles.SelectMany(p => p.Fans).Where(f => f.ControlId == fan.ControlId)) f.MinDuty = min;
        UpdateCalibrationPanel();
        ShowAlert(L($"「{fan.Name}」の最低回転数を全プロファイルで {min:F0}% にしました。"), AlertLevel.Info);
    }

    // =====================================================================
    // 共振回避帯と回転数の試聴
    // =====================================================================

    private static readonly TimeSpan ListenTime = TimeSpan.FromSeconds(20);
    private string? _listenId;
    private DateTime _listenUntil;

    private void UpdateBandPanel()
    {
        var fan = SelectedFan;
        BandEmptyText.Visibility = fan is null || fan.AvoidBands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CurveGraph.AvoidBands = fan?.AvoidBands;
    }

    /// <summary>試聴のスライダーの準備（1 回だけ）: クリックした位置へ一気に飛ばない・ページを離れたら試聴をやめる</summary>
    private void InitListen()
    {
        // クリック 1 回でいきなり 0% になってファンが止まらないように、つまみを動かしたときだけ値を変える
        ListenSlider.IsMoveToPointEnabled = false;
        CurvesPage.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) StopListening();
        };
    }

    private void OnListenChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ListenDutyText is null) return;
        double duty = e.NewValue;
        if (!_syncing && SelectedFan is { IsPump: true } pump && duty < Math.Max(pump.MinDuty, FanEngine.PumpHardMinDuty))
        {
            // ポンプは止めると CPU が冷えなくなるので、最低の回転数より下げない
            duty = Math.Max(pump.MinDuty, FanEngine.PumpHardMinDuty);
            _syncing = true;
            ListenSlider.Value = duty;
            _syncing = false;
        }
        ListenDutyText.Text = $"{duty:F0}%";
        if (_syncing || SelectedFan is not { } fan || !IsLoaded) return;
        _listenId = fan.ControlId;
        _listenUntil = DateTime.Now + ListenTime;
        _controller.Engine.SetOverride(fan.ControlId, duty, ListenTime);
        StopListenButton.Visibility = Visibility.Visible;
    }

    /// <summary>毎周期: 選んでいるファンの回転数を表示し、試聴の時間切れを反映する</summary>
    private void UpdateListen(EngineSnapshot s)
    {
        if (CurvesPage.Visibility != Visibility.Visible || SelectedFan is not { } fan) return;
        var status = s.Fans.FirstOrDefault(f => f.ControlId == fan.ControlId);
        ListenRpmText.Text = status?.Rpm is double rpm ? $"{rpm:N0} rpm" : "";
        bool listening = _listenId == fan.ControlId && DateTime.Now < _listenUntil;
        if (!listening && _listenId is not null)
        {
            _listenId = null;
            StopListenButton.Visibility = Visibility.Collapsed;
        }
        // 試聴していないときは、スライダーを今の回転数に合わせておく（触った瞬間に回転が跳ねないように）
        if (!listening && !ListenSlider.IsMouseCaptureWithin && status?.Duty is double duty)
        {
            _syncing = true;
            ListenSlider.Value = Math.Round(duty);
            _syncing = false;
        }
    }

    private void StopListening()
    {
        if (_listenId is null) return;
        _controller.Engine.SetOverride(_listenId, null, TimeSpan.Zero);
        _listenId = null;
        StopListenButton.Visibility = Visibility.Collapsed;
    }

    private void OnStopListenClick(object sender, RoutedEventArgs e) => StopListening();

    private void OnAvoidHereClick(object sender, RoutedEventArgs e)
    {
        if (SelectedFan is not { } fan) return;
        double v = ListenSlider.Value;
        AddBand(fan, Math.Max(0, v - 4), Math.Min(100, v + 4));
        StopListening();
        ShowAlert(L($"「{fan.Name}」の {Math.Max(0, v - 4):F0}〜{Math.Min(100, v + 4):F0}% を避けるようにしました。カーブのグラフに桃色の帯で表示されます。"), AlertLevel.Info);
    }

    private void OnAddBandClick(object sender, RoutedEventArgs e)
    {
        if (SelectedFan is not { } fan) return;
        double from = fan.AvoidBands.Count > 0 ? Math.Min(90, fan.AvoidBands.Max(b => b.To) + 10) : 40;
        AddBand(fan, from, Math.Min(100, from + 8));
    }

    /// <summary>帯を追加する（重なる帯があればつなげる）</summary>
    private void AddBand(FanConfig fan, double from, double to)
    {
        if (fan.AvoidBands.Count >= 4)
        {
            MessageBox.Show(this, L("共振回避帯は 1 つのファンに 4 つまでです。"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var overlap = fan.AvoidBands.FirstOrDefault(b => from <= b.To && to >= b.From);
        if (overlap is not null)
        {
            overlap.From = Math.Min(overlap.From, from);
            overlap.To = Math.Max(overlap.To, to);
        }
        else
        {
            fan.AvoidBands.Add(new DutyBand { From = Math.Round(from), To = Math.Round(to) });
        }
        AfterBandsChanged(fan);
    }

    private void OnRemoveBandClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DutyBand band } || SelectedFan is not { } fan) return;
        fan.AvoidBands.Remove(band);
        AfterBandsChanged(fan);
    }

    private void OnBandEdited(object sender, RoutedEventArgs e)
    {
        if (SelectedFan is not { } fan) return;
        fan.Normalize();
        AfterBandsChanged(fan);
    }

    /// <summary>共振は端子（ファンとケース）の性質なので、全プロファイルの同じ端子にそろえる</summary>
    private void AfterBandsChanged(FanConfig source)
    {
        foreach (var fan in Config.Profiles.SelectMany(p => p.Fans).Where(f => f != source && f.ControlId == source.ControlId))
        {
            fan.AvoidBands = new ObservableCollection<DutyBand>(source.AvoidBands.Select(b => new DutyBand { From = b.From, To = b.To }));
        }
        UpdateBandPanel();
        WatchConfig();
        ScheduleCommit();
    }
}

// ==================================================
// MainWindow.xaml.cs - メイン画面の中核 (エンジンからの通知・設定変更の保存・画面切替・再バインド)
//   ページごとの処理は MainWindow.Dashboard.cs / .Curves.cs / .History.cs / .Report.cs / .Tools.cs /
//   .AutoTune.cs / .Settings.cs / .Integrations.cs / .Lighting.cs に分けている
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ProFanController.UI;

namespace ProFanController;

public partial class MainWindow : Window
{
    private readonly AppController _controller;
    private readonly DispatcherTimer _commitTimer;
    private readonly HashSet<INotifyPropertyChanged> _watched = new();
    private readonly Dictionary<ProfileConfig, string> _profileNames = new();
    private readonly ObservableCollection<SensorOption> _sensorOptions = new();
    private HardwareCatalog? _catalog;
    private EngineSnapshot? _lastSnapshot;
    private bool _syncing;      // コードから選択を変えている間は、変更イベントを無視する
    private bool _propagating;  // プロファイル間で端子情報を同期している間の再入防止

    private AppConfig Config => _controller.Config;

    /// <summary>true にすると × で本当に閉じる（通常はタスクトレイに隠すだけ）</summary>
    public bool AllowClose { get; set; }

    /// <summary>タスクトレイのツールチップ用の短い要約</summary>
    public string TraySummary { get; private set; } = AppIdentity.Name;

    /// <summary>ミニウィジェットと共有する表示データ</summary>
    public ObservableCollection<TempCardVm> TempCardItems => _tempCards;
    public ObservableCollection<FanCardVm> FanCardItems => _fanCards;

    public event EventHandler? HiddenToTray;
    public event Action? WidgetToggleRequested;

    /// <param name="carry">表示言語を切り替えて画面を作り直すときの、前の画面の記録（履歴・電力量など）。起動時は null</param>
    public MainWindow(AppController controller, ImageSource icon, MainWindowCarry? carry = null)
    {
        InitializeComponent();
        _controller = controller;
        if (carry is not null)
        {
            _history = carry.History;
            _longTerm = carry.LongTerm;
            _energy = carry.Energy;
            _recorder = carry.Recorder;
            _historyRestored = carry.HistoryRestored;
            _nextHealthCheck = carry.NextHealthCheck;
        }

        Icon = icon;
        LogoImage.Source = icon;
        TempCards.ItemsSource = _tempCards;
        MetricTiles.ItemsSource = _metricTiles;
        FindingsList.ItemsSource = _findings;
        TargetSensorCombo.ItemsSource = _sensorOptions;
        HealthList.ItemsSource = _healthItems;
        TuneFans.ItemsSource = _tuneFans;
        DeviceCards.ItemsSource = _deviceCards;
        FanCards.ItemsSource = _fanCards;
        SensorGroups.ItemsSource = _sensorGroups;
        VirtualList.ItemsSource = _virtualRows;
        LiveLegend.ItemsSource = _liveSeries;
        HistoryLegend.ItemsSource = _historySeries;
        SensorCombo.ItemsSource = _sensorOptions;
        AddCardCombo.ItemsSource = _sensorOptions;
        LiveChart.Store = _history;
        LiveChart.Range = TimeSpan.FromMinutes(10);
        HistoryGraph.Store = _history;
        HistoryGraph.Range = TimeSpan.FromMinutes(30);
        FanEditor.Visibility = Visibility.Collapsed;

        // スライダーを動かすたびに保存しないよう、操作が止まってから 0.4 秒後にまとめて反映する
        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _commitTimer.Tick += (_, _) => FlushCommit();

        _controller.ConfigReplaced += RebindAll;
        _controller.Committed += UpdateUndoButtons;
        _controller.EffectiveProfileChanged += UpdateProfileIndicators;

        BindConfig();

        var version = typeof(MainWindow).Assembly.GetName().Version;
        AboutText.Text =
            $"{AppIdentity.Name} {version?.ToString(3)}\n" +
            L("ハードウェアアクセス: LibreHardwareMonitor + PawnIO\n") +
            L($"設定ファイル: {ConfigStore.FilePath}") + "\n\n" +
            L("Zeph-Iris は、LibreHardwareMonitor・PawnIO・OpenRGB・NAudio・QRCoder・Montserrat・Zen Maru Gothic・Inno Setup などのオープンソースのおかげで動いています。作ってくださった皆さんに感謝します（ライセンスはアプリのフォルダーの licenses にあります）。");

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += async (_, _) =>
        {
            // schtasks・powercfg の問い合わせは少し時間がかかるので画面表示を止めない
            bool enabled = await Task.Run(AutoStart.IsEnabled);
            AutoStartSwitch.IsChecked = enabled;
            await LoadPowerPlansAsync();
        };
        InitReport();
        InitLighting();
        InitListen();
        if (carry?.LightHistory is { } lightHistory)
        {
            _lightHistory = lightHistory;
            UpdateLightingUndoButtons();
        }
    }

    // =====================================================================
    // 設定オブジェクトと画面の結び付け
    // =====================================================================

    /// <summary>現在の Config を画面に結び付ける（起動時と、元に戻す・ウィザードで丸ごと入れ替わったとき）</summary>
    private void BindConfig()
    {
        _syncing = true;
        SettingsPage.DataContext = Config;
        SidebarProfileCombo.ItemsSource = Config.Profiles;
        EditProfileCombo.ItemsSource = Config.Profiles;
        NewRuleProfile.ItemsSource = Config.Profiles;
        NewRuleProfile.SelectedIndex = Config.Profiles.Count > 0 ? Config.Profiles.Count - 1 : -1;
        _syncing = false;

        WatchConfig();
        UpdateProfileIndicators();
        UpdateUndoButtons();
        WidgetSwitch.IsChecked = Config.Widget.Visible;
        LoadPlainSettings();
    }

    private void RebindAll()
    {
        // 元に戻した後も、同じプロファイル・ファン・カーブを選んだ状態を保つ
        string? profile = (EditProfileCombo.SelectedItem as ProfileConfig)?.Name;
        string? controlId = (FanList.SelectedItem as FanConfig)?.ControlId;
        int curveIndex = CurveTabs.SelectedIndex;

        BindConfig();
        RebuildSensorOptions();
        RebuildTempCards();
        RebuildVirtualRows();
        RebuildDevices();
        // おまかせ調整の一覧も、入れ替わった設定のプロファイル・ファンで作り直す（調整中は除く）
        if (_catalog is not null && !SetupRunning) RebuildTuneFans();
        if (_catalog is not null) SelectEditProfile(profile ?? _controller.EffectiveProfile, controlId, curveIndex);
    }

    /// <summary>設定オブジェクトすべての変更を監視する（項目を追加・削除したら呼び直す）</summary>
    private void WatchConfig()
    {
        foreach (var o in _watched) o.PropertyChanged -= OnConfigPropertyChanged;
        _watched.Clear();
        _profileNames.Clear();

        void Watch(INotifyPropertyChanged o)
        {
            o.PropertyChanged += OnConfigPropertyChanged;
            _watched.Add(o);
        }

        Watch(Config);
        Watch(Config.Safety);
        Watch(Config.Notify);
        Watch(Config.Energy);
        Watch(Config.Rgb);
        Watch(Config.Rgb.Music);
        Watch(Config.Appearance);
        Watch(Config.Update);
        foreach (var profile in Config.Profiles)
        {
            Watch(profile);
            _profileNames[profile] = profile.Name;
            foreach (var fan in profile.Fans)
            {
                Watch(fan);
                foreach (var band in fan.AvoidBands) Watch(band);
                foreach (var curve in fan.Curves)
                {
                    Watch(curve);
                    foreach (var p in curve.Points) Watch(p);
                }
            }
        }
        foreach (var r in Config.AutoSwitchRules) Watch(r);
        // スケジュールのルールも監視する（以前は監視しておらず、時刻などの変更が保存されなかった）
        foreach (var r in Config.ScheduleRules) Watch(r);
        foreach (var v in Config.VirtualSensors) Watch(v);
    }

    private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (sender)
        {
            case ProfileConfig p when e.PropertyName == nameof(ProfileConfig.Name):
                OnProfileRenamed(p);
                break;
            case FanConfig f when !_propagating && FanConfig.HardwareProperties.Contains(e.PropertyName):
                PropagateHardwareProperty(f, e.PropertyName!);
                break;
            case FanConfig f when e.PropertyName == nameof(FanConfig.Mode):
                if (f.Mode == FanModes.Target && string.IsNullOrEmpty(f.TargetSensorId))
                    f.TargetSensorId = f.Curves.FirstOrDefault()?.SensorId is { Length: > 0 } sid ? sid : DefaultSensorId();
                if (f.Mode == FanModes.Follow && string.IsNullOrEmpty(f.FollowControlId))
                    f.FollowControlId = Config.Profiles.SelectMany(p => p.Fans).FirstOrDefault(x => x.ControlId != f.ControlId && !x.IsPump)?.ControlId ?? "";
                break;
            case FanConfig f when e.PropertyName == nameof(FanConfig.MinDuty) && f == FanList.SelectedItem:
                UpdateCalibrationPanel();
                break;
            case CurveConfig c when e.PropertyName == nameof(CurveConfig.SensorId) && c == CurveTabs.SelectedItem:
                UpdateCurveRange(c);
                break;
            case CurvePoint:
                CurveGraph.InvalidateVisual();
                break;
            case VirtualSensorConfig:
                RebuildVirtualRows();
                break;
            case NotifyConfig:
                UpdateNtfyQr();
                break;
            case RgbConfig or MusicConfig:
                // LED の設定は、保存を待たずにすぐ光り方へ反映する（スライダーを動かしながら確かめられるように）
                CurrentApp.ApplyLightingNow();
                break;
            case DutyBand:
                CurveGraph.InvalidateVisual();
                break;
        }
        ScheduleCommit();
    }

    /// <summary>名前や役割は端子そのものの情報なので、全プロファイルの同じ端子にそろえる</summary>
    private void PropagateHardwareProperty(FanConfig source, string property)
    {
        _propagating = true;
        try
        {
            var prop = typeof(FanConfig).GetProperty(property)!;
            object? value = prop.GetValue(source);
            foreach (var fan in Config.Profiles.SelectMany(p => p.Fans))
            {
                if (fan != source && string.Equals(fan.ControlId, source.ControlId, StringComparison.OrdinalIgnoreCase))
                    prop.SetValue(fan, value);
            }
        }
        finally
        {
            _propagating = false;
        }
    }

    /// <summary>プロファイル名の変更を、普段使う設定と自動切り替えルールに反映する（空や重複は元に戻す）</summary>
    private void OnProfileRenamed(ProfileConfig profile)
    {
        if (!_profileNames.TryGetValue(profile, out var oldName) || oldName == profile.Name) return;

        string newName = profile.Name.Trim();
        if (newName.Length == 0 || Config.Profiles.Any(p => p != profile && p.Name == newName))
        {
            Dispatcher.InvokeAsync(() => profile.Name = oldName);
            return;
        }
        _profileNames[profile] = newName;
        // 前後の空白を取った名前を、プロファイル自身にも書き戻す（ルールなどと名前がずれないように）
        if (profile.Name != newName) Dispatcher.InvokeAsync(() => profile.Name = newName);
        if (Config.ActiveProfile == oldName) Config.ActiveProfile = newName;
        foreach (var r in Config.AutoSwitchRules.Where(r => r.ProfileName == oldName)) r.ProfileName = newName;
        // スケジュールと「静音（一時的）」で使うプロファイル名も一緒に変える（古い名前のままだと見つからなくなる）
        foreach (var r in Config.ScheduleRules.Where(r => r.ProfileName == oldName)) r.ProfileName = newName;
        if (Config.Quick.QuietProfile == oldName) Config.Quick.QuietProfile = newName;
    }

    private void ScheduleCommit()
    {
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    /// <summary>保留中の変更をすぐに保存・反映する</summary>
    private void FlushCommit()
    {
        _commitTimer.Stop();
        foreach (var c in Config.Profiles.SelectMany(p => p.Fans).SelectMany(f => f.Curves)) c.SortPoints();
        _controller.Commit();
        CheckpointLighting(); // ライティングの元に戻す用に、変更が落ち着いた状態を 1 段として覚える（MainWindow.Lighting.cs）
        // 点の一覧を入力中（Tab で次の欄へ移った直後など）に表示し直すと、入力欄から外れてしまうので、そのときは後回しにする
        if (!PointsList.IsKeyboardFocusWithin) PointsList.Items.Refresh();
        CurveGraph.InvalidateVisual();
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _controller.CanUndo;
        RedoButton.IsEnabled = _controller.CanRedo;
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();
    private void OnRedoClick(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        FlushCommit();
        _controller.Undo();
    }

    private void Redo()
    {
        FlushCommit();
        _controller.Redo();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // テキスト入力中の Ctrl+Z は TextBox 自身が処理するので、ここには来ない
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        // ライティングの画面では、ライティングの履歴を使う（ファンの設定の履歴とは別）
        bool lighting = LightingPage.Visibility == Visibility.Visible;
        if (e.Key == Key.Z) { if (lighting) LightingUndo(); else Undo(); e.Handled = true; }
        else if (e.Key == Key.Y) { if (lighting) LightingRedo(); else Redo(); e.Handled = true; }
    }

    // =====================================================================
    // エンジンからの通知（App 経由で UI スレッドに渡ってくる）
    // =====================================================================

    public void OnCatalogReady(HardwareCatalog catalog)
    {
        _catalog = catalog;

        RebuildSensorOptions();
        RebuildTempCards();
        RebuildSensorGroups();
        RebuildVirtualRows();
        RebuildDevices();
        RebuildMetricTiles();
        RegisterHistoryNames();
        ConfigureLongTerm();
        RebuildRgbSensorOptions();
        RebuildTuneFans();

        AddControlCombo.ItemsSource = catalog.Controls;
        AddControlCombo.SelectedIndex = catalog.Controls.Count > 0 ? 0 : -1;
        Grid.SetColumn(CurvesPlaceholder, 1);
        Grid.SetColumnSpan(CurvesPlaceholder, 1);
        SelectEditProfile(_controller.EffectiveProfile, null, 0);

        UpdateDriverStatus(catalog); // PawnIO がなければ、インストールの案内を出す（MainWindow.Setup.cs）
    }

    public void OnSnapshot(EngineSnapshot s)
    {
        _lastSnapshot = s;

        foreach (var card in _tempCards)
        {
            card.Update(s.Values.TryGetValue(card.SensorId, out var v) ? v : null);
        }
        UpdateFanCards(s);
        foreach (var d in _deviceCards) d.Update(s.Values);
        foreach (var m in _metricTiles) m.Update(s.Values);
        RecordHistory(s);
        RecordLongTerm(s);
        UpdateTest(s);
        UpdateTune(s);
        UpdateListen(s);
        foreach (var card in _tempCards) card.SetSuspect(s.Suspects is { } sus && sus.TryGetValue(card.SensorId, out var why) ? why : null);

        if (SensorsPage.Visibility == Visibility.Visible)
        {
            foreach (var (id, row) in _sensorRows)
            {
                row.Update(s.Values.TryGetValue(id, out var v) ? v : null);
            }
            foreach (var row in _virtualRows) row.Update(s.Values);
        }

        UpdateCurveLive(s);
        UpdateStatusPanel(s);
        if (SettingsPage.Visibility == Visibility.Visible && SettingsLink.Visibility == Visibility.Visible && s.Time.Second % 2 == 0)
        {
            UpdateRgbStatus();
            if (CurrentApp.Notifier.LastResult.Length > 0 && NotifyStatus.Text != L("送信しています…")) NotifyStatus.Text = CurrentApp.Notifier.LastResult;
        }

        var cpuCard = _tempCards.FirstOrDefault();
        var gpuCard = _tempCards.Skip(1).FirstOrDefault();
        TraySummary = $"Zeph-Iris [{s.Profile}]\n{cpuCard?.Title ?? "CPU"} {cpuCard?.ValueText ?? "--"}°C / {gpuCard?.Title ?? "GPU"} {gpuCard?.ValueText ?? "--"}°C";
    }

    private void UpdateStatusPanel(EngineSnapshot s)
    {
        if (s.Emergency)
            SetStatus(L("緊急全開"), L("温度が上限に達しています"), Palette.Danger);
        else if (s.BoostUntil is not null)
            SetStatus(L("ブースト中"), L("ポンプ以外を 100% で回しています"), Palette.Accent2);
        else if (s.Paused)
            SetStatus(L("一時停止中"), L("BIOS 制御（ポンプ監視は継続）"), Palette.Warn);
        else if (s.DryRun)
            SetStatus("DRY RUN", L("計算だけで、ファンには書き込んでいません（ポンプ保護は働きます）。下のボタンで制御を始めます"), Palette.Warn);
        else if (s.Fans.Any(f => f.State == FanState.SensorMissing))
            SetStatus(L("制御中（要確認）"), L("見つからないセンサーがあります（部品の交換など）"), Palette.Warn);
        else if (s.Suspects is { Count: > 0 })
            SetStatus(L("制御中（要確認）"), L("値が固まっているセンサーがあります"), Palette.Warn);
        else
            SetStatus(L("制御中"), L($"{s.Fans.Count(f => f.State is FanState.Active or FanState.Manual or FanState.GpuGuard)} 個を制御中"), Palette.Ok);

        // DRY RUN 中は、ここから 1 回で制御を始められるようにする（設定画面の奥まで行かなくてよいように）
        PauseButton.Content = s.DryRun && !s.Paused ? L("制御を開始する（DRY RUN を解除）")
            : s.Paused ? L("制御を再開する") : L("一時停止（BIOS に戻す）");
    }

    private void SetStatus(string text, string sub, SolidColorBrush brush)
    {
        StatusText.Text = text;
        StatusSub.Text = sub;
        StatusDot.Fill = brush;
    }

    private Action? _alertAction;

    /// <summary>警告バナーを出す。actionLabel を指定すると、右側にそのボタンを出す（押すと action を実行してバナーを閉じる）</summary>
    public void ShowAlert(string message, AlertLevel level, string? actionLabel = null, Action? action = null)
    {
        _alertAction = action;
        AlertActionButton.Content = actionLabel;
        AlertActionButton.Visibility = actionLabel is not null && action is not null ? Visibility.Visible : Visibility.Collapsed;
        var (title, brush) = level switch
        {
            AlertLevel.Danger => (L("警告"), Palette.Danger),
            AlertLevel.Warning => (L("注意"), Palette.Warn),
            _ => (L("お知らせ"), Palette.Accent2),
        };
        var c = brush.Color;
        AlertBanner.Background = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B));
        AlertBanner.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B));
        AlertTitle.Foreground = brush;
        AlertIcon.Foreground = brush;
        AlertTitle.Text = $"{title}  ·  {DateTime.Now:HH:mm:ss}";
        AlertText.Text = message;
        AlertBanner.Visibility = Visibility.Visible;
    }

    private void OnDismissAlert(object sender, RoutedEventArgs e) => AlertBanner.Visibility = Visibility.Collapsed;

    private void OnAlertActionClick(object sender, RoutedEventArgs e)
    {
        var action = _alertAction;
        AlertBanner.Visibility = Visibility.Collapsed;
        action?.Invoke();
    }

    /// <summary>カーブの連動先に選べる温度の一覧（実センサー＋仮想センサー）</summary>
    private void RebuildSensorOptions()
    {
        if (_catalog is null) return;
        _sensorOptions.Clear();
        foreach (var vs in Config.VirtualSensors)
        {
            var kind = vs.Kind == VirtualSensorKinds.Delta ? SensorKind.Delta : SensorKind.Other;
            _sensorOptions.Add(new SensorOption(vs.Id, vs.Name, L($"仮想（{vs.KindText}）"), kind));
        }
        foreach (var t in _catalog.Temperatures)
        {
            _sensorOptions.Add(new SensorOption(t.Id, t.Name, t.HardwareName, CurvePresets.Classify(t.Id, t.Name)));
        }
    }

    private SensorOption? FindSensorOption(string? id)
        => _sensorOptions.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));

    // =====================================================================
    // 画面切り替え・ウィンドウ
    // =====================================================================

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (DashboardPage is null) return; // InitializeComponent 中の最初の Checked は無視

        DashboardPage.Visibility = NavDashboard.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DevicesPage.Visibility = NavDevices.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ToolsPage.Visibility = NavTools.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TestPage.Visibility = NavTest.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CurvesPage.Visibility = NavCurves.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = NavHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ReportPage.Visibility = NavReport.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SensorsPage.Visibility = NavSensors.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        // ライティング: 見えている間だけ、音のスペクトラムや LED のプレビューを動かす（MainWindow.Lighting.cs）
        LightingPage.Visibility = NavLighting.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        DashboardActions.Visibility = DashboardPage.Visibility;
        CurvesActions.Visibility = CurvesPage.Visibility;
        LightingActions.Visibility = LightingPage.Visibility;
        HistoryActions.Visibility = HistoryPage.Visibility;

        (PageTitle.Text, PageSubtitle.Text) = sender switch
        {
            _ when sender == NavDevices => (L("デバイス"), L("検出したハードウェアの一覧です。画像のボタンから、自分で撮った写真などに差し替えられます")),
            _ when sender == NavCurves => (L("ファンカーブ"), L("ファンごとに温度と回転数の関係を設定します。変更はすぐに反映・保存されます")),
            _ when sender == NavTools => (L("診断"), L("記録からの自動診断と、ファンのホコリ・劣化のチェック（AI のアドバイスはオプション）")),
            _ when sender == NavTest => (L("自動セットアップ"), L("ファンの特性と、CPU・GPU に負荷をかけたときの温度を測って、カーブを自動で作ります。今の設定でどこまで冷えるかも確かめられます")),
            _ when sender == NavHistory => (L("履歴"), L("最大 24 時間分の温度と回転数（アプリを閉じていた間を除く。再起動前の分は 1 分ごとの平均）")),
            _ when sender == NavReport => (L("レポート"), L("電気代の目安・異常の記録")),
            _ when sender == NavLighting => (L("ライティング"), L("OpenRGB 連携で、PC の LED を温度・レインボー・音楽などに合わせて光らせます")),
            _ when sender == NavSensors => (L("センサー"), L("このPCで検出されたすべての温度・回転数・制御チャネルと、仮想センサー")),
            _ when sender == NavSettings => (L("設定"), L("プロファイル・自動切り替え・起動方法・制御全体の動作")),
            _ => (L("ダッシュボード"), L("温度とファンの状態をリアルタイムに表示します")),
        };

        if (HistoryPage.Visibility == Visibility.Visible) HistoryGraph.Refresh();
        if (ReportPage.Visibility == Visibility.Visible) RefreshReport();
        // 診断: ホコリ・劣化のチェックを、まだ調べていなければ調べる
        if (ToolsPage.Visibility == Visibility.Visible && _healthItems.Count == 0 && !_healthRunning) _ = AnalyzeHealthAsync(automatic: false);
        if (SettingsPage.Visibility == Visibility.Visible) RefreshSettingsInfo();
        if (TestPage.Visibility == Visibility.Visible && !SetupRunning && _catalog is not null) RebuildTuneFans();
    }

    private void OnSettingsTabChanged(object sender, RoutedEventArgs e)
    {
        if (SettingsGeneral is null) return;
        SettingsGeneral.Visibility = SetTabGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsAuto.Visibility = SetTabAuto.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsLink.Visibility = SetTabLink.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsSafety.Visibility = SetTabSafety.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroller.ScrollToTop();
        RefreshSettingsInfo();
    }

    /// <summary>設定画面の指定したタブを開く（ほかの画面からの案内用）</summary>
    public void OpenSettingsTab(string tab)
    {
        NavSettings.IsChecked = true;
        (tab switch { "link" => SetTabLink, "safety" => SetTabSafety, "auto" => SetTabAuto, _ => SetTabGeneral }).IsChecked = true;
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (_lastSnapshot is { DryRun: true, Paused: false })
        {
            Config.DryRun = false;
            FlushCommit();
            Logger.Info("DRY RUN を解除しました（サイドバーのボタン）");
            ShowAlert(L("DRY RUN を解除しました。ファンの制御を始めています。"), AlertLevel.Info);
            return;
        }
        ((App)Application.Current).TogglePause();
    }

    // ----- ワンタッチ操作 -----

    private void OnBoostClick(object sender, RoutedEventArgs e)
        => ((App)Application.Current).ExecuteCommand($"boost:{(sender as FrameworkElement)?.Tag}");

    private void OnQuietClick(object sender, RoutedEventArgs e)
        => ((App)Application.Current).ExecuteCommand($"quiet:{(sender as FrameworkElement)?.Tag}");

    private void OnCancelQuickClick(object sender, RoutedEventArgs e) => ((App)Application.Current).ExecuteCommand("cancel");

    /// <summary>1 秒ごとに App から呼ばれ、残り時間などを更新する</summary>
    public void UpdateQuickStatus()
    {
        string? text = _controller.QuickStatusText;
        QuickStatusText.Text = text ?? "";
        QuickStatusText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        CancelQuickButton.Visibility = QuickStatusText.Visibility;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 表示言語の切り替えで作り直した前の画面: 記録は新しい画面が続けて使うので、ここでは保存しない
        if (_detached)
        {
            base.OnClosing(e);
            return;
        }
        if (_commitTimer.IsEnabled) FlushCommit();
        if (AllowClose) SaveReportData();
        if (!AllowClose)
        {
            // × ボタンでは終了せず、タスクトレイで制御を続ける
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }
        base.OnClosing(e);
    }

    /// <summary>Windows 11 のタイトルバーをダークにして、背景色と合わせる</summary>
}

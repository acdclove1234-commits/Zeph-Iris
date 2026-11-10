// ==================================================
// MainWindow.Report.cs - レポート画面 (電力量と電気代・ファンの劣化とホコリのチェック・異常の記録) と
//                        長期の記録（1 分ごと）・起動前の履歴の復元
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProFanController.UI;
using ProFanController.UI.Controls;

namespace ProFanController;

/// <summary>劣化チェックの 1 行</summary>
public sealed class HealthItemVm
{
    public required HealthItem Item { get; init; }
    public string Title => Item.Title;
    public string Detail => Item.Detail;
    public string Metric => Item.Metric;
    public string ChangeText => Item.Change is double c ? $"{c:+0%;-0%;0%}" : "";
    public SolidColorBrush LevelBrush => Item.Level switch
    {
        HealthLevel.Danger => Palette.Danger,
        HealthLevel.Warning => Palette.Warn,
        HealthLevel.Good => Palette.Ok,
        HealthLevel.Info => Palette.Accent2,
        _ => Palette.Muted,
    };
    public string LevelText => Item.Level switch
    {
        HealthLevel.Danger => L("要点検"),
        HealthLevel.Warning => L("注意"),
        HealthLevel.Good => L("正常"),
        HealthLevel.Info => L("参考"),
        _ => L("収集中"),
    };
}

public partial class MainWindow
{
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromHours(6);

    private readonly LongTermStore _longTerm = new();
    private readonly EnergyMeter _energy = new();
    private readonly FlightRecorder _recorder = new();
    private readonly ObservableCollection<HealthItemVm> _healthItems = new();
    private Dictionary<string, string> _roles = new();
    private bool _historyRestored;
    private bool _healthRunning;
    private DateTime _nextHealthCheck = DateTime.Now.AddMinutes(3);
    private bool _reportSaved;

    /// <summary>異常の記録を保存した（App が通知に使う）</summary>
    public event Action<FlightRecord>? RecordSaved;
    /// <summary>劣化チェックで注意が見つかった（App が通知に使う）</summary>
    public event Action<string, AlertLevel>? MaintenanceNotice;

    private void InitReport() => _recorder.Saved += OnRecordSaved;

    private void OnRecordSaved(FlightRecord r)
    {
        RefreshRecords();
        ShowAlert(L($"異常の前後 10 分の記録を保存しました: {r.Title}"), AlertLevel.Info, L("記録を見る"), () =>
        {
            NavReport.IsChecked = true;
            ShowRecordSummary(r);
        });
        RecordSaved?.Invoke(r);
    }

    // =====================================================================
    // 長期の記録
    // =====================================================================

    /// <summary>役割（CPU 温度・GPU 電力など）ごとのセンサーを決める。部品を交換しても役割で比べられるようにするため</summary>
    private Dictionary<string, string> ResolveRoles()
    {
        var roles = new Dictionary<string, string>();
        if (_catalog is null) return roles;
        var all = _catalog.AllSensors.ToList();
        void Add(string role, SensorInfo? s)
        {
            if (s is not null) roles[role] = s.Id;
        }
        var t = _catalog.Temperatures;
        Add(StatRoles.CpuTemp, SensorNames.CpuTemperature(t)
                               ?? t.FirstOrDefault(x => x.HardwareType.StartsWith("Cpu")));
        Add(StatRoles.GpuTemp, SensorNames.GpuCoreTemperature(t));
        Add(StatRoles.Ambient, AmbientSensor(t));
        Add(StatRoles.CpuPower, SensorNames.Find(all, "Cpu", "Power", SensorNames.CpuPower));
        Add(StatRoles.GpuPower, all.FirstOrDefault(x => x.HardwareType.StartsWith("Gpu") && x.SensorType == "Power" && x.Name.Contains("Package"))
                                ?? all.FirstOrDefault(x => x.HardwareType.StartsWith("Gpu") && x.SensorType == "Power"));
        Add(StatRoles.CpuLoad, SensorNames.Find(all, "Cpu", "Load", SensorNames.CpuLoad));
        Add(StatRoles.GpuLoad, SensorNames.Find(all, "Gpu", "Load", SensorNames.GpuLoad));
        return roles;
    }

    /// <summary>
    /// 冷え方の判定で「室温」として使うセンサー（設定の AmbientSensorId）。自動ならマザーボードの System・Motherboard、
    /// 「使わない」なら null（休んでいるときとの差だけで比べる）
    /// </summary>
    private SensorInfo? AmbientSensor(IEnumerable<SensorInfo> temperatures)
    {
        string? chosen = Config.Maintenance.AmbientSensorId;
        if (chosen == MaintenanceConfig.NoAmbientSensor) return null;
        if (!string.IsNullOrEmpty(chosen) && temperatures.FirstOrDefault(x => x.Id == chosen) is { } picked) return picked;
        return temperatures.FirstOrDefault(x => x.HardwareType == "SuperIO" && x.Name is "System" or "Motherboard");
    }

    /// <summary>「室温のセンサー」の選択肢を作る（自動・使わない・温度センサーの一覧）</summary>
    private void RebuildAmbientOptions()
    {
        if (_catalog is null) return;
        var auto = _catalog.Temperatures.FirstOrDefault(x => x.HardwareType == "SuperIO" && x.Name is "System" or "Motherboard");
        var options = new List<KeyValuePair<string, string>>
        {
            new("", auto is null ? L("自動（見つかりません）") : L($"自動（{auto.Display}）")),
            new(MaintenanceConfig.NoAmbientSensor, L("使わない（休んでいるときとの差だけで比べる）")),
        };
        options.AddRange(_catalog.Temperatures.Select(s => new KeyValuePair<string, string>(s.Id, s.Display)));
        _ambientUpdating = true;
        AmbientSensorBox.ItemsSource = options;
        string current = Config.Maintenance.AmbientSensorId ?? "";
        AmbientSensorBox.SelectedValue = options.Any(o => o.Key == current) ? current : "";
        _ambientUpdating = false;
    }

    private bool _ambientUpdating;

    private void OnAmbientSensorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ambientUpdating || AmbientSensorBox.SelectedValue is not string id) return;
        string? value = id.Length == 0 ? null : id;
        if (value == Config.Maintenance.AmbientSensorId) return;
        Config.Maintenance.AmbientSensorId = value;
        _controller.Commit(recordUndo: false);
        Logger.Info($"冷え方の判定の室温のセンサー: {(value is null ? "自動" : value)}");
        ConfigureLongTerm(rebuildOptions: false); // これからの記録の「室温」を、選んだセンサーにする
        _ = AnalyzeHealthAsync(automatic: false);
    }

    /// <summary>1 分ごとに保存するセンサーを決め、初回は保存してある記録から起動前 24 時間の履歴を復元する</summary>
    private void ConfigureLongTerm(bool rebuildOptions = true)
    {
        if (_catalog is null) return;
        if (rebuildOptions) RebuildAmbientOptions();
        _roles = ResolveRoles();
        var keys = _roles.Values
            .Concat(_tempCards.Select(c => c.SensorId))
            .Concat(Config.Profiles.SelectMany(p => p.Fans).SelectMany(f => f.Curves.Select(c => c.SensorId)))
            .Concat(MetricHistoryKeys.Select(m => m.Id))
            .Concat(Config.VirtualSensors.Select(v => v.Id))
            .Concat(Config.Profiles.SelectMany(p => p.Fans).Select(f => f.ControlId).Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(id => new[] { HistoryStore.RpmKey(id), HistoryStore.DutyKey(id) }))
            .Where(k => k.Length > 0);
        _longTerm.Configure(keys, _roles);

        if (_historyRestored) return;
        _historyRestored = true;
        try
        {
            var now = DateTime.Now;
            foreach (var row in LongTermStore.Load(now - TimeSpan.FromHours(24), now)) _history.AddRaw(row.Time, row.Values);
            if (_history.Count > 0) Logger.Info($"起動前の履歴を {_history.Count} 分ぶん復元しました");
        }
        catch (Exception ex)
        {
            Logger.Warn($"履歴の復元に失敗: {ex.Message}");
        }
    }

    private void RecordLongTerm(EngineSnapshot s)
    {
        if (_catalog is null) return;
        _longTerm.Add(s.Time, s.Values, s.Fans);
        double? W(string role) => _roles.TryGetValue(role, out var id) && s.Values.TryGetValue(id, out var v) ? v : null;
        _energy.Add(s.Time, W(StatRoles.CpuPower), W(StatRoles.GpuPower), Config.Energy.BaseWatts, Config.Energy.PsuEfficiency);
        _recorder.Tick(_history, s.Time);

        if (ReportPage.Visibility == Visibility.Visible && s.Time.Second % 5 == 0) RefreshEnergy();
        if (DateTime.Now >= _nextHealthCheck)
        {
            _nextHealthCheck = DateTime.Now + HealthCheckInterval;
            _ = AnalyzeHealthAsync(automatic: true);
        }
    }

    /// <summary>エンジンからの警告（App から呼ばれる）。危険・注意なら前後の記録を予約する</summary>
    public void OnEngineAlert(string message, AlertLevel level)
    {
        if (level == AlertLevel.Info || !Config.Safety.FlightRecorderEnabled) return;
        _recorder.Trigger(message, _controller.EffectiveProfile, DateTime.Now);
    }

    /// <summary>終了時: 集計途中の 1 分・電力量・待っている異常の記録を保存する</summary>
    public void SaveReportData()
    {
        if (_reportSaved) return;
        _reportSaved = true;
        _longTerm.Flush();
        _energy.Save();
        _recorder.Flush(_history);
    }

    // =====================================================================
    // レポート画面
    // =====================================================================

    private void RefreshReport()
    {
        RefreshEnergy();
        RefreshRecords();
    }

    private bool ShowYen => EnergyYen?.IsChecked == true;

    private string Yen(double kwh) => L($"{kwh * Config.Energy.PricePerKwh:N0} 円");

    private void RefreshEnergy()
    {
        double price = Config.Energy.PricePerKwh;
        var cfg = Config.Energy;
        EnergyNote.Text = cfg.CalibratedAt.Length > 0
            ? L($"{cfg.CalibratedAt} にワットチェッカーで合わせ込んだ値（CPU・GPU 以外 {cfg.BaseWatts:F0} W・電源の効率 {cfg.PsuEfficiency:F0}%）で、コンセントから引く電力を計算しています（単価 {price:0.##} 円/kWh）。モニターなどは含みません。アプリが動いている間だけ数えます。")
            : L($"CPU・GPU はセンサーの値、それ以外は {cfg.BaseWatts:F0} W、電源の効率は {cfg.PsuEfficiency:F0}% として計算した ±20% ほどの目安です（単価 {price:0.##} 円/kWh）。設定の「電気代の計算」でワットチェッカーと合わせ込めます。アプリが動いている間だけ数えます。");
        EnergyNow.Text = $"{_energy.CurrentWatts:F0} W";
        EnergyNowSub.Text = L($"CPU {_energy.CpuWatts:F0} W ・ GPU {_energy.GpuWatts:F0} W ・ その他 {_energy.BaseWatts:F0} W ・ 電源の損失 {_energy.LossWatts:F0} W");

        var today = _energy.Day(DateTime.Today);
        double month = _energy.MonthKwh(DateTime.Today);
        double forecast = _energy.MonthForecastKwh(DateTime.Today);
        if (ShowYen)
        {
            EnergyToday.Text = Yen(today.Kwh);
            EnergyTodaySub.Text = L($"{today.Kwh:F2} kWh ・ {today.Hours:F1} 時間");
            EnergyMonth.Text = Yen(month);
            EnergyMonthSub.Text = $"{month:F1} kWh";
            EnergyForecast.Text = Yen(forecast);
            EnergyForecastSub.Text = L($"{forecast:F1} kWh（記録のある日の平均から）");
        }
        else
        {
            EnergyToday.Text = $"{today.Kwh:F2} kWh";
            EnergyTodaySub.Text = L($"約 {Yen(today.Kwh)} ・ {today.Hours:F1} 時間");
            EnergyMonth.Text = $"{month:F1} kWh";
            EnergyMonthSub.Text = L($"約 {Yen(month)}");
            EnergyForecast.Text = $"{forecast:F0} kWh";
            EnergyForecastSub.Text = L($"約 {Yen(forecast)}");
        }

        EnergyChart.Items = _energy.LastDays(30).Select(d =>
        {
            double v = ShowYen ? d.Energy.Kwh * price : d.Energy.Kwh;
            string tip = L($"{d.Date:M/d（ddd）}  {d.Energy.Kwh:F2} kWh ・ 約 {Yen(d.Energy.Kwh)} ・ {d.Energy.Hours:F1} 時間");
            return new BarItem(d.Date.ToString("M/d"), v, tip);
        }).ToList();
    }

    private void OnEnergyUnitChanged(object sender, RoutedEventArgs e)
    {
        if (EnergyNow is not null) RefreshEnergy();
    }

    // ----- 劣化・ホコリ -----

    private void OnAnalyzeHealthClick(object sender, RoutedEventArgs e) => _ = AnalyzeHealthAsync(automatic: false);

    private async Task AnalyzeHealthAsync(bool automatic)
    {
        if (_healthRunning) return;
        _healthRunning = true;
        if (!automatic) HealthInfo.Text = L("記録を読み込んで調べています…");
        try
        {
            // 集計途中の 1 分はここでは書き出さない（書き出すと、その分が終わったときにもう 1 行書かれて同じ時刻の行が 2 つになる）
            var cfg = Config.Clone();
            var now = DateTime.Now;
            var (items, days) = await Task.Run(() =>
            {
                var rows = LongTermStore.Load(now.AddDays(-60), now);
                double d = rows.Count == 0 ? 0 : (now - rows[0].Time).TotalDays;
                return (HealthAnalyzer.Analyze(rows, cfg, now), d);
            });
            _healthItems.Clear();
            foreach (var i in items.OrderByDescending(i => i.Level == HealthLevel.Collecting ? -1 : (int)i.Level)) _healthItems.Add(new HealthItemVm { Item = i });
            string cleaned = Config.Maintenance.LastCleaned is DateTime c ? L($" ・ 基準: {c:yyyy/MM/dd} の掃除以降") : "";
            HealthInfo.Text = L($"{now:HH:mm} に確認 ・ 記録 {days:F0} 日分{cleaned}");

            // 注意以上が見つかったら、週に 1 回だけ知らせる
            var worst = items.Where(i => i.Level is HealthLevel.Warning or HealthLevel.Danger).ToList();
            if (worst.Count > 0 && (Config.Maintenance.LastNotified is not DateTime n || now - n > TimeSpan.FromDays(7)))
            {
                Config.Maintenance.LastNotified = now;
                _controller.Commit(recordUndo: false);
                string msg = L($"メンテナンスのお知らせ: {string.Join(L("、"), worst.Select(w => w.Title))} に変化があります。");
                var level = worst.Any(w => w.Level == HealthLevel.Danger) ? AlertLevel.Danger : AlertLevel.Warning;
                ShowAlert(msg, level, L("診断を見る"), () => NavTools.IsChecked = true);
                MaintenanceNotice?.Invoke(msg + string.Join(" ", worst.Select(w => w.Metric)), level);
            }
        }
        catch (Exception ex)
        {
            HealthInfo.Text = L($"調べられませんでした: {ex.Message}");
            Logger.Warn($"劣化チェックに失敗: {ex}");
        }
        finally
        {
            _healthRunning = false;
        }
    }

    private void OnCleanedClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            L("ファンやフィルター・ラジエーターを掃除しましたか？\n\nこれ以降の記録を新しい基準にして比べます（判定できるまで 10 日ほどかかります）。"),
            L("掃除した"), MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        Config.Maintenance.LastCleaned = DateTime.Now;
        Config.Maintenance.LastNotified = null;
        _controller.Commit(recordUndo: false);
        _ = AnalyzeHealthAsync(automatic: false);
    }

    // ----- 異常の記録 -----

    /// <summary>フライトレコーダーの記録の一覧（フォルダの中を読むので別スレッドで。画面を開いた瞬間に止まらないように）</summary>
    private async void RefreshRecords()
    {
        List<FlightRecord> list;
        try { list = await Task.Run(FlightRecorder.List); }
        catch (Exception ex) { Logger.Warn($"記録の一覧を読めません: {ex.Message}"); return; }
        RecordsList.ItemsSource = list;
        RecordsEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (list.Count == 0) RecordSummary.Visibility = Visibility.Collapsed;
    }

    private void OnShowRecordClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FlightRecord r }) ShowRecordSummary(r);
    }

    private void ShowRecordSummary(FlightRecord r)
    {
        RecordSummary.Text = r.Summary;
        RecordSummary.Visibility = Visibility.Visible;
        RecordSummary.BringIntoView();
    }

    private void OnOpenRecordClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FlightRecord r } || !File.Exists(r.CsvPath)) return;
        try { SystemTool.Open(r.CsvPath); }
        catch (Exception ex) { MessageBox.Show(this, L($"開けませんでした: {ex.Message}"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnOpenRecordsClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(FlightRecorder.Directory);
        SystemTool.Open(FlightRecorder.Directory);
    }

    /// <summary>ダッシュボードの「電力」タイル（MainWindow.Monitoring.cs から作る）</summary>
    private MetricTileVm CreateEnergyTile() => new()
    {
        Title = L("消費電力（目安）"),
        ShortTitle = L("電力"),
        HistoryKey = null,
        Brush = Palette.Series[7],
        Read = _ =>
        {
            double w = _energy.CurrentWatts;
            double kwh = _energy.TodayKwh;
            return (w, $"{w:F0} W", L($"今日 {kwh:F2} kWh ・ 約 {kwh * Config.Energy.PricePerKwh:N0} 円"), null);
        },
    };
}

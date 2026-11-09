// ==================================================
// MainWindow.Dashboard.cs - ダッシュボード (温度カードの編集・ファンカード・リアルタイムグラフ)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProFanController.UI;
using ProFanController.UI.Controls;

namespace ProFanController;

public partial class MainWindow
{
    private readonly ObservableCollection<TempCardVm> _tempCards = new();
    private readonly ObservableCollection<FanCardVm> _fanCards = new();
    private readonly ObservableCollection<SeriesToggleVm> _liveSeries = new();

    /// <summary>設定にカードの指定がなければ、CPU・GPU・Hot Spot・ケース内を自動で選ぶ</summary>
    private List<DashboardCard> DefaultCards()
    {
        var list = new List<DashboardCard>();
        if (_catalog is null) return list;
        var t = _catalog.Temperatures;
        var cpu = SensorNames.CpuTemperature(t)
                  ?? t.FirstOrDefault(x => x.HardwareType.StartsWith("Cpu"));
        var gpuCore = SensorNames.GpuCoreTemperature(t);
        var gpuHot = SensorNames.GpuHotSpotTemperature(t);
        var system = t.FirstOrDefault(x => x.HardwareType == "SuperIO" && x.Name == "System");

        if (cpu is not null) list.Add(new DashboardCard { Title = "CPU", SensorId = cpu.Id, Warn = 75, Danger = 90 });
        if (gpuCore is not null) list.Add(new DashboardCard { Title = "GPU", SensorId = gpuCore.Id, Warn = 75, Danger = 90 });
        if (gpuHot is not null) list.Add(new DashboardCard { Title = "GPU Hot Spot", SensorId = gpuHot.Id, Warn = 85, Danger = 100 });
        if (system is not null) list.Add(new DashboardCard { Title = L("ケース内"), SensorId = system.Id, Warn = 45, Danger = 55 });
        return list;
    }

    private IEnumerable<DashboardCard> EffectiveCards => Config.DashboardCards.Count > 0 ? Config.DashboardCards : DefaultCards();

    private void RebuildTempCards()
    {
        _tempCards.Clear();
        foreach (var c in EffectiveCards)
        {
            var opt = FindSensorOption(c.SensorId);
            _tempCards.Add(new TempCardVm
            {
                Title = c.Title,
                SensorId = c.SensorId,
                Subtitle = opt?.Group ?? c.SensorId,
                Warn = c.Warn,
                Danger = c.Danger,
                Editing = EditCardsToggle.IsChecked == true,
            });
        }
        RebuildSeries();
        ConfigureLongTerm();
    }

    private void UpdateFanCards(EngineSnapshot s)
    {
        // 構成（端子・種類・名前）が変わったときだけ作り直す
        bool changed = !_fanCards.Select(c => c.ControlId + c.IsPump + c.Name)
            .SequenceEqual(s.Fans.Select(f => f.ControlId + (f.Role == FanRoles.Pump) + f.Name));
        if (changed && !_fanCards.Select(c => c.ControlId + c.IsPump).SequenceEqual(s.Fans.Select(f => f.ControlId + (f.Role == FanRoles.Pump))))
        {
            _fanCards.Clear();
            foreach (var f in s.Fans)
            {
                var device = _catalog?.Controls.FirstOrDefault(c => c.Id == f.ControlId)?.HardwareName ?? "";
                _fanCards.Add(new FanCardVm { ControlId = f.ControlId, IsPump = f.Role == FanRoles.Pump, DeviceText = device });
            }
        }
        var profileFans = Config.FindProfile(s.Profile)?.Fans;
        for (int i = 0; i < s.Fans.Count; i++)
        {
            bool fixedDuty = profileFans?.FirstOrDefault(f => f.ControlId == s.Fans[i].ControlId)?.UseFixedDuty == true;
            _fanCards[i].Update(s.Fans[i], s.Profile, fixedDuty);
        }
        if (changed)
        {
            // 名前が入ってから系列を作る（グラフの凡例と CSV の列名に使う）
            RegisterHistoryNames();
            RebuildSeries();
            ConfigureLongTerm();
        }
    }

    /// <summary>リアルタイムグラフと履歴グラフの系列（温度カードの温度 + 各ファンの回転数）</summary>
    private void RebuildSeries()
    {
        var series = new List<ChartSeries>();
        int color = 0;
        foreach (var card in _tempCards)
        {
            series.Add(new ChartSeries { Key = card.SensorId, Name = card.Title, Brush = Palette.Series[color++ % Palette.Series.Length] });
        }
        foreach (var vs in Config.VirtualSensors.Where(v => _tempCards.All(c => c.SensorId != v.Id)))
        {
            series.Add(new ChartSeries { Key = vs.Id, Name = vs.Name, Brush = Palette.Series[color++ % Palette.Series.Length] });
        }
        foreach (var fan in _fanCards)
        {
            series.Add(new ChartSeries { Key = HistoryStore.RpmKey(fan.ControlId), Name = fan.Name.Length > 0 ? fan.Name : fan.ControlId, IsRpm = true, Brush = Palette.Series[color++ % Palette.Series.Length] });
        }

        // 表示/非表示の選択は、作り直しても引き継ぐ
        var liveHidden = _liveSeries.Where(s => !s.Visible).Select(s => s.Series.Key).ToHashSet();
        var histHidden = _historySeries.Where(s => !s.Visible).Select(s => s.Series.Key).ToHashSet();

        _liveSeries.Clear();
        _historySeries.Clear();
        var liveList = new List<ChartSeries>();
        var histList = new List<ChartSeries>();
        foreach (var s in series)
        {
            var live = new ChartSeries { Key = s.Key, Name = s.Name, Brush = s.Brush, IsRpm = s.IsRpm };
            var hist = new ChartSeries { Key = s.Key, Name = s.Name, Brush = s.Brush, IsRpm = s.IsRpm };
            liveList.Add(live);
            histList.Add(hist);
            _liveSeries.Add(new SeriesToggleVm { Series = live, Visible = !liveHidden.Contains(s.Key) });
            _historySeries.Add(new SeriesToggleVm { Series = hist, Visible = !histHidden.Contains(s.Key) });
        }
        LiveChart.Series = liveList;
        HistoryGraph.Series = histList;
        LiveChart.Refresh();
        HistoryGraph.Refresh();
    }

    private void OnLegendClick(object sender, RoutedEventArgs e)
    {
        LiveChart.Refresh();
        HistoryGraph.Refresh();
    }

    private void OnFanCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string controlId }) return;
        NavCurves.IsChecked = true;
        SelectEditProfile(_controller.EffectiveProfile, controlId, 0);
    }

    private void OnToggleWidgetClick(object sender, RoutedEventArgs e) => WidgetToggleRequested?.Invoke();

    // ----- カードの編集 -----

    private void OnEditCardsClick(object sender, RoutedEventArgs e)
    {
        bool editing = EditCardsToggle.IsChecked == true;
        foreach (var c in _tempCards) c.Editing = editing;
        AddCardPanel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        EditCardsToggle.Content = editing ? L("編集を終了") : L("カードを編集");
    }

    /// <summary>自動選択のままなら、編集の前に今の並びを設定として確定させる</summary>
    private void MaterializeCards()
    {
        if (Config.DashboardCards.Count > 0) return;
        foreach (var c in DefaultCards()) Config.DashboardCards.Add(c);
    }

    private void OnMoveCardLeft(object sender, RoutedEventArgs e) => MoveCard(sender, -1);
    private void OnMoveCardRight(object sender, RoutedEventArgs e) => MoveCard(sender, +1);

    private void MoveCard(object sender, int delta)
    {
        if (sender is not Button { Tag: TempCardVm vm }) return;
        MaterializeCards();
        int i = Config.DashboardCards.ToList().FindIndex(c => c.SensorId == vm.SensorId);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= Config.DashboardCards.Count) return;
        Config.DashboardCards.Move(i, j);
        RebuildTempCards();
        ScheduleCommit();
    }

    private void OnRemoveCard(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TempCardVm vm }) return;
        MaterializeCards();
        var card = Config.DashboardCards.FirstOrDefault(c => c.SensorId == vm.SensorId);
        if (card is null) return;
        if (Config.DashboardCards.Count <= 1)
        {
            MessageBox.Show(this, L("カードは最低 1 枚必要です。"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Config.DashboardCards.Remove(card);
        RebuildTempCards();
        ScheduleCommit();
    }

    private void OnAddCardClick(object sender, RoutedEventArgs e)
    {
        if (AddCardCombo.SelectedItem is not SensorOption opt) return;
        MaterializeCards();
        if (Config.DashboardCards.Any(c => c.SensorId == opt.Id)) return;

        var (warn, danger) = opt.Kind switch
        {
            SensorKind.GpuHotspot => (85.0, 100.0),
            SensorKind.Delta => (10.0, 15.0),
            SensorKind.Other => (60.0, 80.0),
            _ => (75.0, 90.0),
        };
        Config.DashboardCards.Add(new DashboardCard { SensorId = opt.Id, Title = opt.Name, Warn = warn, Danger = danger });
        RebuildTempCards();
        ScheduleCommit();
    }
}

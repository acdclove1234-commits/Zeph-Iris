// ==================================================
// MainWindow.History.cs - 履歴画面 (記録・期間切替・CSV 出力)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ProFanController.UI;

namespace ProFanController;

public partial class MainWindow
{
    private readonly HistoryStore _history = new();
    private readonly ObservableCollection<SeriesToggleVm> _historySeries = new();

    private void RecordHistory(EngineSnapshot s)
    {
        if (_catalog is null) return;
        _history.Add(s, _catalog.Temperatures.Select(t => t.Id)
            .Concat(Config.VirtualSensors.Select(v => v.Id))
            .Concat(MetricHistoryKeys.Select(m => m.Id)));

        if (DashboardPage.Visibility == Visibility.Visible) LiveChart.Refresh();
        if (HistoryPage.Visibility == Visibility.Visible)
        {
            HistoryGraph.Refresh();
            var span = _history.Count > 0 ? s.Time - _history.TimeAt(0) : TimeSpan.Zero;
            HistoryInfo.Text = L($"記録期間: {span:hh\\:mm\\:ss}（{_history.Count:N0} サンプル）  ／  グラフにマウスを乗せると、その時刻の値を表示します");
        }
    }

    /// <summary>CSV の列名に使う表示名を登録する</summary>
    private void RegisterHistoryNames()
    {
        if (_catalog is null) return;
        foreach (var t in _catalog.Temperatures) _history.SetName(t.Id, $"{t.Name} ({t.HardwareName}) [°C]");
        foreach (var v in Config.VirtualSensors) _history.SetName(v.Id, L($"{v.Name} (仮想) [°C]"));
        foreach (var (id, name, unit) in MetricHistoryKeys) _history.SetName(id, $"{name} [{unit}]");
        foreach (var f in Config.Profiles.SelectMany(p => p.Fans).GroupBy(f => f.ControlId).Select(g => g.First()))
        {
            _history.SetName(HistoryStore.RpmKey(f.ControlId), $"{f.Name} [rpm]");
            _history.SetName(HistoryStore.DutyKey(f.ControlId), $"{f.Name} [%]");
        }
    }

    private void OnRangeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || HistoryGraph is null) return;
        HistoryGraph.Range = TimeSpan.FromMinutes(int.Parse(tag));
        HistoryGraph.Refresh();
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0)
        {
            MessageBox.Show(this, L("まだ記録がありません。"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = L("履歴を CSV に書き出す"),
            Filter = L("CSV ファイル (*.csv)|*.csv"),
            FileName = $"{AppIdentity.Name}_{DateTime.Now:yyyyMMdd_HHmm}.csv",
        };
        if (dialog.ShowDialog(this) != true) return;

        // 温度 → 回転数 → Duty の順に、記録しているすべての系列を書き出す
        var keys = _history.Keys
            .OrderBy(k => k.StartsWith("duty:") ? 2 : k.StartsWith("rpm:") ? 1 : 0)
            .ThenBy(k => _history.NameOf(k))
            .ToList();
        try
        {
            var from = _history.TimeAt(_history.Count - 1) - HistoryGraph.Range;
            int rows = _history.ExportCsv(dialog.FileName, from, keys);
            ShowAlert(L($"{rows:N0} 行を書き出しました: {dialog.FileName}"), AlertLevel.Info);
        }
        catch (Exception ex)
        {
            Logger.Error($"CSV 出力に失敗: {ex}");
            MessageBox.Show(this, L($"書き出しに失敗しました。\n{ex.Message}"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

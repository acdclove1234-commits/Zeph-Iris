// ==================================================
// MainWindow.Energy.cs - 設定画面の「ワットチェッカーで合わせ込む」 (コンセントで測った値と、CPU・GPU のセンサーの値を比べる)
//   計算の本体は Core/EnergyMeter.cs の EnergyCalibration
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Windows;

namespace ProFanController;

public partial class MainWindow
{
    private void OnRecordIdleWattsClick(object sender, RoutedEventArgs e) => RecordWallWatts(idle: true);

    private void OnRecordLoadWattsClick(object sender, RoutedEventArgs e) => RecordWallWatts(idle: false);

    /// <summary>入れたワットチェッカーの値と、直近 20 秒の CPU＋GPU のセンサーの平均を 1 組として記録し、合わせ込み直す</summary>
    private void RecordWallWatts(bool idle)
    {
        string text = (idle ? IdleWallBox : LoadWallBox).Text.Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double wall)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out wall))
        {
            WattCalibrationText.Text = L("ワットチェッカーの値を数字で入れてください");
            return;
        }
        var sensor = _energy.RecentSensorWatts();
        if (EnergyCalibration.CheckReading(wall, sensor) is { } problem)
        {
            WattCalibrationText.Text = problem;
            return;
        }
        var cfg = Config.Energy;
        if (idle)
        {
            cfg.IdleWallWatts = wall;
            cfg.IdleSensorWatts = sensor!.Value.Mean;
        }
        else
        {
            cfg.LoadWallWatts = wall;
            cfg.LoadSensorWatts = sensor!.Value.Mean;
        }
        ApplyCalibration();
    }

    /// <summary>記録した点から「CPU・GPU 以外」と「電源の効率」を求めて設定に入れる</summary>
    private void ApplyCalibration()
    {
        var cfg = Config.Energy;
        var r = EnergyCalibration.Solve(cfg.IdleWallWatts, cfg.IdleSensorWatts, cfg.LoadWallWatts, cfg.LoadSensorWatts, cfg.PsuEfficiency);
        if (r.Error is { } error)
        {
            RefreshCalibrationUi();
            WattCalibrationText.Text = error;
            return;
        }
        cfg.BaseWatts = Math.Round(r.BaseWatts);
        cfg.PsuEfficiency = Math.Round(r.EfficiencyPct);
        cfg.CalibratedAt = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _controller.Commit(recordUndo: false);
        Logger.Info($"電力をワットチェッカーで合わせ込み: その他 {cfg.BaseWatts:F0} W・効率 {cfg.PsuEfficiency:F0}%（{(r.TwoPoints ? "2 点" : "1 点")}）");
        RefreshCalibrationUi();
        string done = r.TwoPoints
            ? L($"合わせ込みました: CPU・GPU 以外 {cfg.BaseWatts:F0} W・電源の効率 {cfg.PsuEfficiency:F0}%（アイドル中と負荷中の 2 回から）")
            : L($"合わせ込みました: CPU・GPU 以外 {cfg.BaseWatts:F0} W（電源の効率は {cfg.PsuEfficiency:F0}% のまま。負荷中も記録すると効率も求めます）");
        WattCalibrationText.Text = r.Note is { } note ? done + "\n" + note : done;
        RefreshEnergy();
    }

    private void OnClearCalibrationClick(object sender, RoutedEventArgs e)
    {
        var cfg = Config.Energy;
        cfg.IdleWallWatts = cfg.IdleSensorWatts = cfg.LoadWallWatts = cfg.LoadSensorWatts = 0;
        cfg.BaseWatts = new EnergyConfig().BaseWatts;
        cfg.PsuEfficiency = EnergyConfig.DefaultPsuEfficiency;
        cfg.CalibratedAt = "";
        _controller.Commit(recordUndo: false);
        RefreshCalibrationUi();
        WattCalibrationText.Text = L("合わせ込みを消して、初めの目安（CPU・GPU 以外 50 W・電源の効率 88%）に戻しました");
        RefreshEnergy();
    }

    /// <summary>記録した点と、合わせ込んだ日を表示する（設定画面を開いたときにも呼ぶ）</summary>
    private void RefreshCalibrationUi()
    {
        var cfg = Config.Energy;
        IdlePointText.Text = cfg.IdleWallWatts > 0 ? L($"記録済み: コンセント {cfg.IdleWallWatts:F0} W ・ CPU＋GPU {cfg.IdleSensorWatts:F0} W") : "";
        LoadPointText.Text = cfg.LoadWallWatts > 0 ? L($"記録済み: コンセント {cfg.LoadWallWatts:F0} W ・ CPU＋GPU {cfg.LoadSensorWatts:F0} W") : "";
        bool calibrated = cfg.CalibratedAt.Length > 0;
        WattCalibrationText.Text = calibrated ? L($"{cfg.CalibratedAt} に合わせ込みました") : L("まだ合わせ込んでいません");
        ClearCalibrationButton.Visibility = calibrated || cfg.IdleWallWatts > 0 || cfg.LoadWallWatts > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

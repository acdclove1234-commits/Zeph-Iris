// ==================================================
// EnergyCalibrationTests.cs - 電源の効率と、ワットチェッカーでの合わせ込みの計算
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class EnergyCalibrationTests
{
    /// <summary>コンセント側 = PC の中で使う電力 ÷ 効率。範囲外の効率は初期値（88%）で計算する</summary>
    public static void TestWallWatts()
    {
        T.Near(EnergyCalibration.WallWatts(88, 88), 100, 1e-9, "88 W inside at 88% = 100 W at the wall");
        T.Near(EnergyCalibration.WallWatts(90, 90), 100, 1e-9, "90 W inside at 90% = 100 W");
        T.Near(EnergyCalibration.WallWatts(88, 30), 100, 1e-9, "an out-of-range efficiency falls back to 88%");
        T.Near(EnergyCalibration.WallWatts(-5, 88), 0, 1e-9, "negative inside power counts as 0");
    }

    /// <summary>メーターはコンセント側の電力を積算し、損失の分を分けて持つ</summary>
    public static void TestMeterIncludesLoss()
    {
        var m = new EnergyMeter();
        var t0 = new DateTime(2026, 11, 10, 12, 0, 0);
        m.Add(t0, 30, 14, 44, 88);
        T.Near(m.CurrentWatts, 100, 1e-9, "30 + 14 + 44 W inside at 88% = 100 W");
        T.Near(m.LossWatts, 12, 1e-9, "loss = 12 W");
        T.Near(m.BaseWatts, 44, 1e-9, "other parts = 44 W");
    }

    /// <summary>直近 20 秒の CPU＋GPU の平均と揺れの幅。5 回分より少なければ null</summary>
    public static void TestRecentSensorWatts()
    {
        var m = new EnergyMeter();
        var t0 = new DateTime(2026, 11, 10, 12, 0, 0);
        for (int i = 0; i < 4; i++) m.Add(t0.AddSeconds(i), 20, 10, 50);
        T.Check(m.RecentSensorWatts() is null, "fewer than 5 samples → null");
        for (int i = 4; i < 40; i++) m.Add(t0.AddSeconds(i), i < 30 ? 20 : 60, 10, 50);
        var r = m.RecentSensorWatts();
        T.Check(r is not null, "40 samples → value");
        // 20 秒の窓（19〜39 秒）には 30 W が 11 回、70 W が 10 回
        T.Near(r!.Value.Spread, 40, 1e-9, "spread = 70 − 30");
        T.Check(r.Value.Mean > 30 && r.Value.Mean < 70, $"mean between the two levels ({r.Value.Mean:F1})");
    }

    /// <summary>記録する前の確かめ: センサーがない・範囲外・揺れている・センサーより小さい</summary>
    public static void TestCheckReading()
    {
        T.Check(EnergyCalibration.CheckReading(80, null) is not null, "no sensor yet → refused");
        T.Check(EnergyCalibration.CheckReading(5, (30, 2)) is not null, "5 W → refused");
        T.Check(EnergyCalibration.CheckReading(80, (30, 20)) is not null, "spread 20 W at 30 W → refused");
        T.Check(EnergyCalibration.CheckReading(25, (30, 2)) is not null, "wall below the sensors → refused");
        T.Check(EnergyCalibration.CheckReading(80, (30, 2)) is null, "80 W with 30 W steady → accepted");
        T.Check(EnergyCalibration.CheckReading(450, (340, 40)) is null, "450 W with 340 W ± 20 W under load → accepted");
    }

    /// <summary>2 点: 本当の値（その他 45 W・効率 90%）から作ったワットチェッカーの値を、そのまま戻せる</summary>
    public static void TestSolveTwoPoints()
    {
        double Wall(double sensor) => (sensor + 45) / 0.90;
        var r = EnergyCalibration.Solve(Wall(30), 30, Wall(330), 330, 88);
        T.Check(r.Error is null && r.TwoPoints, "two points solve");
        T.Near(r.BaseWatts, 45, 1e-6, "other parts = 45 W");
        T.Near(r.EfficiencyPct, 90, 1e-6, "efficiency = 90%");
    }

    /// <summary>1 点（アイドル中だけ）: 効率は今の設定のまま、その他だけ求める</summary>
    public static void TestSolveIdleOnly()
    {
        var r = EnergyCalibration.Solve(100, 30, 0, 0, 88);
        T.Check(r.Error is null && !r.TwoPoints, "idle point solves");
        T.Near(r.BaseWatts, 58, 1e-6, "100 × 0.88 − 30 = 58 W");
        T.Near(r.EfficiencyPct, 88, 1e-9, "efficiency stays 88%");
        var load = EnergyCalibration.Solve(0, 0, 400, 300, 90);
        T.Check(load.Error is null && !load.TwoPoints, "a load point alone also solves");
        T.Near(load.BaseWatts, 60, 1e-6, "400 × 0.9 − 300 = 60 W");
    }

    /// <summary>負荷中の差が小さいと、効率は求めずに 1 点として扱い、そのことを知らせる</summary>
    public static void TestSolveSmallGapFallsBack()
    {
        var r = EnergyCalibration.Solve(100, 30, 130, 60, 88);
        T.Check(r.Error is null && !r.TwoPoints && r.Note is not null, "gap 30 W → one point with a note");
        T.Near(r.EfficiencyPct, 88, 1e-9, "efficiency unchanged");
    }

    /// <summary>ありえない組み合わせは使わない（効率が範囲外・その他がマイナス）</summary>
    public static void TestSolveRejectsNonsense()
    {
        // コンセントの差がセンサーの差より小さい → 効率が 100% を超える
        T.Check(EnergyCalibration.Solve(100, 30, 180, 330, 88).Error is not null, "efficiency above 98% → error");
        // コンセントの差が大きすぎる → 効率が 60% より下
        T.Check(EnergyCalibration.Solve(100, 30, 700, 330, 88).Error is not null, "efficiency below 60% → error");
        // アイドル中のコンセントが小さすぎる → その他がマイナス
        T.Check(EnergyCalibration.Solve(32, 30, 0, 0, 88).Error is not null, "negative other parts → error");
        T.Check(EnergyCalibration.Solve(0, 0, 0, 0, 88).Error is not null, "nothing recorded → error");
    }

    /// <summary>設定の効率がおかしい値なら、読み込むときに初期値へ戻す</summary>
    public static void TestConfigNormalizesEfficiency()
    {
        var cfg = new AppConfig();
        cfg.Energy.PsuEfficiency = 150;
        cfg.Normalize();
        T.Near(cfg.Energy.PsuEfficiency, EnergyConfig.DefaultPsuEfficiency, 1e-9, "150% → 88%");
        cfg.Energy.PsuEfficiency = 91;
        cfg.Normalize();
        T.Near(cfg.Energy.PsuEfficiency, 91, 1e-9, "91% kept");
    }
}

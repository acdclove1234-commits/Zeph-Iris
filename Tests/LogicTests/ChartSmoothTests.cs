// 履歴のグラフの「なめらかに」（ChartSmooth）。表示の計算だけを確かめる
using ProFanController;

namespace LogicTests;

public static class ChartSmoothTests
{
    public static void TestWindow()
    {
        T.Near(ChartSmooth.WindowSec(TimeSpan.FromMinutes(5)), 10, 1e-9, "5 分は 10 秒");
        T.Near(ChartSmooth.WindowSec(TimeSpan.FromMinutes(30)), 15, 1e-9, "30 分は 15 秒");
        T.Near(ChartSmooth.WindowSec(TimeSpan.FromHours(1)), 30, 1e-9, "1 時間は 30 秒");
        T.Near(ChartSmooth.WindowSec(TimeSpan.FromHours(6)), 180, 1e-9, "6 時間は 3 分");
        T.Near(ChartSmooth.WindowSec(TimeSpan.FromHours(24)), 600, 1e-9, "24 時間は 10 分まで");
        // 30 分を 1900 列で描くとき（1 列 ≒ 0.95 秒）は、前後 8 列ずつ
        T.Check(ChartSmooth.HalfColumns(TimeSpan.FromMinutes(30), 1800.0 / 1900) == 8, "30 分・1900 列なら前後 8 列");
        T.Check(ChartSmooth.HalfColumns(TimeSpan.FromHours(24), 86400.0 / 1900) == 7, "24 時間・1900 列なら前後 7 列");
        T.Check(ChartSmooth.HalfColumns(TimeSpan.FromMinutes(5), 0) == 0, "列の幅が 0 なら平均しない");
    }

    public static void TestStepSensorFlapping()
    {
        // 2°C 刻みのセンサー（グラボのメモリの温度）が 50 と 52 を 1 秒ごとに行ったり来たりする
        var v = Enumerable.Range(0, 200).Select(i => i % 3 == 0 ? 52.0 : 50.0).ToArray();
        var s = ChartSmooth.Columns(v, 8);
        double min = s.Skip(10).Take(180).Min(), max = s.Skip(10).Take(180).Max();
        T.Check(max - min < 0.3, $"行ったり来たりがならされる（幅 {max - min:F2}°C）");
        T.Near(s.Skip(10).Take(180).Average(), v.Average(), 0.05, "平均は変わらない");
    }

    public static void TestSlowTrendKept()
    {
        // ゆっくり上がる温度（10 分で 42→48°C）は、そのまま残る
        var v = Enumerable.Range(0, 600).Select(i => 42 + 6.0 * i / 599).ToArray();
        var s = ChartSmooth.Columns(v, 8);
        for (int i = 8; i < 592; i += 50) T.Near(s[i], v[i], 1e-9, $"まっすぐな上がり方は変わらない ({i})");
        T.Check(s[^1] > 47.8 && s[0] < 42.2, "端も大きくずれない");
    }

    public static void TestSpikeShrinks()
    {
        // CPU の 1 秒だけのトゲ（38→55°C）は小さくなる
        var v = Enumerable.Repeat(38.0, 100).ToArray();
        v[50] = 55;
        var s = ChartSmooth.Columns(v, 8);
        T.Check(s[50] < 40, $"一瞬のトゲは小さくなる（{s[50]:F1}°C）");
        T.Near(s[10], 38, 1e-9, "トゲから離れたところは変わらない");
    }

    public static void TestGapsStayEmpty()
    {
        var v = new[] { 40.0, double.NaN, 42, double.NaN, double.NaN, 44, 46 };
        var s = ChartSmooth.Columns(v, 1);
        T.Check(double.IsNaN(s[1]) && double.IsNaN(s[3]) && double.IsNaN(s[4]), "データのない列は空いたまま");
        T.Near(s[0], 40, 1e-9, "となりが空なら自分だけ");
        T.Near(s[2], 42, 1e-9, "前後が空なら自分だけ");
        T.Near(s[5], 45, 1e-9, "あるものだけで平均する");
        T.Near(s[6], 45, 1e-9, "端も、あるものだけで平均する");

        var off = ChartSmooth.Columns(v, 0);
        T.Check(off.Length == v.Length && off.Zip(v).All(p => p.First.Equals(p.Second)), "0 列なら元のまま");
        T.Check(!ReferenceEquals(off, v), "元の配列は書き換えない");
        T.Check(ChartSmooth.Columns(Array.Empty<double>(), 5).Length == 0, "空でも落ちない");
    }

    public static void TestConfigDefaultAndRoundTrip()
    {
        T.Check(new AppearanceConfig().SmoothHistory, "初期値はオン");
        T.Check(AppConfig.FromJson("{}").Appearance.SmoothHistory, "前の版の設定（項目なし）でもオン");
        var cfg = new AppConfig();
        cfg.Appearance.SmoothHistory = false;
        T.Check(!AppConfig.FromJson(cfg.ToJson()).Appearance.SmoothHistory, "オフが保存される");
    }
}

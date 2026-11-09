// ==================================================
// HealthAnalyzerTests.cs - ファンの劣化・ホコリの診断のテスト (季節の室温の変化・ファンカーブの変更・急な変化とじわじわした変化)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class HealthAnalyzerTests
{
    private const string Fan = "f1";
    private static readonly Dictionary<string, string> Roles = new() { [StatRoles.CpuTemp] = "cpuT", [StatRoles.CpuPower] = "cpuP" };

    private static AppConfig Cfg()
    {
        var cfg = new AppConfig();
        var p = new ProfileConfig { Name = "標準" };
        p.Fans.Add(new FanConfig { Name = "Front", ControlId = Fan });
        cfg.Profiles.Add(p);
        return cfg;
    }

    /// <summary>
    /// 20 日分の 1 分ごとの記録。毎日 2 時間休み（10 W）、2 時間負荷（100 W）。
    /// ambient(day) = 室温、r(day) = 冷えにくさ（°C/W）、duty(day) = 負荷のときのファンの指示、rpmScale(day) = 回転数の倍率
    /// </summary>
    private static List<MinuteRow> Rows(Func<int, double> ambient, Func<int, double> r, Func<int, double> loadDuty, Func<int, double> rpmScale, int days = 20)
    {
        var rows = new List<MinuteRow>();
        var start = DateTime.Today.AddDays(-days);
        var rnd = new Random(7);
        for (int d = 0; d < days; d++)
            for (int m = 0; m < 240; m++)
            {
                bool load = m >= 120;
                double p = load ? 100 : 10;
                double duty = load ? loadDuty(d) : 40;
                var row = new MinuteRow { Time = start.AddDays(d).AddHours(10).AddMinutes(m), Roles = Roles };
                row.Values["cpuT"] = (float)(ambient(d) + 8 + r(d) * p + rnd.NextDouble() * 0.5);
                row.Values["cpuP"] = (float)p;
                row.Values[HistoryStore.DutyKey(Fan)] = (float)duty;
                row.Values[HistoryStore.RpmKey(Fan)] = (float)((300 + 15 * duty) * rpmScale(d) + rnd.Next(-8, 8));
                row.DutySpread[HistoryStore.DutyKey(Fan)] = 0;
                rows.Add(row);
            }
        return rows;
    }

    private static HealthItem Get(List<HealthItem> items, string title) => items.First(i => i.Title == title);

    public static void TestCoolingIgnoresTheSeason()
    {
        // 室温が 20°C → 30°C に上がる（夏に向かう）。ホコリはない。室温のセンサーもない
        var rows = Rows(d => 20 + d * 0.5, _ => 0.4, _ => 70, _ => 1);
        var cool = Get(HealthAnalyzer.Analyze(rows, Cfg(), DateTime.Today), "CPU の冷え方");
        T.Check(cool.Level == HealthLevel.Good, $"room 20 → 30°C, same cooling → good ({cool.Level}, {cool.Change:P0})");
    }

    public static void TestCoolingFindsGradualDust()
    {
        // 20 日で 50% 冷えにくくなる（最初の 1 週間と最後の 1 週間の平均では、約 30% の差）
        var rows = Rows(_ => 24, d => 0.4 * (1 + 0.5 * d / 19.0), _ => 70, _ => 1);
        var cool = Get(HealthAnalyzer.Analyze(rows, Cfg(), DateTime.Today), "CPU の冷え方");
        T.Check(cool.Level == HealthLevel.Warning && cool.Detail.Contains("ホコリ"), $"cooling slowly 50% worse over 20 days → warning about dust ({cool.Level}, {cool.Change:P0})");
    }

    public static void TestCoolingDoesNotMixMethods()
    {
        // 2026-11 に実際に起きた形: 最初の 13 日は一日中 CPU が 25 W 以上で、休んでいる時間がない（室温のセンサーとの差で求める）。
        // その「室温」はマザーボードの上の温度で、負荷と一緒に上がる。後半は休む時間がある。冷え方（0.4°C/W）はずっと同じ
        var roles = new Dictionary<string, string> { [StatRoles.CpuTemp] = "cpuT", [StatRoles.CpuPower] = "cpuP", [StatRoles.Ambient] = "amb" };
        var rows = new List<MinuteRow>();
        var start = DateTime.Today.AddDays(-20);
        var rnd = new Random(11);
        for (int d = 0; d < 20; d++)
            for (int m = 0; m < 240; m++)
            {
                bool noIdleDay = d < 13;
                double p = m >= 120 ? 50 : noIdleDay ? 25 : 10;
                var row = new MinuteRow { Time = start.AddDays(d).AddHours(10).AddMinutes(m), Roles = roles };
                row.Values["cpuT"] = (float)(24 + 8 + 0.4 * p + rnd.NextDouble() * 0.5);
                row.Values["amb"] = (float)(24 + 8 + 0.15 * p);
                row.Values["cpuP"] = (float)p;
                row.Values[HistoryStore.DutyKey(Fan)] = 50f;
                row.Values[HistoryStore.RpmKey(Fan)] = (float)(1050 + rnd.Next(-8, 8));
                row.DutySpread[HistoryStore.DutyKey(Fan)] = 0;
                rows.Add(row);
            }
        var cool = Get(HealthAnalyzer.Analyze(rows, Cfg(), DateTime.Today), "CPU の冷え方");
        T.Check(cool.Level is not (HealthLevel.Warning or HealthLevel.Info),
            $"baseline only by the board sensor, recent only by idle → not compared, no false warning ({cool.Level}, {cool.Metric})");
    }

    public static void TestRoomSensorChoice()
    {
        var roles = new Dictionary<string, string> { [StatRoles.Ambient] = "amb" };
        var row = new MinuteRow { Time = DateTime.Today, Roles = roles };
        row.Values["amb"] = 36f;
        row.Values["room"] = 22f;
        T.Check(HealthAnalyzer.AmbientReader(null)(row) == 36f && HealthAnalyzer.AmbientReader("")(row) == 36f, "automatic → the sensor recorded as room temperature");
        T.Check(HealthAnalyzer.AmbientReader("room")(row) == 22f, "a chosen sensor → that sensor's value");
        T.Check(HealthAnalyzer.AmbientReader(MaintenanceConfig.NoAmbientSensor)(row) is null, "don't use one → no room temperature");
        T.Check(HealthAnalyzer.AmbientReader("missing")(row) is null, "chosen sensor not in the record → no room temperature");

        // 休む時間のない日だけの記録で、後半に 30% 冷えにくくなる。室温のセンサーを使えば見つけ、「使わない」なら比べない
        var rows = new List<MinuteRow>();
        var start = DateTime.Today.AddDays(-20);
        var rnd = new Random(3);
        var rowRoles = new Dictionary<string, string> { [StatRoles.CpuTemp] = "cpuT", [StatRoles.CpuPower] = "cpuP", [StatRoles.Ambient] = "amb" };
        for (int d = 0; d < 20; d++)
            for (int m = 0; m < 240; m++)
            {
                double p = m % 2 == 0 ? 100 : 40;
                var r = new MinuteRow { Time = start.AddDays(d).AddHours(10).AddMinutes(m), Roles = rowRoles };
                r.Values["cpuT"] = (float)(25 + p * 0.4 * (d >= 13 ? 1.3 : 1.0) + rnd.NextDouble());
                r.Values["cpuP"] = (float)p;
                r.Values["amb"] = 25f;
                rows.Add(r);
            }
        var cfg = Cfg();
        var auto = Get(HealthAnalyzer.Analyze(rows, cfg, DateTime.Today), "CPU の冷え方");
        T.Check(auto.Level == HealthLevel.Warning, $"room sensor used → worse cooling found ({auto.Level})");
        cfg.Maintenance.AmbientSensorId = MaintenanceConfig.NoAmbientSensor;
        var none = Get(HealthAnalyzer.Analyze(rows, cfg, DateTime.Today), "CPU の冷え方");
        T.Check(none.Level == HealthLevel.Collecting, $"room sensor not used and no idle time → not compared ({none.Level})");
    }

    public static void TestCoolingSeesAFanCurveChange()
    {
        // 12 日目から静かなカーブ（負荷のときの指示 70% → 55%）。そのぶん温度も上がる
        var rows = Rows(_ => 24, d => d >= 12 ? 0.52 : 0.4, d => d >= 12 ? 55 : 70, _ => 1);
        var cool = Get(HealthAnalyzer.Analyze(rows, Cfg(), DateTime.Today), "CPU の冷え方");
        T.Check(cool.Level == HealthLevel.Info && cool.Detail.Contains("ファンカーブ"), $"quieter fan curve → says the setting changed, not dust ({cool.Level}: {cool.Metric})");
    }

    public static void TestFanChangeShapes()
    {
        var cfg = Cfg();
        var gradual = Get(HealthAnalyzer.Analyze(Rows(_ => 24, _ => 0.4, _ => 70, d => 1 - 0.18 * d / 19.0), cfg, DateTime.Today), "Front");
        T.Check(gradual.Level >= HealthLevel.Warning && gradual.Detail.Contains("少しずつ") && gradual.Detail.Contains("軸受け"),
            $"rpm slowly 18% lower → warning, wear ({gradual.Level}, {gradual.Change:P0})");

        var sudden = Get(HealthAnalyzer.Analyze(Rows(_ => 24, _ => 0.4, _ => 70, d => d >= 15 ? 0.85 : 1), cfg, DateTime.Today), "Front");
        T.Check(sudden.Level >= HealthLevel.Warning && sudden.Detail.Contains("急に") && sudden.Detail.Contains("ケーブル"),
            $"rpm suddenly 15% lower → warning, something touching ({sudden.Level}: {sudden.Detail})");

        var swapped = Get(HealthAnalyzer.Analyze(Rows(_ => 24, _ => 0.4, _ => 70, d => d >= 15 ? 1.18 : 1), cfg, DateTime.Today), "Front");
        T.Check(swapped.Level == HealthLevel.Info && swapped.Detail.Contains("交換"), $"rpm suddenly 18% higher → info, replaced? ({swapped.Level})");

        var clogged = Get(HealthAnalyzer.Analyze(Rows(_ => 24, _ => 0.4, _ => 70, d => 1 + 0.35 * d / 19.0), cfg, DateTime.Today), "Front");
        T.Check(clogged.Level == HealthLevel.Warning && clogged.Detail.Contains("目詰まり"), $"rpm slowly 35% higher over 20 days → warning, clogged intake ({clogged.Level}, {clogged.Change:P0})");

        var fine = Get(HealthAnalyzer.Analyze(Rows(_ => 24, _ => 0.4, _ => 70, _ => 1), cfg, DateTime.Today), "Front");
        T.Check(fine.Level == HealthLevel.Good && fine.Detail.Contains("比べた時間"), $"no change → good, with the compared time ({fine.Detail})");
    }

    public static void TestBaselineNeedsEnoughData()
    {
        // 最初の 7 日は 60% で回っていない（基準に、比べられる Duty がない）
        var rows = Rows(_ => 24, _ => 0.4, d => d < 8 ? 90 : 70, _ => 1);
        var fan = Get(HealthAnalyzer.Analyze(rows, Cfg(), DateTime.Today), "Front");
        T.Check(fan.Level is HealthLevel.Collecting or HealthLevel.Good, $"no shared duty with enough data → not a false alarm ({fan.Level})");
    }
}

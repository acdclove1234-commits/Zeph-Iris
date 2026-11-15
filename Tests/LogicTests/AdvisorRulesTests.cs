// AI 冷却アドバイザー: アプリが決まったルールで作る提案・温度の目安・記録の写し（AI に数値を作らせない）
using ProFanController;

namespace LogicTests;

public static class AdvisorRulesTests
{
    private static List<CurvePoint> P(params (double t, double d)[] pts) => pts.Select(x => new CurvePoint { Temp = x.t, Duty = x.d }).ToList();

    public static void TestSuggestCurve()
    {
        var gpu = P((43, 30), (66, 43), (73, 59), (79, 79), (85, 100));
        var quiet = AdvisorPrompt.SuggestCurve(gpu, sensorMax: 59, limit: 83, out double s);
        T.Check(quiet is not null && s == 4, $"24 °C margin: shift right, capped so it does not pass the 100% point ({s})");
        T.Check(quiet![^1].Temp == 85 && quiet[^1].Duty == 100, "the full-speed point stays");
        T.Check(AdvisorPrompt.CurveDuty(quiet, 59) < AdvisorPrompt.CurveDuty(gpu, 59), "quieter at the hottest recorded temperature");
        for (int i = 1; i < quiet.Count; i++) T.Check(quiet[i].Temp > quiet[i - 1].Temp, $"points stay in order ({quiet[i - 1].Temp} < {quiet[i].Temp})");

        var flat = P((38, 25), (71, 25), (78, 25), (84, 62), (90, 100));
        T.Check(AdvisorPrompt.SuggestCurve(flat, 49, 90, out _) is null, "curve already at its floor in the recorded range: no suggestion");

        var mid = P((40, 30), (70, 60), (85, 100));
        T.Check(AdvisorPrompt.SuggestCurve(mid, 72, 83, out _) is null, "11 °C margin: leave it");

        var hot = P((40, 30), (70, 60), (85, 100));
        var cooler = AdvisorPrompt.SuggestCurve(hot, 80, 83, out double s2);
        T.Check(cooler is not null && s2 == -5 && AdvisorPrompt.CurveDuty(cooler, 80) > AdvisorPrompt.CurveDuty(hot, 80), $"3 °C margin: spin up earlier ({s2})");

        var odd = P((40, 60), (60, 30), (80, 100));
        T.Check(AdvisorPrompt.SuggestCurve(odd, 50, 90, out _) is null, "curves that go down somewhere are not touched");
    }

    public static void TestLimits()
    {
        T.Check(AdvisorPrompt.LimitFor("/gpu-nvidia/0/temperature/2", "GPU Hot Spot") == 95, "hot spot limit");
        T.Check(AdvisorPrompt.LimitFor("/gpu-nvidia/0/temperature/0", "GPU") == 83, "GPU core limit");
        T.Check(AdvisorPrompt.LimitFor("/intelcpu/0/temperature/16", "CPU") == 90, "CPU limit");
        T.Check(AdvisorPrompt.LimitFor("/nvme/2/temperature/0", "SSD") == 70, "SSD limit");
        T.Check(AdvisorPrompt.LimitFor("/lpc/nct6686d/0/temperature/1", "Inside the case") is null, "board sensors get no limit (what they measure varies)");
    }

    public static void TestHistorySnapshot()
    {
        var h = new HistoryStore();
        var t0 = new DateTime(2026, 11, 12, 10, 0, 0);
        // リングバッファが一周しても、写しは古い順に同じ値を持つ
        int n = HistoryStore.Capacity + 5;
        for (int i = 0; i < n; i++) h.AddRaw(t0.AddSeconds(i), new Dictionary<string, float> { ["x"] = i });
        var c = h.Snapshot();
        T.Check(c.Count == h.Count, "same count");
        bool same = true;
        foreach (int i in new[] { 0, 1, 4, 5, 1000, h.Count - 1 })
            same &= c.ValueAt("x", i) == h.ValueAt("x", i) && c.TimeAt(i) == h.TimeAt(i);
        T.Check(same, "same values and times after the ring wrapped");
        h.AddRaw(t0.AddSeconds(n), new Dictionary<string, float> { ["x"] = -1 });
        T.Check(c.ValueAt("x", c.Count - 1) == n - 1, "the copy does not change when the original gets new samples");
    }

    // =====================================================================
    // センサーの選び間違い（設定だけで分かる）
    // =====================================================================

    private const string CpuT = "/intelcpu/0/temperature/16", GpuT = "/gpu-nvidia/0/temperature/0", Ssd = "/nvme/2/temperature/0";
    private const string Board = "/lpc/nct6686d/0/temperature/1", BoardCpu = "/lpc/nct6686d/0/temperature/0";

    private static readonly List<SensorInfo> Temps = new()
    {
        new(CpuT, "CPU Package", "Intel Core i7", "Cpu", "Temperature"),
        new(GpuT, "GPU Core", "NVIDIA GeForce RTX", "GpuNvidia", "Temperature"),
        new(Ssd, "Composite Temperature", "NVMe SSD", "Storage", "Temperature"),
        new(Board, "System", "Nuvoton NCT6686D", "SuperIO", "Temperature"),
        new(BoardCpu, "CPU", "Nuvoton NCT6686D", "SuperIO", "Temperature"),
    };

    private static AppConfig MismatchConfig(params (string Profile, FanConfig Fan)[] fans)
    {
        var cfg = new AppConfig();
        foreach (var g in fans.GroupBy(f => f.Profile))
        {
            var p = new ProfileConfig { Name = g.Key };
            foreach (var (_, fan) in g) p.Fans.Add(fan);
            cfg.Profiles.Add(p);
        }
        cfg.Normalize();
        return cfg;
    }

    private static CurveConfig Curve(string name, string sensor) => new() { Name = name, SensorId = sensor };

    /// <summary>
    /// 静音プロファイルのラジエーターの「GPU」カーブが SSD の温度を見ている（実際にあった設定）: 警告し、1 クリックで GPU 温度に直せる。
    /// 正しい設定・ほかのプロファイルには何も出さない
    /// </summary>
    public static void TestCurveOnWrongSensorKind()
    {
        var radiator = new FanConfig { Name = "Radiator", ControlId = "/lpc/nct6686d/0/control/0", Curves = { Curve("CPU", CpuT), Curve("GPU", Ssd) } };
        var good = new FanConfig { Name = "Radiator", ControlId = "/lpc/nct6686d/0/control/0", Curves = { Curve("CPU", CpuT), Curve("GPU", GpuT) } };
        var cfg = MismatchConfig(("静音", radiator), ("標準", good));
        var found = Diagnostics.SensorMismatches(cfg, Temps);
        T.Check(found.Count == 1, $"one finding for the wrong curve only ({found.Count})");
        var f = found[0];
        T.Check(f.Level == FindingLevel.Warning && f.Title.Contains("Radiator") && f.Title.Contains("GPU"), $"warning names the fan and curve ({f.Title})");
        T.Check(f.Detail.Contains("静音") && f.Detail.Contains("Composite Temperature") && f.Detail.Contains("ストレージ"), $"detail names the profile, the sensor and its kind ({f.Detail})");
        T.Check(f.Apply is not null && f.ActionLabel?.Contains("GPU Core") == true, $"one-click remap offered ({f.ActionLabel})");
        var points = cfg.FindProfile("静音")!.Fans[0].Curves[1].Points.Select(p => (p.Temp, p.Duty)).ToList();
        f.Apply!(cfg);
        var fixedCurve = cfg.FindProfile("静音")!.Fans[0].Curves[1];
        T.Check(fixedCurve.SensorId == GpuT, "remapped to the GPU core temperature");
        T.Check(fixedCurve.Points.Select(p => (p.Temp, p.Duty)).SequenceEqual(points), "the user's curve points are kept (it was drawn as a GPU curve)");
        T.Check(cfg.FindProfile("静音")!.Fans[0].Curves[0].SensorId == CpuT, "the CPU curve is untouched");
        T.Check(Diagnostics.SensorMismatches(cfg, Temps).Count == 0, "after the fix: no finding");

        // CPU のカーブが GPU を見ている・GPU のカーブが CPU を見ている
        var swapped = MismatchConfig(("標準", new FanConfig { Name = "Front", ControlId = "f", Curves = { Curve("CPU", GpuT) } }),
                                     ("ゲーム", new FanConfig { Name = "Top", ControlId = "t", Curves = { Curve("GPU（自動調整）", CpuT) } }));
        var sw = Diagnostics.SensorMismatches(swapped, Temps);
        T.Check(sw.Count == 2 && sw.All(x => x.Level == FindingLevel.Warning && x.Apply is not null), $"CPU↔GPU swapped curves: both warned ({sw.Count})");
        sw[0].Apply!(swapped);
        sw[1].Apply!(swapped);
        T.Check(swapped.FindProfile("標準")!.Fans[0].Curves[0].SensorId == CpuT && swapped.FindProfile("ゲーム")!.Fans[0].Curves[0].SensorId == GpuT, "each remapped to its own part");
    }

    /// <summary>
    /// マザーボードの温度: 名前が合う（SuperIO の「CPU」）なら何も出さない。合わないときは知らせるだけ（Info）。
    /// 名前に CPU も GPU もない・両方あるカーブ、仮想センサー、今つながっていないセンサー、ポンプは見ない
    /// </summary>
    public static void TestMismatchIgnoresAmbiguousCases()
    {
        var cfg = MismatchConfig(("標準", new FanConfig
        {
            Name = "Front", ControlId = "f",
            Curves =
            {
                Curve("CPU", BoardCpu),                     // SuperIO の CPU 温度: 合っている
                Curve("ケース", Ssd),                       // 名前に CPU・GPU がない
                Curve("CPU+GPU", Ssd),                      // 両方ある
                Curve("GPU", "/virtual/abcd1234"),          // 仮想センサー
                Curve("GPU", "/nvme/9/temperature/0"),      // 今は見つからない
            },
        }), ("標準", new FanConfig { Name = "Pump", ControlId = "p", Role = FanRoles.Pump, Curves = { Curve("CPU", Ssd) } }));
        T.Check(Diagnostics.SensorMismatches(cfg, Temps).Count == 0, "no finding for matching board sensor, unnamed, mixed, virtual, missing or pump curves");

        var board = MismatchConfig(("標準", new FanConfig { Name = "Front", ControlId = "f", Curves = { Curve("GPU", Board) } }));
        var b = Diagnostics.SensorMismatches(board, Temps);
        T.Check(b.Count == 1 && b[0].Level == FindingLevel.Info && b[0].Detail.Contains("System"), $"GPU curve on the board 'System' sensor: info only ({b.FirstOrDefault()?.Level})");
        T.Check(Diagnostics.CurveIntent("CPU（自動調整）") == SensorKind.Cpu && Diagnostics.CurveIntent("GPU Hot Spot") == SensorKind.Gpu
                && Diagnostics.CurveIntent("CPU/GPU") is null && Diagnostics.CurveIntent("SSD") is null, "intent from the curve name");
        T.Check(Diagnostics.IsStorageSensor("/hdd/0/temperature/0", Temps) && !Diagnostics.IsStorageSensor(CpuT, Temps), "storage detection");
    }

    /// <summary>GPU のファン（GPU の端子・名前に GPU）が、ストレージの温度だけで動いている: 警告し、GPU 温度の標準カーブに直せる</summary>
    public static void TestGpuFanOnlyOnStorage()
    {
        var gpuFan = new FanConfig { Name = "GPU Fan", ControlId = "/gpu-nvidia/0/control/0", Curves = { Curve("SSD", Ssd) } };
        var named = new FanConfig { Name = "GPU 排気", ControlId = "/lpc/nct6686d/0/control/3", Curves = { Curve("カーブ", Ssd) } };
        var mixed = new FanConfig { Name = "GPU side", ControlId = "/lpc/nct6686d/0/control/4", Curves = { Curve("SSD", Ssd), Curve("温度", GpuT) } };
        var caseFan = new FanConfig { Name = "Bottom", ControlId = "/lpc/nct6686d/0/control/5", Curves = { Curve("SSD", Ssd) } };
        var cfg = MismatchConfig(("標準", gpuFan), ("標準", named), ("標準", mixed), ("標準", caseFan));
        var found = Diagnostics.SensorMismatches(cfg, Temps);
        T.Check(found.Count == 2 && found.All(f => f.Level == FindingLevel.Warning), $"GPU-port fan and GPU-named fan on storage only: warned; mixed inputs and a case fan on SSD: not ({found.Count})");
        T.Check(found.Any(f => f.Title.StartsWith("GPU Fan")) && found.Any(f => f.Title.StartsWith("GPU 排気")), "both fans named");
        found[0].Apply!(cfg);
        var c = cfg.FindProfile("標準")!.Fans[0].Curves[0];
        T.Check(c.SensorId == GpuT && c.Points.Select(p => (p.Temp, p.Duty)).SequenceEqual(CurvePresets.Create(CurvePresets.Balanced, SensorKind.Gpu).Select(p => (p.Temp, p.Duty))),
            "switched to the GPU core with the standard GPU curve (SSD temperatures do not fit a GPU)");

        // 診断の画面: 記録が足りなくても、設定の間違いは出す
        var d = Diagnostics.Run(new HistoryStore(), cfg, "標準", Temps, TimeSpan.FromMinutes(30));
        T.Check(d.Any(f => f.Title.Contains("GPU 排気")), "Diagnostics.Run shows the mismatch even with too little history");
    }
}

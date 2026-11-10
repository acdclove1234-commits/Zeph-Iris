// ==================================================
// LegacyTests.cs - これまでのファン制御まわりのロジックテスト（v6.1.4 までのもの）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Buffers.Binary;
using System.Text;
using ProFanController;

namespace LogicTests;

public static class LegacyTests
{
    public static void TestFanLogic()
    {
        void Check(bool ok, string name) => T.Check(ok, name);
        var t0 = new DateTime(2026, 10, 23, 12, 0, 0);

        // ===== A3. 異常値フィルター
        {
            var g = new SensorGuard();
            double? r1 = g.Filter("cpu", 45, 5, t0);
            double? spike = g.Filter("cpu", 120, 5, t0.AddSeconds(1));     // 1 回だけ跳ねる
            double? back = g.Filter("cpu", 46, 5, t0.AddSeconds(2));
            Check(r1 == 45 && spike == 45 && back == 46, $"spike is ignored ({r1},{spike},{back})");

            var g2 = new SensorGuard();
            g2.Filter("cpu", 40, 5, t0);
            var a = g2.Filter("cpu", 85, 100, t0.AddSeconds(1));
            var b = g2.Filter("cpu", 87, 100, t0.AddSeconds(2));
            Check(a == 40 && b == 87, $"real jump accepted after 1 tick ({a},{b})");

            var g3 = new SensorGuard();
            g3.Filter("cpu", 35, 5, t0);
            double?[] climb = { 55, 68, 80, 90 };
            double? last = null;
            for (int i = 0; i < climb.Length; i++) last = g3.Filter("cpu", climb[i], 100, t0.AddSeconds(i + 1));
            Check(last == 90, $"fast climb accepted within 3 ticks (last={last})");

            var g4 = new SensorGuard();
            string? stuckId = null;
            g4.StuckDetected += (id, _) => stuckId = id;
            double? v = null;
            for (int i = 0; i < 300; i++) v = g4.Filter("gpu", 52, i < 100 ? 3 : 97, t0.AddSeconds(i));
            Check(stuckId == "gpu" && v is null, "frozen sensor while load swings → stuck");
            var g5 = new SensorGuard();
            for (int i = 0; i < 600; i++) v = g5.Filter("sys", 34, 5, t0.AddSeconds(i));
            Check(v == 34 && g5.Suspects.Count == 0, "constant value at idle is NOT stuck");
            var after = g4.Filter("gpu", 60, 97, t0.AddSeconds(301));
            Check(after is not null && g4.Suspects.Count == 0, "stuck clears when value changes");
        }

        // ===== B7. 共振回避帯
        {
            var fan = new FanConfig { AvoidBands = new() { new DutyBand { From = 40, To = 50 } } };
            Check(FanEngine.PassThroughBands(fan, 35, 43) == 50, "ramp up jumps over band");
            Check(FanEngine.PassThroughBands(fan, 55, 48) == 40, "ramp down jumps over band");
            Check(FanEngine.PassThroughBands(fan, 35, 39) == 39, "outside band unchanged");
            Check(FanEngine.PassThroughBands(fan, 50, 50) == 50, "edge is allowed");
            fan.AvoidBands.Add(new DutyBand { From = 80, To = 70 });
            fan.Normalize();
            Check(fan.AvoidBands[1].From == 70 && fan.AvoidBands[1].To == 80, "inverted band normalized");
            var low = new FanConfig { AvoidBands = new() { new DutyBand { From = 20, To = 35 } } };
            Check(FanEngine.PassThroughBands(low, 40, 30, floor: 25) == 35, "ramp down never goes below min duty via band");
            Check(FanEngine.PassThroughBands(low, 40, 30, floor: 0) == 20, "ramp down to band bottom when allowed");
        }

        // ===== B6. 自動調整のカーブ
        {
            var steps = new List<AutoTuneStep>
            {
                new() { Duty = 100, SteadyTemp = 70 },
                new() { Duty = 80, SteadyTemp = 73 },
                new() { Duty = 65, SteadyTemp = 76 },
                new() { Duty = 50, SteadyTemp = 81 },
                new() { Duty = 40, SteadyTemp = 86 },
            };
            var (need, insuff) = AutoTuner.RequiredDuty(steps, 80);
            // goal 78.5: between 65 (76) and 50 (81) → 50 + (81-78.5)/(81-76)*15 = 57.5
            Check(!insuff && Math.Abs(need - 57.5) < 0.01, $"required duty interpolated ({need:F1})");
            var curve = AutoTuner.BuildCurve(steps, 38, 80, 25);
            Console.WriteLine("     curve: " + string.Join(" → ", curve.Select(p => $"{p.Temp}°C:{p.Duty}%")));
            Check(curve.Count == 5 && curve[2].Temp == 78 && curve[2].Duty == 58 && curve[4].Temp == 90 && curve[4].Duty == 100, "curve hits required duty 2°C before target, 100% at target + 10");
            Check(curve.Zip(curve.Skip(1)).All(z => z.Second.Temp > z.First.Temp && z.Second.Duty >= z.First.Duty), "curve monotonic");
            // 必要な回転数が低くても、目標を少し超えたところで最低回転から全開へ跳ね上がらない（1°C あたり 8% まで）
            var easy = new List<AutoTuneStep> { new() { Duty = 100, SteadyTemp = 60 }, new() { Duty = 25, SteadyTemp = 70 } };
            var gentle = AutoTuner.BuildCurve(easy, 38, 80, 25);
            double steepest = gentle.Zip(gentle.Skip(1)).Max(z => (z.Second.Duty - z.First.Duty) / (z.Second.Temp - z.First.Temp));
            Check(steepest <= 8, $"no cliff above the target ({steepest:F1}%/°C): " + string.Join(" → ", gentle.Select(p => $"{p.Temp}°C:{p.Duty}%")));
            var (n2, i2) = AutoTuner.RequiredDuty(steps, 70);
            Check(i2 && n2 == 100, "target unreachable → insufficient");
            var c2 = AutoTuner.BuildCurve(steps, 38, 70, 25);
            Check(c2[^1].Duty == 100 && c2[^1].Temp == 66, $"insufficient curve reaches 100% early ({c2[^1].Temp})");
            var (n3, i3) = AutoTuner.RequiredDuty(steps, 95);
            Check(!i3 && n3 == 40, "everything below target → lowest measured duty");
            // 待機温度が高い場合でも点が逆転しない
            var c3 = AutoTuner.BuildCurve(steps, 70, 80, 25);
            Check(c3.Zip(c3.Skip(1)).All(z => z.Second.Temp > z.First.Temp), "high idle temp keeps order");
        }

        // ===== A1. 部品交換の検知と対応付け
        {
            var cfg = new AppConfig();
            var p = new ProfileConfig { Name = "標準" };
            var fan = new FanConfig { Name = "CaseFan", ControlId = "/lpc/nct6686d/0/control/3", RpmSensorId = "/lpc/nct6686d/0/fan/3" };
            fan.Curves.Add(new CurveConfig { Name = "CPU", SensorId = "/intelcpu/0/temperature/16" });
            fan.Curves.Add(new CurveConfig { Name = "GPU", SensorId = "/gpu-amd/0/temperature/0" });
            fan.Curves.Add(new CurveConfig { Name = "HS", SensorId = "/gpu-amd/0/temperature/2" });
            var gfan = new FanConfig { Name = "GPU Fan", ControlId = "/gpu-amd/0/control/0" };
            gfan.Curves.Add(new CurveConfig { Name = "GPU", SensorId = "/gpu-amd/0/temperature/0" });
            p.Fans.Add(fan); p.Fans.Add(gfan);
            cfg.Profiles.Add(p);
            cfg.DashboardCards.Add(new DashboardCard { Title = "GPU", SensorId = "/gpu-amd/0/temperature/0" });
            cfg.SensorLabels["/gpu-amd/0/temperature/0"] = new SensorLabel { Name = "GPU Core", Hardware = "AMD Radeon RX 7900 XTX", HardwareType = "GpuAmd", SensorType = "Temperature" };
            cfg.SensorLabels["/gpu-amd/0/temperature/2"] = new SensorLabel { Name = "GPU Hot Spot", Hardware = "AMD Radeon RX 7900 XTX", HardwareType = "GpuAmd", SensorType = "Temperature" };
            cfg.KnownDevices.Add(new KnownDevice { Key = "/gpu-amd/0", Name = "AMD Radeon RX 7900 XTX", Kind = "Gpu" });
            cfg.Safety.AllowGpuFanControl = true;
            cfg.Normalize();

            SensorInfo S(string id, string name, string hw, string type, string st = "Temperature") => new(id, name, hw, type, st);
            var temps = new List<SensorInfo>
            {
                S("/intelcpu/0/temperature/16", "CPU Package", "Intel Core Ultra 5 245KF", "Cpu"),
                S("/gpu-nvidia/0/temperature/0", "GPU Core", "NVIDIA GeForce RTX 5070 Ti", "GpuNvidia"),
                S("/gpu-nvidia/0/temperature/2", "GPU Memory Junction", "NVIDIA GeForce RTX 5070 Ti", "GpuNvidia"),
                S("/lpc/nct6686d/0/temperature/0", "System", "Nuvoton NCT6686D", "SuperIO"),
            };
            var controls = new List<ControlInfo> { new("/lpc/nct6686d/0/control/3", "Fan #4", "Nuvoton NCT6686D", "/lpc/nct6686d/0/fan/3") };
            var groups = new List<HardwareGroup> { new("all", "x", temps.Concat(new[] { S("/lpc/nct6686d/0/fan/3", "Fan #4", "Nuvoton NCT6686D", "SuperIO", "Fan") }).ToList()) };
            var devices = new List<DeviceInfo>
            {
                new("/gpu-nvidia/0", "NVIDIA GeForce RTX 5070 Ti", "Gpu", null, 3, 2, 2, new List<string>()),
                new("/intelcpu/0", "Intel Core Ultra 5 245KF", "Cpu", null, 10, 0, 0, new List<string>()),
            };
            var catalog = new HardwareCatalog(devices, groups, temps, controls, true);

            var report = HardwareChange.Detect(cfg, catalog);
            foreach (var d in report.DeviceChanges) Console.WriteLine("     " + d);
            foreach (var i in report.Items) Console.WriteLine($"     {(i.IsControl ? "端子" : "温度")} {i.OldDisplay} → {i.NewId ?? "(なし)"}  [{i.Note}] {i.Usage}");
            Check(report.DeviceChanges.Any(d => d.Contains("RTX 5070 Ti")), "GPU swap reported");
            var core = report.Items.First(i => i.OldId == "/gpu-amd/0/temperature/0");
            var hs = report.Items.First(i => i.OldId == "/gpu-amd/0/temperature/2");
            Check(core.NewId == "/gpu-nvidia/0/temperature/0", "GPU Core → new GPU Core");
            Check(hs.NewId == "/gpu-nvidia/0/temperature/0" && hs.Note.Contains("Hot Spot"), "Hot Spot (missing on RTX 50) → GPU Core with note");
            var gctl = report.Items.First(i => i.IsControl);
            Check(gctl.OldId == "/gpu-amd/0/control/0" && gctl.NewId is null, "old GPU fan has no match");
            Check(!report.Items.Any(i => i.OldId.Contains("intelcpu")), "unchanged CPU sensor not listed");

            HardwareChange.Apply(cfg, report.Items, catalog);
            var f = cfg.Profiles[0].Fans;
            Check(f.Count == 1 && f[0].Curves.All(c => !c.SensorId.Contains("gpu-amd")), "curves remapped, old GPU fan removed");
            Check(f[0].Enabled, "fan on unchanged header stays enabled after GPU swap");
            Check(cfg.DashboardCards[0].SensorId == "/gpu-nvidia/0/temperature/0", "dashboard card remapped");
            Check(HardwareChange.Remember(cfg, catalog) && cfg.KnownDevices.Any(d => d.Name.Contains("5070")), "known devices updated");
            Check(HardwareChange.Detect(cfg, catalog).Items.Count == 0, "no changes after remap");

            // ===== B8. 書き出し → 別の PC で読み込み
            var pkg = ConfigTransfer.Export(cfg, catalog, new[] { "標準" });
            string path = Path.Combine(Path.GetTempPath(), "t.profan.json");
            ConfigTransfer.Save(pkg, path);
            var loaded = ConfigTransfer.Load(path);
            Check(loaded.Profiles.Count == 1 && loaded.Labels.Count >= 3, $"package roundtrip (labels {loaded.Labels.Count})");

            // 弟の PC: マザーボードのチップが違い、CPU が AMD
            var temps2 = new List<SensorInfo>
            {
                S("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", "AMD Ryzen 7 7800X3D", "Cpu"),
                S("/gpu-nvidia/0/temperature/0", "GPU Core", "NVIDIA GeForce RTX 4070", "GpuNvidia"),
            };
            var controls2 = new List<ControlInfo>
            {
                new("/lpc/nct6799d/0/control/1", "Fan #2", "Nuvoton NCT6799D", "/lpc/nct6799d/0/fan/1"),
                new("/lpc/nct6799d/0/control/3", "Fan #4", "Nuvoton NCT6799D", "/lpc/nct6799d/0/fan/3"),
            };
            var catalog2 = new HardwareCatalog(new List<DeviceInfo>(), new List<HardwareGroup> { new("all", "x", temps2) }, temps2, controls2, true);
            var map = ConfigTransfer.ProposeMapping(loaded, catalog2);
            foreach (var i in map) Console.WriteLine($"     {(i.IsControl ? "端子" : "温度")} {i.OldDisplay} → {i.NewId ?? "(なし)"}  [{i.Note}]");
            Check(map.First(i => i.IsControl).NewId == "/lpc/nct6799d/0/control/3", "fan header matched by name (Fan #4)");
            Check(map.First(i => i.OldId.Contains("intelcpu")).NewId == "/amdcpu/0/temperature/2", "Intel CPU Package → AMD Tctl/Tdie");

            var mine = new AppConfig();
            mine.Profiles.Add(new ProfileConfig { Name = "標準" });
            mine.Normalize();
            var merged = ConfigTransfer.Merge(mine, loaded, new[] { "標準" }, map, catalog2, replaceSameName: false, out var summary);
            Console.WriteLine("     " + summary);
            Check(merged.Profiles.Count == 2 && merged.Profiles[1].Name == "標準（読み込み）", "imported as new profile with suffix");
            loaded.Machine = "OTHER-PC";
            var mergedOther = ConfigTransfer.Merge(mine, loaded, new[] { "標準" }, map, catalog2, false, out var sumOther);
            Check(mergedOther.Profiles[1].Fans.All(x => !x.Enabled) && sumOther.Contains("オフ"), "fans from another PC start in BIOS control");
            var mf = merged.Profiles[1].Fans[0];
            Check(mf.ControlId == "/lpc/nct6799d/0/control/3" && mf.RpmSensorId == "/lpc/nct6799d/0/fan/3", $"control + rpm remapped ({mf.RpmSensorId})");
            Check(mf.Curves.Any(c => c.SensorId == "/amdcpu/0/temperature/2"), "curve sensor remapped");
            var merged2 = ConfigTransfer.Merge(mine, loaded, new[] { "標準" }, map, catalog2, replaceSameName: true, out _);
            Check(merged2.Profiles.Count == 1 && merged2.Profiles[0].Fans.Count == 1, "replace same-name profile");
            Check(mine.Profiles.Count == 1, "original config untouched");
        }

        // ===== A5. 設定のバックアップと復旧
        {
            string dir = AppContext.BaseDirectory;
            File.Delete(ConfigStore.FilePath);
            if (Directory.Exists(ConfigStore.BackupDirectory)) Directory.Delete(ConfigStore.BackupDirectory, true);
            var c = new AppConfig();
            c.Profiles.Add(new ProfileConfig { Name = "標準" });
            ConfigStore.SaveJson(c.ToJson());
            ConfigStore.Backup("manual");
            c.Profiles.Add(new ProfileConfig { Name = "静音" });
            ConfigStore.SaveJson(c.ToJson());
            Check(ConfigStore.ListBackups().Count == 1, $"backup created ({ConfigStore.ListBackups().Count})");
            File.WriteAllText(ConfigStore.FilePath, "{ broken json ");
            var rec = ConfigStore.Load();
            Check(ConfigStore.RecoveredFrom is not null && rec.Profiles.Count == 1, "broken config recovered from backup");
            Check(Directory.GetFiles(dir, "config.broken_*.json").Length >= 1, "broken file kept");
        }

        // ===== C11. 長期の記録と劣化の検知
        {
            if (Directory.Exists(LongTermStore.Directory)) Directory.Delete(LongTermStore.Directory, true);
            var store = new LongTermStore();
            var roles = new Dictionary<string, string> { [StatRoles.CpuTemp] = "cpuT", [StatRoles.CpuPower] = "cpuP", [StatRoles.Ambient] = "amb" };
            store.Configure(new[] { "cpuT", "cpuP", "amb", "rpm:f1", "duty:f1" }, roles);
            var start = DateTime.Today.AddDays(-20);
            var rnd = new Random(3);
            // 20 日分（1 分ごとに 60 サンプル）。後半 7 日はファンの回転数が 15% 低下し、同じ電力で温度が 30% 余計に上がる
            for (int day = 0; day < 20; day++)
            {
                for (int m = 0; m < 60 * 6; m += 1)
                {
                    var minute = start.AddDays(day).AddHours(18).AddMinutes(m);
                    bool degraded = day >= 13;
                    for (int sec = 0; sec < 60; sec += 2)
                    {
                        double power = m % 2 == 0 ? 100 : 40;
                        double rise = power * 0.4 * (degraded ? 1.3 : 1.0);
                        var values = new Dictionary<string, float?> { ["cpuT"] = (float)(25 + rise + rnd.NextDouble()), ["cpuP"] = (float)power, ["amb"] = 25 };
                        double duty = 60;
                        double rpm = 1200 * (degraded ? 0.85 : 1.0) + rnd.Next(-10, 10);
                        var fans = new List<FanStatus> { new("F1", "f1", "Fan", FanState.Active, duty, duty, rpm, Array.Empty<CurveStatus>()) };
                        store.Add(minute.AddSeconds(sec), values, fans);
                    }
                }
            }
            store.Flush();
            var rows = LongTermStore.Load(start, DateTime.Now);
            Check(rows.Count > 7000, $"minute rows written and read ({rows.Count})");
            Check(rows[0].Role(StatRoles.CpuTemp) is not null && rows[0].DutySpread.ContainsKey("duty:f1"), "roles and duty spread kept");

            var cfg = new AppConfig();
            var prof = new ProfileConfig { Name = "標準" };
            prof.Fans.Add(new FanConfig { Name = "Front", ControlId = "f1" });
            cfg.Profiles.Add(prof);
            var health = HealthAnalyzer.Analyze(rows, cfg, DateTime.Today.AddDays(0).AddHours(23));
            foreach (var h in health) Console.WriteLine($"     [{h.Level}] {h.Title}: {h.Metric} {h.Change:P0}");
            Check(health.First(h => h.Title == "Front").Level == HealthLevel.Warning, "fan rpm drop 15% → Warning");
            Check(health.First(h => h.Title == "CPU の冷え方").Level == HealthLevel.Warning, "thermal resistance +30% → Warning");

            cfg.Maintenance.LastCleaned = DateTime.Today.AddDays(-3);
            var h2 = HealthAnalyzer.Analyze(rows, cfg, DateTime.Today.AddHours(23));
            Check(h2.All(h => h.Level == HealthLevel.Collecting), "after cleaning → collecting again");
        }

        // ===== C13. 電力量
        {
            if (File.Exists(EnergyMeter.FilePath)) File.Delete(EnergyMeter.FilePath);
            var em = new EnergyMeter();
            var t = DateTime.Today.AddHours(10);
            // 350 W × 1 時間。電源の効率 88% なので、コンセント側は 350 ÷ 0.88 = 397.7 W
            for (int i = 0; i <= 3600; i++) em.Add(t.AddSeconds(i), 100, 200, 50, 88);
            em.Add(t.AddSeconds(3600 + 600), 100, 200, 50, 88); // 10 分空いた（スリープ）→ 数えない
            double expected = 0.35 / 0.88;
            Check(Math.Abs(em.TodayKwh - expected) < 0.001, $"350W inside × 1h at 88% = {expected:F4} kWh ({em.TodayKwh:F4})");
            em.Save();
            var em2 = new EnergyMeter();
            Check(Math.Abs(em2.TodayKwh - expected) < 0.001 && em2.LastDays(30).Count == 30, "energy persisted");
        }

        // ===== C14. OpenRGB のコントローラー情報（プロトコル版 1）
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            void Str(string s) { var b = Encoding.UTF8.GetBytes(s + "\0"); w.Write((ushort)b.Length); w.Write(b); }
            w.Write(0u); w.Write(0);                    // データ長・種類
            Str("ASRock Polychrome"); Str("ASRock"); Str("desc"); Str("1.0"); Str("SN"); Str("loc");
            w.Write((ushort)2); w.Write(0);             // モード 2 個・選択中
            for (int m = 0; m < 2; m++) { Str(m == 0 ? "Direct" : "Static"); for (int k = 0; k < 9; k++) w.Write(0u); w.Write((ushort)1); w.Write(0xFF0000u); }
            w.Write((ushort)2);                         // ゾーン 2 個
            Str("Zone A"); w.Write(0); w.Write(1u); w.Write(10u); w.Write(4u); w.Write((ushort)0);
            Str("Zone B"); w.Write(1); w.Write(1u); w.Write(10u); w.Write(3u); w.Write((ushort)16); w.Write(1u); w.Write(2u); w.Write(0u); w.Write(1u);
            w.Write((ushort)7); for (int l = 0; l < 7; l++) { Str($"LED {l}"); w.Write((uint)l); }
            w.Write((ushort)7); for (int l = 0; l < 7; l++) w.Write(0u);
            var data = ms.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
            var (name, leds) = OpenRgbClient.ParseController(data);
            Check(name == "ASRock Polychrome" && leds == 7, $"OpenRGB controller parsed ({name}, {leds} LEDs)");
            var cold = RgbSync.ColorFor(30, 40, 85);
            var hot = RgbSync.ColorFor(95, 40, 85);
            var mid = RgbSync.ColorFor(62.5, 40, 85);
            Check(cold == (0x22, 0xC7, 0xFF) && hot == (0xFF, 0x3B, 0x30), "color endpoints");
            Console.WriteLine($"     62.5°C → #{mid.R:X2}{mid.G:X2}{mid.B:X2}");
        }

        // ===== B10. フライトレコーダー
        {
            if (Directory.Exists(FlightRecorder.Directory)) Directory.Delete(FlightRecorder.Directory, true);
            var h = new HistoryStore();
            h.SetName("cpu", "CPU Package (Intel) [°C]");
            var fr = new FlightRecorder();
            FlightRecord? saved = null;
            fr.Saved += r => saved = r;
            var baseT = DateTime.Now.AddMinutes(-30);
            for (int i = 0; i < 1500; i++)
            {
                var tt = baseT.AddSeconds(i);
                var snap = new EngineSnapshot(tt, "標準", new List<FanStatus> { new("F", "f1", "Fan", FanState.Active, 50, 50, 1000 + i % 7, Array.Empty<CurveStatus>()) },
                    new Dictionary<string, float?> { ["cpu"] = 60 + (i > 900 ? 40 : 0) }, false, false, false);
                h.Add(snap, new[] { "cpu" });
                if (i == 901) fr.Trigger("CPU Package が 100°C に達しました", "標準", tt);
                if (i == 950) fr.Trigger("ポンプの回転数が低下", "標準", tt);
                fr.Tick(h, tt);
            }
            fr.Flush(h);
            Check(saved is not null && File.Exists(saved.CsvPath), "record saved");
            Check(saved!.Summary.Contains("100.0") && saved.Summary.Contains("ポンプ"), "summary has max and both events");
            Check(FlightRecorder.List().Count == 1, "record listed");
        }

        // ===== A1. GPU ファンの制御をオフにしているときは、GPU ファンを「消えた端子」として扱わない
        {
            var cfg = new AppConfig();
            var p = new ProfileConfig { Name = "標準" };
            var g = new FanConfig { Name = "GPU Fan", ControlId = "/gpu-nvidia/0/control/0" };
            g.Curves.Add(new CurveConfig { Name = "GPU", SensorId = "/gpu-nvidia/0/temperature/0" });
            p.Fans.Add(g);
            cfg.Profiles.Add(p);
            cfg.Normalize();
            var temps = new List<SensorInfo> { new("/gpu-nvidia/0/temperature/0", "GPU Core", "RTX", "GpuNvidia", "Temperature") };
            var cat = new HardwareCatalog(new List<DeviceInfo>(), new List<HardwareGroup> { new("g", "x", temps) }, temps, new List<ControlInfo>(), true);
            Check(!HardwareChange.Detect(cfg, cat).HasChanges, "GPU fan hidden when GPU control disabled → no change");
        }

        // ===== B6. おまかせ調整の流れ（熱モデルでシミュレーション。外部負荷モードなので CPU は使わない）
        {
            // 落ち着く温度: 100% で 66°C、40% で 86°C くらいの PC（steady = 40 + 26 / (0.3 + 0.7 × duty/100)）
            double Steady(double duty) => 40 + 26 / (0.3 + 0.7 * duty / 100);
            var engine = new FanEngine(); // Start しない（命令はキューに溜まるだけ）
            var fans = new List<(string ControlId, string Name)> { ("f1", "Front"), ("f2", "Top") };
            var tuner = new AutoTuner(engine, fans, "cpuT", "CPU Package", "load", target: 80, abortTemp: 97, minDuty: 25);
            double temp = 45;
            var t = new DateTime(2026, 10, 23, 20, 0, 0);
            var rnd = new Random(5);
            int seconds = 0;
            for (; seconds < 3600 && tuner.Current != AutoTuner.Phase.Done; seconds++)
            {
                bool loaded = tuner.Current is not AutoTuner.Phase.Idle;
                double duty = tuner.Current is AutoTuner.Phase.Warmup or AutoTuner.Phase.Steps ? tuner.CurrentDuty : 30;
                double target = loaded ? Steady(duty) : 45;
                temp += (target - temp) / 25 + (rnd.NextDouble() - 0.5) * 0.6;
                var snap = new EngineSnapshot(t.AddSeconds(seconds), "標準",
                    new List<FanStatus> { new("Front", "f1", "Fan", FanState.Manual, duty, duty, 600 + duty * 12, Array.Empty<CurveStatus>()) },
                    new Dictionary<string, float?> { ["cpuT"] = (float)temp, ["load"] = loaded ? 97f : 3f }, false, false, false);
                tuner.OnSnapshot(snap);
            }
            var r = tuner.Result!;
            Console.WriteLine($"     {seconds} 秒で完了 / 中断: {r.AbortReason ?? "なし"}");
            foreach (var st in r.Steps) Console.WriteLine($"     {st.Duty,3:F0}% → {st.SteadyTemp:F1}°C（理論値 {Steady(st.Duty):F1}）{st.Seconds:F0}s");
            var (need, insuff) = AutoTuner.RequiredDuty(r.Steps, 80);
            // 理論値: 78.5 = 40 + 26/(0.3+0.7d) → d = (26/38.5 - 0.3)/0.7 = 0.536
            Console.WriteLine($"     必要な回転数 {need:F1}%（理論値 53.6%）");
            Check(r.AbortReason is null && r.Steps.Count >= 4, "auto-tune finished with steps");
            Check(r.Steps.All(st => Math.Abs(st.SteadyTemp - Steady(st.Duty)) < 2.0), "measured steady temps close to model");
            Check(!insuff && Math.Abs(need - 53.6) < 6, "required duty close to theory");
            Check(seconds < 20 * 60, $"finished within 20 min ({seconds / 60} min)");
            Check(Math.Abs(r.IdleTemp - 45) < 2, $"idle temp measured ({r.IdleTemp:F1})");
        }

        // ===== B6. 外部負荷がなくなったら中止
        {
            var engine = new FanEngine();
            var tuner = new AutoTuner(engine, new() { ("f1", "Front") }, "gpuT", "GPU Core", "load", 75, 90, 30);
            var t = new DateTime(2026, 10, 23, 21, 0, 0);
            for (int i = 0; i < 400 && tuner.Current != AutoTuner.Phase.Done; i++)
            {
                float load = i < 150 ? 95f : 5f; // 150 秒でゲームを閉じた
                var snap = new EngineSnapshot(t.AddSeconds(i), "標準", new List<FanStatus>(),
                    new Dictionary<string, float?> { ["gpuT"] = 70f, ["load"] = i < 20 ? 3f : load }, false, false, false);
                tuner.OnSnapshot(snap);
            }
            Check(tuner.Current == AutoTuner.Phase.Done && tuner.Result!.AbortReason!.Contains("GPU の負荷"), $"external load lost → abort ({tuner.Result?.AbortReason})");
        }
    }

    public static void TestLightingModes()
    {
        void Check(bool ok, string name) => T.Check(ok, name);
        var cfg = new RgbConfig { Mode = RgbModes.Rainbow, Brightness = 100, Speed = 1, Gamma = false };
        var f0 = RgbEffects.Frame(cfg, 0, 12, null, false);
        var f1 = RgbEffects.Frame(cfg, 1, 12, null, false);
        Check(f0.Distinct().Count() == 12 && f0[0] == new Rgb(255, 0, 0), "rainbow: every LED different, starts red");
        Check(f1[0] != f0[0], "rainbow moves over time");
        var step = RgbEffects.Frame(cfg, 1.0 / 30, 12, null, false)[0];
        Check(step.G <= 12, $"rainbow speed 1: small step per frame at 30 fps (G={step.G})");
        cfg.Mode = RgbModes.Breathing; cfg.StaticColor = "#FF0000";
        var dark = RgbEffects.Frame(cfg, 0, 3, null, false)[0];
        var bright = RgbEffects.Frame(cfg, 3, 3, null, false)[0];
        Check(dark.R < 40 && bright.R == 255, $"breathing dark→bright ({dark.R}→{bright.R})");
        cfg.Mode = RgbModes.Static; cfg.Brightness = 50;
        Check(RgbEffects.Frame(cfg, 0, 2, null, false)[0] == new Rgb(128, 0, 0), "static color with brightness 50%");
        var em = RgbEffects.Frame(cfg, 0, 4, null, emergency: true);
        Check(em.All(c => c == new Rgb(255, 0, 0)), "emergency overrides any mode (red blink)");
        cfg.Mode = RgbModes.Temperature; cfg.Brightness = 100;
        Check(RgbEffects.Frame(cfg, 0, 1, 30, false)[0] == new Rgb(0x22, 0xC7, 0xFF), "temperature mode unchanged");
        cfg.Gamma = true; cfg.Mode = RgbModes.Static; cfg.StaticColor = "#7C5CFF";
        var deep = RgbEffects.Frame(cfg, 0, 1, null, false)[0];
        Check(deep.B == 255 && deep.R < 60 && deep.G < 35, $"gamma makes color deeper (less white) {deep.Hex}");
        cfg.Mode = RgbModes.RainbowStatic; cfg.Gamma = false;
        var s0 = RgbEffects.Frame(cfg, 0, 12, null, false);
        var s9 = RgbEffects.Frame(cfg, 9, 12, null, false);
        Check(s0.SequenceEqual(s9) && s0.Distinct().Count() == 12 && !RgbEffects.IsAnimated(RgbModes.RainbowStatic), "static rainbow: rainbow colors that do not move");
        var mb = RgbEffects.Frame(cfg, 0, 250, null, false, new[] { 10, 80, 80, 80 });
        var onboard = mb.Take(10).ToList();
        Check(onboard.Distinct().Count() == 10 && onboard.Any(c => c.R > 200 && c.G < 60) && onboard.Any(c => c.B > 200 && c.R < 60), "onboard zone shows the whole rainbow");
        var gpuSmall = RgbEffects.Frame(cfg, 0, 6, null, false, new[] { 1, 1, 1, 1, 1, 1 });
        Check(gpuSmall.Distinct().Count() == 6, "single-LED zones are grouped into one rainbow");
    }
}

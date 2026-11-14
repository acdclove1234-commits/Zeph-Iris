// FanControl（Rem0o）の設定の取り込み（FanControlImport）。設定の形は、公開されている報告の例に合わせた作り物
using ProFanController;

namespace LogicTests;

public static class FanControlImportTests
{
    private static HardwareCatalog Catalog()
    {
        SensorInfo S(string id, string name, string hw, string type) => new(id, name, hw, type, "Temperature");
        var temps = new List<SensorInfo>
        {
            S("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", "AMD Ryzen 7 7800X3D", "Cpu"),
            S("/gpu-nvidia/0/temperature/0", "GPU Core", "NVIDIA GeForce RTX 4080 SUPER", "GpuNvidia"),
            S("/lpc/it8686e/0/temperature/1", "System", "ITE IT8686E", "SuperIO"),
        };
        var controls = new List<ControlInfo>
        {
            new("/lpc/it8686e/0/control/0", "Fan #1", "ITE IT8686E", "/lpc/it8686e/0/fan/0"),
            new("/lpc/it8686e/0/control/1", "Fan #2", "ITE IT8686E", "/lpc/it8686e/0/fan/1"),
            new("/lpc/it8686e/0/control/2", "Fan #3", "ITE IT8686E", "/lpc/it8686e/0/fan/2"),
            new("/gpu-nvidia/0/control/1", "GPU Fan 1", "NVIDIA GeForce RTX 4080 SUPER", "/gpu-nvidia/0/fan/1"),
        };
        return new HardwareCatalog(new List<DeviceInfo>(), new List<HardwareGroup> { new("all", "x", temps) }, temps, controls, true);
    }

    // 古い LHM の形（/lpc/it8686e/control/0。/0 がない）・グラフ・Auto・Mix・固定・NvAPI・カーブなし
    private const string Json = """
    {
      "__VERSION__": "235",
      "Main": {
        "Controls": [
          { "Name": "Fan Control #1", "NickName": "CPU cooler", "Identifier": "/lpc/it8686e/control/0", "Enable": true,
            "SelectedFanCurve": { "Name": "CPU graph" }, "MinimumPercent": 20, "SelectedOffset": 0 },
          { "Name": "Fan Control #2", "NickName": "Front", "Identifier": "/lpc/it8686e/control/1", "Enable": true,
            "SelectedFanCurve": { "Name": "Mix" }, "SelectedOffset": 5 },
          { "Name": "Fan Control #3", "NickName": "AIO Pump", "Identifier": "/lpc/it8686e/control/2", "Enable": true,
            "SelectedFanCurve": { "Name": "Pump flat" } },
          { "NickName": "Control 1 - NVIDIA GeForce RTX 4080 SUPER", "Identifier": "NVApiWrapper/0-AD103-A/control/0", "Enable": true,
            "SelectedFanCurve": { "Name": "Auto" } },
          { "NickName": "Unused", "Identifier": "/lpc/it8686e/control/3", "Enable": false, "SelectedFanCurve": null }
        ],
        "FanCurves": [
          { "Name": "CPU graph", "SelectedTempSource": { "Identifier": "/amdcpu/0/temperature/2" },
            "Points": [ "30,20", "60,45", "80,100" ], "SelectedHysteresis": 3 },
          { "Name": "Auto", "SelectedTempSource": { "Identifier": "NVApiWrapper/0-AD103-A/temperature/0" },
            "Step": 2, "Deadband": 2, "IdleTemperature": 45, "MinFanSpeed": 0, "MaxFanSpeed": 80, "LoadTemperature": 71 },
          { "Name": "Mix", "SelectedFanCurves": [ { "Name": "CPU graph" }, { "Name": "Auto" } ], "SelectedMixFunction": 0 },
          { "Name": "Pump flat", "FlatValue": 70 }
        ]
      }
    }
    """;

    public static void TestConvertCurves()
    {
        var cat = Catalog();
        T.Check(FanControlImport.LooksLikeFanControl(Json), "FanControl config is recognised");
        T.Check(!FanControlImport.LooksLikeFanControl("""{ "Format": "Zeph-Iris.Settings", "Profiles": [] }"""), "own settings file is not FanControl");

        var r = FanControlImport.Convert(Json, @"C:\FanControl\Configurations\userConfig.json", cat);
        var p = r.Package.Profiles.Single();
        T.Check(p.Name == "FanControl", $"profile named after FanControl ({p.Name})");
        T.Check(r.FanCount == 4 && p.Fans.Count == 4, $"four fans with curves, the one without is skipped ({r.FanCount})");
        T.Check(p.Fans.All(f => !f.Enabled), "imported fans start with control off (check first)");
        T.Check(r.Package.Machine == FanControlImport.SourceName, "marked as coming from FanControl");

        var cpu = p.Fans.First(f => f.Name == "CPU cooler");
        T.Check(cpu.ControlId == "/lpc/it8686e/0/control/0", $"old LHM id without /0 maps to this PC ({cpu.ControlId})");
        T.Check(cpu.MinDuty == 20, "MinimumPercent becomes the minimum speed");
        var g = cpu.Curves.Single();
        T.Check(g.SensorId == "/amdcpu/0/temperature/2" && g.Points.Count == 3 && g.Points[1].Temp == 60 && g.Points[1].Duty == 45, "graph points kept");
        T.Check(g.HysteresisCelsius == 3, "hysteresis kept");

        var front = p.Fans.First(f => f.Name == "Front");
        T.Check(front.Curves.Count == 2, $"Mix becomes two curves (highest wins) ({front.Curves.Count})");
        T.Check(r.Notes.Any(n => n.Contains("Front") && n.Contains("5")), "offset that cannot be kept is reported");

        var pump = p.Fans.First(f => f.Name == "AIO Pump");
        T.Check(pump.IsPump && pump.Mode == FanModes.Fixed && pump.FixedDuty == 70, "flat curve on a pump becomes a fixed 70% pump");

        var gpu = p.Fans.First(f => f.Name.StartsWith("Control 1"));
        T.Check(gpu.ControlId == "/gpu-nvidia/0/control/1", $"NvAPI GPU fan maps to this PC's GPU fan ({gpu.ControlId})");
        var auto = gpu.Curves.Single();
        T.Check(auto.Points.Count == 2 && auto.Points[0].Temp == 45 && auto.Points[0].Duty == 0 && auto.Points[1].Temp == 71 && auto.Points[1].Duty == 80,
            "Auto curve becomes idle→load line");
    }

    public static void TestMappingAndMerge()
    {
        var cat = Catalog();
        var r = FanControlImport.Convert(Json, "Balanced.json", cat);
        T.Check(r.Package.Profiles[0].Name == "FanControl（Balanced）", "profile name includes the file name");
        var map = ConfigTransfer.ProposeMapping(r.Package, cat);
        T.Check(map.Where(m => m.IsControl).All(m => m.NewId is not null), "every fan header found on this PC");
        var nv = map.FirstOrDefault(m => !m.IsControl && m.OldId.StartsWith("NVApiWrapper"));
        T.Check(nv?.NewId == "/gpu-nvidia/0/temperature/0", $"NvAPI GPU temperature maps to GPU Core ({nv?.NewId})");

        var cfg = new AppConfig();
        cfg.Normalize();
        int before = cfg.Profiles.Count;
        var merged = ConfigTransfer.Merge(cfg, r.Package, new[] { r.Package.Profiles[0].Name }, map, cat, false, out string summary);
        T.Check(merged.Profiles.Count == before + 1, "added as a new profile, existing ones untouched");
        var added = merged.FindProfile("FanControl（Balanced）")!;
        T.Check(added.Fans.All(f => !f.Enabled), "still off after merge");
        T.Check(added.Fans.SelectMany(f => f.Curves).All(c => !c.SensorId.StartsWith("NVApiWrapper")), "no NvAPI ids left after merge");
    }

    // 今の FanControl（__VERSION__ 285）が保存する形: "FanControl" の下・HysteresisConfig・ステップ・線形の Minimum/Maximum
    private const string Json285 = """
    {
      "__VERSION__": "285",
      "FanControl": {
        "Controls": [
          { "NickName": "CPU Fan", "Identifier": "/lpc/it8686e/control/0", "Enable": true, "SelectedFanCurve": { "Name": "グラフ" },
            "SelectedOffset": 0, "MinimumPercent": 0, "SelectedCommandStepUp": 8, "SelectedCommandStepDown": 6 },
          { "NickName": "System Fan #1", "Identifier": "/lpc/it8686e/control/1", "Enable": true, "SelectedFanCurve": { "Name": "リニア" },
            "SelectedOffset": 0, "MinimumPercent": 0, "SelectedCommandStepUp": 8, "SelectedCommandStepDown": 8 },
          { "NickName": "System Fan #2", "Identifier": "/lpc/it8686e/control/2", "Enable": false, "SelectedFanCurve": null }
        ],
        "FanCurves": [
          { "Name": "グラフ", "CommandMode": 0, "SelectedTempSource": { "Identifier": "/amdcpu/0/temperature/2" },
            "Points": [ "40,20", "80,80" ], "MaximumTemperature": 120, "MinimumTemperature": 20, "MaximumCommand": 100,
            "HysteresisConfig": { "ResponseTimeUp": 1, "ResponseTimeDown": 1, "HysteresisValueUp": 2, "HysteresisValueDown": 4 } },
          { "Name": "リニア", "CommandMode": 0, "SelectedTempSource": { "Identifier": "NVApiWrapper/0-AD103-A/sensor/0" },
            "MaximumFanSpeed": 80, "MaximumTemperature": 70, "MinimumFanSpeed": 50, "MinimumTemperature": 35,
            "HysteresisConfig": { "HysteresisValueUp": 3, "HysteresisValueDown": 3 } }
        ]
      }
    }
    """;

    public static void TestCurrentFormat()
    {
        var cat = Catalog();
        T.Check(FanControlImport.LooksLikeFanControl(Json285), "current FanControl file is recognised");
        var r = FanControlImport.Convert(Json285, "userConfig.json", cat);
        var fans = r.Package.Profiles.Single().Fans;
        T.Check(fans.Count == 2, $"two assigned fans, the unassigned one skipped ({fans.Count})");

        var cpu = fans.First(f => f.Name == "CPU Fan");
        var g = cpu.Curves.Single();
        T.Check(g.Points.Count == 2 && g.Points[0].Temp == 40 && g.Points[0].Duty == 20 && g.Points[1].Temp == 80 && g.Points[1].Duty == 80,
            "graph points win over the graph's Minimum/MaximumTemperature range");
        T.Check(g.HysteresisCelsius == 4, $"HysteresisConfig read (larger of up/down) ({g.HysteresisCelsius})");
        T.Check(cpu.MaxStepUpPerTick == 8 && cpu.MaxStepDownPerTick == 6, $"step up/down %/s kept ({cpu.MaxStepUpPerTick}/{cpu.MaxStepDownPerTick})");
        T.Check(cpu.MinDuty == 20, $"no FanControl minimum: minimum lowered to the curve's lowest speed, not the 25% default ({cpu.MinDuty})");

        var sys = fans.First(f => f.Name == "System Fan #1");
        var lin = sys.Curves.Single();
        T.Check(lin.Points.Count == 2 && lin.Points[0].Temp == 35 && lin.Points[0].Duty == 50 && lin.Points[1].Temp == 70 && lin.Points[1].Duty == 80,
            "linear curve from Minimum/Maximum Temperature and FanSpeed");
        T.Check(lin.HysteresisCelsius == 3, "linear HysteresisConfig read");
        T.Check(sys.MinDuty == 25, $"curve never below the default: default minimum kept ({sys.MinDuty})");

        var map = ConfigTransfer.ProposeMapping(r.Package, cat);
        var nv = map.FirstOrDefault(m => !m.IsControl && m.OldId.StartsWith("NVApiWrapper"));
        T.Check(nv?.NewId == "/gpu-nvidia/0/temperature/0", $"NvAPI sensor/0 maps to GPU Core ({nv?.NewId})");
    }

    public static void TestNotSavedYet()
    {
        var cat = Catalog();
        // FanControl でカーブを作っても保存する前: カーブが 0 個・割り当てもない
        const string unsaved = """
        { "__VERSION__": "285", "FanControl": {
            "Controls": [ { "NickName": "CPU Fan", "Identifier": "/lpc/it8686e/control/0", "Enable": false, "SelectedFanCurve": null } ],
            "FanCurves": [] } }
        """;
        string? msg = null;
        try { FanControlImport.Convert(unsaved, "userConfig.json", cat); } catch (InvalidDataException e) { msg = e.Message; }
        T.Check(msg is not null && msg.Contains("保存"), $"no curves in the file: the error says to save in FanControl first ({msg})");

        // カーブはあるが、どのファンにも割り当てていない
        const string unassigned = """
        { "FanControl": {
            "Controls": [ { "NickName": "CPU Fan", "Identifier": "/lpc/it8686e/control/0", "SelectedFanCurve": null } ],
            "FanCurves": [ { "Name": "g", "SelectedTempSource": { "Identifier": "/amdcpu/0/temperature/2" }, "Points": [ "40,20" ] } ] } }
        """;
        msg = null;
        try { FanControlImport.Convert(unassigned, "userConfig.json", cat); } catch (InvalidDataException e) { msg = e.Message; }
        T.Check(msg is not null && !msg.Contains("保存"), $"curves but none assigned: the 'no fans with curves' error ({msg})");
    }

    public static void TestOddShapes()
    {
        var cat = Catalog();
        // 小数点が「,」の地域・点が {X,Y} の形・カーブの中身が直接入っている
        const string odd = """
        { "FanControl": { "Controls": [
            { "Identifier": "/lpc/it8686e/0/control/0", "Name": "Fan #1",
              "SelectedFanCurve": { "Name": "inline", "SelectedTempSource": { "Identifier": "/amdcpu/0/temperature/2" },
                                    "Points": [ "30,5,20,5", { "X": 70, "Y": 90 } ] } } ],
          "FanCurves": [] } }
        """;
        var r = FanControlImport.Convert(odd, "x.json", cat);
        var c = r.Package.Profiles[0].Fans.Single().Curves.Single();
        T.Check(c.Points.Count == 2 && Math.Abs(c.Points[0].Temp - 30.5) < 1e-9 && Math.Abs(c.Points[0].Duty - 20.5) < 1e-9 && c.Points[1].Duty == 90,
            "decimal-comma points and {X,Y} points read");
        bool threw = false;
        try { FanControlImport.Convert("""{ "Controls": [], "FanCurves": [] }""", "x.json", cat); } catch (InvalidDataException) { threw = true; }
        T.Check(threw, "no fans with curves: clear error");
        T.Check(FanControlImport.IdKey("/lpc/it8686e/0/control/2") == FanControlImport.IdKey("/lpc/it8686e/control/2"), "id key ignores the /0 index");
        T.Check(FanControlImport.IdKey("/lpc/it8686e/0/control/2") != FanControlImport.IdKey("/lpc/it8686e/0/control/3"), "id key keeps the header number");
    }
}

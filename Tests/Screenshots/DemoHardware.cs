// 作り物のハードウェア（README の画面写真用）: ASRock B860 + Core Ultra 5 245KF + RTX 5070 Ti に似たセンサーと端子。
// 値は時間とともに動く（ゲームをしているような負荷の波・温度は負荷を追い、ファンの回転数は書き込んだ Duty に従う）
using ProFanController;

namespace Screenshots;

internal sealed class DemoControl : IFanControl
{
    public DemoControl(string id, float duty) { Id = id; Duty = duty; }
    public string Id { get; }
    public float Duty;
    public void SetSoftware(float duty) => Duty = duty;
    public void SetDefault() { }
}

internal sealed class DemoHardware : IFanHardware
{
    public const string Lpc = "/lpc/nct6686d/0";
    public const string Cpu = "/intelcpu/0";
    public const string Gpu = "/gpu-nvidia/0";
    public static readonly string CpuTemp = $"{Cpu}/temperature/0", GpuTemp = $"{Gpu}/temperature/0", GpuHot = $"{Gpu}/temperature/2";
    public static readonly string[] FanControls = { $"{Lpc}/control/0", $"{Lpc}/control/1", $"{Lpc}/control/2", $"{Lpc}/control/3", $"{Lpc}/control/4" };
    public static readonly string[] FanRpms = { $"{Lpc}/fan/0", $"{Lpc}/fan/1", $"{Lpc}/fan/2", $"{Lpc}/fan/3", $"{Lpc}/fan/4" };
    private static readonly string[] FanNames = { "CPU Fan", "Pump Fan", "System Fan #1", "System Fan #2", "System Fan #3" };
    private static readonly double[] MaxRpm = { 2000, 3000, 1600, 1600, 1600 };

    private readonly Dictionary<string, DemoControl> _controls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HardwareGroup> _groups = new();
    private readonly DateTime _start = DateTime.Now;
    private double _cpuT = 42, _gpuT = 40;

    public DemoHardware()
    {
        for (int i = 0; i < FanControls.Length; i++) _controls[FanControls[i]] = new DemoControl(FanControls[i], i == 1 ? 70 : 40);
        SensorInfo S(string id, string name, string hw, string type, string sensor) => new(id, name, hw, type, sensor);
        const string mb = "Nuvoton NCT6686D", cpu = "Intel Core Ultra 5 245KF", gpu = "NVIDIA GeForce RTX 5070 Ti", nvme = "Samsung SSD 990 PRO 2TB";
        _groups.Add(new HardwareGroup(cpu, "Cpu", new[]
        {
            S(CpuTemp, "CPU Package", cpu, "Cpu", "Temperature"), S($"{Cpu}/temperature/1", "Core Max", cpu, "Cpu", "Temperature"),
            S($"{Cpu}/load/0", "CPU Total", cpu, "Cpu", "Load"), S($"{Cpu}/power/0", "CPU Package", cpu, "Cpu", "Power"),
            S($"{Cpu}/clock/1", "P-Core #1", cpu, "Cpu", "Clock"), S($"{Cpu}/clock/2", "P-Core #2", cpu, "Cpu", "Clock"),
        }));
        _groups.Add(new HardwareGroup(gpu, "GpuNvidia", new[]
        {
            S(GpuTemp, "GPU Core", gpu, "GpuNvidia", "Temperature"), S(GpuHot, "GPU Hot Spot", gpu, "GpuNvidia", "Temperature"),
            S($"{Gpu}/load/0", "GPU Core", gpu, "GpuNvidia", "Load"), S($"{Gpu}/power/0", "GPU Package", gpu, "GpuNvidia", "Power"),
            S($"{Gpu}/clock/0", "GPU Core", gpu, "GpuNvidia", "Clock"),
            S($"{Gpu}/smalldata/1", "GPU Memory Used", gpu, "GpuNvidia", "SmallData"), S($"{Gpu}/smalldata/2", "GPU Memory Total", gpu, "GpuNvidia", "SmallData"),
            S($"{Gpu}/fan/1", "GPU Fan 1", gpu, "GpuNvidia", "Fan"),
        }));
        _groups.Add(new HardwareGroup("Total Memory", "Memory", new[]
        {
            S("/ram/load/0", "Memory", "Total Memory", "Memory", "Load"),
            S("/ram/data/0", "Memory Used", "Total Memory", "Memory", "Data"), S("/ram/data/1", "Memory Available", "Total Memory", "Memory", "Data"),
        }));
        var lpc = new List<SensorInfo>
        {
            S($"{Lpc}/temperature/0", "System", mb, "SuperIO", "Temperature"), S($"{Lpc}/temperature/1", "VRM MOS", mb, "SuperIO", "Temperature"),
        };
        for (int i = 0; i < FanRpms.Length; i++) lpc.Add(S(FanRpms[i], FanNames[i], mb, "SuperIO", "Fan"));
        for (int i = 0; i < FanControls.Length; i++) lpc.Add(S(FanControls[i], FanNames[i], mb, "SuperIO", "Control"));
        _groups.Add(new HardwareGroup(mb, "SuperIO", lpc));
        _groups.Add(new HardwareGroup(nvme, "Storage", new[]
        {
            S("/nvme/0/temperature/0", "Composite Temperature", nvme, "Storage", "Temperature"), S("/nvme/0/level/0", "Percentage Used", nvme, "Storage", "Level"),
        }));
        Update();
    }

    public int Update()
    {
        double t = (DateTime.Now - _start).TotalSeconds;
        // ゲームのような負荷: ゆっくりした波 + 細かい揺れ
        double cpuLoad = Math.Clamp(38 + 22 * Math.Sin(t / 23) + 9 * Math.Sin(t / 3.7) + 5 * Math.Sin(t * 1.3), 4, 98);
        double gpuLoad = Math.Clamp(72 + 18 * Math.Sin(t / 31 + 1) + 6 * Math.Sin(t / 2.9), 10, 99);
        double cpuFan = _controls[FanControls[0]].Duty / 100.0, caseFan = _controls[FanControls[2]].Duty / 100.0;
        _cpuT += ((36 + cpuLoad * 0.42 - 6 * cpuFan) - _cpuT) * 0.12;
        _gpuT += ((34 + gpuLoad * 0.38 - 4 * caseFan) - _gpuT) * 0.08;
        _values[CpuTemp] = (float)_cpuT;
        _values[$"{Cpu}/temperature/1"] = (float)(_cpuT + 3);
        _values[$"{Cpu}/load/0"] = (float)cpuLoad;
        _values[$"{Cpu}/power/0"] = (float)(18 + cpuLoad * 0.95);
        _values[$"{Cpu}/clock/1"] = (float)(4800 + 300 * Math.Sin(t / 5));
        _values[$"{Cpu}/clock/2"] = (float)(4700 + 300 * Math.Sin(t / 4));
        _values[GpuTemp] = (float)_gpuT;
        _values[GpuHot] = (float)(_gpuT + 11);
        _values[$"{Gpu}/load/0"] = (float)gpuLoad;
        _values[$"{Gpu}/power/0"] = (float)(30 + gpuLoad * 2.6);
        _values[$"{Gpu}/clock/0"] = (float)(2550 + 120 * Math.Sin(t / 7));
        _values[$"{Gpu}/smalldata/1"] = (float)(6800 + 600 * Math.Sin(t / 40));
        _values[$"{Gpu}/smalldata/2"] = 16303f;
        _values[$"{Gpu}/fan/1"] = (float)(900 + gpuLoad * 9);
        _values["/ram/load/0"] = 46f;
        _values["/ram/data/0"] = 14.7f;
        _values["/ram/data/1"] = 17.2f;
        _values[$"{Lpc}/temperature/0"] = (float)(31 + 0.3 * Math.Sin(t / 50));
        _values[$"{Lpc}/temperature/1"] = (float)(44 + cpuLoad * 0.08);
        for (int i = 0; i < FanRpms.Length; i++)
            _values[FanRpms[i]] = (float)(MaxRpm[i] * Math.Max(0.18, _controls[FanControls[i]].Duty / 100.0) * (1 + 0.004 * Math.Sin(t * 2 + i)));
        _values["/nvme/0/temperature/0"] = (float)(41 + 2 * Math.Sin(t / 60));
        _values["/nvme/0/level/0"] = 2f;
        return 0;
    }

    public int HardwareCount => 5;
    public bool Exists(string? id) => id is not null && (_values.ContainsKey(id) || _controls.ContainsKey(id));
    public float? Value(string? id) => id is null ? null : _controls.TryGetValue(id, out var c) ? c.Duty : _values.TryGetValue(id, out var v) ? v : null;
    public string? NameOf(string? id) => _groups.SelectMany(g => g.Sensors).FirstOrDefault(s => s.Id == id)?.Name;
    public IFanControl? FindControl(string? id) => id is not null && _controls.TryGetValue(id, out var c) ? c : null;
    public float? LoadFor(string? sensorId) => sensorId?.StartsWith(Cpu) == true ? _values[$"{Cpu}/load/0"] : sensorId?.StartsWith(Gpu) == true ? _values[$"{Gpu}/load/0"] : null;
    public void GpuTemps(string controlId, out float? core, out float? hotSpot) { core = _values[GpuTemp]; hotSpot = _values[GpuHot]; }

    public Dictionary<string, float?> SnapshotValues()
    {
        var d = new Dictionary<string, float?>(_values, StringComparer.OrdinalIgnoreCase);
        foreach (var c in _controls.Values) d[c.Id] = c.Duty;
        return d;
    }

    public HardwareCatalog BuildCatalog(bool includeGpuFans)
    {
        var all = _groups.SelectMany(g => g.Sensors).ToList();
        var temps = all.Where(s => s.SensorType == "Temperature").ToList();
        var controls = FanControls.Select((id, i) => new ControlInfo(id, FanNames[i], "Nuvoton NCT6686D", FanRpms[i])).ToList();
        var devices = new List<DeviceInfo>
        {
            new("/motherboard", "ASRock B860 Steel Legend WiFi", "Motherboard", "Nuvoton NCT6686D", 2, 5, 5, FanRpms),
            new(Cpu, "Intel Core Ultra 5 245KF", "Cpu", null, 2, 0, 0, Array.Empty<string>()),
            new(Gpu, "NVIDIA GeForce RTX 5070 Ti", "Gpu", null, 2, 1, 0, new[] { $"{Gpu}/fan/1" }),
            new("/nvme/0", "Samsung SSD 990 PRO 2TB", "Storage", null, 1, 0, 0, Array.Empty<string>()),
        };
        return new HardwareCatalog(devices, _groups, temps, controls, true);
    }

    public void Dispose() { }
}

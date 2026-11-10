// ==================================================
// MainWindow.Monitoring.cs - 監視の拡張 (CPU/GPU の使用率・クロック・電力、メモリ・VRAM、SSD の温度と寿命、消費電力の目安)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using ProFanController.UI;

namespace ProFanController;

public partial class MainWindow
{
    private readonly ObservableCollection<MetricTileVm> _metricTiles = new();

    public ObservableCollection<MetricTileVm> MetricTileItems => _metricTiles;

    /// <summary>
    /// LHM のセンサー名はハードウェアごとに決まっているので、名前で探してタイルを組み立てる。
    /// 見つからないものは作らない（その PC にない部品のタイルは出ない）。
    /// </summary>
    private void RebuildMetricTiles()
    {
        _metricTiles.Clear();
        if (_catalog is null) return;
        var all = _catalog.AllSensors.ToList();

        SensorInfo? Find(string hw, string type, Func<string, bool> name)
            => all.FirstOrDefault(s => s.HardwareType.StartsWith(hw) && s.SensorType == type && name(s.Name));

        static float? V(IReadOnlyDictionary<string, float?> v, SensorInfo? s) => s is not null && v.TryGetValue(s.Id, out var x) ? x : null;

        // --- CPU
        var cpuLoad = SensorNames.Find(all, "Cpu", "Load", SensorNames.CpuLoad);
        var cpuPower = SensorNames.Find(all, "Cpu", "Power", SensorNames.CpuPower);
        var cpuClocks = all.Where(s => s.HardwareType.StartsWith("Cpu") && s.SensorType == "Clock" && s.Name.Contains("Core") && !s.Name.Contains("Bus")).ToList();
        if (cpuLoad is not null)
        {
            _metricTiles.Add(new MetricTileVm
            {
                Title = L($"CPU ・ {cpuLoad.HardwareName}"),
                ShortTitle = "CPU",
                HistoryKey = cpuLoad.Id,
                Brush = Palette.Series[0],
                Read = v =>
                {
                    var load = V(v, cpuLoad);
                    var clock = cpuClocks.Select(c => V(v, c)).Where(c => c > 0).DefaultIfEmpty(null).Max();
                    var power = V(v, cpuPower);
                    string sub = string.Join(L(" ・ "), new[]
                    {
                        clock is > 0 ? $"{clock / 1000:F2} GHz" : null,
                        power is > 0 ? $"{power:F0} W" : null,
                    }.Where(x => x is not null));
                    return (load, load is null ? "--" : $"{load:F0}%", sub, load);
                },
            });
        }

        // --- GPU
        var gpuLoad = SensorNames.Find(all, "Gpu", "Load", SensorNames.GpuLoad);
        var gpuClock = SensorNames.Find(all, "Gpu", "Clock", SensorNames.GpuClock);
        var gpuPower = Find("Gpu", "Power", n => n.Contains("Package")) ?? Find("Gpu", "Power", _ => true);
        if (gpuLoad is not null)
        {
            _metricTiles.Add(new MetricTileVm
            {
                Title = L($"GPU ・ {gpuLoad.HardwareName}"),
                ShortTitle = "GPU",
                HistoryKey = gpuLoad.Id,
                Brush = Palette.Series[1],
                Read = v =>
                {
                    var load = V(v, gpuLoad);
                    var clock = V(v, gpuClock);
                    var power = V(v, gpuPower);
                    string sub = string.Join(L(" ・ "), new[]
                    {
                        clock is > 0 ? $"{clock / 1000:F2} GHz" : null,
                        power is > 0 ? $"{power:F0} W" : null,
                    }.Where(x => x is not null));
                    return (load, load is null ? "--" : $"{load:F0}%", sub, load);
                },
            });
        }

        // --- VRAM
        var vramUsed = SensorNames.Find(all, "Gpu", "SmallData", SensorNames.VramUsed);
        var vramTotal = SensorNames.Find(all, "Gpu", "SmallData", SensorNames.VramTotal);
        if (vramUsed is not null && vramTotal is not null)
        {
            _metricTiles.Add(new MetricTileVm
            {
                Title = L("VRAM（ビデオメモリ）"),
                ShortTitle = "VRAM",
                HistoryKey = vramUsed.Id,
                HistoryUnit = "MB",
                Brush = Palette.Series[2],
                Read = v =>
                {
                    var used = V(v, vramUsed);
                    var total = V(v, vramTotal);
                    if (used is null || total is not > 0) return (null, "--", "", null);
                    double pct = used.Value / total.Value * 100;
                    return (pct, $"{pct:F0}%", $"{used / 1024:F1} / {total / 1024:F0} GB", pct);
                },
            });
        }

        // --- メモリ
        // 仮想メモリ込み（Virtual Memory）も同じ名前で出るので、実メモリだけを選ぶ（32 GB の PC で 53 GB と出ていた）
        var memLoad = SensorNames.PhysicalMemory(all, "Load", "Memory");
        var memUsed = SensorNames.PhysicalMemory(all, "Data", "Memory Used");
        var memAvail = SensorNames.PhysicalMemory(all, "Data", "Memory Available");
        if (memLoad is not null)
        {
            _metricTiles.Add(new MetricTileVm
            {
                Title = L("メモリ（RAM）"),
                ShortTitle = "RAM",
                HistoryKey = memLoad.Id,
                Brush = Palette.Series[3],
                Read = v =>
                {
                    var load = V(v, memLoad);
                    var used = V(v, memUsed);
                    var avail = V(v, memAvail);
                    string sub = used is not null && avail is not null ? $"{used:F1} / {used + avail:F0} GB" : "";
                    return (load, load is null ? "--" : $"{load:F0}%", sub, load);
                },
            });
        }

        // --- SSD（温度と寿命）。NVMe は「使用済みの割合」から寿命を出す
        foreach (var group in _catalog.Groups.Where(g => g.HardwareType == "Storage").Take(3))
        {
            var temp = group.Sensors.FirstOrDefault(s => s.SensorType == "Temperature");
            var used = group.Sensors.FirstOrDefault(s => s.SensorType == "Level" && s.Name == "Percentage Used");
            var life = group.Sensors.FirstOrDefault(s => s.SensorType == "Level" && s.Name.Contains("Life"));
            if (temp is null) continue;
            string name = group.Name.Length > 22 ? group.Name[..22] + "…" : group.Name;
            _metricTiles.Add(new MetricTileVm
            {
                Title = L($"SSD ・ {name}"),
                ShortTitle = "SSD",
                HistoryKey = null, // 温度はすでに温度センサーとして記録している
                Brush = Palette.Series[4],
                Read = v =>
                {
                    var t = V(v, temp);
                    double? remain = V(v, used) is float u ? 100 - u : V(v, life);
                    return (t, t is null ? "--" : $"{t:F0}°C", remain is null ? "" : L($"寿命 {remain:F0}%"), null);
                },
            });
        }

        // --- 消費電力（目安）と今日の電気代
        if (all.Any(s => s.SensorType == "Power" && (s.HardwareType.StartsWith("Cpu") || s.HardwareType.StartsWith("Gpu"))))
            _metricTiles.Add(CreateEnergyTile());

        RegisterHistoryNames();
    }

    /// <summary>履歴（CSV・AI アドバイザー）に記録する、温度以外の値</summary>
    private IEnumerable<(string Id, string Name, string Unit)> MetricHistoryKeys
        => _metricTiles.Where(t => t.HistoryKey is not null).Select(t => (t.HistoryKey!, t.Title, t.HistoryUnit));
}

// ==================================================
// SensorNames.cs - よく使うセンサー（CPU の温度・GPU の Hot Spot・CPU の使用率など）を名前で探す
//                  (CPU・GPU の世代やメーカーで名前が少しずつ違うので、候補の名前を順に試し、なければ同じ種類の最初のセンサーにする)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// センサーの役割ごとの、名前の候補（前ほど優先。大文字小文字は区別しない）。
/// 完全に同じ名前 → 名前に含む の順に探す。見つからなければ null（呼び出し側で「同じ部品の最初のセンサー」などにする）
/// </summary>
public static class SensorNames
{
    public static readonly string[] CpuTemp = { "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Tctl/Tdie", "Tctl", "Tdie", "CPU (Tctl/Tdie)", "Package", "Core Max", "Core Average" };
    public static readonly string[] GpuCoreTemp = { "GPU Core", "GPU Temperature", "GPU Edge", "Edge" };
    public static readonly string[] GpuHotSpot = { "GPU Hot Spot", "Hot Spot", "GPU Junction", "Junction", "Hotspot" };
    public static readonly string[] CpuLoad = { "CPU Total", "Total" };
    public static readonly string[] CpuPower = { "CPU Package", "Package", "CPU PPT", "PPT" };
    public static readonly string[] GpuLoad = { "GPU Core", "D3D 3D" };
    public static readonly string[] GpuClock = { "GPU Core", "GPU Shader" };
    public static readonly string[] VramUsed = { "GPU Memory Used", "D3D Dedicated Memory Used" };
    public static readonly string[] VramTotal = { "GPU Memory Total", "D3D Dedicated Memory Total" };

    /// <summary>名前の候補を順に試して、最初に見つかったもの（なければ null）</summary>
    public static T? Pick<T>(IEnumerable<T> items, Func<T, string> name, IReadOnlyList<string> candidates) where T : class
    {
        var list = items as IList<T> ?? items.ToList();
        foreach (var c in candidates)
            foreach (var x in list)
                if (string.Equals(name(x), c, StringComparison.OrdinalIgnoreCase)) return x;
        foreach (var c in candidates)
            foreach (var x in list)
                if (name(x).Contains(c, StringComparison.OrdinalIgnoreCase)) return x;
        return null;
    }

    /// <summary>センサーの一覧から: 部品の種類（HardwareType の頭）・センサーの種類（null なら問わない）で絞って、名前の候補で探す</summary>
    public static SensorInfo? Find(IEnumerable<SensorInfo> all, string hardware, string? sensorType, IReadOnlyList<string> candidates)
        => Pick(all.Where(s => s.HardwareType.StartsWith(hardware, StringComparison.Ordinal) && (sensorType is null || s.SensorType == sensorType)).ToList(),
            s => s.Name, candidates);

    /// <summary>
    /// 実メモリ（RAM）のセンサー。LibreHardwareMonitor は実メモリ（Total Memory・/ram）と、
    /// 仮想メモリ込みのコミット量（Virtual Memory・/vram）を同じ名前（Memory・Memory Used など）で出すので、仮想メモリ側は選ばない
    /// </summary>
    public static SensorInfo? PhysicalMemory(IEnumerable<SensorInfo> all, string sensorType, string name)
    {
        var list = all.Where(s => s.HardwareType.StartsWith("Memory", StringComparison.Ordinal) && s.SensorType == sensorType && s.Name == name
                                  && !s.Id.StartsWith("/vram/", StringComparison.Ordinal) && s.HardwareName != "Virtual Memory").ToList();
        return list.FirstOrDefault(s => s.Id.StartsWith("/ram/", StringComparison.Ordinal)) ?? list.FirstOrDefault();
    }

    /// <summary>CPU の温度（候補になければ、CPU の最初の温度）</summary>
    public static SensorInfo? CpuTemperature(IEnumerable<SensorInfo> temps)
    {
        var cpu = temps.Where(t => t.HardwareType.StartsWith("Cpu", StringComparison.Ordinal)).ToList();
        return Pick(cpu, t => t.Name, CpuTemp) ?? cpu.FirstOrDefault();
    }

    /// <summary>GPU のコアの温度（Hot Spot・メモリは除く。候補になければ、残りの最初の温度）</summary>
    public static SensorInfo? GpuCoreTemperature(IEnumerable<SensorInfo> temps, string? hardwareName = null)
    {
        var gpu = temps.Where(t => t.HardwareType.StartsWith("Gpu", StringComparison.Ordinal) && (hardwareName is null || t.HardwareName == hardwareName)).ToList();
        return Pick(gpu, t => t.Name, GpuCoreTemp)
               ?? gpu.FirstOrDefault(t => Pick(new[] { t }, x => x.Name, GpuHotSpot) is null && !t.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>GPU の Hot Spot（ないこともある）</summary>
    public static SensorInfo? GpuHotSpotTemperature(IEnumerable<SensorInfo> temps)
        => Pick(temps.Where(t => t.HardwareType.StartsWith("Gpu", StringComparison.Ordinal)).ToList(), t => t.Name, GpuHotSpot);
}

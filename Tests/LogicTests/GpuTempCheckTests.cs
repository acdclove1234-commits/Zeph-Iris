// グラボは見えているのに温度が読めないとき（GpuTempCheck）と、温度を読めたことを覚えること（HardwareChange.Remember）
using ProFanController;

namespace LogicTests;

public static class GpuTempCheckTests
{
    private static DeviceInfo Gpu(string key, string name, int temps) => new(key, name, "Gpu", null, temps, 0, 0, new List<string>());

    public static void TestMissing()
    {
        var known = new List<KnownDevice>();
        var lost = new[] { Gpu("/gpu-amd/5", "AMD Radeon RX 7900 XTX", 0), Gpu("/gpu-amd/0", "AMD Radeon(TM) Graphics", 0) };
        T.Check(GpuTempCheck.Missing(lost, known).SequenceEqual(new[] { "AMD Radeon RX 7900 XTX" }),
            "a discrete card without temperatures is reported; the CPU's built-in graphics (never had any) is not");
        T.Check(GpuTempCheck.Missing(new[] { Gpu("/gpu-amd/5", "AMD Radeon RX 7900 XTX", 4) }, known).Count == 0, "temperatures readable → nothing");
        T.Check(GpuTempCheck.Missing(new[] { Gpu("/gpu-nvidia/0", "NVIDIA GeForce RTX 5070 Ti", 0) }, known).Count == 1, "NVIDIA too");

        known.Add(new KnownDevice { Key = "/gpu-intel/0", Name = "Intel(R) Graphics", Kind = "Gpu", HadTemps = true });
        T.Check(GpuTempCheck.Missing(new[] { Gpu("/gpu-intel/0", "Intel(R) Graphics", 0) }, known).Count == 1, "any GPU that had temperatures before is reported");
        T.Check(GpuTempCheck.Message(new[] { "AMD Radeon RX 7900 XTX" }).Contains("AMD Radeon RX 7900 XTX"), "the message names the card");
    }

    public static void TestLooksDiscrete()
    {
        foreach (var n in new[] { "AMD Radeon RX 7900 XTX", "NVIDIA GeForce RTX 4070", "NVIDIA GeForce GTX 1660", "Intel(R) Arc(TM) A770 Graphics", "AMD Radeon Pro W7800" })
            T.Check(GpuTempCheck.LooksDiscrete(n), $"discrete: {n}");
        foreach (var n in new[] { "AMD Radeon(TM) Graphics", "Intel(R) UHD Graphics 770", "Intel(R) Graphics" })
            T.Check(!GpuTempCheck.LooksDiscrete(n), $"built-in: {n}");
    }
}

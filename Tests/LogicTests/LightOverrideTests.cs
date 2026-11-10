// ==================================================
// LightOverrideTests.cs - デバイス・ゾーンごとの光り方（グラボだけ赤・水冷のヘッドだけ白、など）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class LightOverrideTests
{
    private static readonly int[] Zones = { 10, 8, 8, 8 };
    private static RgbDevice Board() => new(0, "Board", 34, 0, Zones, "HID: x") { ZoneNames = new[] { "Board", "Header 1", "Header 2", "Header 3" } };

    private static Rgb[] Render(RgbConfig cfg, RgbDeviceSettings s, RgbDevice dev, double t = 2.0)
    {
        var r = new DeviceRenderer(dev);
        var input = new RenderInput { Cfg = cfg, Device = s, T = t, TEval = t, Temperature = 60 };
        var o = new Rgb[dev.LedCount];
        r.Render(input, o);
        return o;
    }

    /// <summary>設定の値は、知っている光り方と "#RRGGBB" だけ残す</summary>
    public static void TestNormalize()
    {
        T.Check(RgbLightModes.Normalize("Music") == "", "music is not a per-device mode");
        T.Check(RgbLightModes.Normalize("Static") == RgbModes.Static, "static kept");
        T.Check(RgbLightModes.Normalize(null) == "", "null → same as overall");
        T.Check(RgbLightModes.NormalizeColor("#ff0000") == "#FF0000", "colour upper-cased");
        T.Check(RgbLightModes.NormalizeColor("red") == "" && RgbLightModes.NormalizeColor("#12345") == "", "bad colours dropped");
        var z = new RgbZoneSettings { Name = "Header 1", LightMode = RgbModes.Static };
        T.Check(!z.IsDefault, "a zone with its own light mode is kept when saving");
    }

    /// <summary>デバイス全体を赤の固定色にすると、全体の光り方（虹）に関係なく全部赤</summary>
    public static void TestDeviceStaticRed()
    {
        var cfg = new RgbConfig { Mode = RgbModes.RainbowStatic, Brightness = 100, Gamma = false, StaticColor = "#0000FF" };
        var s = new RgbDeviceSettings { Name = "Board", LightMode = RgbModes.Static, LightColor = "#FF0000" };
        var o = Render(cfg, s, Board());
        T.Check(o.All(c => c == new Rgb(255, 0, 0)), $"all LEDs red ({o[0].Hex} … {o[^1].Hex})");
        var plain = Render(cfg, new RgbDeviceSettings { Name = "Board" }, Board());
        T.Check(plain.Distinct().Count() > 3, "without the override the rainbow is shown");
    }

    /// <summary>ゾーンだけ白・消灯にすると、そのゾーンの LED だけ変わり、ほかは全体の光り方のまま</summary>
    public static void TestZoneOverrides()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Static, Brightness = 100, Gamma = false, StaticColor = "#0000FF" };
        var s = new RgbDeviceSettings
        {
            Name = "Board",
            Zones = new()
            {
                new RgbZoneSettings { Name = "Header 1", LightMode = RgbModes.Static, LightColor = "#FFFFFF" },
                new RgbZoneSettings { Name = "Header 3", LightMode = RgbLightModes.Off },
            },
        };
        var o = Render(cfg, s, Board());
        T.Check(o[..10].All(c => c == new Rgb(0, 0, 255)), "board LEDs keep the overall blue");
        T.Check(o[10..18].All(c => c == new Rgb(255, 255, 255)), "header 1 is white");
        T.Check(o[18..26].All(c => c == new Rgb(0, 0, 255)), "header 2 keeps the overall blue");
        T.Check(o[26..].All(c => c == new Rgb(0, 0, 0)), "header 3 is off");
    }

    /// <summary>ゾーンの光り方が「デバイスと同じ」なら、デバイスの上書き（と色）を使う。ゾーンの色だけ変えることもできる</summary>
    public static void TestZoneInheritsDevice()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Temperature, Brightness = 100, Gamma = false, StaticColor = "#0000FF" };
        var s = new RgbDeviceSettings
        {
            Name = "Board", LightMode = RgbModes.Static, LightColor = "#00FF00",
            Zones = new() { new RgbZoneSettings { Name = "Header 2", LightColor = "#FF00FF" } },
        };
        var o = Render(cfg, s, Board());
        T.Check(o[..18].All(c => c == new Rgb(0, 255, 0)), "device green on board and header 1");
        T.Check(o[18..26].All(c => c == new Rgb(255, 0, 255)), "header 2 uses its own colour with the device's mode");
        var plan = RgbLightPlan.Build(s, 34, Zones, k => new[] { "Board", "Header 1", "Header 2", "Header 3" }[k]);
        T.Check(plan.CoversAll && plan.Segments.Count == 3, $"plan merges neighbours with the same look ({plan.Segments.Count} segments)");
    }

    /// <summary>グラボ（書き込みを減らすデバイス）は、上書きのレインボー（流れる）も止まった虹にする</summary>
    public static void TestGpuOverrideStaysStill()
    {
        var gpu = new RgbDevice(1, "NVIDIA GeForce RTX 5070 Ti", 6, RgbDevice.TypeGpu, new[] { 1, 1, 1, 1, 1, 1 }, "I2C: x");
        var cfg = new RgbConfig { Mode = RgbModes.Static, Brightness = 100, Gamma = false, StaticColor = "#0000FF" };
        var s = new RgbDeviceSettings { Name = gpu.Name, LightMode = RgbModes.Rainbow };
        var a = Render(cfg, s, gpu, 1.0);
        var b = Render(cfg, s, gpu, 9.0);
        T.Check(a.SequenceEqual(b) && a.Distinct().Count() > 1, "GPU: rainbow override is drawn as a still rainbow");
    }

    /// <summary>動き続ける光り方の上書きがあれば、全体が止まった光り方でも毎コマ送る</summary>
    public static void TestAnimatedOverrideDetected()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Static };
        T.Check(!RgbLightModes.AnyAnimated(cfg), "no overrides → not animated");
        cfg.DeviceSettings.Add(new RgbDeviceSettings { Name = "Kbd", LightMode = RgbModes.Static });
        T.Check(!RgbLightModes.AnyAnimated(cfg), "static override → not animated");
        cfg.DeviceSettings.Add(new RgbDeviceSettings { Name = "Board", Zones = new() { new RgbZoneSettings { Name = "Header 1", LightMode = RgbModes.Breathing } } });
        T.Check(RgbLightModes.AnyAnimated(cfg), "breathing zone → animated");
    }
}

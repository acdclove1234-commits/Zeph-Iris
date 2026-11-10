// ==================================================
// AmbientEffectsTests.cs - 動きのある光り方（雷・波・さえずり・炎・オーロラ・流れ星・心拍）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class AmbientEffectsTests
{
    private static LinColor[] Frame(string mode, double t, int n = 60, string color = "#0060FF", double speed = 1)
    {
        var cfg = new RgbConfig { Gamma = true, Speed = speed, StaticColor = color };
        var pos = Enumerable.Range(0, n).Select(i => n <= 1 ? 0 : i / (double)(n - 1)).ToArray();
        var dst = new LinColor[n];
        RgbEffects.FillLinear(mode, cfg, Rgb.Parse(color), t, null, pos, dst);
        return dst;
    }

    private static float Level(LinColor[] f) => f.Average(c => c.MaxChannel);

    /// <summary>どの光り方も、どの時刻でも、色は 0〜1 の範囲の数（NaN にならない）。動きがある</summary>
    public static void TestAllModesValidAndMoving()
    {
        foreach (var mode in RgbModes.Ambient)
        {
            bool ok = true;
            var levels = new List<float>();
            for (double t = 0; t < 30; t += 0.05)
            {
                var f = Frame(mode, t);
                foreach (var c in f)
                    if (!(float.IsFinite(c.R) && float.IsFinite(c.G) && float.IsFinite(c.B)) || c.R < 0 || c.G < 0 || c.B < 0 || c.MaxChannel > 1.0001f) ok = false;
                levels.Add(Level(f));
            }
            T.Check(ok, $"{mode}: every value finite and within 0–1");
            T.Check(levels.Max() - levels.Min() > 0.02f, $"{mode}: it moves ({levels.Min():F3}–{levels.Max():F3})");
            T.Check(RgbEffects.IsAnimated(mode) && RgbLightModes.IsAnimated(mode), $"{mode}: sent every frame");
            T.Check(RgbModes.Label(mode) != mode && RgbModes.IsKnown(mode), $"{mode}: has a label ({RgbModes.Label(mode)})");
        }
        T.Check(RgbModes.All.Distinct().Count() == RgbModes.All.Length, "no mode listed twice");
    }

    /// <summary>時刻だけで決まる: 同じ時刻なら同じ色。LED の数が違うデバイスでも、雷・流れ星は同じ瞬間に光る</summary>
    public static void TestDeterministicAcrossDevices()
    {
        foreach (var mode in RgbModes.Ambient)
            T.Check(Frame(mode, 12.34).SequenceEqual(Frame(mode, 12.34)), $"{mode}: same time, same colours");
        // 雷: 60 秒のうち、30 個のテープと 8 個のファンで、いちばん明るい瞬間が同じ
        double PeakT(int n)
        {
            double best = 0, bestT = 0;
            for (double t = 0; t < 60; t += 1 / 60.0)
            {
                float l = Frame(RgbModes.Lightning, t, n).Max(c => c.MaxChannel);
                if (l > best) { best = l; bestT = t; }
            }
            return bestT;
        }
        T.Check(Math.Abs(PeakT(30) - PeakT(8)) < 0.02, "lightning flashes at the same moment on every device");
    }

    /// <summary>雷: ほとんどは暗い空で、ときどき明るく光る（60 秒に何回か）</summary>
    public static void TestLightningMostlyDarkWithFlashes()
    {
        var levels = new List<float>();
        for (double t = 0; t < 60; t += 1 / 60.0) levels.Add(Frame(RgbModes.Lightning, t).Max(c => c.MaxChannel));
        var sorted = levels.OrderBy(x => x).ToList();
        float median = sorted[sorted.Count / 2], top = sorted[^1];
        int flashes = 0;
        for (int i = 1; i < levels.Count; i++) if (levels[i] > 0.4f && levels[i - 1] <= 0.4f) flashes++;
        T.Check(median < 0.08f, $"lightning sky is dark most of the time (median {median:F3})");
        T.Check(top > 0.6f, $"lightning flashes brightly (max {top:F2})");
        T.Check(flashes is >= 5 and <= 60, $"a handful of flashes per minute ({flashes} rises above 40%)");
    }

    /// <summary>波・心拍は「色」で選んだ色を使う。雷・炎などは決まった色</summary>
    public static void TestColourUse()
    {
        foreach (var mode in RgbModes.Ambient)
        {
            bool changes = !Frame(mode, 5, color: "#FF0000").SequenceEqual(Frame(mode, 5, color: "#00FF00"));
            T.Check(changes == RgbModes.UsesColor(mode), $"{mode}: uses the chosen colour = {RgbModes.UsesColor(mode)}");
            T.Check(RgbModes.UsesSpeed(mode), $"{mode}: shows the speed setting");
        }
    }

    /// <summary>心拍: 速さ 1 で 1 秒に 1 回、速さ 2 で 0.5 秒に 1 回「ドクン」</summary>
    public static void TestHeartbeatFollowsSpeed()
    {
        int Beats(double speed)
        {
            int beats = 0;
            float prev = 0;
            for (double t = 0; t < 10; t += 1 / 120.0)
            {
                float l = Level(Frame(RgbModes.Heartbeat, t, 4, "#FF0000", speed));
                if (l > 0.8f && prev <= 0.8f) beats++;
                prev = l;
            }
            return beats;
        }
        int one = Beats(1), two = Beats(2);
        T.Check(one is >= 9 and <= 11, $"speed 1: about 10 beats in 10 s ({one})");
        T.Check(two is >= 19 and <= 21, $"speed 2: about 20 beats in 10 s ({two})");
    }

    /// <summary>グラボ（書き込みを減らすデバイス）は止まった絵にする。「虹・呼吸も動かす」なら動かす</summary>
    public static void TestGpuStaysStill()
    {
        var gpu = new RgbDevice(1, "NVIDIA GeForce RTX 5070 Ti", 6, RgbDevice.TypeGpu, new[] { 1, 1, 1, 1, 1, 1 }, "I2C: x");
        Rgb[] Render(string mode, double t, bool animate)
        {
            var r = new DeviceRenderer(gpu);
            var input = new RenderInput
            {
                Cfg = new RgbConfig { Mode = mode, Brightness = 100, Gamma = true, StaticColor = "#0060FF" },
                Device = new RgbDeviceSettings { Name = gpu.Name, AnimateOnGpu = animate }, T = t, TEval = t,
            };
            var o = new Rgb[6];
            r.Render(input, o);
            return o;
        }
        foreach (var mode in new[] { RgbModes.Wave, RgbModes.Aurora, RgbModes.Fire })
        {
            T.Check(Render(mode, 1, false).SequenceEqual(Render(mode, 7.3, false)), $"{mode}: still on a GPU");
            T.Check(!Render(mode, 1, true).SequenceEqual(Render(mode, 7.3, true)), $"{mode}: moves on a GPU with \"animate\" on");
        }
    }

    /// <summary>デバイス・ゾーンごとの光り方にも選べる（例: キーボードだけ流れ星）</summary>
    public static void TestUsableAsDeviceOverride()
    {
        foreach (var mode in RgbModes.Ambient)
            T.Check(RgbLightModes.Normalize(mode) == mode, $"{mode}: allowed per device / zone");
    }
}

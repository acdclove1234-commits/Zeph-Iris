// ==================================================
// AmbientEffectsTests.cs - 動きのある光り方（雷・波・さえずり・炎・オーロラ・流れ星・心拍・ムード）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class AmbientEffectsTests
{
    private static LinColor[] Frame(string mode, double t, int n = 60, string color = "#0060FF", double speed = 1, string palette = Palettes.Rainbow, bool gamma = true)
    {
        var cfg = new RgbConfig { Gamma = gamma, Speed = speed, StaticColor = color, ModePalette = palette };
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
        foreach (var mode in new[] { RgbModes.Wave, RgbModes.Aurora, RgbModes.Fire, RgbModes.Mood })
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

    // ================================================================
    // ムード（音に反応しない光り方）

    /// <summary>色相（度）。色がほとんどないときは NaN</summary>
    private static double Hue(LinColor c)
    {
        double r = c.R, g = c.G, b = c.B, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max <= 1e-4 || max - min < 0.15 * max) return double.NaN;
        double h = max == r ? (g - b) / (max - min) : max == g ? 2 + (b - r) / (max - min) : 4 + (r - g) / (max - min);
        return (h * 60 + 360) % 360;
    }

    private static double HueGap(double a, double b) { double d = Math.Abs(a - b) % 360; return d > 180 ? 360 - d : d; }

    /// <summary>LED の並びでの色相の広がり（2 つの LED の色相のいちばん大きい差。度）</summary>
    private static double HueSpread(LinColor[] f)
    {
        var hues = f.Select(Hue).Where(h => !double.IsNaN(h)).ToList();
        double worst = 0;
        foreach (var a in hues) foreach (var b in hues) worst = Math.Max(worst, HueGap(a, b));
        return worst;
    }

    /// <summary>
    /// 30 秒間の見た目の特徴: 明るさのむら（LED の並びで、いちばん暗い LED がいちばん明るい LED よりどれだけ暗いか。0〜1）・
    /// 色相の広がり（LED の並び）・時間での色相の動き（真ん中の LED の、30 秒間の色相の範囲）
    /// </summary>
    private static (double Contrast, double Spread, double HueTravel) Look(string mode, string palette = Palettes.Rainbow)
    {
        double contrast = 0, spread = 0;
        var mid = new List<double>();
        int frames = 0;
        for (double t = 0; t < 30; t += 0.25)
        {
            var f = Frame(mode, t, 60, palette: palette, gamma: false);
            float max = f.Max(c => c.MaxChannel), min = f.Min(c => c.MaxChannel);
            contrast += max > 1e-4f ? 1 - min / max : 0;
            spread += HueSpread(f);
            double h = Hue(f[30]);
            if (!double.IsNaN(h)) mid.Add(h);
            frames++;
        }
        double travel = 0;
        foreach (var a in mid) foreach (var b in mid) travel = Math.Max(travel, HueGap(a, b));
        return (contrast / frames, spread / frames, travel);
    }

    /// <summary>
    /// ムードは、ほかの光り方とはっきり違って見える（見た目の特徴で比べる。パレット「レインボー」）:
    ///   ムード: ほぼ 1 色でむらがない（色相の広がり 60° 以下・むら 0.35 以下。レインボーは 150° 以上、オーロラはむらが大きい）・
    ///     時間で色が移り変わる（30 秒で 90° 以上。呼吸は色が変わらない）
    /// </summary>
    public static void TestStandaloneModesLookDistinct()
    {
        var look = new[] { RgbModes.Mood, RgbModes.Rainbow, RgbModes.Wave, RgbModes.Aurora, RgbModes.Breathing, RgbModes.Meteor }
            .ToDictionary(m => m, m => Look(m));
        foreach (var (m, l) in look) T.Info($"{m,-10} contrast {l.Contrast:F2}  hue spread {l.Spread:F0}°  hue travel {l.HueTravel:F0}°");
        var mood = look[RgbModes.Mood];
        T.Check(mood.Spread <= 60 && look[RgbModes.Rainbow].Spread >= 150, $"Mood is almost one colour along the LEDs, Rainbow is not ({mood.Spread:F0}° / {look[RgbModes.Rainbow].Spread:F0}°)");
        T.Check(mood.Contrast <= 0.35 && look[RgbModes.Aurora].Contrast >= 0.5, $"Mood is even, Aurora has bright bands ({mood.Contrast:F2} / {look[RgbModes.Aurora].Contrast:F2})");
        T.Check(mood.HueTravel >= 90 && look[RgbModes.Breathing].HueTravel < 5, $"Mood drifts through the palette, Breathing keeps its colour ({mood.HueTravel:F0}° / {look[RgbModes.Breathing].HueTravel:F0}°)");
    }

    /// <summary>
    /// ムードは音に反応しない: 音楽の音（大きな低音・キック）があってもなくても、描く色はまったく同じ。
    /// 60 コマ／秒で、1 コマの色の変化が小さい（ちらつかない）
    /// </summary>
    public static void TestStandaloneModesIgnoreSoundAndDoNotFlicker()
    {
        var dev = new RgbDevice(3, "Strip", 30, 4, null, "HID: x");
        foreach (var mode in new[] { RgbModes.Mood })
        {
            var quiet = new DeviceRenderer(dev);
            var loud = new DeviceRenderer(dev);
            var cfg = new RgbConfig { Mode = mode, Brightness = 100, Gamma = true, ModePalette = Palettes.Neon };
            var a = new Rgb[30];
            var b = new Rgb[30];
            var prev = new Rgb[30];
            bool same = true;
            int worstStep = 0;
            for (int k = 0; k < 60 * 20; k++)
            {
                double t = 5 + k / 60.0;
                var qi = new RenderInput { Cfg = cfg, T = t, TEval = t, Dt = 1 / 60.0 };
                var li = new RenderInput { Cfg = cfg, T = t, TEval = t, Dt = 1 / 60.0, Music = MusicTuning.Resolve(cfg) };
                li.Frame.GateOpen = true;
                li.Frame.Activity = 1;
                li.Frame.BassE = li.Frame.MidE = li.Frame.TrebleE = k % 30 < 3 ? 1f : 0.2f;
                li.Frame.LoudVU = k % 30 < 3 ? 1f : 0.3f;
                if (k % 30 == 0) li.Events.Add(new AudioEvent(k + 1, AudioEventKind.Kick, t, 3f));
                quiet.Render(qi, a);
                loud.Render(li, b);
                same &= a.SequenceEqual(b);
                if (k > 60)
                    for (int i = 0; i < 30; i++)
                        worstStep = Math.Max(worstStep, Math.Max(Math.Abs(a[i].R - prev[i].R), Math.Max(Math.Abs(a[i].G - prev[i].G), Math.Abs(a[i].B - prev[i].B))));
                Array.Copy(a, prev, 30);
            }
            T.Check(same, $"{mode}: the same colours with or without music (it does not react to sound)");
            T.Check(worstStep <= 24, $"{mode}: no sudden jumps between frames at 60 fps (largest step {worstStep} of 255)");
        }
    }

    /// <summary>ムードは「色の組み合わせ」の色で光る。「選んだ色だけ」なら、その色だけ</summary>
    public static void TestStandaloneModesUseTheModePalette()
    {
        foreach (var mode in new[] { RgbModes.Mood })
        {
            T.Check(!Frame(mode, 5, palette: Palettes.Ocean).SequenceEqual(Frame(mode, 5, palette: Palettes.Fire)), $"{mode}: a different palette gives different colours");
            // 選んだ色だけ: どの LED も、選んだ色（赤）
            bool red = true;
            for (double t = 0; t < 20; t += 0.5)
                foreach (var c in Frame(mode, t, 30, "#FF0000", palette: Palettes.Solid, gamma: false))
                    if (c.MaxChannel > 0.02f && !(c.R > 0 && c.G <= c.R * 0.02f && c.B <= c.R * 0.02f)) red = false;
            T.Check(red, $"{mode}: the Solid palette shows only the chosen colour");
            T.Check(!Frame(mode, 5, color: "#FF0000", palette: Palettes.Mono).SequenceEqual(Frame(mode, 5, color: "#00FF00", palette: Palettes.Mono)),
                $"{mode}: the Mono palette follows the chosen colour");
        }
    }
}

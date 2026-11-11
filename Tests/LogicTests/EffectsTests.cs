// ==================================================
// EffectsTests.cs - 音楽連動の光り方・パレット・重ね合わせ・仕上げ (OutputStage) のロジックテスト
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class EffectsTests
{
    // ================= テスト用の部品 =================

    private const double Hop = 512.0 / 48000;

    private static readonly int[] AsrockZones = { 10, 80, 80, 80 };
    private static readonly int[] GpuZones = { 1, 1, 1, 1, 1, 1 };

    private static RgbDevice Asrock() => new(0, "ASRock Z890 Taichi", 250, 0, AsrockZones, "HID: /dev/hidraw3");
    private static RgbDevice Gpu() => new(1, "NVIDIA GeForce RTX 5070 Ti", 6, 2, GpuZones, "I2C: /dev/i2c-3, address 0x49");
    private static RgbDevice Strip(int n, bool slow = false) => new(2, "Strip", n, 4, null, slow ? "I2C: x" : "HID: x");

    private static ResolvedMusic Music(string effect, string palette = Palettes.Neon) =>
        ResolvedMusic.Default with { Effect = effect, Palette = palette };

    /// <summary>
    /// 合成した音楽（123 BPM。キック・スネア・ハイハット）。分析結果 1 コマと、出来事を作る。
    /// 拍の頭・LED の描画の周期がそろわない値にしてある（そろうと 1 秒間のコマ数の比較が偏る）
    /// </summary>
    private sealed class Synth
    {
        public double Period = 60.0 / 123;
        public double FirstBeat = 0.137;
        public bool Locked = true;
        public double Conf = 0.85;
        public double SilentFrom = double.PositiveInfinity, SilentUntil = double.PositiveInfinity;
        public bool UseBeat = true;
        private BeatState? _beat;

        public BeatState Beat => !UseBeat ? BeatState.None : _beat ??= new BeatState(Period, FirstBeat, 0, Conf, Locked, 0);

        public bool Silent(double t) => t >= SilentFrom && t < SilentUntil;

        private static double Age(double t, double first, double period)
        {
            if (t < first) return 1e9;
            double a = (t - first) % period;
            return a;
        }

        private static float Env(double age, double attack, double tau) =>
            age < 0 ? 0f : age < attack ? (float)(age / attack) : (float)Math.Exp(-(age - attack) / tau);

        public void Fill(double t, AudioFrame f)
        {
            f.T = t;
            if (Silent(t))
            {
                double keepT = t;
                f.Clear();
                f.T = keepT;
                return;
            }
            float kick = Env(Age(t, FirstBeat, Period), 0.006, 0.12);
            float snare = Env(Age(t, FirstBeat + Period, 2 * Period), 0.004, 0.10);
            float hat = Env(Age(t, FirstBeat, Period / 2), 0.002, 0.04);
            float slow = 0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * t / 3.7);
            f.KickEnv = kick;
            f.BassE = Math.Clamp(0.12f + 0.85f * kick, 0, 1);
            f.SubE = 0.9f * f.BassE;
            f.MidE = Math.Clamp(0.25f + 0.2f * slow + 0.5f * snare, 0, 1);
            f.TrebleE = Math.Clamp(0.15f + 0.15f * slow + 0.6f * hat, 0, 1);
            f.Sub = 0.2f + 0.5f * kick;
            f.Bass = 0.25f + 0.5f * kick;
            f.Mid = 0.3f + 0.3f * slow;
            f.Treble = 0.2f + 0.3f * hat;
            for (int k = 0; k < AudioFrame.BandCount; k++)
            {
                float p = k < 7 ? f.BassE * (1 - k * 0.06f) : k < 16 ? f.MidE * (0.9f + 0.1f * (float)Math.Sin(k)) : f.TrebleE * (1 - (k - 16) * 0.03f);
                f.Punch[k] = Math.Clamp(p, 0, 1);
                f.Band[k] = 0.6f * f.Punch[k] + 0.2f;
            }
            f.LoudVU = Math.Clamp(0.45f + 0.25f * slow + 0.15f * kick, 0, 1);
            f.LoudDb = -20;
            f.RmsDb = -18;
            f.CeilingDb = -12;
            f.CentroidHz = (float)(300 * Math.Pow(2, 2.5 * slow));
            f.Activity = 1;
            f.GateOpen = true;
            f.Extrapolated = false;
        }

        /// <summary>時刻が (t0, t1] の出来事を ring に足す（8 分音符ごと: 拍の頭はキック・立ち上がり・ハイハット、裏拍の 1・3 拍目はスネア）</summary>
        public void AddEvents(double t0, double t1, EventRing ring)
        {
            double half = Period / 2;
            long m0 = Math.Max(0, (long)Math.Floor((t0 - FirstBeat) / half));
            for (long m = m0; ; m++)
            {
                double te = FirstBeat + m * half;
                if (te > t1) break;
                if (te <= t0 || Silent(te)) continue;
                if (m % 2 == 0)
                {
                    long b = m / 2;
                    ring.Add(AudioEventKind.Kick, te, 2.0f);
                    ring.Add(AudioEventKind.Onset, te + 0.001, 1.7f);
                    ring.Add(AudioEventKind.Hat, te, 1.5f);
                    if (b % 2 == 1) ring.Add(AudioEventKind.Snare, te + 0.002, 1.6f);
                }
                else ring.Add(AudioEventKind.Hat, te, 1.3f);
            }
        }
    }

    /// <summary>描画のテスト台: 合成した音楽を分析結果の記録・出来事の記録に流し込み、RgbSync と同じように 1 コマずつ描く</summary>
    private sealed class Rig
    {
        public readonly DeviceRenderer R;
        public readonly RenderInput In = new();
        public readonly Rgb[] Out;
        public readonly FeatureRing Ring = new();
        public readonly EventRing Events = new();
        public readonly Synth Synth = new();
        public bool UseRing = true;
        /// <summary>出来事が出来事の記録に届くまでの遅れ（検出にかかる時間）</summary>
        public double DetectDelay = 0.025;
        private readonly AudioFrame _f = new();
        private long _cursor;
        private double _hopT;
        private double _lastT = double.NaN;

        public Rig(RgbDevice dev, RgbConfig cfg, ResolvedMusic m, RgbDeviceSettings? ds = null)
        {
            R = new DeviceRenderer(dev);
            Out = new Rgb[dev.LedCount];
            In.Cfg = cfg;
            In.Music = m;
            In.Device = ds ?? new RgbDeviceSettings { Name = dev.Name };
        }

        public void Step(double t)
        {
            while (_hopT + Hop <= t)
            {
                _hopT += Hop;
                Synth.Fill(_hopT, _f);
                Ring.Write(_f);
                Synth.AddEvents(_hopT - Hop - DetectDelay, _hopT - DetectDelay, Events);
            }
            In.T = t;
            In.TEval = t;
            In.TPrevEval = double.IsNaN(_lastT) ? t : _lastT;
            In.Dt = double.IsNaN(_lastT) ? 0 : Math.Min(0.1, t - _lastT);
            In.Features = UseRing ? Ring : null;
            Ring.Sample(t, In.Frame);
            In.Beat = Synth.Beat;
            In.Events.Clear();
            Events.Read(ref _cursor, t, In.Events);
            R.Render(In, Out);
            _lastT = t;
        }

        /// <summary>t0 から t1 まで fps で描く</summary>
        public void Run(double t0, double t1, double fps)
        {
            int frames = (int)Math.Round((t1 - t0) * fps);
            for (int i = 0; i < frames; i++) Step(t0 + i / fps);
        }
    }

    /// <summary>手で作った分析結果 1 コマで描く（音の記録・出来事なし）</summary>
    private sealed class Manual
    {
        public readonly DeviceRenderer R;
        public readonly RenderInput In = new();
        public readonly Rgb[] Out;
        private double _lastT = double.NaN;

        public Manual(RgbDevice dev, RgbConfig cfg, ResolvedMusic m, RgbDeviceSettings? ds = null)
        {
            R = new DeviceRenderer(dev);
            Out = new Rgb[dev.LedCount];
            In.Cfg = cfg;
            In.Music = m;
            In.Device = ds ?? new RgbDeviceSettings { Name = dev.Name };
            In.Frame.GateOpen = true;
            In.Frame.Activity = 1;
        }

        public void Step(double t, params AudioEvent[] events)
        {
            In.T = t;
            In.TEval = t;
            In.TPrevEval = double.IsNaN(_lastT) ? t : _lastT;
            In.Dt = double.IsNaN(_lastT) ? 0 : t - _lastT;
            In.Frame.T = t;
            In.Events.Clear();
            In.Events.AddRange(events);
            R.Render(In, Out);
            _lastT = t;
        }
    }

    private static RgbConfig MusicCfg(bool gamma = true) => new() { Mode = RgbModes.Music, Brightness = 100, Gamma = gamma, StaticColor = "#6A00FF" };

    private static float Max(ReadOnlySpan<LinColor> light, int i) => light[i].MaxChannel;

    private static bool Finite(ReadOnlySpan<LinColor> light)
    {
        foreach (var c in light)
            if (!float.IsFinite(c.R) || !float.IsFinite(c.G) || !float.IsFinite(c.B) || c.R < 0 || c.G < 0 || c.B < 0) return false;
        return true;
    }

    /// <summary>色の向き（いちばん強い成分を 1 にそろえる）どうしの差</summary>
    private static float HueDiff(LinColor a, LinColor b)
    {
        float ma = Math.Max(a.MaxChannel, 1e-6f), mb = Math.Max(b.MaxChannel, 1e-6f);
        return Math.Max(Math.Abs(a.R / ma - b.R / mb), Math.Max(Math.Abs(a.G / ma - b.G / mb), Math.Abs(a.B / ma - b.B / mb)));
    }

    // ================= OKLab・パレット =================

    public static void TestOkLabRoundTrip()
    {
        var w = OkLab.FromEncoded(1, 1, 1);
        T.Near(w.L, 1, 1e-3, "white L = 1");
        T.Check(Math.Abs(w.A) < 1e-3 && Math.Abs(w.B) < 1e-3, "white has no colour (a = b = 0)");
        var red = OkLab.FromRgb(new Rgb(255, 0, 0));
        T.Check(Math.Abs(red.L - 0.62796) < 1e-3 && Math.Abs(red.A - 0.22486) < 1e-3 && Math.Abs(red.B - 0.12585) < 1e-3,
            $"sRGB red matches the published OKLab value ({red.L:F4}, {red.A:F4}, {red.B:F4})");

        var rng = new XorShift32(12345);
        double worst = 0;
        for (int i = 0; i < 2000; i++)
        {
            float r = rng.NextFloat(), g = rng.NextFloat(), b = rng.NextFloat();
            var lab = OkLab.FromEncoded(r, g, b);
            OkLab.ToEncoded(lab, out float r2, out float g2, out float b2);
            worst = Math.Max(worst, Math.Max(Math.Abs(r - r2), Math.Max(Math.Abs(g - g2), Math.Abs(b - b2))));
        }
        T.Check(worst < 1e-3, $"sRGB → OKLab → sRGB round trip (worst error {worst:E2})");
        T.Check(OkLab.ToRgb(OkLab.FromRgb(new Rgb(0x7C, 0x3A, 0xED))) == new Rgb(0x7C, 0x3A, 0xED), "byte colour round trip is exact");

        var tooVivid = Lab.FromLch(0.7f, 0.5f, 2.0f);
        var fit = OkLab.FitGamut(tooVivid);
        T.Check(!OkLab.InGamut(tooVivid) && OkLab.InGamut(fit) && Math.Abs(fit.Hue - tooVivid.Hue) < 1e-3 && Math.Abs(fit.L - 0.7f) < 1e-4,
            "out-of-gamut colour: chroma reduced, hue and lightness kept");

        // 光の量のまま混ぜると、赤と緑の中間は暗くならない（画面の色のまま混ぜると暗い黄土色になる）
        var mid = LinColor.Lerp(OutputStage.Lin(new Rgb(255, 0, 0), true), OutputStage.Lin(new Rgb(0, 255, 0), true), 0.5f);
        T.Near(mid.R + mid.G, 1, 1e-4, "linear-light mix keeps total light");
        T.Near(OutputStage.Pow22(0.5f), Math.Pow(0.5, 2.2), 1e-5, "gamma LUT matches x^2.2");
        T.Near(OutputStage.Floor(0.2, true), Math.Pow(0.2, 2.2), 1e-5, "floor F0 = MinBrightness^2.2");
        T.Near(OutputStage.Ilin(0, OutputStage.Floor(0.2, true), true), Math.Pow(0.2, 2.2), 1e-5, "Ilin(0) = F0 (floor survives gamma)");
        T.Near(OutputStage.Ilin(1, 0.3f, true), 1, 1e-6, "Ilin(1) = 1");
    }

    public static void TestPaletteLut()
    {
        var neon = ColorPalette.Get(Palettes.Neon, "#123456");
        string[] stops = { "#FF0080", "#7C3AED", "#00E5FF", "#00FF85" };
        for (int k = 0; k < 4; k++)
        {
            var got = neon.ScreenAt(k / 4.0);
            var want = Rgb.Parse(stops[k]);
            T.Check(Math.Abs(got.R - want.R) <= 1 && Math.Abs(got.G - want.G) <= 1 && Math.Abs(got.B - want.B) <= 1,
                $"Neon stop {k} = {stops[k]} (got {got.Hex})");
        }
        T.Check(neon.ScreenAt(1.0) == neon.ScreenAt(0) && neon.ScreenAt(-0.25) == neon.ScreenAt(0.75) && neon.ScreenAt(2.5) == neon.ScreenAt(0.5),
            "palette is cyclic: P(x) = P(x mod 1)");
        var nearEnd = neon.LabAt(0.999);
        var start = neon.LabAt(0);
        double gap = Math.Sqrt(Math.Pow(nearEnd.L - start.L, 2) + Math.Pow(nearEnd.A - start.A, 2) + Math.Pow(nearEnd.B - start.B, 2));
        T.Check(gap < 0.01, $"cyclic wrap is continuous: last stop blends back into the first (OKLab distance {gap:F4})");
        T.Check(neon.Size == 256, "4-stop palette LUT has 256 entries");
        T.Near(neon.Span, 0.75, 1e-9, "Neon span = 3/4 (P(span) = last stop)");
        T.Check(neon.ScreenAt(neon.Span) == neon.ScreenAt(0.75), "P(span) is the last stop");
        T.Near(neon.Step, 0.25, 1e-9, "palette step = 1 / stops");
        var rainbow = ColorPalette.Get(Palettes.Rainbow, null);
        T.Near(rainbow.Step, 70.0 / 360, 1e-9, "rainbow step = 70°");
        T.Check(rainbow.ScreenAt(0) == new Rgb(255, 0, 0) && rainbow.ScreenAt(1.0 / 6) == new Rgb(255, 255, 0), "rainbow stops red, yellow");
        T.Check(ReferenceEquals(ColorPalette.Get(Palettes.Neon, "#000000"), neon), "palette LUT is cached (built once)");
        T.Check(ColorPalette.Get("NoSuchPalette", null).Name == Palettes.Rainbow, "unknown palette → rainbow");

        var mono = ColorPalette.Get(Palettes.Mono, "#6A00FF");
        T.Check(mono.StopCount == 3 && mono.ScreenAt(0) == new Rgb(0x6A, 0x00, 0xFF), $"Mono P(0) = the chosen colour ({mono.ScreenAt(0).Hex})");
        var m1 = OkLab.FromRgb(mono.StopColors[1]);
        var m0 = OkLab.FromRgb(mono.StopColors[0]);
        double dh = (m1.Hue - m0.Hue) * 180 / Math.PI;
        T.Check(Math.Abs(dh - 25) < 3 && m1.L > m0.L, $"Mono neighbour: hue +25° ({dh:F1}°), lighter");
        T.Check(!ReferenceEquals(mono, ColorPalette.Get(Palettes.Mono, "#00FF00")), "Mono depends on the chosen colour");

        var meter = ColorPalette.Meter;
        T.Check(meter.ScreenAt(0) == Rgb.Parse("#34D399") && meter.ScreenAt(meter.Span) == Rgb.Parse("#FF3B30"), "meter palette green → red");

        var led = neon.At(0, gamma: true);
        T.Check(Math.Abs(led.R - 1) < 1e-4 && Math.Abs(led.B - Math.Pow(0x80 / 255.0, 2.2)) < 2e-3 && led.G < 1e-4,
            $"palette LED colour is gamma-corrected linear light ({led.R:F3}, {led.G:F3}, {led.B:F3})");

        // 色の切り替え: OKLab で、始めと終わりがゆっくり（smoothstep）
        var fade = new ColorFade();
        fade.Reset(0);
        fade.Step(10.0, 0.25, 0.25);
        var half = fade.SampleLab(neon, 0, 10.125);
        var expect = Lab.Lerp(neon.LabAt(0), neon.LabAt(0.25), 0.5f);
        T.Check(Math.Abs(half.L - expect.L) < 1e-4 && Math.Abs(half.A - expect.A) < 1e-4, "colour step crossfades in OKLab (midpoint at half time)");
        var quarter = fade.SampleLab(neon, 0, 10.0625);
        var q = Lab.Lerp(neon.LabAt(0), neon.LabAt(0.25), 0.15625f); // smoothstep(0.25)
        T.Check(Math.Abs(quarter.A - q.A) < 1e-4, "crossfade uses smoothstep easing");
        T.Check(fade.Sample(neon, 0, 10.3, true) == neon.At(0.25, true), "after the crossfade: exactly the next palette colour");
    }

    // ================= 全部の光り方が、どの LED の数でも動く =================

    public static void TestAllEffectsAllLayouts()
    {
        var layouts = new (string Name, Func<RgbDevice> Dev)[]
        {
            ("1 LED", () => Strip(1)), ("3 LEDs", () => Strip(3)), ("6 LEDs", () => Strip(6)), ("30 LEDs", () => Strip(30)),
            ("80 LEDs", () => Strip(80)), ("250 LEDs", () => Strip(250)), ("ASRock {10,80,80,80}", Asrock), ("GPU {1x6}", Gpu),
        };
        int ok = 0, total = 0;
        var failures = new List<string>();
        foreach (var effect in MusicStyles.All)
        {
            foreach (var (name, make) in layouts)
            {
                total++;
                try
                {
                    var m = Music(effect) with { StrobeAllowed = true, ReduceFlashing = effect != MusicStyles.Strobe };
                    var rig = new Rig(make(), MusicCfg(), m);
                    if (effect == MusicStyles.Ripple) rig.Synth.Locked = false; // 予測ビートとキックの両方を通す
                    float peak = 0;
                    bool finite = true;
                    for (int i = 0; i < 600; i++)
                    {
                        rig.Step(0.3 + i / 60.0);
                        if (!Finite(rig.R.LastLight)) finite = false;
                        foreach (var c in rig.R.LastLight) peak = Math.Max(peak, c.MaxChannel);
                    }
                    if (!finite) failures.Add($"{effect} / {name}: NaN or negative light");
                    else if (peak < 0.05f) failures.Add($"{effect} / {name}: stays dark (peak {peak:F3})");
                    else ok++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{effect} / {name}: {ex.GetType().Name} {ex.Message}");
                }
            }
        }
        foreach (var f in failures) T.Info(f);
        T.Check(ok == total, $"all 12 effects render 10 s on 1/3/6/30/80/250 LEDs, ASRock and GPU layouts ({ok}/{total})");

        // 小さいゾーン（6 個未満）は全体で光る代わりの光り方
        var small = new Rig(Strip(3), MusicCfg(), Music(MusicStyles.Spectrum));
        small.Run(0.3, 2, 60);
        var l = small.R.LastLight;
        T.Check(l[0] == l[1] && l[1] == l[2], "Spectrum on a 3-LED zone falls back to a whole-zone colour (Mix)");
    }

    // ================= 個々の光り方 =================

    public static void TestBeatHueStepsThroughPalette()
    {
        var m = Music(MusicStyles.BeatHue) with { TempoMode = TempoModes.KickOnly, ColorStepBeats = 2, CrossfadeMs = 250, MinBrightness = 0.2 };
        var cfg = MusicCfg();
        var x = new Manual(Strip(25), cfg, m, new RgbDeviceSettings { Mirror = false });
        var neon = ColorPalette.Get(Palettes.Neon, null);
        x.In.Frame.BassE = 0.5f;
        var seen = new List<LinColor>();
        double t = 1;
        for (int k = 0; k < 8; k++)
        {
            double kick = 1 + k * 0.5;
            for (; t < kick + 0.4; t += 1 / 60.0)
            {
                bool hit = t >= kick && t - 1 / 60.0 < kick;
                if (hit) x.Step(t, new AudioEvent(k + 1, AudioEventKind.Kick, kick, 2f));
                else x.Step(t);
            }
            seen.Add(x.R.LastLight[12]); // 真ん中の LED（p = 0.5 なので色のずれなし）
            for (; t < kick + 0.5; t += 1 / 60.0) x.Step(t);
        }
        // 2 回の合図ごとに次の色: 合図 2・4・6・8 回目のあとで 1・2・3・0 番目の色
        bool palette = true;
        string got = "";
        for (int k = 0; k < 8; k++)
        {
            int idx = ((k + 1) / 2) % 4;
            float d = HueDiff(seen[k], neon.At(idx * 0.25, true));
            got += $"{d:F2} ";
            if (d > 0.08f) palette = false;
        }
        T.Check(palette, $"BeatHue: colour steps to the next Neon colour every 2 kicks (hue error per kick: {got})");
        T.Check(HueDiff(seen[0], seen[1]) > 0.3f && HueDiff(seen[1], seen[2]) < 0.08f, "colour changes on the step trigger and holds between");
        T.Check(x.R.ActiveEffect == MusicStyles.BeatHue, "ActiveEffect = BeatHue");

        // Mono パレット = 選んだ色
        var mono = new Manual(Strip(10), MusicCfg(), m with { Palette = Palettes.Mono });
        mono.In.Cfg.StaticColor = "#00FF00";
        mono.In.Frame.BassE = 1;
        mono.Step(1);
        mono.Step(1.02);
        var c = mono.R.LastLight[5];
        // 明るさ = Ilin(0.6 × 低音) = 0.6^2.2 + F0 ≈ 0.34
        T.Check(c.G > 0.25f && c.R < 0.05f * c.G && c.B < 0.05f * c.G, $"BeatHue with Mono uses the chosen colour (green) ({c.R:F2},{c.G:F2},{c.B:F2})");
    }

    public static void TestPulseAfterglow()
    {
        var cfg = MusicCfg(gamma: false);
        var m = Music(MusicStyles.Pulse, Palettes.Fire) with { TempoMode = TempoModes.Off, MinBrightness = 0 };
        foreach (bool slow in new[] { false, true })
        {
            var x = new Manual(slow ? Strip(5, slow: true) : Strip(5), cfg, m);
            double tau = slow ? 0.45 : 0.25;
            double t = 0;
            for (; t < 1.0; t += 0.01) { x.In.Frame.BassE = 0; x.Step(t); }
            for (; t < 1.1; t += 0.01) { x.In.Frame.BassE = 1; x.Step(t); }
            double tEnd = t - 0.01;
            float top = x.R.LastLight[0].R;
            double at1 = 0, at2 = 0;
            for (; t < 2.0; t += 0.01)
            {
                x.In.Frame.BassE = 0;
                x.Step(t);
                if (Math.Abs(t - (tEnd + tau)) < 0.005) at1 = x.R.LastLight[0].R;
                if (Math.Abs(t - (tEnd + 2 * tau)) < 0.005) at2 = x.R.LastLight[0].R;
            }
            string v = slow ? "slow device" : "fast device";
            // 遅いデバイスは立ち上がりを 40 ミリ秒遅らせる（0.1 秒の低音で 1 − e^−2.5 = 0.92）
            T.Check(top > (slow ? 0.9f : 0.99f), $"Pulse ({v}): bass hit lights the device fully ({top:F3})");
            // 遅いデバイスは、いちばん暗いときでも 45% は光らせる（チカチカ防止）。床を引いた残りが e^-1・e^-2 で減る
            double f0 = slow ? DeviceRenderer.SlowMinBrightness : 0;
            T.Near((at1 - f0) / (top - f0), Math.Exp(-1), 0.03, $"Pulse ({v}): afterglow after τg = {tau}s is e^-1");
            T.Near((at2 - f0) / (top - f0), Math.Exp(-2), 0.03, $"Pulse ({v}): afterglow after 2τg is e^-2");
            T.Check(x.R.LastLight[0].G < 0.01f && x.R.LastLight[0].B < 0.01f, $"Pulse ({v}): colour is the palette's first colour (Fire = red)");
        }
    }

    public static void TestSpectrumFollowsBands()
    {
        var cfg = MusicCfg(gamma: false);
        var m = Music(MusicStyles.Spectrum, Palettes.Mono) with { MinBrightness = 0, TempoMode = TempoModes.Off };
        cfg.StaticColor = "#FFFFFF";
        var x = new Manual(Strip(48), cfg, m, new RgbDeviceSettings { Mirror = false });
        void Bands(bool bass)
        {
            for (int k = 0; k < AudioFrame.BandCount; k++) x.In.Frame.Punch[k] = (bass ? k < 3 : k >= 7) ? 1f : 0f; // 低音 = 31〜125 Hz、高音 = 4〜16 kHz
        }
        double t = 0;
        Bands(true);
        for (; t < 2; t += 1 / 60.0) x.Step(t);
        float low = Max(x.R.LastLight, 1), high = Max(x.R.LastLight, 46);
        T.Check(low > 0.6f && high < 0.15f, $"Spectrum: bass lights the low end, not the high end ({low:F2} / {high:F2})");
        Bands(false);
        for (; t < 4; t += 1 / 60.0) x.Step(t);
        low = Max(x.R.LastLight, 1);
        high = Max(x.R.LastLight, 46);
        T.Check(high > 0.6f && low < 0.15f, $"Spectrum: treble lights the high end, bass end falls after the hold ({low:F2} / {high:F2})");

        // 30 個以上のゾーンは自動で左右対称（中央が低音）
        var y = new Manual(Strip(60), cfg, m);
        for (int k = 0; k < AudioFrame.BandCount; k++) y.In.Frame.Punch[k] = k < 3 ? 1f : 0f;
        for (t = 0; t < 1; t += 1 / 60.0) y.Step(t);
        T.Check(Max(y.R.LastLight, 30) > 0.6f && Max(y.R.LastLight, 0) < 0.15f && Max(y.R.LastLight, 59) < 0.15f,
            "Spectrum on a 60-LED strip: auto mirror puts the bass in the centre");
    }

    public static void TestMeterGravityDot()
    {
        var cfg = MusicCfg(gamma: false);
        var m = Music(MusicStyles.Meter) with { MinBrightness = 0, TempoMode = TempoModes.Off };
        var x = new Manual(Strip(41), cfg, m, new RgbDeviceSettings { Mirror = false });
        double t = 0;
        // ビートがないときの長さは、音量の LoudShare 倍（音量 0.8 → 0.4、0.1 → 0.05）
        float high = MeterEffect.LoudShare * 0.8f, low = MeterEffect.LoudShare * 0.1f;
        for (; t < 1.0; t += 0.01) { x.In.Frame.LoudVU = 0.8f; x.Step(t); }
        var meter = (MeterEffect)x.R.CurrentEffect!;
        T.Near(meter.DotHeight, high, 1e-3, "Meter: dot sits at the level peak");
        double drop = t;
        double h04 = 0, h09 = 0, h11 = 0;
        for (; t < drop + 1.6; t += 0.01)
        {
            x.In.Frame.LoudVU = 0.1f;
            x.Step(t);
            double age = t - drop;
            if (Math.Abs(age - 0.4) < 0.005) h04 = meter.DotHeight;
            if (Math.Abs(age - 0.9) < 0.005) h09 = meter.DotHeight;
            if (Math.Abs(age - 1.1) < 0.005) h11 = meter.DotHeight;
        }
        T.Near(h04, high, 0.02, "Meter: dot holds for 0.5 s after the level drops");
        T.Near(h09, high - 0.5 * 2.5 * 0.4 * 0.4, 0.03, "Meter: then falls with gravity (h0 − ½·2.5·t²)");
        T.Check(h11 < h09 && h11 >= low - 1e-3, $"Meter: dot keeps falling but never below the level ({h11:F3})");
        T.Near(meter.DotHeight, low, 0.02, "Meter: dot lands on the current level");
        // 点の位置の LED は白っぽい（3 色とも明るい）
        int dotLed = (int)Math.Round(meter.DotHeight * 40);
        var d = x.R.LastLight[dotLed];
        T.Check(Math.Min(d.R, Math.Min(d.G, d.B)) > 0.5f, $"Meter: the dot LED ({dotLed}) is white");
        var lit = x.R.LastLight[1];
        var unlit = x.R.LastLight[30];
        T.Check(lit.MaxChannel > 0.5f && unlit.MaxChannel < 0.05f, "Meter: LEDs below the level are lit, above are dark");
    }

    public static void TestMeterBouncesOnBeats()
    {
        var cfg = MusicCfg(gamma: false);
        var m = Music(MusicStyles.Meter) with { MinBrightness = 0, TempoMode = TempoModes.Off };
        var x = new Manual(Strip(41), cfg, m, new RgbDeviceSettings { Mirror = false });
        double t = 0;
        // 低音が鳴り続けても（BassE が高いまま）、それだけでは弾まない
        for (; t < 1.0; t += 0.01) { x.In.Frame.LoudVU = 0.8f; x.In.Frame.BassE = 1; x.Step(t); }
        var meter = (MeterEffect)x.R.CurrentEffect!;
        float rest = meter.Level;
        T.Near(rest, MeterEffect.LoudShare * 0.8, 0.01, $"steady loud music with sustained bass, no kicks: about half ({rest:F2})");
        x.Step(t, new AudioEvent(1, AudioEventKind.Kick, t - 0.02, 3f));   // 強いキック（検出は 20 ミリ秒遅れて届く）
        t += 0.01;
        float kick = meter.Level;
        T.Check(kick > 0.9f, $"a kick pushes the meter to the top ({kick:F2})");
        for (double end = t + 0.3; t < end; t += 0.01) x.Step(t);
        T.Check(meter.Level < rest + 0.08f, $"and it falls back within 0.3 s ({meter.Level:F2})");
        T.Check(meter.DotHeight > 0.85f, $"the peak dot stays up after the kick ({meter.DotHeight:F2})");
        x.Step(t, new AudioEvent(2, AudioEventKind.Kick, t - 0.02, 1.0f));   // しきい値より弱い
        T.Check(meter.Level < rest + 0.08f, "a weak kick (below 1.2×) does not bounce");
    }

    public static void TestRippleRadiusUsesEventTime()
    {
        var cfg = MusicCfg(gamma: false);
        var m = Music(MusicStyles.Ripple) with { MinBrightness = 0, TempoMode = TempoModes.Auto };
        var x = new Manual(Strip(81), cfg, m);
        double t = 0;
        for (; t < 2.1; t += 1 / 60.0) x.Step(t);
        // 2.0 秒に鳴ったキックが、検出の遅れで 2.1 秒に届く
        x.Step(t, new AudioEvent(1, AudioEventKind.Kick, 2.0, 2f));
        t += 1 / 60.0;
        int Brightest()
        {
            int best = 40;
            for (int i = 40; i < 81; i++) if (Max(x.R.LastLight, i) > Max(x.R.LastLight, best)) best = i;
            return best;
        }
        for (; t < 2.25; t += 1 / 60.0) x.Step(t);
        x.Step(2.25);
        double r1 = (Brightest() - 40) / 80.0;
        for (t = 2.25 + 1 / 60.0; t < 2.40; t += 1 / 60.0) x.Step(t);
        x.Step(2.40);
        double r2 = (Brightest() - 40) / 80.0;
        T.Near(r1, 1.2 * 0.25, 0.02, "Ripple: ring radius = 1.2/s × (t − event time) at t = 2.25 (event at 2.0, delivered at 2.1)");
        T.Near(r2, 1.2 * 0.40, 0.02, "Ripple: ring keeps growing with time (t = 2.40)");
        var ripple = (RippleEffect)x.R.CurrentEffect!;
        T.Check(ripple.Spawned == 1, "Ripple: one kick → one ring");
    }

    /// <summary>
    /// きらめきのハイハット: 「強さ」が控えめほど暗く、同じまとまりでは詰めて光らせない（ハイハットに過敏にならない）
    /// </summary>
    public static void TestSparkleHatResponse()
    {
        float Peak(string intensity, bool reduce)
        {
            var cfg = MusicCfg(gamma: false);
            var m = Music(MusicStyles.Sparkle) with { MinBrightness = 0, Intensity = intensity, ReduceFlashing = reduce };
            var x = new Manual(Strip(100), cfg, m, new RgbDeviceSettings { Mirror = false });
            x.In.Frame.MidE = 0;
            x.Step(1.0);
            float b = 0;
            for (int i = 0; i < 100; i++) b = Math.Max(b, Max(x.R.LastLight, i));
            x.Step(1.01, new AudioEvent(1, AudioEventKind.Hat, 1.01, 2f));
            float p = 0;
            for (int i = 0; i < 100; i++) p = Math.Max(p, Max(x.R.LastLight, i));
            return p - b;
        }
        float subtle = Peak(Intensities.Subtle, false), moderate = Peak(Intensities.Moderate, false), intense = Peak(Intensities.Intense, false);
        T.Check(subtle < moderate && moderate < intense, $"hat sparkle brighter with stronger intensity ({subtle:F2} < {moderate:F2} < {intense:F2})");
        T.Check(Peak(Intensities.Moderate, true) < moderate, "reduce flashing dims hat sparkles");

        // 16 分音符の刻み（0.06 秒おき）: 「ふつう」では 0.16 秒より詰めて光らせない
        var cfg2 = MusicCfg(gamma: false);
        var m2 = Music(MusicStyles.Sparkle) with { MinBrightness = 0, Intensity = Intensities.Moderate, ReduceFlashing = false };
        var y = new Manual(Strip(100), cfg2, m2, new RgbDeviceSettings { Mirror = false });
        y.In.Frame.MidE = 0;
        y.Step(1.0);
        y.Step(1.01, new AudioEvent(1, AudioEventKind.Hat, 1.01, 2f));
        var lit = Enumerable.Range(0, 100).Where(i => Max(y.R.LastLight, i) > 0.1f).ToHashSet();
        y.Step(1.07, new AudioEvent(2, AudioEventKind.Hat, 1.07, 2f));
        var lit2 = Enumerable.Range(0, 100).Where(i => Max(y.R.LastLight, i) > 0.1f && !lit.Contains(i)).Count();
        T.Check(lit2 == 0, $"a hat 0.06 s after the last one lights nothing new ({lit2} new LEDs)");
        y.Step(1.25, new AudioEvent(3, AudioEventKind.Hat, 1.25, 2f));
        int lit3 = Enumerable.Range(0, 100).Count(i => Max(y.R.LastLight, i) > 0.1f);
        T.Check(lit3 > 0, "a hat 0.24 s later lights again");
    }

    /// <summary>
    /// きらめき: キックを検出しなくても、テンポをつかんでいれば予測した拍で全体が明るくなる
    /// （キックの検出を待つと、検出にかかる時間だけ遅れて見えるため。ほかの光り方と同じ）
    /// </summary>
    public static void TestSparkleFollowsPredictedBeat()
    {
        var cfg = MusicCfg(gamma: false);
        var m = Music(MusicStyles.Sparkle) with { MinBrightness = 0 };
        var x = new Manual(Strip(60), cfg, m, new RgbDeviceSettings { Mirror = false });
        x.In.Frame.MidE = 0;
        x.In.Beat = new BeatState(0.5, 1.0, 0, 0.95, true, 0);
        float onBeat = 0, between = 0;
        for (double t = 1.6; t <= 4.0; t += 1 / 60.0)
        {
            x.Step(t);
            double ph = (t - 1.0) / 0.5 % 1;
            float level = x.R.LastLight.ToArray().Average(c => c.MaxChannel);
            if (ph < 0.06 || ph > 0.97) onBeat = Math.Max(onBeat, level);
            else if (ph is > 0.4 and < 0.6) between = Math.Max(between, level);
        }
        T.Check(onBeat > between * 1.5, $"Sparkle brightens on the predicted beat without a kick event (on beat {onBeat:F3}, between {between:F3})");
    }

    public static void TestSparkleAndChase()
    {
        // きらめき: ハイハットで粒が出て、0.08 秒で消える
        var cfg = MusicCfg(gamma: false);
        var m = Music(MusicStyles.Sparkle) with { MinBrightness = 0 };
        var x = new Manual(Strip(100), cfg, m, new RgbDeviceSettings { Mirror = false });
        x.In.Frame.MidE = 0;
        x.Step(1.0);
        float baseLevel = Max(x.R.LastLight, 0);
        x.Step(1.01, new AudioEvent(1, AudioEventKind.Hat, 1.01, 2f));
        int sparks = 0;
        for (int i = 0; i < 100; i++) if (Max(x.R.LastLight, i) > baseLevel + 0.2f) sparks++;
        T.Check(sparks >= 1 && sparks <= 3, $"Sparkle: a hat (s=2) at the default strength lights round(0.02·100·2·0.55) = 2 separate LEDs ({sparks} distinct)");
        x.Step(1.01 + 0.4);
        sparks = 0;
        for (int i = 0; i < 100; i++) if (Max(x.R.LastLight, i) > baseLevel + 0.05f) sparks++;
        T.Check(sparks == 0, "Sparkle: sparks fade out (τ 70 ms)");

        // テンポ追従: 偶数拍で帯が伸びる
        var c = new Rig(Strip(40), MusicCfg(gamma: false), Music(MusicStyles.Chase) with { MinBrightness = 0 }, new RgbDeviceSettings { Mirror = false });
        c.Synth.Conf = 1;
        double beat0 = c.Synth.FirstBeat + 4 * c.Synth.Period; // 偶数拍
        c.Run(0.3, beat0 + 0.25 * c.Synth.Period, 60);
        float early = 0;
        for (int i = 0; i < 40; i++) if (Max(c.R.LastLight, i) > 0.3f) early++;
        c.Run(beat0 + 0.25 * c.Synth.Period, beat0 + 0.8 * c.Synth.Period, 60);
        float late = 0;
        for (int i = 0; i < 40; i++) if (Max(c.R.LastLight, i) > 0.3f) late++;
        T.Check(early > 4 && late > early, $"Chase: the bar grows during an even beat ({early} → {late} LEDs)");
    }

    // ================= 重ね合わせ（音が止まったとき・点滅の制限・緊急時・同期テスト） =================

    public static void TestIdleCrossfadeToMood()
    {
        var rig = new Rig(Strip(30), MusicCfg(), Music(MusicStyles.BeatHue) with { IdleDelaySec = 2, IdleEffect = IdleEffects.Mood });
        rig.Synth.SilentFrom = 5;
        rig.Synth.SilentUntil = 12;
        rig.Run(0.3, 5, 60);
        T.Check(rig.R.IdleWeight == 0, "music playing → idle weight 0");
        rig.Run(5, 6.8, 60);
        T.Check(rig.R.IdleWeight == 0, "gate closed for less than IdleDelay → still music");
        rig.Run(6.8, 7.6, 60);
        T.Check(rig.R.IdleWeight > 0.2 && rig.R.IdleWeight < 0.9, $"after IdleDelay: ramping in over 1 s ({rig.R.IdleWeight:F2})");
        rig.Run(7.6, 9, 60);
        T.Check(rig.R.IdleWeight == 1, "idle weight reaches 1");
        // ムード（明るさ 0.4）が見えている
        var mood = rig.R.LastLight[15];
        T.Check(mood.MaxChannel > 0.1f, $"idle shows Mood at I = 0.4 (light {mood.MaxChannel:F2})");
        rig.Run(9, 12.0, 60);
        rig.Run(12.0, 12.45, 60);
        T.Check(rig.R.IdleWeight == 0, "sound returns → back to music within 0.4 s (0.15 s debounce + 0.25 s ramp)");

        var off = new Rig(Strip(30), MusicCfg(), Music(MusicStyles.Pulse) with { IdleDelaySec = 1, IdleEffect = IdleEffects.Off });
        off.Synth.SilentFrom = 0;
        off.Run(0.3, 3, 60);
        T.Check(off.Out.All(c => c == Rgb.Black), "IdleEffect Off → dark after the delay");
    }

    public static void TestFlashLimiter()
    {
        foreach (bool reduce in new[] { true, false })
        {
            var m = Music(MusicStyles.BeatHue) with { ReduceFlashing = reduce, TempoMode = TempoModes.KickOnly, FlashAmp = 1 };
            var x = new Manual(Strip(10), MusicCfg(gamma: false), m with { MinBrightness = 0 });
            x.In.Frame.BassE = 0;
            int id = 1;
            double nextKick = 1.0;
            var levels = new List<float>();
            for (double t = 1.0; t < 4.0; t += 1 / 120.0)
            {
                if (t >= nextKick)
                {
                    x.Step(t, new AudioEvent(id++, AudioEventKind.Kick, nextKick, 2f));
                    nextKick += 0.125; // 1 秒に 8 回
                }
                else x.Step(t);
                levels.Add(x.R.LastLight[0].MaxChannel);
            }
            // 明るさが大きく跳ね上がった回数（= 目に見える点滅）
            int big = 0;
            for (int i = 1; i < levels.Count; i++) if (levels[i] - levels[i - 1] > 0.2f) big++;
            var lim = x.R.Limiter;
            if (reduce)
            {
                T.Check(lim.Accepted <= 10 && lim.MinAcceptedGap >= FlashLimiter.MinGapSec - 1e-9,
                    $"FlashLimiter on: ≤ 3 accepted flashes per second (accepted {lim.Accepted} in 3 s, min gap {lim.MinAcceptedGap:F3} s)");
                T.Check(lim.Rejected > 10 && big <= 10, $"rejected flashes are weakened to 0.35·Fl ({lim.Rejected} rejected, {big} big jumps in 3 s)");
            }
            else T.Check(big >= 20, $"FlashLimiter off: every kick flashes ({big} big jumps in 3 s)");
        }
    }

    public static void TestEmergencyOverridesMusic()
    {
        var cfg = MusicCfg();
        var x = new Manual(Asrock(), cfg, Music(MusicStyles.Pulse));
        x.In.Frame.BassE = 1;
        x.Step(1.0);
        x.Step(1.1);
        x.In.Emergency = true;
        x.Step(1.2);
        T.Check(x.Out.All(c => c == new Rgb(0xFF, 0, 0)), "emergency: every LED bright red (same look as RgbEffects)");
        x.Step(1.7);
        T.Check(x.Out.All(c => c == new Rgb(0x20, 0, 0)), "emergency: blinks to dark red at 1 Hz");
        T.Check(RgbEffects.EmergencyColor(1.2) == new Rgb(0xFF, 0, 0) && RgbEffects.EmergencyColor(1.7) == new Rgb(0x20, 0, 0), "blink matches RgbEffects.EmergencyColor");
        x.In.Emergency = false;
        x.Step(2.0);
        x.Step(2.05);
        T.Check(x.Out.Any(c => c.R > 0x20 && c.R < 0xFF) || x.Out.Any(c => c.G > 0 || c.B > 0), "after emergency: crossfades back to music");
        cfg.BlinkOnEmergency = false;
        x.In.Emergency = true;
        x.Step(3.0);
        T.Check(!x.Out.All(c => c == new Rgb(0xFF, 0, 0)) && !x.Out.All(c => c == new Rgb(0x20, 0, 0)), "BlinkOnEmergency off → no red blink");
    }

    public static void TestSyncTestFlash()
    {
        var cfg = MusicCfg(gamma: false);
        var x = new Manual(Strip(8), cfg, Music(MusicStyles.Pulse));
        x.In.SyncTest = true;
        x.Step(0.9);
        T.Check(x.Out.All(c => c == Rgb.Black), "sync test: dark between clicks");
        x.Step(1.0, new AudioEvent(1, AudioEventKind.Click, 1.0, 1f));
        T.Check(x.Out.All(c => c == new Rgb(255, 255, 255)), "sync test: white at the click time");
        x.Step(1.08);
        T.Near(x.Out[0].R, 255 * Math.Exp(-1), 1.5, "sync test: flash decays with τ 80 ms");
        x.Step(1.5);
        T.Check(x.Out.All(c => c.R <= 1), "sync test: dark again");
    }

    public static void TestSlowDeviceAutoVariants()
    {
        var gpu = Gpu();
        T.Check(gpu.IsSlow && !Asrock().IsSlow, "GPU (I2C) is slow, ASRock USB is fast");
        (string Effect, EffectVariant Want)[] cases =
        {
            (MusicStyles.Pulse, EffectVariant.PulseSlow), (MusicStyles.BeatHue, EffectVariant.BeatHueSlow),
            (MusicStyles.Spectrum, EffectVariant.MixSlow), (MusicStyles.Energy, EffectVariant.MixSlow), (MusicStyles.Mix, EffectVariant.MixSlow),
            (MusicStyles.Meter, EffectVariant.PulseLoudSlow), (MusicStyles.Scroll, EffectVariant.BeatHueSlow),
            (MusicStyles.Ripple, EffectVariant.BeatHueSlow), (MusicStyles.Sparkle, EffectVariant.BeatHueSlow),
            (MusicStyles.Chase, EffectVariant.BeatHueSlow), (MusicStyles.Strobe, EffectVariant.BeatHueSlow), (MusicStyles.Mood, EffectVariant.Mood),
            (MusicStyles.Role, EffectVariant.RoleSlow),
        };
        bool all = true;
        foreach (var (effect, want) in cases)
        {
            var got = EffectCatalog.Resolve(effect, "Auto", isSlow: true, strobeAllowed: true);
            if (got != want) { all = false; T.Info($"{effect}: got {got}, want {want}"); }
        }
        T.Check(all, "slow device + Auto → slow variants (A12 table)");
        T.Check(EffectCatalog.Resolve(MusicStyles.Pulse, MusicStyles.Spectrum, true, false) == EffectVariant.Spectrum, "explicit per-device effect is kept on slow devices");
        T.Check(EffectCatalog.Resolve(MusicStyles.Pulse, MusicStyles.Strobe, true, true) == EffectVariant.BeatHue, "Strobe never on slow devices");
        T.Check(EffectCatalog.Resolve(MusicStyles.Strobe, "Auto", false, false) == EffectVariant.BeatHue, "Strobe not allowed (reduce flashing) → BeatHue");
        T.Check(EffectCatalog.Resolve(MusicStyles.Strobe, "Auto", false, true) == EffectVariant.Strobe, "Strobe allowed on fast devices");
        T.Check(EffectCatalog.Resolve(MusicStyles.Spectrum, "", false, false) == EffectVariant.Spectrum, "empty per-device effect = Auto");

        var rig = new Rig(gpu, MusicCfg(), Music(MusicStyles.Spectrum));
        rig.Run(0.3, 2, 8);
        // グラボも全体と同じ光り方（遅いデバイス用の版）。置き換えるのは ASRock の Radeon だけ
        T.Check(rig.R.ActiveEffect == MusicStyles.Mix && rig.R.ActiveVariant == EffectVariant.MixSlow, $"renderer on the GPU keeps the global effect ({rig.R.ActiveEffect})");
        var pg = new Rig(new RgbDevice(7, "ASRock Radeon RX 7900 XTX Phantom Gaming", 4, RgbDevice.TypeGpu, new[] { 1, 1, 1, 1 }), MusicCfg(), Music(MusicStyles.Spectrum));
        pg.Run(0.3, 2, 8);
        T.Check(pg.R.ActiveVariant == EffectVariant.BeatHueSlow, $"flicker-prone ASRock GPU uses the low-write variant ({pg.R.ActiveEffect})");
        var ramRig = new Rig(new RgbDevice(6, "ENE DRAM", 8, RgbDevice.TypeDram), MusicCfg(), Music(MusicStyles.Spectrum));
        ramRig.Run(0.3, 2, 8);
        T.Check(ramRig.R.ActiveVariant == EffectVariant.MixSlow, $"renderer on slow RAM shows the slow variant ({ramRig.R.ActiveEffect})");
        var fast = new Rig(Asrock(), MusicCfg(), Music(MusicStyles.Spectrum));
        fast.Run(0.3, 1, 60);
        T.Check(fast.R.ActiveEffect == MusicStyles.Spectrum, "renderer on the motherboard keeps Spectrum");
    }

    // ================= 仕上げ（OutputStage） =================

    public static void TestDitherAverage()
    {
        var stage = new OutputStage();
        var src = new LinColor[3];
        var dst = new Rgb[3];
        double[] targets = { 0.4, 1.3, 3.3, 17.75, 63.2 };
        foreach (double code in targets)
        {
            float v = (float)(code / 255);
            Array.Fill(src, new LinColor(v, v * 0.5f, 0));
            stage.Reset();
            double sumR = 0, sumG = 0;
            int frames = 1000;
            for (int i = 0; i < frames; i++)
            {
                stage.Write(src, dst, 1f, dither: true);
                sumR += dst[1].R;
                sumG += dst[1].G;
            }
            T.Near(sumR / frames, code, 0.01, $"dither: mean of {frames} frames = target {code} codes");
            T.Near(sumG / frames, code * 0.5, 0.01, $"dither: mean of half value = {code * 0.5} codes");
        }
        Array.Fill(src, new LinColor(100 / 255f, 0, 0));
        stage.Write(src, dst, 1f, true);
        var first = dst[0];
        stage.Write(src, dst, 1f, true);
        T.Check(first.R == 100 && dst[0].R == 100 && dst[0].B == 0, "no dithering above code 64; black stays black");
        Array.Fill(src, new LinColor(1.3f / 255, 0, 0));
        stage.Write(src, dst, 1f, dither: false);
        T.Check(dst[0].R == 1, "without dithering: plain rounding");

        // 遅いデバイスはディザリングしない（毎回同じ値）
        var slow = new Manual(Strip(4, slow: true), MusicCfg(), Music(MusicStyles.Mood) with { MinBrightness = 0.05 });
        var values = new HashSet<byte>();
        for (int i = 0; i < 30; i++) { slow.Step(1 + i * 1e-4); values.Add(slow.Out[0].B); }
        T.Check(values.Count == 1, "slow devices: no temporal dithering");
    }

    public static void TestNonMusicModesKeepTheirLook()
    {
        string[] modes = { RgbModes.Static, RgbModes.RainbowStatic, RgbModes.Temperature, RgbModes.Rainbow, RgbModes.Breathing };
        foreach (string mode in modes)
        {
            var cfg = new RgbConfig { Mode = mode, Brightness = 80, Gamma = true, StaticColor = "#FF8000", Speed = 1 };
            var dev = new RgbDevice(5, "Slow", 250, 4, AsrockZones, "I2C");   // 遅い（I2C）がグラボではない: グラボは虹・呼吸を止める
            var r = new DeviceRenderer(dev);
            var input = new RenderInput { Cfg = cfg, T = 3.3, TEval = 3.3, Temperature = 62 };
            var output = new Rgb[250];
            r.Render(input, output);
            var legacy = RgbEffects.Frame(cfg, 3.3, 250, 62, false, AsrockZones);
            T.Check(output.SequenceEqual(legacy), $"{mode}: renderer output = RgbEffects.Frame (same look)");
        }
        // モードを変えると 0.4 秒でクロスフェード
        var c2 = new RgbConfig { Mode = RgbModes.Static, StaticColor = "#FF0000", Gamma = false };
        var rr = new DeviceRenderer(Strip(4));
        var inp = new RenderInput { Cfg = c2 };
        var o = new Rgb[4];
        inp.T = 1; rr.Render(inp, o);
        c2.Mode = RgbModes.Temperature;
        inp.Temperature = 20;
        inp.T = 1.2; rr.Render(inp, o);
        var first = o[0];
        inp.T = 1.4; rr.Render(inp, o);
        var mid = o[0];
        inp.T = 1.7; rr.Render(inp, o);
        T.Check(first == new Rgb(0xFF, 0, 0) && mid.R > 0x40 && mid.R < 0xF0 && mid.B > 0x20 && o[0] == new Rgb(0x22, 0xC7, 0xFF),
            $"mode change crossfades over 0.4 s (start {first.Hex}, mid {mid.Hex}, end {o[0].Hex})");
        // 温度モードは温度をならす（L20: τ 1.5 秒）。20 → 85 °C に跳んでも、0.8 秒後はまだ 47 °C あたりの色
        inp.Temperature = 85;
        inp.T = 1.8; rr.Render(inp, o);
        inp.T = 2.5; rr.Render(inp, o);
        var smooth = o[0];
        var (er, eg, eb) = RgbSync.ColorFor(20 + 65 * (1 - Math.Exp(-0.8 / DeviceRenderer.TemperatureTauSec)), 40, 85);
        T.Check(Math.Abs(smooth.R - er) <= 2 && Math.Abs(smooth.G - eg) <= 2 && Math.Abs(smooth.B - eb) <= 2,
            $"temperature step is glided (τ {DeviceRenderer.TemperatureTauSec} s): {smooth.Hex}");
        inp.T = 12; rr.Render(inp, o);
        T.Check(o[0] == new Rgb(0xFF, 0x3B, 0x30), "temperature reaches the new colour");
    }

    // ================= 1 秒間のコマ数に左右されない（A15 の 9） =================

    public static void TestFrameRateIndependence()
    {
        // 光り方そのものの計算を比べる。立ち上がりの段つきを止める仕上げ（AttackHold）は、コマが細かいほど働く
        // （8 fps では 1 コマが保つ時間より長いので何もしない）ので、ここでは外す（AttackHold は TestAttackHold* で確かめる）
        double savedHold = AttackHold.HoldSec;
        AttackHold.HoldSec = 0;
        try { FrameRateIndependence(); }
        finally { AttackHold.HoldSec = savedHold; }
    }

    private static void FrameRateIndependence()
    {
        // 同じ 10 秒の合成音楽を 144 / 60 / 30 / 8 fps で描き、LED ごとの平均の明るさ（R+G+B）を比べる。
        // コマの位置がたまたまキックの頂点に当たるかどうかの「偶然の差」と、コマ数で明るさが変わる「偏り」を分けるため、
        // 描き始めを 1 コマの 1/6 ずつずらして 6 回描き、その平均で比べる（偶然の差は平均すると小さくなるが、偏りは残る）
        double[] fpsList = { 144, 60, 30, 8 };
        string[] effects =
        {
            MusicStyles.Pulse, MusicStyles.BeatHue, MusicStyles.Mix, MusicStyles.Mood, MusicStyles.Spectrum, MusicStyles.Energy,
            MusicStyles.Meter, MusicStyles.Scroll, MusicStyles.Ripple, MusicStyles.Chase, MusicStyles.Sparkle,
            MusicStyles.Pulse + " unlocked", MusicStyles.BeatHue + " unlocked",
        };
        const int Offsets = 6;
        foreach (string name in effects)
        {
            // "unlocked" = テンポをつかんでいない（ビートの光はキックの出来事だけから。出来事は検出の遅れの分だけ遅れて届く）
            bool unlocked = name.EndsWith(" unlocked") || name == MusicStyles.Ripple;
            string effect = name.Split(' ')[0];
            double[]? reference = null;
            double refMean = 0;
            string line = "";
            bool fastOk = true, slowOk = true;
            foreach (double fps in fpsList)
            {
                var sum = new double[250];
                int frames = (int)Math.Round(10 * fps);
                for (int o = 0; o < Offsets; o++)
                {
                    var rig = new Rig(Asrock(), MusicCfg(), Music(effect));
                    if (unlocked) { rig.Synth.Locked = false; rig.Synth.Conf = 0; }
                    double shift = o / (Offsets * fps);
                    rig.Run(0.3 + shift, 1.0 + shift, fps);
                    for (int i = 0; i < frames; i++)
                    {
                        rig.Step(1.0 + shift + i / fps);
                        for (int j = 0; j < 250; j++) sum[j] += rig.Out[j].R + rig.Out[j].G + rig.Out[j].B;
                    }
                }
                for (int j = 0; j < 250; j++) sum[j] /= frames * Offsets;
                double mean = sum.Average();
                if (reference is null) { reference = sum; refMean = mean; continue; }
                // LED ごとの差（暗い LED は、デバイス全体の平均の 1 割を下限にして割る）
                double worst = 0;
                int worstJ = 0;
                for (int j = 0; j < 250; j++)
                {
                    double e = Math.Abs(sum[j] - reference[j]) / Math.Max(reference[j], 0.1 * refMean);
                    if (e > worst) { worst = e; worstJ = j; }
                }
                double dev = Math.Abs(mean - refMean) / refMean;
                line += $"  {fps}fps: LED {worst * 100:F1}% (#{worstJ} {sum[worstJ]:F0}/{reference[worstJ]:F0}) all {dev * 100:F1}%";
                if (fps >= 30 && worst > 0.03) fastOk = false;
                if (fps < 30 && worst > 0.05) slowOk = false;
            }
            T.Info($"{name,-16}{line}");
            T.Check(fastOk, $"{name}: per-LED mean intensity at 30/60 fps within 3 % of 144 fps");
            T.Check(slowOk, $"{name}: per-LED mean intensity at 8 fps within 5 % of 144 fps");
        }

        // ビートの光は、ビートの頭の直後のコマで立ち上がる（1 コマ以上遅れない）
        foreach (double fps in fpsList)
        {
            var rig = new Rig(Asrock(), MusicCfg(), Music(MusicStyles.BeatHue));
            double p = rig.Synth.Period;
            rig.Run(0.3, 1.0, fps);
            int beats = 0, onTime = 0;
            float prev = rig.R.Context.Fl;
            double tPrev = 1.0 - 1 / fps;
            for (int i = 0; i < (int)(6 * fps); i++)
            {
                double t = 1.0 + i / fps;
                rig.Step(t);
                float fl = rig.R.Context.Fl;
                double k = Math.Floor((t - rig.Synth.FirstBeat) / p);
                double tb = rig.Synth.FirstBeat + k * p;
                if (tb > tPrev && tb <= t)
                {
                    beats++;
                    if (fl > prev + 0.1f) onTime++;
                }
                prev = fl;
                tPrev = t;
            }
            T.Check(beats > 5 && onTime == beats, $"{fps} fps: flash rises on the first frame after every beat ({onTime}/{beats})");
        }
    }

    // ================= 毎コマ new しない =================

    public static void TestNoAllocationsPerFrame()
    {
        var setups = new List<(string Name, Rig Rig)>();
        foreach (var effect in MusicStyles.All)
        {
            var m = Music(effect) with { StrobeAllowed = true, ReduceFlashing = effect != MusicStyles.Strobe };
            setups.Add((effect, new Rig(Asrock(), MusicCfg(), m)));
            setups.Add((effect + " (GPU)", new Rig(Gpu(), MusicCfg(), m)));
        }
        var idle = new Rig(Asrock(), MusicCfg(), Music(MusicStyles.Spectrum) with { IdleDelaySec = 1 });
        idle.Synth.SilentFrom = 1.5;
        setups.Add(("idle Mood", idle));
        setups.Add(("Rainbow mode", new Rig(Asrock(), new RgbConfig { Mode = RgbModes.Rainbow }, Music(MusicStyles.Pulse))));
        setups.Add(("Breathing mode", new Rig(Asrock(), new RgbConfig { Mode = RgbModes.Breathing }, Music(MusicStyles.Pulse))));
        setups.Add(("Temperature mode", new Rig(Asrock(), new RgbConfig { Mode = RgbModes.Temperature }, Music(MusicStyles.Pulse))));
        var sync = new Rig(Asrock(), MusicCfg(), Music(MusicStyles.Pulse));
        sync.In.SyncTest = true;
        setups.Add(("sync test", sync));

        var bad = new List<string>();
        foreach (var (name, rig) in setups)
        {
            rig.In.Temperature = 55;
            rig.Run(0.3, 4.0, 60);    // 準備（配列・パレット・光り方を作る）
            long before = GC.GetAllocatedBytesForCurrentThread();
            rig.Run(4.0, 9.0, 60);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (bytes != 0) bad.Add($"{name}: {bytes} bytes in 300 frames");
        }
        foreach (var b in bad) T.Info(b);
        T.Check(bad.Count == 0, $"no allocations per frame after warm-up ({setups.Count} setups × 300 frames)");

        // 描画の時間（A0: 300 LED で 1 コマ 0.5 ミリ秒未満）。ASRock（250 LED）で、いちばん重い光り方を測る
        var sw = new System.Diagnostics.Stopwatch();
        double worstUs = 0;
        string worstName = "";
        foreach (var effect in MusicStyles.All)
        {
            var m = Music(effect) with { StrobeAllowed = true, ReduceFlashing = effect != MusicStyles.Strobe, CrossfadeMs = 600 };
            var rig = new Rig(new RgbDevice(0, "300", 300, 4, new[] { 60, 120, 120 }, "HID"), MusicCfg(), m);
            rig.Run(0.3, 2.0, 60);
            rig.UseRing = true;
            sw.Restart();
            rig.Run(2.0, 7.0, 60);
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000 / 300;
            if (us > worstUs) { worstUs = us; worstName = effect; }
        }
        T.Info($"slowest effect: {worstName} {worstUs:F0} µs per frame for 300 LEDs (including the test rig)");
        T.Check(worstUs < 500, "render time per frame for 300 LEDs < 0.5 ms");
    }

    public static void TestSharedColorKeepsDevicesInStep()
    {
        var shared = new SharedColor();
        var a = new ColorFade();
        var b = new ColorFade();
        // 速いデバイスが 1.000 秒のビートで進め、遅いデバイスは 0.02 秒遅れて同じビートを見る
        shared.Step(1.000, 0.25);
        shared.Step(1.020, 0.25);
        T.Check(Math.Abs(shared.Target - 0.25) < 1e-9, "same beat seen by two devices steps once");
        a.SyncTo(shared, 1.0, 0.1);
        b.SyncTo(shared, 1.02, 0.3);
        shared.Step(1.5, 0.25);
        a.SyncTo(shared, 1.5, 0.1);
        T.Check(Math.Abs(a.Target - 0.5) < 1e-9 && Math.Abs(b.Target - 0.25) < 1e-9, "lagging device is behind until it syncs");
        b.SyncTo(shared, 1.55, 0.3);
        T.Check(Math.Abs(a.Target - b.Target) < 1e-9, "after syncing, every device shows the same colour");
    }
}

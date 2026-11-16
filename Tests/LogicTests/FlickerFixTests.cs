// ==================================================
// FlickerFixTests.cs - ちらつき止めの部品ごとのテスト (LedSteady・音楽連動でディザリングしない・ストロボの光の長さ・
//                      呼吸のいちばん暗いところ・きらめきの粒の間隔・スペクトラムのならし)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class FlickerFixTests
{
    private const float Dt = 1 / 60f;

    /// <summary>1 つの LED に値を順に入れて、LedSteady を通した明るさ（いちばん強い成分）を返す</summary>
    private sealed class One
    {
        private readonly LedSteady _s = new();
        private readonly LinColor[] _px = new LinColor[1];

        public float Step(float v, float dt = Dt) => StepColor(new LinColor(v, v * 0.5f, 0), dt).MaxChannel;

        public LinColor StepColor(LinColor c, float dt = Dt)
        {
            _px[0] = c;
            _s.Apply(_px, 1, dt);
            return _px[0];
        }
    }

    /// <summary>
    /// LedSteady: 明るくなった直後・暗くなり始めた直後の小さな上下では向きを変えない（上がって下がってまた上がる、をしない）
    /// </summary>
    public static void TestLedSteadyIgnoresShortDips()
    {
        var s = new One();
        s.Step(0.2f);
        float peak = s.Step(0.6f);
        float dip = s.Step(0.57f);
        float back = s.Step(0.6f);
        T.Check(Math.Abs(peak - 0.6f) < 1e-5 && Math.Abs(dip - 0.6f) < 1e-5 && Math.Abs(back - 0.6f) < 1e-5,
            $"a small dip right after a rise is held ({peak:F3} → {dip:F3} → {back:F3})");
        // 暗くなっている途中の小さな上がりも保つ（6% + 2.5 段より小さい）
        var d = new One();
        d.Step(0.8f);
        for (int i = 0; i < 10; i++) d.Step(0.8f);
        float a = d.Step(0.5f);
        for (int i = 0; i < 3; i++) a = d.Step(0.5f);
        float bump = d.Step(a + 0.02f);
        T.Check(bump <= a + 1e-5, $"a small bump while falling does not turn the light back up ({a:F3} → {bump:F3})");
    }

    /// <summary>
    /// LedSteady: 明るくなってから MinFlipSec（75 ms）は暗くし始めない。暗くなる速さは時定数 ReleaseTauSec（80 ms）まで
    /// （1 コマで消えない。6.15.8 までは 30 ms で、保ったあとに光が四角く切れて見えた）。そのあとは入力どおりに暗くなる
    /// </summary>
    public static void TestLedSteadyHoldsThenReleasesGently()
    {
        var s = new One();
        s.Step(0.1f);
        s.Step(0.1f);
        float on = s.Step(0.8f);
        var held = new List<float>();
        for (int i = 0; i < 4; i++) held.Add(s.Step(0.05f));   // 17・33・50・67 ms
        T.Check(Math.Abs(on - 0.8f) < 1e-5 && held.All(v => Math.Abs(v - 0.8f) < 1e-5),
            $"the light stays on for at least 75 ms after it rose ({string.Join(", ", held.Select(v => v.ToString("F2")))})");
        float f1 = s.Step(0.05f), f2 = s.Step(0.05f);
        float k = EffectContext.Decay(Dt, LedSteady.ReleaseTauSec);
        T.Check(Math.Abs(f1 - 0.8f * k) < 1e-4 && Math.Abs(f2 - 0.8f * k * k) < 1e-4,
            $"then it fades with τ {LedSteady.ReleaseTauSec * 1000:F0} ms instead of dropping in one frame ({f1:F3}, {f2:F3})");
        float end = 0;
        for (int i = 0; i < 20; i++) end = s.Step(0.05f);
        T.Check(Math.Abs(end - 0.05f) < 1e-5, $"and reaches the input ({end:F3})");
    }

    /// <summary>LedSteady: 新しい音で 1 コマで大きく明るくなったら、暗くし始めた直後でも待たずに明るくする（光り始めを遅らせない）</summary>
    public static void TestLedSteadyLetsNewHitsThroughAtOnce()
    {
        var s = new One();
        s.Step(0.2f);
        s.Step(0.9f);
        for (int i = 0; i < 6; i++) s.Step(0.3f);   // 暗くなり始めた
        float hit = s.Step(0.85f);
        T.Check(Math.Abs(hit - 0.85f) < 1e-5, $"a new hit right after the fall began goes through at once ({hit:F3})");
        // 待たせた小さな上がりは、少しずつ追いかける（保っていた分を 1 コマで跳ばない）
        var g = new One();
        g.Step(0.5f);
        for (int i = 0; i < 10; i++) g.Step(0.5f);
        for (int i = 0; i < 8; i++) g.Step(0.40f);   // 暗くなっていく
        float lo = g.Step(0.40f);
        // 1 コマで少しだけ（BigRise より小さく）上がり、そのまま留まる
        float r1 = g.Step(lo + 0.04f), r2 = g.Step(lo + 0.04f);
        T.Check(r1 > lo && r1 < lo + 0.04f && r2 > r1, $"a small late rise glides up ({lo:F3} → {r1:F3} → {r2:F3})");
    }

    /// <summary>LedSteady: ゆっくり明るくなる・暗くなる光は、そのまま通す（段にしない）。色は今の色のまま、明るさだけを直す</summary>
    public static void TestLedSteadyPassesSlowFadesAndKeepsColour()
    {
        var up = new One();
        var down = new One();
        bool same = true;
        for (int i = 0; i <= 120; i++) { float v = 0.1f + 0.7f * i / 120; same &= Math.Abs(up.Step(v) - v) < 1e-5; }
        for (int i = 120; i >= 0; i--) { float v = 0.1f + 0.7f * i / 120; same &= Math.Abs(down.Step(v) - v) < 1e-5; }
        T.Check(same, "a slow 2 s ramp up, and a slow ramp down, pass unchanged");
        // 山の頂上で向きを変えるときは、HystRel 分だけ待ってから下がり始める（行ったり来たりしない）
        float prev = up.Step(0.8f), lastOut = prev;
        bool monotone = true;
        for (int i = 1; i <= 60; i++) { float o = up.Step(0.8f - 0.7f * i / 120); monotone &= o <= prev + 1e-6f; prev = o; lastOut = o; }
        T.Check(monotone && Math.Abs(lastOut - 0.45f) < 0.06f, $"at the top it turns once and then follows the fall ({lastOut:F3})");

        // 色が変わった直後に明るさを保つコマ: 6.15.8 までは「新しい色を保った明るさで出す」ことを確かめていたが、それが
        // 色替えの途中のちかっとした光そのもの（明るい青 → 暗い緑の下地で、緑が青の明るさで光る）だった。
        // いまは新しい色を今の明るさで出し、足りない分だけを前に出した光の色で足す（輝度は前後の明るいほうを超えない）
        var c = new One();
        c.StepColor(new LinColor(0.2f, 0, 0));
        c.StepColor(new LinColor(0.9f, 0, 0));
        var held = c.StepColor(new LinColor(0, 0, 0.88f));
        T.Check(Math.Abs(held.B - 0.88f) < 1e-5 && Math.Abs(held.R - 0.02f) < 1e-4,
            $"a held frame shows the new colour at its own brightness, the held rest in the colour shown before ({held.R:F3}, {held.B:F3})");
        var black = c.StepColor(LinColor.Black);
        T.Check(black.B > 0.5f && black.R < 0.03f, $"if the effect goes black during the hold, the last colour is kept ({black.R:F2}, {black.B:F2})");
        // 暗い色への色替え: 明るい青のあと暗い緑の下地。緑を青の明るさで出さない（輝度が前後のどちらより高くならない）
        var g = new One();
        g.StepColor(new LinColor(0, 0, 0.3f));
        var blue = g.StepColor(new LinColor(0, 0, 1f));
        static float Y(LinColor x) => 0.2126f * x.R + 0.7152f * x.G + 0.0722f * x.B;
        float worst = 0;
        for (int i = 0; i < 6; i++) worst = Math.Max(worst, Y(g.StepColor(new LinColor(0, 0.1f, 0))));
        T.Check(worst <= Math.Max(Y(blue), Y(new LinColor(0, 0.1f, 0))) * 1.001f + 1e-6f,
            $"bright blue then a dim green background: no frame is brighter than both (worst Y {worst:F3}, blue {Y(blue):F3}, green {Y(new LinColor(0, 0.1f, 0)):F3})");

        // 外した LED（ゾーンだけストロボ）はそのまま。光が動く光り方の LED は、明るくなった直後でも暗くし始める（尾を引かない）。
        // 暗くなる速さの上限はどちらにも使う
        var st = new LedSteady();
        var px = new[] { new LinColor(0.1f, 0, 0), new LinColor(0.1f, 0, 0), new LinColor(0.1f, 0, 0) };
        var kinds = new[] { LedSteady.Kind.Full, LedSteady.Kind.Skip, LedSteady.Kind.Moving };
        st.Apply(px, 3, Dt, kinds);
        px[0] = px[1] = px[2] = new LinColor(0.9f, 0, 0);
        st.Apply(px, 3, Dt, kinds);
        px[0] = px[1] = px[2] = new LinColor(0.1f, 0, 0);
        st.Apply(px, 3, Dt, kinds);
        float k = EffectContext.Decay(Dt, LedSteady.MovingReleaseTauSec);
        T.Check(Math.Abs(px[0].R - 0.9f) < 1e-5 && Math.Abs(px[1].R - 0.1f) < 1e-6 && Math.Abs(px[2].R - 0.9f * k) < 1e-4,
            $"full: held ({px[0].R:F2}); skipped: unchanged ({px[1].R:F2}); moving light: starts fading at once ({px[2].R:F2})");
        T.Check(!LedSteady.Applies(EffectVariant.Strobe) && !LedSteady.Applies(EffectVariant.Mood) && LedSteady.KindOf(EffectVariant.Spectrum) == LedSteady.Kind.Full
            && LedSteady.KindOf(EffectVariant.Ripple) == LedSteady.Kind.Moving && LedSteady.KindOf(EffectVariant.Scroll) == LedSteady.Kind.Moving,
            "used for every music style except Strobe and Mood; Ripple and Scroll as moving light");
    }

    /// <summary>
    /// 音楽連動: 暗い LED を 2・3・2・3 と行き来させない（ディザリングをやめて、前に送った値を保つ）。
    /// ゆっくり暗くなる光は、段を 1 つずつ下がる（行き来しない）
    /// </summary>
    public static void TestMusicModeHasNoTemporalDither()
    {
        var dev = new RgbDevice(3, "Strip", 4, 4, null, "HID: x");
        var r = new DeviceRenderer(dev);
        var input = new RenderInput
        {
            Cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = true, StaticColor = "#6A00FF" },
            Music = ResolvedMusic.Default with { Effect = MusicStyles.Pulse, MinBrightness = 0, TempoMode = TempoModes.Off },
            Device = new RgbDeviceSettings { Name = "Strip" },
        };
        input.Frame.GateOpen = true;
        input.Frame.Activity = 1;
        var o = new Rgb[4];
        int reversals = 0, prev = -1, dir = 0;
        double last = double.NaN;
        for (int i = 0; i < 300; i++)
        {
            double t = 1 + i / 60.0;
            // 低音の強さがゆっくり下がる（暗いところで端数が出続ける）
            input.Frame.BassE = (float)Math.Max(0, 0.35 - 0.35 * i / 300);
            input.Frame.LoudVU = input.Frame.BassE;
            input.T = input.TEval = input.Frame.T = t;
            input.TPrevEval = double.IsNaN(last) ? t : last;
            input.Dt = double.IsNaN(last) ? 0 : t - last;
            r.Render(input, o);
            last = t;
            int v = Math.Max(o[0].R, Math.Max(o[0].G, o[0].B));
            if (prev >= 0 && v != prev)
            {
                int d = Math.Sign(v - prev);
                if (dir != 0 && d != dir) reversals++;
                dir = d;
            }
            prev = v;
        }
        T.Check(reversals == 0, $"a slow fade in music mode steps down without going back up ({reversals} reversals)");
    }

    /// <summary>
    /// ストロボ: 全開のコマの数が、光の始まりとコマの位置の関係で変わらない（60 コマ／秒なら、どの光も 3 コマ）。
    /// キックを見つけたのが遅れて届いても同じ長さ
    /// </summary>
    public static void TestStrobeFlashLengthDoesNotDependOnFramePhase()
    {
        var counts = new List<int>();
        foreach (double phase in new[] { 0.0, 0.003, 0.006, 0.009, 0.012, 0.015 })
        {
            var dev = new RgbDevice(3, "Strip", 8, 4, null, "HID: x");
            var r = new DeviceRenderer(dev);
            var input = new RenderInput
            {
                Cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = false },
                Music = ResolvedMusic.Default with { Effect = MusicStyles.Strobe, StrobeAllowed = true, ReduceFlashing = false, TempoMode = TempoModes.Off, MinBrightness = 0 },
                Device = new RgbDeviceSettings { Name = "Strip" },
            };
            input.Frame.GateOpen = true;
            input.Frame.Activity = 1;
            var o = new Rgb[8];
            var kicks = Enumerable.Range(0, 6).Select(k => 2.0 + k * 0.6 + phase).ToList();
            double last = double.NaN;
            int lit = 0, flashes = 0;
            bool wasLit = false;
            long id = 0;
            for (int i = 0; i < 360; i++)
            {
                double t = 1 + i / 60.0;
                input.Events.Clear();
                // キックは鳴ってから 20 ms 後に見つかる
                foreach (double kt in kicks)
                    if (kt + 0.02 <= t && kt + 0.02 > (double.IsNaN(last) ? double.NegativeInfinity : last))
                        input.Events.Add(new AudioEvent(++id, AudioEventKind.Kick, kt, 3f));
                input.T = input.TEval = input.Frame.T = t;
                input.TPrevEval = double.IsNaN(last) ? t : last;
                input.Dt = double.IsNaN(last) ? 0 : t - last;
                r.Render(input, o);
                last = t;
                bool on = r.LastLight[0].MaxChannel >= 0.95f;
                if (on) lit++;
                if (on && !wasLit) flashes++;
                if (!on && wasLit) { counts.Add(lit); lit = 0; }
                wasLit = on;
            }
            T.Check(flashes == kicks.Count, $"phase {phase * 1000:F0} ms: every kick flashes ({flashes}/{kicks.Count})");
        }
        T.Check(counts.Count > 0 && counts.All(c => c == 3), $"every flash is fully on for 3 frames at 60 fps, whatever the frame phase ({string.Join(" ", counts)})");
    }

    /// <summary>
    /// 呼吸: 全体の明るさを下げても、いちばん暗いところで消えて点く「まばたき」をしない（6.15.7: 30% で 0.40 回／秒）。
    /// 明るさ 100% の見た目（いちばん暗いところで 1 割）は変えない
    /// </summary>
    public static void TestBreathingDoesNotBlinkWhenDim()
    {
        var strip = new RgbDevice(0, "Strip", 60, 4, null, "HID: x");
        foreach (double bri in new[] { 100.0, 30.0, 10.0, 3.0 })
        foreach (string color in new[] { "#0060FF", "#FF8000", "#202020" })
        {
            var cfg = new RgbConfig { Mode = RgbModes.Breathing, Brightness = bri, Gamma = true, StaticColor = color, Speed = 1 };
            var (j, b, tg) = FlickerDiagTests.MeasureAll(strip, cfg);
            T.Check(b == 0 && j < 0.05 && tg < 0.05, $"Breathing {color} at {bri}%: no blink at the dark end (blink {b:F2}/s, jitter {j:F2}/s, toggles {tg:F2}/s)");
        }
        T.Near(RgbEffects.BreathFloor(Rgb.Parse("#0060FF"), 100, true), 0.1, 1e-9, "at 100% the darkest point stays 10% of the colour");
        double lo = RgbEffects.BreathFloor(Rgb.Parse("#0060FF"), 30, true);
        double code = OutputStage.Pow22((float)lo) * 0.3 * 255;
        T.Check(lo > 0.1 && code >= RgbEffects.BreathMinCode - 0.01, $"at 30% the darkest point is raised so the brightest channel stays lit ({lo:F3} → {code:F2} codes)");
    }

    /// <summary>
    /// きらめき: ハイハットが 1 秒に何回鳴っても、同じ LED の粒は 1 秒に 3 回まで（0.333 秒あける）。
    /// 粒の数そのものは減らしすぎない（別の LED を選ぶ）。小さいゾーン（全体が光る）も同じ
    /// </summary>
    public static void TestSparkleParticlesHaveAPerLedBudget()
    {
        foreach (int n in new[] { 24, 4 })
        {
            var dev = new RgbDevice(3, "Strip", n, 4, null, "HID: x");
            var r = new DeviceRenderer(dev);
            var input = new RenderInput
            {
                Cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = false },
                Music = ResolvedMusic.Default with { Effect = MusicStyles.Sparkle, Intensity = Intensities.Intense, ReduceFlashing = false, TempoMode = TempoModes.Off, MinBrightness = 0 },
                Device = new RgbDeviceSettings { Name = "Strip", Mirror = false },
            };
            input.Frame.GateOpen = true;
            input.Frame.Activity = 1;
            var o = new Rgb[n];
            var prev = new float[n];
            var lastRise = Enumerable.Repeat(double.NegativeInfinity, n).ToArray();
            double minGap = double.PositiveInfinity, last = double.NaN;
            int rises = 0;
            long id = 0;
            for (int i = 0; i < 300; i++)
            {
                double t = 1 + i / 60.0;
                input.Events.Clear();
                // 16 分より細かいハイハット（0.067 秒おき）
                if (i % 4 == 0) input.Events.Add(new AudioEvent(++id, AudioEventKind.Hat, t, 2.5f));
                input.T = input.TEval = input.Frame.T = t;
                input.TPrevEval = double.IsNaN(last) ? t : last;
                input.Dt = double.IsNaN(last) ? 0 : t - last;
                r.Render(input, o);
                last = t;
                for (int j = 0; j < n; j++)
                {
                    float v = r.LastLight[j].MaxChannel;
                    if (v - prev[j] > 0.1f)
                    {
                        if (!double.IsInfinity(lastRise[j])) minGap = Math.Min(minGap, t - lastRise[j]);
                        lastRise[j] = t;
                        rises++;
                    }
                    prev[j] = v;
                }
            }
            T.Check(minGap >= SparkleEffect.ParticleGapSec - 0.5 / 60, $"{n} LEDs: no LED sparkles again within {SparkleEffect.ParticleGapSec:F3} s (shortest {minGap:F3} s)");
            T.Check(rises >= (n < EffectContext.SmallZoneLeds ? 10 : 30), $"{n} LEDs: still sparkles ({rises} sparks in 5 s)");
        }
    }

    /// <summary>
    /// スペクトラム: 16 分のハイハット（0.117 秒おき）で高音側の LED が上下しない（保つ間に次の音が来るので明るいまま）。
    /// 音が止まったら 1 秒以内に暗くなる
    /// </summary>
    public static void TestSpectrumIsSmoothedOverTime()
    {
        var dev = new RgbDevice(3, "Strip", 24, 4, null, "HID: x");
        var r = new DeviceRenderer(dev);
        var input = new RenderInput
        {
            Cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = false },
            Music = ResolvedMusic.Default with { Effect = MusicStyles.Spectrum, TempoMode = TempoModes.Off, MinBrightness = 0 },
            Device = new RgbDeviceSettings { Name = "Strip", Mirror = false },
        };
        input.Frame.GateOpen = true;
        input.Frame.Activity = 1;
        var o = new Rgb[24];
        double last = double.NaN;
        var during = new List<float>();
        float after = 0;
        for (int i = 0; i < 240; i++)
        {
            double t = 1 + i / 60.0;
            bool playing = t < 3;
            // 高音の帯が 0.117 秒おきに一瞬だけ強くなる（7 コマに 1 回）
            float p = playing && i % 7 == 0 ? 0.8f : 0f;
            input.Frame.Punch[8] = input.Frame.Punch[9] = p;
            input.T = input.TEval = input.Frame.T = t;
            input.TPrevEval = double.IsNaN(last) ? t : last;
            input.Dt = double.IsNaN(last) ? 0 : t - last;
            r.Render(input, o);
            last = t;
            float v = r.LastLight[23].MaxChannel;
            if (t > 1.5 && playing) during.Add(v);
            if (t > 3.9) after = v;
        }
        float hi = during.Max(), lowest = during.Min();
        T.Check(lowest >= 0.9f * hi, $"treble LED stays lit between 16th-note hats ({lowest:F2} .. {hi:F2})");
        T.Check(after < 0.25f * hi, $"and goes dark within a second after the sound stops ({after:F2})");
    }
}

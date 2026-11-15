// ==================================================
// SceneColorTests.cs - 楽器の AI の場面の色（ScenePalettes・PaletteView）: オフなら今までと同じ出力・色がゆっくり変わる・
//                      場面がばたつかない・点滅の制限は同じ・使わない光り方とパレット・ちかちかしやすいグラボ・遅れの違うデバイス・
//                      音が止まったときの光り方・設定の読み込み
//   色の確かめは合成の曲（SignalGen の音の多い曲）と、手で入れた場面で行う（楽器の AI が実際の曲で何と答えるかは確かめていない）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class SceneColorTests
{
    private const double Fps = 60;
    private const double T0 = 1000.0;

    /// <summary>
    /// 場面の移り変わりで、1 コマ（1/60 秒）に変わってよい色の差（OKLab の ΔE）。2.5 秒の smoothstep の傾きの最大（0.6/秒）で、
    /// いちばん離れた組み合わせ（レインボーの青 → 金管の金色）が 1 コマ 0.013。設計の目安 0.012 を少し超えるので 0.015
    /// （OKLab で見て分かる差は 0.02 くらい。点滅ではなく、ゆっくりした色の移り変わり）
    /// </summary>
    private const double MaxFadeDe = 0.015;

    /// <summary>コマごとに呼ぶ（レーンの番号, 曲の頭からの秒, 描いたデバイス, 送った値, 光の量）</summary>
    internal delegate void FrameSink(int lane, double t, DeviceRenderer r, Rgb[] output, ReadOnlySpan<LinColor> light);

    /// <summary>描くレーン 1 本（デバイス・設定・場面の代わり。latency はデバイスの遅れ（秒。TEval = T + latency））</summary>
    internal sealed record LaneSpec(string Label, RgbDevice Dev, ResolvedMusic Music, Func<double, MusicScene>? Scenes = null, double Latency = 0);

    internal static RgbDevice StripDev() => new(0, "Strip", 24, 4, null, "HID: x");
    internal static RgbDevice SlowDev() => new(1, "Dram", 8, RgbDevice.TypeDram, null, "I2C: x");
    internal static RgbDevice GpuDev() => new(2, "NVIDIA GeForce RTX", 6, RgbDevice.TypeGpu, new[] { 1, 1, 1, 1, 1, 1 }, "I2C: y");
    internal static RgbDevice FlickerProneDev() => new(3, "ASRock Radeon RX", 4, RgbDevice.TypeGpu, new[] { 1, 1, 1, 1 }, "I2C: z");

    private static float[]? _dense;
    /// <summary>音の多い曲（128 BPM・20 秒）</summary>
    private static float[] Dense()
    {
        if (_dense is not null) return _dense;
        var g = new SignalGen(48000, 20, seed: 128);
        g.DenseMix(128, 0.5, 19.5);
        return _dense = g.Buf;
    }

    internal static RgbConfig Cfg() => new() { Mode = RgbModes.Music, Brightness = 100, Gamma = true, StaticColor = "#6A00FF" };

    /// <summary>
    /// 本物の分析（AudioEngine）に曲を 10 ミリ秒ずつ流し、60 コマ／秒で全部のレーンを描く（ModeSyncBench と同じ順序）。
    /// 返すのはレーンごとの、送った値（0〜255）全部の FNV-1a
    /// </summary>
    internal static ulong[] Render(float[] mono, int fs, IReadOnlyList<LaneSpec> specs, FrameSink? frame = null)
    {
        var engine = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
        int nl = specs.Count;
        var r = new DeviceRenderer[nl];
        var inp = new RenderInput[nl];
        var outs = new Rgb[nl][];
        var cursors = new long[nl];
        var hash = new ulong[nl];
        var clocks = new Func<double, MusicScene>?[nl];
        for (int i = 0; i < nl; i++)
        {
            r[i] = new DeviceRenderer(specs[i].Dev);
            inp[i] = new RenderInput { Cfg = Cfg(), Music = specs[i].Music, Device = new RgbDeviceSettings { Name = specs[i].Dev.Name } };
            outs[i] = new Rgb[specs[i].Dev.LedCount];
            hash[i] = 14695981039346656037UL;
            clocks[i] = ModeSyncBenchTests.SceneClock(specs[i].Scenes, T0);
        }
        int chunk = fs / 100;
        double next = T0, last = double.NaN;
        for (int pos = 0; pos < mono.Length; pos += chunk)
        {
            int n = Math.Min(chunk, mono.Length - pos);
            engine.Push(mono.AsSpan(pos, n), T0 + pos / (double)fs);
            double tAudio = T0 + (pos + n) / (double)fs;
            while (next <= tAudio)
            {
                double t = next;
                for (int i = 0; i < nl; i++)
                {
                    var input = inp[i];
                    double lat = specs[i].Latency;
                    input.T = t;
                    input.TEval = t + lat;
                    input.TPrevEval = double.IsNaN(last) ? t + lat : last + lat;
                    input.Dt = double.IsNaN(last) ? 1 / Fps : t - last;
                    input.Beat = engine.Beat;
                    input.Scene = clocks[i]?.Invoke(t) ?? engine.Scene;   // アプリと同じく、1 コマに 1 回読んだ場面を全デバイスに
                    input.Features = engine.Features;
                    engine.Features.Sample(t + lat, input.Frame);
                    input.Events.Clear();
                    engine.Events.Read(ref cursors[i], t + lat, input.Events);
                    r[i].Render(input, outs[i]);
                    ulong h = hash[i];
                    foreach (var o in outs[i])
                    {
                        h = (h ^ o.R) * 1099511628211UL;
                        h = (h ^ o.G) * 1099511628211UL;
                        h = (h ^ o.B) * 1099511628211UL;
                    }
                    hash[i] = h;
                    frame?.Invoke(i, t - T0, r[i], outs[i], r[i].LastLight);
                }
                last = t;
                next += 1 / Fps;
            }
        }
        return hash;
    }

    /// <summary>場面の並び（曲の頭からの秒）。同じ場面の間は同じオブジェクトを返す（毎コマ作らない）</summary>
    internal static Func<double, MusicScene> Schedule(params (double T, SceneGroup G)[] steps)
    {
        var scenes = new MusicScene[steps.Length];
        var from = SceneGroup.None;
        for (int k = 0; k < steps.Length; k++)
        {
            scenes[k] = new MusicScene(steps[k].G, from, steps[k].T, 0.8f);
            from = steps[k].G;
        }
        return t =>
        {
            int idx = -1;
            for (int k = 0; k < steps.Length; k++) if (t >= steps[k].T) idx = k;
            return idx < 0 ? MusicScene.None : scenes[idx];
        };
    }

    /// <summary>6 秒ごとに 場面なし → 歌 → 金管 → ギター → 鍵盤 → シンセ → 弦 → 場面なし … と巡る</summary>
    internal static Func<double, MusicScene> Cycle(double seconds = 60)
    {
        SceneGroup[] order = { SceneGroup.Vocal, SceneGroup.Brass, SceneGroup.Guitar, SceneGroup.Keys, SceneGroup.Synth, SceneGroup.Strings, SceneGroup.None };
        var steps = new List<(double, SceneGroup)>();
        for (int k = 0; 6.0 * (k + 1) < seconds; k++) steps.Add((6.0 * (k + 1), order[k % order.Length]));
        return Schedule(steps.ToArray());
    }

    /// <summary>光り方 × パレット × デバイス（速いテープ・遅いメモリ・グラボ）のレーン</summary>
    internal static List<LaneSpec> MatrixLanes(Func<ResolvedMusic, ResolvedMusic>? tweak = null, Func<double, MusicScene>? scenes = null)
    {
        var lanes = new List<LaneSpec>();
        foreach (var dev in new[] { StripDev(), SlowDev(), GpuDev() })
        foreach (var style in MusicStyles.All)
        foreach (var pal in new[] { Palettes.Rainbow, Palettes.Fire, Palettes.Solid, Palettes.Mono })
        {
            var m = ResolvedMusic.Default with { Effect = style, Palette = pal };
            if (style == MusicStyles.Strobe) m = m with { StrobeAllowed = true, ReduceFlashing = false };
            if (tweak is not null) m = tweak(m);
            lanes.Add(new LaneSpec($"{dev.Name}/{style}/{pal}", dev, m, scenes));
        }
        return lanes;
    }

    /// <summary>このレーンで実際に動く光り方に、場面の色を使うか（パレット・光り方で決まる）</summary>
    private static bool Tinted(LaneSpec l)
    {
        var v = EffectCatalog.Resolve(l.Music.Effect, null, l.Dev.IsSlow, l.Music.StrobeAllowed);
        if (l.Dev.IsFlickerProne) v = DeviceRenderer.FlickerProneVariant(v, null);
        return ScenePalettes.Applies(l.Music with { SceneColors = true }, v);
    }

    // ================================================================
    // B0・B1・B5 オフなら今までと同じ・使わない光り方とパレット

    /// <summary>
    /// B1: 「AI で楽器を聞き分ける」がオフなら、場面が来ても出力は 1 バイトも変わらない。オンでも場面なしなら同じ。
    /// （B0: 場面の色を入れる前の版で、同じ 156 本の出力の FNV-1a を書き出し、入れた後のオフの出力と全部同じことを確かめた）
    /// B5: 音量メーター・ストロボ・パーツごとの役割と、パレット「選んだ色だけ」「選んだ色と近い色」は、場面が巡っても同じ出力
    /// </summary>
    public static void TestOffIsByteIdenticalAndExclusionsUnchanged()
    {
        var cycle = Cycle(20);
        var baseLanes = MatrixLanes();
        var offScene = MatrixLanes(null, cycle);
        var onNone = MatrixLanes(m => m with { SceneColors = true }, _ => MusicScene.None);
        var onCycle = MatrixLanes(m => m with { SceneColors = true }, cycle);
        var all = baseLanes.Concat(offScene).Concat(onNone).Concat(onCycle).ToList();
        var h = Render(Dense(), 48000, all);
        int n = baseLanes.Count;
        int same1 = 0, same2 = 0, excl = 0, exclSame = 0, tinted = 0, tintedDiff = 0, pattern = 0, patternDiff = 0;
        var bad = new List<string>();
        for (int i = 0; i < n; i++)
        {
            if (h[n + i] == h[i]) same1++; else bad.Add("off+scene " + baseLanes[i].Label);
            if (h[2 * n + i] == h[i]) same2++; else bad.Add("on+None " + baseLanes[i].Label);
            // 「楽器で光る」は、単色・近い色のパレットでも場面で光り方の形を変える（色は変えない。InstrumentEnsembleTests が色合いを確かめる）
            if (onCycle[i].Music.Effect == MusicStyles.Ensemble && !Tinted(onCycle[i]))
            {
                pattern++;
                if (h[3 * n + i] != h[i]) patternDiff++;
                continue;
            }
            if (!Tinted(onCycle[i]))
            {
                excl++;
                if (h[3 * n + i] == h[i]) exclSame++; else bad.Add("excluded but changed " + baseLanes[i].Label);
            }
            else
            {
                tinted++;
                if (h[3 * n + i] != h[i]) tintedDiff++; else bad.Add("tinted but unchanged " + baseLanes[i].Label);
            }
        }
        foreach (var b in bad.Take(20)) T.Info(b);
        T.Check(same1 == n, $"scene colours off: injected scenes change nothing ({same1}/{n} lanes byte-identical, all styles × Rainbow/Fire/Solid/Mono × strip/DRAM/GPU)");
        T.Check(same2 == n, $"scene colours on with no scene: byte-identical to off ({same2}/{n})");
        T.Check(exclSame == excl && excl > 0, $"Meter, Strobe, Role and the Solid/Mono palettes ignore scenes ({exclSame}/{excl} lanes byte-identical)");
        T.Check(tintedDiff == tinted && tinted > 0, $"every other style recolours with scenes on ({tintedDiff}/{tinted} lanes changed)");
        T.Check(pattern == 6 && patternDiff >= 2, $"Ensemble on Solid/Mono switches its light pattern with scenes instead of its colour ({patternDiff}/{pattern} lanes changed; the GPU keeps a constant brightness)");
    }

    // ================================================================
    // B2 色: 歌の場面で青くなり、場面がなくなるとパレットの色に戻る

    private static readonly float VocalHue = OkLab.FromRgb(Rgb.Parse("#2D6BFF")).Hue;

    private static float HueDiffDeg(float a, float b)
    {
        float d = MathF.Abs(a - b) % (2 * MathF.PI);
        if (d > MathF.PI) d = 2 * MathF.PI - d;
        return d * 180 / MathF.PI;
    }

    /// <summary>
    /// B2: パレット「炎」・歌の場面を 5 秒から、場面なしを 13 秒から。光っている LED の、明るさで重みをつけた色相の平均。
    /// 戻ったあとは、場面の色を使わないレーンの同じ時間と比べる（パルス・ムードは色が時刻でゆっくり移るので、5 秒前の色とは違う）
    /// </summary>
    public static void TestVocalSceneTurnsBlueAndBack()
    {
        var sched = Schedule((5.0, SceneGroup.Vocal), (13.0, SceneGroup.None));
        var styles = MusicStyles.All.Where(s => ScenePalettes.Applies(EffectCatalog.Resolve(s, null, false, true))).ToList();
        var lanes = styles.Select(s => new LaneSpec(s, StripDev(), ResolvedMusic.Default with { Effect = s, Palette = Palettes.Fire, SceneColors = true }, sched))
            .Concat(styles.Select(s => new LaneSpec(s + " off", StripDev(), ResolvedMusic.Default with { Effect = s, Palette = Palettes.Fire }))).ToList();
        int ns = styles.Count;
        // 時間の区切り: 0 = 1〜5 秒（炎）、1 = 7.5〜13 秒（歌の色）、2 = 15.5〜19.5 秒（炎に戻る）
        var sx = new double[lanes.Count, 3];
        var sy = new double[lanes.Count, 3];
        Render(Dense(), 48000, lanes, (i, t, _, _, light) =>
        {
            int w = t >= 1 && t < 5 ? 0 : t >= 7.5 && t < 13 ? 1 : t >= 15.5 && t < 19.5 ? 2 : -1;
            if (w < 0) return;
            foreach (var c in light)
            {
                float m = c.MaxChannel;
                if (m < 0.02f) continue;
                var lab = OkLab.FromLinear(c.R, c.G, c.B);
                if (lab.Chroma < 0.02f) continue;
                sx[i, w] += m * Math.Cos(lab.Hue);
                sy[i, w] += m * Math.Sin(lab.Hue);
            }
        });
        for (int i = 0; i < ns; i++)
        {
            float h0 = (float)Math.Atan2(sy[i, 0], sx[i, 0]), h1 = (float)Math.Atan2(sy[i, 1], sx[i, 1]), h2 = (float)Math.Atan2(sy[i, 2], sx[i, 2]);
            float off2 = (float)Math.Atan2(sy[ns + i, 2], sx[ns + i, 2]);
            float d0 = HueDiffDeg(h0, VocalHue), d1 = HueDiffDeg(h1, VocalHue), d2 = HueDiffDeg(h2, VocalHue), back = HueDiffDeg(h2, off2);
            T.Check(d0 > 60 && d1 < 30 && d2 > 60 && back < 10,
                $"{lanes[i].Label}: Fire {d0:F0}° from vocal blue before, {d1:F0}° during the vocal scene, back to Fire after ({d2:F0}° from blue, {back:F1}° from the scenes-off run)");
        }
    }

    // ================================================================
    // B3 なめらかさ: 移り変わりの途中の色の変化と明るさ

    /// <summary>
    /// B3: すべての場面の組み合わせ（場面なし → 各場面も）× パレット（レインボー・ネオン・炎）で、1/60 秒ごと・位置 0〜1（0.05 刻み）に
    /// 色を引き、となりのコマとの色の差（OKLab の ΔE）が MaxFadeDe（0.015）以下、いちばん強い成分が 2 つの色の間の値の ±3% 以内。
    /// 移り変わりは 2.5 秒の smoothstep（10%〜90% に 1.5 秒以上かかる）
    /// </summary>
    public static void TestSceneFadeIsSmooth()
    {
        var groups = Enum.GetValues<SceneGroup>();
        double worstDe = 0, worstMax = 0, worstLabMax = 0;
        string worstDeAt = "", worstMaxAt = "";
        bool userExact = true;
        foreach (var palName in new[] { Palettes.Rainbow, Palettes.Neon, Palettes.Fire })
        {
            var user = ColorPalette.Get(palName, null);
            foreach (var from in groups)
            foreach (var to in groups)
            {
                if (from == to) continue;
                var scene = new MusicScene(to, from, 10.0, 1);
                Lab[] prev = new Lab[21];
                for (int f = -2; f <= (int)(MusicScene.FadeSec * Fps) + 2; f++)
                {
                    double t = 10.0 + f / Fps;
                    var v = PaletteView.Of(user, scene, t, snap: false);
                    for (int k = 0; k <= 20; k++)
                    {
                        double x = k * 0.05;
                        var led = v.At(x, true);
                        var lab = OkLab.FromLinear(led.R, led.G, led.B);
                        if (f > -2)
                        {
                            var p = prev[k];
                            double de = Math.Sqrt((lab.L - p.L) * (lab.L - p.L) + (lab.A - p.A) * (lab.A - p.A) + (lab.B - p.B) * (lab.B - p.B));
                            if (de > worstDe) { worstDe = de; worstDeAt = $"{palName} {from}→{to} x={x:F2} t+{f / Fps:F2}"; }
                        }
                        prev[k] = lab;
                        var pa = (ScenePalettes.Of(from) ?? user).At(x, true).MaxChannel;
                        var pb = (ScenePalettes.Of(to) ?? user).At(x, true).MaxChannel;
                        float target = pa + (pb - pa) * v.U;
                        double e = Math.Abs(led.MaxChannel - target) / Math.Max(target, 1e-3);
                        if (e > worstMax) { worstMax = e; worstMaxAt = $"{palName} {from}→{to} x={x:F2}"; }
                        // OKLab で引いた色（白と混ぜる光り方が使う）も、光の量で同じ明るさ
                        var l2 = v.LabAt(x);
                        OkLab.ToLinear(l2, out float r2, out float g2, out float b2);
                        var aLab = (ScenePalettes.Of(from) ?? user).LabAt(x);
                        var bLab = (ScenePalettes.Of(to) ?? user).LabAt(x);
                        OkLab.ToLinear(aLab, out float ra, out float ga, out float ba);
                        OkLab.ToLinear(bLab, out float rb, out float gb, out float bb);
                        float ma = Math.Clamp(Math.Max(ra, Math.Max(ga, ba)), 0, 1), mb = Math.Clamp(Math.Max(rb, Math.Max(gb, bb)), 0, 1);
                        float tl = ma + (mb - ma) * v.U;
                        double e2 = Math.Abs(Math.Clamp(Math.Max(r2, Math.Max(g2, b2)), 0, 1) - tl) / Math.Max(tl, 1e-3);
                        worstLabMax = Math.Max(worstLabMax, e2);
                        // 場面なし・話し声どうしは、選んだパレットそのもの
                        if (ScenePalettes.Of(from) is null && ScenePalettes.Of(to) is null && led != user.At(x, true)) userExact = false;
                    }
                }
            }
        }
        T.Info($"worst ΔE per frame {worstDe:F4} at {worstDeAt}; worst max-channel error {worstMax * 100:F2} % at {worstMaxAt}; OKLab path {worstLabMax * 100:F2} %");
        T.Check(worstDe <= MaxFadeDe, $"scene crossfade: colour change per 1/60 s ≤ {MaxFadeDe} ΔE for every pair (worst {worstDe:F4})");
        T.Check(worstMax <= 0.03 && worstLabMax <= 0.03, $"scene crossfade: no brightness dip (max channel within ±3 % of the target; LED {worstMax * 100:F2} %, OKLab {worstLabMax * 100:F2} %)");
        T.Check(userExact, "None ↔ Speech: exactly the user's palette (no colour change)");

        // 移り変わりの速さ: 10% → 90% に 1.5 秒以上（smoothstep の 2.5 秒）、1 コマの変化は 0.0101 以下（傾きの最大 1.5 / 2.5 秒）
        var s = new MusicScene(SceneGroup.Vocal, SceneGroup.None, 0, 1);
        double t10 = double.NaN, t90 = double.NaN, maxStep = 0;
        float last = 0;
        for (int f = 0; f <= 200; f++)
        {
            double t = f / Fps;
            float u = s.Progress(t);
            maxStep = Math.Max(maxStep, u - last);
            last = u;
            if (double.IsNaN(t10) && u >= 0.1f) t10 = t;
            if (double.IsNaN(t90) && u >= 0.9f) t90 = t;
        }
        T.Check(t90 - t10 >= 1.5 && maxStep <= 1.5 / MusicScene.FadeSec / Fps + 1e-4,
            $"scene fade takes 2.5 s: 10 %→90 % in {t90 - t10:F2} s (≥ 1.5 s), largest step per frame {maxStep:F4}");
        // 白っぽい鍵盤の色でも、ふつうのパレットと同じ明るさ（どの見本も、どれか 1 つの成分が FF）
        float minStop = 1;
        foreach (var g in groups)
            if (ScenePalettes.Of(g) is { } p)
                for (int k = 0; k < p.StopCount; k++) minStop = Math.Min(minStop, p.At(k / (double)p.StopCount, false).MaxChannel);
        T.Check(minStop >= 0.99f, $"every scene palette stop has one channel at FF (same brightness as the normal palettes; lowest {minStop * 255:F1})");
    }

    // ================================================================
    // 場面がばたつかない: 楽器の AI の点数が入れ替わり続けても、色は 6 秒に 1 回までしか変わらない

    /// <summary>
    /// 歌とギターの点数が 1 回（0.48 秒）ごと、2 秒ごとに入れ替わる・しきい値の近くで揺れる点数を SceneTracker に入れ、
    /// その場面で光らせる。色の切り替えは 6 秒以上あき、切り替えごとに 2.5 秒かけて変わる（途中で次の切り替えが始まらない）
    /// </summary>
    public static void TestScenesDoNotFlapUnderAlternatingScores()
    {
        var rng = new Random(7);
        var scenarios = new (string Name, Func<int, double, float[]> Scores)[]
        {
            ("every window", (k, _) => G(k % 2 == 0 ? 0.4f : 0, k % 2 == 0 ? 0 : 0.4f)),
            ("every 2 s", (_, t) => ((int)(t / 2) % 2 == 0) ? G(0.6f, 0.1f) : G(0.1f, 0.6f)),
            ("near threshold", (_, _) => G(0.20f + (float)(rng.NextDouble() * 0.12 - 0.06), 0.18f + (float)(rng.NextDouble() * 0.12 - 0.06))),
        };
        foreach (var (name, scores) in scenarios)
        {
            var tracker = new SceneTracker();
            var timeline = new List<MusicScene>();
            double hop = Yamnet.HopSec;
            for (int k = 0; k * hop < 120; k++)
            {
                double t = (k + 1) * hop;
                timeline.Add(tracker.Update(t, scores(k, t)));
            }
            var switches = timeline.Select(s => s.SwitchT).Where(double.IsFinite).Distinct().OrderBy(x => x).ToList();
            double minGap = switches.Zip(switches.Skip(1), (a, b) => b - a).DefaultIfEmpty(double.PositiveInfinity).Min();
            // その場面で光らせたときの色（PaletteView。光り方の中で引く色）の、1 コマの変化。
            // 切り替えが前の移り変わりの途中で始まると、色が跳ぶ
            var r = new DeviceRenderer(StripDev());
            var input = new RenderInput { Cfg = Cfg(), Music = ResolvedMusic.Default with { Effect = MusicStyles.Mood, Palette = Palettes.Fire, SceneColors = true } };
            input.Frame.GateOpen = true;
            input.Frame.Activity = 1;
            var outp = new Rgb[24];
            var prev = new Lab[11];
            double maxDe = 0;
            for (int f = 0; f < 120 * Fps; f++)
            {
                double t = f / Fps;
                int idx = Math.Min((int)Math.Floor(t / hop) - 1, timeline.Count - 1);
                input.T = input.TEval = t;
                input.TPrevEval = Math.Max(0, t - 1 / Fps);
                input.Dt = 1 / Fps;
                input.Frame.T = t;
                input.Scene = idx < 0 ? MusicScene.None : timeline[idx];
                r.Render(input, outp);
                var v = r.Context.View;
                for (int k = 0; k <= 10; k++)
                {
                    var led = v.At(k * 0.1, true);
                    var lab = OkLab.FromLinear(led.R, led.G, led.B);
                    if (f > 0) maxDe = Math.Max(maxDe, Math.Sqrt((lab.L - prev[k].L) * (lab.L - prev[k].L) + (lab.A - prev[k].A) * (lab.A - prev[k].A) + (lab.B - prev[k].B) * (lab.B - prev[k].B)));
                    prev[k] = lab;
                }
            }
            T.Check(minGap >= SceneTracker.Tune.MinDwellSec - 1e-6 && switches.Count <= 120 / SceneTracker.Tune.MinDwellSec,
                $"{name}: {switches.Count} scene switches in 120 s, closest {minGap:F2} s apart (≥ 6 s)");
            T.Check(maxDe <= MaxFadeDe, $"{name}: scene colour never jumps (largest colour change per frame {maxDe:F4} ΔE ≤ {MaxFadeDe})");
        }

        static float[] G(float vocal, float guitar)
        {
            var g = new float[SceneClasses.Count];
            g[SceneClasses.Vocal] = vocal;
            g[SceneClasses.Guitar] = guitar;
            g[SceneClasses.Music] = 0.9f;
            return g;
        }
    }

    // ================================================================
    // 点滅の制限は変わらない

    /// <summary>
    /// 場面が巡っても、点滅の制限（FlashLimiter）が受け付けた光・はじいた光・いちばん短い間隔はオフのときと同じ。
    /// 送った値で数えた「強い光」（ゾーンの平均が 0.1 秒以内に 0.2 以上明るくなる）も増えない
    /// </summary>
    public static void TestFlashLimitsUnchanged()
    {
        var cycle = Cycle(20);
        var styles = MusicStyles.All.ToArray();
        var lanes = new List<LaneSpec>();
        foreach (var (intensity, reduce) in new[] { (Intensities.Moderate, true), (Intensities.Intense, false) })
        foreach (var s in styles)
        {
            var cfg = new RgbConfig { Mode = RgbModes.Music };
            MusicTuning.ApplyIntensity(cfg.Music, intensity);
            cfg.Music.ReduceFlashing = reduce;
            var m = MusicTuning.Resolve(cfg) with { Effect = s, Palette = Palettes.Neon };
            lanes.Add(new LaneSpec($"{intensity}/{s} off", StripDev(), m));
            lanes.Add(new LaneSpec($"{intensity}/{s} on", StripDev(), m with { SceneColors = true }, cycle));
        }
        var limiter = new (int A, int R, double Gap)[lanes.Count];
        var means = lanes.Select(_ => new List<double>()).ToArray();
        Render(Dense(), 48000, lanes, (i, _, r, o, _) =>
        {
            double sum = 0;
            foreach (var c in o) sum += Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
            means[i].Add(sum / o.Length);
            limiter[i] = (r.Limiter.Accepted, r.Limiter.Rejected, r.Limiter.MinAcceptedGap);
        });
        int same = 0, ok = 0;
        var bad = new List<string>();
        for (int i = 0; i < lanes.Count; i += 2)
        {
            if (limiter[i] == limiter[i + 1]) same++; else bad.Add($"{lanes[i].Label}: limiter {limiter[i]} vs {limiter[i + 1]}");
            var (a, aw) = StrongFlashes(means[i]);
            var (b, bw) = StrongFlashes(means[i + 1]);
            // 点滅を抑える設定（ストロボ以外）では 1 秒に 3 回まで。オフのときより 1 秒あたりの最大が増えない
            bool reduce = lanes[i].Music.ReduceFlashing && lanes[i].Music.Effect != MusicStyles.Strobe;
            bool pass = bw <= Math.Max(aw, reduce ? 3 : aw);
            if (pass) ok++;
            if (!pass || a != b) bad.Add($"{lanes[i].Label}: strong flashes {a} → {b} in 20 s, most in 1 s {aw} → {bw}");
        }
        foreach (var b in bad) T.Info(b);
        int pairs = lanes.Count / 2;
        T.Check(same == pairs, $"flash limiter decisions identical with scenes on ({same}/{pairs} style × intensity pairs)");
        T.Check(ok == pairs, $"strong flashes (zone mean +0.2 within 0.1 s) per 1 s window not above the scenes-off run (and ≤ 3 with reduce flashing) ({ok}/{pairs})");
    }

    /// <summary>
    /// ゾーンの平均（0〜1）が 0.1 秒（6 コマ）以内に 0.2 以上明るくなった回数（続けて明るくなる間は 1 回）と、
    /// 1 秒（60 コマ）の間のいちばん多い回数
    /// </summary>
    private static (int Count, int MaxPerSec) StrongFlashes(List<double> m)
    {
        var at = new List<int>();
        bool inFlash = false;
        for (int f = 6; f < m.Count; f++)
        {
            double lo = double.MaxValue;
            for (int k = f - 6; k < f; k++) lo = Math.Min(lo, m[k]);
            bool rise = m[f] - lo >= 0.2;
            if (rise && !inFlash) at.Add(f);
            inFlash = rise;
        }
        int best = 0;
        for (int a = 0, b = 0; b < at.Count; b++)
        {
            while (at[b] - at[a] >= (int)Fps) a++;
            best = Math.Max(best, b - a + 1);
        }
        return (at.Count, best);
    }

    // ================================================================
    // B6 ちかちかしやすいデバイス・グラボ: 場面の切り替えごとに、場面のせいで色が変わるのは 1 回だけ

    /// <summary>
    /// グラボ（明るさ一定・ビートの色を一度に切り替える）と、ちかちかしやすいグラボ（ASRock の Radeon。どの光り方も一度に切り替える）は、
    /// 場面の色も移り変わりの半分で一度に切り替える（途中の色を書かない）。場面の切り替え 3 回で、使う色の表が変わるのは 3 回だけ
    /// </summary>
    public static void TestLowWriteDevicesSwitchOnce()
    {
        var sched = Schedule((4.0, SceneGroup.Vocal), (10.0, SceneGroup.Brass), (16.0, SceneGroup.Keys));
        var lanes = new List<LaneSpec>();
        foreach (var dev in new[] { GpuDev(), FlickerProneDev() })
        foreach (var s in new[] { MusicStyles.BeatHue, MusicStyles.Pulse, MusicStyles.Mood, MusicStyles.Mix })
            lanes.Add(new LaneSpec($"{dev.Name}/{s}", dev, ResolvedMusic.Default with { Effect = s, Palette = Palettes.Neon, SceneColors = true }, sched));
        var mid = new int[lanes.Count];
        var swaps = new int[lanes.Count];
        var noFade = new bool[lanes.Count];
        var last = new ColorPalette?[lanes.Count];
        Render(Dense(), 48000, lanes, (i, _, r, _, _) =>
        {
            var c = r.Context;
            noFade[i] |= c.NoFade;
            var v = c.View;
            var p = ReferenceEquals(v.From, v.To) || v.U >= 1 ? v.To : v.U <= 0 ? v.From : null;
            if (p is null) { mid[i]++; return; }
            if (last[i] is not null && !ReferenceEquals(p, last[i])) swaps[i]++;
            last[i] = p;
        });
        for (int i = 0; i < lanes.Count; i++)
        {
            if (!noFade[i]) { T.Info($"{lanes[i].Label}: not a no-fade style on this device (fades over 2.5 s like its own colours), {mid[i]} mid-fade frames"); continue; }
            T.Check(mid[i] == 0 && swaps[i] == 3, $"{lanes[i].Label}: scene colour switches in one write ({swaps[i]} palette swaps for 3 scene switches, {mid[i]} mid-fade frames)");
        }
    }


    // ================================================================
    // B7 遅れの違う 2 台のデバイス（8 ミリ秒・60 ミリ秒）

    /// <summary>
    /// 場面の色の割合は、LED が光る時刻（描く時刻 + デバイスの遅れ）で決まるので、同じ時刻に光る LED はどちらのデバイスも同じ割合。
    /// 同じコマで比べると、遅れの差 52 ミリ秒の分だけずれる（傾きの最大 0.6/秒 × 0.052 秒 = 0.031）
    /// </summary>
    public static void TestDevicesWithDifferentLatencyStayInStep()
    {
        var sched = Schedule((6.0, SceneGroup.Synth));
        var m = ResolvedMusic.Default with { Effect = MusicStyles.Pulse, Palette = Palettes.Ocean, SceneColors = true };
        var lanes = new List<LaneSpec>
        {
            new("8 ms", StripDev(), m, sched, 0.008),
            new("60 ms", StripDev(), m, sched, 0.060),
        };
        var u = new[] { new List<(double T, float U)>(), new List<(double T, float U)>() };
        Render(Dense(), 48000, lanes, (i, t, r, _, _) =>
        {
            var v = r.Context.View;
            u[i].Add((t + lanes[i].Latency, ReferenceEquals(v.From, v.To) ? 0 : v.U));
        });
        double perTick = 0, atLight = 0;
        for (int f = 0; f < u[0].Count; f++)
        {
            perTick = Math.Max(perTick, Math.Abs(u[0][f].U - u[1][f].U));
            // 同じ光る時刻: 60 ミリ秒のデバイスの、光る時刻が同じになるコマ（52 ミリ秒 ≒ 3.1 コマ前）の値と比べる（間は直線で補う）
            double tl = u[1][f].T;
            int g = u[0].FindIndex(x => x.T >= tl);
            if (g <= 0) continue;
            var (ta, ua) = u[0][g - 1];
            var (tb, ub) = u[0][g];
            float interp = (float)(ua + (ub - ua) * (tl - ta) / (tb - ta));
            atLight = Math.Max(atLight, Math.Abs(interp - u[1][f].U));
        }
        T.Check(atLight <= 0.012, $"two devices (8 / 60 ms latency): scene blend at the same light time differs by {atLight:F4} (≤ 0.012, linear interpolation between frames)");
        T.Check(perTick <= 0.6 * 0.052 + 1e-3, $"per render tick the blend differs by {perTick:F4} (≤ 0.032 = fade slope × 52 ms latency difference)");
    }

    // ================================================================
    // B8 設定

    public static void TestOldConfigLoadsWithSceneColorsOff()
    {
        // 6.15.7 の設定（MusicScenes がない）
        const string old = "{\"Rgb\":{\"Enabled\":true,\"Mode\":\"Music\",\"MusicStyle\":\"BeatHue\",\"MusicAi\":true,\"MusicMemory\":true,\"Music\":{\"Palette\":\"Fire\"}}}";
        var cfg = AppConfig.FromJson(old);
        T.Check(!cfg.Rgb.MusicScenes && !MusicTuning.Resolve(cfg.Rgb).SceneColors, "a 6.15.7 config loads with scene colours off (Resolve gives SceneColors=false)");
        T.Check(!ResolvedMusic.Default.SceneColors && !new RgbConfig().MusicScenes, "new configs start with scene colours off");
        cfg.Rgb.MusicScenes = true;
        var back = AppConfig.FromJson(cfg.ToJson());
        T.Check(back.Rgb.MusicScenes && MusicTuning.Resolve(back.Rgb).SceneColors, "MusicScenes=true survives a save/load round trip and resolves to SceneColors=true");
    }

    // ================================================================
    // B10 音が止まったときの光り方は、場面の色を使わない

    public static void TestIdleIgnoresScenes()
    {
        var g = new SignalGen(48000, 14, seed: 5);
        g.DenseMix(120, 0.5, 5);
        var sched = Schedule((1.0, SceneGroup.Vocal));
        var lanes = new List<LaneSpec>();
        foreach (var idle in IdleEffects.All)
        {
            var m = ResolvedMusic.Default with { Effect = MusicStyles.BeatHue, Palette = Palettes.Fire, IdleEffect = idle, IdleDelaySec = 1 };
            lanes.Add(new LaneSpec($"{idle} off", StripDev(), m));
            lanes.Add(new LaneSpec($"{idle} on", StripDev(), m with { SceneColors = true }, sched));
        }
        var outs = lanes.Select(_ => new List<Rgb[]>()).ToArray();
        var idleW = lanes.Select(_ => new List<float>()).ToArray();
        Render(g.Buf, 48000, lanes, (i, _, r, o, _) =>
        {
            outs[i].Add((Rgb[])o.Clone());
            idleW[i].Add(r.IdleWeight);
        });
        for (int i = 0; i < lanes.Count; i += 2)
        {
            int compared = 0, diff = 0, firstFull = idleW[i + 1].FindIndex(w => w >= 1);
            for (int f = Math.Max(0, firstFull) + (int)Fps; firstFull >= 0 && f < outs[i].Count; f++)
            {
                compared++;
                if (!outs[i][f].AsSpan().SequenceEqual(outs[i + 1][f])) diff++;
            }
            T.Check(compared > 120 && diff == 0, $"idle {lanes[i].Label.Split(' ')[0]}: with the music stopped, scenes change nothing ({diff}/{compared} frames differ)");
        }
    }
}

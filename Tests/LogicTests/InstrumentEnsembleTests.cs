// ==================================================
// InstrumentEnsembleTests.cs - 光り方「楽器で光る」(EnsembleEffect): 単色で色合いが変わらない・楽器ごとの光の形
//                              （キックは全体・スネアは片側・ハイハットは小さな粒・ベースは端のほう・歌は全体の明るさ・
//                              ギターや鍵盤は流れる光・パッドは呼吸・金管は白く）・小さいゾーン・遅いデバイス・点滅の数・
//                              場面で光の形を切り替えてもなめらか
//   どれも SignalGen の合成音（InstrumentFeatures の手がかりが働くと確かめてある音。InstrumentRoleTests）で確かめる。
//   本物の曲でどう見えるか、楽器の AI の場面の選び方が本物の曲で合っているかは確かめていない
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

/// <summary>1 コマ分の記録（曲の頭からの秒・光の量・送った値）</summary>
public sealed record EnsembleFrame(double T, LinColor[] Light, Rgb[] Out)
{
    /// <summary>そのコマの手がかり（歌・パッド・ベースの大きさ・ギターと鍵盤・金管）</summary>
    public float Vocal, Pad, Bass, Guitar, Brass, FlowA, WaveA, WaveP;
    /// <summary>更新の遅いデバイス: 最後の拍のふくらみの時刻（曲の頭からの秒。まだなければ負の無限大）</summary>
    public double AccentT = double.NegativeInfinity;
}

/// <summary>「楽器で光る」を本物の分析に流して描き、LED ごとに記録する（MusicLight.Run と同じく、音を 80 ミリ秒先まで流しておく）</summary>
public static class EnsembleRun
{
    public const int Fs = 48000;
    public const string Violet = "#6A00FF";
    private const double Lookahead = 0.08, T0 = 1000;

    public static RgbDevice Strip(int leds = 24) => new(0, "Strip", leds, 4, null, "HID: x");
    public static RgbDevice Dram() => new(1, "Dram", 8, RgbDevice.TypeDram, null, "I2C: x");

    /// <summary>単色（選んだ色だけ）の「楽器で光る」。強さ「ふつう」・点滅を抑える</summary>
    public static ResolvedMusic Solid(bool scenes = false) =>
        ResolvedMusic.Default with { Effect = MusicStyles.Ensemble, Palette = Palettes.Solid, SceneColors = scenes };

    public static List<EnsembleFrame> Run(float[] buf, ResolvedMusic m, RgbDevice? dev = null, Func<double, MusicScene>? scenes = null, bool gamma = true,
        List<(double T, AudioEventKind Kind, double Seen)>? events = null)
    {
        dev ??= Strip();
        var e = new AudioEngine(Fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
        var r = new DeviceRenderer(dev);
        var input = new RenderInput
        {
            Cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = gamma, StaticColor = Violet },
            Music = m,
            Device = new RgbDeviceSettings { Name = dev.Name },
        };
        var clock = ModeSyncBenchTests.SceneClock(scenes, T0);
        var output = new Rgb[dev.LedCount];
        var frames = new List<EnsembleFrame>();
        long cursor = 0;
        int pos = 0;
        double last = double.NaN, total = buf.Length / (double)Fs;
        for (int k = 0; ; k++)
        {
            double sec = k / 60.0;
            if (sec + Lookahead > total) break;
            while (pos < buf.Length && pos / (double)Fs < sec + Lookahead)
            {
                int n = Math.Min(480, buf.Length - pos);
                e.Push(new ReadOnlySpan<float>(buf, pos, n), T0 + pos / (double)Fs);
                pos += n;
            }
            double t = T0 + sec;
            input.T = input.TEval = t;
            input.TPrevEval = double.IsNaN(last) ? t : last;
            input.Dt = double.IsNaN(last) ? 0 : t - last;
            input.Beat = e.Beat;
            input.Scene = clock?.Invoke(t) ?? MusicScene.None;
            input.Features = e.Features;
            if (!e.Features.Sample(t, input.Frame)) input.Frame.Clear();
            input.Events.Clear();
            e.Events.Read(ref cursor, t, input.Events);
            if (events is not null) foreach (var ev in input.Events) events.Add((ev.T - T0, ev.Kind, sec));
            r.Render(input, output);
            last = t;
            var fi = input.Frame;
            frames.Add(new EnsembleFrame(sec, r.LastLight.ToArray(), (Rgb[])output.Clone()) { Vocal = fi.Vocal, Pad = fi.Pad, Bass = fi.BassLevel, Guitar = fi.GuitarKeys, Brass = fi.Brass,
                FlowA = (r.CurrentEffect as EnsembleEffect)?.Motion.Flow ?? 0, WaveA = (r.CurrentEffect as EnsembleEffect)?.Motion.Wave ?? 0, WaveP = (r.CurrentEffect as EnsembleEffect)?.Motion.WaveP ?? 0,
                AccentT = ((r.CurrentEffect as EnsembleEffect)?.SlowState.AccentT ?? double.NegativeInfinity) - T0 });
        }
        return frames;
    }

    /// <summary>送った値のいちばん強い成分（0〜1）</summary>
    public static double Code(Rgb c) => Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;

    /// <summary>LED [a, b) の送った値の平均（0〜1）</summary>
    public static double Mean(EnsembleFrame f, int a, int b)
    {
        double s = 0;
        for (int i = a; i < b; i++) s += Code(f.Out[i]);
        return s / Math.Max(1, b - a);
    }

    public static double Mean(EnsembleFrame f) => Mean(f, 0, f.Out.Length);

    /// <summary>
    /// LED [a, b) の、目で見た明るさの平均（送った値は光の量に比例するので、sRGB の曲線で目の感じ方に直す。0〜1）。
    /// 点滅の数は光の量（Mean）で数え、光り方が見て分かるかはこちらで見る
    /// </summary>
    public static double Perceived(EnsembleFrame f, int a, int b)
    {
        double s = 0;
        for (int i = a; i < b; i++) s += OkLab.LinearToSrgb(Code(f.Out[i]));
        return s / Math.Max(1, b - a);
    }

    public static double Perceived(EnsembleFrame f) => Perceived(f, 0, f.Out.Length);

    public static IEnumerable<EnsembleFrame> Between(List<EnsembleFrame> fr, double a, double b) => fr.Where(x => x.T >= a && x.T < b);

    /// <summary>光の量の OKLab（ガンマ補正ありの光の量 → OKLab）</summary>
    public static Lab LabOf(LinColor c) => OkLab.FromLinear(c.R, c.G, c.B);

    public static double HueDiffDeg(double a, double b)
    {
        double d = Math.Abs(a - b) % (2 * Math.PI);
        if (d > Math.PI) d = 2 * Math.PI - d;
        return d * 180 / Math.PI;
    }

    /// <summary>順位の相関（Spearman）</summary>
    public static double Spearman(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        static double[] Rank(IReadOnlyList<double> v)
        {
            var idx = Enumerable.Range(0, v.Count).OrderBy(i => v[i]).ToArray();
            var r = new double[v.Count];
            for (int k = 0; k < idx.Length; k++) r[idx[k]] = k;
            return r;
        }
        var rx = Rank(x);
        var ry = Rank(y);
        double mx = rx.Average(), my = ry.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < rx.Length; i++) { sxy += (rx[i] - mx) * (ry[i] - my); sxx += (rx[i] - mx) * (rx[i] - mx); syy += (ry[i] - my) * (ry[i] - my); }
        return sxx > 0 && syy > 0 ? sxy / Math.Sqrt(sxx * syy) : 0;
    }

    /// <summary>
    /// 強い点滅: ゾーンの平均（送った値 0〜1）が 0.1 秒以内に 0.2 以上上がった、その始まりの時刻（続けて上がっている間は 1 回）
    /// </summary>
    public static List<double> StrongFlashes(IReadOnlyList<EnsembleFrame> fr, int a, int b)
    {
        var m = fr.Select(f => Mean(f, a, b)).ToArray();
        var list = new List<double>();
        bool inFlash = false;
        for (int i = 1; i < m.Length; i++)
        {
            double lo = m[i];
            for (int k = 1; k <= 6 && i - k >= 0; k++) lo = Math.Min(lo, m[i - k]);
            bool rising = m[i] - lo >= 0.2;
            if (rising && !inFlash) list.Add(fr[i].T);
            inFlash = rising;
        }
        return list;
    }

    /// <summary>どの 1 秒の区間でも数えた、いちばん多い数</summary>
    public static int MaxPerSecond(List<double> times)
    {
        int best = 0;
        for (int i = 0; i < times.Count; i++)
        {
            int n = 0;
            for (int k = i; k < times.Count && times[k] < times[i] + 1.0; k++) n++;
            best = Math.Max(best, n);
        }
        return best;
    }
}

public static class InstrumentEnsembleTests
{
    private const int Fs = EnsembleRun.Fs;
    private static readonly double VioletHue = OkLab.FromRgb(Rgb.Parse(EnsembleRun.Violet)).Hue;

    /// <summary>調べる用: ENSEMBLE_DIAG=名前（kick・snare・hat・bass・vocal・strum・pad・brass）で、その合成音の光を 2 秒ぶん出す</summary>
    public static void TestEnsembleDiag()
    {
        if (Environment.GetEnvironmentVariable("ENSEMBLE_DIAG") is not { Length: > 0 } name) return;
        var g = Source(name, 20);
        if (Environment.GetEnvironmentVariable("ENSEMBLE_DIAG_RAW") == "1") LedSteady.Enabled = false;
        var evs = new List<(double T, AudioEventKind Kind, double Seen)>();
        var fr = EnsembleRun.Run(g.Buf, EnsembleRun.Solid(), events: evs);
        double t0 = double.Parse(Environment.GetEnvironmentVariable("ENSEMBLE_DIAG_T") ?? "5", System.Globalization.CultureInfo.InvariantCulture);
        int i0 = (int)(60 * t0);
        LedSteady.Enabled = true;
        foreach (var e in evs.Where(x => x.T >= t0 && x.T < t0 + 2)) T.Info($"event {e.Kind} at {e.T:F3} s (seen at frame {e.Seen:F3})");
        for (int i = i0; i < Math.Min(fr.Count, i0 + 120); i += 2)
        {
            var f = fr[i];
            T.Info($"{f.T:F3} fl{f.FlowA:F2} wv{f.WaveA:F2}@{f.WaveP:F2} v{f.Vocal:F2} p{f.Pad:F2} b{f.Bass:F2} g{f.Guitar:F2} br{f.Brass:F2} mean {EnsembleRun.Mean(f):F3} " + string.Join(" ", f.Out.Select(o => EnsembleRun.Code(o).ToString("F2"))));
        }
    }

    /// <summary>楽器 1 つだけの合成音（0.5 秒から sec 秒まで）</summary>
    private static SignalGen Source(string name, double sec, int seed = 3)
    {
        var g = new SignalGen(Fs, sec, seed: seed);
        switch (name)
        {
            case "kick": g.Loop(120, 0.5, sec, hats: false); break;
            case "snare": g.Loop(120, 0.5, sec, hats: false, snare: true); break;
            case "hat": for (double t = 0.5; t < sec - 0.2; t += 0.125) g.Hat(t, 0.08); break;
            case "bass": g.BassLine(0.5, sec, 2, InstrumentSources.BassNotes); break;
            case "vocal": for (double t = 0.5; t < sec - 2; t += 4) g.VocalTone(t, t + 2, InstrumentSources.VocalNotes, 0.6); break;
            case "strum": for (double t = 0.5; t < sec - 0.5; t += 1) g.Strum(t, new[] { 392.0, 493.9, 587.3, 784 }, 0.08, 0.03); g.Tone(261.6, 0.02, 0.2, sec); g.Pink(-48, 0.2, sec); break;
            case "pad": g.PadChord(0.5, sec - 0.2, InstrumentSources.PadChord1); break;
            case "brass": g.BrassTone(0.5, sec, InstrumentSources.BrassNotes, 0.45); break;
            default: throw new ArgumentException(name);
        }
        return g;
    }

    /// <summary>全部の楽器を重ねた合成音（DenseMix・歌・ギター・パッド・金管）</summary>
    private static float[] Everything(double sec = 20)
    {
        var g = new SignalGen(Fs, sec, seed: 9);
        g.DenseMix(118, 0.5, sec - 0.2);
        g.VocalTone(2, 8, InstrumentSources.VocalNotes, 0.6);
        for (double t = 6; t < 14; t += 0.5) g.Strum(t, new[] { 196.0, 246.9, 293.7, 392 }, 0.06);
        g.PadChord(10, 16, InstrumentSources.PadChord1);
        g.BrassTone(14, sec - 0.5, InstrumentSources.BrassNotes, 0.45);
        return g.Buf;
    }

    /// <summary>
    /// C1: パレット「選んだ色だけ」なら、どの楽器が鳴っても色合いは選んだ色のまま（光っている LED の OKLab の色相が ±3° 以内）。
    /// 変わるのは明るさ・場所・動きと、金管の白さ（鮮やかさ）だけ。場面（歌・ギター・金管…）を巡らせても同じ
    /// </summary>
    public static void TestSolidKeepsTheChosenHue()
    {
        var buf = Everything();
        foreach (var (label, m, scenes) in new (string, ResolvedMusic, Func<double, MusicScene>?)[]
        {
            ("no scenes", EnsembleRun.Solid(), null),
            ("scene cycle", EnsembleRun.Solid(scenes: true), SceneColorTests.Cycle(20)),
            ("intense", EnsembleRun.Solid() with { Intensity = Intensities.Intense, ReduceFlashing = false }, null),
        })
        {
            var fr = EnsembleRun.Run(buf, m, scenes: scenes);
            double worst = 0, worstOut = 0;
            int lit = 0, litOut = 0;
            foreach (var f in fr.Where(x => x.T > 1))
            {
                for (int j = 0; j < f.Light.Length; j++)
                {
                    var lab = EnsembleRun.LabOf(f.Light[j]);
                    if (lab.Chroma > 0.03f)
                    {
                        lit++;
                        worst = Math.Max(worst, EnsembleRun.HueDiffDeg(lab.Hue, VioletHue));
                    }
                    // 送った値（0〜255 に丸めた後）でも、十分明るい LED は同じ色合い
                    var o = f.Out[j];
                    if (Math.Max(o.R, Math.Max(o.G, o.B)) >= 96)
                    {
                        litOut++;
                        worstOut = Math.Max(worstOut, EnsembleRun.HueDiffDeg(OkLab.FromLinear(o.R / 255.0, o.G / 255.0, o.B / 255.0).Hue, VioletHue));
                    }
                }
            }
            T.Check(lit > 1000 && worst <= 3, $"Solid {label}: every lit LED keeps the chosen hue (worst {worst:F2}° over {lit} lit LED frames, light before rounding)");
            T.Check(litOut > 100 && worstOut <= 3, $"Solid {label}: sent colours keep the hue too (worst {worstOut:F2}° over {litOut} LED frames ≥ 96)");
        }
    }

    /// <summary>キックの時刻ごとの、ゾーンの平均の上がり方（直前の平均・いちばん明るいコマ・その時刻・そのコマの LED のばらつき）</summary>
    private static List<(double Base, double Peak, double PeakT, double Cv, double RiseT)> Pulses(List<EnsembleFrame> fr, IEnumerable<double> times, int a, int b)
    {
        var list = new List<(double, double, double, double, double)>();
        foreach (double tk in times)
        {
            var before = EnsembleRun.Between(fr, tk - 0.08, tk - 0.01).ToList();
            var after = EnsembleRun.Between(fr, tk - 0.01, tk + 0.15).ToList();
            if (before.Count == 0 || after.Count == 0) continue;
            double bs = before.Average(f => EnsembleRun.Perceived(f, a, b));
            var pk = after.OrderByDescending(f => EnsembleRun.Perceived(f, a, b)).First();
            double peak = EnsembleRun.Perceived(pk, a, b);
            var lv = Enumerable.Range(a, b - a).Select(i => EnsembleRun.Code(pk.Out[i])).ToArray();
            double mean = lv.Average(), sd = Math.Sqrt(lv.Average(x => (x - mean) * (x - mean)));
            // 上がり始め: 直前の平均から、上がった分の半分を超えた最初のコマ
            double rise = after.First(f => EnsembleRun.Perceived(f, a, b) >= bs + 0.5 * (peak - bs)).T;
            list.Add((bs, peak, pk.T, mean > 0 ? sd / mean : 0, rise - tk));
        }
        return list;
    }

    /// <summary>C2: キックだけのループ: キックのたびにゾーン全体が光り（30 ミリ秒以内に上がり始め、深さ 0.25 以上）、光ったコマは LED ごとの差が小さい</summary>
    public static void TestKickPulsesTheWholeZone()
    {
        var g = Source("kick", 12);
        var fr = EnsembleRun.Run(g.Buf, EnsembleRun.Solid());
        var p = Pulses(fr, g.KickTimes.Where(t => t > 3), 0, 24);
        double depth = p.Min(x => x.Peak - x.Base), rise = p.Max(x => x.RiseT), cv = p.Max(x => x.Cv);
        T.Info($"kicks {p.Count}: perceived depth min {depth:F2} mean {p.Average(x => x.Peak - x.Base):F2}, rise max {rise * 1000:F0} ms, LED spread at the peak (sd/mean) max {cv:F3}");
        foreach (var x in p.OrderBy(x => x.Peak - x.Base).Take(2).Concat(p.OrderByDescending(x => x.Cv).Take(2))) T.Info($"  shallowest or most uneven: peak at {x.PeakT:F3} s base {x.Base:F2} peak {x.Peak:F2} spread {x.Cv:F3}");
        T.Check(p.Count >= 15 && depth >= 0.25, $"every kick pulses the zone mean by ≥ 0.25 of full scale (min {depth:F2})");
        T.Check(rise <= 0.03, $"the pulse starts within 30 ms of the kick (worst {rise * 1000:F0} ms)");
        T.Check(cv < 0.1, $"the kick lights the whole zone evenly (sd/mean at the peak ≤ {cv:F3} < 0.1)");
    }

    /// <summary>C3: スネアだけ: スネアのたびにゾーンの片側が光り（明るい側の平均がもう片側の 1.5 倍以上）、光る側はスネアごとに入れかわる</summary>
    public static void TestSnareLightsOneSide()
    {
        var g = Source("snare", 20);
        var fr = EnsembleRun.Run(g.Buf, EnsembleRun.Solid());
        var ratios = new List<double>();
        var sides = new List<int>();
        foreach (double ts in g.SnareTimes.Where(t => t > 3))
        {
            var pk = EnsembleRun.Between(fr, ts - 0.01, ts + 0.12).OrderByDescending(f => EnsembleRun.Mean(f)).First();
            double lo = EnsembleRun.Mean(pk, 0, 10), hi = EnsembleRun.Mean(pk, 14, 24);
            ratios.Add(Math.Max(lo, hi) / Math.Max(1e-3, Math.Min(lo, hi)));
            sides.Add(hi > lo ? 1 : 0);
        }
        int alt = 0;
        for (int i = 1; i < sides.Count; i++) if (sides[i] != sides[i - 1]) alt++;
        double altShare = alt / (double)Math.Max(1, sides.Count - 1);
        T.Info($"snares {ratios.Count}: brighter/darker half min {ratios.Min():F2} median {ratios.OrderBy(x => x).ElementAt(ratios.Count / 2):F2}, side alternates {altShare:P0}");
        T.Check(ratios.Count >= 12 && ratios.Min() >= 1.5, $"each snare lights one half (brighter half ≥ 1.5× the other, min {ratios.Min():F2})");
        T.Check(altShare >= 0.8, $"the lit side alternates on successive snares ({altShare:P0} ≥ 80%)");
    }

    /// <summary>C4: ハイハットだけ: 光るのは離れた小さな粒（どのコマでも、まわりより 0.15 以上明るい LED は N/12 個まで）で、ゾーン全体の平均はほとんど変わらない</summary>
    public static void TestHatsAreSmallSparkles()
    {
        foreach (int leds in new[] { 24, 8 })
        {
            var g = Source("hat", 12);
            var fr = EnsembleRun.Run(g.Buf, EnsembleRun.Solid(), EnsembleRun.Strip(leds));
            int most = 0, sparkles = 0;
            double worstMean = 0;
            foreach (var f in fr.Where(x => x.T > 3))
            {
                var c = f.Out.Select(EnsembleRun.Code).OrderBy(x => x).ToArray();
                double median = c[c.Length / 2];
                int n = c.Count(x => x > median + 0.15);
                most = Math.Max(most, n);
                if (n > 0) sparkles++;
            }
            foreach (double th in g.HatTimes.Where(t => t > 3))
            {
                double before = EnsembleRun.Between(fr, th - 0.05, th - 0.01).Select(f => EnsembleRun.Mean(f)).DefaultIfEmpty(0).Average();
                double peak = EnsembleRun.Between(fr, th - 0.01, th + 0.1).Select(f => EnsembleRun.Mean(f)).DefaultIfEmpty(0).Max();
                worstMean = Math.Max(worstMean, peak - before);
            }
            int limit = Math.Max(1, leds / 12);
            T.Info($"{leds} LEDs: frames with a sparkle {sparkles}, most sparkling LEDs in one frame {most}, largest zone-mean rise per hat {worstMean:F3}");
            T.Check(sparkles > 50 && most <= limit, $"{leds} LEDs: hats light at most {limit} LED(s) at a time ({most}), never the whole zone");
            T.Check(worstMean <= 0.05, $"{leds} LEDs: a hat moves the zone mean by ≤ 5% ({worstMean:F3})");
        }
    }

    /// <summary>
    /// C5: ベースだけ: 音が鳴っている間は、ゾーンの始まりの側（最初の 35%）が終わりの側（最後の 35%）の 1.5 倍以上明るい。
    /// 1 秒続く音は、0.25 秒の音の 2 倍以上の長さ光る（光の長さが音の長さについてくる。ここは減衰しない低い音で確かめる）
    /// </summary>
    public static void TestBassGlowsAtTheLowEnd()
    {
        var g = Source("bass", 12);
        var fr = EnsembleRun.Run(g.Buf, EnsembleRun.Solid());
        var during = new List<double>();
        foreach (double tn in g.BassNoteTimes.Where(t => t > 3))
            foreach (var f in EnsembleRun.Between(fr, tn + 0.08, tn + 0.3))
                during.Add(EnsembleRun.Mean(f, 0, 8) / Math.Max(1e-3, EnsembleRun.Mean(f, 16, 24)));
        double med = during.OrderBy(x => x).ElementAt(during.Count / 2);
        T.Info($"bass notes: low/high end ratio while sounding median {med:F2}, 10th percentile {during.OrderBy(x => x).ElementAt(during.Count / 10):F2}");
        T.Check(during.Count > 100 && med >= 1.5, $"while a bass note sounds, the low end is ≥ 1.5× the far end (median {med:F2})");

        // 長い音と短い音（減衰しない 55 Hz と 2 倍音。4 秒ごと）
        var h = new SignalGen(Fs, 20, seed: 5);
        var longs = new List<double>();
        var shorts = new List<double>();
        for (double t = 2; t < 19; t += 2)
        {
            bool isLong = ((int)(t / 2)) % 2 == 0;
            double len = isLong ? 1.0 : 0.25;
            h.Tone(55, 0.15, t, t + len);
            h.Tone(110, 0.06, t, t + len);
            (isLong ? longs : shorts).Add(t);
        }
        var fh = EnsembleRun.Run(h.Buf, EnsembleRun.Solid());
        double Lit(double t0)
        {
            var seg = EnsembleRun.Between(fh, t0 - 0.2, t0 + 1.8).ToList();
            var d = seg.Select(f => EnsembleRun.Mean(f, 0, 8) - EnsembleRun.Mean(f, 16, 24)).ToArray();
            double peak = d.Max();
            if (peak < 0.02) return 0;
            return d.Count(x => x > 0.5 * peak) / 60.0;
        }
        double ll = longs.Where(t => t > 3).Average(Lit), ls = shorts.Where(t => t > 3).Average(Lit);
        T.Info($"lit time at the low end: 1.0 s notes {ll:F2} s, 0.25 s notes {ls:F2} s (ratio {ll / Math.Max(1e-3, ls):F2})");
        T.Check(ls > 0 && ll / ls >= 2, $"the bass glow lasts as long as the note (1.0 s notes lit {ll:F2} s vs 0.25 s notes {ls:F2} s, ratio ≥ 2)");
    }

    /// <summary>C6: 歌（2 秒鳴って 2 秒止まる）: ゾーン全体の明るさがなめらかについてくる（遅れ 0.3 秒以内・1 コマの変化は 3 段まで）</summary>
    public static void TestVocalMovesTheWholeBrightnessSmoothly()
    {
        var g = Source("vocal", 20);
        var fr = EnsembleRun.Run(g.Buf, EnsembleRun.Solid());
        // 遅れは、声らしさ（InstrumentFeatures の Vocal）が上がりきるまでの半分に来た時刻から、ゾーンの平均が上がった分の半分に来るまで。
        // 声らしさそのものは、声が始まってから 0.25〜0.3 秒かかって上がる（手がかりの遅れ。ここでは別に数えて表示する）
        var lags = new List<double>();
        var total = new List<double>();
        double steady = 0;
        for (double t = 4.5; t < 16; t += 4)
        {
            var seg = EnsembleRun.Between(fr, t - 0.2, t + 2).ToList();
            double lo = seg.Take(10).Average(f => EnsembleRun.Mean(f));
            double hi = seg.Skip(seg.Count - 30).Average(f => EnsembleRun.Mean(f));
            steady = Math.Max(steady, hi - lo);
            var light = seg.FirstOrDefault(f => f.T >= t && EnsembleRun.Mean(f) >= lo + 0.5 * (hi - lo));
            var cue = seg.FirstOrDefault(f => f.T >= t && f.Vocal >= 0.5f * seg.Skip(seg.Count - 30).Average(x => x.Vocal));
            lags.Add(light is null || cue is null ? 9 : light.T - cue.T);
            total.Add(light is null ? 9 : light.T - t);
        }
        int maxStep = 0;
        double stepT = 0;
        for (int i = 1; i < fr.Count; i++)
            if (fr[i].T > 3)
                for (int j = 0; j < fr[i].Out.Length; j++)
                {
                    int st = Math.Abs((int)Math.Round(EnsembleRun.Code(fr[i].Out[j]) * 255) - (int)Math.Round(EnsembleRun.Code(fr[i - 1].Out[j]) * 255));
                    if (st > maxStep) { maxStep = st; stepT = fr[i].T; }
                }
        T.Info($"vocal on/off: rise {steady:F2}, half-way lag behind the vocal cue {string.Join(" / ", lags.Select(x => x.ToString("F2")))} s, behind the voice itself {string.Join(" / ", total.Select(x => x.ToString("F2")))} s, largest per-frame step {maxStep} codes at {stepT:F2} s");
        T.Check(steady >= 0.05, $"the voice raises the whole zone ({steady:F2})");
        T.Check(lags.Max() <= 0.3, $"the brightness follows the vocal cue within 0.3 s (worst {lags.Max():F2} s; {total.Max():F2} s after the voice starts, most of it the cue itself)");
        // 歌の層はゾーン全体なので、ゾーンの平均の 1 コマの変化で見る（LED ごとの値は、ちらつき止め（LedSteady）が保っていた値を放すときに少し大きく動く）
        double meanStep = 0;
        double meanStepT = 0;
        for (int i = 1; i < fr.Count; i++)
        {
            double st = Math.Abs(EnsembleRun.Mean(fr[i]) - EnsembleRun.Mean(fr[i - 1])) * 255;
            if (fr[i].T > 3 && st > meanStep) { meanStep = st; meanStepT = fr[i].T; }
        }
        T.Info($"largest per-frame step of the zone mean {meanStep:F1} codes at {meanStepT:F2} s (single LED {maxStep} codes)");
        T.Check(meanStep <= 4, $"the whole-zone brightness steps by at most 4 codes per frame ({meanStep:F1}; single LED {maxStep}: the anti-flicker filter LedSteady holds a turning LED until it moves by 2.5 codes + 6%, then lets go)");
        // 光り方そのもの（ちらつき止めの前）は、どの LED も 1 コマ 4 段まで（歌の立ち上がり τ 0.25 秒で、明るさ 0.35 あたりの 1 コマが約 3 段。丸めで 1 段増えることがある）
        bool saved = LedSteady.Enabled;
        int rawStep = 0, rawLed = -1;
        double rawT = 0;
        try
        {
            LedSteady.Enabled = false;
            var raw = EnsembleRun.Run(g.Buf, EnsembleRun.Solid());
            for (int i = 1; i < raw.Count; i++)
                if (raw[i].T > 3)
                    for (int j = 0; j < raw[i].Out.Length; j++)
                    {
                        int st = Math.Abs((int)Math.Round(EnsembleRun.Code(raw[i].Out[j]) * 255) - (int)Math.Round(EnsembleRun.Code(raw[i - 1].Out[j]) * 255));
                        if (st > rawStep) { rawStep = st; rawT = raw[i].T; rawLed = j; }
                    }
        }
        finally { LedSteady.Enabled = saved; }
        T.Check(rawStep <= 4, $"the effect itself (before LedSteady) never steps any LED by more than 4 codes per frame ({rawStep} at {rawT:F2} s, LED {rawLed})");
    }

    /// <summary>
    /// C7: ギター（1 秒ごとにはじく和音。小さな持続音の上）: 光のかたまりが帯に沿って流れる。いちばん明るい LED の場所を
    /// コマごとに追い、前へ進んだコマが、戻ったコマよりずっと多い（端から入り直すのは数えない）。
    /// 合成のはじく音は、打楽器の出来事（スネア・ハイハット）にも取り違えられるので、その出来事から 0.3 秒の間のコマは数えない
    /// </summary>
    public static void TestGuitarFlowsAlongTheStrip()
    {
        var g = Source("strum", 14);
        var evs = new List<(double T, AudioEventKind Kind, double Seen)>();
        var fr = EnsembleRun.Run(g.Buf, EnsembleRun.Solid(), events: evs);
        var drums = evs.Where(e => e.Kind is AudioEventKind.Kick or AudioEventKind.Snare).Select(e => e.T).ToList();
        int forward = 0, backward = 0, wraps = 0, valid = 0;
        int prev = -1;
        foreach (var f in fr.Where(x => x.T > 3))
        {
            var c = f.Out.Select(EnsembleRun.Code).ToArray();
            double median = c.OrderBy(x => x).ElementAt(c.Length / 2);
            int arg = Array.IndexOf(c, c.Max());
            bool ok = c.Max() - median >= 0.015 && !drums.Any(d => f.T >= d - 0.02 && f.T < d + 0.3);
            if (!ok) { prev = -1; continue; }
            valid++;
            if (prev >= 0)
            {
                int d = arg - prev;
                if (d < -c.Length / 2) wraps++;
                else if (d > 0) forward++;
                else if (d < 0) backward++;
            }
            prev = arg;
        }
        double share = forward / (double)Math.Max(1, forward + backward);
        T.Info($"guitar: frames with a moving light {valid}, brightest LED moved forward {forward}, back {backward} ({share:P0} forward), re-entries {wraps}, drum-like events {drums.Count}");
        T.Check(forward >= 60 && share >= 0.85, $"the light flows along the strip ({forward} forward vs {backward} back steps, {share:P0} ≥ 85%)");
    }

    /// <summary>C8: パッドだけ: ゾーン全体がゆっくり呼吸する（周期は BreathSec の ±20%・振れ幅 0.15 まで）。どの LED も 4 コマ以内に向きが戻らない</summary>
    public static void TestPadBreathesSlowly()
    {
        var g = Source("pad", 30);
        var fr = EnsembleRun.Run(g.Buf, EnsembleRun.Solid());
        var seg = fr.Where(f => f.T > 6).ToList();
        var m = seg.Select(f => EnsembleRun.Mean(f)).ToArray();
        // 1 秒でならした値を引いて残る、ゆっくりした波の周期（自己相関の最初の山）
        double mean = m.Average();
        var d = m.Select(x => x - mean).ToArray();
        double best = double.NegativeInfinity;
        int bestLag = 0;
        for (int lag = 90; lag <= 480; lag++)
        {
            double s = 0;
            for (int i = 0; i + lag < d.Length; i++) s += d[i] * d[i + lag];
            s /= d.Length - lag;
            if (s > best) { best = s; bestLag = lag; }
        }
        double period = bestLag / 60.0, amp = m.Max() - m.Min();
        int reversals = 0, revLed = -1;
        double revT = 0;
        for (int j = 0; j < 24; j++)
        {
            int dir = 0, flip = -100;
            for (int i = 1; i < seg.Count; i++)
            {
                int v = (int)Math.Round(EnsembleRun.Code(seg[i].Out[j]) * 255), u = (int)Math.Round(EnsembleRun.Code(seg[i - 1].Out[j]) * 255);
                int s = Math.Abs(v - u) >= 2 ? Math.Sign(v - u) : 0;
                if (s == 0) continue;
                if (dir != 0 && s != dir) { if (i - flip <= 4) { reversals++; revT = seg[i].T; revLed = j; } flip = i; }
                dir = s;
            }
        }
        T.Info($"pad: breathing period {period:F2} s (BreathSec {EnsembleEffect.BreathSec} s without a tempo), swing {amp:F3}, quick reversals {reversals} (last at {revT:F2} s, LED {revLed})");
        T.Check(Math.Abs(period - EnsembleEffect.BreathSec) <= 0.2 * EnsembleEffect.BreathSec, $"the pad breathes with a {EnsembleEffect.BreathSec} s period ±20% ({period:F2} s)");
        T.Check(amp > 0.01 && amp <= 0.15, $"the breathing is gentle (swing {amp:F3} ≤ 0.15)");
        T.Check(reversals == 0, $"no LED reverses direction within 4 frames ({reversals})");
    }

    /// <summary>C9: 金管: 同じ色合いのまま白っぽくなる（鮮やかさ ÷ 明るさが、選んだ色より 10% 以上小さい。色相は ±3°）</summary>
    public static void TestBrassTurnsWhiter()
    {
        var g = Source("brass", 12);
        var fr = EnsembleRun.Run(g.Buf, EnsembleRun.Solid());
        var refLab = OkLab.FromRgb(Rgb.Parse(EnsembleRun.Violet));
        double refRatio = refLab.Chroma / refLab.L;
        var ratios = new List<double>();
        double hue = 0;
        foreach (var f in fr.Where(x => x.T > 4))
            foreach (var c in f.Light)
            {
                var lab = EnsembleRun.LabOf(c);
                if (lab.L < 0.1f) continue;
                ratios.Add(lab.Chroma / lab.L / refRatio);
                hue = Math.Max(hue, EnsembleRun.HueDiffDeg(lab.Hue, VioletHue));
            }
        double med = ratios.OrderBy(x => x).ElementAt(ratios.Count / 2);
        T.Info($"brass: chroma/L relative to the chosen colour median {med:F3} (min {ratios.Min():F3}), worst hue {hue:F2}°");
        T.Check(med <= 0.9, $"brass makes the chosen colour whiter (chroma/L {med:P0} of the chosen colour ≤ 90%)");
        T.Check(hue <= 3, $"brass keeps the hue ({hue:F2}° ≤ 3°)");
    }

    /// <summary>
    /// C10: 小さいゾーン（1 個・4 個）と 24 個のテープで、音の多い曲（140 BPM）・「激しい点滅を抑える」: 強い点滅（ゾーンの平均が 0.1 秒以内に 0.2 以上上がる）は、
    /// どの 1 秒にも 3 回まで
    /// </summary>
    public static void TestFlashLimitOnEveryZoneSize()
    {
        var g = new SignalGen(Fs, 30, seed: 140);
        g.DenseMix(140, 0.5, 29.5);
        g.VocalTone(8, 20, InstrumentSources.VocalNotes, 0.6);
        for (double t = 12; t < 24; t += 0.43) g.Strum(t, new[] { 196.0, 246.9, 293.7, 392 }, 0.06);
        foreach (int leds in new[] { 1, 4, 24 })
        foreach (bool mono in new[] { true, false })
        {
            var m = EnsembleRun.Solid() with { Palette = mono ? Palettes.Solid : Palettes.Rainbow };
            var fr = EnsembleRun.Run(g.Buf, m, EnsembleRun.Strip(leds)).Where(f => f.T > 3).ToList();
            var fl = EnsembleRun.StrongFlashes(fr, 0, leds);
            int most = EnsembleRun.MaxPerSecond(fl);
            T.Info($"{leds,2} LEDs {(mono ? "Solid" : "Rainbow")}: strong flashes {fl.Count} in {fr.Count / 60.0:F0} s, most in any 1 s {most}");
            T.Check(most <= 3, $"{leds} LEDs {(mono ? "Solid" : "Rainbow")}: ≤ 3 strong flashes in every 1 s window ({most})");
        }
    }

    /// <summary>
    /// C11: 更新の遅いデバイス（メモリ）: 一瞬の光（キック・ハイハットの光）はなく、ゾーンの平均が 0.1 秒で 10% より大きく上がるのは
    /// 拍のふくらみ（EnsembleEffect.SwellAmp）のときだけ。ふくらみは SwellGapSec 以上あけ、上がり幅は 20% まで
    /// </summary>
    /// <remarks>
    /// 6.15.8 までは「0.1 秒で 10% より大きく上がらない」だけを確かめていた（遅いデバイスは明るさをゆっくり変えるだけ）。
    /// 本物の曲では、遅いデバイスの「楽器で光る」が 84〜95% の時間止まって見え（直接書き込むグラボで 43%、単色で 86%）、
    /// グラボがずっと同じ色に見えると言われたので、拍で明るさをふくらませるようにした（1 秒に 2.4 回まで。光感受性の目安の 3 回より少ない）。
    /// それ以外の上がり（キック・ハイハットの一瞬の光・場所の動き）は、これまでどおり 10% までにする
    /// </remarks>
    public static void TestSlowDeviceStaysCalm()
    {
        var buf = Everything();
        foreach (var pal in new[] { Palettes.Solid, Palettes.Rainbow })
        {
            var fr = EnsembleRun.Run(buf, EnsembleRun.Solid() with { Palette = pal }, EnsembleRun.Dram()).Where(f => f.T > 3).ToList();
            double worst = 0, worstOther = 0, worstT = 0, worstOtherT = 0;
            var m = fr.Select(f => EnsembleRun.Mean(f)).ToArray();
            for (int i = 6; i < m.Length; i++)
            {
                double r = m[i] - m.Skip(i - 6).Take(6).Min();
                // 拍のふくらみ（このコマから 0.1 秒前までに始まった）による上がりか
                bool swell = fr[i].T - fr[i].AccentT is >= -0.02 and <= 0.12;
                if (r > worst) { worst = r; worstT = fr[i].T; }
                if (!swell && r > worstOther) { worstOther = r; worstOtherT = fr[i].T; }
            }
            var accents = fr.Select(f => f.AccentT).Where(double.IsFinite).Distinct().OrderBy(x => x).ToList();
            double minGap = accents.Zip(accents.Skip(1), (a, b) => b - a).DefaultIfEmpty(double.PositiveInfinity).Min();
            T.Info($"DRAM {pal}: largest zone-mean rise within 0.1 s {worst:F3} (at {worstT:F2} s), apart from beat swells {worstOther:F3} (at {worstOtherT:F2} s); {accents.Count} swells, closest {minGap:F2} s apart");
            if (Environment.GetEnvironmentVariable("ENSEMBLE_DIAG") == "slow") foreach (var f in fr.Where(x => Math.Abs(x.T - worstOtherT) < 0.3)) T.Info($"{f.T:F3} v{f.Vocal:F2} p{f.Pad:F2} br{f.Brass:F2} g{f.Guitar:F2} acc{f.AccentT:F2} " + string.Join(" ", f.Out.Take(2).Select(o => $"{o.R},{o.G},{o.B}")));
            T.Check(worstOther <= 0.10, $"slow device {pal}: apart from beat swells, no rise above 10% within 100 ms ({worstOther:F3})");
            T.Check(worst <= 0.20, $"slow device {pal}: a beat swell rises at most 20% ({worst:F3})");
            T.Check(accents.Count >= 10 && minGap >= EnsembleEffect.SwellGapSec - 1e-6, $"slow device {pal}: beat swells happen ({accents.Count}) and at least {EnsembleEffect.SwellGapSec} s apart ({minGap:F2})");
        }
    }

    /// <summary>
    /// C11b: 直接書き込むグラボ（明るさを変えてもちかちかしない）は、下地を低くして拍のふくらみを大きくする（メモリと同じ大きさでは、
    /// ほとんど止まって見えた）。ふくらみはメモリより大きく、「激しい点滅を抑える」がオンなら FlashLimiter.MinGapSec 以上あけ、
    /// 強い点滅（0.1 秒で平均 +0.2）は 1 秒に 3 回まで。メモリは今までどおり（C11）
    /// </summary>
    public static void TestDirectGpuSwellIsVisible()
    {
        var buf = Everything();
        var pny = new RgbDevice(7, "PNY GeForce RTX 5070Ti ARGB Epic-X OC", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }, "I2C: Nvidia NvAPI I2C on GPU 0, address 0x60") { DirectGpu = true };
        var music = EnsembleRun.Solid() with { Palette = Palettes.Rainbow, ReduceFlashing = true };
        double MedianSwell(List<EnsembleFrame> fr)
        {
            // ふくらみの始まりから 0.15 秒の、いちばん明るい平均 − 始まる直前の平均（送った値）
            var rises = new List<double>();
            foreach (double a in fr.Select(f => f.AccentT).Where(double.IsFinite).Distinct())
            {
                var before = fr.LastOrDefault(f => f.T < a);
                var during = fr.Where(f => f.T >= a && f.T <= a + 0.15).ToList();
                if (before is null || during.Count == 0) continue;
                rises.Add(during.Max(f => EnsembleRun.Mean(f)) - EnsembleRun.Mean(before));
            }
            rises.Sort();
            return rises.Count == 0 ? 0 : rises[rises.Count / 2];
        }
        var direct = EnsembleRun.Run(buf, music, pny).Where(f => f.T > 3).ToList();
        var dram = EnsembleRun.Run(buf, music, EnsembleRun.Dram()).Where(f => f.T > 3).ToList();
        double sDirect = MedianSwell(direct), sDram = MedianSwell(dram);
        var accents = direct.Select(f => f.AccentT).Where(double.IsFinite).Distinct().OrderBy(x => x).ToList();
        double minGap = accents.Zip(accents.Skip(1), (a, b) => b - a).DefaultIfEmpty(double.PositiveInfinity).Min();
        int most = FlashGuardTests.MostPerSecond(direct.Select(f => f.T).ToList(), direct.Select(f => EnsembleRun.Mean(f)).ToList());
        T.Info($"direct GPU: {accents.Count} swells, closest {minGap:F2} s apart, median swell {sDirect:F3} (DRAM {sDram:F3}), most strong flashes in 1 s {most}");
        T.Check(accents.Count >= 10 && minGap >= FlashLimiter.MinGapSec - 1e-6, $"direct GPU: beat swells happen ({accents.Count}) and at least {FlashLimiter.MinGapSec} s apart with reduce flashing ({minGap:F2})");
        T.Check(sDirect >= 0.15 && sDirect >= 2 * sDram, $"direct GPU: the swell is clearly visible ({sDirect:F3}) and larger than on DRAM ({sDram:F3})");
        T.Check(most <= 3, $"direct GPU: ≤ 3 strong flashes in every 1 s window ({most})");

        // 速い拍（テンポをつかめない 0.2〜0.4 秒おきのキック・スネア）: 「激しい点滅を抑える」がオンなら、拍の時刻でもふくらみを描き始めたコマでも
        // DirectLimitedGapSec 以上あけ、強い点滅は 1 秒に 3 回まで（FlashGuard に頼らない）。オフなら DirectSwellGapSec まで詰める
        var fast = new SignalGen(Fs, 20, seed: 13);
        fast.PadChord(0.5, 19.8, InstrumentSources.PadChord1);
        fast.RandomHits(0.5, 19.5, meanGap: 0.3, minGap: 0.2);
        (int N, double Beat, double Frame, int Most) Gaps(bool reduce)
        {
            var fr = EnsembleRun.Run(fast.Buf, music with { ReduceFlashing = reduce }, pny).Where(f => f.T > 3).ToList();
            var beats = fr.Select(f => f.AccentT).Where(double.IsFinite).Distinct().OrderBy(x => x).ToList();
            var starts = new List<double>();
            for (int i = 1; i < fr.Count; i++) if (fr[i].AccentT != fr[i - 1].AccentT && double.IsFinite(fr[i].AccentT)) starts.Add(fr[i].T);
            static double Closest(List<double> v) => v.Zip(v.Skip(1), (a, b) => b - a).DefaultIfEmpty(double.PositiveInfinity).Min();
            int m = FlashGuardTests.MostPerSecond(fr.Select(f => f.T).ToList(), fr.Select(f => EnsembleRun.Mean(f)).ToList());
            return (beats.Count, Closest(beats), Closest(starts), m);
        }
        var on = Gaps(true);
        var off = Gaps(false);
        T.Info($"direct GPU, fast hits: reduce flashing on {on.N} swells, closest {on.Beat:F3} s (beats) / {on.Frame:F3} s (frames), most strong flashes in 1 s {on.Most}; off {off.N} swells, closest {off.Beat:F3} / {off.Frame:F3} s");
        T.Check(on.N >= 20 && on.Beat >= EnsembleEffect.DirectLimitedGapSec - 1e-6 && on.Frame >= EnsembleEffect.DirectLimitedGapSec - 1e-6 && on.Most <= 3,
            $"direct GPU, fast hits, reduce flashing on: swells ≥ {EnsembleEffect.DirectLimitedGapSec:F3} s apart in beat time ({on.Beat:F3}) and frame time ({on.Frame:F3}), ≤ 3 strong flashes in 1 s ({on.Most})");
        T.Check(off.Beat >= EnsembleEffect.DirectSwellGapSec - 1e-6 && off.Beat < EnsembleEffect.DirectLimitedGapSec && off.N > on.N,
            $"direct GPU, fast hits, reduce flashing off: swells may come closer ({off.Beat:F3} s, ≥ {EnsembleEffect.DirectSwellGapSec} s) and more often ({off.N} vs {on.N})");
    }

    /// <summary>
    /// C12: 場面で光の形を切り替える（場面なし → 歌 10 秒 → ギター 20 秒。パッドと低い音が続く曲）: 切り替えの間も、1 コマの変化は
    /// 切り替えない場合のいちばん大きな変化 + 1 段まで。場面の重みは 2.5 秒かけて移り、ドラムの重みは 1 を超えない
    /// </summary>
    public static void TestScenePatternSwitchIsSmooth()
    {
        var g = new SignalGen(Fs, 30, seed: 12);
        // 続くパッドと、続く低い音（はじいたベースの音は、合成音ではキックの出来事にも取り違えられ、場面ごとの重みの違いがキックの光の差になるので使わない）
        g.PadChord(0.5, 29.8, InstrumentSources.PadChord1);
        g.Tone(55, 0.12, 0.5, 29.8);
        g.Tone(110, 0.05, 0.5, 29.8);
        var sched = SceneColorTests.Schedule((10.0, SceneGroup.Vocal), (20.0, SceneGroup.Guitar));
        var evs = new List<(double T, AudioEventKind Kind, double Seen)>();
        var on = EnsembleRun.Run(g.Buf, EnsembleRun.Solid(scenes: true), scenes: sched, events: evs);
        var hits = evs.Where(e => e.Kind is AudioEventKind.Kick or AudioEventKind.Snare).Select(e => e.T).ToList();
        var off = EnsembleRun.Run(g.Buf, EnsembleRun.Solid(scenes: true));
        static int MaxStep(List<EnsembleFrame> fr, double a, double b)
        {
            int best = 0;
            for (int i = 1; i < fr.Count; i++)
            {
                if (fr[i].T < a || fr[i].T >= b) continue;
                for (int j = 0; j < fr[i].Out.Length; j++)
                    best = Math.Max(best, Math.Abs((int)Math.Round(EnsembleRun.Code(fr[i].Out[j]) * 255) - (int)Math.Round(EnsembleRun.Code(fr[i - 1].Out[j]) * 255)));
            }
            return best;
        }
        int baseStep = MaxStep(off, 3, 30);
        int sw = Math.Max(MaxStep(on, 9.9, 13), MaxStep(on, 19.9, 23));
        double diff = Math.Abs(on.Where(f => f.T > 14 && f.T < 19).Average(f => EnsembleRun.Mean(f)) - off.Where(f => f.T > 14 && f.T < 19).Average(f => EnsembleRun.Mean(f)));
        T.Info($"largest per-frame step: no switch {baseStep} codes, during the switches {sw} codes; vocal pattern vs rhythm zone mean differs by {diff:F3}");
        T.Check(sw <= baseStep + 1, $"switching patterns adds at most 1 code per frame ({sw} vs {baseStep})");
        // もっと細かく: 切り替える場合と切り替えない場合のゾーンの平均の差が、1 コマで変わる量（重みの移り変わりの分だけが残る）
        double extra = 0;
        int used = 0;
        for (int i = 1; i < Math.Min(on.Count, off.Count); i++)
        {
            if (!((on[i].T >= 9.9 && on[i].T < 13) || (on[i].T >= 19.9 && on[i].T < 23))) continue;
            // キックなどの光は場面で強さが違う（重みが違う）ので、その光の間は数えない（切り替えそのものの変化だけを見る）
            if (hits.Any(h => on[i].T >= h - 0.02 && on[i].T < h + 0.6)) continue;
            double d = (EnsembleRun.Mean(on[i]) - EnsembleRun.Mean(off[i])) - (EnsembleRun.Mean(on[i - 1]) - EnsembleRun.Mean(off[i - 1]));
            extra = Math.Max(extra, Math.Abs(d) * 255);
            used++;
        }
        T.Info($"extra per-frame change of the zone mean caused by the switch: {extra:F2} codes over {used} frames (frames within 0.6 s after {hits.Count} kick/snare events skipped)");
        T.Check(used >= 60 && extra <= 1, $"the pattern switch itself changes the zone mean by at most 1 code per frame ({extra:F2})");
        T.Check(EnsembleEffect.Patterns.All(p => p.Wk <= 1 && p.Ws <= 1 && p.Wh <= 1), "drum weights never exceed 1 in any scene (the flash budget does not depend on the scene)");
        var mid = EnsembleEffect.Blend(new MusicScene(SceneGroup.Vocal, SceneGroup.None, 10, 1), 11.25);
        var a = EnsembleEffect.PatternOf(SceneGroup.None);
        var b = EnsembleEffect.PatternOf(SceneGroup.Vocal);
        T.Check(Math.Abs(mid.Wv - 0.5f * (a.Wv + b.Wv)) < 1e-4 && EnsembleEffect.Blend(new MusicScene(SceneGroup.Vocal, SceneGroup.None, 10, 1), 13).Wv == b.Wv,
            $"pattern weights blend over MusicScene.FadeSec (half-way at 1.25 s: Wv {mid.Wv:F2})");
    }

    /// <summary>
    /// 「AI で楽器を聞き分ける」がオフなら、場面が来ても光の形は変わらない（いつもリズム）。単色でも、場面の色がないので色も同じ
    /// </summary>
    public static void TestScenesOffMeansRhythmPattern()
    {
        var buf = Everything(10);
        var sched = SceneColorTests.Schedule((2.0, SceneGroup.Vocal), (6.0, SceneGroup.Guitar));
        var a = EnsembleRun.Run(buf, EnsembleRun.Solid(), scenes: sched);
        var b = EnsembleRun.Run(buf, EnsembleRun.Solid());
        bool same = a.Count == b.Count && a.Zip(b).All(x => x.First.Out.SequenceEqual(x.Second.Out));
        T.Check(same, "scenes off: injected scenes change nothing in Ensemble (byte-identical output)");
    }

    /// <summary>
    /// C13: 更新の遅いデバイス（直接書き込むグラボ・メモリ・OpenRGB を通すグラボ）でも、「楽器で光る」が拍に合わせて動いて見える。
    /// 本物の送り方（RgbDeviceChannel: 送る回数の上限・早めに送る・小さな変化は送らない）で送った色で確かめる（合成した 120 BPM の曲、20 秒）:
    ///   ・2 秒の間、送った色がまったく変わらない時間は 10% まで（6.15.8 は本物の曲で 42〜95%。グラボが同じ色のまま、と言われた）
    ///   ・明るさを変えてよいデバイス: 明るくなった送信の 70% 以上が拍（± 80 ms）。明るくなる間は 0.4 秒以上あく（1 秒に 2.5 回まで）
    ///   ・明るさを変えないグラボ: 明るさ（いちばん強い成分）は変えず、拍で白へ寄せた色を送り、0.5 秒以内に元の色へ戻す送信がある
    /// </summary>
    public static void TestSlowDevicesShowTheBeat()
    {
        var (l, _, beats) = SlowDeviceBenchTests.Synth(120, 20, 120);
        const double T0 = 1000, Tick = 1.0 / 60, From = 6;
        var lanes = new List<(string Name, RgbDeviceChannel Ch, List<(double T, Rgb[] F)> Sends)>();
        foreach (var dn in new[] { "direct", "dram", "gpu" })
            foreach (var pal in new[] { Palettes.Solid, Palettes.Rainbow })
            {
                var dev = SlowDeviceBenchTests.DeviceOf(dn);
                var ch = new RgbDeviceChannel(dev, T0) { SharedColor = new SharedColor() };
                ch.Configure(new RgbConfig { Mode = RgbModes.Music, MusicStyle = MusicStyles.Ensemble, Brightness = 100, StaticColor = EnsembleRun.Violet },
                    ResolvedMusic.Default with { Effect = MusicStyles.Ensemble, Palette = pal }, new RgbDeviceSettings { Name = dev.Name }, false);
                lanes.Add(($"{dn}/{pal}", ch, new()));
            }
        var e = new AudioEngine(Fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
        int pos = 0;
        double total = l.Length / (double)Fs;
        for (int k = 0; k / 60.0 + 0.15 < total; k++)
        {
            double sec = k * Tick;
            while (pos < l.Length && pos / (double)Fs < sec + 0.1)
            {
                int n = Math.Min(480, l.Length - pos);
                e.Push(new ReadOnlySpan<float>(l, pos, n), T0 + pos / (double)Fs);
                pos += n;
            }
            foreach (var (_, ch, sends) in lanes)
            {
                ch.Prepare(T0 + sec, Tick, e.Features, e.Events, e.Beat, null, false, false);
                if (!ch.Render()) continue;
                var reason = ch.ShouldSend(T0 + sec, Tick, true);
                if (reason == SendReason.None) continue;
                ch.MarkSent(T0 + sec, Tick, reason, 1);
                sends.Add((sec + ch.EffectiveLatencyMs / 1000.0, (Rgb[])ch.Frame.Clone()));
            }
        }
        static double Max(Rgb c) => Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
        static double Min(Rgb c) => Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
        foreach (var (name, ch, sends) in lanes)
        {
            var s = sends.Where(x => x.T >= From).ToList();
            // 2 秒の窓で、送った色が 1 度も変わらなかった割合
            int still = 0, wins = 0;
            for (double w = From; w + 2 <= total - 0.2; w += 0.5)
            {
                wins++;
                var inWin = sends.Where(x => x.T >= w && x.T < w + 2).ToList();
                var prev = sends.LastOrDefault(x => x.T < w).F;
                bool change = inWin.Any(x => prev is null || !x.F[0].Equals(prev[0])) && inWin.Count > 0;
                if (!change) still++;
            }
            double stillShare = still / (double)Math.Max(1, wins);
            if (!ch.Device.IsLowWrite)
            {
                // 明るくなった送信（LED 0 のいちばん強い成分が 0.03 以上上がった）
                var rises = new List<double>();
                for (int i = 1; i < s.Count; i++)
                    if (Max(s[i].F[0]) - Max(s[i - 1].F[0]) >= 0.03) rises.Add(s[i].T);
                double onBeat = rises.Count == 0 ? 0 : rises.Count(t => beats.Any(b => Math.Abs(b - t) < 0.08)) / (double)rises.Count;
                double gap = rises.Zip(rises.Skip(1), (a, b) => b - a).DefaultIfEmpty(1).Min();
                T.Info($"{name,-16} sends {s.Count / (total - From):F1}/s  still {stillShare * 100:F0}%  brighter sends {rises.Count} ({onBeat * 100:F0}% on the beat, closest {gap:F2} s apart)");
                T.Check(stillShare <= 0.10, $"{name}: Ensemble does not look frozen ({stillShare * 100:F0}% of 2 s windows without a change)");
                T.Check(rises.Count >= 10 && onBeat >= 0.7, $"{name}: brighter sends come on the beat ({onBeat * 100:F0}% of {rises.Count})");
                T.Check(gap >= 0.4, $"{name}: brighter sends at least 0.4 s apart ({gap:F2} s)");
            }
            else
            {
                // 白へ寄せた送信（いちばん弱い成分が上がった）と、0.5 秒以内に戻す送信
                int whiter = 0, back = 0;
                double bright = s.Count == 0 ? 0 : s.Max(x => Math.Abs(Max(x.F[0]) - Max(s[0].F[0])));
                for (int i = 1; i < s.Count; i++)
                {
                    if (Min(s[i].F[0]) - Min(s[i - 1].F[0]) < 0.03) continue;
                    whiter++;
                    double floor = Min(s[i - 1].F[0]) + 0.01;
                    if (s.Skip(i + 1).TakeWhile(x => x.T <= s[i].T + 0.5).Any(x => Min(x.F[0]) <= floor)) back++;
                }
                T.Info($"{name,-16} sends {s.Count / (total - From):F1}/s  still {stillShare * 100:F0}%  whiter sends {whiter}, returned within 0.5 s {back}, brightness change {bright:F3}");
                T.Check(stillShare <= 0.10, $"{name}: Ensemble does not look frozen ({stillShare * 100:F0}% of 2 s windows without a change)");
                T.Check(whiter >= 10 && back >= whiter - 1, $"{name}: beat accents turn the colour whiter and come back within 0.5 s ({back} of {whiter})");
            }
        }
    }

    /// <summary>時刻ごとの、目で見た明るさの上がり幅（ゾーンの平均。0.15 秒以内のいちばん明るいコマ − 直前 70 ミリ秒の平均。下がったときは 0）</summary>
    private static List<double> Rises(List<EnsembleFrame> fr, IEnumerable<double> times, int leds = 24)
    {
        var list = new List<double>();
        foreach (double tk in times)
        {
            var before = EnsembleRun.Between(fr, tk - 0.08, tk - 0.01).ToList();
            var after = EnsembleRun.Between(fr, tk - 0.01, tk + 0.15).ToList();
            if (before.Count == 0 || after.Count == 0) continue;
            list.Add(Math.Max(0, after.Max(f => EnsembleRun.Perceived(f, 0, leds)) - before.Average(f => EnsembleRun.Perceived(f, 0, leds))));
        }
        return list;
    }

    private static double MeanRise(List<EnsembleFrame> fr, IEnumerable<double> times, int leds = 24) => Rises(fr, times, leds).DefaultIfEmpty(0).Average();

    /// <summary>設定画面で強さを選んだときと同じ値の「楽器で光る」（単色・点滅を抑える）</summary>
    private static ResolvedMusic AtIntensity(string intensity)
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = MusicStyles.Ensemble };
        MusicTuning.ApplyIntensity(cfg.Music, intensity);
        return MusicTuning.Resolve(cfg) with { Palette = Palettes.Solid, ReduceFlashing = true, SceneColors = false };
    }

    /// <summary>
    /// C14: キック・スネアがはっきり光る（6.15.11。「楽器で光る」はメリハリが足りないと言われた。6.15.10 はキックの上がり幅が 0.2 ほどで、
    /// ハイハットの粒のほうがキックより明るかった）:
    ///   ・キックだけ: キックの光（ゾーンの平均・目で見た明るさ）は、ハイハットだけのときの粒（いちばん明るい LED）より 0.1 以上明るい
    ///   ・音の多い曲（DenseMix・歌・ギター・パッド・金管）でも、キックでゾーンの平均が 0.3 以上上がり、スネアでも 0.2 以上上がる
    /// </summary>
    public static void TestKicksAndSnaresStandOut()
    {
        var k = Source("kick", 12);
        var fk = EnsembleRun.Run(k.Buf, EnsembleRun.Solid());
        double kickPeak = k.KickTimes.Where(t => t > 3).Average(t => EnsembleRun.Between(fk, t - 0.01, t + 0.15).Max(f => EnsembleRun.Perceived(f)));
        var h = Source("hat", 12);
        var fh = EnsembleRun.Run(h.Buf, EnsembleRun.Solid()).Where(f => f.T > 3).ToList();
        double hatPeak = fh.Max(f => f.Out.Max(o => OkLab.LinearToSrgb(EnsembleRun.Code(o))));
        T.Info($"kick-only zone-mean peak {kickPeak:F3}, brightest hat sparkle LED {hatPeak:F3} (perceived)");
        T.Check(kickPeak >= hatPeak + 0.1, $"a kick lights brighter than a hat sparkle ({kickPeak:F3} ≥ {hatPeak:F3} + 0.1)");

        var g = new SignalGen(Fs, 20, seed: 9);
        g.DenseMix(118, 0.5, 19.8);
        g.VocalTone(2, 8, InstrumentSources.VocalNotes, 0.6);
        for (double t = 6; t < 14; t += 0.5) g.Strum(t, new[] { 196.0, 246.9, 293.7, 392 }, 0.06);
        g.PadChord(10, 16, InstrumentSources.PadChord1);
        var fr = EnsembleRun.Run(g.Buf, EnsembleRun.Solid());
        var snares = g.SnareTimes.Where(t => t > 3).ToHashSet();
        double kick = MeanRise(fr, g.KickTimes.Where(t => t > 3 && !snares.Contains(t)));
        double snare = MeanRise(fr, snares);
        T.Info($"dense mix: perceived zone-mean rise at kicks {kick:F3}, at kick + snare {snare:F3}");
        T.Check(kick >= 0.3, $"dense mix: a kick raises the zone mean by ≥ 0.3 ({kick:F3})");
        T.Check(snare >= 0.2, $"dense mix: a snare raises the zone mean by ≥ 0.2 ({snare:F3})");
    }

    /// <summary>
    /// C15: 強さの設定で、キックの光の大きさが変わる（「控えめ」&lt;「ふつう」&lt;「強め」≤「最大」。6.15.10 は「強め」と「最大」が同じで、
    /// 下地だけが変わっていた）。どの強さでも、「激しい点滅を抑える」がオンなら強い点滅は 1 秒に 3 回まで
    /// </summary>
    public static void TestIntensityScalesTheHits()
    {
        var g = new SignalGen(Fs, 16, seed: 21);
        g.DenseMix(124, 0.5, 15.8);
        var kicks = g.KickTimes.Where(t => t > 3 && !g.SnareTimes.Contains(t)).ToList();
        var rises = new List<double>();
        foreach (var i in Intensities.All)
        {
            var fr = EnsembleRun.Run(g.Buf, AtIntensity(i));
            rises.Add(MeanRise(fr, kicks));
            int most = EnsembleRun.MaxPerSecond(EnsembleRun.StrongFlashes(fr.Where(f => f.T > 3).ToList(), 0, 24));
            T.Check(most <= 3, $"{i}: ≤ 3 strong flashes in every 1 s window with reduce flashing ({most})");
        }
        T.Info("kick rise by intensity " + string.Join(" / ", Intensities.All.Zip(rises, (i, r) => $"{i} {r:F3}")));
        T.Check(rises[0] + 0.03 < rises[1] && rises[1] + 0.03 < rises[2] && rises[3] >= rises[2] - 0.005,
            $"the kick grows with the intensity setting (Subtle {rises[0]:F3} < Moderate {rises[1]:F3} < High {rises[2]:F3} ≤ Intense {rises[3]:F3})");
    }

    /// <summary>
    /// C16: 「激しい点滅を抑える」がオンでテンポをつかんでいるとき、拍の間の音（ブリッジミュートの刻み）が点滅の制限を先に使って、
    /// 拍のキック・スネアが弱くなることがない（6.15.10 は、先に来た音から順に制限を使ったので、ロックの刻みの曲で拍の間だけが光り続けることがあった）。
    /// 拍の頭（キック・スネア）の上がり幅は 0.25 以上で、拍の間の刻みの上がり幅の 2 倍以上。
    /// 拍の間でも、拍の頭よりずっと大きな音（ゲームの銃声など）は今までどおり光る
    /// </summary>
    public static void TestBeatKeepsTheFlashBudget()
    {
        var s = GuitarMixes.Rock();
        // A メロ（ブリッジミュートの 8 分の刻み）の、テンポをつかんだあとの所
        double p = 60 / s.Bpm, t0 = 0.5, from = 6, to = t0 + 10 * 4 * p;
        var on = new List<double>();
        var off = new List<double>();
        for (double t = t0; t < to - 1e-9; t += p)
        {
            if (t < from) continue;
            on.Add(t);
            double o = t + p / 2;
            if (!s.Kicks.Any(x => Math.Abs(x - o) < 0.02)) off.Add(o);
        }
        var fr = EnsembleRun.Run(s.With, EnsembleRun.Solid());
        double beat = MeanRise(fr, on), between = MeanRise(fr, off);
        T.Info($"rock verse, reduce flashing: rise on the beat {beat:F3}, between beats (palm-muted chugs) {between:F3}");
        T.Check(beat >= 0.25 && beat >= 2 * between, $"the beat keeps its flash (on the beat {beat:F3} ≥ 0.25 and ≥ 2× between beats {between:F3})");

        // 拍の間の大きな音: 120 BPM のドラムの上に、拍の間（裏拍の少し前）に 9 dB 大きな破裂音
        var g = new SignalGen(Fs, 20, seed: 31);
        g.Loop(120, 0.5, 19.5, snare: true, kickAmp: 0.4);
        var shots = new List<double>();
        for (double t = 6.85; t < 19; t += 1.5) { g.Snare(t, 1.0); g.Kick(t, 1.0); shots.Add(t); }
        var fg = EnsembleRun.Run(g.Buf, EnsembleRun.Solid());
        var ps = Rises(fg, shots);
        int lit = ps.Count(x => x >= 0.2);
        T.Info($"loud off-beat hits over a beat: {lit} of {ps.Count} raise the zone mean by ≥ 0.2 (mean {ps.Average():F3})");
        T.Check(ps.Count >= 7 && lit >= ps.Count - 1, $"a much louder hit between beats still flashes ({lit} of {ps.Count})");
    }

    /// <summary>
    /// C17: ベースの音の出だし（太鼓の「カツッ」という高い音がない）をキックと取り違えても、大きくは光らせない
    /// （キックを大きくしたので、ベースだけの曲でゾーン全体が点滅しないように）。本物のキックの光は、その 1.6 倍以上
    /// </summary>
    public static void TestBassNotesDoNotFlashLikeKicks()
    {
        var b = Source("bass", 12);
        var evs = new List<(double T, AudioEventKind Kind, double Seen)>();
        var fb = EnsembleRun.Run(b.Buf, EnsembleRun.Solid(), events: evs);
        var bassKicks = evs.Where(e => e.Kind == AudioEventKind.Kick && e.T > 3).Select(e => e.T).ToList();
        double bass = MeanRise(fb, bassKicks);
        var k = Source("kick", 12);
        double kick = MeanRise(EnsembleRun.Run(k.Buf, EnsembleRun.Solid()), k.KickTimes.Where(t => t > 3));
        T.Info($"bass line: {bassKicks.Count} kick events, rise {bass:F3}; real kicks rise {kick:F3}");
        T.Check(bassKicks.Count == 0 || kick >= 1.6 * bass, $"bass notes taken for kicks light much less than real kicks ({bass:F3} vs {kick:F3}, ≥ 1.6×)");
    }

#if BEATHUE_BETA
    public static void TestFixedBrightnessTracksInstrumentWithoutDrums()
    {
        var c = new EffectContext { SlowDevice = true, FixedBrightness = true, F0 = 1, Dt = 0.1f,
            Music = ResolvedMusic.Default with { Effect = MusicStyles.Ensemble } };
        var effect = new EnsembleEffect(slow: true);
        effect.Reset(c);
        c.Frame.Activity = 1;
        var output = new LinColor[1];
        LinColor Phrase(float vocal, float guitar)
        {
            c.Frame.Vocal = vocal;
            c.Frame.GuitarKeys = guitar;
            for (int frame = 0; frame < 60; frame++)
            {
                c.TPrev = c.T;
                c.T += c.Dt;
                effect.Update(c);
            }
            effect.Render(c, new LedSegment(0, 1, false, false), output);
            return output[0];
        }
        var voice = Phrase(1, 0);
        var guitar = Phrase(0, 1);
        T.Check(voice != guitar && effect.SlowState.Dominant == 2 && double.IsNegativeInfinity(effect.SlowState.AccentT),
            "fixed-brightness GPU changes from the vocal colour to the guitar colour without a beat");
    }

    public static void TestDirectGpuShowsInstrumentPosition()
    {
        var gpu = new RgbDevice(7, "Direct GPU", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }, "") { DirectGpu = true };
        foreach (var source in new[] { "bass", "strum" })
        {
            var signal = Source(source, 12);
            var frames = EnsembleRun.Run(signal.Buf, EnsembleRun.Solid(), gpu);
            var spread = frames.Where((f, i) => f.T > 3 && i % 6 == 0)
                .Select(f => f.Out.Skip(4).Take(20).Max(EnsembleRun.Code) - f.Out.Skip(4).Take(20).Min(EnsembleRun.Code))
                .OrderBy(x => x).ToArray();
            double upper = spread[spread.Length * 9 / 10];
            T.Check(upper >= 0.05, $"direct GPU {source}: spatial brightness range at 10 Hz (90th percentile {upper:F3})");
        }
    }

    public static void TestSmallStripShowsBassPosition()
    {
        var g = Source("bass", 12);
        var frames = EnsembleRun.Run(g.Buf, EnsembleRun.Solid(), EnsembleRun.Strip(4));
        var ratios = g.BassNoteTimes.Where(t => t > 3)
            .SelectMany(t => EnsembleRun.Between(frames, t + 0.08, t + 0.3))
            .Select(f => EnsembleRun.Mean(f, 0, 1) / Math.Max(1e-3, EnsembleRun.Mean(f, 3, 4)))
            .OrderBy(r => r).ToArray();
        double median = ratios[ratios.Length / 2];
        T.Check(median >= 1.5, $"four LEDs: bass lights the low end (median end-to-end ratio {median:F2})");
    }
#endif

}

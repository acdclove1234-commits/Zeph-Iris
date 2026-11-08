// ==================================================
// FaintSoundTests.cs - 小さな音で光りすぎないか（ハイハット・スネア・立ち上がりの検出）
//   ふつうのドラムに 20 dB 小さい「チッ」を混ぜた曲・低音の大きい曲に小さなハイハットだけの曲で、
//   小さな音から出るイベントの数を数える。ふつうの大きさの音は今までどおり検出すること
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class FaintSoundTests
{
    private const int Fs = 48000;

    /// <summary>truth のどれかから tol 秒以内のイベントの数</summary>
    private static int Near(IEnumerable<AudioEvent> events, IReadOnlyList<double> truthSec, double t0, double tol = 0.03)
        => events.Count(e => truthSec.Any(t => Math.Abs(e.T - (t0 + t)) <= tol));

    /// <summary>ふつうのドラム（キック・スネア・裏拍のハイハット）＋ 小さな「チッ」（平均 4 回/秒、ほかの音から 60 ミリ秒以上離す）</summary>
    private static (SignalGen G, List<double> Faint) DrumsWithFaintTicks(double faintAmp, int seed)
    {
        var g = new SignalGen(Fs, 24, seed);
        g.Loop(120, 0.3, 24, hats: true, snare: true);
        var rng = new Random(seed + 100);
        var loud = g.KickTimes.Concat(g.SnareTimes).Concat(g.HatTimes).ToList();
        var faint = new List<double>();
        for (double t = 2.0; t < 23.5; t += 0.1 + rng.NextDouble() * 0.3)
        {
            if (loud.Any(x => Math.Abs(x - t) < 0.06)) continue;
            faint.Add(t);
        }
        int before = g.HatTimes.Count;
        foreach (var t in faint) g.Hat(t, faintAmp);
        g.HatTimes.RemoveRange(before, g.HatTimes.Count - before); // 正解のハイハットには入れない
        return (g, faint);
    }

    private static (int LoudHats, int LoudHatsFound, int FaintTicks, int FaintFired, int Snares, int SnaresFound) Measure(SignalGen g, List<double> faint)
    {
        var run = new EngineRun(new AudioEngine(Fs)).Feed(g.Buf);
        double from = 2.0;
        var hats = run.Kind(AudioEventKind.Hat).Where(e => e.T > run.At(from)).ToList();
        var any = run.Events.Where(e => e.T > run.At(from) && e.Kind is AudioEventKind.Hat or AudioEventKind.Snare or AudioEventKind.Onset).ToList();
        var loudHats = g.HatTimes.Where(t => t > from).ToList();
        var snares = g.SnareTimes.Where(t => t > from).ToList();
        var faintIn = faint.Where(t => t > from).ToList();
        return (loudHats.Count, Near(hats, loudHats, run.T0), faintIn.Count, Near(any, faintIn, run.T0, 0.02),
                snares.Count, Near(run.Kind(AudioEventKind.Snare), snares, run.T0));
    }

    /// <summary>
    /// ふつうのドラムに 20 dB・14 dB 小さい「チッ」を混ぜても、その音ではほとんど光らない（ハイハット・スネア・立ち上がりのイベントにならない）。
    /// ふつうの大きさのハイハット・スネアは今までどおり検出する。「反応の感度」を上げると、小さな音も拾う
    /// （以前は 14 dB 小さい音の 26/35 で出来事が出ていた）
    /// </summary>
    public static void TestFaintTicksDoNotTrigger()
    {
        foreach (var (amp, label) in new[] { (0.012, "-20 dB"), (0.024, "-14 dB") })
        {
            var (g, faint) = DrumsWithFaintTicks(amp, seed: 21);
            var m = Measure(g, faint);
            T.Info($"faint ticks {label}: loud hats found {m.LoudHatsFound}/{m.LoudHats}, snares found {m.SnaresFound}/{m.Snares}, " +
                   $"events from faint ticks {m.FaintFired}/{m.FaintTicks}");
            T.Check(m.LoudHatsFound >= 0.9 * m.LoudHats, $"{label}: normal hats still found");
            T.Check(m.SnaresFound >= 0.9 * m.Snares, $"{label}: snares still found");
            T.Check(m.FaintFired <= 0.15 * m.FaintTicks, $"{label}: faint ticks rarely trigger events");
        }
        // 感度を上げる（3.0）と、14 dB 小さい音も拾う
        var (g3, f3) = DrumsWithFaintTicks(0.024, seed: 21);
        var run = new EngineRun(new AudioEngine(Fs) { Settings = new AudioEngineSettings(BeatSensitivity: 3.0) }).Feed(g3.Buf);
        var any = run.Events.Where(e => e.T > run.At(2) && e.Kind is AudioEventKind.Hat or AudioEventKind.Snare or AudioEventKind.Onset).ToList();
        int fired = Near(any, f3.Where(t => t > 2).ToList(), run.T0, 0.02);
        T.Info($"-14 dB ticks at sensitivity 3.0: {fired}/{f3.Count(t => t > 2)}");
        T.Check(fired >= 0.5 * f3.Count(t => t > 2), "higher sensitivity picks the faint sounds up again");
    }

    /// <summary>調査（MUSIC_MINSIZE="0.6,0.8,…"）: 大きさの下限ごとに、ふつうのハイハット・スネアを見つける割合と、小さな音で出る出来事</summary>
    public static void TestMinSizeSweep()
    {
        string? list = Environment.GetEnvironmentVariable("MUSIC_MINSIZE");
        if (string.IsNullOrEmpty(list)) return;
        double saved = OnsetDetector.EventTune.MinSize;
        try
        {
            foreach (var v in list.Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)))
            {
                OnsetDetector.EventTune.MinSize = v;
                var (g20, f20) = DrumsWithFaintTicks(0.012, seed: 21);
                var m20 = Measure(g20, f20);
                var (g14, f14) = DrumsWithFaintTicks(0.024, seed: 21);
                var m14 = Measure(g14, f14);
                var d = new SignalGen(Fs, 20, 2);
                d.DenseMix(124, 0.3, 20);
                var run = new EngineRun(new AudioEngine(Fs)).Feed(d.Buf);
                var hats = run.Kind(AudioEventKind.Hat).Where(e => e.T > run.At(2)).ToList();
                var loudDense = d.HatTimes.Where(t => t > 2).ToList();
                int denseFound = Near(hats, loudDense, run.T0);
                T.Info($"MinSize {v:F2}: loud hats {m20.LoudHatsFound}/{m20.LoudHats}, snares {m20.SnaresFound}/{m20.Snares}, " +
                       $"-20 dB ticks fire {m20.FaintFired}/{m20.FaintTicks}, -14 dB ticks fire {m14.FaintFired}/{m14.FaintTicks}, dense-mix hats {denseFound}/{loudDense.Count}");
            }
        }
        finally { OnsetDetector.EventTune.MinSize = saved; }
    }

    /// <summary>調査（MUSIC_FAINTDIAG=1）: いろいろな合成曲で、4 つの帯の平均の比（いちばん大きい帯を 1 として）</summary>
    public static void TestBandMeanRatios()
    {
        if (Environment.GetEnvironmentVariable("MUSIC_FAINTDIAG") != "1") return;
        void Show(string name, SignalGen g)
        {
            var e = new AudioEngine(Fs);
            var sums = new double[4];
            int n = 0;
            var run = new EngineRun(e);
            run.Feed(g.Buf, before: s =>
            {
                if (s < 4) return;
                for (int b = 0; b < 4; b++) sums[b] += e.Onsets.BandMean(b);
                n++;
            });
            double max = sums.Max();
            T.Info($"{name,-28} band means / max: " + string.Join("  ", sums.Select(x => (x / max).ToString("F2"))));
        }
        var a = new SignalGen(Fs, 16, 1); a.Loop(120, 0.3, 16, hats: true, snare: true); Show("loop kick/snare/hat 0.12", a);
        var b = new SignalGen(Fs, 16, 2); b.DenseMix(124, 0.3, 16); Show("dense mix", b);
        var c = new SignalGen(Fs, 16, 3);
        for (double t = 0.3; t < 16; t += 0.5) { c.Kick(t); c.Tone(55, 0.25, t + 0.02, t + 0.48); c.Hat(t + 0.25, 0.012); }
        Show("bass + quiet hats 0.012", c);
        var d = new SignalGen(Fs, 16, 4);
        for (double t = 0.3; t < 16; t += 0.5) { d.Kick(t); d.Tone(55, 0.25, t + 0.02, t + 0.48); d.Hat(t + 0.25, 0.06); }
        Show("bass + hats 0.06", d);
        var f = new SignalGen(Fs, 16, 5); f.Loop(120, 0.3, 16, hats: false, snare: true); Show("kick/snare, no hats", f);
    }

    /// <summary>
    /// 調査（MUSIC_FAINTDIAG=1）: 鳴らした音（正解の時刻 ±30 ミリ秒）での、帯の立ち上がりの大きさ ÷ いちばん大きい帯の平均。
    /// 小さな音と、ふつうの大きさの音で分かれるか
    /// </summary>
    public static void TestEventSizeRatios()
    {
        if (Environment.GetEnvironmentVariable("MUSIC_FAINTDIAG") != "1") return;
        List<double> Sizes(SignalGen g, IReadOnlyList<double> at, int band)
        {
            var e = new AudioEngine(Fs);
            var hops = new List<(double T, double X)>();
            var run = new EngineRun(e);
            e.Onsets.DiagHop = (t, odf, bn) =>
            {
                double max = Math.Max(Math.Max(e.Onsets.BandMean(0), e.Onsets.BandMean(1)), Math.Max(e.Onsets.BandMean(2), e.Onsets.BandMean(3)));
                hops.Add((t, bn[band] * (e.Onsets.BandMean(band) + 0.02) / Math.Max(1e-9, max)));
            };
            run.Feed(g.Buf);
            return at.Where(x => x > 3).Select(x => hops.Where(h => Math.Abs(h.T - (run.T0 + x)) <= 0.03).Select(h => h.X).DefaultIfEmpty(0).Max()).ToList();
        }
        string Q(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? "-" : $"p10 {s[(int)(0.1 * (s.Count - 1))]:F2} p50 {s[s.Count / 2]:F2} p90 {s[(int)(0.9 * (s.Count - 1))]:F2} (n {s.Count})"; }

        var a = new SignalGen(Fs, 16, 1); a.Loop(120, 0.3, 16, hats: true, snare: true);
        T.Info($"loop hats 0.12          hat: {Q(Sizes(a, a.HatTimes, 3))}   snare(G2): {Q(Sizes(a, a.SnareTimes, 2))}");
        var b = new SignalGen(Fs, 16, 2); b.DenseMix(124, 0.3, 16);
        T.Info($"dense mix hats 0.04-0.08 hat: {Q(Sizes(b, b.HatTimes, 3))}   snare(G2): {Q(Sizes(b, b.SnareTimes, 2))}");
        foreach (var amp in new[] { 0.012, 0.024, 0.06 })
        {
            var c = new SignalGen(Fs, 16, 3);
            var hh = new List<double>();
            for (double t = 0.3; t < 16; t += 0.5) { c.Kick(t); c.Tone(55, 0.25, t + 0.02, t + 0.48); hh.Add(t + 0.25); }
            foreach (var t in hh) c.Hat(t, amp);
            T.Info($"bass + hats {amp,-6}      hat: {Q(Sizes(c, hh, 3))}");
        }
        foreach (var amp in new[] { 0.012, 0.024 })
        {
            var (g, faint) = DrumsWithFaintTicks(amp, seed: 21);
            T.Info($"drums + ticks {amp,-6}    tick: {Q(Sizes(g, faint, 3))}   loud hats: {Q(Sizes(g, g.HatTimes, 3))}");
        }
    }

    /// <summary>
    /// 低音の大きい曲（キック＋ベース）に、とても小さなハイハットだけ: 小さなハイハットではほとんど光らない
    /// （帯ごとに直近の平均で割るので、ほかの音がない帯では小さな音も大きく見えてしまっていた）
    /// </summary>
    public static void TestQuietHatsUnderLoudBass()
    {
        var g = new SignalGen(Fs, 24, seed: 31);
        double p = 0.5;
        var quiet = new List<double>();
        for (double t = 0.3; t < 24; t += p)
        {
            g.Kick(t, 0.8);
            g.Tone(55, 0.25, t + 0.02, t + p - 0.02);
            quiet.Add(t + p / 2);
        }
        int before = g.HatTimes.Count;
        foreach (var t in quiet) g.Hat(t, 0.012);
        g.HatTimes.RemoveRange(before, g.HatTimes.Count - before);
        var run = new EngineRun(new AudioEngine(Fs)).Feed(g.Buf);
        var hats = run.Kind(AudioEventKind.Hat).Where(e => e.T > run.At(2)).ToList();
        int fired = Near(hats, quiet.Where(t => t > 2).ToList(), run.T0, 0.03);
        int n = quiet.Count(t => t > 2);
        T.Info($"quiet hats (-20 dB) under loud bass: Hat events {fired}/{n}");
        T.Check(fired <= 0.15 * n, "quiet hats under loud bass rarely trigger Hat events");
    }
}

// ==================================================
// AudioEngineTests.cs - 音楽連動の分析エンジン (AudioEngine 2) のテスト。合成音で、キックの遅れ・出来事の時刻・
//                       テンポ・ロック・ノイズ・無音判定・帯のバランス・速さ・メモリの確保を確かめる
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using ProFanController;

namespace LogicTests;

/// <summary>エンジンに音を流しながら、分析結果・出来事・ビートの状態を記録する</summary>
public sealed class EngineRun
{
    public readonly AudioEngine E;
    public readonly double T0;
    public readonly List<AudioFrame> Frames = new();
    public readonly List<AudioEvent> Events = new();
    public readonly List<BeatState> Beats = new();
    public readonly List<BeatLock> Locks = new();
    private long _cursor;
    private double _lastT = double.NegativeInfinity;

    public EngineRun(AudioEngine e, double t0 = 1000.0)
    {
        E = e;
        T0 = t0;
    }

    /// <summary>buf の [from, to) 秒を、480 個（10 ミリ秒）ずつ流す。before は各かたまりの前に呼ぶ（設定の変更など）</summary>
    public EngineRun Feed(float[] buf, double from = 0, double to = double.PositiveInfinity, int chunk = 480, Action<double>? before = null)
    {
        int fs = E.SampleRate;
        int a = (int)Math.Round(from * fs), b = (int)Math.Min(buf.Length, Math.Round(Math.Min(to, 1e9) * fs));
        for (int i = a; i < b; i += chunk)
        {
            int n = Math.Min(chunk, b - i);
            before?.Invoke(i / (double)fs);
            E.Push(new ReadOnlySpan<float>(buf, i, n), T0 + i / (double)fs);
            Poll();
        }
        return this;
    }

    private void Poll()
    {
        if (E.Features.LatestT > _lastT)
        {
            var f = new AudioFrame();
            E.Features.Latest(f);
            _lastT = f.T;
            Frames.Add(f);
            Beats.Add(E.Beat);
            Locks.Add(E.BeatLock);
        }
        E.Events.Read(ref _cursor, double.PositiveInfinity, Events);
    }

    /// <summary>信号の先頭からの秒 → エンジンの時刻</summary>
    public double At(double sec) => T0 + sec;

    public IEnumerable<AudioEvent> Kind(AudioEventKind k) => Events.Where(e => e.Kind == k);

    /// <summary>時刻（信号の先頭からの秒）が [from, to) のコマ</summary>
    public IEnumerable<(AudioFrame F, BeatState B, BeatLock L)> Between(double from, double to)
    {
        for (int i = 0; i < Frames.Count; i++)
        {
            double s = Frames[i].T - T0;
            if (s >= from && s < to) yield return (Frames[i], Beats[i], Locks[i]);
        }
    }
}

public static class AudioEngineTests
{
    private static double Median(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToArray();
        if (a.Length == 0) return double.NaN;
        return a.Length % 2 == 1 ? a[a.Length / 2] : 0.5 * (a[a.Length / 2 - 1] + a[a.Length / 2]);
    }

    private static double Std(IEnumerable<double> xs)
    {
        var a = xs.ToArray();
        if (a.Length < 2) return 0;
        double m = a.Average();
        return Math.Sqrt(a.Sum(x => (x - m) * (x - m)) / (a.Length - 1));
    }

    // ================================================================
    // 部品

    public static void TestFftMatchesDft()
    {
        var rng = new Random(3);
        int n = 64;
        var re = new double[n];
        var im = new double[n];
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = re[i] = rng.NextDouble() * 2 - 1;
        new Fft(n).Forward(re, im);
        double maxErr = 0, energyT = 0, energyF = 0;
        for (int k = 0; k < n; k++)
        {
            double r = 0, iv = 0;
            for (int t = 0; t < n; t++)
            {
                r += x[t] * Math.Cos(2 * Math.PI * k * t / n);
                iv -= x[t] * Math.Sin(2 * Math.PI * k * t / n);
            }
            maxErr = Math.Max(maxErr, Math.Abs(r - re[k]) + Math.Abs(iv - im[k]));
            energyF += re[k] * re[k] + im[k] * im[k];
        }
        for (int t = 0; t < n; t++) energyT += x[t] * x[t];
        T.Check(maxErr < 1e-9, $"FFT matches naive DFT (max error {maxErr:G3})");
        T.Near(energyF / n, energyT, 1e-9, "Parseval: Σ|X|²/N = Σx²");
        var w = Fft.PeriodicHann(2048);
        T.Near(w.Sum(v => v * v), 3 * 2048 / 8.0, 1e-6, "periodic Hann Σw² = 3N/8");
        T.Check(w[0] == 0 && Math.Abs(w[1024] - 1) < 1e-12, "periodic Hann: w[0] = 0, w[N/2] = 1");
    }

    public static void TestBandLayoutAndPowerScale()
    {
        // 一般的な 10 バンドのイコライザーと同じ 1 オクターブごとの 10 帯（31 Hz〜16 kHz）
        T.Check(AudioFrame.BandCount == 10, "10 bands, like a standard 10-band graphic equalizer");
        var names = Enumerable.Range(0, AudioFrame.BandCount).Select(BandAnalyzer.Label).ToArray();
        T.Check(string.Join(" ", names) == "31 63 125 250 500 1k 2k 4k 8k 16k", $"band names are the usual equalizer names ({string.Join(" ", names)})");
        T.Near(BandAnalyzer.Centre(0), 31.25, 1e-9, "centre 0 = 31.25 Hz (\"31\")");
        T.Near(BandAnalyzer.Centre(5), 1000, 1e-9, "centre 5 = 1 kHz");
        T.Near(BandAnalyzer.Centre(9), 16000, 1e-9, "centre 9 = 16 kHz");
        T.Near(BandAnalyzer.Edge(0), 20, 1e-9, "lowest band starts at 20 Hz");
        T.Near(BandAnalyzer.Edge(1), 44.19, 0.01, "edge 1 = 31.25·√2 ≈ 44 Hz");
        T.Near(BandAnalyzer.Edge(6), 1414.2, 0.1, "edge 6 = 1 kHz·√2 ≈ 1,414 Hz");
        T.Near(BandAnalyzer.Edge(10), 20000, 1e-9, "highest band ends at 20 kHz");
        T.Check(BandAnalyzer.SplitBand == 7 && BandAnalyzer.Edge(BandAnalyzer.SplitBand) > 2172, "up to the 2 kHz band on the large FFT, 4 kHz and up on the small one (the mid and treble bands each keep one delay)");
        for (int k = 1; k < AudioFrame.BandCount; k++)
            T.Check(BandAnalyzer.Edge(k) > BandAnalyzer.Centre(k - 1) && BandAnalyzer.Edge(k) < BandAnalyzer.Centre(k), $"edge {k} lies between centres {k - 1} and {k}");
        // 判定に使う細かい帯（まとめた帯・基準・音量・重心）は前と同じ 40 Hz〜16 kHz の 24 帯
        T.Near(BandAnalyzer.FineEdge(0), 40, 1e-9, "fine edge 0 = 40 Hz");
        T.Near(BandAnalyzer.FineEdge(12), 800, 1e-6, "fine edge 12 = 800 Hz");
        T.Near(BandAnalyzer.FineEdge(24), 16000, 1e-6, "fine edge 24 = 16 kHz");
        T.Near(BandAnalyzer.FineCentre(0), 45.3, 0.2, "fine centre 0 ≈ 45 Hz");
        T.Near(BandAnalyzer.FineCentre(23), 14123, 5, "fine centre 23 ≈ 14.1 kHz");

        // 白色ノイズ: 帯のパワーは帯の幅に比例 → 1 オクターブの帯どうしの比は 2（+3.01 dB）。合計は 20 Hz〜20 kHz の分の 2 乗平均
        int fs = 48000;
        var ba = new BandAnalyzer(fs, 2048, 1024, 512.0 / fs);
        var fl = new Fft(2048);
        var fh = new Fft(1024);
        var wl = Fft.PeriodicHann(2048);
        var wh = Fft.PeriodicHann(1024);
        var rng = new Random(5);
        double sigma = 0.1;
        var acc = new double[AudioFrame.BandCount];
        int frames = 400;
        var reL = new double[2048]; var imL = new double[2048]; var reH = new double[1024]; var imH = new double[1024];
        var pL = new double[1025]; var pH = new double[513];
        for (int f = 0; f < frames; f++)
        {
            var x = new double[2048];
            for (int i = 0; i < 2048; i++) x[i] = sigma * Math.Sqrt(3) * (rng.NextDouble() * 2 - 1);
            for (int i = 0; i < 2048; i++) { reL[i] = x[i] * wl[i]; imL[i] = 0; }
            for (int i = 0; i < 1024; i++) { reH[i] = x[1024 + i] * wh[i]; imH[i] = 0; }
            fl.Forward(reL, imL);
            fh.Forward(reH, imH);
            for (int b = 0; b <= 1024; b++) pL[b] = reL[b] * reL[b] + imL[b] * imL[b];
            for (int b = 0; b <= 512; b++) pH[b] = reH[b] * reH[b] + imH[b] * imH[b];
            ba.Process(pL, pH, AudioEngineSettings.Default, true, 1);
            for (int k = 0; k < AudioFrame.BandCount; k++) acc[k] += ba.Power[k] / frames;
        }
        double total = acc.Sum();
        double expected = sigma * sigma * (BandAnalyzer.HighHz - BandAnalyzer.LowHz) / 24000.0;
        T.Info($"white noise σ² = {sigma * sigma}: Σ band power = {total:G4} (expected {expected:G4})");
        T.Near(10 * Math.Log10(total / expected), 0, 0.3, "white noise: total band power matches mean square (dB)");
        var slopes = new List<double>();
        // 両端の帯（20 Hz・20 kHz で切った帯）を除いた、1 オクターブちょうどの帯どうし
        for (int k = 1; k < AudioFrame.BandCount - 2; k++) slopes.Add(10 * Math.Log10(acc[k + 1] / acc[k]));
        T.Info("band dB: " + string.Join(" ", acc.Select(p => (10 * Math.Log10(p)).ToString("F1"))));
        T.Near(slopes.Average(), 10 * Math.Log10(2), 0.2, "white noise: +3.01 dB per octave band (fractional weights)");
        T.Check(slopes.All(s => Math.Abs(s - 10 * Math.Log10(2)) < 0.5), $"white noise: every octave band doubles ({string.Join(" ", slopes.Select(s => s.ToString("F2")))} dB)");

        // サイン波（振幅 0.5）: 帯のパワーの合計 = 0.5²/2 = 0.125
        var ba2 = new BandAnalyzer(fs, 2048, 1024, 512.0 / fs);
        foreach (double freq in new[] { 100.0, 1000.0, 5000.0 })
        {
            for (int i = 0; i < 2048; i++) { reL[i] = 0.5 * Math.Sin(2 * Math.PI * freq * i / fs + 0.3) * wl[i]; imL[i] = 0; }
            for (int i = 0; i < 1024; i++) { reH[i] = 0.5 * Math.Sin(2 * Math.PI * freq * (1024 + i) / fs + 0.3) * wh[i]; imH[i] = 0; }
            fl.Forward(reL, imL);
            fh.Forward(reH, imH);
            for (int b = 0; b <= 1024; b++) pL[b] = reL[b] * reL[b] + imL[b] * imL[b];
            for (int b = 0; b <= 512; b++) pH[b] = reH[b] * reH[b] + imH[b] * imH[b];
            ba2.Process(pL, pH, AudioEngineSettings.Default, true, 1);
            double sum = ba2.Power.Sum();
            T.Near(10 * Math.Log10(sum / 0.125), 0, 0.5, $"sine {freq} Hz amp 0.5: Σ band power ≈ 0.125 (got {sum:G4})");
        }
    }

    public static void TestOnsetFilterbank()
    {
        var od = new OnsetDetector(48000, 2048, 93.75);
        T.Info($"onset filterbank: {od.FilterCount} triangular filters (spec: about 46)");
        T.Check(od.FilterCount >= 40 && od.FilterCount <= 52, "filterbank has about 46 filters");
    }

    // ================================================================
    // 時刻

    public static void TestFrameTimes()
    {
        var g = new SignalGen(48000, 1.0);
        g.Tone(440, 0.3, 0, 1);
        var run = new EngineRun(new AudioEngine(48000), 500.0).Feed(g.Buf, chunk: 333);
        T.Check(run.Frames.Count == 48000 / 512, $"one frame per 512 samples ({run.Frames.Count})");
        T.Near(run.Frames[0].T, 500.0 + 511 / 48000.0, 1e-9, "first frame time = time of sample 511");
        double maxDev = 0;
        for (int i = 1; i < run.Frames.Count; i++) maxDev = Math.Max(maxDev, Math.Abs(run.Frames[i].T - run.Frames[i - 1].T - 512 / 48000.0));
        T.Check(maxDev < 1e-9, $"frame spacing = 512/fs exactly (max deviation {maxDev:G3} s)");
    }

    // ================================================================
    // キック

    public static void TestKickEnvelopeLatency()
    {
        var rng = new Random(11);
        var g = new SignalGen(48000, 12.5);
        var onsets = new List<double>();
        for (int k = 0; k < 24; k++)
        {
            double t = 0.5 + k * 0.5 + rng.NextDouble() * 0.0107; // 512 個の区切りに対していろいろな位置
            g.Kick(t, 0.7);
        }
        var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
        var peakDelay = new List<double>();
        var riseDelay = new List<double>();
        foreach (double t in g.KickTimes.Skip(4))
        {
            var seg = run.Between(t - 0.03, t + 0.08).Select(x => x.F).ToList();
            int pk = 0;
            for (int i = 1; i < seg.Count; i++) if (seg[i].KickEnv > seg[pk].KickEnv) pk = i;
            double peak = seg[pk].KickEnv;
            peakDelay.Add((seg[pk].T - run.At(t)) * 1000);
            for (int i = 1; i <= pk; i++)
            {
                if (seg[i].KickEnv >= 0.5 * peak && seg[i - 1].KickEnv < 0.5 * peak)
                {
                    double u = (0.5 * peak - seg[i - 1].KickEnv) / (seg[i].KickEnv - seg[i - 1].KickEnv);
                    double tc = seg[i - 1].T + u * (seg[i].T - seg[i - 1].T);
                    riseDelay.Add((tc - run.At(t)) * 1000);
                    break;
                }
            }
        }
        T.Info($"KickEnv peak frame delay: median {Median(peakDelay):F1} ms, max {peakDelay.Max():F1} ms (frame grid 10.7 ms)");
        T.Info($"KickEnv 50% rise (as the renderer interpolates it): median {Median(riseDelay):F1} ms, max {riseDelay.Max():F1} ms");
        T.Check(Median(peakDelay) <= 10, "KickEnv peaks ≤ 10 ms after the kick (median over hop alignments)");
        T.Check(riseDelay.Count == peakDelay.Count && riseDelay.Max() <= 10, "KickEnv reaches 50% within 10 ms for every alignment");
        double peakMean = run.Frames.Where(f => f.T > run.At(3)).Max(f => f.KickEnv);
        T.Check(peakMean > 0.8, $"KickEnv reaches near full scale on kicks ({peakMean:F2})");
        // キックとキックの間で、いったん十分に下がる（次のキックが目に見えて光る）
        double worstMin = 0;
        for (int i = 4; i + 1 < g.KickTimes.Count; i++)
        {
            double m = run.Between(g.KickTimes[i] + 0.05, g.KickTimes[i + 1]).Min(x => x.F.KickEnv);
            worstMin = Math.Max(worstMin, m);
        }
        T.Check(worstMin < 0.3, $"KickEnv falls between kicks (highest minimum between two kicks {worstMin:F2})");
    }

    public static void TestKickEventTiming()
    {
        var g = new SignalGen(48000, 20, seed: 7);
        g.Loop(120, 0.3, 20, hats: true);
        var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
        CheckEventTiming(run, g.KickTimes, AudioEventKind.Kick, "Kick", 2.0);
        // キックの「カチッ」にも 4 kHz 以上の音が少しあるので、キックと同時の Hat は数えない
        CheckEventTiming(run, g.HatTimes, AudioEventKind.Hat, "Hat", 2.0, g.KickTimes);
        var snareish = run.Kind(AudioEventKind.Snare).Count(e => e.T > run.At(2));
        T.Info($"Snare events in a kick+hat loop: {snareish}");
        T.Check(snareish <= 2, "kick+hat loop gives (almost) no Snare events");

        var g2 = new SignalGen(48000, 16, seed: 8);
        g2.Loop(100, 0.3, 16, hats: true, snare: true);
        var run2 = new EngineRun(new AudioEngine(48000)).Feed(g2.Buf);
        CheckEventTiming(run2, g2.SnareTimes, AudioEventKind.Snare, "Snare", 2.0);
        CheckEventTiming(run2, g2.KickTimes, AudioEventKind.Kick, "Kick (with snares)", 2.0);
    }

    private static void CheckEventTiming(EngineRun run, List<double> truth, AudioEventKind kind, string name, double after, List<double>? alsoAllowedAt = null)
    {
        var events = run.Kind(kind).Where(e => e.T > run.At(after)).ToList();
        var errs = new List<double>();
        int hit = 0;
        var used = new HashSet<long>();
        foreach (double t in truth.Where(t => t > after + 0.05 && t < (run.Frames[^1].T - run.T0) - 0.1))
        {
            var near = events.Where(e => Math.Abs(e.T - run.At(t)) < 0.05).OrderBy(e => Math.Abs(e.T - run.At(t))).FirstOrDefault();
            if (near.Id != 0)
            {
                hit++;
                used.Add(near.Id);
                errs.Add((near.T - run.At(t)) * 1000);
            }
        }
        int expected = truth.Count(t => t > after + 0.05 && t < (run.Frames[^1].T - run.T0) - 0.1);
        int extra = events.Count(e => !used.Contains(e.Id) && (alsoAllowedAt is null || alsoAllowedAt.All(t => Math.Abs(e.T - run.At(t)) > 0.03)));
        double medAbs = errs.Count > 0 ? Median(errs.Select(Math.Abs)) : double.NaN;
        T.Info($"{name}: detected {hit}/{expected}, extra {extra}, true-time error median {Median(errs):F1} ms (|median| {medAbs:F1} ms, min {errs.DefaultIfEmpty().Min():F1}, max {errs.DefaultIfEmpty().Max():F1})");
        T.Check(hit >= 0.9 * expected, $"{name}: ≥ 90% of hits detected");
        T.Check(extra <= Math.Max(1, 0.1 * expected), $"{name}: few extra events ({extra})");
        T.Check(medAbs <= 5, $"{name}: median true-time error ≤ 5 ms");
    }

    // ================================================================
    // テンポ

    public static void TestTempoLoops()
    {
        foreach (var (bpm, fs) in new[] { (76.0, 48000), (92.0, 48000), (100.0, 48000), (128.0, 48000), (174.0, 48000), (128.0, 44100) })
        {
            var g = new SignalGen(fs, 14, seed: (int)bpm);
            g.Loop(bpm, 0.2, 14, hats: true);
            var run = new EngineRun(new AudioEngine(fs)).Feed(g.Buf);
            CheckTempo(run, g, bpm, $"{bpm} BPM @ {fs / 1000.0} kHz");
        }
    }

    private static void CheckTempo(EngineRun run, SignalGen g, double bpm, string name, double lockBy = 6.5)
    {
        double confAt = double.NaN, lockAt = double.NaN;
        for (int i = 0; i < run.Frames.Count; i++)
        {
            double s = run.Frames[i].T - run.T0;
            if (double.IsNaN(confAt) && run.Beats[i].Confidence >= 0.55) confAt = s;
            if (double.IsNaN(lockAt) && run.Beats[i].Locked) lockAt = s;
        }
        var b = run.Beats[^1];
        double err = (b.Bpm - bpm) / bpm * 100;
        // 位相: 最後の 4 秒のキックが、公開した拍の並びのどこに来るか
        var phaseErr = new List<double>();
        foreach (double t in g.KickTimes.Where(t => t > g.Seconds - 4.5 && t < g.Seconds - 0.3))
        {
            double ta = run.At(t);
            double k = Math.Round((ta - b.LastBeatT) / b.Period);
            phaseErr.Add((b.LastBeatT + k * b.Period - ta) * 1000);
        }
        T.Info($"{name}: est {b.Bpm:F2} BPM ({err:+0.00;-0.00}%), conf {b.Confidence:F2}, locked {b.Locked} (conf≥0.55 at {confAt:F1} s, locked at {lockAt:F1} s), beat phase error median {Median(phaseErr):F1} ms");
        T.Check(Math.Abs(err) <= 1.0, $"{name}: tempo within ±1 %");
        T.Check(b.Confidence >= 0.55 && b.Locked, $"{name}: conf ≥ 0.55 and Locked at the end");
        T.Check(lockAt <= lockBy, $"{name}: Locked within {lockBy} s");
        T.Check(Math.Abs(Median(phaseErr)) <= 15, $"{name}: predicted beats land on the kicks (median error ≤ 15 ms)");
    }

    public static void TestDenseMix()
    {
        foreach (double bpm in new[] { 124.0, 96.0 })
        {
            var g = new SignalGen(48000, 16, seed: (int)bpm + 100);
            g.DenseMix(bpm, 0.3, 16);
            var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
            CheckEventTiming(run, g.KickTimes, AudioEventKind.Kick, $"dense {bpm} BPM: Kick", 2.0);
            var hats = run.Kind(AudioEventKind.Hat).Count(e => e.T > run.At(2));
            T.Info($"dense {bpm} BPM: Hat events {hats} for {g.HatTimes.Count(t => t > 2)} hats, Onset events {run.Kind(AudioEventKind.Onset).Count(e => e.T > run.At(2))}");
            CheckTempo(run, g, bpm, $"dense {bpm} BPM", lockBy: 7.0);
            var tail = run.Between(8, 16).Select(x => x.F).ToList();
            T.Info($"dense {bpm} BPM: Bass {tail.Average(f => f.Bass):F2} Mid {tail.Average(f => f.Mid):F2} Treble {tail.Average(f => f.Treble):F2}, BassE range {tail.Min(f => f.BassE):F2}..{tail.Max(f => f.BassE):F2}, KickEnv range {tail.Min(f => f.KickEnv):F2}..{tail.Max(f => f.KickEnv):F2}");
            T.Check(tail.Max(f => f.BassE) - tail.Min(f => f.BassE) > 0.4, $"dense {bpm} BPM: BassE pulses with the kicks (range > 0.4)");
        }
    }

    public static void TestTempoChangeAndSilenceRelock()
    {
        // テンポが変わる: 120 → 126 BPM
        var g = new SignalGen(48000, 24, seed: 3);
        g.Loop(120, 0.2, 10, hats: true);
        g.Loop(126, 10 + 60 / 120.0, 24, hats: true);
        var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
        var b = run.Beats[^1];
        double relock = run.Between(10.5, 24).Where(x => x.B.Locked && Math.Abs(x.B.Bpm - 126) / 126 < 0.01).Select(x => x.F.T - run.At(10.5)).DefaultIfEmpty(double.NaN).First();
        T.Info($"tempo change 120 → 126 BPM: final {b.Bpm:F2} BPM, locked {b.Locked}, locked on 126 after {relock:F1} s");
        T.Check(Math.Abs(b.Bpm - 126) / 126 < 0.01 && b.Locked, "follows a tempo change to 126 BPM (±1 %) and locks again");

        // 無音をはさむ: 曲 → 5 秒の無音 → 同じテンポの曲
        var g2 = new SignalGen(48000, 22, seed: 4);
        g2.Loop(128, 0.2, 9, hats: true);
        g2.Loop(128, 14, 22, hats: true);
        var run2 = new EngineRun(new AudioEngine(48000)).Feed(g2.Buf);
        bool lockedInSilence = run2.Between(12.5, 14).Any(x => x.B.Locked);
        double back = run2.Between(14, 22).Where(x => x.B.Locked).Select(x => x.F.T - run2.At(14)).DefaultIfEmpty(double.NaN).First();
        T.Info($"after 5 s of silence: locked during silence {lockedInSilence}, re-locked {back:F1} s after the music resumed (final {run2.Beats[^1].Bpm:F1} BPM)");
        T.Check(!lockedInSilence, "lock is released during a long silence");
        T.Check(back <= 4.0, "re-locks within 4 s after the music resumes (tempo memory kept)");

        // 短い間奏（キックだけ 1.5 秒抜ける）: ロックは続く（予測を続ける）
        var g3 = new SignalGen(48000, 16, seed: 6);
        g3.Loop(120, 0.2, 8, hats: true);
        for (double t = 8.25; t < 9.5; t += 0.5) g3.Hat(t);
        g3.Tone(300, 0.05, 8, 9.5);
        g3.Loop(120, 9.7, 16, hats: true);
        var run3 = new EngineRun(new AudioEngine(48000)).Feed(g3.Buf);
        bool keptLock = run3.Between(8, 9.7).All(x => x.B.Locked);
        T.Info($"1.5 s break without kicks: lock kept {keptLock}, states {string.Join(",", run3.Between(8, 9.7).Select(x => x.L).Distinct())}");
        T.Check(keptLock, "lock (coasting) continues through a 1.5 s break");
    }

    public static void TestPublishedPhaseIsContinuous()
    {
        var g = new SignalGen(48000, 16, seed: 12);
        g.DenseMix(118, 0.3, 16);
        var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
        double maxJumpMs = 0;
        int republished = 0;
        for (int i = 1; i < run.Frames.Count; i++)
        {
            var a = run.Beats[i - 1];
            var b = run.Beats[i];
            if (!a.Locked || !b.Locked || ReferenceEquals(a, b)) continue;
            republished++;
            double t = run.Frames[i].T;
            // 同じ時刻の「次の拍の頭」が、差し替えの前後でどれだけ動いたか
            double na = a.NextBeatAfter(t), nb = b.NextBeatAfter(t);
            double d = Math.Abs(na - nb);
            d = Math.Min(d, Math.Abs(d - b.Period));
            maxJumpMs = Math.Max(maxJumpMs, d * 1000);
        }
        T.Info($"locked: BeatState replaced {republished} times in {run.Frames.Count} frames; largest jump of the predicted next beat {maxJumpMs:F1} ms");
        T.Check(maxJumpMs < 4, "phase glide: the published beat grid never jumps by 4 ms or more while locked");
        T.Check(republished < run.Frames.Count / 3, "BeatState is not re-created every frame");
    }

    public static void TestRobustToBadInput()
    {
        var e = new AudioEngine(48000);
        var g = new SignalGen(48000, 6, seed: 13);
        g.Loop(120, 0.2, 6, hats: true);
        g.Buf[48000] = float.NaN;
        g.Buf[48001] = float.PositiveInfinity;
        g.Buf[48002] = float.NegativeInfinity;
        var rng = new Random(14);
        double Pick(double good) => rng.NextDouble() < 0.1 ? double.NaN : good;
        var run = new EngineRun(e).Feed(g.Buf, before: t =>
        {
            // 設定と「アプリの割合」を、ときどき変な値も含めてでたらめに変える
            if (rng.NextDouble() < 0.1)
            {
                e.Settings = new AudioEngineSettings(Pick(rng.NextDouble() * 24 - 12), Pick(24 + rng.NextDouble() * 36), Pick(rng.NextDouble()),
                    Pick(rng.NextDouble() * 4.5), Pick(0.7 + rng.NextDouble() * 1.3), Pick(rng.NextDouble() * 40), Pick(60 + rng.NextDouble() * 540),
                    Pick(rng.NextDouble()), Pick(0.5 + rng.NextDouble() * 2.5), rng.NextDouble() < 0.5 ? TempoModes.Auto : TempoModes.KickOnly,
                    rng.NextDouble() < 0.5 ? 1 : 2);
                e.AppGate = rng.NextDouble() < 0.1 ? double.NaN : rng.NextDouble();
            }
        });
        bool finite = run.Frames.All(f => f.Band.All(float.IsFinite) && f.Punch.All(float.IsFinite) && float.IsFinite(f.Bass) && float.IsFinite(f.BassE)
            && float.IsFinite(f.KickEnv) && float.IsFinite(f.LoudVU) && float.IsFinite(f.CeilingDb) && float.IsFinite(f.CentroidHz) && float.IsFinite(f.Activity));
        bool inRange = run.Frames.All(f => f.Band.All(v => v >= 0 && v <= 1) && f.Bass >= 0 && f.Bass <= 1 && f.BassE >= 0 && f.BassE <= 1 && f.Activity >= 0 && f.Activity <= 1);
        T.Check(finite, "NaN/Inf samples and NaN settings never produce NaN outputs");
        T.Check(inRange, "levels stay within 0..1");
        T.Check(run.Events.All(x => double.IsFinite(x.T)) && run.Beats.All(b => !double.IsNaN(b.Period) && !double.IsNaN(b.LastBeatT)), "event times and beat state stay finite");
        bool mono = true;
        for (int i = 1; i < run.Frames.Count; i++) mono &= run.Frames[i].T > run.Frames[i - 1].T;
        T.Check(mono, "frame times are strictly increasing");

        // 時刻が戻っても（時計の取り直し）、コマの時刻は進み続ける
        var e2 = new AudioEngine(48000);
        e2.Push(new float[2048], 100.0);
        double last = e2.Features.LatestT;
        e2.Push(new float[1024], 99.0);
        T.Check(e2.Features.LatestT > last, "a clock step backwards does not make frame times go backwards");
    }

    public static void TestRandomHitsDoNotLock()
    {
        var confs = new List<double>();
        int lockedFrames = 0, frames = 0;
        foreach (int seed in new[] { 21, 22, 23 })
        {
            var g = new SignalGen(48000, 20, seed);
            g.RandomHits(0.2, 20);
            var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
            foreach (var x in run.Between(3, 20))
            {
                frames++;
                if (x.B.Locked) lockedFrames++;
                confs.Add(x.B.Confidence);
            }
        }
        T.Info($"random hits: locked {lockedFrames}/{frames} frames, conf mean {confs.Average():F3}, max {confs.Max():F2}");
        T.Check(lockedFrames == 0, "random (Poisson) hits never lock");
        T.Check(confs.Average() < 0.2, "random hits keep the confidence low (mean < 0.2)");
    }

    public static void TestTempoFactorModeAndReset()
    {
        var g = new SignalGen(48000, 22, seed: 5);
        g.Loop(120, 0.2, 22, hats: true);
        var e = new AudioEngine(48000);
        var run = new EngineRun(e).Feed(g.Buf, 0, 10);
        var b1 = e.Beat;
        T.Check(b1.Locked, $"120 BPM locked before changing the factor ({b1.Bpm:F1} BPM)");
        e.Settings = e.Settings with { TempoFactor = 2 };
        run.Feed(g.Buf, 10, 10.05);
        var b2 = e.Beat;
        T.Near(b2.Period, b1.Period / 2, b1.Period * 0.01, "TempoFactor 2 halves the period");
        // 倍のテンポの拍は、キックと裏拍の両方に来る
        double tk = run.At(g.KickTimes.First(t => t > 10.3));
        double offB = Math.Abs(b2.Phase(tk + 0.25 + BeatTracker.BeatLatencySec * 0) - 0);
        T.Info($"factor 2: phase at an off-beat = {b2.Phase(tk + 0.25):F2} (0 or 1 = on a beat)");
        _ = offB;
        e.Settings = e.Settings with { TempoFactor = 0.5 };
        run.Feed(g.Buf, 10.05, 10.1);
        T.Near(e.Beat.Period, b1.Period * 2, b1.Period * 0.02, "TempoFactor 0.5 doubles the period");
        e.Settings = e.Settings with { TempoFactor = 1 };
        run.Feed(g.Buf, 10.1, 12);

        e.ResetTempo();
        T.Check(ReferenceEquals(e.Beat, BeatState.None), "ResetTempo publishes BeatState.None immediately");
        run.Feed(g.Buf, 12, 22);
        var relock = run.Between(12, 22).FirstOrDefault(x => x.B.Locked);
        T.Info($"re-locked {(relock.F is null ? "never" : $"{relock.F.T - run.At(12):F1} s")} after ResetTempo");
        T.Check(relock.F is not null && relock.F.T - run.At(12) < 7, "re-locks within 7 s after ResetTempo");

        var e2 = new AudioEngine(48000) { Settings = AudioEngineSettings.Default with { TempoMode = TempoModes.Off } };
        var run2 = new EngineRun(e2).Feed(g.Buf, 0, 10);
        T.Check(run2.Beats.All(x => ReferenceEquals(x, BeatState.None)), "TempoMode Off: Beat stays BeatState.None");
        T.Check(run2.Kind(AudioEventKind.Kick).Any(), "TempoMode Off: Kick events still arrive");
        var e3 = new AudioEngine(48000) { Settings = AudioEngineSettings.Default with { TempoMode = TempoModes.KickOnly } };
        new EngineRun(e3).Feed(g.Buf, 0, 10);
        T.Check(e3.Beat.HasTempo && Math.Abs(e3.Beat.Bpm - 120) < 1.2, $"TempoMode KickOnly still tracks the tempo ({e3.Beat.Bpm:F1})");
    }

    // ================================================================
    // ノイズ・門・バランス

    public static void TestNoiseGivesNoOnsetsAndSteadyLevels()
    {
        foreach (bool pink in new[] { true, false })
        {
            var g = new SignalGen(48000, 12, seed: pink ? 31 : 32);
            if (pink) g.Pink(-20, 0, 12); else g.White(-20, 0, 12);
            var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
            string name = pink ? "pink noise" : "white noise";
            var ev = run.Events.Where(e => e.T > run.At(1.0) && e.Kind != AudioEventKind.Click).ToList();
            T.Info($"{name}: events after 1 s: {ev.Count} (Kick {ev.Count(e => e.Kind == AudioEventKind.Kick)}, Snare {ev.Count(e => e.Kind == AudioEventKind.Snare)}, Hat {ev.Count(e => e.Kind == AudioEventKind.Hat)}, Onset {ev.Count(e => e.Kind == AudioEventKind.Onset)}) in 11 s");
            T.Check(ev.Count <= 3, $"{name}: (nearly) no onset events");
            var tail = run.Between(4, 12).Select(x => x.F).ToList();
            double sb = Std(tail.Select(f => (double)f.Bass)), sm = Std(tail.Select(f => (double)f.Mid)), st = Std(tail.Select(f => (double)f.Treble));
            T.Info($"{name}: Bass {tail.Average(f => f.Bass):F2}±{sb:F3}, Mid {tail.Average(f => f.Mid):F2}±{sm:F3}, Treble {tail.Average(f => f.Treble):F2}±{st:F3}, LoudVU {tail.Average(f => f.LoudVU):F2}, ceiling {tail.Average(f => f.CeilingDb):F1} dB, centroid {tail.Average(f => f.CentroidHz):F0} Hz");
            T.Check(sb < 0.08 && sm < 0.08 && st < 0.08, $"{name}: steady levels (std < 0.08)");
            double vu = tail.Average(f => f.LoudVU);
            T.Check(vu > 0.7 && vu < 0.95, $"{name}: the loudness meter is not pinned at the top for a steady sound ({vu:F2})");
            T.Check(!run.Beats[^1].Locked, $"{name}: no tempo lock");
        }
    }

    public static void TestLoudnessMeterFollowsLevelChanges()
    {
        var g = new SignalGen(48000, 12, seed: 33);
        g.Pink(-20, 0, 6);
        g.Pink(-28, 6, 12);   // 8 dB 小さくなる
        var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
        double loud = run.Between(4, 6).Average(x => x.F.LoudVU);
        double quiet = run.Between(6.5, 7.5).Average(x => x.F.LoudVU);
        T.Info($"loudness meter: {loud:F2} → {quiet:F2} after an 8 dB drop");
        T.Check(loud < 0.95, $"steady loud part leaves headroom ({loud:F2})");
        T.Check(loud - quiet > 0.12, $"an 8 dB drop is clearly visible on the meter ({loud:F2} → {quiet:F2})");
    }

    public static void TestGate()
    {
        var g = new SignalGen(48000, 9, seed: 41);
        g.White(-70, 0, 3);
        g.White(-30, 3, 5);
        g.White(-70, 5, 9);
        var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
        var quiet = run.Between(0.1, 3).ToList();
        T.Check(quiet.All(x => !x.F.GateOpen && x.F.Activity == 0), "−70 dBFS noise: gate stays closed, activity 0");
        T.Check(quiet.All(x => x.F.Bass == 0 && x.F.Treble == 0), "−70 dBFS noise: no levels");
        T.Check(!run.Events.Any(e => e.T < run.At(2.9)), "−70 dBFS noise: no events");
        T.Check(run.Between(3.03, 5).All(x => x.F.GateOpen), "−30 dBFS noise: gate opens at once");
        var close = run.Between(5, 9).FirstOrDefault(x => !x.F.GateOpen);
        double closeAfter = close.F is null ? double.NaN : close.F.T - run.At(5);
        T.Info($"gate closed {closeAfter * 1000:F0} ms after the level dropped to −70 dBFS; RmsDb then {close.F?.RmsDb:F1}");
        T.Check(closeAfter >= 0.3 && closeAfter <= 0.4, "gate closes ≈ 300 ms after the level drops");
        T.Check(run.Between(5.8, 9).All(x => x.F.Activity < 0.01), "activity decays to 0 after the gate closes");
    }

    public static void TestSpectralBalanceKept()
    {
        var g = new SignalGen(48000, 8);
        g.Tone(60, 0.25, 0, 4);
        g.Tone(6000, 0.25, 4, 8);
        var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
        var lowPart = run.Between(2, 4).Select(x => x.F).ToList();
        var highPart = run.Between(6, 8).Select(x => x.F).ToList();
        double bass = lowPart.Average(f => f.Bass), treble = lowPart.Average(f => f.Treble), mid = lowPart.Average(f => f.Mid);
        T.Info($"60 Hz tone: Sub {lowPart.Average(f => f.Sub):F2} Bass {bass:F2} Mid {mid:F2} Treble {treble:F3}, centroid {lowPart.Average(f => f.CentroidHz):F0} Hz");
        T.Check(bass > 0.5 && treble < 0.05 && mid < 0.2, "bass-only tone: Bass ≫ Treble (global AGC keeps the balance)");
        double bass2 = highPart.Average(f => f.Bass), treble2 = highPart.Average(f => f.Treble);
        T.Info($"6 kHz tone: Bass {bass2:F3} Treble {treble2:F2}, centroid {highPart.Average(f => f.CentroidHz):F0} Hz");
        T.Check(treble2 > 0.5 && bass2 < 0.05, "treble-only tone: Treble ≫ Bass");
        T.Check(lowPart.Average(f => f.CentroidHz) < 150 && highPart.Average(f => f.CentroidHz) > 3000, "centroid follows the tone");

        // 低音が強く、高音が 20 dB 小さい曲: 高音は低音より明らかに暗い
        var g2 = new SignalGen(48000, 6);
        g2.Tone(80, 0.3, 0, 6);
        g2.Tone(5000, 0.03, 0, 6);
        var run2 = new EngineRun(new AudioEngine(48000)).Feed(g2.Buf);
        var mix = run2.Between(3, 6).Select(x => x.F).ToList();
        T.Info($"80 Hz + 5 kHz (−20 dB): Bass {mix.Average(f => f.Bass):F2}, Treble {mix.Average(f => f.Treble):F2}");
        T.Check(mix.Average(f => f.Bass) > mix.Average(f => f.Treble) + 0.15, "a quieter treble stays dimmer than the bass");
    }

    public static void TestSteadyToneDoesNotSaturatePunch()
    {
        var g = new SignalGen(48000, 10);
        g.Tone(100, 0.3, 0.5, 10);
        var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
        var start = run.Between(0.5, 0.8).Select(x => x.F).ToList();
        var mid = run.Between(3.5, 4).Select(x => x.F).ToList();
        var end = run.Between(9, 10).Select(x => x.F).ToList();
        double w = AudioEngineSettings.Default.Punch;
        double maxStart = start.Max(f => f.BassE);
        double bassE = end.Average(f => f.BassE), bass = end.Average(f => f.Bass);
        int k = 2; // 100 Hz は帯 2（125 Hz の帯: 88〜177 Hz）
        double punchK = end.Average(f => f.Punch[k]), bandK = end.Average(f => f.Band[k]);
        T.Info($"steady 100 Hz: BassE at onset {maxStart:F2}, after 3 s {mid.Average(f => f.BassE):F2}, after 8.5 s BassE {bassE:F2} (Bass {bass:F2}), band {k}: punch {punchK:F2} vs (1−w)·band {(1 - w) * bandK:F2}");
        T.Check(maxStart > 0.8, "tone onset gives a punch");
        T.Check(mid.Average(f => f.BassE) < maxStart - 0.05, "steady tone: punch is already falling after 3 s");
        T.Check(bassE < 0.62, "steady tone: BassE settles well below 1 (no saturated punch)");
        T.Near(punchK, (1 - w) * bandK, 0.05, "steady tone: punch → (1 − w)·band level");
    }

    public static void TestSilenceDecaysSmoothly()
    {
        var g = new SignalGen(48000, 9, seed: 9);
        g.Loop(120, 0.2, 5, hats: true);
        g.Tone(220, 0.2, 0.2, 5);
        var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
        var after = run.Between(5.06, 9).Select(x => x.F).ToList();
        double maxRise = 0, maxDrop = 0;
        for (int i = 1; i < after.Count; i++)
        {
            foreach (var (a, b) in new[] { (after[i - 1].Bass, after[i].Bass), (after[i - 1].Mid, after[i].Mid), (after[i - 1].LoudVU, after[i].LoudVU), (after[i - 1].Band[3], after[i].Band[3]) })
            {
                maxRise = Math.Max(maxRise, b - a);
                maxDrop = Math.Max(maxDrop, a - b);
            }
        }
        var late = run.Between(6.5, 9).Select(x => x.F).ToList();
        T.Info($"after music stops: max rise per frame {maxRise:F3}, max drop per frame {maxDrop:F3}; at +1.5 s Mid {late[0].Mid:F3} LoudVU {late[0].LoudVU:F3} gate {late[0].GateOpen}");
        T.Check(maxRise <= 0.01, "levels never rise after the music stops");
        T.Check(maxDrop <= 0.12, "levels fall smoothly (≤ 0.12 per frame)");
        T.Check(late.All(f => f.Mid < 0.02 && f.Bass < 0.02 && f.LoudVU < 0.05 && !f.GateOpen), "levels reach 0 and the gate closes within 1.5 s");
        T.Check(!run.Events.Any(e => e.T > run.At(5.1) && e.Kind != AudioEventKind.Click), "no events in the silence");
    }

    // ================================================================
    // 同期テスト・ドロップ・設定・サンプリング周波数

    public static void TestClickDetect()
    {
        var g = new SignalGen(48000, 4);
        var truth = new List<double>();
        for (int k = 0; k < 6; k++)
        {
            double t = 0.4 + k * 0.6 + k * 0.00013;
            g.Click(t);
            truth.Add(Math.Round(t * 48000) / 48000);
        }
        var e = new AudioEngine(48000) { ClickDetect = true };
        var run = new EngineRun(e).Feed(g.Buf);
        var clicks = run.Kind(AudioEventKind.Click).ToList();
        T.Check(clicks.Count == truth.Count, $"one Click event per click ({clicks.Count}/{truth.Count})");
        double maxErr = 0;
        for (int i = 0; i < Math.Min(clicks.Count, truth.Count); i++) maxErr = Math.Max(maxErr, Math.Abs(clicks[i].T - run.At(truth[i])));
        T.Info($"click time error max {maxErr * 1e6:F0} µs");
        T.Check(maxErr < 0.0002, "Click time is sample-accurate (< 0.2 ms)");
        var run2 = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
        T.Check(!run2.Kind(AudioEventKind.Click).Any(), "no Click events when ClickDetect is false");
    }

    /// <summary>
    /// 同期テストで本当に鳴らすクリック（SyncClick。なめらかに立ち上がる）を検出できる。
    /// 立ち上がりの小さな音で「無音」の数え直しをしていたため、一度も検出できず、同期テストで LED が光らなかった
    /// </summary>
    public static void TestClickDetectRealSyncClick()
    {
        foreach (int rate in new[] { 48000, 192000 })
        {
            double lead = 0.4;
            var buf = new float[rate * 4];
            var rnd = new Random(7);
            int start = (int)Math.Round(lead * rate);
            for (int i = 0; i < buf.Length; i++)
            {
                float noise = (float)((rnd.NextDouble() - 0.5) * 0.004); // かすかな雑音（無音とみなす大きさより小さい）
                buf[i] = noise + (i >= start ? SyncClick.Sample(i - start, rate) : 0f);
            }
            var truth = new List<double>();
            for (double t = lead; t < 4 - 0.01; t += SyncClick.Interval) truth.Add(t);

            var run = new EngineRun(new AudioEngine(rate) { ClickDetect = true }).Feed(buf, chunk: rate / 100);
            var clicks = run.Kind(AudioEventKind.Click).ToList();
            T.Check(clicks.Count == truth.Count, $"{rate} Hz: one Click per real sync click ({clicks.Count}/{truth.Count})");
            double maxErr = 0;
            for (int i = 0; i < Math.Min(clicks.Count, truth.Count); i++) maxErr = Math.Max(maxErr, Math.Abs(clicks[i].T - run.At(truth[i])));
            T.Info($"{rate} Hz: click time error max {maxErr * 1e3:F2} ms");
            T.Check(clicks.Count > 0 && maxErr < 0.001, $"{rate} Hz: Click time within 1 ms of the click start");
        }
    }

    public static void TestDropEvent()
    {
        var g = new SignalGen(48000, 26, seed: 51);
        g.Loop(128, 0.2, 12, hats: true);
        // ブレイク: キックなし（ハイハットとパッドだけ）
        double p = 60 / 128.0;
        for (double t = 12 + p / 2; t < 18; t += p) g.Hat(t);
        g.Tone(330, 0.05, 12, 18);
        double drop = 12 + Math.Ceiling((18 - 12) / p) * p;
        g.Kick(drop, 1.0);
        g.Loop(128, drop + p, 26, hats: true);
        var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
        var drops = run.Kind(AudioEventKind.Drop).ToList();
        T.Info($"Drop events: {string.Join(", ", drops.Select(d => $"{d.T - run.T0:F2} s (s={d.Strength:F1})"))}; true drop at {drop:F2} s");
        T.Check(drops.Count(d => Math.Abs(d.T - run.At(drop)) < 0.05) == 1, "one Drop event at the drop");
        T.Check(drops.Count == 1, "no other Drop events");
    }

    public static void TestSettingsChangeAndReset()
    {
        var g = new SignalGen(48000, 10, seed: 61);
        g.Loop(110, 0.2, 10, hats: true);
        g.Tone(440, 0.1, 0.2, 10);
        var e = new AudioEngine(48000);
        var run = new EngineRun(e).Feed(g.Buf, 0, 4);
        double before = run.Between(3, 4).Average(x => x.F.Mid);
        e.Settings = e.Settings with { GainDb = -12, ReactionMs = 0, SmoothnessMs = 600, Punch = 1, Contrast = 2, AutoBalance = 0 };
        run.Feed(g.Buf, 4, 6);
        double after = run.Between(5, 6).Average(x => x.F.Mid);
        T.Info($"Mid with gain 0 dB {before:F2} → −12 dB {after:F2}");
        T.Check(after < before - 0.15, "GainDb change takes effect on the next frames");
        e.Reset();
        T.Check(e.Features.Count == 0 && ReferenceEquals(e.Beat, BeatState.None), "Reset clears the feature ring and the beat state");
        run.Feed(g.Buf, 6, 10);
        T.Check(e.Features.Count > 0 && run.Frames[^1].GateOpen, "analysis continues after Reset");
    }

    public static void TestOtherSampleRates()
    {
        foreach (int fs in new[] { 44100, 96000, 192000 })
        {
            var g = new SignalGen(fs, 14, seed: 71);
            g.Loop(120, 0.2, 14, hats: true);
            var e = new AudioEngine(fs);
            var run = new EngineRun(e).Feed(g.Buf);
            T.Info($"{fs} Hz: analysed at {e.AnalysisRate} Hz, FFT {e.FftSize}, frames {run.Frames.Count} ({run.Frames.Count / 14.0:F1}/s)");
            T.Near(run.Frames.Count / 14.0, e.AnalysisRate / 512.0, 1, $"{fs} Hz: one frame per 512 analysed samples (96 / 192 kHz are analysed at 48 kHz)");
            CheckEventTiming(run, g.KickTimes, AudioEventKind.Kick, $"Kick @ {fs} Hz", 2.0);
            CheckTempo(run, g, 120, $"120 BPM @ {fs / 1000.0} kHz");
            var tone = new SignalGen(fs, 4);
            tone.Tone(60, 0.25, 0, 4);
            var r2 = new EngineRun(new AudioEngine(fs)).Feed(tone.Buf);
            var seg = r2.Between(2, 4).Select(x => x.F).ToList();
            T.Check(seg.Average(f => f.Bass) > 0.5 && seg.Average(f => f.Treble) < 0.05, $"{fs} Hz: bass tone → Bass ≫ Treble");
        }
    }

    public static void TestDownbeatHeuristic()
    {
        var g = new SignalGen(48000, 20, seed: 81);
        g.Loop(120, 0.2, 20, hats: true, accentFirst: true);
        var run = new EngineRun(new AudioEngine(48000)).Feed(g.Buf);
        var b = run.Beats[^1];
        // 強いキック（4 拍ごと）が小節の頭（Bar = 0）に来るか
        var accents = g.KickTimes.Where((t, i) => i % 4 == 0 && t > 14).ToList();
        var bars = accents.Select(t => b.Bar(run.At(t) + 0.02)).ToList();
        T.Info($"downbeat offset {b.DownbeatOffset}; bar of the accented kicks: {string.Join(",", bars)}");
        T.Check(bars.All(x => x == 0), "accented first beat becomes the downbeat (Bar = 0)");
    }

    // ================================================================
    // 速さとメモリ

    public static void TestPerformanceAndAllocations()
    {
        int fs = 48000;
        var g = new SignalGen(fs, 34, seed: 91);
        g.Loop(128, 0.2, 34, hats: true, snare: false);
        g.Pink(-30, 0, 34);
        foreach (string mode in new[] { TempoModes.Auto, TempoModes.Off })
        {
            var e = new AudioEngine(fs) { Settings = AudioEngineSettings.Default with { TempoMode = mode } };
            // 準備運転（JIT と、最初の数秒にだけ確保するものを済ませる）
            int warm = 4 * fs / 512 * 512;
            for (int i = 0; i < warm; i += 512) e.Push(new ReadOnlySpan<float>(g.Buf, i, 512), 1000 + i / (double)fs);
            int hops = (g.Buf.Length - warm) / 512;
            var times = new double[hops];
            long alloc0 = GC.GetAllocatedBytesForCurrentThread();
            long ticks0 = Stopwatch.GetTimestamp();
            for (int h = 0; h < hops; h++)
            {
                int i = warm + h * 512;
                long t0 = Stopwatch.GetTimestamp();
                e.Push(new ReadOnlySpan<float>(g.Buf, i, 512), 1000 + i / (double)fs);
                times[h] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }
            double totalMs = (Stopwatch.GetTimestamp() - ticks0) * 1000.0 / Stopwatch.Frequency;
            long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
            Array.Sort(times);
            double avg = totalMs / hops, p99 = times[(int)(hops * 0.99)], mx = times[^1];
            T.Info($"TempoMode {mode}: Push per hop avg {avg:F3} ms, p99 {p99:F3} ms, max {mx:F3} ms ({hops} hops); allocated {alloc} bytes = {alloc / (double)hops:F1} B/hop; locked {e.Beat.Locked}");
            T.Check(avg < 1.0, $"TempoMode {mode}: average Push cost per hop < 1 ms (target 0.3 ms)");
            if (mode == TempoModes.Off) T.Check(alloc <= 1024, $"TempoMode Off: no allocations per hop after warm-up ({alloc} bytes)");
            else T.Check(alloc / (double)hops <= 16, $"TempoMode Auto: only BeatState snapshots are allocated (≤ 16 B/hop, got {alloc / (double)hops:F1})");
        }
    }
}

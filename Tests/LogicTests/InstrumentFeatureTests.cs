// ==================================================
// InstrumentFeatureTests.cs - 楽器ごとの手がかり (打楽器と持続音・左右の広がり・12 音の色・ベースの音の高さ・
//                             楽器らしさ) と、ベースの音をキックと数えないこと
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

/// <summary>左右 2 つの音を、ミッド・サイドにして流す（EngineRun のステレオ版）</summary>
public sealed class StereoRun
{
    public readonly AudioEngine E;
    public readonly double T0 = 1000;
    public readonly List<AudioFrame> Frames = new();
    public readonly List<AudioEvent> Events = new();
    private long _cursor;
    private double _lastT = double.NegativeInfinity;

    public StereoRun(AudioEngine e) => E = e;

    /// <summary>l・r（同じ長さ）を 480 個ずつ流す。r が null ならモノラル（l だけ）</summary>
    public StereoRun Feed(float[] l, float[]? r = null, int chunk = 480)
    {
        var mid = new float[chunk];
        var side = new float[chunk];
        for (int i = 0; i < l.Length; i += chunk)
        {
            int n = Math.Min(chunk, l.Length - i);
            if (r is null) E.Push(new ReadOnlySpan<float>(l, i, n), T0 + i / (double)E.SampleRate);
            else
            {
                for (int k = 0; k < n; k++)
                {
                    mid[k] = 0.5f * (l[i + k] + r[i + k]);
                    side[k] = 0.5f * (l[i + k] - r[i + k]);
                }
                E.Push(mid.AsSpan(0, n), side.AsSpan(0, n), T0 + i / (double)E.SampleRate);
            }
            if (E.Features.LatestT > _lastT)
            {
                var f = new AudioFrame();
                E.Features.Latest(f);
                _lastT = f.T;
                Frames.Add(f);
            }
            E.Events.Read(ref _cursor, double.PositiveInfinity, Events);
        }
        return this;
    }

    public IEnumerable<AudioFrame> Between(double from, double to) => Frames.Where(f => f.T - T0 >= from && f.T - T0 < to);

    public double Mean(double from, double to, Func<AudioFrame, double> pick)
    {
        var xs = Between(from, to).Select(pick).ToList();
        return xs.Count > 0 ? xs.Average() : 0;
    }
}

public static class InstrumentSources
{
    public const int Fs = 48000;
    public static readonly double[] BassNotes = { 55, 73.4, 65.4, 82.4, 49, 61.7 };
    public static readonly double[] VocalNotes = { 220, 246.9, 261.6, 293.7, 261.6, 246.9 };
    public static readonly double[] BrassNotes = { 349.2, 392, 440, 392, 466.2, 392 };
    public static readonly double[] PadChord1 = { 261.6, 329.6, 392 };
    public static readonly double[] PadChord2 = { 220, 261.6, 329.6 };

    /// <summary>名前つきの音（左・右）。秒数 sec</summary>
    public static (string Name, float[] L, float[] R, SignalGen G) Make(string name, double sec = 8)
    {
        var l = new SignalGen(Fs, sec, seed: 51);
        var r = new SignalGen(Fs, sec, seed: 52);
        SignalGen g = l;
        switch (name)
        {
            case "vocal":
                // 真ん中（左右同じ）
                l.VocalTone(0.2, sec, VocalNotes, 0.6);
                r.VocalTone(0.2, sec, VocalNotes, 0.6);
                break;
            case "pad":
                // 左右で少し違う音（横に広い）。2 秒ごとに和音が変わる
                for (double t = 0.2; t < sec - 0.1; t += 2)
                {
                    var c = ((int)(t / 2)) % 2 == 0 ? PadChord1 : PadChord2;
                    l.PadChord(t, Math.Min(sec, t + 2.3), c, seed: 61 + (int)t);
                    r.PadChord(t, Math.Min(sec, t + 2.3), c, seed: 71 + (int)t);
                }
                break;
            case "bass":
                l.BassLine(0.2, sec, 4, BassNotes);
                r.BassLine(0.2, sec, 4, BassNotes);
                break;
            case "brass":
                l.BrassTone(0.2, sec, BrassNotes, 0.45);
                r.BrassTone(0.2, sec, BrassNotes, 0.45);
                break;
            case "guitar":
                // 左寄り（左 1・右 0.35）の和音を 1 秒に 2 回
                for (double t = 0.2; t < sec - 0.5; t += 0.5)
                {
                    var c = ((int)(t * 2)) % 4 < 2 ? new[] { 196.0, 246.9, 293.7, 392 } : new[] { 220.0, 261.6, 329.6, 440 };
                    l.Strum(t, c);
                    r.Strum(t, c, 0.08 * 0.35);
                }
                break;
            default:
                throw new ArgumentException(name);
        }
        return (name, l.Buf, r.Buf, g);
    }

    public static readonly string[] Names = { "vocal", "guitar", "pad", "brass", "bass" };
}

public static class InstrumentDiagTests
{
    /// <summary>調査用: 環境変数 INST_DIAG=1 のとき、キックの候補の打楽器らしさと、音ごとのらしさの材料を表示する</summary>
    public static void TestInstrumentDiag()
    {
        if (Environment.GetEnvironmentVariable("INST_DIAG") != "1") return;
        {
            var ins = new InstrumentFeatures(48000, 2048, 1024, 512 / 48000.0);
            var rng = new Random(3);
            var pl = Enumerable.Range(0, 1025).Select(_ => rng.NextDouble() * 1e4).ToArray();
            var ph = Enumerable.Range(0, 513).Select(_ => rng.NextDouble() * 1e4).ToArray();
            var x = Enumerable.Range(0, 512).Select(i => (float)Math.Sin(i * 0.01)).ToArray();
            var s = AudioEngineSettings.Default;
            for (int w = 0; w < 200; w++) { ins.Process(pl, ph, ph, s, true, 1); ins.EndHop(w, false, 0); }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int w = 0; w < 2000; w++) ins.Process(pl, ph, null, s, true, 1);
            double tp = sw.Elapsed.TotalMilliseconds / 2000;
            sw.Restart();
            for (int w = 0; w < 2000; w++) ins.EndHop(w, false, 0);
            double te = sw.Elapsed.TotalMilliseconds / 2000;
            sw.Restart();
            for (int w = 0; w < 2000; w++) foreach (var v in x) ins.Sample(v);
            double ts = sw.Elapsed.TotalMilliseconds / 2000;
            T.Info($"cost per hop: Process {tp:F4} ms, EndHop (YIN) {te:F4} ms, Sample x512 {ts:F4} ms");
        }
        bool saved = OnsetDetector.KickTune.PercGate;
        try
        {
            OnsetDetector.KickTune.PercGate = false;
            void Cands(string name, SignalGen g, bool kicksAreTrue)
            {
                var e = new AudioEngine(g.Fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
                var list = new List<(double T, double Prom, double Rise, double Rel, double P)>();
                e.Onsets.DiagKickCandidate = (t, prom, rise, rel) => list.Add((t - 1000, prom, rise, rel, OnsetDetector.Percussiveness(rise, rel, prom)));
                new EngineRun(e).Feed(g.Buf);
                var kicks = list.Where(x => x.T > 2).ToList();
                var trueK = kicks.Where(x => g.KickTimes.Any(k => Math.Abs(k - x.T) < 0.03)).ToList();
                var falseK = kicks.Where(x => !g.KickTimes.Any(k => Math.Abs(k - x.T) < 0.03)).ToList();
                string S(IEnumerable<double> v) { var a = v.OrderBy(x => x).ToArray(); return a.Length == 0 ? "-" : $"{a[0]:F2}/{a[(int)(0.1 * (a.Length - 1))]:F2}/{a[a.Length / 2]:F2}/{a[^1]:F2}"; }
                double gate = OnsetDetector.KickMinPercussive;
                T.Info($"{name,-28} true {trueK.Count,3} (pass {trueK.Count(x => x.P >= gate),3}) perc[{S(trueK.Select(x => x.P))}] prom[{S(trueK.Select(x => x.Prom))}] rise[{S(trueK.Select(x => x.Rise))}] rel[{S(trueK.Select(x => x.Rel))}]");
                T.Info($"{"",-28} false {falseK.Count / Math.Max(1, g.Seconds - 2):F1}/s (pass {falseK.Count(x => x.P >= gate) / Math.Max(1, g.Seconds - 2):F2}/s) perc[{S(falseK.Select(x => x.P))}] prom[{S(falseK.Select(x => x.Prom))}] rise[{S(falseK.Select(x => x.Rise))}] rel[{S(falseK.Select(x => x.Rel))}]");
            }
            foreach (var (rate, reg, n) in new[] { (8.0, 0.125, "bass 8/s"), (4.0, 0.125, "bass 4/s"), (6.0, 0.25, "low 6/s"), (8.0, 0.25, "low 8/s") })
                Cands("arp " + n, MusicLight.Arpeggio(rate, 16, reg).G, false);
            foreach (var c in MusicBench.Cases()) Cands(c.Name, c.Gen, true);
            {
                var g = new SignalGen(48000, 16, seed: 3);
                g.DenseMix(124, 0.2, 16);
                Cands("dense 124", g, true);
            }
            {
                var g = new SignalGen(48000, 16, seed: 4);
                g.BassLine(0.2, 16, 4, InstrumentSources.BassNotes);
                Cands("bassline 4/s", g, false);
            }
            {
                var g = new SignalGen(48000, 16, seed: 5);
                g.BassLine(0.2, 16, 8, InstrumentSources.BassNotes);
                g.Loop(120, 0.2, 16, hats: true);
                Cands("bassline 8/s + loop", g, true);
            }
            {
                // キックに「カチッ」がない（808 のような音）
                var g = new SignalGen(48000, 16, seed: 6);
                for (double t = 0.2; t < 16; t += 0.5) g.Kick(t, 0.8, click: false);
                MusicBench.Backing(g, 120, 0.2, 16);
                Cands("clickless kicks", g, true);
            }
            {
                // ベースの音とハイハットが同時（8 分）
                var g = new SignalGen(48000, 16, seed: 7);
                g.BassLine(0.2, 16, 4, InstrumentSources.BassNotes);
                for (double t = 0.2; t < 16; t += 0.25) g.Hat(t, 0.06);
                Cands("bassline + hats together", g, false);
            }
        }
        finally { OnsetDetector.KickTune.PercGate = saved; }

        {
            var (_, l, r, g) = InstrumentSources.Make("bass", 12);
            var run = new StereoRun(new AudioEngine(48000) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) }).Feed(l, r);
            int ok = 0, n = 0, voiced = 0;
            for (int i = 0; i < g.BassNoteTimes.Count; i++)
            {
                double t0 = g.BassNoteTimes[i], f = g.BassNoteHz[i];
                foreach (var fr in run.Between(t0 + 0.06, t0 + 0.2))
                {
                    n++;
                    if (fr.BassF0Hz > 0) voiced++;
                    if (fr.BassF0Hz > 0 && Math.Abs(12 * Math.Log2(fr.BassF0Hz / f)) < 0.5) ok++;
                }
            }
            var ons = new List<double>();
            for (int i = 1; i < run.Frames.Count; i++) if (run.Frames[i].BassOnset > run.Frames[i - 1].BassOnset + 0.15) ons.Add(run.Frames[i].T - run.T0);
            int hit = g.BassNoteTimes.Count(t => ons.Any(o => o - t > -0.01 && o - t < 0.08));
            var lags = g.BassNoteTimes.Select(t => ons.Where(o => o - t > -0.01 && o - t < 0.08).DefaultIfEmpty(double.NaN).First() - t).Where(double.IsFinite).OrderBy(x => x).ToList();
            T.Info($"bass f0: voiced {voiced / (double)n:P0}, right note {ok / (double)n:P0}; onsets {ons.Count} for {g.BassNoteTimes.Count} notes, hit {hit}, lag p50 {(lags.Count > 0 ? lags[lags.Count / 2] * 1000 : double.NaN):F0} ms");
            var g2 = new SignalGen(48000, 12, seed: 8);
            g2.Loop(120, 0.2, 12, hats: true, snare: true);
            var run2 = new StereoRun(new AudioEngine(48000)).Feed(g2.Buf);
            int ons2 = 0;
            for (int i = 1; i < run2.Frames.Count; i++) if (run2.Frames[i].BassOnset > run2.Frames[i - 1].BassOnset + 0.15) ons2++;
            T.Info($"drum loop only: bass onsets {ons2 / 11.8:F2}/s, bass level {run2.Mean(2, 12, f => f.BassLevel):F2}, harm bass {run2.Mean(2, 12, f => f.HarmBass):F2} perc low {run2.Mean(2, 12, f => f.PercLow):F2}");
            var (_, pl, pr, _) = InstrumentSources.Make("pad", 8);
            var run3 = new StereoRun(new AudioEngine(48000)).Feed(pl, pr);
            foreach (var (from, to) in new[] { (1.0, 2.0), (3.0, 4.0), (5.0, 6.0) })
            {
                var fr = run3.Between(from, to).ToList();
                var top = Enumerable.Range(0, 12).OrderByDescending(k => fr.Average(x => x.Chroma[k])).Take(3).OrderBy(k => k);
                T.Info($"pad {from:F0}-{to:F0} s: chroma {string.Join(" ", Enumerable.Range(0, 12).Select(k => fr.Average(x => x.Chroma[k]).ToString("F2")))} top chroma {string.Join(",", top)}  pitch class {string.Join(",", fr.GroupBy(x => x.PitchClass).OrderByDescending(x => x.Count()).Take(2).Select(x => $"{x.Key}:{x.Count()}"))}  clarity {fr.Average(x => x.PitchClarity):F2}");
            }
        }

        foreach (var name in InstrumentSources.Names)
        {
            // ドラムのループを重ねたとき
            var (_, dl, dr, _) = InstrumentSources.Make(name);
            var dg = new SignalGen(48000, 8, seed: 21);
            dg.Loop(120, 0.2, 8, hats: true, snare: true, kickAmp: 0.5);
            for (int i = 0; i < dl.Length; i++) { dl[i] += dg.Buf[i]; dr[i] += dg.Buf[i]; }
            OnsetDetector.KickTune.PercGate = false;
            var dOff = new StereoRun(new AudioEngine(48000) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) }).Feed(dl, dr);
            OnsetDetector.KickTune.PercGate = true;
            T.Info($"   kicks without the gate {dOff.Events.Count(x => x.Kind == AudioEventKind.Kick && x.T - dOff.T0 > 2) / 6.0:F1}/s");
            var dRun = new StereoRun(new AudioEngine(48000) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) }).Feed(dl, dr);
            T.Info($"{name,-7} + drums: vocal {dRun.Mean(2, 8, f => f.Vocal):F2} guitar {dRun.Mean(2, 8, f => f.GuitarKeys):F2} pad {dRun.Mean(2, 8, f => f.Pad):F2} brass {dRun.Mean(2, 8, f => f.Brass):F2} bassLvl {dRun.Mean(2, 8, f => f.BassLevel):F2} kicks {dRun.Events.Count(x => x.Kind == AudioEventKind.Kick && x.T - dRun.T0 > 2) / 6.0:F1}/s");
        }
        foreach (var name in InstrumentSources.Names)
        {
            var (_, l, r, _) = InstrumentSources.Make(name);
            var e = new AudioEngine(48000) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
            var ins = e.Instruments;
            var cue = new List<double[]>();
            var run = new StereoRun(e);
            run.Feed(l, r);
            // 材料は最後のコマの値だけ取れるので、もう一度 1 回ずつ流して集める
            var e2 = new AudioEngine(48000) { Settings = e.Settings };
            var mid = new float[512];
            var side = new float[512];
            for (int i = 0; i + 512 <= l.Length; i += 512)
            {
                for (int k = 0; k < 512; k++) { mid[k] = 0.5f * (l[i + k] + r[i + k]); side[k] = 0.5f * (l[i + k] - r[i + k]); }
                e2.Push(mid, side, 1000 + i / 48000.0);
                var d = e2.Instruments;
                if (i > 2 * 48000) cue.Add(new[] { d.DiagVib, d.DiagPluck, d.DiagSteady, d.DiagCentroidHz, d.DiagHarmMH, d.Flatness, d.WidthMid });
            }
            double M(int j) => cue.Average(x => x[j]);
            T.Info($"{name,-7} vocal {run.Mean(2, 8, f => f.Vocal):F2} guitar {run.Mean(2, 8, f => f.GuitarKeys):F2} pad {run.Mean(2, 8, f => f.Pad):F2} brass {run.Mean(2, 8, f => f.Brass):F2}  " +
                $"| harmB {run.Mean(2, 8, f => f.HarmBass):F2} harmM {run.Mean(2, 8, f => f.HarmMid):F2} harmH {run.Mean(2, 8, f => f.HarmHigh):F2} percL {run.Mean(2, 8, f => f.PercLow):F2} percM {run.Mean(2, 8, f => f.PercMid):F2} percH {run.Mean(2, 8, f => f.PercHigh):F2} " +
                $"bassLvl {run.Mean(2, 8, f => f.BassLevel):F2} centre {run.Mean(2, 8, f => f.Centre):F2} side {run.Mean(2, 8, f => f.Side):F2} " +
                $"| vib {M(0):F1} pluck {M(1):F2} flux {M(2):F3} cent {M(3):F0} hmh {M(4):F2} flat {M(5):F2} width {M(6):F2}");
        }
    }
}

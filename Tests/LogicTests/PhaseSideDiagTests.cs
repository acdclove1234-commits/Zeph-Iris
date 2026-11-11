// 調べる用: 環境変数 PHASE_DIAG に曲のファイル。10 秒ごとに、物差しの拍（表）と拍の間（裏）で、帯ごとの立ち上がりの強さを比べる。
// アプリの拍が裏へずれる部分で、どの帯が「裏のほうが強い」のか（拍の位置の決め方を直す手がかり）
using ProFanController;

namespace LogicTests;

public static class PhaseSideDiagTests
{
    public static void TestPhaseSideDiag()
    {
        string? path = Environment.GetEnvironmentVariable("PHASE_DIAG");
        if (path is null || !File.Exists(path)) return;
        var mono = MusicWavTests.ReadWavMono(path, out int fs);
        var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
        var hops = new List<(double T, double Odf, double[] Bn)>();
        e.Onsets.DiagHop = (t, odf, bn) => hops.Add((t, odf, (double[])bn.Clone()));
        var run = new EngineRun(e).Feed(mono);
        var refHops = hops.Select(h => (h.T, (1.0 * h.Bn[0] + 0.8 * h.Bn[1] + 0.35 * h.Bn[2] + 0.15 * h.Bn[3]) / 2.3)).ToList();
        var beats = BeatReference.Track(refHops, out double bpm);
        var ts = hops.Select(h => h.T).ToArray();
        double At(double t, Func<(double T, double Odf, double[] Bn), double> f)
        {
            int k = Array.BinarySearch(ts, t);
            if (k < 0) k = ~k;
            double m = 0;
            for (int i = Math.Max(0, k - 3); i < Math.Min(ts.Length, k + 3); i++) if (Math.Abs(ts[i] - t) <= 0.025) m = Math.Max(m, f(hops[i]));
            return m;
        }
        T.Info($"ref {bpm:F1} BPM. per 10 s: on-beat / half-beat strength (ratio) for odf, low, low-mid, high-mid, high; kick events on / half");
        double end = mono.Length / (double)fs;
        var kicks = run.Events.Where(x => x.Kind == AudioEventKind.Kick).Select(x => x.T).ToList();
        for (double w = 10; w < end; w += 10)
        {
            var bs = beats.Where(t => t - run.T0 >= w - 10 && t - run.T0 < w).ToList();
            if (bs.Count < 4) continue;
            var halves = bs.Zip(bs.Skip(1), (a, b) => 0.5 * (a + b)).ToList();
            string R(Func<(double T, double Odf, double[] Bn), double> f)
            {
                double on = bs.Average(t => At(t, f)), off = halves.Average(t => At(t, f));
                return $"{on / Math.Max(1e-9, off),4:F2}";
            }
            int kOn = kicks.Count(k => bs.Any(b => Math.Abs(b - k) < 0.05)), kOff = kicks.Count(k => halves.Any(h => Math.Abs(h - k) < 0.05));
            T.Info($"  {w - 10,4:F0}-{w,3:F0}s  odf {R(h => h.Odf)}  low {R(h => h.Bn[0])}  lmid {R(h => h.Bn[1])}  hmid {R(h => h.Bn[2])}  high {R(h => h.Bn[3])}  kicks {kOn}/{kOff}");
        }
    }
}

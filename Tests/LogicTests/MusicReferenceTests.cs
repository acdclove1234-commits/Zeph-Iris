// ==================================================
// MusicReferenceTests.cs - 実際の曲での拍の正しさを測るための「物差し」
//   (曲全体を見てから拍を決める方法（動的計画法）で拍の並びを作り、アプリの拍（先を見ずに決める）と比べる。
//    合成音で物差しそのものが正しいことも確かめる)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

/// <summary>
/// 曲全体の立ち上がりの強さから、拍の並びを後から決める（Ellis 2007 の方法）。
///   1. テンポ: 立ち上がりの強さの自己相関に、120 BPM を中心にした重み（1 オクターブ幅）を掛けていちばん高いところ
///   2. 拍: C(t) = O(t) + max_τ [C(τ) − α·log²((t−τ)/P)]（τ は t − 2P 〜 t − P/2）を前から計算し、最後からたどる
/// 先を見られるので、アプリ（今までの音だけで決める）より正しくなりやすい。正解ではないが、食い違う曲を見つける物差しになる
/// </summary>
public static class BeatReference
{
    private const double Tightness = 400;
    /// <summary>テンポを選ぶとき、周期の何倍のそろい方を足すか（1・2・4・8 倍 = 拍・2 拍・1 小節・2 小節。付点 8 分の刻み（4 つで 3 拍）を拍と取り違えないように）</summary>
    public static int[] RefMultiples = { 1, 2, 4, 8 };

    public static List<double> Track(IReadOnlyList<(double T, double Odf)> hops, out double bpm)
    {
        bpm = 0;
        int n = hops.Count;
        var beats = new List<double>();
        if (n < 400) return beats;
        double fr = (n - 1) / (hops[^1].T - hops[0].T);
        var o = new double[n];
        double mean = 0;
        for (int i = 0; i < n; i++) mean += hops[i].Odf;
        mean /= n;
        double var = 0;
        for (int i = 0; i < n; i++) var += (hops[i].Odf - mean) * (hops[i].Odf - mean);
        double std = Math.Sqrt(var / n);
        if (std <= 1e-9) return beats;
        for (int i = 0; i < n; i++) o[i] = hops[i].Odf / std;

        // 1. テンポ: 自己相関を τ・2τ・4τ・8τ で足す（拍・2 拍・1 小節・2 小節。1 小節の繰り返しが強い曲で、3/2 倍・4/3 倍などを選ばないように）
        int lagMin = (int)Math.Floor(fr * 60 / 200), lagMax = (int)Math.Ceiling(fr * 60 / 50);
        int acfMax = Math.Min(n - 1, 8 * (lagMax + 1) + 8);
        var acf = new double[acfMax + 1];
        double m0 = mean / std;
        for (int lag = 0; lag <= acfMax; lag++)
        {
            double s = 0;
            for (int i = lag; i < n; i++) s += (o[i] - m0) * (o[i - lag] - m0);
            acf[lag] = s / (n - lag);
        }
        var score = new double[lagMax + 2];
        for (int lag = lagMin; lag <= lagMax + 1; lag++)
        {
            double s = 0;
            foreach (int k in RefMultiples)
            {
                double mx = double.NegativeInfinity;
                int tol = Math.Min(k - 1, 3);
                for (int v = k * lag - tol; v <= k * lag + tol; v++)
                    if (v >= 0 && v <= acfMax) mx = Math.Max(mx, acf[v]);
                if (!double.IsNegativeInfinity(mx)) s += mx;
            }
            double w = Math.Log2(lag / (fr * 0.5));
            score[lag] = s * Math.Exp(-0.5 * w * w);
        }
        int best = lagMin;
        for (int lag = lagMin; lag <= lagMax; lag++) if (score[lag] > score[best]) best = lag;
        double p = best;
        if (best > lagMin && best < lagMax)
        {
            double a = score[best - 1], b = score[best], c = score[best + 1];
            double d = a - 2 * b + c;
            if (Math.Abs(d) > 1e-12) p = best + 0.5 * (a - c) / d;
        }
        bpm = 60 * fr / p;

        // 2. 拍の並び
        var cum = new double[n];
        var back = new int[n];
        int lo = (int)Math.Round(p / 2), hi = (int)Math.Round(2 * p);
        for (int t = 0; t < n; t++)
        {
            double bestPrev = double.NegativeInfinity;
            int arg = -1;
            for (int tau = t - hi; tau <= t - lo; tau++)
            {
                if (tau < 0) continue;
                double l = Math.Log((t - tau) / p);
                double v = cum[tau] - Tightness * l * l;
                if (v > bestPrev) { bestPrev = v; arg = tau; }
            }
            cum[t] = o[t] + (arg >= 0 ? Math.Max(0, bestPrev) : 0);
            back[t] = arg >= 0 && bestPrev > 0 ? arg : -1;
        }
        int end = n - 1;
        for (int t = Math.Max(0, n - (int)Math.Round(p)); t < n; t++) if (cum[t] > cum[end]) end = t;
        for (int t = end; t >= 0; t = back[t]) beats.Add(hops[t].T);
        beats.Reverse();
        return beats;
    }
}

/// <summary>アプリの拍と物差しの拍を比べた結果（1 曲分）</summary>
public sealed record BeatEval(double RefBpm, double AppBpm, double FirstLockSec, double SyncedFrac, double ClearSyncedFrac, double ClearFrac,
    int Flashes, double OnFrac, double WrongFrac, double SwitchesPerMin, double MedianErrMs)
{
    public string Format() =>
        $"ref {RefBpm,5:F1} app {AppBpm,5:F1} BPM  first lock {FirstLockSec,4:F1} s  synced {SyncedFrac * 100,3:F0}% (clear parts {ClearSyncedFrac * 100,3:F0}% of {ClearFrac * 100,3:F0}%)  " +
        $"flashes {Flashes,4} on-beat {OnFrac * 100,3:F0}%  wrong-but-synced {WrongFrac * 100,3:F0}%  switches {SwitchesPerMin,4:F1}/min  |err| {MedianErrMs,4:F0} ms";
}

public static class BeatEvaluator
{
    /// <summary>物差しの拍から、これ以内なら「拍の上」（秒）</summary>
    public const double Tolerance = 0.07;
    /// <summary>直前の Evaluate で、裏拍（物差しの拍のちょうど間）で光った割合（調査用）</summary>
    [ThreadStatic] public static double HalfBeatShare;
    /// <summary>何秒ずつに区切って「拍がはっきりしている区間」「間違えたまま同期している区間」を見るか</summary>
    private const double WindowSec = 4.0;

    /// <param name="startSec">この秒数より後を測る（曲の始めの、まだつかめない時間を除く）</param>
    public static BeatEval Evaluate(EngineRun run, IReadOnlyList<(double T, double Odf)> hops, List<double> refBeats, double refBpm, double startSec = 8)
    {
        double t0 = run.T0 + startSec;
        double tEnd = run.Frames.Count > 0 ? run.Frames[^1].T : t0;

        // アプリの光る拍（拍の頭をまたいだ時刻）
        var flashes = new List<(double T, double Bpm)>();
        int synced = 0, total = 0, switches = 0;
        bool prevLocked = false;
        double firstLock = double.NaN;
        for (int i = 1; i < run.Frames.Count; i++)
        {
            var b = run.Beats[i];
            double ta = run.Frames[i - 1].T, tb = run.Frames[i].T;
            if (double.IsNaN(firstLock) && b.Locked) firstLock = tb - run.T0;
            if (tb < t0) { prevLocked = b.Locked; continue; }
            total++;
            if (b.Locked) synced++;
            if (prevLocked && !b.Locked) switches++;
            prevLocked = b.Locked;
            if (b.Locked && b.Crossed(ta, tb)) flashes.Add((b.NextBeatAfter(ta), b.Bpm));
        }

        // 物差しの拍との差（アプリが倍のテンポなら、物差しの拍の間も「拍の上」とする）
        double RefPeriodAt(double t)
        {
            int k = refBeats.BinarySearch(t);
            if (k < 0) k = ~k;
            k = Math.Clamp(k, 1, refBeats.Count - 1);
            return refBeats[k] - refBeats[k - 1];
        }
        double ErrTo(double t, double appBpm)
        {
            double p = RefPeriodAt(t);
            double ratio = appBpm * p / 60;
            double grid = Math.Abs(ratio - 2) < 0.12 ? 0.5 : 1;
            int k = refBeats.BinarySearch(t);
            if (k < 0) k = ~k;
            double bestErr = double.PositiveInfinity;
            for (int j = Math.Max(0, k - 2); j < Math.Min(refBeats.Count, k + 2); j++)
            {
                double pj = j + 1 < refBeats.Count ? refBeats[j + 1] - refBeats[j] : p;
                for (double h = 0; h < 1 - 1e-9; h += grid)
                {
                    double e = t - (refBeats[j] + h * pj);
                    if (Math.Abs(e) < Math.Abs(bestErr)) bestErr = e;
                }
            }
            return bestErr;
        }
        var errs = flashes.Select(f => ErrTo(f.T, f.Bpm)).ToList();
        int on = errs.Count(e => Math.Abs(e) <= Tolerance);
        // 物差しの拍のちょうど間（裏拍）で光った割合
        int half = flashes.Count(f => { double p = RefPeriodAt(f.T); double e = ErrTo(f.T - p / 2, f.Bpm); return Math.Abs(e) <= Tolerance && Math.Abs(ErrTo(f.T, f.Bpm)) > Tolerance; });
        HalfBeatShare = flashes.Count > 0 ? half / (double)flashes.Count : 0;

        // 区間ごと: 拍がはっきりしているか（物差しの拍の位置の強さ / 区間の平均）と、同期中に外れていないか
        int windows = 0, clear = 0, wrong = 0, syncedWin = 0;
        double clearSynced = 0;
        for (double w = t0; w + WindowSec <= tEnd; w += WindowSec)
        {
            windows++;
            var seg = hops.Where(h => h.T >= w && h.T < w + WindowSec).ToList();
            var rb = refBeats.Where(t => t >= w && t < w + WindowSec).ToList();
            double segMean = seg.Count > 0 ? seg.Average(h => h.Odf) : 0;
            double atBeats = rb.Count > 0 ? rb.Average(t => seg.Where(h => Math.Abs(h.T - t) <= 0.025).Select(h => h.Odf).DefaultIfEmpty(0).Max()) : 0;
            bool isClear = segMean > 1e-9 && atBeats / segMean >= 2.0;
            int frames = 0, lockedFrames = 0;
            for (int i = 0; i < run.Frames.Count; i++)
            {
                double t = run.Frames[i].T;
                if (t < w || t >= w + WindowSec) continue;
                frames++;
                if (run.Beats[i].Locked) lockedFrames++;
            }
            double share = frames > 0 ? lockedFrames / (double)frames : 0;
            if (isClear) { clear++; clearSynced += share; }
            var wf = flashes.Select((f, i) => (f, e: errs[i])).Where(x => x.f.T >= w && x.f.T < w + WindowSec).ToList();
            if (share >= 0.5)
            {
                syncedWin++;
                if (wf.Count >= 3 && wf.Count(x => Math.Abs(x.e) <= Tolerance) < 0.5 * wf.Count) wrong++;
            }
        }
        double minutes = Math.Max(1e-9, (tEnd - t0) / 60);
        var absErr = errs.Where(e => Math.Abs(e) <= Tolerance).Select(Math.Abs).OrderBy(x => x).ToList();
        return new BeatEval(refBpm, run.Beats[^1].Bpm, firstLock,
            total > 0 ? synced / (double)total : 0,
            clear > 0 ? clearSynced / clear : 0,
            windows > 0 ? clear / (double)windows : 0,
            flashes.Count, flashes.Count > 0 ? on / (double)flashes.Count : 0,
            syncedWin > 0 ? wrong / (double)syncedWin : 0,
            switches / minutes,
            absErr.Count > 0 ? absErr[absErr.Count / 2] * 1000 : double.NaN);
    }
}

/// <summary>
/// 調査用: 拍の確かさの値（BeatTracker.Tune）を変えて、録音した曲と合成音の結果を比べる。
/// 環境変数 BEAT_SWEEP に「名前=値;名前=値|名前=値…」（| で区切った組ごとに 1 回。空の組 = 今の値）、MUSIC_WAV に曲のフォルダー
/// </summary>
public static class BeatTuneSweepTests
{
    private static readonly System.Reflection.FieldInfo[] Fields =
        typeof(BeatTracker.Tune).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .Append(typeof(OnsetDetector).GetField("OdfWeights", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!)
            .Concat(typeof(OnsetDetector.SnareTune).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Select(f => f))
            .Concat(typeof(BeatHueEffect.Tune).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic))
            .Concat(typeof(BeatDriver).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Where(x => !x.IsLiteral && !x.IsInitOnly))
            .ToArray();

    public static Dictionary<System.Reflection.FieldInfo, object?> Snapshot() => Fields.ToDictionary(f => f, f => f.GetValue(null));

    public static void Restore(Dictionary<System.Reflection.FieldInfo, object?> s)
    {
        foreach (var (f, v) in s) f.SetValue(null, v);
    }

    public static void Apply(string spec)
    {
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=');
            var f = Fields.FirstOrDefault(x => x.Name == kv[0].Trim());
            if (f is null || kv.Length != 2) throw new ArgumentException($"unknown tune '{part}'");
            if (f.FieldType == typeof(int[])) { f.SetValue(null, kv[1].Split(',').Select(int.Parse).ToArray()); continue; }
            if (f.FieldType == typeof(double[])) { f.SetValue(null, kv[1].Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray()); continue; }
            double v = double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture);
            object value = f.FieldType == typeof(int) ? (object)(int)Math.Round(v) : f.FieldType == typeof(float) ? (object)(float)v : (object)v;
            f.SetValue(null, value);
        }
    }

    /// <summary>合成音の 11 曲で、合格の条件（TestBenchMusicStyles と同じ）を満たした数と、いちばん悪い値</summary>
    public static string BenchSummary()
    {
        var rs = MusicBench.Cases().AsParallel().Select(c => (c, r: MusicBench.Measure(c))).ToList();
        int ok = 0;
        var bad = new List<string>();
        foreach (var (c, r) in rs)
        {
            bool pass;
            if (c.Name.StartsWith("funk")) pass = r.Flashes == 0 || r.OnBeatFrac >= 0.9;
            else
            {
                bool tempoOk = r.TempoVerdict == "ok" || (c.HalfOk && r.TempoVerdict == "half") || (c.DoubleOk && r.TempoVerdict == "double");
                pass = tempoOk && r.LockedFrac >= 0.8 && r.OnBeatFrac >= 0.9 && r.MedianAbsErrMs <= 30;
            }
            if (pass) ok++; else bad.Add($"{c.Name} ({r.TempoVerdict}, locked {r.LockedFrac * 100:F0}%, on {r.OnBeatFrac * 100:F0}%)");
        }
        return $"bench {ok}/{rs.Count}" + (bad.Count > 0 ? $"  failing: {string.Join("; ", bad)}" : "");
    }

    public static void TestBeatTuneSweep()
    {
        string? sweep = Environment.GetEnvironmentVariable("BEAT_SWEEP");
        string? dir = Environment.GetEnvironmentVariable("MUSIC_WAV");
        if (sweep is null || dir is null || !Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.wav").OrderBy(f => f).ToList();
        bool perSong = Environment.GetEnvironmentVariable("BEAT_SWEEP_SONGS") == "1";
        foreach (var spec in sweep.Split('|'))
        {
            var saved = Snapshot();
            try
            {
                Apply(spec);
                var rs = files.AsParallel().AsOrdered().Select(f => MusicWavTests.AnalyzeSong(f)).ToList();
                T.Info($"[{(spec.Length == 0 ? "current" : spec)}]");
                if (perSong) foreach (var r in rs) T.Info("   " + MusicWavTests.FormatSong(r).Substring(0, 200) + $" off-beat {r.HalfBeat * 100:F0}%");
                T.Info($"   songs: synced {rs.Average(r => r.Beat.SyncedFrac) * 100:F0}% (clear parts {rs.Average(r => r.Beat.ClearSyncedFrac) * 100:F0}%), " +
                       $"on-beat {rs.Average(r => r.Beat.OnFrac) * 100:F0}%, wrong-but-synced {rs.Average(r => r.Beat.WrongFrac) * 100:F0}%, " +
                       $"first lock {rs.Where(r => !double.IsNaN(r.Beat.FirstLockSec)).Select(r => r.Beat.FirstLockSec).DefaultIfEmpty(double.NaN).Average():F1} s, " +
                       $"never locked {rs.Count(r => double.IsNaN(r.Beat.FirstLockSec))}/{rs.Count}, switches {rs.Average(r => r.Beat.SwitchesPerMin):F1}/min");
                T.Info("   " + BenchSummary());
            }
            finally { Restore(saved); }
        }
    }
}

/// <summary>調査用: 環境変数 SNARE_BACK に曲のファイル。物差しの 2・4 拍目（キックの少ないほうの 1 拍おき）で、中音の帯の強さとしきい値を並べる</summary>
public static class SnareBackbeatDiagTests
{
    public static void TestSnareBackbeatDiag()
    {
        string? path = Environment.GetEnvironmentVariable("SNARE_BACK");
        if (path is null || !File.Exists(path)) return;
        var mono = MusicWavTests.ReadWavMono(path, out int fs);
        var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
        var o = e.Onsets;
        var hops = new List<(double T, double Odf)>();
        var rows = new List<(double T, double[] Bn, double[] Thr, double[] Lv)>();
        o.DiagHop = (t, odf, bn) =>
        {
            hops.Add((t, odf));
            rows.Add((t, (double[])bn.Clone(), Enumerable.Range(0, 4).Select(k => o.Picker(k).Threshold).ToArray(), Enumerable.Range(0, 4).Select(k => o.Picker(k).Level).ToArray()));
        };
        var run = new EngineRun(e).Feed(mono);
        var beats = BeatReference.Track(hops, out double bpm).Where(t => t > run.T0 + 30).ToList();
        var kicks = run.Events.Where(x => x.Kind == AudioEventKind.Kick).Select(x => x.T).ToList();
        int even = beats.Where((t, i) => i % 2 == 0).Count(t => kicks.Any(k => Math.Abs(k - t) < 0.05));
        int odd = beats.Where((t, i) => i % 2 == 1).Count(t => kicks.Any(k => Math.Abs(k - t) < 0.05));
        int parity = even >= odd ? 1 : 0;
        T.Info($"ref {bpm:F1} BPM; backbeats = parity {parity}");
        foreach (var (t, label) in beats.Select((t, i) => (t, i % 2 == parity ? "2·4" : "1·3")).Take(16))
        {
            var near = rows.Where(r => Math.Abs(r.T - t) <= 0.03).ToList();
            if (near.Count == 0) continue;
            var pk = near.OrderByDescending(r => Math.Max(r.Bn[1], r.Bn[2])).First();
            T.Info($"  {t - run.T0,6:F2}s {label}  G0 {pk.Bn[0],5:F2}/{pk.Thr[0],5:F2}  G1 {pk.Bn[1],5:F2}/{pk.Thr[1],5:F2} (lv {pk.Lv[1]:F2})  G2 {pk.Bn[2],5:F2}/{pk.Thr[2],5:F2} (lv {pk.Lv[2]:F2})  G3 {pk.Bn[3],5:F2}/{pk.Thr[3],5:F2}");
        }
    }
}

public static class MusicReferenceTests
{
    /// <summary>物差しそのものの確かめ: 合成音（本当の拍が分かっている）で、物差しの拍が本当の拍（か、その倍・半分）に乗っている</summary>
    public static void TestReferenceFindsTheBeatsOnBench()
    {
        foreach (var c in MusicBench.Cases())
        {
            if (c.Name.StartsWith("funk") && Environment.GetEnvironmentVariable("REF_FUNK") != "1") continue; // 拍でない繰り返しの強い曲（物差しでも分からない）
            var e = new AudioEngine(c.Gen.Fs);
            var hops = new List<(double T, double Odf)>();
            e.Onsets.DiagHop = (t, odf, _) => hops.Add((t, odf));
            var run = new EngineRun(e).Feed(c.Gen.Buf);
            var beats = BeatReference.Track(hops, out double bpm);
            double ratio = bpm / c.Bpm;
            double grid = Math.Abs(ratio - 2) < 0.1 ? 0.5 : 1;
            var truth = c.Beats.Select(b => b + run.T0).ToList();
            var late = beats.Where(b => b - run.T0 > 4).ToList();
            int on = late.Count(b => truth.Any(t => Enumerable.Range(0, (int)Math.Round(1 / grid)).Any(h => Math.Abs(b - (t + h * grid * 60 / c.Bpm)) <= 0.05)));
            double frac = late.Count > 0 ? on / (double)late.Count : 0;
            // テンポが少しずつ変わる曲（116 → 124）は、全体のテンポと最後のテンポが 3% ほど違う
            bool tempoOk = Math.Abs(ratio - 1) < 0.04 || Math.Abs(ratio - 2) < 0.08 || Math.Abs(ratio - 0.5) < 0.02;
            T.Check(tempoOk && frac >= 0.9, $"reference on {c.Name}: {bpm:F1} BPM (true {c.Bpm}), {frac * 100:F0}% of its beats on the true beats");
        }
    }
}

/// <summary>調査用: 環境変数 CONF_PARTS に曲のフォルダー。曲ごとに、確かさの 3 つの部分の平均と、どれがいちばん足を引っぱったかの割合</summary>
public static class ConfPartsDiagTests
{
    public static void TestConfParts()
    {
        string? dir = Environment.GetEnvironmentVariable("CONF_PARTS");
        if (dir is null || !Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*.wav").OrderBy(x => x))
        {
            var mono = MusicWavTests.ReadWavMono(f, out int fs);
            var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
            var parts = new List<(double Err, double Ratio, double Hit)>();
            e.Tracker.DiagConf = (a, b, c) => parts.Add((a, b, c));
            new EngineRun(e).Feed(mono);
            if (parts.Count == 0) continue;
            int lowErr = parts.Count(p => p.Err <= Math.Min(p.Ratio, p.Hit));
            int lowRatio = parts.Count(p => p.Ratio < p.Err && p.Ratio <= p.Hit);
            int lowHit = parts.Count - lowErr - lowRatio;
            double n = parts.Count;
            T.Info($"{Path.GetFileNameWithoutExtension(f),-7} beats {parts.Count,4}  mean err {parts.Average(p => p.Err):F2} ratio {parts.Average(p => p.Ratio):F2} hit {parts.Average(p => p.Hit):F2}  " +
                   $"lowest: err {lowErr / n * 100:F0}% ratio {lowRatio / n * 100:F0}% hit {lowHit / n * 100:F0}%  conf≥0.55 {parts.Count(p => p.Err * p.Ratio * p.Hit >= 0.55) / n * 100:F0}%");
        }
    }
}

/// <summary>調査用: 環境変数 ONOFF_BANDS に曲のフォルダー。物差しの拍の上と、拍の間（裏拍）での、4 つの帯の強さの平均の比（拍の上 / 裏拍）</summary>
public static class OnOffBandsDiagTests
{
    public static void TestOnOffBands()
    {
        string? dir = Environment.GetEnvironmentVariable("ONOFF_BANDS");
        if (dir is null || !Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*.wav").OrderBy(x => x))
        {
            var mono = MusicWavTests.ReadWavMono(f, out int fs);
            var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
            var hops = new List<(double T, double Odf)>();
            var bands = new List<double[]>();
            e.Onsets.DiagHop = (t, odf, bn) => { hops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3)); bands.Add((double[])bn.Clone()); };
            var run = new EngineRun(e).Feed(mono);
            var beats = BeatReference.Track(hops, out double bpm).Where(t => t > run.T0 + 8).ToList();
            var times = hops.Select(h => h.T).ToList();
            double[] At(double t)
            {
                int k = times.BinarySearch(t - 0.025);
                if (k < 0) k = ~k;
                var m = new double[4];
                for (int i = k; i < hops.Count && hops[i].T <= t + 0.025; i++) for (int g = 0; g < 4; g++) m[g] = Math.Max(m[g], bands[i][g]);
                return m;
            }
            var on = new double[4];
            var off = new double[4];
            for (int i = 0; i + 1 < beats.Count; i++)
            {
                var a = At(beats[i]);
                var b = At(0.5 * (beats[i] + beats[i + 1]));
                for (int g = 0; g < 4; g++) { on[g] += a[g]; off[g] += b[g]; }
            }
            string R(int g) => off[g] > 1e-9 ? $"{on[g] / off[g],5:F2}" : "  inf";
            T.Info($"{Path.GetFileNameWithoutExtension(f),-7} ref {bpm,5:F1}  on/off: kick {R(0)}  low-mid {R(1)}  mid {R(2)}  hat {R(3)}");
        }
    }
}

/// <summary>調査用: 環境変数 TRACK_LOG に曲のファイルと BEAT_TUNE に値。拍の追跡の記録（拍ごとの行は除く）を、物差しの拍との差と並べる</summary>
public static class TrackLogDiagTests
{
    public static void TestTrackLog()
    {
        string? path = Environment.GetEnvironmentVariable("TRACK_LOG");
        if (path is null || !File.Exists(path)) return;
        var saved = BeatTuneSweepTests.Snapshot();
        try
        {
            BeatTuneSweepTests.Apply(Environment.GetEnvironmentVariable("BEAT_TUNE") ?? "");
            var mono = MusicWavTests.ReadWavMono(path, out int fs);
            var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
            var hops = new List<(double T, double Odf)>();
            e.Onsets.DiagHop = (t, odf, bn) => hops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
            var log = new List<string>();
            var tr = e.Tracker;
            BeatTracker.SideDiag = Environment.GetEnvironmentVariable("SIDE_DIAG") == "1";
            tr.DiagLog = m => { if (!m.StartsWith("beat ")) log.Add($"{m}  [{tr.Lock}]"); };
            var run = new EngineRun(e).Feed(mono);
            var beats = BeatReference.Track(hops, out double bpm);
            T.Info($"ref {bpm:F1} BPM");
            foreach (var l in log.Take(BeatTracker.SideDiag ? 4000 : 80)) T.Info("  " + l);
            // 10 秒ごと: アプリの拍（光る位置）が、物差しの拍からどれだけずれているか（周期の割合。0 = 表、0.5 = 裏）
            for (double w = 10; w < mono.Length / (double)fs; w += 10)
            {
                var ph = new List<double>();
                for (int i = 1; i < run.Frames.Count; i++)
                {
                    var b = run.Beats[i];
                    double t0 = run.Frames[i - 1].T, t1 = run.Frames[i].T;
                    if (t1 - run.T0 < w - 10 || t1 - run.T0 >= w || !b.Locked || !b.Crossed(t0, t1)) continue;
                    double tb = b.NextBeatAfter(t0);
                    int k = beats.BinarySearch(tb);
                    if (k < 0) k = ~k;
                    if (k < 1 || k >= beats.Count) continue;
                    double p = beats[k] - beats[k - 1];
                    double f = (tb - beats[k - 1]) / p;
                    ph.Add(f - Math.Floor(f));
                }
                if (ph.Count == 0) { T.Info($"  {w - 10,4:F0}-{w,3:F0}s: not locked"); continue; }
                int on = ph.Count(x => x < 0.15 || x > 0.85), off = ph.Count(x => x > 0.35 && x < 0.65);
                T.Info($"  {w - 10,4:F0}-{w,3:F0}s: {ph.Count,3} flashes, on {on * 100 / ph.Count,3}% off-beat {off * 100 / ph.Count,3}%");
            }
        }
        finally { BeatTuneSweepTests.Restore(saved); }
    }
}

/// <summary>
/// 調査用: スネアの見分け方の値を変えて比べる。環境変数 SNARE_SWEEP に「名前=値;…|…」、MUSIC_WAV に曲のフォルダー。
/// 2・4 拍目に中音がはっきり強い曲（×1.4 以上 = スネアのある曲）で、2・4 拍目を見つけた割合と、見つけたスネアが 2・4 拍目に乗った割合。
/// 合成音の音のテスト（AudioEngineTests・SnareDetectionTests・MusicFeelTests・MusicRoleTests）の失敗の数も出す
/// </summary>
public static class SnareSweepTests
{
    public static void TestSnareSweep()
    {
        string? sweep = Environment.GetEnvironmentVariable("SNARE_SWEEP");
        string? dir = Environment.GetEnvironmentVariable("MUSIC_WAV");
        if (sweep is null || dir is null || !Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.wav").OrderBy(f => f).ToList();
        foreach (var spec in sweep.Split('|'))
        {
            var saved = BeatTuneSweepTests.Snapshot();
            try
            {
                BeatTuneSweepTests.Apply(spec);
                var rs = files.AsParallel().AsOrdered().Select(f => MusicWavTests.AnalyzeSong(f)).ToList();
                T.Info($"[{(spec.Length == 0 ? "current" : spec)}]");
                foreach (var r in rs)
                    T.Info($"   {r.Name}  snare {r.Snare:F1}/s  on 2·4 {r.SnareOnBackbeat * 100,3:F0}% 1·3 {r.Front * 100,3:F0}% between {r.Half * 100,3:F0}%  2·4 found {r.BackbeatFound * 100,3:F0}%  (2·4 mid ×{r.BackbeatMidRatio:F2}{(r.BackbeatMidRatio >= 1.4 ? ", has backbeat" : "")})");
                var bb = rs.Where(r => r.BackbeatMidRatio >= 1.4).ToList();
                if (bb.Count > 0)
                    T.Info($"   backbeat songs ({bb.Count}): 2·4 found {bb.Average(r => r.BackbeatFound) * 100:F0}%, on 2·4 {bb.Average(r => r.SnareOnBackbeat) * 100:F0}%, snare {bb.Average(r => r.Snare):F2}/s");
                T.Info("   synthetic: " + RunSyntheticAudioTests());
            }
            finally { BeatTuneSweepTests.Restore(saved); }
        }
    }

    /// <summary>音の合成音のテストを、表示を出さずに走らせて、失敗したものの名前を返す</summary>
    private static string RunSyntheticAudioTests()
    {
        var names = new[] { "AudioEngineTests", "SnareDetectionTests", "MusicFeelTests", "MusicRoleTests" };
        var methods = typeof(SnareSweepTests).Assembly.GetTypes().Where(t => names.Contains(t.Name))
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Where(m => m.Name.StartsWith("Test") && m.GetParameters().Length == 0)).ToList();
        var outW = Console.Out;
        int f0 = T.Failed, p0 = T.Passed, n0 = T.Failures.Count;
        var info = new StringWriter();
        Console.SetOut(info);
        try { foreach (var m in methods) { try { m.Invoke(null, null); } catch (Exception ex) { T.Failed++; T.Failures.Add($"{m.Name} threw {ex.InnerException?.Message}"); } } }
        finally { Console.SetOut(outW); }
        var failed = T.Failures.Skip(n0).ToList();
        int fails = T.Failed - f0, passes = T.Passed - p0;
        // 調べるためのテストなので、ここでの失敗は全体の結果に数えない
        T.Failed = f0; T.Passed = p0; T.Failures.RemoveRange(n0, T.Failures.Count - n0);
        string dense = string.Join("; ", info.ToString().Split('\n').Where(l => l.Contains("dense ") && l.Contains("snares found")).Select(l => l.Trim()));
        return $"{passes} passed, {fails} failed{(failed.Count > 0 ? " — " + string.Join(" | ", failed.Take(4)) : "")}  [{dense}]";
    }
}


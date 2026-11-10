// ==================================================
// OctaveDiagTests.cs - 倍のテンポでつかんでいる区間と、本当に速い曲の区間を、何で見分けられるかの調査
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class OctaveDiagTests
{
    /// <summary>
    /// 調査（MUSIC_TIMING=wav のフォルダー）: ロック中に速いテンポ（130 BPM 以上）でつかんでいる 5 秒ごとの区間で、
    /// アプリの拍の 1 拍おき（偶数拍と奇数拍）の強さの比（小さいほど 1 拍おきに強弱がある）を、帯ごとに求める。
    /// 物差しの倍のテンポでつかんでいる区間と、物差しと同じ（本当に速い曲）区間で分けて、分布を比べる
    /// </summary>
    public static void TestOctaveAltDiag()
    {
        string? folder = Environment.GetEnvironmentVariable("MUSIC_TIMING");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        string[] names = { "bass", "lowmid", "mid", "high", "Low", "odf" };
        var dbl = names.Select(_ => new List<double>()).ToArray();
        var same = names.Select(_ => new List<double>()).ToArray();
        foreach (string wav in Directory.GetFiles(folder, "*.wav").Where(f => File.Exists(Path.ChangeExtension(f, ".done"))).OrderBy(f => f))
        {
            var mono = MusicWavTests.ReadWavMono(wav, out int fs);
            var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
            var hops = new List<(double T, double[] V)>();
            var refHops = new List<(double T, double Odf)>();
            e.Onsets.DiagHop = (t, odf, bn) =>
            {
                hops.Add((t, new[] { bn[0], bn[1], bn[2], bn[3], bn[0] + 0.8 * bn[1], odf }));
                refHops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
            };
            var run = new EngineRun(e).Feed(mono);
            BeatReference.Track(refHops, out double refBpm);
            double hopSec = hops.Count > 1 ? (hops[^1].T - hops[0].T) / (hops.Count - 1) : 0.0107;
            int nd = 0, ns = 0;
            var dblR = new List<double>();
            for (double w = run.T0 + 10; w + 5 <= run.Frames[^1].T; w += 2.5)
            {
                int mid = run.Frames.FindIndex(f => f.T >= w + 2.5);
                if (mid < 0) break;
                var b = run.Beats[mid];
                if (!b.Locked || b.Bpm < 130) continue;
                double r = b.Bpm / refBpm;
                bool isDbl = Math.Abs(r - 2) < 0.12, isSame = Math.Abs(r - 1) < 0.06;
                if (!isDbl && !isSame) continue;
                double p = 60 / b.Bpm, anchor = b.NextBeatAfter(w + 2.5);
                var beatTimes = new List<double>();
                for (double t = anchor - Math.Ceiling(2.5 / p) * p; t < w + 5; t += p) if (t >= w) beatTimes.Add(t);
                if (beatTimes.Count < 6) continue;
                for (int s = 0; s < names.Length; s++)
                {
                    double even = 0, odd = 0;
                    for (int k = 0; k < beatTimes.Count; k++)
                    {
                        double v = PeakNear(hops, beatTimes[k], 2 * hopSec, s);
                        if ((k & 1) == 0) even += v; else odd += v;
                    }
                    even /= (beatTimes.Count + 1) / 2;
                    odd /= beatTimes.Count / 2;
                    double mx = Math.Max(even, odd);
                    double ratio = mx > 1e-9 ? Math.Min(even, odd) / mx : 1;
                    (isDbl ? dbl : same)[s].Add(ratio);
                    if (isDbl && s == 4) dblR.Add(ratio);
                }
                if (isDbl) nd++; else ns++;
            }
            if (nd + ns > 0)
                T.Info($"{Path.GetFileNameWithoutExtension(wav)} ref {refBpm,5:F1}: fast windows double {nd}, same {ns}" +
                       (dblR.Count > 0 ? $"  (Low ratio in double windows: median {Q(dblR, 0.5):F2})" : ""));
        }
        for (int s = 0; s < names.Length; s++)
            T.Info($"{names[s],-6} double [{string.Join(" ", new[] { 0.1, 0.25, 0.5, 0.75, 0.9 }.Select(q => Q(dbl[s], q).ToString("F2")))}] (n {dbl[s].Count})   " +
                   $"same-fast [{string.Join(" ", new[] { 0.1, 0.25, 0.5, 0.75, 0.9 }.Select(q => Q(same[s], q).ToString("F2")))}] (n {same[s].Count})");
        // しきい値ごとに、倍の区間を何割直せて、本当に速い曲の区間を何割まちがえて半分にするか
        foreach (int s in new[] { 4, 5, 2 })
            foreach (double th in new[] { 0.35, 0.45, 0.55, 0.65 })
                T.Info($"{names[s],-6} ratio < {th:F2}: catches {dbl[s].Count(x => x < th) / (double)Math.Max(1, dbl[s].Count):P0} of double, " +
                       $"wrongly halves {same[s].Count(x => x < th) / (double)Math.Max(1, same[s].Count):P0} of same-fast");
    }

    /// <summary>
    /// 調査（MUSIC_TIMING）: 「ありがちさ」の速い側の幅（PriorFastWidthOct）を変えたとき、曲ごとの同じ・倍・半分の割合と、
    /// 全体の同期・拍の上の割合、合成音のベンチのテンポの判定
    /// </summary>
    public static void TestPriorFastWidthSweep()
    {
        string? folder = Environment.GetEnvironmentVariable("MUSIC_TIMING");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var files = Directory.GetFiles(folder, "*.wav").Where(f => File.Exists(Path.ChangeExtension(f, ".done"))).OrderBy(f => f).ToList();
        double saved = BeatTracker.Tune.PriorFastWidthOct;
        try
        {
            foreach (double wOct in (Environment.GetEnvironmentVariable("MUSIC_FASTWIDTH") ?? "1,0.8,0.65,0.5").Split(',').Select(double.Parse))
            {
                BeatTracker.Tune.PriorFastWidthOct = wOct;
                EvaluateAll(files, $"fast width {wOct:F2} oct");
            }
        }
        finally { BeatTracker.Tune.PriorFastWidthOct = saved; }
    }

    /// <summary>
    /// 調査（MUSIC_TIMING、MUSIC_HALVE="BPM:秒,…"。0:0 は切る）: ロック中に速いテンポから半分にする（LockedHalveBpm / LockedHalveSec）とき
    /// </summary>
    public static void TestLockedHalveSweep()
    {
        string? folder = Environment.GetEnvironmentVariable("MUSIC_TIMING");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var files = Directory.GetFiles(folder, "*.wav").Where(f => File.Exists(Path.ChangeExtension(f, ".done"))).OrderBy(f => f).ToList();
        double savedBpm = BeatTracker.Tune.LockedHalveBpm, savedSec = BeatTracker.Tune.LockedHalveSec;
        try
        {
            foreach (string item in (Environment.GetEnvironmentVariable("MUSIC_HALVE") ?? "0:0,140:2").Split(','))
            {
                var p = item.Split(':').Select(double.Parse).ToArray();
                BeatTracker.Tune.LockedHalveBpm = p[0];
                BeatTracker.Tune.LockedHalveSec = p[1];
                EvaluateAll(files, p[0] > 0 ? $"halve at {p[0]:F0} BPM after {p[1]:F1} s" : "halve off");
            }
        }
        finally
        {
            BeatTracker.Tune.LockedHalveBpm = savedBpm;
            BeatTracker.Tune.LockedHalveSec = savedSec;
        }
    }

    /// <summary>調査（MUSIC_TIMING、MUSIC_HALFSHIFT="1.3,2,3"）: 半拍ずれた位置へ取り直すのに必要な比（HalfShiftRatio）を変えたとき</summary>
    public static void TestHalfShiftSweep()
    {
        string? folder = Environment.GetEnvironmentVariable("MUSIC_TIMING");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var files = Directory.GetFiles(folder, "*.wav").Where(f => File.Exists(Path.ChangeExtension(f, ".done"))).OrderBy(f => f).ToList();
        double saved = BeatTracker.Tune.HalfShiftRatio;
        try
        {
            foreach (double r in (Environment.GetEnvironmentVariable("MUSIC_HALFSHIFT") ?? "1.3,2,3").Split(',').Select(double.Parse))
            {
                BeatTracker.Tune.HalfShiftRatio = r;
                EvaluateAll(files, $"half shift ratio {r:F1}");
            }
        }
        finally { BeatTracker.Tune.HalfShiftRatio = saved; }
    }

    /// <summary>全曲を流して、同じ・倍・半分の割合・同期・拍の上の割合と、合成音のベンチの判定を 1 行に（曲ごとの内訳も）</summary>
    private static void EvaluateAll(List<string> files, string label)
    {
        {
            {
                double same = 0, dbl = 0, half = 0, all = 0;
                int flashes = 0, onReal = 0, between = 0;
                var evals = new List<BeatEval>();
                var per = new List<string>();
                foreach (string f in files)
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    var mono = MusicWavTests.ReadWavMono(f, out int fs);
                    var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
                    var hops = new List<(double T, double Odf)>();
                    e.Onsets.DiagHop = (t, odf, bn) => hops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
                    var run = new EngineRun(e).Feed(mono);
                    var rb = BeatReference.Track(hops, out double refBpm);
                    var ev = BeatEvaluator.Evaluate(run, hops, rb, refBpm);
                    evals.Add(ev);
                    int s = 0, d = 0, h = 0, n = 0;
                    foreach (var b in run.Beats)
                    {
                        if (!b.Locked || b.Bpm <= 0) continue;
                        n++;
                        double r = b.Bpm / refBpm;
                        if (Math.Abs(r - 1) < 0.06) s++;
                        else if (Math.Abs(r - 2) < 0.12) d++;
                        else if (Math.Abs(r - 0.5) < 0.03) h++;
                    }
                    same += s; dbl += d; half += h; all += n;
                    // 厳しい見方: 光った時刻が物差しの拍（4 分音符）そのものに近いか、拍のちょうど間か（倍のテンポの 8 分音符は間に数える）
                    int f0 = flashes, on0 = onReal, bt0 = between;
                    for (int i = 1; i < run.Frames.Count; i++)
                    {
                        var b = run.Beats[i];
                        double ta = run.Frames[i - 1].T, tb = run.Frames[i].T;
                        if (tb - run.T0 < 8 || !b.Locked || !b.Crossed(ta, tb)) continue;
                        double err = Math.Abs(MusicTiming.ErrTo(rb, b.NextBeatAfter(ta)));
                        flashes++;
                        if (err <= BeatEvaluator.Tolerance) onReal++;
                        else if (Math.Abs(err - 30 / refBpm) <= BeatEvaluator.Tolerance) between++;
                    }
                    int nn = Math.Max(1, n);
                    int fn = Math.Max(1, flashes - f0);
                    per.Add($"{name}@{refBpm:F0}:{100 * s / nn}/{100 * d / nn}/{100 * h / nn}:{ev.SyncedFrac * 100:F0}:w{ev.WrongFrac * 100:F0}:on{100 * (onReal - on0) / fn}:bt{100 * (between - bt0) / fn}");
                }
                var bench = MusicBench.Cases().Select(MusicBench.Measure).ToList();
                T.Info($"{label}: synced {evals.Average(x => x.SyncedFrac):P0} on-beat {evals.Average(x => x.OnFrac):P0} on real beats {onReal / (double)Math.Max(1, flashes):P1} between beats {between / (double)Math.Max(1, flashes):P1} " +
                       $"wrong {evals.Average(x => x.WrongFrac):P0} same {same / Math.Max(1, all):P1} double {dbl / Math.Max(1, all):P1} half {half / Math.Max(1, all):P1} | bench " +
                       string.Join(" ", bench.Select(r => $"{r.Name.Split(' ')[0]}:{r.TempoVerdict}:{r.OnBeatFrac:P0}")));
                T.Info("   same/double/half:synced  " + string.Join("  ", per));
            }
        }
    }

    /// <summary>
    /// 調査（MUSIC_TIMING）: 倍のテンポでロックしている間、テンポの判定のどこで倍になっていたか。
    /// くし形のフィルター自体が速いテンポを選んでいた / 遅いテンポを選んだのを OctaveUp が倍にした / 判定は遅いのにロックが速いまま、に分ける
    /// </summary>
    public static void TestOctaveSourceDiag()
    {
        string? folder = Environment.GetEnvironmentVariable("MUSIC_TIMING");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        int[] tot = new int[4];
        string[] labels = { "comb picked fast", "OctaveUp doubled", "estimate slow, lock kept fast", "other" };
        foreach (string wav in Directory.GetFiles(folder, "*.wav").Where(f => File.Exists(Path.ChangeExtension(f, ".done"))).OrderBy(f => f))
        {
            var mono = MusicWavTests.ReadWavMono(wav, out int fs);
            var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
            var hops = new List<(double T, double Odf)>();
            e.Onsets.DiagHop = (t, odf, bn) => hops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
            var tr = e.Tracker;
            var samples = new List<(double T, int Oct, double CombBpm, double EstBpm)>();
            var run = new EngineRun(e);
            run.Feed(mono, before: s => samples.Add((run.T0 + s, tr.DiagOctave,
                tr.DiagCombTau > 0 ? 60 * tr.FrameRate / tr.DiagCombTau : 0, tr.PeriodSec > 0 ? 60 / tr.PeriodSec : 0)));
            BeatReference.Track(hops, out double refBpm);
            int[] c = new int[4];
            int k = 0;
            for (int i = 0; i < run.Frames.Count; i++)
            {
                var b = run.Beats[i];
                if (!b.Locked || b.Bpm <= 0 || Math.Abs(b.Bpm / refBpm - 2) >= 0.12) continue;
                while (k + 1 < samples.Count && samples[k + 1].T <= run.Frames[i].T) k++;
                var smp = samples[k];
                int cls = smp.Oct == 1 ? 1
                    : Math.Abs(Math.Log(smp.CombBpm / b.Bpm)) < 0.06 ? 0
                    : Math.Abs(Math.Log(2 * smp.CombBpm / b.Bpm)) < 0.06 || Math.Abs(Math.Log(2 * smp.EstBpm / b.Bpm)) < 0.06 ? 2 : 3;
                c[cls]++;
            }
            if (c.Sum() == 0) continue;
            for (int j = 0; j < 4; j++) tot[j] += c[j];
            T.Info($"{Path.GetFileNameWithoutExtension(wav)} ref {refBpm,5:F1}: " + string.Join(", ", Enumerable.Range(0, 4).Select(j => $"{labels[j]} {c[j] / (double)c.Sum():P0}")));
        }
        T.Info("ALL double-locked time: " + string.Join(", ", Enumerable.Range(0, 4).Select(j => $"{labels[j]} {tot[j] / (double)Math.Max(1, tot.Sum()):P1}")));
    }

    /// <summary>
    /// 調査（MUSIC_TIMING）: ロック中に、テンポの判定（倍・半分の直しの後）が「今の半分のテンポ」と言い続ける長さ。
    /// 物差しの倍でロックしている時間（直したい）と、物差しと同じでロックしている時間（直してはいけない）で、
    /// 「L 秒続いたら半分に切り替える」としたときに直せる時間と、まちがえて切り替える回数を数える。
    /// ロック中に半分にする直し（LockedHalveBpm）を切って測る（入れたままだと、続く長さが LockedHalveSec で切れてしまう）
    /// </summary>
    public static void TestOctaveRunDiag()
    {
        double saved = BeatTracker.Tune.LockedHalveBpm;
        BeatTracker.Tune.LockedHalveBpm = 0;
        try { OctaveRunDiag(); }
        finally { BeatTracker.Tune.LockedHalveBpm = saved; }
    }

    private static void OctaveRunDiag()
    {
        string? folder = Environment.GetEnvironmentVariable("MUSIC_TIMING");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        double[] ls = { 1, 2, 3, 5, 8, 12 };
        double lockedDbl = 0, lockedSame = 0;
        var fixedDbl = new double[ls.Length];
        var falseSame = new int[ls.Length];
        var caughtDbl = new int[ls.Length];
        var caughtSongs = ls.Select(_ => new HashSet<string>()).ToArray();
        var falseSongs = ls.Select(_ => new HashSet<string>()).ToArray();
        foreach (string wav in Directory.GetFiles(folder, "*.wav").Where(f => File.Exists(Path.ChangeExtension(f, ".done"))).OrderBy(f => f))
        {
            string name = Path.GetFileNameWithoutExtension(wav);
            var mono = MusicWavTests.ReadWavMono(wav, out int fs);
            var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
            var hops = new List<(double T, double Odf)>();
            e.Onsets.DiagHop = (t, odf, bn) => hops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
            var tr = e.Tracker;
            var samples = new List<(double T, double Bpm)>();
            var run = new EngineRun(e);
            run.Feed(mono, before: s =>
            {
                double tau = tr.DiagCombTau * (tr.DiagOctave == -1 ? 2 : tr.DiagOctave == 1 ? 0.5 : 1);
                samples.Add((run.T0 + s, tau > 0 ? 60 * tr.FrameRate / tau : 0));
            });
            BeatReference.Track(hops, out double refBpm);
            int k = 0;
            double runStart = double.NaN, prevT = double.NaN;
            int runCls = -1;
            var crossed = new bool[ls.Length];
            for (int i = 0; i < run.Frames.Count; i++)
            {
                var b = run.Beats[i];
                double t = run.Frames[i].T;
                double dt = double.IsNaN(prevT) ? 0 : t - prevT;
                prevT = t;
                int cls = -1;
                if (b.Locked && b.Bpm > 0)
                {
                    double r = b.Bpm / refBpm;
                    cls = Math.Abs(r - 1) < 0.06 ? 0 : Math.Abs(r - 2) < 0.12 ? 1 : -1;
                }
                if (cls == 0) lockedSame += dt; else if (cls == 1) lockedDbl += dt;
                while (k + 1 < samples.Count && samples[k + 1].T <= t) k++;
                bool halfEst = cls >= 0 && samples[k].Bpm > 0 && Math.Abs(Math.Log(2 * samples[k].Bpm / b.Bpm)) < 0.06;
                if (!halfEst || cls != runCls) { runStart = halfEst ? t : double.NaN; runCls = halfEst ? cls : -1; Array.Clear(crossed); }
                if (double.IsNaN(runStart)) continue;
                double len = t - runStart;
                for (int j = 0; j < ls.Length; j++)
                {
                    if (len < ls[j]) continue;
                    if (cls == 1) { fixedDbl[j] += dt; if (!crossed[j]) { caughtDbl[j]++; caughtSongs[j].Add(name); } }
                    else if (!crossed[j]) { falseSame[j]++; falseSongs[j].Add(name); }
                    crossed[j] = true;
                }
            }
        }
        T.Info($"locked: double {lockedDbl:F0} s, same {lockedSame:F0} s");
        for (int j = 0; j < ls.Length; j++)
            T.Info($"switch after {ls[j],4:F0} s of 'half': double time in such runs {fixedDbl[j] / Math.Max(1, lockedDbl):P0}, caught {caughtDbl[j]} times in {string.Join(",", caughtSongs[j])}; " +
                   $"wrong switches on same-tempo locks {falseSame[j]} ({falseSame[j] / Math.Max(1, lockedSame / 3600):F1}/h) in {string.Join(",", falseSongs[j])}");
    }

    /// <summary>
    /// 調査（MUSIC_TIMING）: ロック中でテンポが物差しと同じ 10 秒ごとの区間で、アプリの拍の上と、拍のちょうど間（半拍）の
    /// どちらに音の立ち上がりが強いか（帯ごとの「間 ÷ 上」。1 より大きいと間のほうが強い）と、キック・スネアの数。
    /// 物差しと合っている区間（ok）と半拍ずれている区間（OFF）を並べて出す。どちらの拍が本物かを音そのもので見るため
    /// </summary>
    public static void TestHalfBeatDiag()
    {
        string? folder = Environment.GetEnvironmentVariable("MUSIC_TIMING");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        string only = Environment.GetEnvironmentVariable("MUSIC_ONLY") ?? "";
        foreach (string wav in Directory.GetFiles(folder, "*.wav").Where(f => File.Exists(Path.ChangeExtension(f, ".done"))).OrderBy(f => f))
        {
            string name = Path.GetFileNameWithoutExtension(wav);
            if (only.Length > 0 && !only.Split(',').Contains(name)) continue;
            var mono = MusicWavTests.ReadWavMono(wav, out int fs);
            var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
            var hops = new List<(double T, double[] V)>();
            var refHops = new List<(double T, double Odf)>();
            var log = new List<string>();
            e.Onsets.DiagHop = (t, odf, bn) =>
            {
                hops.Add((t, new[] { bn[0], bn[1], bn[2], bn[3], odf }));
                refHops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
            };
            e.Tracker.DiagLog = s => log.Add(s);
            var run = new EngineRun(e).Feed(mono);
            var rb = BeatReference.Track(refHops, out double refBpm);
            double hopSec = (hops[^1].T - hops[0].T) / (hops.Count - 1);
            var kicks = run.Events.Where(x => x.Kind == AudioEventKind.Kick).Select(x => x.T).ToList();
            var snares = run.Events.Where(x => x.Kind == AudioEventKind.Snare).Select(x => x.T).ToList();
            T.Info($"--- {name} ref {refBpm:F1}");
            for (double w = run.T0 + 8; w + 10 <= run.Frames[^1].T; w += 10)
            {
                int mid = run.Frames.FindIndex(f => f.T >= w + 5);
                if (mid < 0) break;
                var b = run.Beats[mid];
                if (!b.Locked || b.Bpm <= 0) { T.Info($"  {w - run.T0,4:F0}s unlocked"); continue; }
                double p = 60 / b.Bpm, anchor = b.NextBeatAfter(w + 5);
                double Phase(double t) { double x = (t - anchor) / p; return x - Math.Floor(x); }
                var on = new double[5];
                var bt = new double[5];
                int nb = 0, agree = 0, halfOff = 0;
                for (double t = anchor - Math.Ceiling(5 / p) * p; t < w + 10; t += p)
                {
                    if (t < w) continue;
                    nb++;
                    for (int s = 0; s < 5; s++)
                    {
                        on[s] += PeakNear(hops, t, 2 * hopSec, s);
                        bt[s] += PeakNear(hops, t + p / 2, 2 * hopSec, s);
                    }
                    double err = Math.Abs(MusicTiming.ErrTo(rb, t));
                    if (err < 0.07) agree++;
                    else if (Math.Abs(err - 30 / refBpm) < 0.07) halfOff++;
                }
                string verdict = agree > 2 * halfOff ? "ok " : halfOff > 2 * agree ? "OFF" : "mix";
                int kOn = kicks.Count(t => t >= w && t < w + 10 && (Phase(t) < 0.12 || Phase(t) > 0.88));
                int kBt = kicks.Count(t => t >= w && t < w + 10 && Math.Abs(Phase(t) - 0.5) < 0.12);
                int sOn = snares.Count(t => t >= w && t < w + 10 && (Phase(t) < 0.12 || Phase(t) > 0.88));
                int sBt = snares.Count(t => t >= w && t < w + 10 && Math.Abs(Phase(t) - 0.5) < 0.12);
                string R(int s) => (bt[s] / Math.Max(1e-9, on[s])).ToString("F2");
                T.Info($"  {w - run.T0,4:F0}s {verdict} app {b.Bpm,5:F1} between/on: bass {R(0)} lowmid {R(1)} mid {R(2)} high {R(3)} odf {R(4)} | kick on {kOn} bt {kBt} | snare on {sOn} bt {sBt}");
            }
            foreach (string s in log.Where(s => s.StartsWith("acquire") || s.StartsWith("locked")).Take(40)) T.Info("  log: " + s);
        }
    }

    private static double PeakNear(List<(double T, double[] V)> hops, double t, double tol, int s)
    {
        int lo = 0, hi = hops.Count - 1;
        while (lo < hi) { int m = (lo + hi) / 2; if (hops[m].T < t - tol) lo = m + 1; else hi = m; }
        double best = 0;
        for (int i = lo; i < hops.Count && hops[i].T <= t + tol; i++) best = Math.Max(best, hops[i].V[s]);
        return best;
    }

    private static double Q(List<double> xs, double q)
    {
        if (xs.Count == 0) return double.NaN;
        var a = xs.OrderBy(x => x).ToArray();
        return a[Math.Clamp((int)Math.Round(q * (a.Length - 1)), 0, a.Length - 1)];
    }
}

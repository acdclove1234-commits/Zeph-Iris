// 拍の AI（Beat This!）: exe に埋め込んだモデルが読めて、合成のドラムの拍を正しい時刻に見つけるか。かかる時間も出す
using System.Diagnostics;
using ProFanController;

namespace LogicTests;

public static class BeatThisTests
{
    private static float[] To22k(float[] buf, int fs)
    {
        var r = new Resampler22k(fs);
        var outList = new List<float>();
        for (int i = 0; i < buf.Length; i += 4800)
            r.Push(buf.AsSpan(i, Math.Min(4800, buf.Length - i)), s => outList.AddRange(s.ToArray()));
        return outList.ToArray();
    }

    /// <summary>追跡の拍が、本物の拍（キック）からどれだけずれているか（周期の割合。0 = ぴったり、0.5 = 裏拍）</summary>
    private static double PhaseError(BeatTracker tr, IEnumerable<double> kicksAbs)
    {
        double p = tr.PeriodSec, l = tr.GridLastBeatT;
        if (!(p > 0) || !double.IsFinite(l)) return double.NaN;
        var errs = kicksAbs.Select(k => { double x = (k - l) / p; return Math.Abs(x - Math.Round(x)); }).OrderBy(x => x).ToList();
        return errs[errs.Count / 2];
    }

    public static void TestAssistFixesOffBeatLock()
    {
        using var bt = BeatThis.TryCreate(out string? err);
        T.Check(bt is not null, $"model loads ({err ?? "ok"})");
        if (bt is null) return;
        const double bpm = 120, t0 = 1000;
        var g = new SignalGen(48000, 60, seed: 11);
        g.DenseMix(bpm, 1, 59);

        // 追跡が自分で裏拍から戻る仕組み（半拍の取り直し・裏拍の確かめ）を止めて、AI だけで直せるかを見る
        var saved = BeatTuneSweepTests.Snapshot();
        BeatTuneSweepTests.Apply("HalfShiftRatio=100;SideLowRatio=0");
        try {
        foreach (bool push in new[] { false, true })
        {
            var e = new AudioEngine(48000);
            using var assist = new BeatAssist(bt, 48000) { Inline = true };
            e.Assist = assist;
            var tr = e.Tracker;
            bool pushed = false;
            new EngineRun(e, t0).Feed(g.Buf, before: s =>
            {
                // 20 秒で、わざと半拍ずらす（裏拍に張りついた状態にする）
                if (push && !pushed && s >= 20 && tr.PeriodSec > 0)
                {
                    tr.Steer(tr.PeriodSec, tr.GridLastBeatT + tr.PeriodSec / 2);
                    pushed = true;
                }
            });
            var late = g.KickTimes.Where(k => k > 50 && k < 58).Select(k => t0 + k);
            double ph = PhaseError(tr, late);
            T.Info($"{(push ? "pushed off-beat at 20 s" : "left alone")}: AI runs {assist.Runs}, agreed {assist.Agreed}, steered {assist.Steered}, final phase error {ph:F2} of a beat, {60 / tr.PeriodSec:F1} BPM");
            if (push)
            {
                T.Check(assist.Steered >= 1, "the AI noticed the off-beat lock and moved the tracker");
                T.Check(ph < 0.1, $"back on the beat by the end ({ph:F2})");
            }
            else
            {
                T.Check(assist.Steered <= 1 && assist.Agreed >= 1, $"no needless moves when the tracker is right (steered {assist.Steered})");
                T.Check(ph < 0.1, $"on the beat ({ph:F2})");
            }
        }
        } finally { BeatTuneSweepTests.Restore(saved); }
    }

    public static void TestFindsDrumBeats()
    {
        long before = Process.GetCurrentProcess().PrivateMemorySize64;
        var sw = Stopwatch.StartNew();
        using var bt = BeatThis.TryCreate(out string? err);
        T.Check(bt is not null, $"model loads from the exe ({err ?? "ok"})");
        if (bt is null) return;
        T.Info($"load {sw.ElapsedMilliseconds} ms, private memory +{(Process.GetCurrentProcess().PrivateMemorySize64 - before) / 1048576} MB");

        foreach (double bpm in new[] { 96.0, 128.0 })
        {
            var g = new SignalGen(48000, 45, seed: 7);
            g.Loop(bpm, 1, 44, hats: true, snare: true);
            var a = To22k(g.Buf, 48000);
            T.Check(Math.Abs(a.Length - 45 * BeatThis.SampleRate) < 2000, $"resampled to 22,050 Hz ({a.Length} samples)");
            sw.Restart();
            var (beats, downs) = bt.Analyze(a);
            T.Info($"{bpm} BPM: {beats.Count} beats, {downs.Count} downbeats, {sw.ElapsedMilliseconds} ms for 45 s");
            var ibi = beats.Zip(beats.Skip(1), (x, y) => y - x).OrderBy(x => x).ToList();
            double med = ibi.Count > 0 ? ibi[ibi.Count / 2] : 0;
            T.Check(Math.Abs(60 / med - bpm) / bpm < 0.02 || Math.Abs(30 / med - bpm) / bpm < 0.02,
                $"tempo found: {60 / Math.Max(med, 1e-9):F1} BPM for {bpm} (or half/double)");
            // 拍の時刻は、鳴らしたキックの時刻から 40 ミリ秒以内（AI の 1 コアは 20 ミリ秒）
            var kicks = g.KickTimes.Where(t => t > 5 && t < 40).ToList();
            int near = kicks.Count(k => beats.Any(b => Math.Abs(b - k) < 0.04));
            T.Check(near >= kicks.Count * 0.9, $"beats land on the kicks ({near}/{kicks.Count})");
        }
    }

    /// <summary>
    /// 音楽連動中は、少しずつ長さの違う 20 秒前後の音を何度も解析する。ONNX Runtime の作業用メモリを毎回返さないと、
    /// これで約 1.2 GB まで育った（実機のログで 120 MB → 1.1 GB）。返していれば、何度解析しても増えない
    /// </summary>
    public static void TestRepeatedAnalysisMemoryStaysFlat()
    {
        using var bt = BeatThis.TryCreate(out string? err);
        T.Check(bt is not null, $"model loads ({err ?? "ok"})");
        if (bt is null) return;
        var g = new SignalGen(48000, 26, seed: 5);
        g.Loop(120, 1, 25, hats: true, snare: true);
        var a = To22k(g.Buf, 48000);
        var rnd = new Random(3);
        var p = Process.GetCurrentProcess();
        long baseline = 0, peak = 0;
        for (int k = 0; k < 16; k++)
        {
            int n = Math.Min(a.Length, 20 * BeatThis.SampleRate + rnd.Next(0, 4 * BeatThis.SampleRate));
            bt.Analyze(a.AsSpan(0, n));
            p.Refresh();
            if (k == 1) baseline = p.PrivateMemorySize64;   // 1 回目・2 回目でモデルの準備が終わったあと
            if (k > 1) peak = Math.Max(peak, p.PrivateMemorySize64);
        }
        long grew = (peak - baseline) / 1048576;
        T.Info($"private memory after 2 runs {baseline / 1048576} MB, highest over the next 14 runs {peak / 1048576} MB");
        T.Check(grew < 200, $"repeated analysis does not grow memory (+{grew} MB over 14 runs; was about +1 GB)");
    }
}

public static class SongMemoryTests
{
    /// <summary>はっきりした始まりのある合成の曲: 4 秒はハイハットだけ、そのあとドラムとベース</summary>
    private static SignalGen Song(double bpm, int seed)
    {
        var g = new SignalGen(48000, 75, seed: seed);
        double p = 60 / bpm;
        for (double t = 0.5; t < 4.5; t += p / 2) g.Hat(t, 0.15);
        g.DenseMix(bpm, 4.5, 74);
        return g;
    }

    /// <summary>曲を流す。titleLead 秒だけ先に「曲が変わった」と知らせる。最初に本物の拍にロックした時刻（曲の頭から）を返す</summary>
    private static double Play(BeatThis bt, SongMemory mem, SignalGen g, double titleLead, out BeatAssist used)
    {
        const double t0 = 1000;
        var e = new AudioEngine(48000);
        var a = new BeatAssist(bt, 48000) { Inline = true, Memory = mem };
        e.Assist = a;
        used = a;
        var tr = e.Tracker;
        mem.BeginSong(SongMemory.KeyOf("song A"));
        double firstGood = double.NaN;
        var kicks = g.KickTimes.Select(k => t0 + k).ToList();
        new EngineRun(e, t0).Feed(g.Buf, before: s =>
        {
            if (!double.IsNaN(firstGood) || tr.Lock == BeatLock.Unlocked || !(tr.PeriodSec > 0)) return;
            double l = tr.GridLastBeatT, p = tr.PeriodSec;
            var near = kicks.Where(k => k > t0 + s - 6 && k < t0 + s).Select(k => { double x = (k - l) / p; return Math.Abs(x - Math.Round(x)); }).ToList();
            if (near.Count >= 4 && near.Average() < 0.08) firstGood = s;
        });
        mem.BeginSong(null);
        mem.Tick(t0 + 75, 0, false);   // 曲の終わりを伝える（次のコマで覚える）
        a.Dispose();
        return firstGood;
    }

    public static void TestRememberAndRecall()
    {
        using var bt = BeatThis.TryCreate(out string? err);
        T.Check(bt is not null, $"model loads ({err ?? "ok"})");
        if (bt is null) return;
        string dir = Path.Combine(Path.GetTempPath(), "zeph-songmem-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var g = Song(104, 21);
            var mem = new SongMemory(dir);
            double first = Play(bt, mem, g, 0, out var a1);
            T.Info($"first play: locked on the beat at {first:F1} s, AI runs {a1.Runs}, saved {mem.Saved}");
            T.Check(mem.Saved == 1 && mem.Count == 1, "the song's beats are remembered after the first play");

            var mem2 = new SongMemory(dir);   // アプリを起動し直した想定（ファイルから読む）
            double second = Play(bt, mem2, g, 0.7, out var a2);
            T.Info($"second play: locked on the beat at {second:F1} s, recalled {mem2.Recalled}, AI runs {a2.Runs}");
            T.Check(mem2.Recalled == 1, "recognised and aligned the remembered song");
            T.Check(second <= first, $"on the beat no later than the first time ({second:F1} s vs {first:F1} s)");
            T.Check(a2.Runs <= a1.Runs, $"the AI runs less often for a remembered song ({a2.Runs} vs {a1.Runs})");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    /// <summary>画面の「覚えた曲: N 曲」は Version が変わったときに出し直す: 保存・消去で変わり、覚えなかった曲では変わらない</summary>
    public static void TestVersionTracksSaves()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zeph-songmem-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mem = new SongMemory(dir);
            void Listen(string title, double t0, double sec)
            {
                mem.BeginSong(SongMemory.KeyOf(title));
                mem.Tick(t0, 0.5, true);
                var beats = Enumerable.Range(0, (int)(sec / 0.5)).Select(i => t0 + 0.5 + i * 0.5).ToList();
                mem.AddBeats(beats, beats.Where((_, i) => i % 4 == 0));
                mem.BeginSong(null);
                mem.Tick(t0 + sec, 0, false);   // 曲の終わり（ここで覚える）
            }
            int v0 = mem.Version;
            Listen("short", 1000, 20);
            T.Check(mem.Version == v0 && mem.Count == 0, "a song heard for only 20 s is not remembered and the count stays");
            Listen("song A", 2000, 60);
            T.Check(mem.Version == v0 + 1 && mem.Count == 1, $"saving a song bumps the version (count {mem.Count})");
            Listen("song B", 3000, 60);
            T.Check(mem.Version == v0 + 2 && mem.Count == 2, $"each saved song bumps it again (count {mem.Count})");
            mem.Clear();
            T.Check(mem.Version == v0 + 3 && mem.Count == 0, "clearing bumps the version and the count is 0");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}

/// <summary>
/// 調査用: 環境変数 MEMORY_WAV に曲のフォルダー。曲ごとに 3 回流して比べる:
/// ① AI なし ② AI あり・はじめて（覚える） ③ AI あり・2 回目（覚えた拍で合わせる）
/// </summary>
public static class SongMemoryBenchTests
{
    public static void TestSongMemoryBench()
    {
        string? dir = Environment.GetEnvironmentVariable("MEMORY_WAV");
        if (dir is null || !Directory.Exists(dir)) return;
        using var bt = BeatThis.TryCreate(out _);
        if (bt is null) { T.Check(false, "model loads"); return; }
        string memDir = Path.Combine(Path.GetTempPath(), "zeph-songmem-bench-" + Guid.NewGuid().ToString("N"));
        var rows = new List<(string Name, MusicWavTests.SongResult A, MusicWavTests.SongResult B, MusicWavTests.SongResult C, bool Recalled, int RunsB, int RunsC)>();
        try
        {
            foreach (var f in Directory.GetFiles(dir, "*.wav").OrderBy(x => x))
            {
                string key = SongMemory.KeyOf(Path.GetFileName(f));
                var a = MusicWavTests.AnalyzeSong(f);
                var mem = new SongMemory(memDir);
                BeatAssist? asB = null, asC = null;
                var b = MusicWavTests.AnalyzeSong(f, e => { e.Assist = asB = new BeatAssist(bt, e.SampleRate) { Inline = true, Memory = mem }; mem.BeginSong(key); });
                mem.EndSong();
                asB?.Dispose();
                var mem2 = new SongMemory(memDir);
                var c = MusicWavTests.AnalyzeSong(f, e => { e.Assist = asC = new BeatAssist(bt, e.SampleRate) { Inline = true, Memory = mem2 }; mem2.BeginSong(key); });
                asC?.Dispose();
                rows.Add((Path.GetFileNameWithoutExtension(f), a, b, c, mem2.Recalled > 0, asB?.Runs ?? 0, asC?.Runs ?? 0));
                T.Info($"{Path.GetFileNameWithoutExtension(f),-6} synced {a.Beat.SyncedFrac * 100,3:F0}% → AI {b.Beat.SyncedFrac * 100,3:F0}% → 2nd {c.Beat.SyncedFrac * 100,3:F0}%   " +
                       $"on-beat {a.Beat.OnFrac * 100,3:F0}% → {b.Beat.OnFrac * 100,3:F0}% → {c.Beat.OnFrac * 100,3:F0}%   " +
                       $"first lock {a.Beat.FirstLockSec,4:F1} → {b.Beat.FirstLockSec,4:F1} → {c.Beat.FirstLockSec,4:F1} s   recalled {mem2.Recalled > 0} offset {mem2.OffsetSec * 1000:F0} ms  AI runs {asB?.Runs} → {asC?.Runs}");
            }
            if (rows.Count == 0) return;
            double Avg(Func<MusicWavTests.SongResult, double> sel, int which) => rows.Average(r => sel(which == 0 ? r.A : which == 1 ? r.B : r.C));
            double Lock(int which) => rows.Select(r => (which == 0 ? r.A : which == 1 ? r.B : r.C).Beat.FirstLockSec).Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Average();
            T.Info($"average: synced {Avg(r => r.Beat.SyncedFrac, 0) * 100:F0}% → AI {Avg(r => r.Beat.SyncedFrac, 1) * 100:F0}% → 2nd {Avg(r => r.Beat.SyncedFrac, 2) * 100:F0}%, " +
                   $"on-beat {Avg(r => r.Beat.OnFrac, 0) * 100:F0}% → {Avg(r => r.Beat.OnFrac, 1) * 100:F0}% → {Avg(r => r.Beat.OnFrac, 2) * 100:F0}%, " +
                   $"wrong {Avg(r => r.Beat.WrongFrac, 0) * 100:F0}% → {Avg(r => r.Beat.WrongFrac, 1) * 100:F0}% → {Avg(r => r.Beat.WrongFrac, 2) * 100:F0}%, " +
                   $"first lock {Lock(0):F1} → {Lock(1):F1} → {Lock(2):F1} s, recalled {rows.Count(r => r.Recalled)}/{rows.Count}, AI runs {rows.Average(r => r.RunsB):F1} → {rows.Average(r => r.RunsC):F1} per song");
        }
        finally { try { Directory.Delete(memDir, true); } catch (IOException) { } }
    }
}

/// <summary>調査用: 環境変数 AI_DIAG に曲のファイル。AI（曲全体）の拍・物差しの拍・キックの位置を比べる</summary>
public static class BeatAiDiagTests
{
    public static void TestBeatAiDiag()
    {
        string? path = Environment.GetEnvironmentVariable("AI_DIAG");
        if (path is null || !File.Exists(path)) return;
        using var bt = BeatThis.TryCreate(out _);
        var mono = MusicWavTests.ReadWavMono(path, out int fs);
        var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
        var hops = new List<(double T, double Odf)>();
        e.Onsets.DiagHop = (t, odf, bn) => hops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
        var run = new EngineRun(e, 0).Feed(mono);
        var refBeats = BeatReference.Track(hops, out double refBpm);
        var kicks = run.Events.Where(x => x.Kind == AudioEventKind.Kick).Select(x => x.T).ToList();
        // AI: 20 秒の窓を 6 秒ずつずらして（アプリと同じ見方）、窓ごとのテンポ
        var r = new Resampler22k(fs);
        var a22 = new List<float>();
        r.Push(mono, s => a22.AddRange(s.ToArray()));
        var all = new List<double>();
        for (double w0 = 0; w0 + 20 <= a22.Count / 22050.0; w0 += 6)
        {
            var seg = a22.Skip((int)(w0 * 22050)).Take(20 * 22050).ToArray();
            var (b, d) = bt!.Analyze(seg);
            var g = BeatAssist.EstimateGrid(b.Select(x => x + w0).ToList(), d.Select(x => x + w0).ToList(), w0 + 20);
            var ibi = b.Zip(b.Skip(1), (x, y) => y - x).OrderBy(x => x).ToList();
            double raw = ibi.Count > 0 ? 60 / ibi[ibi.Count / 2] : 0;
            double phRef = double.NaN;
            if (g is { } gg)
            {
                var near = refBeats.Where(t => t > w0 + 5 && t < w0 + 18).Select(t => { double x = (t - gg.BeatT) / gg.Period; return Math.Abs(x - Math.Round(x)); }).ToList();
                if (near.Count > 0) phRef = near.Average();
            }
            T.Info($"{w0,5:F0}s AI raw {raw,6:F1} BPM grid {(g is { } q ? 60 / q.Period : 0),6:F1} BPM  phase vs ref {phRef:F2}");
            if (w0 == 0 || w0 % 30 < 6) all.AddRange(b.Select(x => x + w0).Where(x => x > w0 + 2 && x < w0 + 18.5));
        }
        double Near(IEnumerable<double> grid) { var gl = grid.OrderBy(x => x).ToList(); return kicks.Count(k => gl.Any(t => Math.Abs(t - k) < 0.06)) / (double)Math.Max(1, kicks.Count); }
        T.Info($"ref {refBpm:F1} BPM; kicks on ref beats {Near(refBeats) * 100:F0}%, kicks on AI beats {Near(all) * 100:F0}% (AI sampled windows)");
    }
}

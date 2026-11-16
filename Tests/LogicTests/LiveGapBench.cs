// ==================================================
// LiveGapBench.cs - 調査用: アプリの中（続けて何曲も流す・拍の AI は別のスレッドで遅れて届く・曲の記憶・音量）と、
//                   ベンチ（1 曲ずつ新しい分析・AI はその場で）で、テンポの追跡がロックしている割合がどれだけ違うかを比べる
//   ・LIVE_WAV=<フォルダー>（; で複数）のときだけ動く（中の *.wav を名前順に。ステレオならミッドとサイドに分けて流す）
//   ・表示するのは番号だけ（song_NN ならそのまま、それ以外は file_NN。曲名・パスは出さない）
// 調べる用の環境変数:
//   LIVE_CHAIN=1      1 つの分析で全部の曲を続けて流す（アプリと同じ。既定は 1 曲ずつ新しい分析 = ベンチと同じ）
//   LIVE_GAP=2        続けて流すとき、曲と曲の間に入れる無音（秒。既定 0 = 曲間なし）
//   LIVE_SEC=90       1 曲あたり流す長さ（秒）
//   LIVE_AI=inline|async|off（既定 inline。async は AI を別のスレッドで動かし、LIVE_AI_LAT 秒（音の時間）後に結果を届ける）
//   LIVE_AI_LAT=0.5   async のときの、頼んでから結果が届くまでの音の時間（秒）
//   LIVE_MEM=<フォルダー>  曲の記憶を使う（記憶はこのフォルダーに書く。曲の頭で BeginSong を呼ぶ）。LIVE_PASSES=2 で 2 周流す（2 周目は覚えた曲）
//   LIVE_GAIN_DB=-20  音量を変える（dB）
//   LIVE_SPEC=old|cur|new  SyncRegressionBench の決め方の組（既定 new = 今のコードの既定）
//   LIVE_JITTER=1     届く音の塊の大きさをばらつかせる（5〜30 ミリ秒。既定は 10 ミリ秒ずつ）
//   LIVE_SKIP=0.5     曲の頭のこの秒数を飛ばす（少しのずれで結果がどれだけ変わるか = ばらつきを見る）
//   LIVE_STEERFIX=1|2 AI で拍の並びを入れた直後に、追跡のテンポの記録を入れた周期に寄せる（直し方の案を製品を変えずに試す。FixAfterSteer）
//   LIVE_RESET に mem（テンポの記憶）・lowt（AI の表裏の判定に使う低音の記録）も指定できる。newsong = アプリと同じ曲の変わり目の知らせ（AudioEngine.NewSong）
//   LIVE_TUNE=MemFadeSec=20,MemBoost=0  BeatTracker.Tune の値を変える（終わったら戻す）
//   LIVE_DIAG=1       ロックが外れた理由・AI で直した内容を曲ごとに出す
//   LIVE_RESET=tempo|assist|both|all  続けて流すとき、曲の頭で追跡だけ（ResetTempo）・AI の音だけ（BeatAssist.Reset）・両方・全部（Reset）をやり直す
// 数え方は 2 通り出す: app = アプリの「音の統計」と同じ（音が鳴っている回だけ・曲の頭から）、bench = SyncRegressionBench と同じ（曲の頭 5 秒を除いた全部の回）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Reflection;
using System.Text.RegularExpressions;
using ProFanController;

namespace LogicTests;

public static class LiveGapBenchTests
{
    private sealed class SongStat
    {
        public string Id = "";
        public int AppN, AppLocked, AppSynced, BenchN, BenchLocked, BenchSynced;
        public double FirstLock = double.NaN;
        public int UnlockNoOnset, UnlockConf, UnlockSilence, UnlockOther;
        public int Steers, SteerSameUnlocked, SteerSameLocked, SteerOther, MemSteers, HalfShifts, AiRuns;
        /// <summary>AI で別のテンポに直したあと 2 秒で: テンポが残った・元に戻った（直したテンポから 6% 以上離れた）。直したあと 6 秒でロックが外れていた（テンポが同じでも）</summary>
        public int SteerKept, SteerReverted, SteerUnlocked6s;
        public double AiComputeSum;
        public int AiComputeN;
        public double AppLock => AppN > 0 ? AppLocked / (double)AppN : double.NaN;
        public double AppSync => AppN > 0 ? AppSynced / (double)AppN : double.NaN;
        public double BenchLock => BenchN > 0 ? BenchLocked / (double)BenchN : double.NaN;
        public double BenchSync => BenchN > 0 ? BenchSynced / (double)BenchN : double.NaN;
    }

    private static readonly Lazy<BeatThis?> Model = new(() => BeatThis.TryCreate(out _));

    private static readonly FieldInfo BusyField = typeof(BeatAssist).GetField("_busy", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo ResultField = typeof(BeatAssist).GetField("_result", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo LastLowField = typeof(BeatTracker).GetField("_lastLowOnset", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo NField = typeof(BeatTracker).GetField("_n", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo SilenceField = typeof(BeatTracker).GetField("_silenceDone", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo BadField = typeof(BeatTracker).GetField("_badBeats", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static FieldInfo TF(string n) => typeof(BeatTracker).GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!;

    public static void TestLiveGapBench()
    {
        string? dirs = Environment.GetEnvironmentVariable("LIVE_WAV");
        if (string.IsNullOrEmpty(dirs)) return;
        var files = dirs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists)
            .SelectMany((d, di) => Directory.GetFiles(d, "*.wav").OrderBy(x => x, StringComparer.Ordinal).Select(f => (Set: di, Path: f)))
            .ToList();
        if (files.Count == 0) { T.Skip("LIVE_WAV has no wav"); return; }
        bool chain = Environment.GetEnvironmentVariable("LIVE_CHAIN") == "1";
        double gap = D("LIVE_GAP", 0);
        double sec = D("LIVE_SEC", 90);
        // LIVE_SKIP=0.5: 曲の頭のこの秒数を飛ばす（ばらつきの大きさを見るため。結果が少しのずれでどれだけ変わるか）
        double skip = D("LIVE_SKIP", 0);
        string aiMode = Environment.GetEnvironmentVariable("LIVE_AI") ?? "inline";
        double lat = D("LIVE_AI_LAT", 0.5);
        string? memDir = Environment.GetEnvironmentVariable("LIVE_MEM");
        int passes = (int)D("LIVE_PASSES", 1);
        double gain = Math.Pow(10, D("LIVE_GAIN_DB", 0) / 20);
        bool jitter = Environment.GetEnvironmentVariable("LIVE_JITTER") == "1";
        bool diag = Environment.GetEnvironmentVariable("LIVE_DIAG") == "1";
        string spec = Environment.GetEnvironmentVariable("LIVE_SPEC") ?? "new";
        // LIVE_RESET=tempo|assist|both|all: 続けて流すとき、曲の頭で追跡だけ・AI の音だけ・両方・全部をやり直す（既定 none）
        string reset = Environment.GetEnvironmentVariable("LIVE_RESET") ?? "none";
        // LIVE_STEERFIX=1|2: AI・覚えた曲で拍の並びを入れた直後（次の回）に、追跡のテンポの記録（_post）を入れた周期に寄せ、
        //   別の候補への乗り換えの数を 0 に戻す。1 = 覚えたテンポが遠ければ忘れる・2 = 入れたテンポを覚えたテンポにする（直し方の案を、製品を変えずに試す）
        int steerFix = (int)D("LIVE_STEERFIX", 0);
        // LIVE_RESET_AT=1.5: LIVE_RESET を曲の頭からこの秒数後にする（曲が変わったと分かるまでの遅れ）
        double resetAt = D("LIVE_RESET_AT", 0);
        BeatThis? model = aiMode == "off" ? null : Model.Value;
        T.Info($"chain {chain} gap {gap:F1}s sec {sec:F0} AI {aiMode}{(aiMode == "async" ? $" lat {lat:F2}s" : "")} mem {(memDir is null ? "off" : "on")} passes {passes} gain {20 * Math.Log10(gain):F0} dB jitter {jitter} spec {spec} reset {reset}");

        // 決め方の組（SyncRegressionBench の Configs / Apply を借りる）
        var sb = typeof(SyncRegressionBenchTests);
        var saveM = sb.GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Static)!;
        var restoreM = sb.GetMethod("Restore", BindingFlags.NonPublic | BindingFlags.Static)!;
        var applyM = sb.GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Static)!;
        var parseM = sb.GetMethod("ParseCfg", BindingFlags.NonPublic | BindingFlags.Static)!;
        object saved = saveM.Invoke(null, null)!;
        var cfg = parseM.Invoke(null, new object?[] { spec });
        if (cfg is null) { T.Skip("LIVE_SPEC unknown"); return; }
        applyM.Invoke(null, new object?[] { cfg, saved, null, true });
        // LIVE_TUNE=MemFadeSec=20,MemBoost=0: BeatTracker.Tune の値を変える（終わったら戻す）
        var tuneSaved = new List<(FieldInfo F, object? V)>();
        foreach (string kv in (Environment.GetEnvironmentVariable("LIVE_TUNE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p2 = kv.Split('=', 2);
            var tf = typeof(BeatTracker.Tune).GetField(p2[0], BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            if (tf is null || p2.Length < 2) { T.Info($"unknown tune '{kv}'"); continue; }
            tuneSaved.Add((tf, tf.GetValue(null)));
            tf.SetValue(null, Convert.ChangeType(p2[1], tf.FieldType, System.Globalization.CultureInfo.InvariantCulture));
            T.Info($"tune {p2[0]} = {p2[1]}");
        }

        SongMemory? memory = null;
        if (!string.IsNullOrEmpty(memDir))
        {
            Directory.CreateDirectory(memDir);
            memory = new SongMemory(memDir);
        }

        var stats = new List<SongStat>();
        var rnd = new Random(12345);
        try
        {
            int fs0 = 0;
            AudioEngine? e = null;
            BeatAssist? assist = null;
            SongStat? cur = null;
            double tAudio = 1000;
            var frame = new AudioFrame();
            BeatLock prevLock = BeatLock.Unlocked;
            double songStart = 0;
            bool songHasSound = false;
            double asyncStart = double.NaN;
            // 直したテンポを確かめる時刻（音の時刻）と、直したテンポ・テンポを変えたか
            double chk2 = double.NaN, chk6 = double.NaN, steerBpm = 0;
            bool steerChanged = false;
            bool fixPending = false;

            void Attach(int fs)
            {
                assist?.Dispose();
                e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
                prevLock = BeatLock.Unlocked;
                if (model is not null)
                {
                    e.Assist = assist = new BeatAssist(model, fs) { Inline = aiMode == "inline", Memory = memory };
                    assist.Log = m =>
                    {
                        if (cur is null) return;
                        if (m.Contains("半拍")) cur.HalfShifts++;
                        else if (m.StartsWith("覚えた曲の拍")) { cur.MemSteers++; fixPending = steerFix > 0; }
                        else if (m.StartsWith("拍の AI に合わせる"))
                        {
                            cur.Steers++;
                            var mm = Regex.Match(m, @"合わせる: ([\d.]+) BPM（追跡は ([\d.]+) BPM・(\S+)）");
                            if (mm.Success)
                            {
                                double a = double.Parse(mm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), t = double.Parse(mm.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                                bool same = t > 0 && Math.Abs(a / t - 1) < 0.03;
                                if (same && mm.Groups[3].Value.StartsWith("ロックなし")) cur.SteerSameUnlocked++;
                                else if (same) cur.SteerSameLocked++;
                                else cur.SteerOther++;
                                steerBpm = a; steerChanged = !same; chk2 = tAudio + 2; chk6 = tAudio + 6;
                                fixPending = steerFix > 0;
                                if (diag) T.Info($"      {cur.Id} @{tAudio - songStart,5:F1}s {m}");
                            }
                        }
                    };
                    assist.Analyzed = r => { if (cur is not null && r.ComputeSec > 0) { cur.AiComputeSum += r.ComputeSec; cur.AiComputeN++; } };
                }
                else assist = null;
                // 回ごと（1 つ前の回の結果）: ロックの状態と、門・反応の強さ
                var eng = e;
                eng.Onsets.DiagHop = (t, odf, bn) =>
                {
                    if (fixPending) { fixPending = false; FixAfterSteer(eng.Tracker, steerFix); }
                    if (cur is null) return;
                    var lk = eng.BeatLock;
                    eng.Features.Latest(frame);
                    double s = t - songStart;
                    if (lk == BeatLock.Locked && double.IsNaN(cur.FirstLock)) cur.FirstLock = s;
                    if (frame.GateOpen && frame.Activity >= OnsetDetector.MinActivity)
                    {
                        cur.AppN++;
                        if (lk == BeatLock.Locked) cur.AppLocked++;
                        if (lk != BeatLock.Unlocked) cur.AppSynced++;
                    }
                    if (s >= 5 && songHasSound)
                    {
                        cur.BenchN++;
                        if (lk == BeatLock.Locked) cur.BenchLocked++;
                        if (lk != BeatLock.Unlocked) cur.BenchSynced++;
                    }
                    if (prevLock != BeatLock.Unlocked && lk == BeatLock.Unlocked)
                    {
                        var tr = eng.Tracker;
                        long n = (long)NField.GetValue(tr)!, last = (long)LastLowField.GetValue(tr)!;
                        bool silence = (bool)SilenceField.GetValue(tr)!;
                        int bad = (int)BadField.GetValue(tr)!;
                        double since = (n - last) / tr.FrameRate;
                        string why;
                        if (silence) { cur.UnlockSilence++; why = "silence"; }
                        else if (bad >= BeatTracker.Tune.LockBeats) { cur.UnlockConf++; why = "low confidence"; }
                        else if (since > 1.9) { cur.UnlockNoOnset++; why = $"no low onset {since:F1}s"; }
                        else { cur.UnlockOther++; why = "other"; }
                        if (diag) T.Info($"      {cur.Id} @{s,5:F1}s unlock: {why}");
                    }
                    // AI で直してから 2 秒後: テンポが残っているか（別のテンポに直したときだけ）・6 秒後: ロックしているか
                    if (t >= chk2)
                    {
                        chk2 = double.NaN;
                        double bpm = eng.Tracker.PeriodSec > 0 ? 60 / eng.Tracker.PeriodSec : 0;
                        if (steerChanged)
                        {
                            if (bpm > 0 && Math.Abs(Math.Log(bpm / steerBpm)) < 0.06) cur.SteerKept++;
                            else cur.SteerReverted++;
                            if (diag) T.Info($"      {cur.Id} @{s,5:F1}s steer to {steerBpm:F1} -> tracker {bpm:F1} BPM after 2 s");
                        }
                    }
                    if (t >= chk6)
                    {
                        chk6 = double.NaN;
                        if (lk == BeatLock.Unlocked) cur.SteerUnlocked6s++;
                    }
                    prevLock = lk;
                };
            }

            // 続けて流すときの、曲の頭でのやり直し（LIVE_RESET）
            void ApplyReset()
            {
                var parts = reset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Contains("tempo") || parts.Contains("both")) e!.ResetTempo();
                if (parts.Contains("assist") || parts.Contains("both")) assist?.Reset();
                if (parts.Contains("all")) e!.Reset();
                // newsong: アプリが曲名の見張りで曲が変わったと分かったときと同じ（AudioEngine.NewSong = 覚えているテンポだけを忘れる）
                if (parts.Contains("newsong")) e!.NewSong();
                // 分析の部品だけ（AudioEngine の中の _onsets・_bands・_kick・_inst・_hatPath など。, で複数）
                foreach (string part in reset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (part is "none" or "tempo" or "assist" or "both" or "all" or "newsong") continue;
                    // lowt: AI の拍を表か裏か決めるのに使う、直近の低音の記録（BeatAssist._lowT / _lowV。Reset では消えない）
                    // mem: 追跡のテンポの記憶（_memPeriod。BeatTracker.Reset では消えない。無音 4 秒の SilenceReset だけが消す）
                    if (part == "mem") { TF("_memPeriod").SetValue(e!.Tracker, 0.0); TF("_memBeats").SetValue(e!.Tracker, 0); continue; }
                    // post: テンポの記録（ビタビの _post）を半分だけ一様に戻す（SilenceReset と同じ）
                    if (part == "post") { var post = (double[])TF("_post").GetValue(e!.Tracker)!; for (int j = 0; j < post.Length; j++) post[j] = 0.5 * post[j] + 0.5 / post.Length; continue; }
                    if (part == "lowt")
                    {
                        if (assist is null) continue;
                        foreach (string fn in new[] { "_lowT", "_lowV" })
                            Array.Clear((double[])typeof(BeatAssist).GetField(fn, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(assist)!);
                        continue;
                    }
                    var fld = typeof(AudioEngine).GetField("_" + part, BindingFlags.NonPublic | BindingFlags.Instance);
                    var obj = fld?.GetValue(e);
                    var rm = obj?.GetType().GetMethod("Reset", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
                    if (rm is null) { T.Info($"no Reset for _{part}"); continue; }
                    rm.Invoke(obj, null);
                    if (obj!.GetType().IsValueType) fld!.SetValue(e, obj);
                }
            }

            void Push(float[] mid, float[] side, int fs, int from, int count)
            {
                int end = from + count;
                int i = from;
                var bm = new float[4096];
                var bs = new float[4096];
                while (i < end)
                {
                    int ms = jitter ? rnd.Next(5, 31) : 10;
                    int n = Math.Min(Math.Min(end - i, fs * ms / 1000), bm.Length);
                    bool stereo = side.Length >= i + n;
                    for (int k = 0; k < n; k++)
                    {
                        bm[k] = mid.Length > 0 ? mid[i + k] * (float)gain : 0;
                        bs[k] = stereo ? side[i + k] * (float)gain : 0;
                    }
                    e!.Push(new ReadOnlySpan<float>(bm, 0, n), stereo ? new ReadOnlySpan<float>(bs, 0, n) : ReadOnlySpan<float>.Empty, tAudio);
                    tAudio += n / (double)fs;
                    i += n;
                    // 別のスレッドの AI: 頼んでから lat 秒（音の時間）たつまで流し、そのあと結果が出るまで待つ（結果は次の回に使われる）
                    if (assist is not null && aiMode == "async" && (bool)BusyField.GetValue(assist)!)
                    {
                        if (double.IsNaN(asyncStart)) asyncStart = tAudio;
                        if (tAudio - asyncStart >= lat)
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            while (ResultField.GetValue(assist) is null && sw.Elapsed.TotalSeconds < 30) Thread.Sleep(1);
                            asyncStart = double.NaN;
                        }
                    }
                    else asyncStart = double.NaN;
                }
            }

            for (int pass = 0; pass < passes; pass++)
            {
                for (int fi = 0; fi < files.Count; fi++)
                {
                    var (set, path) = files[fi];
                    var (mid, side, fs) = SyncRegressionBenchTests.ReadWavStereo(path);
                    if (mid.Length == 0) continue;
                    int len = (int)Math.Min(mid.Length, sec * fs);
                    if (e is null || !chain || fs != fs0)
                    {
                        Attach(fs);
                        fs0 = fs;
                    }
                    // 曲と曲の間の無音（続けて流すときだけ）
                    if (chain && gap > 0 && fi + pass > 0)
                    {
                        cur = null;
                        Push(Array.Empty<float>(), Array.Empty<float>(), fs, 0, (int)(gap * fs));
                    }
                    // 続けて流すとき、曲の頭（LIVE_RESET_AT 秒後）で一部だけやり直す（どの状態の持ち越しが効いているかを調べる）
                    bool resetDue = chain && fi + pass > 0 && reset != "none";
                    if (resetDue && resetAt <= 0) { ApplyReset(); resetDue = false; }
                    cur = new SongStat { Id = $"{(char)('A' + set)}{SongNum(path, fi)}{(passes > 1 ? $"/p{pass + 1}" : "")}" };
                    songStart = tAudio;
                    songHasSound = true;
                    memory?.BeginSong(SongMemory.KeyOf(cur.Id[..^(passes > 1 ? 3 : 0)]));
                    int from = Math.Min(len, (int)(skip * fs));
                    if (resetDue)
                    {
                        // 曲が変わったと分かるまでの遅れ（タイトルの見張りは 1 秒ごと）を真似る
                        int cut = Math.Clamp(from + (int)(resetAt * fs), from, len);
                        Push(mid, side, fs, from, cut - from);
                        ApplyReset();
                        from = cut;
                    }
                    Push(mid, side, fs, from, len - from);
                    if (assist is not null) cur.AiRuns = assist.Runs;
                    stats.Add(cur);
                    var c = cur;
                    T.Info($"{c.Id,-8} app lock {c.AppLock * 100,3:F0}% sync {c.AppSync * 100,3:F0}% | bench lock {c.BenchLock * 100,3:F0}% sync {c.BenchSync * 100,3:F0}% | first {c.FirstLock,5:F1}s | " +
                           $"unlock noOnset {c.UnlockNoOnset} conf {c.UnlockConf} silence {c.UnlockSilence} other {c.UnlockOther} | AI steer {c.Steers} (same-tempo unlocked {c.SteerSameUnlocked}, same locked {c.SteerSameLocked}, other {c.SteerOther}) mem {c.MemSteers} half {c.HalfShifts} | tempo steers kept {c.SteerKept} reverted {c.SteerReverted}, unlocked 6 s after a steer {c.SteerUnlocked6s}" +
                           (c.AiComputeN > 0 ? $" | AI {c.AiComputeSum / c.AiComputeN * 1000:F0} ms" : ""));
                }
            }
            memory?.EndSong();
            assist?.Dispose();
        }
        finally
        {
            restoreM.Invoke(null, new[] { saved });
            foreach (var (f, v) in tuneSaved) f.SetValue(null, v);
        }

        double Avg(Func<SongStat, double> f) => stats.Select(f).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Average();
        int appN = stats.Sum(s => s.AppN), benchN = stats.Sum(s => s.BenchN);
        T.Info($"total {stats.Count} songs: app lock {Avg(s => s.AppLock) * 100:F1}% sync {Avg(s => s.AppSync) * 100:F1}% (time-weighted {stats.Sum(s => s.AppLocked) * 100.0 / Math.Max(1, appN):F1}% / {stats.Sum(s => s.AppSynced) * 100.0 / Math.Max(1, appN):F1}%) | " +
               $"bench lock {Avg(s => s.BenchLock) * 100:F1}% sync {Avg(s => s.BenchSync) * 100:F1}% | first lock {Avg(s => s.FirstLock):F1}s (never {stats.Count(s => double.IsNaN(s.FirstLock))}) | " +
               $"unlocks noOnset {stats.Sum(s => s.UnlockNoOnset)} conf {stats.Sum(s => s.UnlockConf)} silence {stats.Sum(s => s.UnlockSilence)} other {stats.Sum(s => s.UnlockOther)} | " +
               $"AI steers {stats.Sum(s => s.Steers)} (same-tempo unlocked {stats.Sum(s => s.SteerSameUnlocked)}, same locked {stats.Sum(s => s.SteerSameLocked)}, other {stats.Sum(s => s.SteerOther)}) mem {stats.Sum(s => s.MemSteers)} half {stats.Sum(s => s.HalfShifts)} | tempo steers kept {stats.Sum(s => s.SteerKept)} reverted {stats.Sum(s => s.SteerReverted)}, unlocked 6 s after a steer {stats.Sum(s => s.SteerUnlocked6s)}");
    }


    /// <summary>
    /// 直し方の案（LIVE_STEERFIX）: Steer の直後に、テンポの記録（ビタビの _post）を入れた周期のまわりだけにし、乗り換えの候補を消す。
    /// mode 1 = 覚えたテンポ（_memPeriod）が入れた周期（倍・半分を含む）から 4% 以上離れていれば忘れる・2 = 入れた周期を覚えたテンポにする
    /// </summary>
    private static void FixAfterSteer(BeatTracker tr, int mode)
    {
        double period = (double)TF("_period").GetValue(tr)!;
        if (!(period > 0)) return;
        var post = (double[])TF("_post").GetValue(tr)!;
        double sum = 0;
        for (int j = 0; j < post.Length; j++)
        {
            double d = Math.Log((tr.TauMin + j) / period) / 0.05;
            post[j] = Math.Exp(-0.5 * d * d) + 1e-6;
            sum += post[j];
        }
        for (int j = 0; j < post.Length; j++) post[j] /= sum;
        TF("_candidateCount").SetValue(tr, 0);
        double mem = (double)TF("_memPeriod").GetValue(tr)!;
        if (mode == 2) { TF("_memPeriod").SetValue(tr, period); TF("_memAtSec").SetValue(tr, (double)TF("_tNow").GetValue(tr)!); }
        else if (mem > 0)
        {
            double dm = Math.Min(Math.Abs(Math.Log(period / mem)), Math.Min(Math.Abs(Math.Log(period / (2 * mem))), Math.Abs(Math.Log(period / (0.5 * mem)))));
            if (dm >= 0.04) { TF("_memPeriod").SetValue(tr, 0.0); TF("_memBeats").SetValue(tr, 0); }
        }
    }

    private static double D(string name, double def) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : def;

    /// <summary>曲の番号（song_NN の NN。それ以外は並び順）</summary>
    private static string SongNum(string path, int index)
    {
        var m = Regex.Match(Path.GetFileNameWithoutExtension(path), @"^song_(\d{1,4})$");
        return m.Success ? int.Parse(m.Groups[1].Value).ToString("00") : (index + 1).ToString("00");
    }
}
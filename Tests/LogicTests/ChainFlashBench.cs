// ==================================================
// ChainFlashBench.cs - 調査用: 続けて録音した曲（曲の間に無音がない）を、録音したとおりに続けて流し、
//                      曲が変わった直後に「ビートで色替え」の光が新しい曲の拍に乗っているかを測る（CHAIN_WAV を渡したときだけ動く）
//   ・CHAIN_WAV=<フォルダー>: 中の song_NN.wav を番号順に。segments.txt（「song_NN<TAB>区切りの番号」）があれば、
//     番号が続いていて区切りの番号が同じファイルどうしだけを「曲間なしの曲の変わり目」とし、それ以外は分析を作り直す（続きではない）
//   ・表示するのは RNN（song_NN の NN）だけ。曲名・パスは出さない
//   ・アプリと同じに、ミッドとサイド・拍の AI（その場で）つきで AudioEngine に 10 ミリ秒ずつ流し、60 コマ／秒で描く
//     （光の立ち上がりの数え方は BeatFlashBench と同じ。LED ごとに明るくなった分の山）
//   ・物差しの拍は、ファイルごとに、そのファイルだけを新しい分析に流して求める（前の曲の音を混ぜない）。CHAIN_REF=<フォルダー> に覚えておける
//   ・曲名の見張り（MusicAi.WatchTitle → AudioEngine.NewSong）を真似る: 曲の変わり目から CHAIN_DELAY 秒（既定 1.0。none = 知らせない）後に NewSong
//   出す値: 変わり目ごと・まとめて、変わり目から 0-2・2-5・5-10・10-30 秒の、光の数・拍に乗った割合（±60 ms）・拍にも拍の間にも乗らない割合（stray）・
//           光った拍の割合（lit）。曲全体（各ファイルの 8 秒以降。BeatFlashBench と同じ）のロックの割合・ロック中の拍に乗った割合・lit・ロックが外れた回数
//           色替えのちかっとした光（BlipMeter を描いた値に。全部と、変わり目から 10 秒の間）・送った値すべての指紋（hash）も出す
// 調べる用: CHAIN_STYLES=BeatHue,Spectrum、CHAIN_DEV=strip,pny、CHAIN_AI=0（拍の AI なし）、CHAIN_FILES=02,03（この番号の曲だけ。区切りは作り直す）、
//           CHAIN_PAIRS=60（変わり目ごとに、前の曲の最後 60 秒と次の曲の頭 CHAIN_POST 秒（既定 30）だけを新しい分析で流す。速く比べる用）、
//           CHAIN_CROSS=1（曲の組をすべて試す: X の最後 CHAIN_PAIRS 秒（既定 45）→ Y の頭 CHAIN_POST 秒。実際に続けて流れた組でなくても、
//           曲の終わり方・始まり方は録音のまま。変わり目の数を増やして、ばらつきを小さくする）、CHAIN_DUMP=BeatHue/strip（変わり目の 10 秒の光を 1 つずつ）、
//           CHAIN_TUNE=BeatFlash+Tune.UnlockedGridGain=0.5;…（名前空間を除いた型の名前 + 項目。終わったら戻す）、
//           CHAIN_SKIPAT=90（CHAIN_CROSS と組で、X を 90 秒の手前で止める = 曲の途中で飛ばした）、
//           CHAIN_MIDSPLIT=90（同じ曲の 90 秒で NewSong だけ送る = 曲名だけ変わり拍は続く。DJ ミックス・つながったアルバム）、
//           負の CHAIN_DELAY（曲名が音より先に変わった）、CHAIN_AGE=0.15（NewSong に渡す知らせの遅れの見積もり。既定はアプリと同じ）、
//           CHAIN_MEM=<フォルダー>（曲の記憶を使う。区間の頭と NewSong で BeginSong。印は RNN の SHA-256 だけ）、
//           CHAIN_PASSES=2（同じ並びを 2 回流し、最後の回だけ数える。CHAIN_MEM と組で、覚えた曲）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ProFanController;

namespace LogicTests;

public static class ChainFlashBenchTests
{
    private const double Fps = 60, T0 = 1000.0, SongSkip = 8;
    private static readonly double[] WinEdges = { 0, 2, 5, 10, 30 };

    private static readonly Lazy<BeatThis?> Model = new(() => BeatThis.TryCreate(out _));

    /// <summary>ファイル 1 つ（番号・続きか）</summary>
    private sealed record Song(string Id, string Path, bool Continues);

    /// <summary>レーン（光り方 × デバイス）1 本</summary>
    private sealed class Lane
    {
        public required string Label;
        public required DeviceRenderer R;
        public required RenderInput In;
        public required Rgb[] Out;
        public long Cursor;
        public int[] Prev = Array.Empty<int>();
        public ulong Hash = 1469598103934665603UL;
        // 今のファイルのコマごとの明るくなった量
        public readonly List<double> U = new();
        public bool First = true;
        // 調べる用（CHAIN_DUMP）: コマごとの光のきっかけ（0 なし・1 予測した拍・2 キック）
        public readonly List<byte> Src = new();
        // 色替えのちかっとした光（ColorBlipDiag の BlipMeter。描いた値で）: 全部と、変わり目から 10 秒の間だけ
        public BlipMeter? Blip, BlipTr;
        public bool InTr;
        // 集計: [窓] の光の数・拍に乗った・stray・物差しの拍・光った拍（変わり目のあとだけ）
        public readonly int[] WN = new int[4], WOn = new int[4], WStray = new int[4], WBeats = new int[4], WLit = new int[4];
        // 曲全体（8 秒以降）: 全部 / ロック中
        public int SN, SOn, SLN, SLOn, SBeats, SLit, SLBeats, SLLit, SStray;
        public double SSecs, SLSecs;
    }

    public static void TestChainFlashBench()
    {
        string? dir = Environment.GetEnvironmentVariable("CHAIN_WAV");
        if (string.IsNullOrEmpty(dir)) return;
        if (!Directory.Exists(dir)) { T.Skip("CHAIN_WAV is not a folder"); return; }
        var songs = ListSongs(dir);
        if (songs.Count == 0) { T.Skip("CHAIN_WAV has no song_NN.wav"); return; }
        string[] styles = (Environment.GetEnvironmentVariable("CHAIN_STYLES") ?? MusicStyles.BeatHue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] devs = (Environment.GetEnvironmentVariable("CHAIN_DEV") ?? "strip,pny").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string delaySpec = Environment.GetEnvironmentVariable("CHAIN_DELAY") ?? "1.0";
        double delay = delaySpec == "none" ? double.NaN : double.Parse(delaySpec, CultureInfo.InvariantCulture);
        string? refDir = Environment.GetEnvironmentVariable("CHAIN_REF");
        BeatThis? model = Environment.GetEnvironmentVariable("CHAIN_AI") == "0" ? null : Model.Value;
        double pairs = D("CHAIN_PAIRS", 0), post = D("CHAIN_POST", 30);
        bool cross = Environment.GetEnvironmentVariable("CHAIN_CROSS") == "1";
        var only = Environment.GetEnvironmentVariable("CHAIN_FILES")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (only is not null)
        {
            var keep = new List<Song>();
            foreach (var s in songs)
                if (only.Contains(s.Id[1..])) keep.Add(keep.Count > 0 && keep[^1] == Prev(songs, s) ? s : s with { Continues = false });
            songs = keep;
        }
        var segs = new List<Seg>();
        double skipAt = D("CHAIN_SKIPAT", 0), midCut = D("CHAIN_MIDSPLIT", 0);
        if (midCut > 0)
        {
            // CHAIN_MIDSPLIT=秒: 同じ曲の途中で NewSong（曲名だけ変わり、拍は続く = DJ ミックス・つながったアルバム・ライブ盤）
            double pre = pairs > 0 ? pairs : 45;
            foreach (var x in songs)
                if (WavLen(x.Path) >= midCut + post + 2)
                {
                    segs.Add(new Seg(x, midCut - pre, midCut, false, false, x.Id));
                    segs.Add(new Seg(x, midCut, midCut + post, true, false, $"{x.Id}~"));
                }
        }
        else if (cross)
        {
            // 曲の組をすべて試す（X の最後 pre 秒 → Y の頭 post 秒。X と Y は違う曲。実際に続けて流れた組でなくても、曲の終わり方と始まり方は録音のまま）
            // CHAIN_SKIPAT=秒 なら X は [秒-pre, 秒)（曲の途中で飛ばした）
            double pre = pairs > 0 ? pairs : 45;
            foreach (var x in songs)
            {
                if (skipAt > 0 && WavLen(x.Path) < skipAt + 2) continue;
                foreach (var y in songs)
                    if (x != y)
                    {
                        segs.Add(skipAt > 0 ? new Seg(x, skipAt - pre, skipAt, false, false, x.Id) : new Seg(x, -pre, double.PositiveInfinity, false, false, x.Id));
                        segs.Add(new Seg(y, 0, post, true, false, $"{x.Id}>{y.Id}"));
                    }
            }
        }
        else if (pairs > 0)
        {
            for (int i = 1; i < songs.Count; i++)
                if (songs[i].Continues)
                {
                    segs.Add(new Seg(songs[i - 1], -pairs, double.PositiveInfinity, false, false, songs[i - 1].Id));
                    segs.Add(new Seg(songs[i], 0, post, true, false, songs[i].Id));
                }
        }
        else segs.AddRange(songs.Select(x => new Seg(x, 0, double.PositiveInfinity, x.Continues, true, x.Id)));
        // CHAIN_PASSES=2 なら同じ並びを 2 回流し、最後の回だけ数える（CHAIN_MEM と組で、覚えた曲）
        int passes = (int)D("CHAIN_PASSES", 1);
        if (passes > 1)
        {
            var one = segs.ToList();
            segs = new List<Seg>();
            for (int p = 0; p < passes - 1; p++) segs.AddRange(one.Select(x => x with { Warm = true }));
            segs.AddRange(one);
        }
        string? memDir = Environment.GetEnvironmentVariable("CHAIN_MEM");
        SongMemory? memory = string.IsNullOrEmpty(memDir) ? null : new SongMemory(memDir);
        bool hasNewSong = typeof(AudioEngine).GetMethod("NewSong") is not null;
        T.Info($"styles {string.Join(",", styles)}; devices {string.Join(",", devs)}; NewSong {(double.IsNaN(delay) ? "none" : $"{delay:F1} s after the change")}{(hasNewSong ? "" : " (this build has no NewSong)")}; " +
               $"beat AI {(model is null ? "off" : "on")}; {(cross ? $"all pairs of songs: {(pairs > 0 ? pairs : 45):F0} s before + {post:F0} s after" : pairs > 0 ? $"pairs: {pairs:F0} s before + {post:F0} s after each change" : "full chain")}");
        var saved = ApplyTune(Environment.GetEnvironmentVariable("CHAIN_TUNE") ?? "");
        try { Run(segs, styles, devs, delay, refDir, model, keepParts: !segs.All(x => x.Whole), memory); }
        finally { foreach (var (f, v) in saved) f.SetValue(null, v); }
    }

    private static Song? Prev(List<Song> all, Song s)
    {
        int i = all.IndexOf(s);
        return i > 0 && s.Continues ? all[i - 1] : null;
    }

    /// <summary>song_NN.wav を番号順に。segments.txt で、続いている（番号が 1 つ違いで区切りが同じ）かを決める</summary>
    private static List<Song> ListSongs(string dir)
    {
        var seg = new Dictionary<int, string>();
        string segPath = System.IO.Path.Combine(dir, "segments.txt");
        if (File.Exists(segPath))
            foreach (var line in File.ReadAllLines(segPath))
            {
                var p = line.Split('\t');
                var m = Regex.Match(p[0].Trim(), @"^song_(\d{1,4})$");
                if (p.Length >= 2 && m.Success) seg[int.Parse(m.Groups[1].Value)] = p[1].Trim();
            }
        var list = new List<(int N, string Path)>();
        foreach (var f in Directory.GetFiles(dir, "*.wav"))
        {
            var m = Regex.Match(System.IO.Path.GetFileNameWithoutExtension(f), @"^song_(\d{1,4})$");
            if (m.Success) list.Add((int.Parse(m.Groups[1].Value), f));
        }
        list.Sort((a, b) => a.N.CompareTo(b.N));
        var songs = new List<Song>();
        for (int i = 0; i < list.Count; i++)
        {
            bool cont = i > 0 && list[i].N == list[i - 1].N + 1 && seg.Count > 0
                        && seg.TryGetValue(list[i].N, out var a) && seg.TryGetValue(list[i - 1].N, out var b) && a == b;
            songs.Add(new Song($"R{list[i].N:00}", list[i].Path, cont));
        }
        return songs;
    }

    /// <summary>
    /// 流す区間 1 つ（ファイルの From〜To 秒。From が負なら最後の −From 秒。Continues = 前の区間から曲間なしで続く曲の変わり目。
    /// Whole = 曲全体を流す。Tag = 表示する名前）
    /// </summary>
    private sealed record Seg(Song S, double From, double To, bool Continues, bool Whole, string Tag, bool Warm = false);

    /// <summary>音（区間だけ流すときは、要る部分だけ覚えておく。ファイルは 1 回だけ読む）</summary>
    private sealed class Audio
    {
        private readonly Dictionary<(string, double), (float[] M, float[] S, int Fs, double Len, double From)> _part = new();
        public bool KeepParts;

        /// <summary>区間の音・サンプリング周波数・ファイルの長さ（秒）・区間の始まり（秒）</summary>
        public (float[] M, float[] S, int Fs, double Len, double From) Get(Seg g, IReadOnlyList<Seg> all)
        {
            if (!KeepParts)
            {
                var (m, sd, fs) = ReadWav(g.S.Path);
                double l = m.Length / (double)Math.Max(1, fs);
                return Slice(m, sd, fs, l, g.From, g.To);
            }
            if (_part.TryGetValue((g.S.Path, g.From), out var p)) return p;
            // そのファイルの要る区間をまとめて切り出して覚える
            var (mid, side, rate) = ReadWav(g.S.Path);
            double len = mid.Length / (double)Math.Max(1, rate);
            foreach (var x in all.Where(x => x.S.Path == g.S.Path).Select(x => (x.From, x.To)).Distinct())
                _part[(g.S.Path, x.From)] = Slice(mid, side, rate, len, x.From, x.To);
            return _part[(g.S.Path, g.From)];
        }

        private static (float[] M, float[] S, int Fs, double Len, double From) Slice(float[] mid, float[] side, int rate, double len, double from, double to)
        {
            double f = from < 0 ? Math.Max(0, len + from) : Math.Min(from, len);
            double t = Math.Clamp(to, f, len);
            int a = (int)Math.Round(f * rate), b = Math.Min(mid.Length, (int)Math.Round(t * rate));
            if (a == 0 && b == mid.Length) return (mid, side, rate, len, 0);
            return (mid[a..b], side.Length >= b ? side[a..b] : Array.Empty<float>(), rate, len, a / (double)rate);
        }
    }

    private static void Run(List<Seg> segs, string[] styles, string[] devs, double delay, string? refDir, BeatThis? model, bool keepParts, SongMemory? memory = null)
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyIntensity(cfg.Music, Intensities.Moderate);
        var newSong = typeof(AudioEngine).GetMethod("NewSong");
        var steerCount = typeof(BeatTracker).GetProperty("SteerCount");
        var allLanes = new Dictionary<string, Lane>();
        // 変わり目ごとの結果（表示用）
        var perTr = new List<(string Id, string Lane, int[] N, int[] On, int[] Stray, int[] Beats, int[] Lit)>();
        var trackerTr = new List<string>();
        int unlocks = 0, demotes = 0;
        double unlockSecs = 0;
        var audio = new Audio { KeepParts = keepParts };
        var refCache = new Dictionary<string, (List<double> B, double Bpm)>();
        // 拍の AI の結果（調べる用）: AI の拍と物差しの拍のずれ・周期の比・直したあとの追跡の拍のずれ・回数
        int aiRuns = 0, aiAgreed = 0, aiSteered = 0;
        var aiOff = new List<double>();
        var aiPeriod = new List<double>();
        var steerOff = new List<double>();
        List<double>? curRef = null;
        double curFileStart = 0, curLo = double.PositiveInfinity;
        void OnAi(BeatAssist.Result r)
        {
            if (curRef is null || curRef.Count < 8) return;
            double lo = Math.Max(curLo, r.WindowEndT - BeatAssist.EdgeSec - 16), hi = r.WindowEndT - BeatAssist.EdgeSec;
            var bs = r.Beats.Where(b => b >= lo && b <= hi).ToList();
            if (bs.Count < 8) return;
            foreach (double b in bs) aiOff.Add(-Nearest(curRef, b - curFileStart));
            var rs = curRef.Where(t => t + curFileStart >= bs[0] && t + curFileStart <= bs[^1]).Select(t => t + curFileStart).ToList();
            if (FitPeriod(bs) is { } pa && FitPeriod(rs) is { } pr) aiPeriod.Add(pa / pr);
        }
        void TakeAi(BeatAssist? a)
        {
            if (a is null) return;
            aiRuns += a.Runs; aiAgreed += a.Agreed; aiSteered += a.Steered;
        }

        AudioEngine? e = null;
        BeatAssist? assist = null;
        List<Lane> lanes = new();
        double tAudio = T0, nextFrame = T0, lastFrame = double.NaN;

        void Attach(int fs)
        {
            TakeAi(assist);
            assist?.Dispose();
            e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(cfg) };
            assist = model is null ? null : new BeatAssist(model, AiRate(e)) { Inline = true, Memory = memory };
            if (assist is not null) assist.Analyzed = OnAi;
            if (assist is not null) e.Assist = assist;
            lanes = new List<Lane>();
            foreach (var dn in devs)
                foreach (var st in styles)
                {
                    var dev = dn == "pny" ? new RgbDevice(5, "PNY GeForce RTX ARGB Epic-X", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }) { DirectGpu = true }
                                          : new RgbDevice(0, "Strip", 24, 4, null, "HID: x");
                    var music = MusicTuning.Resolve(cfg) with { Effect = st };
                    var input = new RenderInput { Cfg = cfg, Music = music, Device = new RgbDeviceSettings { Name = dev.Name } };
                    string label = $"{st}/{dn}";
                    var L = new Lane { Label = label, R = new DeviceRenderer(dev), In = input, Out = new Rgb[dev.LedCount], Prev = new int[dev.LedCount] };
                    // 集計は続ける（作り直しても同じレーン）
                    if (allLanes.TryGetValue(label, out var acc)) CopyAcc(acc, L);
                    else { L.Blip = new BlipMeter(dev.LedCount); L.BlipTr = new BlipMeter(dev.LedCount) { MaxExamples = 30 }; }
                    allLanes[label] = L;
                    lanes.Add(L);
                }
            tAudio += 10;   // 作り直したら時刻を少し空ける
            nextFrame = tAudio;
            lastFrame = double.NaN;
        }

        for (int si = 0; si < segs.Count; si++)
        {
            var seg = segs[si];
            var song = seg.S;
            var (mid, side, fs, len, from) = audio.Get(seg, segs);
            if (mid.Length == 0) continue;
            if (!refCache.TryGetValue(song.Id, out var rc))
            {
                // 物差しの拍はファイル全体で（覚えていなければ、ファイルを丸ごと読む）
                bool cached = refDir is not null && File.Exists(System.IO.Path.Combine(refDir, song.Id + ".txt"));
                var whole = cached ? Array.Empty<float>() : from == 0 && mid.Length >= (int)(len * fs) - 1 ? mid : ReadWav(song.Path).Mid;
                rc = (RefBeats(song, whole, fs, refDir, out double bpm0), bpm0);
                refCache[song.Id] = rc;
            }
            var refBeats = rc.B;
            double refBpm = rc.Bpm;
            // 続きでなければ、分析を作り直す（曲の切れ目ではないので、前の曲の続きにしない）
            if (e is null || !seg.Continues || e.SampleRate != fs) Attach(fs);
            double fileStart = tAudio - from;
            curRef = refBeats;
            curFileStart = fileStart;
            curLo = fileStart + (seg.Continues ? from + 2 : from);
            if (memory is not null && !seg.Continues) memory.BeginSong(SongMemory.KeyOf(song.Id));
            double tr0 = seg.Continues ? from : 0;
            bool notify = seg.Continues && !double.IsNaN(delay) && delay >= 0 && newSong is not null;
            double notifyAt = fileStart + tr0 + (double.IsNaN(delay) ? 0 : delay);
            // 負の CHAIN_DELAY = 曲名が音より先に変わった（次の区間の変わり目の −delay 秒前に NewSong）
            bool early = !double.IsNaN(delay) && delay < 0 && si + 1 < segs.Count && segs[si + 1].Continues && newSong is not null;
            double earlyAt = tAudio + mid.Length / (double)fs + (double.IsNaN(delay) ? 0 : delay);
            var frameLocks = new List<BeatLock>();
            var played = new List<bool>();
            var grid = new List<(double T, BeatLock L)>();
            foreach (var L in lanes) { L.U.Clear(); L.Src.Clear(); }
            string startState = $"{e!.BeatLock} {e.Beat.Bpm:F1} BPM conf {e.Tracker.Confidence:F2}";
            int chunk = fs / 100;
            var trackerLog = new StringBuilder();
            BeatLock prevLock = e!.BeatLock;
            int prevSteer = steerCount is null ? 0 : (int)steerCount.GetValue(e.Tracker)!;
            for (int pos = 0; pos < mid.Length; pos += chunk)
            {
                int n = Math.Min(chunk, mid.Length - pos);
                var sd = side.Length >= pos + n ? new ReadOnlySpan<float>(side, pos, n) : ReadOnlySpan<float>.Empty;
                e!.Push(mid.AsSpan(pos, n), sd, tAudio);
                tAudio += n / (double)fs;
                if (notify && tAudio >= notifyAt) { notify = false; CallNewSong(newSong!, e, NoticeAge(delay)); memory?.BeginSong(SongMemory.KeyOf(song.Id)); trackerLog.Append($" {tAudio - fileStart - tr0:F1}:newsong"); }
                if (early && tAudio >= earlyAt) { early = false; CallNewSong(newSong!, e, NoticeAge(delay)); memory?.BeginSong(SongMemory.KeyOf(segs[si + 1].S.Id)); }
                while (nextFrame <= tAudio)
                {
                    double t = nextFrame;
                    var lk = e.BeatLock;
                    double s = t - fileStart;
                    // 予測した拍の頭（このコマまでにまたいだもの）
                    var b = e.Beat;
                    if (b.HasTempo && !double.IsNaN(lastFrame))
                    {
                        double k = Math.Floor((t - b.LastBeatT) / b.Period);
                        double bt = b.LastBeatT + k * b.Period;
                        if (bt > lastFrame && bt <= t) grid.Add((bt - fileStart, lk));
                    }
                    // ロックの移り変わり（曲の 10 秒以降で、外れた・Locked から下がった）
                    if (seg.Whole && !seg.Warm && s >= 10 && prevLock != BeatLock.Unlocked && lk == BeatLock.Unlocked) unlocks++;
                    if (seg.Whole && !seg.Warm && s >= 10 && prevLock == BeatLock.Locked && lk != BeatLock.Locked) demotes++;
                    if (seg.Continues && prevLock != lk && s - tr0 < 30) trackerLog.Append($" {s - tr0:F1}:{lk.ToString()[0]}{e.Beat.Bpm:F0}{(IsStale(e.Tracker) ? "s" : "")}");
                    int sc = steerCount is null ? 0 : (int)steerCount.GetValue(e.Tracker)!;
                    if (seg.Continues && sc != prevSteer && s - tr0 < 30) trackerLog.Append($" {s - tr0:F1}:steer{e.Beat.Bpm:F0}");
                    if (sc != prevSteer && e.Beat is { HasTempo: true } sb0) steerOff.Add(-Nearest(refBeats, sb0.LastBeatT - fileStart));
                    prevSteer = sc;
                    prevLock = lk;
                    // 頭から数えたコマの位置（区間だけ流すときも、ファイルの頭からの秒で数える）
                    int fi = (int)Math.Round(s * Fps);
                    while (frameLocks.Count < fi) { frameLocks.Add(BeatLock.Unlocked); played.Add(false); foreach (var L in lanes) { L.U.Add(0); L.Src.Add(0); } }
                    frameLocks.Add(lk);
                    played.Add(true);
                    foreach (var L in lanes)
                    {
                        L.InTr = seg.Continues && s - tr0 >= 0 && s - tr0 < 10;
                        // 例の時刻 = 変わり目の番号 × 1000 + 曲の頭からの秒（どの変わり目か分かるように）
                        if (L.BlipTr is not null) L.BlipTr.Now = perTr.Count / Math.Max(1, lanes.Count) * 1000 + s;
                        Draw(L, e, t, lastFrame);
                    }
                    lastFrame = t;
                    nextFrame += 1 / Fps;
                }
            }
            if (seg.Whole && !seg.Warm) unlockSecs += Math.Max(0, len - 10);
            if (seg.Warm) continue;
            if (!seg.Whole && !seg.Continues) continue;   // 前の曲の最後だけ（変わり目の前の準備）: 数えない

            // この区間の光を数える
            string? dump = Environment.GetEnvironmentVariable("CHAIN_DUMP");
            string id = seg.Tag;
            double playedTo = from + mid.Length / (double)fs;
            foreach (var L in lanes)
            {
                var flashes = Flashes(L.U, played);
                if (seg.Continues && dump == L.Label)
                {
                    var sb = new StringBuilder($"{id} {L.Label} flashes 0-10 s:");
                    foreach (double ft in flashes.Where(x => x - tr0 < 10))
                    {
                        int fi = Math.Clamp((int)Math.Round(ft * Fps), 0, frameLocks.Count - 1);
                        byte src = 0;
                        for (int k = Math.Max(0, fi - 2); k <= Math.Min(L.Src.Count - 1, fi + 2); k++) src = Math.Max(src, L.Src[k]);
                        double off = -Nearest(refBeats, ft);
                        sb.Append($" {ft:F2}{(Math.Abs(off) <= 0.06 ? "+" : "-")}{frameLocks[fi].ToString()[0]}{(src == 1 ? "g" : src == 2 ? "k" : "?")}({off * 1000:+0;-0})");
                    }
                    T.Info(sb.ToString());
                }
                var times = flashes;
                var halves = refBeats.Zip(refBeats.Skip(1), (a, c) => new[] { a + 0.25 * (c - a), a + 0.5 * (c - a), a + 0.75 * (c - a) }).SelectMany(x => x).ToList();
                var tn = new int[4]; var ton = new int[4]; var tst = new int[4]; var tb = new int[4]; var tl = new int[4];
                int pn = 0, pon = 0, pln = 0, plon = 0, pfr = 0, plfr = 0;
                foreach (double ft in flashes)
                {
                    double off = Math.Abs(Nearest(refBeats, ft));
                    bool on = off <= 0.06, stray = off > 0.07 && Math.Abs(Nearest(halves, ft)) > 0.04;
                    int fi = Math.Clamp((int)Math.Round(ft * Fps), 0, frameLocks.Count - 1);
                    if (seg.Continues)
                    {
                        int w = Win(ft - tr0);
                        if (w >= 0) { tn[w]++; if (on) ton[w]++; if (stray) tst[w]++; }
                    }
                    if (seg.Whole && ft >= SongSkip && ft <= len - 1)
                    {
                        L.SN++; if (on) L.SOn++; if (stray) L.SStray++;
                        if (frameLocks[fi] == BeatLock.Locked) { L.SLN++; if (on) L.SLOn++; }
                        pn++; if (on) pon++;
                        if (frameLocks[fi] == BeatLock.Locked) { pln++; if (on) plon++; }
                    }
                }
                foreach (double bt in refBeats)
                {
                    if (bt < from || bt >= playedTo - 0.1) continue;
                    bool lit = times.Count > 0 && Math.Abs(Nearest(times, bt)) <= 0.07;
                    int fi = Math.Clamp((int)Math.Round(bt * Fps), 0, frameLocks.Count - 1);
                    if (seg.Continues)
                    {
                        int w = Win(bt - tr0);
                        if (w >= 0) { tb[w]++; if (lit) tl[w]++; }
                    }
                    if (seg.Whole && bt >= SongSkip && bt <= len - 1)
                    {
                        L.SBeats++; if (lit) L.SLit++;
                        if (frameLocks[fi] == BeatLock.Locked) { L.SLBeats++; if (lit) L.SLLit++; }
                    }
                }
                if (seg.Whole)
                    for (int i = (int)(SongSkip * Fps); i < Math.Min(frameLocks.Count, (int)((len - 1) * Fps)); i++)
                    {
                        L.SSecs += 1 / Fps;
                        if (frameLocks[i] == BeatLock.Locked) L.SLSecs += 1 / Fps;
                        pfr++; if (frameLocks[i] == BeatLock.Locked) plfr++;
                    }
                // CHAIN_PERSONG=1: 曲ごとの曲全体の値（最初のレーンだけ）
                if (seg.Whole && L == lanes[0] && Environment.GetEnvironmentVariable("CHAIN_PERSONG") == "1")
                    T.Info($"   song {id} {L.Label}: on {pon * 100.0 / Math.Max(1, pn):F1} | LOCK on {plon * 100.0 / Math.Max(1, pln):F1} | locked {plfr * 100.0 / Math.Max(1, pfr):F1}% | flashes {pn}");
                if (seg.Continues)
                {
                    for (int w = 0; w < 4; w++) { L.WN[w] += tn[w]; L.WOn[w] += ton[w]; L.WStray[w] += tst[w]; L.WBeats[w] += tb[w]; L.WLit[w] += tl[w]; }
                    perTr.Add((id, L.Label, tn, ton, tst, tb, tl));
                }
            }
            if (seg.Continues)
            {
                // 追跡の拍の頭が、新しい曲の拍（±70 ms）に乗っていた割合（窓ごと。ロックの状態別: L = Locked、C = Coasting、U = Unlocked）
                var gs = new StringBuilder();
                for (int w = 0; w < 4; w++)
                {
                    var gw = grid.Where(g => g.T - tr0 >= WinEdges[w] && g.T - tr0 < WinEdges[w + 1]).ToList();
                    string Part(BeatLock k) { var x = gw.Where(g => g.L == k).ToList(); return x.Count == 0 ? "-" : $"{x.Count(g => Math.Abs(Nearest(refBeats, g.T)) <= 0.07)}/{x.Count}"; }
                    gs.Append($" [{WinEdges[w]:F0}-{WinEdges[w + 1]:F0}s L {Part(BeatLock.Locked)} C {Part(BeatLock.Coasting)} U {Part(BeatLock.Unlocked)}]");
                }
                trackerTr.Add($"{id} ref {refBpm,5:F1} BPM | at the change {startState} | grid on new beat:{gs} | lock changes:{trackerLog}");
            }
            if (seg.Whole) T.Info($"{id} {len,5:F0} s ref {refBpm,5:F1} BPM{(seg.Continues ? " (gapless change)" : " (fresh start)")}");
        }
        TakeAi(assist);
        assist?.Dispose();
        memory?.EndSong();

        // 表示
        string Cell(int n, int on, int stray, double secs) => n == 0 ? $"{"-",4} {"-",4} {0,3}" : $"{on * 100.0 / n,4:F0} {stray * 100.0 / n,4:F0} {n,3}";
        double WSec(int w) => WinEdges[w + 1] - WinEdges[w];
        T.Info("per change (window: on60% stray% flashes):            0-2 s            2-5 s            5-10 s           10-30 s");
        foreach (var r in perTr)
            T.Info($"   {r.Id} {r.Lane,-14}" + string.Concat(Enumerable.Range(0, 4).Select(w => " | " + Cell(r.N[w], r.On[w], r.Stray[w], WSec(w)) + $" lit {(r.Beats[w] > 0 ? r.Lit[w] * 100.0 / r.Beats[w] : 0),3:F0}")));
        T.Info("tracker per change:");
        foreach (var s in trackerTr) T.Info("   " + s);
        T.Info("pooled over changes (on60% stray% flashes lit%):");
        void Pool(string name, IEnumerable<Lane> ls)
        {
            var l = ls.ToList();
            var sb = new StringBuilder($"   {name,-16}");
            for (int w = 0; w < 4; w++)
            {
                int n = l.Sum(x => x.WN[w]), on = l.Sum(x => x.WOn[w]), st = l.Sum(x => x.WStray[w]), b = l.Sum(x => x.WBeats[w]), lt = l.Sum(x => x.WLit[w]);
                sb.Append($" | {WinEdges[w]:F0}-{WinEdges[w + 1]:F0}s " + Cell(n, on, st, 0) + $" lit {(b > 0 ? lt * 100.0 / b : 0),3:F0}");
            }
            // 0-2 s で拍に乗った光が 1 つもなかった変わり目の数
            var zero = perTr.Where(r => l.Any(x => x.Label == r.Lane)).GroupBy(r => r.Lane).Select(g => g.Count(r => r.On[0] == 0)).ToList();
            sb.Append($" | 0% in 0-2 s: {string.Join("/", zero)} of {perTr.Count(r => r.Lane == l[0].Label)}");
            T.Info(sb.ToString());
        }
        foreach (var L in allLanes.Values) Pool(L.Label, new[] { L });
        Pool("ALL", allLanes.Values);
        if (segs.Any(x => x.Whole))
        {
            T.Info("whole songs (from 8 s; on60% / locked on60% / lit% / locked lit% / locked share / stray%):");
            foreach (var L in allLanes.Values)
                T.Info($"   {L.Label,-16} on {L.SOn * 100.0 / Math.Max(1, L.SN),5:F1} | LOCK on {L.SLOn * 100.0 / Math.Max(1, L.SLN),5:F1} | lit {L.SLit * 100.0 / Math.Max(1, L.SBeats),5:F1} | LOCK lit {L.SLLit * 100.0 / Math.Max(1, L.SLBeats),5:F1} | locked {L.SLSecs * 100 / Math.Max(1e-9, L.SSecs),5:F1}% | stray {L.SStray * 100.0 / Math.Max(1, L.SN),5:F1} | fl/s {L.SN / Math.Max(1e-9, L.SSecs),4:F2}");
            T.Info($"   tracker: unlocks {unlocks} ({unlocks * 60 / Math.Max(1e-9, unlockSecs):F2}/min), Locked -> lower {demotes} ({demotes * 60 / Math.Max(1e-9, unlockSecs):F2}/min) in {unlockSecs / 60:F1} min (from 10 s of each song)");
        }
 T.Info("colour blips on the drawn values (BlipMeter lum / colour / max / off; per minute): " + string.Join(" | ", allLanes.Values.Select(L =>
            $"{L.Label} all {Bl(L.Blip)} | 0-10 s after changes {Bl(L.BlipTr)}")));
        if (Environment.GetEnvironmentVariable("CHAIN_BLIPS") == "1")
        {
            var tags = perTr.Select(r => r.Id).Distinct().ToList();
            foreach (var L in allLanes.Values)
                foreach (var ex in L.BlipTr?.Examples ?? new())
                {
                    var m = Regex.Match(ex.Text, @"t=(\d+)\.");
                    int k = m.Success ? (int)(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1000) : -1;
                    T.Info($"   blip after change {(k >= 0 && k < tags.Count ? tags[k] : "?")} {L.Label}: {ex.Text}");
                }
        }
        static string Bl(BlipMeter? m) => m is null || m.Samples == 0 ? "-" : $"{m.LumBlips}/{m.LumColour}/{m.MaxBlips}/{m.OffPath} ({m.LumBlips * 60.0 * Fps / m.Samples:F2}/min)";
        T.Info("hashes: " + string.Join(" ", allLanes.Values.Select(L => $"{L.Label} {L.Hash:x16}")));
        if (model is not null)
        {
            static double Med(IEnumerable<double> x) { var l = x.OrderBy(v => v).ToList(); return l.Count == 0 ? double.NaN : l[l.Count / 2]; }
            var near = aiOff.Where(o => Math.Abs(o) < 0.1).ToList();
            var p1 = aiPeriod.Where(r => Math.Abs(r - 1) < 0.05).ToList();
            T.Info($"beat AI: runs {aiRuns} agreed {aiAgreed} steered {aiSteered} | AI beats vs ref (n {aiOff.Count}): median {Med(near) * 1000:+0.0;-0.0} ms, " +
                   $"within 30 ms {aiOff.Count(o => Math.Abs(o) <= 0.03) * 100.0 / Math.Max(1, aiOff.Count):F1}%, within 60 ms {aiOff.Count(o => Math.Abs(o) <= 0.06) * 100.0 / Math.Max(1, aiOff.Count):F1}%, " +
                   $"mean |off| (<100 ms) {(near.Count > 0 ? near.Average(Math.Abs) * 1000 : 0):F1} ms | AI period / ref (n {p1.Count} of {aiPeriod.Count}): median {(Med(p1) - 1) * 100:+0.000;-0.000}% | " +
                   $"tracker right after a steer vs ref (n {steerOff.Count}): median {Med(steerOff.Where(o => Math.Abs(o) < 0.1)) * 1000:+0.0;-0.0} ms, within 60 ms {steerOff.Count(o => Math.Abs(o) <= 0.06) * 100.0 / Math.Max(1, steerOff.Count):F1}%");
        }
    }

    private static double WavLen(string path)
    {
        using var fsr = File.OpenRead(path);
        using var br = new BinaryReader(fsr);
        br.ReadBytes(12);
        int ch = 2, rate = 48000, bits = 32;
        while (fsr.Position + 8 <= fsr.Length)
        {
            string id = new string(br.ReadChars(4));
            uint size = br.ReadUInt32();
            if (id == "fmt ") { br.ReadUInt16(); ch = br.ReadInt16(); rate = br.ReadInt32(); br.ReadInt32(); br.ReadInt16(); bits = br.ReadInt16(); br.ReadBytes((int)size - 16); }
            else if (id == "data") return Math.Min(size, fsr.Length - fsr.Position) / (double)(bits / 8 * ch) / rate;
            else fsr.Seek(size + (size & 1), SeekOrigin.Current);
        }
        return 0;
    }

    private static int Win(double s)
    {
        for (int w = 0; w < 4; w++) if (s >= WinEdges[w] && s < WinEdges[w + 1]) return w;
        return -1;
    }

    private static void CopyAcc(Lane a, Lane b)
    {
        Array.Copy(a.WN, b.WN, 4); Array.Copy(a.WOn, b.WOn, 4); Array.Copy(a.WStray, b.WStray, 4); Array.Copy(a.WBeats, b.WBeats, 4); Array.Copy(a.WLit, b.WLit, 4);
        b.SN = a.SN; b.SOn = a.SOn; b.SLN = a.SLN; b.SLOn = a.SLOn; b.SBeats = a.SBeats; b.SLit = a.SLit; b.SLBeats = a.SLBeats; b.SLLit = a.SLLit; b.SStray = a.SStray;
        b.SSecs = a.SSecs; b.SLSecs = a.SLSecs; b.Hash = a.Hash;
        b.U.AddRange(a.U);
        b.Src.AddRange(a.Src);
        b.Blip = a.Blip;
        b.BlipTr = a.BlipTr;
    }

    private static void Draw(Lane L, AudioEngine e, double t, double lastT)
    {
        var input = L.In;
        input.T = t;
        input.TEval = t;
        input.TPrevEval = double.IsNaN(lastT) ? t : lastT;
        input.Dt = double.IsNaN(lastT) ? 1 / Fps : t - lastT;
        input.Beat = e.Beat;
        input.Scene = e.Scene;
        input.Features = e.Features;
        e.Features.Sample(t, input.Frame);
        input.Events.Clear();
        e.Events.Read(ref L.Cursor, t, input.Events);
        L.R.Render(input, L.Out);
        double up = 0;
        for (int i = 0; i < L.Out.Length; i++)
        {
            var o = L.Out[i];
            int v = Math.Max(o.R, Math.Max(o.G, o.B));
            if (!L.First) up += Math.Max(0, v - L.Prev[i]) / 255.0;
            L.Prev[i] = v;
            L.Hash = (L.Hash ^ (uint)(o.R | (o.G << 8) | (o.B << 16))) * 1099511628211UL;
        }
        L.First = false;
        L.U.Add(up / L.Out.Length);
        L.Blip?.Feed(L.Out);
        L.BlipTr?.Feed(L.Out, L.InTr);
        byte src = 0;
        var fx = L.R.CurrentEffect;
        var fp = fx?.GetType().GetProperty("Flash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var bfl = fp?.GetValue(fx);
        if (bfl is not null)
        {
            var t1 = bfl.GetType();
            if ((bool)t1.GetProperty("BeatStarted")!.GetValue(bfl)!) src = 1;
            else if ((bool)t1.GetProperty("KickStarted")!.GetValue(bfl)!) src = 2;
        }
        L.Src.Add(src);
    }

    /// <summary>光の立ち上がりの時刻（ファイルの頭からの秒。BeatFlashBench.Score と同じ数え方。しきい値はファイルごと）</summary>
    private static List<double> Flashes(List<double> uList, List<bool> played)
    {
        var u = uList.ToArray();
        int n = u.Length;
        var us = u.Where((x, i) => i < played.Count && played[i]).OrderBy(x => x).ToArray();
        double UP(double q) => us.Length == 0 ? 0 : us[(int)Math.Clamp(q * (us.Length - 1), 0, us.Length - 1)];
        double thr = Math.Max(0.03, Math.Max(UP(0.85), 3 * UP(0.5)));
        var times = new List<double>();
        double lastFlash = double.NegativeInfinity;
        for (int i = 3; i < n - 3; i++)
        {
            if (u[i] < thr) continue;
            bool peak = true;
            for (int k = -3; k <= 3 && peak; k++) if (k != 0 && (u[i + k] > u[i] || (k < 0 && u[i + k] == u[i]))) peak = false;
            if (!peak) continue;
            int s = i;
            while (s > i - 3 && u[s - 1] >= thr / 3) s--;
            double ft = (s - 0.5) / Fps;
            if (ft - lastFlash < 0.1) continue;
            lastFlash = ft;
            times.Add(ft);
        }
        return times;
    }

    /// <summary>物差しの拍（ファイルの頭からの秒）: そのファイルだけを新しい分析に流す（CHAIN_REF があれば覚えておく）</summary>
    private static List<double> RefBeats(Song song, float[] mid, int fs, string? refDir, out double bpm)
    {
        string? cache = refDir is null ? null : System.IO.Path.Combine(refDir, song.Id + ".txt");
        if (cache is not null && File.Exists(cache))
        {
            var lines = File.ReadAllLines(cache);
            bpm = double.Parse(lines[0], CultureInfo.InvariantCulture);
            return lines.Skip(1).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToList();
        }
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyIntensity(cfg.Music, Intensities.Moderate);
        var refEngine = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(cfg) };
        var hops = new List<(double T, double Odf)>();
        refEngine.Onsets.DiagHop = (t, odf, bn) => hops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
        int chunk = fs / 100;
        for (int pos = 0; pos < mid.Length; pos += chunk)
        {
            int n = Math.Min(chunk, mid.Length - pos);
            refEngine.Push(mid.AsSpan(pos, n), T0 + pos / (double)fs);
        }
        var beats = BeatReference.Track(hops, out bpm).Select(t => t - T0).ToList();
        if (cache is not null)
        {
            Directory.CreateDirectory(refDir!);
            File.WriteAllLines(cache, new[] { bpm.ToString("R", CultureInfo.InvariantCulture) }.Concat(beats.Select(b => b.ToString("R", CultureInfo.InvariantCulture))));
        }
        return beats;
    }

    /// <summary>WAV（32 bit 浮動小数・16 bit。ステレオならミッドとサイド）を速く読む</summary>
    private static (float[] Mid, float[] Side, int Fs) ReadWav(string path)
    {
        using var fsr = File.OpenRead(path);
        using var br = new BinaryReader(fsr);
        br.ReadBytes(12);
        int ch = 2, rate = 48000, bits = 32, format = 3;
        while (fsr.Position + 8 <= fsr.Length)
        {
            string id = new string(br.ReadChars(4));
            uint size = br.ReadUInt32();
            if (id == "fmt ")
            {
                format = br.ReadUInt16(); ch = br.ReadInt16(); rate = br.ReadInt32(); br.ReadInt32(); br.ReadInt16(); bits = br.ReadInt16();
                br.ReadBytes((int)size - 16);
            }
            else if (id == "data")
            {
                int bytes = bits / 8;
                long avail = Math.Min(size, fsr.Length - fsr.Position);
                int n = (int)(avail / bytes / Math.Max(1, ch));
                var mid = new float[n];
                var side = ch >= 2 ? new float[n] : Array.Empty<float>();
                bool isFloat = format == 3 || (format == 0xFFFE && bits == 32);
                var buf = new byte[1 << 20];
                int frameBytes = bytes * ch, done = 0, carry = 0;
                while (done < n)
                {
                    int want = Math.Min(buf.Length - carry, (n - done) * frameBytes - carry);
                    int got = fsr.Read(buf, carry, want);
                    if (got <= 0) break;
                    int total = carry + got, frames = total / frameBytes;
                    for (int i = 0; i < frames && done < n; i++, done++)
                    {
                        int o = i * frameBytes;
                        float l, r;
                        if (bits == 32 && isFloat) { l = BitConverter.ToSingle(buf, o); r = ch >= 2 ? BitConverter.ToSingle(buf, o + 4) : l; }
                        else if (bits == 16) { l = BitConverter.ToInt16(buf, o) / 32768f; r = ch >= 2 ? BitConverter.ToInt16(buf, o + 2) / 32768f : l; }
                        else { l = BitConverter.ToInt32(buf, o) / 2147483648f; r = ch >= 2 ? BitConverter.ToInt32(buf, o + 4) / 2147483648f : l; }
                        mid[done] = ch >= 2 ? 0.5f * (l + r) : l;
                        if (ch >= 2) side[done] = 0.5f * (l - r);
                    }
                    carry = total - frames * frameBytes;
                    if (carry > 0) Buffer.BlockCopy(buf, frames * frameBytes, buf, 0, carry);
                }
                return (mid, side, rate);
            }
            else fsr.Seek(size + (size & 1), SeekOrigin.Current);
        }
        return (Array.Empty<float>(), Array.Empty<float>(), rate);
    }

    /// <summary>CHAIN_TUNE=型+Tune.項目=値;…（名前空間 ProFanController を除いた型の名前）。戻すための元の値を返す</summary>
    private static List<(FieldInfo F, object? V)> ApplyTune(string spec)
    {
        var saved = new List<(FieldInfo, object?)>();
        foreach (var kv in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = kv.Split('=', 2);
            int dot = p[0].LastIndexOf('.');
            if (p.Length < 2 || dot < 0) { T.Info($"bad tune '{kv}'"); continue; }
            var type = typeof(AudioEngine).Assembly.GetType("ProFanController." + p[0][..dot]);
            var f = type?.GetField(p[0][(dot + 1)..], BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f is null) { T.Info($"unknown tune '{kv}'"); continue; }
            saved.Add((f, f.GetValue(null)));
            f.SetValue(null, Convert.ChangeType(p[1], f.FieldType, CultureInfo.InvariantCulture));
            T.Info($"tune {p[0]} = {p[1]}");
        }
        return saved;
    }

    /// <summary>
    /// NewSong に渡す、知らせの遅れの見積もり（秒）: CHAIN_AGE があればその値。なければアプリ（MusicAi）と同じ見積もり
    /// （0.5 秒以下の遅れ = Spotify のウィンドウを速く見て分かった、それより遅い = メディアの情報などで分かった）
    /// </summary>
    private static double NoticeAge(double delay)
    {
        double a = D("CHAIN_AGE", double.NaN);
        if (!double.IsNaN(a)) return a;
        var f = typeof(AudioEngine).GetField(delay <= 0.5 ? "SongNoticeQuickSec" : "SongNoticeFullSec", BindingFlags.Static | BindingFlags.Public);
        return f?.GetValue(null) is double v ? v : Math.Max(0, delay);
    }

    /// <summary>AudioEngine.NewSong を呼ぶ（知らせの遅れを渡せない版なら、渡さない）</summary>
    private static void CallNewSong(MethodInfo m, AudioEngine e, double age) =>
        m.Invoke(e, m.GetParameters().Length == 1 ? new object[] { age } : null);

    /// <summary>拍の並びが前の曲のもの（BeatTracker.GridStale。ない版では false）</summary>
    private static readonly PropertyInfo? StaleProp = typeof(BeatTracker).GetProperty("GridStale");
    private static bool IsStale(BeatTracker t) => StaleProp?.GetValue(t) is true;

    private static double D(string name, double def) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;

    /// <summary>拍の AI に渡すサンプリング周波数（アプリと同じ: AudioCapture は分析の周波数で作る。CHAIN_AIRATE=full か、前の版（BeatAssist.InputRate がない）なら取り込みの周波数）</summary>
    private static int AiRate(AudioEngine e) =>
        Environment.GetEnvironmentVariable("CHAIN_AIRATE") == "full" || typeof(BeatAssist).GetProperty("InputRate") is null ? e.SampleRate : e.AnalysisRate;

    /// <summary>拍の時刻に番号を振り、時刻 = a + 番号 × 周期 の直線を当てはめた周期（拍が 8 個未満なら null）</summary>
    private static double? FitPeriod(List<double> b)
    {
        if (b.Count < 8) return null;
        var ibi = b.Zip(b.Skip(1), (x, y) => y - x).OrderBy(x => x).ToList();
        double med = ibi[ibi.Count / 2];
        if (!(med > 0)) return null;
        var k = b.Select(t => Math.Round((t - b[0]) / med)).ToList();
        double mk = k.Average(), mt = b.Average(), sxy = 0, sxx = 0;
        for (int i = 0; i < b.Count; i++) { sxy += (k[i] - mk) * (b[i] - mt); sxx += (k[i] - mk) * (k[i] - mk); }
        return sxx > 0 ? sxy / sxx : null;
    }

    private static double Nearest(List<double> xs, double t)
    {
        int k = xs.BinarySearch(t);
        if (k < 0) k = ~k;
        double best = double.PositiveInfinity;
        if (k < xs.Count) best = xs[k] - t;
        if (k > 0 && Math.Abs(t - xs[k - 1]) < Math.Abs(best)) best = xs[k - 1] - t;
        return best;
    }
}

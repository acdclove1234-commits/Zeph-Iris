// ==================================================
// BeatFlashBench.cs - 「ビートで色替え」の光が拍に乗っているかを、録音した曲で測る（BFL_WAV を渡したときだけ動く）
//   ・アプリと同じに、ミッドとサイド・拍の AI つきで AudioEngine に 10 ミリ秒ずつ流し、60 コマ／秒で描く
//   ・光の立ち上がりの数え方は ModeSyncBench と同じ（LED ごとに明るくなった分の山）
//   ・光ごとに、そのときテンポをつかんでいたか（Locked / Coasting / Unlocked）と、どの経路で光ったか（予測した拍・キックの光・低音・その他）を記録する
//   出す値（最初の 8 秒は数えない。曲ごと・まとまりごと・つかんでいた／いなかったで分ける）:
//     fl/s   … 光の立ち上がりの数（1 秒あたり）
//     on60 / on70 … 物差しの拍（±60 / ±70 ms）に乗った立ち上がりの割合
//     sub    … 拍の間（8 分・16 分の位置 ±40 ms）に乗った割合
//     stray  … 拍にも拍の間にも乗らない割合（数・明るくなった量）
//     lit    … 物差しの拍のうち、光が立ち上がった割合（±70 ms）
//     hash   … 送った値すべての指紋（ほかの光り方が変わっていないかを比べる）
//     br     … 明るさ（LED ごとの max(R,G,B)/255 の平均）の中央値・90 パーセンタイル・0.08 未満のコマの割合（拍に乗せても暗くなりすぎていないか）
//   調べる用: BFL_WAV=フォルダー;フォルダー（まとまりの名前は BFL_SETS=A;B）、BFL_STYLES=BeatHue,Spectrum,…、BFL_DEV=strip,pny,dram（both = strip,pny。OpenRGB を通すグラボ gpu は、このベンチの送り方では何も描かれないので測らない。SlowDeviceBench で測る）、
//            BFL_SEC=90、BFL_INTENS=Moderate、BFL_AI=0（拍の AI を付けない）、BFL_SONGS=song_01,…、BFL_DUMP=光り方（光ごとの記録を出す）
//   曲の名前は出さない（song_NN だけ）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Text;
using ProFanController;

namespace LogicTests;

public static class BeatFlashBenchTests
{
    private const double Fps = 60, Skip = 8, T0 = 1000.0;

    /// <summary>光 1 つの記録</summary>
    private sealed record Flash(double T, double Amp, BeatLock Lock, string Src, double RefOff, bool Sub);

    /// <summary>レーン（光り方 × デバイス）1 本の、1 曲分の結果</summary>
    private sealed class Result
    {
        public required string Label;
        public double Secs, LockedSecs, OtherSecs;
        public readonly List<Flash> Flashes = new();
        public int BeatsLocked, BeatsLockedLit, BeatsOther, BeatsOtherLit;
        /// <summary>状態（Unlocked・Coasting・Locked）ごとの秒数・物差しの拍の数・光った拍の数</summary>
        public readonly double[] Secs3 = new double[3];
        public readonly int[] Beats3 = new int[3], Lit3 = new int[3];
        /// <summary>予測した拍の頭・キック（強さ 1.2 以上）のうち、物差しの拍（±70 ms）に乗った数（Unlocked・Coasting・Locked ごと）</summary>
        public readonly int[] GridN = new int[3], GridOn = new int[3], KickN = new int[3], KickOn = new int[3];
        /// <summary>コマごとの明るさ（最初の Skip 秒を除く）</summary>
        public readonly List<double> Bright = new();
        public ulong Hash;
    }

    private sealed class Lane
    {
        public required string Label, Style;
        public required DeviceRenderer R;
        public required RenderInput In;
        public required Rgb[] Out;
        public long Cursor;
        public int[] Prev = Array.Empty<int>();
        public readonly List<double> U = new();
        // コマごとの手がかり（どの経路で光ったか）
        public readonly List<float> Fl = new(), Bass = new();
        public readonly List<byte> Cross = new(), Kick = new();
        public readonly List<BeatLock> Locks = new();
        public readonly List<(double T, BeatLock L)> Grid = new();
        public readonly List<(double T, float S, BeatLock L)> Kicks = new();
        public readonly List<double> Bright = new();
        public ulong Hash = 1469598103934665603UL;
    }

    private static readonly Lazy<BeatThis?> Model = new(() => BeatThis.TryCreate(out _));

    public static void TestBeatFlashBench()
    {
        string? spec = Environment.GetEnvironmentVariable("BFL_WAV");
        if (string.IsNullOrEmpty(spec)) return;
        var dirs = spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var setNames = (Environment.GetEnvironmentVariable("BFL_SETS") ?? "A;B;C;D").Split(';');
        string[] styles = (Environment.GetEnvironmentVariable("BFL_STYLES") ?? MusicStyles.BeatHue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string devSpec = Environment.GetEnvironmentVariable("BFL_DEV") ?? "strip";
        double maxSec = double.TryParse(Environment.GetEnvironmentVariable("BFL_SEC"), System.Globalization.CultureInfo.InvariantCulture, out double ms) && ms > 0 ? ms : 90;
        string intens = Environment.GetEnvironmentVariable("BFL_INTENS") ?? Intensities.Moderate;
        var only = Environment.GetEnvironmentVariable("BFL_SONGS")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        BeatThis? model = Environment.GetEnvironmentVariable("BFL_AI") == "0" ? null : Model.Value;
        // BFL_TUNE=名前=値;…（BeatTracker・BeatHueEffect・BeatFlash などの Tune の値。終わったら戻す）
        var savedTune = BeatTuneSweepTests.Snapshot();
        BeatTuneSweepTests.Apply(Environment.GetEnvironmentVariable("BFL_TUNE")?.Replace(',', ';') ?? "");
        try { Bench(dirs, setNames, styles, devSpec, maxSec, intens, only, model); }
        finally { BeatTuneSweepTests.Restore(savedTune); }
    }

    private static void Bench(string[] dirs, string[] setNames, string[] styles, string devSpec, double maxSec, string intens, string[]? only, BeatThis? model)
    {
        T.Info($"styles {string.Join(",", styles)}; device {devSpec}; intensity {intens}; beat AI {(model is null ? "off" : "on")}; {maxSec:F0} s per song");

        var totals = new Dictionary<string, List<Result>>();
        for (int d = 0; d < dirs.Length; d++)
        {
            if (!Directory.Exists(dirs[d])) continue;
            string set = d < setNames.Length && setNames[d].Length > 0 ? setNames[d] : $"S{d + 1}";
            var files = Directory.GetFiles(dirs[d], "*.wav").OrderBy(x => x, StringComparer.Ordinal).ToList();
            for (int fi = 0; fi < files.Count; fi++)
            {
                string id = SlowDeviceBenchTests.SafeId(files[fi], fi);
                if (only is not null && !only.Contains(id)) continue;
                string tag = set + id.Replace("song_", "");
                var (mid, side, fs) = SyncRegressionBenchTests.ReadWavStereo(files[fi]);
                if (mid.Length == 0) continue;
                if (mid.Length > maxSec * fs)
                {
                    mid = mid.AsSpan(0, (int)(maxSec * fs)).ToArray();
                    if (side.Length > mid.Length) side = side.AsSpan(0, mid.Length).ToArray();
                }
                var results = RunSong(tag, mid, side, fs, styles, devSpec, intens, model, out double refBpm);
                T.Info($"{tag} ref {refBpm,5:F1} BPM");
                foreach (var r in results)
                {
                    T.Info("   " + Format(r.Label, new[] { r }) + " | " + Bright(new[] { r }));
                    (totals.TryGetValue(set + "|" + r.Label, out var l) ? l : totals[set + "|" + r.Label] = new()).Add(r);
                    (totals.TryGetValue("all|" + r.Label, out var a) ? a : totals["all|" + r.Label] = new()).Add(r);
                }
            }
        }
        T.Info("totals (flashes pooled over songs; lit = beats lit):");
        foreach (var kv in totals.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            T.Info($"   {kv.Key,-18} " + Format("", kv.Value));
            var rs = kv.Value;
            string G(int k) => $"{(BeatLock)k} grid {rs.Sum(r => r.GridN[k]) / Math.Max(1e-9, rs.Sum(r => r.Secs)),4:F2}/s on {rs.Sum(r => r.GridOn[k]) * 100.0 / Math.Max(1, rs.Sum(r => r.GridN[k])),3:F0}%  kicks {rs.Sum(r => r.KickN[k]) / Math.Max(1e-9, rs.Sum(r => r.Secs)),4:F2}/s on {rs.Sum(r => r.KickOn[k]) * 100.0 / Math.Max(1, rs.Sum(r => r.KickN[k])),3:F0}%";
            T.Info($"   {kv.Key,-18}    {G(0)} | {G(1)} | {G(2)}");
            T.Info($"   {kv.Key,-18}    sources of off-beat (>60 ms) flashes: " + Sources(kv.Value));
            T.Info($"   {kv.Key,-18}    " + Bright(kv.Value));
        }
        T.Info("hashes (per song, per lane):");
        foreach (var kv in totals.Where(k => !k.Key.StartsWith("all|")).OrderBy(k => k.Key, StringComparer.Ordinal))
            T.Info($"   {kv.Key,-18} " + string.Join(" ", kv.Value.Select(r => r.Hash.ToString("x16"))));
    }

    /// <summary>まとめた値（つかんでいた・つかんでいない・全部）</summary>
    private static string Format(string label, IReadOnlyList<Result> rs)
    {
        var sb = new StringBuilder();
        if (label.Length > 0) sb.Append($"{label,-14} ");
        double secs = rs.Sum(r => r.Secs), lsecs = rs.Sum(r => r.LockedSecs), osecs = rs.Sum(r => r.OtherSecs);
        sb.Append($"locked {lsecs / Math.Max(1e-9, secs) * 100,3:F0}% | ");
        sb.Append("ALL " + Part(rs.SelectMany(r => r.Flashes).ToList(), secs, rs.Sum(r => r.BeatsLocked + r.BeatsOther), rs.Sum(r => r.BeatsLockedLit + r.BeatsOtherLit)));
        sb.Append(" | LOCK " + Part(rs.SelectMany(r => r.Flashes).Where(f => f.Lock == BeatLock.Locked).ToList(), lsecs, rs.Sum(r => r.BeatsLocked), rs.Sum(r => r.BeatsLockedLit)));
        sb.Append(" | UNL " + Part(rs.SelectMany(r => r.Flashes).Where(f => f.Lock != BeatLock.Locked).ToList(), osecs, rs.Sum(r => r.BeatsOther), rs.Sum(r => r.BeatsOtherLit)));
        foreach (var k in new[] { BeatLock.Coasting, BeatLock.Unlocked })
            sb.Append($" | {(k == BeatLock.Coasting ? "coast" : "unlk")} {rs.Sum(r => r.Secs3[(int)k]) / Math.Max(1e-9, secs) * 100,3:F0}% " + Part(rs.SelectMany(r => r.Flashes).Where(f => f.Lock == k).ToList(), rs.Sum(r => r.Secs3[(int)k]), rs.Sum(r => r.Beats3[(int)k]), rs.Sum(r => r.Lit3[(int)k])));
        return sb.ToString();
    }

    private static string Part(List<Flash> fl, double secs, int beats, int lit)
    {
        int n = fl.Count;
        if (n == 0) return $"fl/s  0.00 on60  -  on70  -  sub  -  stray  -/-  lit {(beats > 0 ? lit * 100.0 / beats : 0),3:F0}%";
        double on60 = fl.Count(f => Math.Abs(f.RefOff) <= 0.06) * 100.0 / n;
        double on70 = fl.Count(f => Math.Abs(f.RefOff) <= 0.07) * 100.0 / n;
        double sub = fl.Count(f => Math.Abs(f.RefOff) > 0.07 && f.Sub) * 100.0 / n;
        var stray = fl.Where(f => Math.Abs(f.RefOff) > 0.07 && !f.Sub).ToList();
        double amp = fl.Sum(f => f.Amp);
        return $"fl/s {n / Math.Max(1e-9, secs),5:F2} on60 {on60,4:F1} on70 {on70,4:F1} sub {sub,4:F1} stray {stray.Count * 100.0 / n,4:F1}/{(amp > 0 ? stray.Sum(f => f.Amp) / amp * 100 : 0),4:F1} lit {(beats > 0 ? lit * 100.0 / beats : 0),4:F1}%";
    }

    /// <summary>明るさの中央値・90 パーセンタイル・0.08 未満の割合（曲をまとめて）</summary>
    private static string Bright(IReadOnlyList<Result> rs)
    {
        var b = rs.SelectMany(r => r.Bright).OrderBy(x => x).ToArray();
        if (b.Length == 0) return "br -";
        double Q(double q) => b[(int)Math.Clamp(q * (b.Length - 1), 0, b.Length - 1)];
        return $"br p50 {Q(0.5):F3} p90 {Q(0.9):F3} mean {b.Average():F3} dark<.08 {b.Count(x => x < 0.08) * 100.0 / b.Length:F0}%";
    }

    /// <summary>拍に乗らなかった光（±60 ms の外）が、どの経路で光ったか（割合）</summary>
    private static string Sources(IReadOnlyList<Result> rs)
    {
        var off = rs.SelectMany(r => r.Flashes).Where(f => Math.Abs(f.RefOff) > 0.06).ToList();
        int all = rs.Sum(r => r.Flashes.Count);
        if (off.Count == 0) return "none";
        return $"{off.Count * 100.0 / Math.Max(1, all):F0}% of flashes: " + string.Join("  ", off.GroupBy(f => (f.Lock == BeatLock.Locked ? "L:" : "U:") + f.Src)
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count() * 100.0 / off.Count:F0}%"));
    }

    private static RgbDevice DeviceOf(string name) => name switch
    {
        "pny" => new RgbDevice(5, "PNY GeForce RTX ARGB Epic-X", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }) { DirectGpu = true },
        "dram" => SlowDeviceBenchTests.DeviceOf(name),
        "gpu" => throw new ArgumentException("BFL_DEV=gpu: OpenRGB を通すグラボはこのベンチでは何も描かれない（SlowDeviceBench で測る）"),
        _ => new RgbDevice(0, "Strip", 24, 4, null, "HID: x"),
    };

    private static List<Result> RunSong(string tag, float[] mid, float[] side, int fs, string[] styles, string devSpec, string intens, BeatThis? model, out double refBpm)
    {
        double seconds = mid.Length / (double)fs;
        // 1) 物差しの拍（ModeSyncBench と同じ）
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyIntensity(cfg.Music, intens);
        var refEngine = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(cfg) };
        var hops = new List<(double T, double Odf)>();
        refEngine.Onsets.DiagHop = (t, odf, bn) => hops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
        var refRun = new EngineRun(refEngine).Feed(mid);
        var beats = BeatReference.Track(hops, out refBpm).Select(t => t - refRun.T0).ToList();

        // 2) 本物の分析に流しながら描く
        var engine = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(cfg) };
        BeatAssist? assist = model is null ? null : new BeatAssist(model, fs) { Inline = true };
        if (assist is not null) engine.Assist = assist;
        string[] devs = devSpec == "both" ? new[] { "strip", "pny" } : devSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var lanes = new List<Lane>();
        foreach (var dn in devs)
            foreach (var st in styles)
            {
                var dev = DeviceOf(dn);
                var music = MusicTuning.Resolve(cfg) with { Effect = st };
                var input = new RenderInput { Cfg = cfg, Music = music, Device = new RgbDeviceSettings { Name = dev.Name } };
                lanes.Add(new Lane { Label = devs.Length > 1 || dn != "strip" ? $"{st}/{dn}" : st, Style = st, R = new DeviceRenderer(dev), In = input, Out = new Rgb[dev.LedCount], Prev = new int[dev.LedCount] });
            }
        var frameLocks = new List<BeatLock>();
        int chunk = fs / 100;
        double nextFrame = T0, lastFrame = double.NaN;
        for (int pos = 0; pos < mid.Length; pos += chunk)
        {
            int n = Math.Min(chunk, mid.Length - pos);
            var sd = side.Length >= pos + n ? new ReadOnlySpan<float>(side, pos, n) : ReadOnlySpan<float>.Empty;
            engine.Push(mid.AsSpan(pos, n), sd, T0 + pos / (double)fs);
            double tAudio = T0 + (pos + n) / (double)fs;
            while (nextFrame <= tAudio)
            {
                double t = nextFrame;
                frameLocks.Add(engine.BeatLock);
                foreach (var L in lanes) Draw(L, engine, t, lastFrame);
                lastFrame = t;
                nextFrame += 1 / Fps;
            }
        }
        assist?.Dispose();
        var res = new List<Result>();
        foreach (var L in lanes) res.Add(Score(tag, L, beats, frameLocks, seconds));
        return res;
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
        double up = 0, br = 0;
        for (int i = 0; i < L.Out.Length; i++)
        {
            var o = L.Out[i];
            int v = Math.Max(o.R, Math.Max(o.G, o.B));
            br += v / 255.0;
            if (L.U.Count > 0) up += Math.Max(0, v - L.Prev[i]) / 255.0;
            L.Prev[i] = v;
            L.Hash = (L.Hash ^ (uint)(o.R | (o.G << 8) | (o.B << 16))) * 1099511628211UL;
        }
        L.U.Add(up / L.Out.Length);
        L.Bright.Add(br / L.Out.Length);
        var c = L.R.Context;
        // 拍に乗せる光（BeatFlash）があれば、その値を見る（ない版のコードでも動くように、名前で探す）
        var bfl = FlashOf(L.R.CurrentEffect);
        L.Fl.Add(bfl is null ? c.Fl : (float)Prop(bfl, "Value"));
        L.Bass.Add(Math.Max(c.Frame.BassE, c.Peak.BassE));
        L.Cross.Add((byte)(bfl is not null ? ((bool)Prop(bfl, "BeatStarted") ? 2 : (bool)Prop(bfl, "KickStarted") ? 3 : 0) : c.BeatCrossed ? (c.Locked ? 2 : 1) : 0));
        float kick = 0;
        foreach (var ev in input.Events)
            if (ev.Kind is AudioEventKind.Kick or AudioEventKind.Onset)
            {
                kick = Math.Max(kick, ev.Strength);
                if (ev.Strength >= 1.2f) L.Kicks.Add((ev.T - T0, ev.Strength, e.BeatLock));
            }
        if (c.BeatCrossed) L.Grid.Add((c.BeatCrossT - T0, e.BeatLock));
        L.Kick.Add((byte)Math.Clamp(kick * 10, 0, 255));
        L.Locks.Add(e.BeatLock);
    }

    private static Result Score(string tag, Lane L, List<double> beats, List<BeatLock> frameLocks, double seconds)
    {
        var u = L.U.ToArray();
        int n = u.Length;
        int f0 = (int)(Skip * Fps);
        var us = u.Skip(f0).OrderBy(x => x).ToArray();
        double UP(double q) => us.Length == 0 ? 0 : us[(int)Math.Clamp(q * (us.Length - 1), 0, us.Length - 1)];
        double thr = Math.Max(0.03, Math.Max(UP(0.85), 3 * UP(0.5)));
        var bs = beats.Where(x => x >= Skip && x <= seconds - 1).ToList();
        var halves = bs.Zip(bs.Skip(1), (a, c) => new[] { a + 0.25 * (c - a), a + 0.5 * (c - a), a + 0.75 * (c - a) }).SelectMany(x => x).ToList();
        var res = new Result { Label = L.Label, Hash = L.Hash };
        var times = new List<double>();
        double lastFlash = double.NegativeInfinity;
        bool dump = Environment.GetEnvironmentVariable("BFL_DUMP") == L.Label;
        var sb = dump ? new StringBuilder() : null;
        for (int i = Math.Max(f0, 3); i < n - 3; i++)
        {
            if (u[i] < thr) continue;
            bool peak = true;
            for (int k = -3; k <= 3 && peak; k++) if (k != 0 && (u[i + k] > u[i] || (k < 0 && u[i + k] == u[i]))) peak = false;
            if (!peak) continue;
            int s = i;
            while (s > i - 3 && u[s - 1] >= thr / 3) s--;
            double ft = (s - 0.5) / Fps;
            if (ft - lastFlash < 0.1) continue;
            double amp = 0;
            for (int k = Math.Max(0, i - 3); k <= Math.Min(n - 1, i + 2); k++) amp += u[k];
            lastFlash = ft;
            times.Add(ft);
            double off = -Nearest(bs, ft);
            bool sub = Math.Abs(Nearest(halves, ft)) <= 0.04;
            var lk = frameLocks[Math.Min(i, frameLocks.Count - 1)];
            string src = Source(L, s, i);
            res.Flashes.Add(new Flash(ft, amp, lk, src, off, sub));
            sb?.Append($"{ft,7:F2} {(Math.Abs(off) <= 0.06 ? "BEAT " : sub ? "sub  " : "STRAY")} {lk,-8} {src,-10} amp {amp:F2} beat{off * 1000:+0;-0}ms\n");
        }
        if (sb is not null) T.Info(tag + " " + L.Label + "\n" + sb);
        for (int i = f0; i < n; i++)
        {
            res.Secs += 1 / Fps;
            if (frameLocks[i] == BeatLock.Locked) res.LockedSecs += 1 / Fps; else res.OtherSecs += 1 / Fps;
            res.Secs3[(int)frameLocks[i]] += 1 / Fps;
        }
        foreach (var (gt, gl) in L.Grid.Where(g => g.T >= Skip))
        {
            res.GridN[(int)gl]++;
            if (Math.Abs(Nearest(bs, gt)) <= 0.07) res.GridOn[(int)gl]++;
        }
        foreach (var (kt, _, kl) in L.Kicks.Where(k => k.T >= Skip))
        {
            res.KickN[(int)kl]++;
            if (Math.Abs(Nearest(bs, kt)) <= 0.07) res.KickOn[(int)kl]++;
        }
        for (int i = f0; i < n; i++) res.Bright.Add(L.Bright[i]);
        foreach (var b in bs)
        {
            int fi = Math.Clamp((int)Math.Round(b * Fps), 0, frameLocks.Count - 1);
            bool lit = times.Count > 0 && Math.Abs(Nearest(times, b)) <= 0.07;
            res.Beats3[(int)frameLocks[fi]]++;
            if (lit) res.Lit3[(int)frameLocks[fi]]++;
            if (frameLocks[fi] == BeatLock.Locked) { res.BeatsLocked++; if (lit) res.BeatsLockedLit++; }
            else { res.BeatsOther++; if (lit) res.BeatsOtherLit++; }
        }
        return res;
    }

    /// <summary>
    /// 光が立ち上がったコマ（s〜i）で、何が明るさを上げたか:
    ///   pred = 予測した拍の頭をまたいでビートの光が上がった、kick = キック・立ち上がりの出来事でビートの光が上がった、
    ///   fl = ビートの光が上がった（出来事は前のコマまでに届いていた）、bass = 低音が上がった、other = それ以外（色の切り替え・メロディなど）
    /// </summary>
    private static string Source(Lane L, int s, int i)
    {
        int a = Math.Max(1, s - 1);
        bool cross = false, kick = false;
        float flRise = 0, bassRise = 0;
        for (int k = a; k <= i; k++)
        {
            cross |= L.Cross[k] != 0;
            kick |= L.Kick[k] >= 12;
            flRise = Math.Max(flRise, L.Fl[k] - L.Fl[a - 1]);
            bassRise = Math.Max(bassRise, L.Bass[k] - L.Bass[a - 1]);
        }
        if (flRise > 0.05)
        {
            if (cross) { var cs = L.Cross.Skip(a).Take(i - a + 1).ToList(); return cs.Contains((byte)2) ? "pred" : cs.Contains((byte)3) ? "kickF" : "predU"; }
            return kick ? "kick" : "fl";
        }
        if (bassRise > 0.05) return kick ? "bass+kick" : "bass";
        return "other";
    }

    private static readonly Dictionary<(Type, string), System.Reflection.PropertyInfo?> Props = new();

    private static object Prop(object o, string name)
    {
        var key = (o.GetType(), name);
        if (!Props.TryGetValue(key, out var p)) Props[key] = p = o.GetType().GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        return p!.GetValue(o)!;
    }

    private static object? FlashOf(IMusicEffect? fx)
    {
        if (fx is null) return null;
        var key = (fx.GetType(), "Flash");
        if (!Props.TryGetValue(key, out var p)) Props[key] = p = fx.GetType().GetProperty("Flash", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        return p?.GetValue(fx);
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

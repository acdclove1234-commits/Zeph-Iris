// ==================================================
// SlowDeviceBench.cs - 更新の遅いデバイス（グラボ・メモリ）で、光り方ごとに「見た目がどれだけ音楽で変わるか」を数値にする
//   環境変数を渡したときだけ動く（ふつうのテストでは何もしない）:
//     SLOWDEV_BENCH=1        合成した曲（ドラム・ベース・歌・パッド・金管）で測る
//     SLOWDEV_WAV=フォルダー  録音した曲（ステレオの WAV。48 kHz でも 96 / 192 kHz でもよい: アプリと同じに、そのままの周波数で AudioEngine に渡す。song_NN.wav）で測る。曲の名前は出さず、song_NN（なければ file_k）だけ出す
//   本物の送り方（RgbDeviceChannel: 送る回数の上限・ビートの頭で早めに送る・小さな変化は送らない・LED に届くまでの遅れ）で送り、
//   LED に実際に見えている色（最後に送った色）を 1/60 秒ごとに見る。
//   出す値（最初の 8 秒は数えない）:
//     wr/s   … 1 秒に送った回数
//     dB/s   … 1 秒ごとの、見えている明るさ（目で見た明るさ = sRGB。LED の平均）の最大 − 最小（中央値・p90）
//     dE/s   … 1 秒ごとの、見えている色（OKLab。LED の平均）のいちばん離れた 2 つの差（中央値）。0.02 くらいで違いが分かる
//     dE/4s  … 4 秒ごとの同じ値（ゆっくりした色の移り変わり）
//     vis/s  … 見て分かる変化（dE ≥ 0.02 か 明るさ ≥ 0.03）の回数（1 秒あたり）
//     onbeat … 見て分かる「始まり」（明るくなる 0.03 以上、または明るさが下がらずに色が 0.02 以上変わる。0.25 秒以内の続きは除く）のうち、拍（±70 ms）に乗っている割合
//     still  … 2 秒の間、見て分かる変化が 1 つもなかった時間の割合（止まって見える）
//     flash  … 相対輝度が 0.1 以上行き来した回数の、どの 1 秒でもいちばん多い値 ÷ 2（光感受性の目安。3 以下）
//     blip   … 明るさが 0.08 以上変わって 0.25 秒以内に戻った回数（1 分あたり。「一瞬ちかっとする」の目安）
//   調べる用: SLOWDEV_STYLES=BeatHue,Ensemble…（光り方）、SLOWDEV_DEVICES=gpu,direct,dram,asrock,strip、SLOWDEV_PAL=Rainbow,Solid、
//             SLOWDEV_SEC=秒（曲ごとの長さの上限。既定 60）、SLOWDEV_TRACE=光り方:デバイス:秒（その 4 秒の送った色を出す）、
//             SLOWDEV_UNLOCK=1（テンポをつかんでいないことにする: 拍の予測を渡さない）、SLOWDEV_PROTO=0（設計案の試作 PROTO を外す）、
//             DIRECT_OPTS=nohold,slowbeat,settle（直接書き込むグラボの描き方・送り方。DirectGpuOptions。none で 6.15.8 まで）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class SlowDeviceBenchTests
{
    private const int Fs = 48000;
    private const double Tick = 1.0 / 60, T0 = 1000, Skip = 8, BeatTol = 0.07;
    private const double VisDe = 0.02, VisDb = 0.03;

    public sealed record Score(string Label, double Writes, double DbMed, double DbP90, double DeMed, double De4, double Vis, double OnBeat,
        double Still, double Flash, double Blips, double MeanB)
    {
        /// <summary>見て分かる「始まり」の回数（1 秒あたり）</summary>
        public double Starts { get; init; }
    }

    private sealed class Lane
    {
        public required string Style, DeviceName, Pal;
        public RgbDeviceChannel? Ch;
        /// <summary>設計案の試作（Ch の代わり）</summary>
        public SlowEnsembleProto? P;
        public readonly List<(double T, Rgb[] F)> Sends = new();
        public string Label => $"{Style,-9} {DeviceName,-6} {Pal,-7}";
    }

    public static RgbDevice DeviceOf(string name) => name switch
    {
        // OpenRGB を通すグラボ（PNY Epic-X と同じ LED の数・ゾーン）: 5 回/秒・小さな変化は送らない・明るさ一定
        "gpu" => new RgbDevice(2, "PNY GeForce RTX 5070Ti ARGB Epic-X OC", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }),
        // 直接書き込むグラボ: 10 回/秒
        "direct" => new RgbDevice(2, "PNY GeForce RTX 5070Ti ARGB Epic-X OC", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }) { DirectGpu = true },
        "dram" => new RgbDevice(3, "ENE DRAM", 8, RgbDevice.TypeDram, null, "I2C: x"),
        "asrock" => new RgbDevice(1, "ASRock Radeon RX 7900 XTX Phantom Gaming", 4, RgbDevice.TypeGpu, new[] { 1, 1, 1, 1 }),
        _ => new RgbDevice(0, "Strip", 24, 4, null, "HID: x"),
    };

    private static string[] List(string env, string fallback) =>
        (Environment.GetEnvironmentVariable(env) is { Length: > 0 } s ? s : fallback).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>合成した曲（BPM ごと）: 拍の時刻も返す</summary>
    public static (float[] L, float[] R, List<double> Beats) Synth(double bpm, double sec, int seed)
    {
        var g = new SignalGen(Fs, sec, seed: seed);
        g.DenseMix(bpm, 0.5, sec - 0.2);
        g.BassLine(0.5, sec - 0.2, bpm / 60, InstrumentSources.BassNotes, 0.2);
        g.VocalTone(4, 14, InstrumentSources.VocalNotes, 60 / bpm);
        g.PadChord(12, 22, InstrumentSources.PadChord1);
        g.BrassTone(22, sec - 0.5, InstrumentSources.BrassNotes, 60 / bpm, 0.3);
        var beats = g.KickTimes.ToList();
        return (g.Buf, g.Buf, beats);
    }

    public static void TestSlowDeviceSynthBench()
    {
        if (Environment.GetEnvironmentVariable("SLOWDEV_BENCH") != "1") return;
        double sec = double.TryParse(Environment.GetEnvironmentVariable("SLOWDEV_SEC"), System.Globalization.CultureInfo.InvariantCulture, out double s0) ? Math.Min(s0, 60) : 32;
        var all = new Dictionary<string, List<Score>>();
        foreach (double bpm in new[] { 118.0, 128 })
        {
            var (l, r, beats) = Synth(bpm, sec, (int)bpm);
            T.Info($"synth {bpm:0} BPM {sec:0} s");
            foreach (var sc in Run(l, null, beats, Fs))
            {
                T.Info("   " + Format(sc));
                (all.TryGetValue(sc.Label, out var x) ? x : all[sc.Label] = new()).Add(sc);
            }
        }
        Average(all);
    }

    public static void TestSlowDeviceWavBench()
    {
        string? dir = Environment.GetEnvironmentVariable("SLOWDEV_WAV");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        double limit = double.TryParse(Environment.GetEnvironmentVariable("SLOWDEV_SEC"), System.Globalization.CultureInfo.InvariantCulture, out double s0) ? s0 : 60;
        var all = new Dictionary<string, List<Score>>();
        int k = 0;
        foreach (var f in Directory.GetFiles(dir, "*.wav").OrderBy(x => x))
        {
            string id = SafeId(f, k++);
            var (l, r, fs) = ReadWavStereo(f, limit);
            if (fs <= 0 || l.Length < fs * (Skip + 4)) { T.Info($"{id}: skipped (rate {fs}, {l.Length / (double)Math.Max(1, fs):F0} s)"); continue; }
            var beats = ReferenceBeats(l, r, fs, out double bpm);
            T.Info($"{id} {l.Length / (double)fs,4:F0} s {fs} Hz (analysis {new AudioEngine(fs).AnalysisRate} Hz) ref {bpm,5:F1} BPM");
            foreach (var sc in Run(l, r, beats, fs))
            {
                T.Info("   " + Format(sc));
                (all.TryGetValue(sc.Label, out var x) ? x : all[sc.Label] = new()).Add(sc);
            }
        }
        Average(all);
    }

    /// <summary>曲の名前を出さない: song_NN ならそのまま、そうでなければ file_k</summary>
    public static string SafeId(string path, int k)
    {
        string n = Path.GetFileNameWithoutExtension(path);
        return System.Text.RegularExpressions.Regex.IsMatch(n, "^song_[0-9]+$") ? n : $"file_{k:00}";
    }

    private static void Average(Dictionary<string, List<Score>> all)
    {
        if (all.Count == 0) return;
        T.Info("average over songs:");
        foreach (var (label, l) in all)
            T.Info("   " + Format(new Score(label, l.Average(x => x.Writes), l.Average(x => x.DbMed), l.Average(x => x.DbP90), l.Average(x => x.DeMed),
                l.Average(x => x.De4), l.Average(x => x.Vis), l.Average(x => x.OnBeat), l.Average(x => x.Still), l.Max(x => x.Flash), l.Average(x => x.Blips),
                l.Average(x => x.MeanB)) { Starts = l.Average(x => x.Starts) }));
    }

    public static string Format(Score s) =>
        $"{s.Label} wr/s {s.Writes,4:F1}  dB/s {s.DbMed,5:F3} p90 {s.DbP90,5:F3}  dE/s {s.DeMed,5:F3}  dE/4s {s.De4,5:F3}  vis/s {s.Vis,4:F2}  onbeat {s.OnBeat * 100,3:F0}% of {s.Starts,4:F2}/s  still {s.Still * 100,3:F0}%  flash {s.Flash,3:F1}  blip/min {s.Blips,5:F1}  meanB {s.MeanB:F2}";

    /// <summary>
    /// 曲を本物の分析に流し、光り方 × デバイス × パレットごとに RgbDeviceChannel で描いて送る。
    /// r が null ならモノラル。beats は物差しの拍（曲の頭からの秒）。fs は音のサンプリング周波数（アプリと同じに、そのまま AudioEngine に 10 ミリ秒ずつ渡す）
    /// </summary>
    private static List<Score> Run(float[] l, float[]? r, List<double> beats, int fs)
    {
        var styles = List("SLOWDEV_STYLES", string.Join(",", MusicStyles.All.Where(x => x != MusicStyles.Strobe)));
        var devices = List("SLOWDEV_DEVICES", "gpu,direct,dram");
        var pals = List("SLOWDEV_PAL", $"{Palettes.Rainbow},{Palettes.Solid}");
        var lanes = new List<Lane>();
        foreach (var dn in devices)
            foreach (var st in styles)
                foreach (var pal in pals)
                {
                    var dev = DeviceOf(dn);
                    var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = st, Brightness = 100, StaticColor = "#6A00FF" };
                    var music = ResolvedMusic.Default with { Effect = st, Palette = pal };
                    var ch = new RgbDeviceChannel(dev, T0) { SharedColor = new SharedColor() };
                    ch.Configure(cfg, music, new RgbDeviceSettings { Name = dev.Name }, false);
                    lanes.Add(new Lane { Style = st, DeviceName = dn, Pal = pal, Ch = ch });
                }
        // 設計案の試作（SLOWDEV_PROTO=0 で外す）: 遅いデバイスの「楽器で光る」。SLOWDEV_PROTO_TUNE=ふくらみの大きさ,時定数,最小の間隔 で試せる
        if (Environment.GetEnvironmentVariable("SLOWDEV_PROTO_TUNE") is { Length: > 0 } tune && tune.Split(",") is [var ta, var tt, var tg])
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            SlowEnsembleProto.SwellAmp = double.Parse(ta, ic);
            SlowEnsembleProto.SwellTauSec = double.Parse(tt, ic);
            SlowEnsembleProto.SwellGapSec = double.Parse(tg, ic);
        }
        if (Environment.GetEnvironmentVariable("SLOWDEV_PROTO") != "0")
            foreach (var dn in devices.Where(d => d is "gpu" or "direct" or "dram"))
                foreach (var pal in pals)
                    lanes.Add(new Lane { Style = "PROTO", DeviceName = dn, Pal = pal, P = new SlowEnsembleProto(DeviceOf(dn), pal) });

        var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
        bool unlock = Environment.GetEnvironmentVariable("SLOWDEV_UNLOCK") == "1";
        DirectGpuOptions.Apply();
        int pos = 0, chunk = Math.Max(1, fs / 100);
        var mid = new float[chunk];
        var side = new float[chunk];
        double total = l.Length / (double)fs;
        for (int k = 0; ; k++)
        {
            double sec = k * Tick;
            if (sec + 0.15 > total) break;
            // 音は 0.1 秒先まで流しておく（グラボの遅れ 60 ms 先の音を読むので）
            while (pos < l.Length && pos / (double)fs < sec + 0.1)
            {
                int n = Math.Min(chunk, l.Length - pos);
                if (r is null) e.Push(new ReadOnlySpan<float>(l, pos, n), T0 + pos / (double)fs);
                else
                {
                    for (int i = 0; i < n; i++) { mid[i] = 0.5f * (l[pos + i] + r[pos + i]); side[i] = 0.5f * (l[pos + i] - r[pos + i]); }
                    e.Push(mid.AsSpan(0, n), side.AsSpan(0, n), T0 + pos / (double)fs);
                }
                pos += n;
            }
            double t = T0 + sec;
            var beatIn = unlock ? BeatState.None : e.Beat;
            foreach (var L in lanes)
            {
                if (L.P is { } proto)
                {
                    if (proto.Step(t, e, beatIn) is { } frame) L.Sends.Add((sec + proto.LatencySec, frame));
                    continue;
                }
                var ch = L.Ch!;
                ch.Prepare(t, Tick, e.Features, e.Events, beatIn, null, false, false);
                if (!ch.Render()) continue;
                var reason = ch.ShouldSend(t, Tick, true);
                if (reason == SendReason.None) continue;
                ch.MarkSent(t, Tick, reason, 1);
                // LED に見えるのは、送ってから遅れの分だけ後
                L.Sends.Add((sec + ch.EffectiveLatencyMs / 1000.0, (Rgb[])ch.Frame.Clone()));
            }
        }
        DirectGpuOptions.Reset();
        return lanes.Select(L => Measure(L, beats, total)).ToList();
    }

    /// <summary>見えている色（LED の平均の OKLab）・目で見た明るさ（sRGB の平均）・相対輝度</summary>
    private static (Lab Lab, double B, double Y) Look(Rgb[] f)
    {
        double sl = 0, sa = 0, sb = 0, br = 0, y = 0;
        foreach (var c in f)
        {
            // 送った値は光の量に比例する（ガンマ補正は描く側で済んでいる）
            var lab = OkLab.FromLinear(c.R / 255.0, c.G / 255.0, c.B / 255.0);
            sl += lab.L; sa += lab.A; sb += lab.B;
            br += OkLab.LinearToSrgb(Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0);
            y += (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        }
        int n = Math.Max(1, f.Length);
        return (new Lab((float)(sl / n), (float)(sa / n), (float)(sb / n)), br / n, y / n);
    }

    private static double De(Lab a, Lab b) => Math.Sqrt((a.L - b.L) * (a.L - b.L) + (a.A - b.A) * (a.A - b.A) + (a.B - b.B) * (a.B - b.B));

    private static Score Measure(Lane L, List<double> beats, double total)
    {
        // 見えている状態の列（送るたびに切り替わる）
        var states = L.Sends.Select(s => (s.T, Look: Look(s.F))).ToList();
        double end = total - 0.2;
        int writes = L.Sends.Count(s => s.T >= Skip && s.T < end);
        double span = Math.Max(1e-6, end - Skip);
        // 1/60 秒ごとに見えている状態
        var seen = new List<(double T, Lab Lab, double B, double Y)>();
        int si = -1;
        for (double t = 0; t < end; t += Tick)
        {
            while (si + 1 < states.Count && states[si + 1].T <= t) si++;
            if (si < 0) continue;
            var lk = states[si].Look;
            if (t >= Skip) seen.Add((t, lk.Lab, lk.B, lk.Y));
        }
        if (seen.Count < 120) return new Score(L.Label, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0);

        // 調べる用: 送った色を 4 秒ぶん
        if (Environment.GetEnvironmentVariable("SLOWDEV_TRACE") is { } tr && tr.Split(':') is [var ts, var td, var tt]
            && ts == L.Style && td == L.DeviceName && double.TryParse(tt, System.Globalization.CultureInfo.InvariantCulture, out double ta))
        {
            foreach (var (t, f) in L.Sends.Where(x => x.T >= ta && x.T < ta + 4))
            {
                var lk = Look(f);
                bool atBeat = beats.Any(b => Math.Abs(b - t) < BeatTol);
                T.Info($"  {L.Pal,-7} {t,7:F3} {(atBeat ? '*' : ' ')} B {lk.B:F3} L {lk.Lab.L:F3} a {lk.Lab.A,6:F3} b {lk.Lab.B,6:F3}  led0 {f[0].R},{f[0].G},{f[0].B}");
            }
        }

        // 1 秒・4 秒ごとの幅
        var db = new List<double>();
        var de = new List<double>();
        var de4 = new List<double>();
        for (double w = Skip; w + 1 <= end; w += 1)
        {
            var win = seen.Where(x => x.T >= w && x.T < w + 1).ToList();
            if (win.Count == 0) continue;
            db.Add(win.Max(x => x.B) - win.Min(x => x.B));
            de.Add(MaxPair(win.Select(x => x.Lab).ToList()));
        }
        for (double w = Skip; w + 4 <= end; w += 4)
            de4.Add(MaxPair(seen.Where(x => x.T >= w && x.T < w + 4).Select(x => x.Lab).ToList()));

        // 見て分かる変化（見えている状態が切り替わったところ）
        var vis = new List<double>();
        var starts = new List<double>();
        for (int i = 1; i < seen.Count; i++)
        {
            bool v = De(seen[i].Lab, seen[i - 1].Lab) >= VisDe || Math.Abs(seen[i].B - seen[i - 1].B) >= VisDb;
            if (!v) continue;
            vis.Add(seen[i].T);
            bool start = seen[i].B - seen[i - 1].B >= VisDb || (De(seen[i].Lab, seen[i - 1].Lab) >= VisDe && seen[i].B >= seen[i - 1].B - 0.005 && seen[i].Lab.L >= seen[i - 1].Lab.L - 0.002);
            if (start && (starts.Count == 0 || seen[i].T - starts[^1] > 0.25)) starts.Add(seen[i].T);
        }
        double onBeat = starts.Count == 0 ? 0 : starts.Count(t => beats.Any(b => Math.Abs(b - t) < BeatTol)) / (double)starts.Count;
        // 止まって見える時間（2 秒の窓に変化がない）
        int stillWin = 0, wins = 0;
        for (double w = Skip; w + 2 <= end; w += 0.5)
        {
            wins++;
            if (!vis.Any(t => t >= w && t < w + 2)) stillWin++;
        }
        // 光感受性の目安: 相対輝度が 0.1 以上行き来した回数（どの 1 秒でもいちばん多い値 ÷ 2）
        var turns = new List<double>();
        double ext = seen[0].Y;
        int dir = 0;
        foreach (var x in seen)
        {
            if (dir >= 0 && x.Y > ext) ext = x.Y;
            else if (dir <= 0 && x.Y < ext) ext = x.Y;
            if (dir >= 0 && ext - x.Y >= 0.1) { turns.Add(x.T); dir = -1; ext = x.Y; }
            else if (dir <= 0 && x.Y - ext >= 0.1) { turns.Add(x.T); dir = 1; ext = x.Y; }
        }
        double flash = 0;
        foreach (var t in turns) flash = Math.Max(flash, turns.Count(u => u >= t && u < t + 1) / 2.0);
        // 一瞬ちかっとする: 明るさが 0.08 以上変わって、0.25 秒以内にもとの 0.03 以内へ戻る
        int blips = 0, shown = 0;
        double lastBlip = double.NegativeInfinity;
        // 調べる用: SLOWDEV_BLIPS=光り方:デバイス:パレット で、その組の「ちかっ」を 6 つまで出す（見えている明るさの並び）
        bool showBlips = Environment.GetEnvironmentVariable("SLOWDEV_BLIPS") is { } sb && sb == $"{L.Style}:{L.DeviceName}:{L.Pal}";
        for (int i = 1; i < seen.Count; i++)
        {
            double before = seen[i - 1].B, jump = seen[i].B - before;
            if (Math.Abs(jump) < 0.08 || seen[i].T - lastBlip < 0.25) continue;
            for (int j = i + 1; j < seen.Count && seen[j].T - seen[i].T <= 0.25; j++)
                if (Math.Abs(seen[j].B - before) <= 0.03)
                {
                    blips++;
                    lastBlip = seen[i].T;
                    if (showBlips && shown++ < 6)
                    {
                        bool atBeat = beats.Any(b => Math.Abs(b - seen[i].T) < BeatTol);
                        T.Info($"  blip {seen[i].T,7:F3}{(atBeat ? " *" : "  ")} B " + string.Join(" ", seen.Skip(Math.Max(0, i - 3)).Take(j - i + 6).Select(x => x.B.ToString("F2"))));
                    }
                    break;
                }
        }
        return new Score(L.Label, writes / span, Median(db), Pct(db, 0.9), Median(de), Median(de4), vis.Count / span, onBeat,
            wins == 0 ? 0 : stillWin / (double)wins, flash, blips / span * 60, seen.Average(x => x.B)) { Starts = starts.Count / span };
    }

    private static double MaxPair(List<Lab> xs)
    {
        // 同じ色が続くことが多いので、違う色だけで比べる
        var d = new List<Lab>();
        foreach (var x in xs) if (d.Count == 0 || De(d[^1], x) > 1e-4) d.Add(x);
        double m = 0;
        for (int i = 0; i < d.Count; i++)
            for (int j = i + 1; j < d.Count; j++) m = Math.Max(m, De(d[i], d[j]));
        return m;
    }

    private static double Median(List<double> xs) => Pct(xs, 0.5);

    private static double Pct(List<double> xs, double q)
    {
        if (xs.Count == 0) return 0;
        var a = xs.OrderBy(x => x).ToArray();
        return a[(int)Math.Clamp(q * (a.Length - 1), 0, a.Length - 1)];
    }

    /// <summary>物差しの拍（曲全体の立ち上がりの強さから後で決める。ModeSyncBench と同じ）</summary>
    private static List<double> ReferenceBeats(float[] l, float[] r, int fs, out double bpm)
    {
        var mono = new float[l.Length];
        for (int i = 0; i < mono.Length; i++) mono[i] = 0.5f * (l[i] + r[i]);
        var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
        var hops = new List<(double T, double Odf)>();
        e.Onsets.DiagHop = (t, odf, bn) => hops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
        var run = new EngineRun(e).Feed(mono, chunk: Math.Max(1, fs / 100));
        return BeatReference.Track(hops, out bpm).Select(t => t - run.T0).ToList();
    }

    /// <summary>ステレオの WAV（16・24 bit 整数、32 bit 浮動小数）を左・右で読む（limitSec 秒まで。モノラルなら左右に同じ音）</summary>
    public static (float[] L, float[] R, int Fs) ReadWavStereo(string path, double limitSec = double.PositiveInfinity)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        br.ReadBytes(12);
        int ch = 2, fs = 48000, bits = 32, format = 3;
        while (br.BaseStream.Position < br.BaseStream.Length)
        {
            string id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            if (id == "fmt ")
            {
                format = br.ReadUInt16();
                ch = br.ReadInt16();
                fs = br.ReadInt32();
                br.ReadInt32();
                br.ReadInt16();
                bits = br.ReadInt16();
                br.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                bool isFloat = format == 3 || (format != 1 && bits == 32);
                // 書きかけの録音（data の大きさが実際より大きい・0xFFFFFFFF）でも、あるところまで読む
                long avail = size >= 0 ? Math.Min(size, br.BaseStream.Length - br.BaseStream.Position) : br.BaseStream.Length - br.BaseStream.Position;
                int n = (int)(avail / (bits / 8) / Math.Max(1, ch));
                if (double.IsFinite(limitSec)) n = (int)Math.Min(n, limitSec * fs);
                var l = new float[n];
                var r = new float[n];
                for (int i = 0; i < n; i++)
                {
                    for (int c = 0; c < ch; c++)
                    {
                        float s = bits switch
                        {
                            16 => br.ReadInt16() / 32768f,
                            24 => (br.ReadByte() | (br.ReadByte() << 8) | (br.ReadSByte() << 16)) / 8388608f,
                            _ => isFloat ? br.ReadSingle() : br.ReadInt32() / 2147483648f,
                        };
                        if (c == 0) l[i] = s;
                        else if (c == 1) r[i] = s;
                    }
                    if (ch == 1) r[i] = l[i];
                }
                return (l, r, fs);
            }
            else br.ReadBytes(size);
        }
        return (Array.Empty<float>(), Array.Empty<float>(), fs);
    }
}


/// <summary>
/// 設計案の試作: 遅いデバイス（直接書き込むグラボ・メモリ・OpenRGB のグラボ）の「楽器で光る」。製品のコードではない（測るためだけ）。
///   明るさ（明るさを変えてよいデバイス）: 下地 I = 0.42 + 0.22·Section + 0.12·歌 + 0.08·ギター鍵盤 + 0.06·パッド·呼吸（上がる 0.6 秒・下がる 1.5 秒）、
///     拍のふくらみ: 拍（テンポをつかんでいれば予測した拍、なければキック・スネア・強い立ち上がり）で SwellAmp だけ上げ、τ SwellTauSec で戻す。
///     ふくらみの間は SwellGapSec 以上あける（1 秒に 2.4 回まで）
///   色: 単色は選んだ色だけ（明るさだけ）。ほかは、目立つ楽器（歌・パッド・ギター鍵盤・金管・ベース・ドラム）の色（RoleEffect.OffsetOf の位置）。
///     目立つ楽器は DomRatio 倍以上で DomHoldSec 続いたら替え、DomFadeSec かけて移る（OpenRGB のグラボは、次の拍で一度に替える）
///   OpenRGB のグラボ（明るさ一定）: 拍で白へ LowWriteWhite 寄せて LowWriteHoldSec 保ち、一度に戻す（1 拍 2 回の書き込み）
/// </summary>
public sealed class SlowEnsembleProto
{
    public static double SwellAmp = 0.20, SwellTauSec = 0.35, SwellGapSec = 0.42, DomHoldSec = 2.0, DomFadeSec = 1.5, DomRatio = 1.25;
    public static float LowWriteWhite = 0.22f;
    public static double LowWriteHoldSec = 0.18;
    private readonly RgbDevice _dev;
    private readonly bool _mono, _lowWrite;
    private readonly ColorPalette _pal;
    private readonly double _interval;
    private readonly int _threshold;
    private readonly float _f0;
    public double LatencySec { get; }
    private readonly SendScheduler _sched = new();
    private readonly BeatCrossing _cross = new();
    private readonly SectionMeter _section = new();
    private readonly AudioFrame _f = new();
    private readonly List<AudioEvent> _ev = new();
    private long _cursor;
    private double _lastEval = double.NaN, _lastT = double.NaN;
    private float _base, _vocal, _pad, _harm, _brass, _bass, _drums;
    private double _breath;
    private double _accT = double.NegativeInfinity, _lastDrumT = double.NegativeInfinity;
    private float _accA;
    private int _dom = -1, _cand = -1;
    private double _candT;
    private Lab _col, _from, _to;
    private double _fadeT = double.NegativeInfinity;
    private Rgb[] _sent = Array.Empty<Rgb>();
    private bool _hasSent, _holdSent;

    /// <summary>層の色の位置: 0 歌、1 パッド、2 ギター・鍵盤、3 金管、4 ベース、5 ドラム</summary>
    private static readonly double[] Offsets = { RoleEffect.OffsetOf(MusicRole.Vocal), RoleEffect.OffsetOf(MusicRole.Pad), RoleEffect.OffsetOf(MusicRole.Harmony),
        RoleEffect.OffsetOf(MusicRole.Vocal), RoleEffect.OffsetOf(MusicRole.BassLine), RoleEffect.OffsetOf(MusicRole.Kick) };

    public SlowEnsembleProto(RgbDevice dev, string palette)
    {
        _dev = dev;
        _mono = palette == Palettes.Solid;
        _pal = ColorPalette.Get(palette, "#6A00FF");
        _lowWrite = dev.IsLowWrite;
        var s = new RgbDeviceSettings { Name = dev.Name };
        _interval = 1.0 / RgbDeviceChannel.MaxFpsFor(dev, s);
        _threshold = dev.IsFlickerProne ? RgbDeviceChannel.FlickerProneChangeThreshold : dev.IsGpu ? RgbDeviceChannel.GpuChangeThreshold
            : dev.IsSlow ? RgbDeviceChannel.SlowChangeThreshold : 0;
        // 直接書き込むグラボは、書き込みにかかる時間（約 35 ミリ秒）+ 1
        LatencySec = (dev.DirectGpu ? 36 : RgbDeviceChannel.LatencyFor(dev, s)) / 1000.0;
        double floor = dev.IsSlow ? Math.Max(ResolvedMusic.Default.MinBrightness, DeviceRenderer.SlowMinBrightness) : ResolvedMusic.Default.MinBrightness;
        _f0 = OutputStage.Floor(floor, true);
        _col = _from = _to = _pal.LabAt(_mono ? 0 : Offsets[5]);
    }

    private static float Span01(float x, float from, float span) => Math.Clamp((x - from) / span, 0f, 1f);

    /// <summary>1 コマ（1/60 秒）。送るなら LED の値を返す</summary>
    public Rgb[]? Step(double t, AudioEngine e, BeatState beat)
    {
        double tEval = t + LatencySec;
        double t0 = double.IsNaN(_lastEval) ? tEval : _lastEval;
        float dt = double.IsNaN(_lastT) ? 1 / 60f : (float)(t - _lastT);
        _lastEval = tEval;
        _lastT = t;
        if (!e.Features.Sample(tEval, _f)) _f.Clear();
        _ev.Clear();
        e.Events.Read(ref _cursor, tEval, _ev);
        // beat: e.Beat（SLOWDEV_UNLOCK=1 なら BeatState.None = テンポをつかんでいない）
        bool locked = beat.Locked;
        bool cue = _cross.Step(beat, locked, t0, tEval, out double bt);
        float section = _section.Update(_f, dt);

        // 楽器の大きさ（EnsembleEffect と同じ手がかり・幅）
        _vocal = EffectContext.Follow(_vocal, Span01(_f.Vocal, EnsembleEffect.TextureFrom, EnsembleEffect.TextureSpan), dt, 0.25, 0.8);
        _pad = EffectContext.Follow(_pad, Span01(_f.Pad, EnsembleEffect.TextureFrom, EnsembleEffect.TextureSpan), dt, 0.6, 1.2);
        _harm = EffectContext.Follow(_harm, Span01(_f.GuitarKeys, EnsembleEffect.GuitarFrom, EnsembleEffect.GuitarSpan), dt, 0.08, 0.3);
        _brass = EffectContext.Follow(_brass, Span01(_f.Brass, EnsembleEffect.TextureFrom, EnsembleEffect.TextureSpan), dt, 0.3, 0.8);
        _bass = EffectContext.Follow(_bass, Span01(_f.BassLevel, EnsembleEffect.BassFrom, EnsembleEffect.BassSpan), dt, 0.08, 0.35);
        float active = _f.Activity > 0.3f ? 1f : 0f;
        _breath += dt / 4.0;
        float breath = 0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * _breath);

        // 拍のふくらみ: テンポをつかんでいれば予測した拍、なければキック・スネア（ドラムがなければ強い立ち上がり）
        double accT = double.NaN;
        float accS = 0;
        if (locked && cue) { accT = bt; accS = 1f; }
        foreach (var ev in _ev)
        {
            bool drum = ev.Kind is AudioEventKind.Kick or AudioEventKind.Snare;
            if (drum) _lastDrumT = Math.Max(_lastDrumT, ev.T);
            if (locked) continue;
            bool use = drum || (ev.Kind == AudioEventKind.Onset && ev.Strength >= 1.5f && ev.T - _lastDrumT > 1.5);
            if (use && (double.IsNaN(accT) || ev.T < accT))
            {
                accT = ev.T;
                accS = Math.Clamp(0.6f + 0.25f * (ev.Strength - 1), 0.6f, 1f) * (ev.Kind == AudioEventKind.Snare ? 0.8f : 1f);
            }
        }
        bool accent = false;
        if (!double.IsNaN(accT) && accT - _accT >= SwellGapSec && active > 0)
        {
            _accT = accT;
            _accA = accS;
            accent = true;
            _holdSent = false;
            if (!locked) _sched.Latch();
        }
        _drums = EffectContext.Follow(_drums, tEval - _lastDrumT < 1.5 ? 1f : 0f, dt, 0.3, 1.5);
        double age = Math.Max(0, tEval - _accT);
        float env = (float)(_accA * Math.Exp(-age / SwellTauSec));

        float target = active * (0.42f + 0.22f * section + 0.12f * _vocal + 0.08f * _harm + 0.06f * _pad * breath);
        _base = EffectContext.Follow(_base, Math.Clamp(target, 0, 0.78f), dt, 0.6, 1.5);
        float I = Math.Clamp(_base + (float)SwellAmp * env * active, 0, 1);

        // 目立つ楽器の色
        if (!_mono)
        {
            float[] lv = { _vocal, _pad, _harm, _brass, 0.8f * _bass, 0.7f * _drums };
            int best = 0;
            for (int k = 1; k < lv.Length; k++) if (lv[k] > lv[best]) best = k;
            if (_dom < 0) { _dom = best; _col = _from = _to = _pal.LabAt(Offsets[best]); }
            else if (best != _dom && lv[best] > DomRatio * lv[_dom] + 0.02f)
            {
                if (best != _cand) { _cand = best; _candT = tEval; }
                else if (tEval - _candT >= DomHoldSec && (!_lowWrite || accent))
                {
                    _dom = best;
                    _cand = -1;
                    _from = _col;
                    _to = _pal.LabAt(Offsets[best]);
                    if (best == 3) _to = Lab.Lerp(_to, Lab.White, 0.2f);
                    _fadeT = _lowWrite ? double.NegativeInfinity : tEval;
                    if (_lowWrite) _col = _to;
                }
            }
            else _cand = -1;
            if (!_lowWrite && double.IsFinite(_fadeT))
            {
                float u = (float)Math.Clamp((tEval - _fadeT) / DomFadeSec, 0, 1);
                u = u * u * (3 - 2 * u);
                _col = Lab.Lerp(_from, _to, u);
            }
        }
        LinColor light;
        bool hold = _lowWrite && tEval - _accT < LowWriteHoldSec && active > 0;
        if (_lowWrite)
        {
            // 明るさは一定。拍で白へ寄せて、LowWriteHoldSec 後に一度に戻す
            light = OkLab.ToLed(hold ? Lab.Lerp(_col, Lab.White, LowWriteWhite * _accA) : _col, true);
        }
        else light = OkLab.ToLed(_col, true) * OutputStage.Ilin(I, _f0, true);

        var frame = new Rgb[_dev.LedCount];
        var code = new Rgb((byte)Math.Round(255 * Math.Clamp(light.R, 0, 1)), (byte)Math.Round(255 * Math.Clamp(light.G, 0, 1)), (byte)Math.Round(255 * Math.Clamp(light.B, 0, 1)));
        Array.Fill(frame, code);

        // 送る（本物と同じ: いつもの間隔・テンポをつかんでいれば拍の頭・ふくらみの瞬間は早めに。小さな変化は送らない）
        double next = locked && beat.HasTempo ? beat.NextBeatAfter(tEval) - tEval : double.PositiveInfinity;
        // 白へ寄せた色から戻すのは、ふくらみの後の最初の送れる機会（差が小さくても送る）
        bool back = _lowWrite && !hold && !_holdSent && double.IsFinite(_accT);
        if (back) _sched.Latch();
        var reason = _sched.Decide(t, 1 / 60.0, _interval, locked && cue, next);
        if (reason == SendReason.None) return null;
        if (_hasSent && RgbDeviceChannel.Similar(_sent, frame, _threshold) && !accent && !back) return null;
        if (back) _holdSent = true;
        _sched.Commit(t, 1 / 60.0, _interval, reason);
        _sent = frame;
        _hasSent = true;
        return frame;
    }
}

// ==================================================
// StyleSimilarityBench.cs - 音楽連動の光り方（MusicStyles.All）が「どれだけ似て見えるか」を数値にする（STYLE_SIM=1 のときだけ動く）
//   ・合成した 4 曲（四つ打ち・フィルのあるロック・静かなアンビエント・歌の多い曲）を本物の分析（AudioEngine）に 10 ミリ秒ずつ流し、
//     60 コマ／秒で全部の光り方を DeviceRenderer で描く（初期設定: パレット「レインボー」・強さ「ふつう」・激しい点滅を抑える オン）
//   ・描くデバイス: 24 個のテープ、1 個のゾーン、更新の遅いメモリ（8 個・1 秒に 8 回。光り方が置き換わる）
// 出す値:
//   光り方ごとの特徴（明るさの平均・揺れ・強い光の回数・全部同じ色の割合・場所によるむら・光の重心の動き・流れ・色の変わる速さ・
//     色の数・彩度・拍／キック／ハイハット／スネアとの相関・音の大きさとの相関）
//   (a) 特徴を光り方の間で標準化した距離、(b) 同じコマどうしの LED の明るさ・色の相関から作った距離、(c) 両方を合わせた距離
// 調べる用の環境変数: STYLE_SECS（1 曲の秒数。初期値 45）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Text;
using ProFanController;

namespace LogicTests;

public static class StyleSimilarityBenchTests
{
    private const double Fps = 60;
    /// <summary>始まりの秒数（テンポをつかむまで）は数えない</summary>
    private const double SkipSec = 6;

    private static readonly Dictionary<string, string> Code = new()
    {
        [MusicStyles.BeatHue] = "BHue", [MusicStyles.Meter] = "Metr",
        [MusicStyles.Spectrum] = "Spec", [MusicStyles.Ripple] = "Ripl",
        [MusicStyles.Sparkle] = "Sprk", [MusicStyles.Role] = "Role", [MusicStyles.Ensemble] = "Ensm",
    };

    private sealed record Song(string Id, float[] Buf, int Fs, List<double> Beats, List<double> Kicks, List<double> Hats, List<double> Snares);

    private sealed class Lane
    {
        public required string Style;
        public required string Dev;
        public required DeviceRenderer R;
        public required RenderInput In;
        public required Rgb[] Out;
        /// <summary>描く間隔（秒。0 なら毎コマ）</summary>
        public double Step;
        public double NextDraw = double.NegativeInfinity, LastDraw = double.NaN;
        public long Cursor;
        public readonly List<byte[]> Frames = new();
    }

    /// <summary>1 曲・1 デバイスで描いた、光り方ごとのコマ（[コマ][LED*3]）</summary>
    private sealed record Render(Song Song, string Dev, int N, Dictionary<string, List<byte[]>> Frames, double[] RmsDb);

    public static void TestStyleSimilarity()
    {
        if (Environment.GetEnvironmentVariable("STYLE_SIM") != "1") { T.Skip("STYLE_SIM=1 not set"); return; }
        var inv = CultureInfo.InvariantCulture;
        double secs = double.TryParse(Environment.GetEnvironmentVariable("STYLE_SECS"), NumberStyles.Float, inv, out double s) ? s : 45;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var songs = new List<Song> { FourOnFloor(secs), RockFills(secs), Ambient(secs), Vocal(secs) };
        T.Info($"songs made in {sw.Elapsed.TotalSeconds:F1} s");
        var styles = MusicStyles.All;
        var strip = new List<Render>();
        var zone = new List<Render>();
        var dram = new List<Render>();
        foreach (var song in songs)
        {
            var rs = RunSong(song, styles);
            strip.Add(rs[0]); zone.Add(rs[1]); dram.Add(rs[2]);
            T.Info($"{song.Id}: rendered in {sw.Elapsed.TotalSeconds:F1} s");
        }

        // ===== 特徴
        string[] stripNames = { "meanB", "stdB", "flash/s", "ledFl/s", "flat", "spStd", "motion", "flow", "hue°/s", "hueSprd", "sat", "rBeat", "rKick", "rHat", "rSnare", "rLoud" };
        string[] zoneNames = { "meanB", "stdB", "flash/s", "hue°/s", "sat", "rBeat", "rKick", "rHat", "rSnare", "rLoud" };
        var fStrip = styles.ToDictionary(x => x, x => Avg(strip.Select(r => StripFeatures(r, x)).ToList()));
        var fZone = styles.ToDictionary(x => x, x => Avg(zone.Select(r => ZoneFeatures(r, x)).ToList()));
        T.Info("features, 24-LED strip (average of 4 songs):");
        T.Info("  " + "style".PadRight(9) + string.Concat(stripNames.Select(n => n.PadLeft(8))));
        foreach (var x in styles) T.Info("  " + x.PadRight(9) + string.Concat(fStrip[x].Select(v => Fmt(v).PadLeft(8))));
        T.Info("features, 1-LED zone (average of 4 songs):");
        T.Info("  " + "style".PadRight(9) + string.Concat(zoneNames.Select(n => n.PadLeft(8))));
        foreach (var x in styles) T.Info("  " + x.PadRight(9) + string.Concat(fZone[x].Select(v => Fmt(v).PadLeft(8))));
        // 曲ごとの違い（強い光・拍との相関）
        foreach (var r in strip)
            T.Info($"  {r.Song.Id,-8} flash/s " + string.Join(" ", styles.Select(x => $"{Code[x]}:{StripFeatures(r, x)[2]:F1}")));
        foreach (var r in strip)
            T.Info($"  {r.Song.Id,-8} rBeat   " + string.Join(" ", styles.Select(x => $"{Code[x]}:{StripFeatures(r, x)[11]:F2}")));

        // (a) 特徴の距離（光り方の間で標準化。テープの特徴とゾーンの特徴を同じ重みで）
        var vec = styles.ToDictionary(x => x, x => fStrip[x].Concat(fZone[x]).ToArray());
        int dim = vec[styles[0]].Length;
        var mu = new double[dim];
        var sd = new double[dim];
        for (int k = 0; k < dim; k++)
        {
            mu[k] = styles.Average(x => vec[x][k]);
            sd[k] = Math.Sqrt(styles.Average(x => Math.Pow(vec[x][k] - mu[k], 2)));
        }
        int m = styles.Length;
        var featD = new double[m, m];
        for (int a = 0; a < m; a++)
            for (int b = 0; b < m; b++)
            {
                double acc = 0; int c = 0;
                for (int k = 0; k < dim; k++)
                {
                    if (sd[k] < 1e-9) continue;
                    double w = k < stripNames.Length ? 1.0 : (double)stripNames.Length / zoneNames.Length * 0.6; // ゾーンは少し軽く
                    acc += w * Math.Pow((vec[styles[a]][k] - vec[styles[b]][k]) / sd[k], 2);
                    c++;
                }
                featD[a, b] = c > 0 ? Math.Sqrt(acc / c) : 0;
            }

        // (b) コマどうしの近さ（同じ音に対して、同じコマで同じように光っているか）
        var frameD = new double[m, m];
        var perSong = new Dictionary<(int, int), double[]>();
        var parts = new Dictionary<(int, int), double[]>();
        for (int a = 0; a < m; a++)
            for (int b = a; b < m; b++)
            {
                var ds = new double[songs.Count];
                var pp = new double[5];
                for (int q = 0; q < songs.Count; q++)
                {
                    var p = FrameSim(strip[q], zone[q], styles[a], styles[b]);
                    ds[q] = 1 - p.Average();
                    for (int k = 0; k < 5; k++) pp[k] += p[k] / songs.Count;
                }
                frameD[a, b] = frameD[b, a] = ds.Average();
                perSong[(a, b)] = ds;
                parts[(a, b)] = pp;
            }

        // (c) 合わせた距離（それぞれの中央値で割って平均）
        double medF = Median(Pairs(m).Select(p => featD[p.a, p.b]));
        double medS = Median(Pairs(m).Select(p => frameD[p.a, p.b]));
        var combD = new double[m, m];
        for (int a = 0; a < m; a++)
            for (int b = 0; b < m; b++)
                combD[a, b] = a == b ? 0 : 0.5 * featD[a, b] / medF + 0.5 * frameD[a, b] / medS;

        PrintMatrix("(a) feature distance (z-scored RMS; 0 = same)", styles, featD, 100);
        PrintMatrix("(b) frame distance (1 - mean of [strip mean-brightness corr, strip LED x time corr, strip color agreement, zone brightness corr, zone color agreement])", styles, frameD, 100);
        PrintMatrix("(c) combined distance (each / its median, averaged; 1 = typical pair)", styles, combD, 100);

        T.Info("closest pairs by combined distance:");
        foreach (var (a, b) in Pairs(m).OrderBy(p => combD[p.a, p.b]).Take(14))
        {
            var pp = parts[(a, b)];
            var ds = perSong[(a, b)];
            T.Info($"  {styles[a],-9} {styles[b],-9} comb {combD[a, b]:F2}  feat {featD[a, b]:F2}  frame {frameD[a, b]:F2} " +
                   $"[stripB {pp[0]:F2} stripLED {pp[1]:F2} stripCol {pp[2]:F2} zoneB {pp[3]:F2} zoneCol {pp[4]:F2}]  per song {string.Join("/", ds.Select(d => d.ToString("F2", inv)))}  " +
                   $"why: {Why(vec[styles[a]], vec[styles[b]], sd, stripNames, zoneNames)}");
        }
        // ほとんど同じ絵になる組（LED の値の差の平均が 1% 未満）
        foreach (var (label, rs) in new[] { ("24-LED strip", strip), ("1-LED zone", zone) })
        {
            var same = Pairs(m).Select(p => (p.a, p.b, d: rs.Average(r => MeanAbsDiff(r.Frames[styles[p.a]], r.Frames[styles[p.b]]))))
                .Where(p => p.d < 0.01).Select(p => $"{styles[p.a]}={styles[p.b]} ({p.d:F4})").ToList();
            T.Info($"{label}: near-identical pairs (mean |LED diff| < 1%): {(same.Count > 0 ? string.Join("  ", same) : "none")}");
        }
        var zoneD = new double[m, m];
        for (int a = 0; a < m; a++)
            for (int b = a; b < m; b++)
                zoneD[a, b] = zoneD[b, a] = zone.Select(r => { var p = FrameSim(r, r, styles[a], styles[b]); return 1 - 0.5 * (p[3] + p[4]); }).Average();
        PrintMatrix("1-LED zone only: frame distance (1 - mean of [brightness corr, color agreement])", styles, zoneD, 100);
        T.Info("nearest neighbour of each style (combined):");
        for (int a = 0; a < m; a++)
        {
            int best = Enumerable.Range(0, m).Where(b => b != a).OrderBy(b => combD[a, b]).First();
            T.Info($"  {styles[a],-9} -> {styles[best],-9} {combD[a, best]:F2} (median to others {Median(Enumerable.Range(0, m).Where(b => b != a).Select(b => combD[a, b])):F2})");
        }

        // 遅いデバイス（メモリ）: 光り方が置き換わる。コマの距離（テープと同じ式・ゾーンの代わりにテープの値を使う）
        var slowD = new double[m, m];
        for (int a = 0; a < m; a++)
            for (int b = a; b < m; b++)
                slowD[a, b] = slowD[b, a] = dram.Select(r => 1 - FrameSim(r, r, styles[a], styles[b]).Average()).Average();
        PrintMatrix("slow device (DRAM, 8 LEDs, 8 fps): frame distance", styles, slowD, 100);
        var groups = new List<List<string>>();
        for (int a = 0; a < m; a++)
        {
            var g = groups.FirstOrDefault(gr => slowD[a, Array.IndexOf(styles, gr[0])] < 0.02);
            if (g is null) groups.Add(new List<string> { styles[a] }); else g.Add(styles[a]);
        }
        T.Info("slow device: styles that look identical (frame distance < 0.02): " +
               string.Join("  ", groups.Select(g => "{" + string.Join(",", g) + "}")));
        T.Info($"total {sw.Elapsed.TotalSeconds:F1} s");
    }

    private static IEnumerable<(int a, int b)> Pairs(int m)
    {
        for (int a = 0; a < m; a++)
            for (int b = a + 1; b < m; b++)
                yield return (a, b);
    }

    private static string Fmt(double v) => double.IsNaN(v) ? "nan" : Math.Abs(v) >= 100 ? v.ToString("F0", CultureInfo.InvariantCulture) : v.ToString("F2", CultureInfo.InvariantCulture);

    private static void PrintMatrix(string title, string[] styles, double[,] d, double scale)
    {
        T.Info(title + $" x{scale:F0}:");
        var sb = new StringBuilder();
        sb.Append("       ");
        foreach (var x in styles) sb.Append(Code[x].PadLeft(5));
        T.Info(sb.ToString());
        for (int a = 0; a < styles.Length; a++)
        {
            sb.Clear();
            sb.Append("  ").Append(Code[styles[a]]).Append(' ');
            for (int b = 0; b < styles.Length; b++) sb.Append((a == b ? "-" : Math.Round(d[a, b] * scale).ToString("F0", CultureInfo.InvariantCulture)).PadLeft(5));
            T.Info(sb.ToString());
        }
    }

    /// <summary>2 つの光り方で、標準化した差がいちばん小さい特徴と大きい特徴</summary>
    private static string Why(double[] x, double[] y, double[] sd, string[] sn, string[] zn)
    {
        var names = sn.Select(n => "s." + n).Concat(zn.Select(n => "z." + n)).ToArray();
        var diffs = Enumerable.Range(0, x.Length).Where(k => sd[k] > 1e-9).Select(k => (k, z: Math.Abs(x[k] - y[k]) / sd[k])).ToList();
        string same = string.Join(",", diffs.OrderBy(d => d.z).Take(5).Select(d => names[d.k]));
        string diff = string.Join(",", diffs.OrderByDescending(d => d.z).Take(3).Select(d => $"{names[d.k]}({Fmt(x[d.k])} vs {Fmt(y[d.k])})"));
        return $"alike in {same}; differ most in {diff}";
    }

    private static double[] Avg(List<double[]> xs)
    {
        var r = new double[xs[0].Length];
        for (int k = 0; k < r.Length; k++)
        {
            var v = xs.Select(x => x[k]).Where(d => !double.IsNaN(d)).ToList();
            r[k] = v.Count > 0 ? v.Average() : 0;
        }
        return r;
    }

    private static double Median(IEnumerable<double> xs)
    {
        var a = xs.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
        return a.Length == 0 ? double.NaN : a.Length % 2 == 1 ? a[a.Length / 2] : 0.5 * (a[a.Length / 2 - 1] + a[a.Length / 2]);
    }

    // ===== 合成した曲（時刻は曲の頭からの秒）

    /// <summary>四つ打ち 128 BPM: 毎拍キック・2・4 拍目にクラップ・裏拍のオープンハイハット・16 分の細かいハイハット・裏拍のベース・パッド。24〜30 秒はキックの抜けるブレイク</summary>
    private static Song FourOnFloor(double secs)
    {
        double bpm = 128, p = 60 / bpm;
        var g = new SignalGen(48000, secs, seed: 128);
        var beats = new List<double>();
        int k = 0;
        for (double t = 0.5; t < secs - 0.5; t += p, k++)
        {
            beats.Add(t);
            bool brk = t >= 24 && t < 30;
            if (!brk) g.Kick(t, 0.7);
            if (k % 2 == 1) g.Snare(t, brk ? 0.12 : 0.22);
            g.Hat(t + p / 2, 0.10);
            if (!brk) { g.Hat(t + p / 4, 0.03); g.Hat(t + 3 * p / 4, 0.03); }
        }
        double[] bass = { 55, 55, 65.4, 49 };
        g.BassLine(0.5 + p / 2, 24, 1 / p, bass, 0.18);
        double after = beats.First(t => t >= 30);
        g.BassLine(after + p / 2, secs - 0.5, 1 / p, bass, 0.18);
        g.PadChord(0.5, secs - 0.5, new[] { 220.0, 261.6, 329.6 }, 0.04);
        return new Song("four128", g.Buf, g.Fs, beats, g.KickTimes.ToList(), g.HatTimes.ToList(), g.SnareTimes.ToList());
    }

    /// <summary>
    /// ロック 120 BPM: 0〜12 秒は静かな A メロ（キック 1・3 拍目・スネア 2・4 拍目・8 分のハイハット・小節ごとの和音）、
    /// そのあとはサビ（3 拍目の裏にもキック・毎拍の和音・8 分のベース）。4 小節目ごとに 3・4 拍目が 16 分のスネアのフィル、次の小節の頭にクラッシュ
    /// </summary>
    private static Song RockFills(double secs)
    {
        double bpm = 120, p = 60 / bpm;
        var g = new SignalGen(48000, secs, seed: 120);
        var beats = new List<double>();
        double[][] chords = { new[] { 82.4, 123.5, 164.8 }, new[] { 110.0, 164.8, 220 }, new[] { 146.8, 220, 293.7 }, new[] { 110.0, 164.8, 220 } };
        int k = 0;
        bool crashNext = false;
        for (double t = 0.5; t < secs - 0.5; t += p, k++)
        {
            beats.Add(t);
            int bar = k / 4, beat = k % 4;
            bool chorus = t >= 12;
            bool fill = chorus && bar % 4 == 3 && beat >= 2;
            var chord = chords[bar % chords.Length];
            if (beat == 0 && crashNext) { g.White(-20, t, t + 0.35); g.Kick(t, 0.8); crashNext = false; }
            if (fill)
            {
                for (int j = 0; j < 4; j++) g.Snare(t + j * p / 4, 0.18 + 0.07 * ((beat - 2) * 4 + j) / 2);
                if (beat == 3) crashNext = true;
                continue;
            }
            if (beat is 0 or 2) g.Kick(t, chorus ? 0.75 : 0.5);
            if (chorus && beat == 2) g.Kick(t + p / 2, 0.5);
            if (beat is 1 or 3) g.Snare(t, chorus ? 0.4 : 0.25);
            g.Hat(t, chorus ? 0.08 : 0.05);
            g.Hat(t + p / 2, chorus ? 0.06 : 0.04);
            if (chorus || beat == 0) g.Strum(t, chord, chorus ? 0.05 : 0.04);
        }
        g.BassLine(12, secs - 0.5, 2 / p, new[] { 41.2, 41.2, 55, 55, 73.4, 73.4, 55, 55 }, 0.2);
        return new Song("rock120", g.Buf, g.Fs, beats, g.KickTimes.ToList(), g.HatTimes.ToList(), g.SnareTimes.ToList());
    }

    /// <summary>
    /// アンビエント 70 BPM: パッドの和音（15 秒ごとに変わる）と、ときどき鳴るベルの音。15〜30 秒だけ 2 拍ごとの小さなキック、
    /// 22〜38 秒だけ小節の 3 拍目に小さなハイハット（音のほとんどは拍がない）
    /// </summary>
    private static Song Ambient(double secs)
    {
        double bpm = 70, p = 60 / bpm;
        var g = new SignalGen(48000, secs, seed: 70);
        var beats = new List<double>();
        int k = 0;
        for (double t = 0.5; t < secs - 0.5; t += p, k++)
        {
            beats.Add(t);
            if (t >= 15 && t < 30 && k % 2 == 0) g.Kick(t, 0.3, click: false);
            if (t >= 22 && t < 38 && k % 4 == 2) g.Hat(t, 0.04);
        }
        g.PadChord(0.5, Math.Min(secs - 0.5, 15.5), new[] { 220.0, 261.6, 329.6 }, 0.06, seed: 1);
        if (secs > 15) g.PadChord(15, Math.Min(secs - 0.5, 30.5), new[] { 174.6, 220, 261.6 }, 0.06, seed: 2);
        if (secs > 30) g.PadChord(30, secs - 0.5, new[] { 261.6, 329.6, 392 }, 0.06, seed: 3);
        var rng = new Random(70);
        double[] bells = { 880, 987.8, 1174.7, 1318.5 };
        for (double t = 2; t < secs - 2; t += 1.5 + 2.5 * rng.NextDouble())
            g.Strum(t, new[] { bells[rng.Next(bells.Length)] }, 0.05);
        return new Song("ambient70", g.Buf, g.Fs, beats, g.KickTimes.ToList(), g.HatTimes.ToList(), g.SnareTimes.ToList());
    }

    /// <summary>歌の多い曲 96 BPM: 4 小節歌って 1 小節休むメロディー・軽いドラム（キック 1・3 拍目、スネア 2・4 拍目、8 分のハイハット）・ベース・小さなパッド</summary>
    private static Song Vocal(double secs)
    {
        double bpm = 96, p = 60 / bpm;
        var g = new SignalGen(48000, secs, seed: 96);
        var beats = new List<double>();
        int k = 0;
        for (double t = 0.5; t < secs - 0.5; t += p, k++)
        {
            beats.Add(t);
            int beat = k % 4;
            if (beat is 0 or 2) g.Kick(t, 0.45);
            if (beat is 1 or 3) g.Snare(t, 0.22);
            g.Hat(t, 0.035);
            g.Hat(t + p / 2, 0.025);
        }
        double[][] phrases =
        {
            new[] { 220.0, 246.9, 261.6, 293.7, 329.6, 293.7, 261.6, 246.9 },
            new[] { 261.6, 261.6, 293.7, 329.6, 392.0, 349.2, 329.6, 293.7 },
        };
        double bar = 4 * p;
        int ph = 0;
        for (double t = 0.5 + bar; t + 4 * bar < secs - 0.5; t += 5 * bar, ph++)
            g.VocalTone(t, t + 4 * bar, phrases[ph % 2], 2 * p, 0.2);
        g.BassLine(0.5, secs - 0.5, 2 / p, new[] { 55.0, 55, 65.4, 65.4, 73.4, 73.4, 49, 49 }, 0.15);
        g.PadChord(0.5, secs - 0.5, new[] { 220.0, 277.2, 329.6 }, 0.02);
        return new Song("vocal96", g.Buf, g.Fs, beats, g.KickTimes.ToList(), g.HatTimes.ToList(), g.SnareTimes.ToList());
    }

    // ===== 描く

    /// <summary>曲を本物の分析に流し、テープ・ゾーン・遅いデバイスで全部の光り方を描く（ModeSyncBench.RunSong と同じ順序）</summary>
    private static Render[] RunSong(Song song, string[] styles)
    {
        var mono = song.Buf;
        int fs = song.Fs;
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        var engine = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(cfg) };
        var devs = new[]
        {
            (Id: "strip24", D: new RgbDevice(0, "Strip", 24, 4, null, "HID: x"), Step: 0.0),
            (Id: "zone1", D: new RgbDevice(1, "Zone", 1, 4, null, "HID: y"), Step: 0.0),
            (Id: "dram8", D: new RgbDevice(2, "DRAM", 8, RgbDevice.TypeDram, null, "I2C: z"), Step: 1 / 8.0),
        };
        var lanes = new List<Lane>();
        foreach (var (id, d, step) in devs)
        {
            foreach (var st in styles)
            {
                var music = ResolvedMusic.Default with { Effect = st };
                lanes.Add(new Lane
                {
                    Style = st, Dev = id, R = new DeviceRenderer(d), Out = new Rgb[d.LedCount], Step = step,
                    In = new RenderInput { Cfg = cfg, Music = music, Device = new RgbDeviceSettings { Name = d.Name } },
                });
            }
        }

        const double t0 = 1000.0;
        int chunk = fs / 100;
        double nextFrame = t0;
        var rms = new List<double>();
        for (int pos = 0; pos < mono.Length; pos += chunk)
        {
            int n = Math.Min(chunk, mono.Length - pos);
            engine.Push(mono.AsSpan(pos, n), t0 + pos / (double)fs);
            double tAudio = t0 + (pos + n) / (double)fs;
            while (nextFrame <= tAudio)
            {
                double t = nextFrame;
                int a = (int)Math.Max(0, (t - t0 - 1 / Fps) * fs), b = (int)Math.Min(mono.Length, (t - t0) * fs);
                double s2 = 0;
                for (int i = a; i < b; i++) s2 += mono[i] * (double)mono[i];
                rms.Add(20 * Math.Log10(Math.Max(1e-5, b > a ? Math.Sqrt(s2 / (b - a)) : 0)));
                foreach (var L in lanes) Draw(L, engine, t);
                nextFrame += 1 / Fps;
            }
        }
        var rmsArr = rms.ToArray();
        return devs.Select(d => new Render(song, d.Id, d.D.LedCount,
            lanes.Where(L => L.Dev == d.Id).ToDictionary(L => L.Style, L => L.Frames), rmsArr)).ToArray();
    }

    private static void Draw(Lane L, AudioEngine e, double t)
    {
        if (t + 1e-9 >= L.NextDraw)
        {
            var input = L.In;
            input.T = t;
            input.TEval = t;
            input.TPrevEval = double.IsNaN(L.LastDraw) ? t : L.LastDraw;
            input.Dt = double.IsNaN(L.LastDraw) ? 1 / Fps : t - L.LastDraw;
            input.Beat = e.Beat;
            input.Scene = e.Scene;
            input.Features = e.Features;
            e.Features.Sample(t, input.Frame);
            input.Events.Clear();
            e.Events.Read(ref L.Cursor, t, input.Events);
            L.R.Render(input, L.Out);
            L.LastDraw = t;
            L.NextDraw = L.Step > 0 ? (double.IsNegativeInfinity(L.NextDraw) ? t : L.NextDraw) + L.Step : t;
        }
        // 遅いデバイスは、次に描くまで前の色のまま
        var f = new byte[L.Out.Length * 3];
        for (int i = 0; i < L.Out.Length; i++) { f[3 * i] = L.Out[i].R; f[3 * i + 1] = L.Out[i].G; f[3 * i + 2] = L.Out[i].B; }
        L.Frames.Add(f);
    }

    // ===== 特徴

    private static double V(byte[] f, int i) => Math.Max(f[3 * i], Math.Max(f[3 * i + 1], f[3 * i + 2])) / 255.0;

    /// <summary>色相（度）・彩度。暗い LED・白に近い LED は色相なし（false）</summary>
    private static bool Hue(byte[] f, int i, out double h, out double sat)
    {
        double r = f[3 * i], g = f[3 * i + 1], b = f[3 * i + 2];
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), c = mx - mn;
        sat = mx > 0 ? c / mx : 0;
        h = 0;
        if (mx < 25 || sat < 0.25) return false;
        if (mx == r) h = 60 * (((g - b) / c % 6 + 6) % 6);
        else if (mx == g) h = 60 * ((b - r) / c + 2);
        else h = 60 * ((r - g) / c + 4);
        return true;
    }

    /// <summary>出来事の時刻の列を、コマごとの信号（その前後 1 コマにも半分）にする</summary>
    private static double[] Train(List<double> times, int n)
    {
        var x = new double[n];
        foreach (double t in times)
        {
            int k = (int)Math.Round(t * Fps);
            for (int d = -1; d <= 1; d++)
                if (k + d >= 0 && k + d < n) x[k + d] += d == 0 ? 1 : 0.5;
        }
        return x;
    }

    /// <summary>明るさの増え方 u と、出来事の信号の相関（光が 0〜150 ミリ秒遅れるまでで、いちばん高いもの）</summary>
    private static double EventCorr(double[] u, List<double> times, int f0)
    {
        if (times.Count < 3) return double.NaN;
        var x = Train(times, u.Length);
        double best = double.NegativeInfinity;
        for (int lag = 0; lag <= 9; lag++)
            best = Math.Max(best, Corr(f0, u.Length - lag, i => x[i], i => u[i + lag]));
        return best;
    }

    /// <summary>テープの特徴（名前は TestStyleSimilarity の stripNames）</summary>
    private static double[] StripFeatures(Render r, string style) => Features(r, r.Frames[style], strip: true);

    private static double[] ZoneFeatures(Render r, string style) => Features(r, r.Frames[style], strip: false);

    private static double[] Features(Render r, List<byte[]> fr, bool strip)
    {
        int n = r.N, nT = fr.Count, f0 = (int)(SkipSec * Fps);
        var mean = new double[nT];
        var up = new double[nT];
        var cen = new double[nT];
        var hue = new double[nT];
        var hasHue = new bool[nT];
        double spStd = 0, flat = 0, sat = 0, satW = 0, spread = 0;
        int spreadN = 0, ledFlashes = 0;
        var lastLedFlash = new int[n];
        Array.Fill(lastLedFlash, -1000);
        for (int t = 0; t < nT; t++)
        {
            var f = fr[t];
            double sum = 0, sum2 = 0, ci = 0, hx = 0, hy = 0, hw = 0;
            bool same = true;
            for (int i = 0; i < n; i++)
            {
                double v = V(f, i);
                sum += v; sum2 += v * v; ci += i * v;
                if (t > 0) up[t] += Math.Max(0, v - V(fr[t - 1], i)) / n;
                if (t >= 3 && v - V(fr[t - 3], i) >= 0.35 && t - lastLedFlash[i] >= 6) { if (t >= f0) ledFlashes++; lastLedFlash[i] = t; }
                if (i > 0 && (Math.Abs(f[3 * i] - f[0]) > 12 || Math.Abs(f[3 * i + 1] - f[1]) > 12 || Math.Abs(f[3 * i + 2] - f[2]) > 12)) same = false;
                bool hasH = Hue(f, i, out double h, out double sa);
                if (v > 0.1) { sat += v * sa; satW += v; }
                if (hasH)
                {
                    double w = v;
                    hx += w * Math.Cos(h * Math.PI / 180); hy += w * Math.Sin(h * Math.PI / 180); hw += w;
                }
            }
            mean[t] = sum / n;
            cen[t] = sum > 0.02 * n && n > 1 ? ci / sum / (n - 1) : double.NaN;
            if (hw > 0)
            {
                hasHue[t] = true;
                hue[t] = Math.Atan2(hy, hx) * 180 / Math.PI;
                if (t >= f0 && n > 1) { spread += 1 - Math.Sqrt(hx * hx + hy * hy) / hw; spreadN++; }
            }
            if (t >= f0)
            {
                spStd += Math.Sqrt(Math.Max(0, sum2 / n - mean[t] * mean[t]));
                if (same) flat++;
            }
        }
        int cnt = Math.Max(1, nT - f0);
        double secs = cnt / Fps;
        double mB = mean.Skip(f0).Average();
        double sB = Math.Sqrt(mean.Skip(f0).Average(x => (x - mB) * (x - mB)));
        // 強い光（テープ全体）: 3 コマで平均の明るさが 0.15 以上増えた山。次は 100 ミリ秒あける
        int flashes = 0, lastF = -1000;
        for (int t = Math.Max(f0, 3); t < nT - 1; t++)
        {
            double rise = mean[t] - mean[t - 3];
            if (rise >= 0.15 && mean[t] - mean[t - 1] > 0 && mean[t + 1] <= mean[t] && t - lastF >= 6) { flashes++; lastF = t; }
        }
        // 重心の動き（1 秒あたり、テープの長さが 1）・流れ（1 コマ前の形をずらすと今のコマに合う割合）
        double motion = 0; int motionN = 0, flow = 0, flowN = 0;
        double hueRate = 0; int hueN = 0;
        for (int t = Math.Max(f0, 1); t < nT; t++)
        {
            if (!double.IsNaN(cen[t]) && !double.IsNaN(cen[t - 1])) { motion += Math.Abs(cen[t] - cen[t - 1]); motionN++; }
            if (hasHue[t] && hasHue[t - 1])
            {
                double d = Math.Abs(hue[t] - hue[t - 1]) % 360;
                hueRate += Math.Min(d, 360 - d); hueN++;
            }
            if (n > 4 && mean[t] > 0.02)
            {
                var a = fr[t]; var b = fr[t - 1];
                double c0 = ShiftCorr(a, b, n, 0);
                double cb = Math.Max(Math.Max(ShiftCorr(a, b, n, 1), ShiftCorr(a, b, n, -1)), Math.Max(ShiftCorr(a, b, n, 2), ShiftCorr(a, b, n, -2)));
                if (cb > c0 + 0.05) flow++;
                flowN++;
            }
        }
        var sm = Smooth(mean, 30);
        var sr = Smooth(r.RmsDb.Take(nT).ToArray(), 30);
        double rLoud = Corr(f0, Math.Min(nT, sr.Length), i => sm[i], i => sr[i]);
        var sec = (Func<List<double>, List<double>>)(xs => xs.Where(x => x >= SkipSec && x < nT / Fps - 0.2).ToList());
        double rBeat = EventCorr(up, sec(r.Song.Beats), f0), rKick = EventCorr(up, sec(r.Song.Kicks), f0),
               rHat = EventCorr(up, sec(r.Song.Hats), f0), rSnare = EventCorr(up, sec(r.Song.Snares), f0);
        double hueDps = hueN > 0 ? hueRate / hueN * Fps : 0;
        double satM = satW > 0 ? sat / satW : 0;
        if (!strip)
            return new[] { mB, sB, flashes / secs, hueDps, satM, rBeat, rKick, rHat, rSnare, rLoud };
        return new[]
        {
            mB, sB, flashes / secs, ledFlashes / secs / n, flat / cnt, spStd / cnt, motionN > 0 ? motion / motionN * Fps : 0,
            flowN > 0 ? flow / (double)flowN : 0, hueDps, spreadN > 0 ? spread / spreadN : 0, satM, rBeat, rKick, rHat, rSnare, rLoud,
        };
    }

    /// <summary>今のコマ a と、1 コマ前 b を k 個ずらしたものの明るさの相関（重なる LED だけ）</summary>
    private static double ShiftCorr(byte[] a, byte[] b, int n, int k)
    {
        int from = Math.Max(0, k), to = Math.Min(n, n + k);
        return Corr(from, to, i => V(a, i), i => V(b, i - k));
    }

    // ===== コマどうしの近さ

    /// <summary>
    /// 2 つの光り方の近さ（1 = 同じ）: [テープの平均の明るさの相関（±100 ミリ秒ずらして最大）, テープの LED × 時間の明るさの相関（左右反対も見て最大）,
    /// テープの色の一致, ゾーンの明るさの相関, ゾーンの色の一致]
    /// </summary>
    private static double[] FrameSim(Render s, Render z, string x, string y)
    {
        int f0 = (int)(SkipSec * Fps);
        var sa = s.Frames[x]; var sb = s.Frames[y];
        var za = z.Frames[x]; var zb = z.Frames[y];
        return new[]
        {
            LagCorr(MeanSeries(sa, s.N), MeanSeries(sb, s.N), f0), SpaceTimeCorr(sa, sb, s.N, f0), ColorAgree(sa, sb, s.N, f0),
            LagCorr(MeanSeries(za, z.N), MeanSeries(zb, z.N), f0), ColorAgree(za, zb, z.N, f0),
        };
    }

    private static double[] MeanSeries(List<byte[]> fr, int n) => fr.Select(f => { double s = 0; for (int i = 0; i < n; i++) s += V(f, i); return s / n; }).ToArray();

    private static double LagCorr(double[] a, double[] b, int f0)
    {
        double best = double.NegativeInfinity;
        for (int lag = -6; lag <= 6; lag++)
            best = Math.Max(best, Corr(f0 + 6, Math.Min(a.Length, b.Length) - 6, i => a[i], i => b[i + lag]));
        // 2 つとも明るさが変わらない（相関が決まらない）ときは、平均の明るさが近いかで決める
        if (Var(a, f0) < 1e-6 && Var(b, f0) < 1e-6) best = 1 - Math.Min(1, Math.Abs(a.Skip(f0).Average() - b.Skip(f0).Average()) * 4);
        return best;
    }

    private static double Var(double[] a, int f0)
    {
        double m = a.Skip(f0).Average();
        return a.Skip(f0).Average(x => (x - m) * (x - m));
    }

    private static double SpaceTimeCorr(List<byte[]> a, List<byte[]> b, int n, int f0)
    {
        int nT = Math.Min(a.Count, b.Count);
        double Run(bool mirror)
        {
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0; long c = 0;
            for (int t = f0; t < nT; t++)
                for (int i = 0; i < n; i++)
                {
                    double p = V(a[t], i), q = V(b[t], mirror ? n - 1 - i : i);
                    sx += p; sy += q; sxx += p * p; syy += q * q; sxy += p * q; c++;
                }
            double vx = sxx - sx * sx / c, vy = syy - sy * sy / c;
            return vx <= 1e-9 || vy <= 1e-9 ? 0 : (sxy - sx * sy / c) / Math.Sqrt(vx * vy);
        }
        return Math.Max(Run(false), Run(true));
    }

    /// <summary>どちらかが光っている LED（明るさ 8% 以上）での、色（RGB の向き）の一致の平均。片方だけ光っていれば 0</summary>
    private static double ColorAgree(List<byte[]> a, List<byte[]> b, int n, int f0)
    {
        int nT = Math.Min(a.Count, b.Count);
        double acc = 0; long c = 0;
        for (int t = f0; t < nT; t++)
            for (int i = 0; i < n; i++)
            {
                bool la = V(a[t], i) >= 0.08, lb = V(b[t], i) >= 0.08;
                if (!la && !lb) continue;
                c++;
                if (!(la && lb)) continue;
                double ar = a[t][3 * i], ag = a[t][3 * i + 1], ab = a[t][3 * i + 2], br = b[t][3 * i], bg = b[t][3 * i + 1], bb = b[t][3 * i + 2];
                acc += (ar * br + ag * bg + ab * bb) / Math.Sqrt((ar * ar + ag * ag + ab * ab) * (br * br + bg * bg + bb * bb));
            }
        return c > 0 ? acc / c : 1;
    }

    private static double MeanAbsDiff(List<byte[]> a, List<byte[]> b)
    {
        double acc = 0; long c = 0;
        for (int t = 0; t < Math.Min(a.Count, b.Count); t++)
            for (int k = 0; k < a[t].Length; k++) { acc += Math.Abs(a[t][k] - b[t][k]) / 255.0; c++; }
        return c > 0 ? acc / c : 0;
    }

    private static double[] Smooth(double[] x, int w)
    {
        var y = new double[x.Length];
        double acc = 0;
        for (int i = 0; i < x.Length; i++)
        {
            acc += x[i];
            if (i >= w) acc -= x[i - w];
            y[i] = acc / Math.Min(i + 1, w);
        }
        return y;
    }

    private static double Corr(int from, int to, Func<int, double> x, Func<int, double> y)
    {
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0; int c = 0;
        for (int i = from; i < to; i++)
        {
            double a = x(i), b = y(i);
            sx += a; sy += b; sxx += a * a; syy += b * b; sxy += a * b; c++;
        }
        if (c < 2) return 0;
        double vx = sxx - sx * sx / c, vy = syy - sy * sy / c;
        return vx <= 1e-12 || vy <= 1e-12 ? 0 : (sxy - sx * sy / c) / Math.Sqrt(vx * vy);
    }
}

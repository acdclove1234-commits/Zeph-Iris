// 音楽連動の光り方ごとに、録音した曲で「音楽に合って光っているか」を数値にする（MODE_WAV にフォルダーを渡したときだけ動く）
//   ・本物の分析（AudioEngine）に曲を 10 ミリ秒ずつ流し、そのつど 60 コマ／秒で全部の光り方を描く（アプリと同じ順序・同じ情報）
//   ・物差しの拍（BeatReference。曲全体を見て後から決める）と比べる
// 出す値:
//   hit   … 光の立ち上がりのうち、拍（±70 ms）に乗っている割合（拍と関係なく光る、の逆）
//   beat  … 物差しの拍のうち、光が立ち上がった割合（反応がまばら、の逆）
//   off   … 拍に乗った立ち上がりの、拍からのずれの中央値（ms。+ なら光が遅れている）
//   resp  … 明るさの増え方と、音の立ち上がりの強さの相関（いちばん合う時間のずれで）。音に反応しているか
//   lag   … resp がいちばん高くなる時間のずれ（ms。+ なら光が遅れている）
//   loud  … 明るさ（0.5 秒でならす）と音の大きさの相関。静かなところで暗く、盛り上がりで明るくなっているか
//   range … 明るさの p95 − p5（メリハリ）
//   jit   … 0.07 秒以内に明るさの向きが戻った回数（LED 1 個・1 秒あたり。ちらつき）
using ProFanController;

namespace LogicTests;

public static class ModeSyncBenchTests
{
    public sealed record ModeScore(string Mode, double Hit, double Beat, double OffMs, double Resp, double LagMs, double Loud, double Range, double Jit, int Flashes)
    {
        /// <summary>裏拍に乗った立ち上がりの割合</summary>
        public double Half { get; init; }
        /// <summary>拍にも裏拍にも乗らない光が、明るくなった量の合計に占める割合（拍と関係なく光る感じ）</summary>
        public double RandAmp { get; init; }
        /// <summary>拍の光の立ち上がり ÷ 拍の間の光の立ち上がり（平均。拍が目立つか）</summary>
        public double Accent { get; init; } = double.NaN;
    }

    private const double Fps = 60;
    private const double HitTol = 0.07;
    /// <summary>その曲で分析が出した出来事（調べる用。時刻は曲の始まりから）</summary>
    public static readonly List<(double T, AudioEventKind Kind, float Strength)> AllEvents = new();

    /// <summary>
    /// 調べる用: 環境変数 SYNTH_WAV にフォルダー。合成した曲（音の多い曲・テンポ違い）を 32 bit の WAV で書く
    /// （録音を消したあとでも、光り方の比べに使えるように）
    /// </summary>
    public static void TestWriteSynthSongs()
    {
        string? dir = Environment.GetEnvironmentVariable("SYNTH_WAV");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        foreach (double bpm in new[] { 92.0, 118, 128, 140 })
        {
            var g = new SignalGen(48000, 60, seed: (int)bpm);
            g.DenseMix(bpm, 0.5, 59);
            string path = Path.Combine(dir, $"synth{bpm:000}.wav");
            using var bw = new BinaryWriter(File.Create(path));
            int n = g.Buf.Length;
            bw.Write("RIFF"u8.ToArray()); bw.Write(36 + n * 4); bw.Write("WAVE"u8.ToArray());
            bw.Write("fmt "u8.ToArray()); bw.Write(16); bw.Write((short)3); bw.Write((short)1); bw.Write(48000); bw.Write(48000 * 4); bw.Write((short)4); bw.Write((short)32);
            bw.Write("data"u8.ToArray()); bw.Write(n * 4);
            foreach (var x in g.Buf) bw.Write(x);
            T.Info($"wrote {path}");
        }
    }

    public static void TestModeSyncBench()
    {
        string? dir = Environment.GetEnvironmentVariable("MODE_WAV");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        if (double.TryParse(Environment.GetEnvironmentVariable("MODE_HOLD"), out double holdSec)) AttackHold.HoldSec = holdSec;
        if (Environment.GetEnvironmentVariable("MODE_SECTION") == "0") SectionMeter.Enabled = false;
        var savedTune = BeatTuneSweepTests.Snapshot();
        BeatTuneSweepTests.Apply(Environment.GetEnvironmentVariable("BEAT_TUNE") ?? "");
        try { Bench(dir); }
        finally { BeatTuneSweepTests.Restore(savedTune); AttackHold.HoldSec = AttackHold.DefaultHoldSec; SectionMeter.Enabled = true; BeatHueEffect.Tune.OffBeatDuck = 0.25f; }
    }

    private static void Bench(string dir)
    {
        if (float.TryParse(Environment.GetEnvironmentVariable("MODE_DUCK"), out float duck)) { BeatHueEffect.Tune.OffBeatDuck = duck; }
        string[] modes = (Environment.GetEnvironmentVariable("MODE_LIST") ?? string.Join(",", MusicStyles.All)).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var all = new Dictionary<string, List<ModeScore>>();
        foreach (var f in Directory.GetFiles(dir, "*.wav").OrderBy(x => x))
        {
            var scores = RunSong(f, modes, out double bpm, out double secs);
            T.Info($"{Path.GetFileNameWithoutExtension(f)} {secs,4:F0}s ref {bpm,5:F1} BPM");
            foreach (var s in scores)
            {
                T.Info("   " + Format(s));
                (all.TryGetValue(s.Mode, out var l) ? l : all[s.Mode] = new()).Add(s);
            }
        }
        T.Info("average over songs:");
        foreach (var m in modes.Where(all.ContainsKey))
        {
            var l = all[m];
            T.Info("   " + Format(new ModeScore(m, l.Average(x => x.Hit), l.Average(x => x.Beat), Median(l.Select(x => x.OffMs)),
                l.Average(x => x.Resp), Median(l.Select(x => x.LagMs)), l.Average(x => x.Loud), l.Average(x => x.Range), l.Average(x => x.Jit), (int)l.Average(x => x.Flashes)) { Half = l.Average(x => x.Half), RandAmp = l.Average(x => x.RandAmp), Accent = Median(l.Select(x => x.Accent)) }));
        }
    }

    public static string Format(ModeScore s) =>
        $"{s.Mode,-9} hit {s.Hit * 100,3:F0}% +sub {s.Half * 100,3:F0}% stray {s.RandAmp * 100,3:F0}% accent {s.Accent,4:F2}  beat {s.Beat * 100,3:F0}%  off {s.OffMs,4:F0} ms  resp {s.Resp,5:F2} lag {s.LagMs,4:F0} ms  loud {s.Loud,5:F2}  range {s.Range,4:F2}  jit {s.Jit,5:F2}  flashes {s.Flashes,4}";

    private static double Median(IEnumerable<double> xs)
    {
        var a = xs.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
        return a.Length == 0 ? double.NaN : a[a.Length / 2];
    }

    private sealed class Lane
    {
        public required string Mode;
        public required DeviceRenderer R;
        public required RenderInput In;
        public required Rgb[] Out;
        public long Cursor;
        public readonly List<double> B = new();
        /// <summary>LED ごとに明るくなった分の平均（動く光でも、新しく光ったところを数えられる）</summary>
        public readonly List<double> U = new();
        public int[] Prev = Array.Empty<int>(), Dir = Array.Empty<int>(), Flip = Array.Empty<int>();
        public long Jitter;
    }

    public static List<ModeScore> RunSong(string path, string[] modes, out double refBpm, out double secondsOut)
    {
        var mono = MusicWavTests.ReadWavMono(path, out int fs);
        double seconds = mono.Length / (double)fs;
        secondsOut = seconds;

        // 1) 物差しの拍（曲全体の立ち上がりの強さから、後から決める）
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        var refEngine = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(cfg) };
        var hops = new List<(double T, double Odf)>();
        refEngine.Onsets.DiagHop = (t, odf, bn) => hops.Add((t, (1.0 * bn[0] + 0.8 * bn[1] + 0.35 * bn[2] + 0.15 * bn[3]) / 2.3));
        var refRun = new EngineRun(refEngine).Feed(mono);
        var refBeats = BeatReference.Track(hops, out refBpm).Select(t => t - refRun.T0).ToList();
        var odf = hops.Select(h => (T: h.T - refRun.T0, h.Odf)).ToList();

        // 2) 本物の分析に流しながら、全部の光り方を描く
        var dev = new RgbDevice(0, "Strip", 24, 4, null, "HID: x");
        var engine = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(cfg) };
        var lanes = modes.Select(m =>
        {
            var music = ResolvedMusic.Default with { Effect = m };
            if (m == MusicStyles.Strobe) music = music with { StrobeAllowed = true, ReduceFlashing = false };
            var input = new RenderInput { Cfg = cfg, Music = music, Device = new RgbDeviceSettings { Name = dev.Name } };
            return new Lane { Mode = m, R = new DeviceRenderer(dev), In = input, Out = new Rgb[dev.LedCount],
                Prev = new int[dev.LedCount], Dir = new int[dev.LedCount], Flip = new int[dev.LedCount] };
        }).ToList();

        const double t0 = 1000.0; // EngineRun と同じ始まり
        int chunk = fs / 100;
        double nextFrame = t0, lastFrame = double.NaN;
        long evCursor = 0;
        var evBuf = new List<AudioEvent>();
        AllEvents.Clear();
        var rms = new List<double>();
        for (int pos = 0; pos < mono.Length; pos += chunk)
        {
            int n = Math.Min(chunk, mono.Length - pos);
            engine.Push(mono.AsSpan(pos, n), t0 + pos / (double)fs);
            double tAudio = t0 + (pos + n) / (double)fs;
            while (nextFrame <= tAudio)
            {
                double t = nextFrame;
                // 音の大きさ（このコマまでの 1/60 秒）
                int a = (int)Math.Max(0, (t - t0 - 1 / Fps) * fs), b = (int)Math.Min(mono.Length, (t - t0) * fs);
                double s2 = 0;
                for (int i = a; i < b; i++) s2 += mono[i] * (double)mono[i];
                rms.Add(b > a ? Math.Sqrt(s2 / (b - a)) : 0);
                foreach (var L in lanes) DrawFrame(L, engine, t, lastFrame);
                evBuf.Clear();
                engine.Events.Read(ref evCursor, t, evBuf);
                foreach (var ev in evBuf) AllEvents.Add((ev.T - t0, ev.Kind, ev.Strength));
                lastFrame = t;
                nextFrame += 1 / Fps;
            }
        }
        return lanes.Select(L => Score(L, refBeats, odf, rms, seconds)).ToList();
    }

    private static void DrawFrame(Lane L, AudioEngine e, double t, double lastT)
    {
        var input = L.In;
        input.T = t;
        input.TEval = t;
        input.TPrevEval = double.IsNaN(lastT) ? t : lastT;
        input.Dt = double.IsNaN(lastT) ? 1 / Fps : t - lastT;
        input.Beat = e.Beat;
        input.Features = e.Features;
        e.Features.Sample(t, input.Frame);
        input.Events.Clear();
        e.Events.Read(ref L.Cursor, t, input.Events);
        L.R.Render(input, L.Out);
        double sum = 0, up = 0;
        int f = L.B.Count;
        for (int i = 0; i < L.Out.Length; i++)
        {
            int v = Math.Max(L.Out[i].R, Math.Max(L.Out[i].G, L.Out[i].B));
            sum += v / 255.0;
            if (f > 0)
            {
                up += Math.Max(0, v - L.Prev[i]) / 255.0;
                int d = Math.Abs(v - L.Prev[i]) >= 2 ? Math.Sign(v - L.Prev[i]) : 0;
                if (d != 0)
                {
                    if (L.Dir[i] != 0 && d != L.Dir[i]) { if (f - L.Flip[i] <= 4) L.Jitter++; L.Flip[i] = f; }
                    L.Dir[i] = d;
                }
            }
            L.Prev[i] = v;
        }
        L.B.Add(sum / L.Out.Length);
        L.U.Add(up / L.Out.Length);
    }

    private static ModeScore Score(Lane L, List<double> beats, List<(double T, double Odf)> odf, List<double> rms, double seconds)
    {
        var b = L.B.ToArray();
        int n = b.Length;
        double skip = 8; // 始まりの 8 秒（テンポをつかむまで）は数えない
        int f0 = (int)(skip * Fps);
        var sorted = b.Skip(f0).OrderBy(x => x).ToArray();
        double P(double q) => sorted.Length == 0 ? 0 : sorted[(int)Math.Clamp(q * (sorted.Length - 1), 0, sorted.Length - 1)];
        double range = P(0.95) - P(0.05);

        // 光の立ち上がり: LED ごとに明るくなった分（u）の山。動く光（波紋・テンポ追従）でも、新しく光ったところで数える。
        // 山 = 前後 3 コマで最大、かつ u の上位 15%・中央値の 3 倍以上・明るさ 3% 以上。次は 100 ms あける
        var u = L.U.ToArray();
        // 調べる用: MODE_TRACE=光り方:秒 で、その光り方の明るさを 3 秒ぶん出す（拍の位置に * を付ける）
        if (Environment.GetEnvironmentVariable("MODE_TRACE") is { } tr && tr.Split(':') is [var tm, var ts] && tm == L.Mode && double.TryParse(ts, out double tStart))
        {
            int a = (int)(tStart * Fps);
            var sb2 = new System.Text.StringBuilder();
            for (int i = a; i < Math.Min(n, a + 180); i++)
            {
                double t = i / Fps;
                bool onBeat = beats.Any(x => Math.Abs(x - t) < 0.5 / Fps);
                sb2.Append($"{t,7:F3} {(onBeat ? '*' : ' ')} b {b[i]:F3} u {u[i]:F3} {new string('#', (int)(b[i] * 60))}\n");
            }
            T.Info(sb2.ToString());
        }
        var us = u.Skip(f0).OrderBy(x => x).ToArray();
        double UP(double q) => us.Length == 0 ? 0 : us[(int)Math.Clamp(q * (us.Length - 1), 0, us.Length - 1)];
        double thr = Math.Max(0.03, Math.Max(UP(0.85), 3 * UP(0.5)));
        var flashes = new List<double>();
        var amps = new List<double>();
        double lastFlash = double.NegativeInfinity;
        for (int i = Math.Max(f0, 3); i < n - 3; i++)
        {
            if (u[i] < thr) continue;
            bool peak = true;
            for (int k = -3; k <= 3 && peak; k++) if (k != 0 && (u[i + k] > u[i] || (k < 0 && u[i + k] == u[i]))) peak = false;
            if (!peak) continue;
            // 山の前で明るくなり始めたコマを、光った時刻にする（u がしきい値の 1/3 を超えた最初のコマ）
            int s = i;
            while (s > i - 3 && u[s - 1] >= thr / 3) s--;
            double ft = (s - 0.5) / Fps;
            if (ft - lastFlash < 0.1) continue;
            double amp = 0;
            for (int k = Math.Max(0, i - 3); k <= Math.Min(n - 1, i + 2); k++) amp += u[k];
            amps.Add(amp);
            flashes.Add(ft);
            lastFlash = ft;
        }
        var bs = beats.Where(t => t >= skip && t <= seconds - 1).ToList();
        double Nearest(List<double> xs, double t)
        {
            int k = xs.BinarySearch(t);
            if (k < 0) k = ~k;
            double best = double.PositiveInfinity;
            if (k < xs.Count) best = xs[k] - t;
            if (k > 0 && Math.Abs(t - xs[k - 1]) < Math.Abs(best)) best = xs[k - 1] - t;
            return best;
        }
        var offs = flashes.Select(t => -Nearest(bs, t)).Where(d => Math.Abs(d) <= HitTol).ToList(); // + = 光が拍より後
        double hit = flashes.Count > 0 ? offs.Count / (double)flashes.Count : 0;
        // 裏拍（拍と拍のちょうど間・±50 ms）に乗った立ち上がり（ハイハットなど。音楽的には合っている）
        var halves = bs.Zip(bs.Skip(1), (a, c) => new[] { a + 0.25 * (c - a), a + 0.5 * (c - a), a + 0.75 * (c - a) }).SelectMany(x => x).ToList(); // 8 分・16 分の位置
        double half = flashes.Count > 0 ? flashes.Count(t => Math.Abs(Nearest(bs, t)) > HitTol && Math.Abs(Nearest(halves, t)) <= 0.04) / (double)flashes.Count : 0;
        // 拍にも裏拍にも乗らない立ち上がりが、光の立ち上がり全体（明るくなった量の合計）に占める割合
        double totalAmp = amps.Sum(), randAmp = 0;
        for (int q = 0; q < flashes.Count; q++)
            if (Math.Abs(Nearest(bs, flashes[q])) > HitTol && Math.Abs(Nearest(halves, flashes[q])) > 0.04) randAmp += amps[q];
        double randShare = totalAmp > 0 ? randAmp / totalAmp : 0;
        // 拍の光が、拍の間（8 分・16 分）の光より何倍明るく立ち上がるか（拍が目立つか）
        double beatAmpSum = 0, subAmpSum = 0; int beatN = 0, subN = 0;
        for (int q = 0; q < flashes.Count; q++)
        {
            if (Math.Abs(Nearest(bs, flashes[q])) <= HitTol) { beatAmpSum += amps[q]; beatN++; }
            else if (Math.Abs(Nearest(halves, flashes[q])) <= 0.04) { subAmpSum += amps[q]; subN++; }
        }
        double accent = beatN > 0 && subN > 0 && subAmpSum > 0 ? (beatAmpSum / beatN) / (subAmpSum / subN) : double.NaN;
        if (Environment.GetEnvironmentVariable("MODE_STRAY") == L.Mode)
        {
            var sb3 = new System.Text.StringBuilder();
            for (int q = 0; q < flashes.Count; q++)
            {
                double ft = flashes[q];
                bool onB = Math.Abs(Nearest(bs, ft)) <= HitTol, onH = !onB && Math.Abs(Nearest(halves, ft)) <= 0.04;
                var near = AllEvents.Where(e => Math.Abs(e.T - ft) <= 0.05).Select(e => $"{e.Kind}{e.Strength:F1}@{(e.T - ft) * 1000:+0;-0}");
                sb3.Append($"{ft,7:F2} {(onB ? "BEAT" : onH ? "half" : "STRAY")} amp {amps[q]:F2} beat{-Nearest(bs, ft) * 1000:+0;-0}ms  {string.Join(" ", near)}\n");
            }
            T.Info(sb3.ToString());
        }
        double beatCover = bs.Count > 0 ? bs.Count(t => Math.Abs(Nearest(flashes, t)) <= HitTol) / (double)bs.Count : 0;
        double offMs = offs.Count > 0 ? Median(offs) * 1000 : double.NaN;

        // 反応: 明るさの増え方（コマ間の + の差）と、音の立ち上がりの強さ（コマごとに最大）の相関
        var on = new double[n];
        foreach (var (t, v) in odf) { int i = (int)Math.Round(t * Fps); if (i >= 0 && i < n) on[i] = Math.Max(on[i], v); }
        var up = u;
        double bestR = double.NegativeInfinity; int bestLag = 0;
        for (int lag = -6; lag <= 12; lag++)
        {
            double r = Corr(f0, n, i => on[i], i => i + lag >= 0 && i + lag < n ? up[i + lag] : 0);
            if (r > bestR) { bestR = r; bestLag = lag; }
        }
        // 大きさ: 0.5 秒でならした明るさと音の大きさ
        var sb = Smooth(b, 30);
        var sr = Smooth(rms.Take(n).Select(x => 20 * Math.Log10(Math.Max(1e-5, x))).ToArray(), 30);
        double loud = Corr(f0, Math.Min(n, sr.Length), i => sb[i], i => sr[i]);
        double jit = L.Jitter / Math.Max(1e-9, (n - f0) / Fps * L.Out.Length);
        return new ModeScore(L.Mode, hit, beatCover, offMs, bestR, bestLag * 1000 / Fps, loud, range, jit, flashes.Count) { Half = half, RandAmp = randShare, Accent = accent };
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

// ==================================================
// SpectrumTimingTests.cs - 光り方「スペクトラム」の低音の端が、キックに遅れずに光り、キックの間ははっきり暗くなることを確かめる
//   （「低音に少し反応するけど、何に反応しているか分からず、遅れて見える」という声のため。合成した曲。録音がなくても動く）
//   アプリと同じ流れ（本物の分析に 10 ミリ秒ずつ流し、60 コマ／秒で描き、RgbDeviceChannel の送る・送らない・送ってから光るまでの遅れまで通す）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class SpectrumTimingTests
{
    public static void TestWeakSnareBodyDoesNotBounceLikeAKick()
    {
        var bounce = new KickBounce();
        var c = new EffectContext { T = 1 };
        c.Events.Add(new AudioEvent(1, AudioEventKind.Kick, 1, 2.4f, -4.4f, 4.7f));
        bounce.Take(c, weighBody: true);
        float snare = bounce.At(1, 0.12);
        c.Events.Clear();
        c.Events.Add(new AudioEvent(2, AudioEventKind.Kick, 2, 2.4f, 2f, 10f));
        c.T = 2;
        bounce.Take(c, weighBody: true);
        T.Check(snare < 0.25f * bounce.At(2, 0.12), "a weak snare body produces a smaller bass-edge rise than a kick");
    }
    private const double Tick = 1.0 / 60, T0 = 1000;
    /// <summary>音を先に分析しておく秒数（音の遅れの補正 30 ミリ秒と同じ）</summary>
    private const double Lead = 0.03;

    /// <summary>1 つの光り方 × デバイスの、LED に出ている明るさ（LED ごとの R・G・B の最大。0〜1。60 コマ／秒）</summary>
    private sealed class Lane
    {
        public required string Style, Dev;
        public required RgbDeviceChannel Ch;
        /// <summary>LED の位置（0〜1。6 個未満のゾーンの LED は NaN）</summary>
        public required float[] X;
        public readonly Queue<(double At, Rgb[] F)> Pending = new();
        public Rgb[] Seen = Array.Empty<Rgb>();
        public readonly List<Rgb[]> Hist = new();
    }

    private static RgbDevice DeviceOf(string name) => name switch
    {
        "strip24" => new RgbDevice(0, "Strip", 24, 4, null, "HID: a"),
        // マザーボードの 8 個の LED の端子（ファン 1 個分）
        "fan8" => new RgbDevice(1, "Board", 8, RgbDevice.TypeMotherboard, null, "HID: b"),
        // 直接書き込むグラボ（1 秒に 10 回まで。ゾーン 4・20・17）
        "direct" => new RgbDevice(5, "GPU", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }) { DirectGpu = true },
        // メモリ（I2C。遅いデバイス）
        "dram" => new RgbDevice(6, "DRAM", 8, RgbDevice.TypeDram, null, "I2C: e"),
        _ => throw new ArgumentException(name),
    };

    private static float[] Positions(RgbDevice d)
    {
        var zs = d.Zones is { Count: > 0 } z ? z.ToArray() : new[] { d.LedCount };
        var x = new float[d.LedCount];
        int s0 = 0;
        foreach (int c in zs)
        {
            for (int j = 0; j < c; j++) x[s0 + j] = c < 6 ? float.NaN : j / (float)(c - 1);
            s0 += c;
        }
        return x;
    }

    private static List<Lane> Play(SignalGen g, IEnumerable<(string Style, string Dev)> lanes)
    {
        var baseCfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyIntensity(baseCfg.Music, Intensities.Moderate);
        var engine = new AudioEngine(g.Fs) { Settings = MusicTuning.EngineSettings(baseCfg) };
        var list = new List<Lane>();
        foreach (var (style, devName) in lanes)
        {
            var dev = DeviceOf(devName);
            var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = style, Brightness = 100 };
            MusicTuning.ApplyIntensity(cfg.Music, Intensities.Moderate);
            cfg.Music.ReduceFlashing = true;
            var ch = new RgbDeviceChannel(dev, T0) { SharedColor = new SharedColor() };
            ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = dev.Name }, false);
            list.Add(new Lane { Style = style, Dev = devName, Ch = ch, X = Positions(dev), Seen = new Rgb[dev.LedCount] });
        }
        var buf = g.Buf;
        int pos = 0, chunk = g.Fs / 100;
        for (int k = 0; ; k++)
        {
            double sec = k * Tick;
            if (sec + 0.15 > buf.Length / (double)g.Fs) break;
            while (pos < buf.Length && pos / (double)g.Fs < sec + Lead)
            {
                int m = Math.Min(chunk, buf.Length - pos);
                engine.Push(new ReadOnlySpan<float>(buf, pos, m), T0 + pos / (double)g.Fs);
                pos += m;
            }
            double t = T0 + sec;
            var beat = engine.Beat;
            foreach (var L in list)
            {
                var ch = L.Ch;
                ch.Prepare(t, Tick, engine.Features, engine.Events, beat, null, false, false);
                if (ch.Render())
                {
                    var reason = ch.ShouldSend(t, Tick, true);
                    if (reason != SendReason.None)
                    {
                        ch.MarkSent(t, Tick, reason, 1);
                        // 送ってから LED に出るまでの遅れ（直接書き込むグラボ・メモリは長い）
                        L.Pending.Enqueue((sec + ch.EffectiveLatencyMs / 1000.0, (Rgb[])ch.Frame.Clone()));
                    }
                }
                while (L.Pending.Count > 0 && L.Pending.Peek().At <= sec + 1e-9) L.Seen = L.Pending.Dequeue().F;
                L.Hist.Add(L.Seen);
            }
        }
        return list;
    }

    // ===== 測り方

    /// <summary>選んだ LED の明るさの平均（コマごと。0〜1）</summary>
    private static float[] Series(Lane L, Func<int, bool> pick)
    {
        var idx = Enumerable.Range(0, L.X.Length).Where(pick).ToArray();
        var r = new float[L.Hist.Count];
        for (int f = 0; f < r.Length; f++)
        {
            double a = 0;
            foreach (int i in idx) { var c = L.Hist[f][i]; a += Math.Max(c.R, Math.Max(c.G, c.B)); }
            r[f] = (float)(a / Math.Max(1, idx.Length) / 255);
        }
        return r;
    }

    /// <summary>低音の端（x ≤ 0.2）の LED</summary>
    private static float[] Low(Lane L) => Series(L, i => L.X[i] <= 0.2f);

    private static float Max(float[] s, double a, double b)
    {
        float m = 0;
        for (int f = Math.Max(0, (int)Math.Ceiling(a / Tick)); f <= Math.Min(s.Length - 1, (int)Math.Floor(b / Tick)); f++) m = Math.Max(m, s[f]);
        return m;
    }

    private static float Min(float[] s, double a, double b)
    {
        float m = 1;
        for (int f = Math.Max(0, (int)Math.Ceiling(a / Tick)); f <= Math.Min(s.Length - 1, (int)Math.Floor(b / Tick)); f++) m = Math.Min(m, s[f]);
        return m;
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return double.NaN;
        var o = v.OrderBy(x => x).ToList();
        return o[o.Count / 2];
    }

    /// <summary>
    /// キックごとの、光が半分まで上がるまでの秒数（キックの前 0.06 秒のいちばん暗いところから、後 0.2 秒のいちばん明るいところまでの半分）の中央値と、
    /// 上がった割合（0.15 以上上がったキック）
    /// </summary>
    private static (double Lat50, double Hit) Rise(float[] s, List<double> kicks)
    {
        var lat = new List<double>();
        int hit = 0;
        foreach (double k in kicks)
        {
            float lo = Min(s, k - 0.06, k), pk = Max(s, k, k + 0.2);
            if (pk - lo < 0.15f) continue;
            hit++;
            float half = lo + 0.5f * (pk - lo);
            for (int f = (int)Math.Ceiling(k / Tick); f < s.Length; f++)
                if (s[f] >= half) { lat.Add(f * Tick - k); break; }
        }
        return (Median(lat), hit / (double)Math.Max(1, kicks.Count));
    }

    /// <summary>キックごとの、明るさの上下の深さ（キックの山と、次のキックまでの谷の差 ÷ 山）の中央値</summary>
    private static double Depth(float[] s, List<double> kicks)
    {
        var d = new List<double>();
        for (int i = 0; i + 1 < kicks.Count; i++)
        {
            double k = kicks[i], next = kicks[i + 1];
            float pk = Max(s, k, k + 0.15), tr = Min(s, k + 0.12, next - 0.02);
            if (pk > 0.05f) d.Add((pk - tr) / pk);
        }
        return Median(d);
    }

    /// <summary>
    /// ハウスの曲（124 BPM）: 毎拍キック、キックでしぼんで裏拍にふくらむベース（サイドチェイン）、16 分のハイハット、2・4 拍目のクラップ、和音、歌に似た音。
    /// ベースとハイハットがずっと鳴っているので、帯の強さだけで光ると、低音の端はいつも明るかった
    /// </summary>
    private static SignalGen House(double seconds, int seed)
    {
        var g = new SignalGen(48000, seconds, seed);
        double p = 60 / 124.0;
        int k = 0;
        for (double t = 0.5; t < seconds - 0.5; t += p, k++)
        {
            g.Kick(t, 0.7);
            if (k % 2 == 1) g.Snare(t, 0.25);
            for (int s = 1; s < 4; s++) g.Hat(t + s * p / 4, s == 2 ? 0.07 : 0.035);
            SidechainBass(g, t, t + p, 0.14);
        }
        g.PadChord(0.5, seconds - 0.5, new[] { 220.0, 261.6, 329.6 }, amp: 0.03);
        g.VocalTone(4, seconds - 1, new[] { 330.0, 392, 440, 392 }, 0.9, amp: 0.05);
        g.Pink(-40, 0, seconds);
        return g;
    }

    /// <summary>キックの時刻 from から次のキック to まで鳴るベース（55・110 Hz）。キックでしぼみ、τ 0.08 秒でふくらむ（サイドチェイン）</summary>
    private static void SidechainBass(SignalGen g, double from, double to, double amp)
    {
        int a = (int)(from * g.Fs), b = Math.Min(g.Buf.Length, (int)(to * g.Fs));
        for (int i = a; i < b; i++)
        {
            double t = (i - a) / (double)g.Fs, tt = i / (double)g.Fs;
            double env = (1 - Math.Exp(-t / 0.08)) * Math.Min(1, (b - i) / (0.01 * g.Fs));
            double v = amp * env * (Math.Sin(2 * Math.PI * 55 * tt) + 0.35 * Math.Sin(2 * Math.PI * 110 * tt));
            g.Buf[i] = (float)Math.Clamp(g.Buf[i] + v, -1, 1);
        }
    }

    /// <summary>
    /// 更新の速いデバイス（ファン 8 個・テープ 24 個）: 低音の端は、キックから 25 ミリ秒以内に半分まで光り（音量メーターと同じくらい。
    /// 6.15.10 は FFT の 31・63 Hz を待つので 60〜70 ミリ秒）、キックの間は 7 割以上暗くなる（6.15.10 はふくらむベースで 0.45 程度しか下がらなかった）
    /// </summary>
    public static void TestBassEdgeLightsOnTheKick()
    {
        var g = House(24, 61);
        var kicks = g.KickTimes.Where(t => t > 6 && t < 23).ToList();
        var lanes = Play(g, new[] { (MusicStyles.Spectrum, "fan8"), (MusicStyles.Spectrum, "strip24"), (MusicStyles.Meter, "fan8") });
        var meter = lanes.Single(l => l.Style == MusicStyles.Meter);
        var (meterLat, _) = Rise(Series(meter, _ => true), kicks);
        foreach (var L in lanes.Where(l => l.Style == MusicStyles.Spectrum))
        {
            var lo = Low(L);
            var (lat, hit) = Rise(lo, kicks);
            double depth = Depth(lo, kicks);
            T.Info($"Spectrum {L.Dev,-8} bass edge: half rise {lat * 1000:F0} ms (meter {meterLat * 1000:F0} ms), kicks lit {hit:P0}, depth {depth:F2}");
            T.Check(lat <= 0.025 && lat <= meterLat + 0.010, $"Spectrum {L.Dev}: the bass edge rises with the kick ({lat * 1000:F0} ms, meter {meterLat * 1000:F0} ms)");
            T.Check(hit >= 0.9, $"Spectrum {L.Dev}: the bass edge lights on every kick ({hit:P0})");
            T.Check(depth >= 0.7, $"Spectrum {L.Dev}: the bass edge goes dark between kicks, not lit by the off-beat bass (depth {depth:F2})");
        }
    }

    /// <summary>
    /// 遅いデバイス: 直接書き込むグラボ（1 秒に 10 回）の低音の端は、テンポをつかんだ曲でキックから 30 ミリ秒以内に半分まで光る
    /// （6.15.10 は拍の頭に送るコマにまだ上がる前の絵が入り、上がった絵は次の 0.1 秒後に届いたので 110 ミリ秒前後）。
    /// メモリ（全体で 1 色）は 40 ミリ秒以内に光り、キックの 6 割以上で光る（6.15.10 は 130 ミリ秒前後で、中音・高音でいつも明るかった）
    /// </summary>
    public static void TestSlowDevicesLightOnTheKick()
    {
        var g = House(24, 62);
        var kicks = g.KickTimes.Where(t => t > 8 && t < 23).ToList();
        var lanes = Play(g, new[] { (MusicStyles.Spectrum, "direct"), (MusicStyles.Spectrum, "dram") });
        var gpu = lanes.Single(l => l.Dev == "direct");
        var (gLat, gHit) = Rise(Low(gpu), kicks);
        T.Info($"Spectrum direct bass edge: half rise {gLat * 1000:F0} ms, kicks lit {gHit:P0}, depth {Depth(Low(gpu), kicks):F2}");
        T.Check(gLat <= 0.030 && gHit >= 0.9, $"Spectrum direct GPU: the bass edge rises with the kick ({gLat * 1000:F0} ms, {gHit:P0} of kicks)");
        var dram = lanes.Single(l => l.Dev == "dram");
        var all = Series(dram, _ => true);
        var (dLat, dHit) = Rise(all, kicks);
        T.Info($"Spectrum dram: half rise {dLat * 1000:F0} ms, kicks lit {dHit:P0}, depth {Depth(all, kicks):F2}");
        T.Check(dLat <= 0.040 && dHit >= 0.6, $"Spectrum DRAM: lights with the kick ({dLat * 1000:F0} ms, {dHit:P0} of kicks)");
    }

    /// <summary>
    /// LED の色は位置で決まる（低音の端はいつも同じ色。6.15.10 は拍のたびに色が少しずつ回り、60 拍で別の色になっていたので、
    /// どの高さの音がどの色か覚えられなかった）
    /// </summary>
    public static void TestBassColourStaysTheSame()
    {
        var g = House(36, 63);
        var kicks = g.KickTimes.Where(t => t > 4 && t < 35).ToList();
        var L = Play(g, new[] { (MusicStyles.Spectrum, "strip24") }).Single();
        // キックの 30 ミリ秒後の、最初の LED の色（いちばん強い成分で割る）
        (double R, double G, double B)? Col(double k)
        {
            int f = Math.Min(L.Hist.Count - 1, (int)Math.Round((k + 0.03) / Tick));
            var c = L.Hist[f][0];
            double m = Math.Max(c.R, Math.Max(c.G, c.B));
            return m < 40 ? null : (c.R / m, c.G / m, c.B / m);
        }
        var cols = kicks.Select(Col).Where(c => c is not null).Select(c => c!.Value).ToList();
        double worst = 0;
        if (cols.Count > 0)
        {
            var first = cols[0];
            foreach (var c in cols)
                worst = Math.Max(worst, Math.Max(Math.Abs(c.R - first.R), Math.Max(Math.Abs(c.G - first.G), Math.Abs(c.B - first.B))));
        }
        T.Info($"Spectrum strip24 bass colour over {cols.Count} kicks: largest change {worst:F2}");
        T.Check(cols.Count >= 50 && worst <= 0.1, $"Spectrum: the bass LED keeps its colour over {cols.Count} kicks (change {worst:F2})");
    }

    /// <summary>
    /// 話し声だけ（拍のない声と「パ・タ」の音）: 低音の端・全体が上がる量（1 秒あたりの上がりの合計）は 6.15.10 より増えない
    /// （ファン 8 個。6.15.10 は低音の端 0.43/s・全体 0.49/s。話し声の出だしもキックとして届くので、テンポをつかめていないキックの弾みは弱い）
    /// </summary>
    public static void TestSpeechDoesNotPumpTheBassEdge()
    {
        var g = new SignalGen(48000, 24, seed: 64);
        var r = new Random(64);
        g.Pink(-58, 0, 24);
        double[] speech = { 180, 165, 200, 150, 190, 170, 210, 160 };
        for (double t = 2; t < 22.4; t += 2.5)
        {
            g.VocalTone(t, t + 1.6, speech, 0.2, amp: 0.15, vibratoCents: 10);
            for (double s = t; s < t + 1.6; s += 0.2) if (r.NextDouble() < 0.4) g.Snare(s, 0.09);
        }
        var L = Play(g, new[] { (MusicStyles.Spectrum, "fan8") }).Single();
        static double UpPerSec(float[] s, double a, double b)
        {
            double up = 0;
            for (int f = (int)(a / Tick) + 1; f < Math.Min(s.Length, (int)(b / Tick)); f++) up += Math.Max(0, s[f] - s[f - 1]);
            return up / (b - a);
        }
        double lo = UpPerSec(Low(L), 4, 22), all = UpPerSec(Series(L, _ => true), 4, 22);
        T.Info($"Spectrum fan8 speech: bass edge up {lo:F2}/s, whole up {all:F2}/s");
        T.Check(lo <= 0.4 && all <= 0.49, $"Spectrum: speech does not pump the bass edge (bass {lo:F2}/s, whole {all:F2}/s)");
    }
}

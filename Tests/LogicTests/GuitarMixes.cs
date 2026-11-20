// ==================================================
// GuitarMixes.cs - テスト用の合成した、ギターの多い曲（弦は Karplus-Strong。ひずみ・キャビネット・ミックスのハイパス、
//   キックでダッキング、バスのコンプレッサー、マスタリングのリミッター）。同じ曲の「ギターあり」「ギターなし」と、
//   ギターの音の時刻・ドラムの時刻も返す。ギターのない曲（せりふ・映画）もここに置く
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace LogicTests;

/// <summary>ギターの多い合成の曲</summary>
public sealed class GuitarSong
{
    public required string Name;
    public double Bpm;
    public float[] With = Array.Empty<float>(), Without = Array.Empty<float>();
    /// <summary>ギターの音の時刻と大きさ（0〜1）</summary>
    public readonly List<(double T, double Amp)> Gtr = new();
    /// <summary>ドラム（キック・スネア・ハイハット・シンバル）の時刻</summary>
    public readonly List<double> Drums = new();
    public readonly List<double> Kicks = new();
    public double Sec => With.Length / (double)GuitarMixes.Fs;

    /// <summary>ドラムから 80 ミリ秒以上離れたギターの音（ギターだけの音）</summary>
    public List<double> GuitarOnly(double from, double to)
        => Gtr.Where(h => h.T >= from && h.T < to && !Drums.Any(d => Math.Abs(d - h.T) < 0.08)).Select(h => h.T).ToList();
}

public static class GuitarMixes
{
    public const int Fs = 48000;

    private sealed class Mix
    {
        public readonly SignalGen Dr, Mu;
        public readonly float[] Gt;
        private readonly GuitarSong _s;
        public Mix(GuitarSong s, double sec, int seed) { _s = s; Dr = new SignalGen(Fs, sec, seed); Mu = new SignalGen(Fs, sec, seed + 100); Gt = new float[Dr.Buf.Length]; }
        public void K(double t, double a) { Dr.Kick(t, a); _s.Drums.Add(t); _s.Kicks.Add(t); }
        public void Sn(double t, double a) { Dr.Snare(t, a); _s.Drums.Add(t); }
        public void H(double t, double a) { Dr.Hat(t, a); _s.Drums.Add(t); }

        public void Render(double duck, double duckSec = 0.12)
        {
            _s.With = Bus(true, duck, duckSec);
            _s.Without = Bus(false, duck, duckSec);
        }

        /// <summary>ドラム + （キックでダッキングした）音楽 + ギター → バスのコンプレッサー（−16 dBFS から 3:1）→ リミッター</summary>
        private float[] Bus(bool gtr, double duck, double duckSec)
        {
            int n = Dr.Buf.Length;
            var o = new double[n];
            var ks = _s.Kicks.OrderBy(x => x).ToArray();
            int ki = 0;
            double lastK = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Fs;
                while (ki < ks.Length && ks[ki] <= t) lastK = ks[ki++];
                double d = 1 - duck * Math.Exp(-(t - lastK) / duckSec);
                o[i] = Dr.Buf[i] + Mu.Buf[i] * d + (gtr ? Gt[i] : 0);
            }
            double env = 0, aA = 1 - Math.Exp(-1 / (0.005 * Fs)), aR = 1 - Math.Exp(-1 / (0.12 * Fs));
            double thr = Math.Pow(10, -16 / 20.0);
            var r = new float[n];
            for (int i = 0; i < n; i++)
            {
                double x = Math.Abs(o[i]);
                env += (x > env ? aA : aR) * (x - env);
                double g = env > thr ? Math.Pow(env / thr, 1 / 3.0 - 1) : 1;
                r[i] = (float)(Math.Tanh(1.6 * o[i] * g) / 1.6 * 1.4);
            }
            Limiter(r);
            return r;
        }
    }

    /// <summary>マスタリングのリミッター（+4 dB 上げて −1 dBFS を超えないように。先読み 2 ミリ秒・戻り 80 ミリ秒）</summary>
    private static void Limiter(float[] r)
    {
        int la = (int)(0.002 * Fs);
        double ceil = Math.Pow(10, -1 / 20.0), up = Math.Pow(10, 4 / 20.0), g = 1, rel = 1 - Math.Exp(-1 / (0.08 * Fs));
        var o = new float[r.Length];
        for (int i = 0; i < r.Length; i++)
        {
            double pk = 0;
            for (int j = i; j < Math.Min(r.Length, i + la); j++) pk = Math.Max(pk, Math.Abs(r[j]) * up);
            double need = pk > ceil ? ceil / pk : 1;
            g = need < g ? need : g + (need - g) * rel;
            o[i] = (float)(r[i] * up * g);
        }
        Array.Copy(o, r, r.Length);
    }

    /// <summary>
    /// 弦 1 本（Karplus-Strong）。bright = はじいたノイズの明るさ（0〜1）、decay = 消える秒数、damp = ループの低域フィルターの強さ（0.5 で最大）。
    /// ピックの「カチッ」（2 ミリ秒の高い音のノイズ）も足す
    /// </summary>
    public static void Pluck(double[] dst, double t0, double f, double amp, double decay, double bright, double damp, double dur, int seed, double pick = 0.25)
    {
        int len = Math.Max(2, (int)Math.Round(Fs / f));
        var line = new double[len];
        var r = new Random(seed);
        double lp = 0;
        for (int i = 0; i < len; i++) { lp += bright * ((r.NextDouble() * 2 - 1) - lp); line[i] = lp; }
        double mean = line.Average();
        for (int i = 0; i < len; i++) line[i] -= mean;
        double rho = Math.Exp(-1.0 / (f * decay));
        int s0 = (int)Math.Round(t0 * Fs), n = (int)(dur * Fs);
        int idx = 0;
        double hp1 = 0, hx1 = 0;
        for (int i = 0; i < n && s0 + i < dst.Length; i++)
        {
            double y = line[idx];
            line[idx] = rho * ((1 - damp) * line[idx] + damp * line[(idx + 1) % len]);
            idx = (idx + 1) % len;
            double fade = i > n - 0.02 * Fs ? (n - i) / (0.02 * Fs) : 1;
            double v = amp * y * fade;
            if (i < 0.002 * Fs && pick > 0)
            {
                double w = r.NextDouble() * 2 - 1;
                double h = 0.85 * (hp1 + w - hx1); hp1 = h; hx1 = w;
                v += amp * pick * h * (1 - i / (0.002 * Fs));
            }
            if (s0 + i >= 0) dst[s0 + i] += v;
        }
    }

    /// <summary>ギターのストローク: 弦を spread 秒ずつずらしてはじく（低い弦から）</summary>
    public static void Strum(double[] dst, double t0, double[] strings, double amp, double spread, double decay, double bright, double damp, int seed, double dur = 1.5)
    {
        for (int j = 0; j < strings.Length; j++) Pluck(dst, t0 + j * spread, strings[j], amp, decay, bright, damp, dur, seed * 31 + j);
    }

    /// <summary>2 次のフィルター（RBJ の双 2 次）。hp = true でハイパス</summary>
    private static void Biquad(double[] x, double fc, bool hp, double q = 0.707)
    {
        double w = 2 * Math.PI * fc / Fs, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q);
        double b0 = hp ? (1 + cs) / 2 : (1 - cs) / 2, b1 = hp ? -(1 + cs) : 1 - cs, b2 = b0, a0 = 1 + al, a1 = -2 * cs, a2 = 1 - al;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double y = (b0 * x[i] + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = y; x[i] = y;
        }
    }

    /// <summary>ひずみ（tanh）＋キャビネット（4 次のローパス 5 kHz）＋ミックスのハイパス（2 次 100 Hz）をかけて足す</summary>
    public static void Distort(double[] src, float[] dst, double drive, double level)
    {
        var y = new double[src.Length];
        double norm = Math.Tanh(drive);
        for (int i = 0; i < src.Length; i++) y[i] = Math.Tanh(drive * src[i]) / norm;
        Biquad(y, 5000, false, 0.54); Biquad(y, 5000, false, 1.31); Biquad(y, 100, true);
        for (int i = 0; i < y.Length; i++) dst[i] = (float)(dst[i] + level * y[i]);
    }

    /// <summary>クリーンのギター: ミックスのハイパス（2 次 100 Hz）をかけて足す</summary>
    public static void Clean(double[] src, float[] dst, double level)
    {
        var y = (double[])src.Clone();
        Biquad(y, 100, true);
        for (int i = 0; i < y.Length; i++) dst[i] = (float)(dst[i] + level * y[i]);
    }

    private static readonly double[] Am = { 220, 261.6, 329.6 }, Fc = { 174.6, 220, 261.6 }, C = { 261.6, 329.6, 392 }, G = { 196, 246.9, 293.7 };
    private static readonly double[][] Prog = { Am, Fc, C, G };
    // ギターの和音（6 本の弦。E 形・A 形の押さえ方に近い）
    private static readonly double[] GE = { 82.4, 123.5, 164.8, 207.7, 246.9, 329.6 }, GA = { 110, 164.8, 220, 277.2, 329.6, 440 },
        GD = { 146.8, 220, 293.7, 370, 440 }, GG = { 98, 123.5, 146.8, 196, 246.9, 392 }, GAm = { 110, 164.8, 220, 261.6, 329.6, 440 },
        GC = { 130.8, 164.8, 196, 261.6, 329.6 }, GF = { 87.3, 130.8, 174.6, 220, 261.6, 349.2 };
    private static double[] Power(double root) => new[] { root, root * 1.498, root * 2 };

    /// <summary>ロック 120 BPM: ひずんだギター（A メロは 8 分の刻み（ブリッジミュート）と 4 拍目の 16 分裏の食い、サビは開放のパワーコード）</summary>
    public static GuitarSong Rock()
    {
        double bpm = 120, p = 60 / bpm, bar = 4 * p, t0 = 0.5; int bars = 20;
        var s = new GuitarSong { Name = "rock120", Bpm = bpm };
        var m = new Mix(s, t0 + bars * bar + 1.5, 21);
        var g = new double[m.Gt.Length];
        double[] roots = { 82.4, 110, 98, 73.4 };
        int seed = 1;
        for (int b = 0; b < bars; b++)
        {
            bool cho = b >= 10;
            double root = roots[(b / 2) % 4];
            for (int k = 0; k < 4; k++)
            {
                double t = t0 + b * bar + k * p;
                if (k == 0 || k == 2) m.K(t, cho ? 0.8 : 0.55);
                if (k == 2 && b % 2 == 1) m.K(t + p / 2, 0.4);
                if (k == 1 || k == 3) m.Sn(t, cho ? 0.5 : 0.32);
                m.H(t, cho ? 0.06 : 0.035); m.H(t + p / 2, cho ? 0.045 : 0.03);
                m.Mu.Tone(root, cho ? 0.11 : 0.08, t + 0.01, t + p - 0.02);
                if (!cho)
                {
                    for (int q = 0; q < 2; q++)
                    {
                        double tg = t + q * p / 2;
                        foreach (var f in Power(root)) Pluck(g, tg, f, 0.33, 0.10, 0.5, 0.5, 0.2, seed++);
                        s.Gtr.Add((tg, q == 0 ? 1 : 0.8));
                    }
                    if (k == 3) { double tg = t + 0.75 * p; foreach (var f in Power(root * 1.335)) Pluck(g, tg, f, 0.4, 0.5, 0.8, 0.3, 0.3, seed++); s.Gtr.Add((tg, 1)); }
                }
                else if (k == 0 || k == 3)
                {
                    foreach (var f in Power(k == 0 ? root : root * 1.122)) Pluck(g, t, f, 0.45, 1.2, 0.9, 0.2, k == 0 ? 0.74 * p * 2 : p, seed++);
                    s.Gtr.Add((t, 1));
                }
                else if (k == 1)
                {
                    double tg = t + p / 2;
                    foreach (var f in Power(root * 1.189)) Pluck(g, tg, f, 0.45, 1.2, 0.9, 0.2, 1.4 * p, seed++);
                    s.Gtr.Add((tg, 1));
                }
            }
            if (cho) m.Mu.VocalTone(t0 + b * bar, t0 + (b + 1) * bar - 0.1, new[] { 329.6, 392, 440, 392 }, p, amp: 0.06);
        }
        Distort(g, m.Gt, 6, 0.10);
        m.Render(duck: 0.15);
        return s;
    }

    /// <summary>厚いロック 140 BPM: ひずんだ和音を 8 分で弾き続ける壁（ライド・クラッシュ・歌）。前半 4 小節はギターのリフだけ、後半はリードギターが乗る</summary>
    public static GuitarSong RockDense()
    {
        double bpm = 140, p = 60 / bpm, bar = 4 * p, t0 = 0.5; int bars = 24;
        var s = new GuitarSong { Name = "rockdense140", Bpm = bpm };
        var m = new Mix(s, t0 + bars * bar + 1.5, 81);
        var g = new double[m.Gt.Length];
        var lead = new double[m.Gt.Length];
        double[] roots = { 110, 98, 87.3, 98 };
        double[] riff = { 110, 110, 130.8, 110, 146.8, 110, 130.8, 98 };
        double[] mel = { 659.3, 587.3, 523.3, 587.3, 659.3, 784, 659.3, 587.3 };
        int seed = 1;
        var r = new Random(9);
        for (int b = 0; b < bars; b++)
        {
            bool intro = b < 4, leadOn = b >= 14;
            double root = roots[(b / 2) % 4];
            for (int k = 0; k < 4; k++)
            {
                double t = t0 + b * bar + k * p;
                if (!intro)
                {
                    if (k == 0 || k == 2) m.K(t, 0.8);
                    if (k == 3) m.K(t + p / 2, 0.5);
                    if (k == 1 || k == 3) m.Sn(t, 0.5);
                    m.H(t, 0.07); m.H(t + p / 2, 0.05);
                    if (k == 0 && b % 2 == 0) { m.Dr.White(-24, t, t + 0.9); s.Drums.Add(t); }
                    m.Mu.Tone(root / 2, 0.1, t + 0.01, t + p - 0.02);
                    for (int q = 0; q < 2; q++)
                    {
                        double tg = t + q * p / 2;
                        foreach (var f in Power(root)) Pluck(g, tg, f, 0.4, 0.6, 0.85, 0.25, p / 2 + 0.05, seed++);
                        s.Gtr.Add((tg, q == 0 ? 1 : 0.8));
                    }
                }
                else
                {
                    for (int q = 0; q < 2; q++)
                    {
                        double tg = t + q * p / 2;
                        Pluck(g, tg, riff[(k * 2 + q) % 8], 0.55, 0.5, 0.85, 0.25, p / 2 + 0.03, seed++);
                        s.Gtr.Add((tg, 1));
                    }
                }
                if (leadOn)
                {
                    int sub = (b % 4 == 3) ? 4 : 2;
                    for (int q = 0; q < sub; q++)
                    {
                        double tl = t + q * p / sub;
                        Pluck(lead, tl, mel[(b * 4 + k * sub + q) % 8] * (r.NextDouble() < 0.2 ? 1.122 : 1), 0.5, 1.0, 0.8, 0.2, p / sub + 0.04, seed++);
                        s.Gtr.Add((tl, 0.8));
                    }
                }
            }
            if (b >= 6 && b < 14) m.Mu.VocalTone(t0 + b * bar, t0 + (b + 1) * bar - 0.1, new[] { 440.0, 493.9, 523.3, 493.9 }, p, amp: 0.06);
        }
        Distort(g, m.Gt, 7, 0.11);
        Distort(lead, m.Gt, 9, 0.07);
        m.Render(duck: 0.1);
        return s;
    }

    /// <summary>アルペジオ 76 BPM: クリーンのギターで和音を 1 音ずつ 8 分で弾く（パッド・歌）。前半 6 小節はドラムなし、後半は軽いドラム</summary>
    public static GuitarSong Arpeggio()
    {
        double bpm = 76, p = 60 / bpm, bar = 4 * p, t0 = 0.5; int bars = 12;
        var s = new GuitarSong { Name = "arpeggio76", Bpm = bpm };
        var m = new Mix(s, t0 + bars * bar + 2, 51);
        var g = new double[m.Gt.Length];
        var chords = new[] { GAm, GF, GC, GG };
        int[] order = { 0, 2, 3, 4, 5, 4, 3, 2 };
        int seed = 1;
        for (int b = 0; b < bars; b++)
        {
            bool drums = b >= 6;
            var ch = chords[b % 4];
            for (int q = 0; q < 8; q++)
            {
                double tg = t0 + b * bar + q * p / 2;
                Pluck(g, tg, ch[Math.Min(ch.Length - 1, order[q])], q == 0 ? 0.5 : 0.38, 1.4, 0.7, 0.25, 2.0, seed++);
                s.Gtr.Add((tg, q == 0 ? 1 : 0.75));
            }
            m.Mu.PadChord(t0 + b * bar, t0 + (b + 1) * bar, Prog[b % 4], 0.025, seed: b);
            m.Mu.VocalTone(t0 + b * bar + 0.2, t0 + (b + 1) * bar - 0.2, new[] { 440.0, 493.9, 523.3, 493.9 }, p, amp: 0.045);
            for (int k = 0; k < 4; k++)
            {
                double t = t0 + b * bar + k * p;
                if (drums) { if (k == 0) m.K(t, 0.4); if (k == 1 || k == 3) m.Sn(t, 0.22); m.H(t, 0.015); }
            }
        }
        Clean(g, m.Gt, 0.2);
        m.Render(duck: 0.1);
        return s;
    }

    /// <summary>ギターだけ 90 BPM: 前半はアコギのストローク（1 拍ずつ、強弱あり）、後半はひずんだパワーコード（2 拍ずつ）</summary>
    public static GuitarSong GuitarAlone()
    {
        double bpm = 90, p = 60 / bpm, bar = 4 * p, t0 = 0.5; int bars = 12;
        var s = new GuitarSong { Name = "guitar90", Bpm = bpm };
        var m = new Mix(s, t0 + bars * bar + 2, 71);
        var g = new double[m.Gt.Length];
        var g2 = new double[m.Gt.Length];
        var chords = new[] { GE, GA, GD, GA };
        int seed = 1;
        for (int b = 0; b < bars; b++)
        {
            var ch = chords[b % 4];
            for (int k = 0; k < 4; k++)
            {
                double tg = t0 + b * bar + k * p;
                if (b < 6)
                {
                    double a = k == 0 ? 0.32 : k == 2 ? 0.24 : 0.14;
                    Strum(g, tg, ch, a, 0.012, 1.0, 0.85, 0.35, seed++);
                    s.Gtr.Add((tg, a / 0.32));
                }
                else if (k % 2 == 0)
                {
                    foreach (var f in Power(ch[0])) Pluck(g2, tg, f, 0.45, 1.5, 0.9, 0.2, 2 * p, seed++);
                    s.Gtr.Add((tg, 1));
                }
            }
        }
        Clean(g, m.Gt, 0.25);
        Distort(g2, m.Gt, 6, 0.10);
        m.Render(duck: 0);
        return s;
    }

    /// <summary>ベースライン（1 秒 4 音）の上に、ひずんだギター（wall = 8 分で弾き続ける壁、でなければ 2 拍ごとの白玉）。ドラムなし（16 秒）</summary>
    public static float[] BassAndGuitar(bool wall)
    {
        var g = new SignalGen(Fs, 16, seed: 5);
        g.BassLine(0.2, 16, 4, InstrumentSources.BassNotes);
        var gt = new double[g.Buf.Length];
        double[] roots = { 110, 98, 87.3, 98 };
        int seed = 1;
        for (double t = 0.2; t < 15.5; t += wall ? 0.25 : 1.0)
        {
            double r = roots[(int)(t / 2) % 4];
            foreach (var f in Power(r)) Pluck(gt, t, f, 0.4, wall ? 0.6 : 1.5, 0.85, 0.25, wall ? 0.3 : 1.05, seed++);
        }
        var tmp = new float[g.Buf.Length];
        Distort(gt, tmp, 7, 0.11);
        for (int i = 0; i < tmp.Length; i++) g.Buf[i] = Math.Clamp(g.Buf[i] + tmp[i], -1, 1);
        return g.Buf;
    }

    /// <summary>せりふだけ（40 秒）: 1.6 秒しゃべって 0.9 秒あける。小さな雑音と、ときどき口の音（小さなスネアのような音）</summary>
    public static float[] Dialogue()
    {
        var g = new SignalGen(Fs, 40, seed: 13);
        var r = new Random(13);
        double[] speech = { 140, 175, 160, 190, 150, 210, 130, 170 };
        g.Pink(-54, 0, 40);
        for (double t = 1; t < 38; t += 2.5)
        {
            g.VocalTone(t, t + 1.6, speech, 0.2, amp: 0.12, vibratoCents: 10);
            for (double q = t; q < t + 1.6; q += 0.2) if (r.NextDouble() < 0.3) g.Snare(q, 0.04);
        }
        return g.Buf;
    }

    /// <summary>映画（62 秒）: 静かな BGM・せりふ・銃声の連射と爆発・小さなせりふと足音・BGM の盛り上がりと打撃音</summary>
    public static float[] Film()
    {
        var g = new SignalGen(Fs, 62, seed: 7);
        var rng = new Random(7);
        g.Pink(-52, 0, 62);
        g.PadChord(0, 8, new[] { 220.0, 261.6, 329.6 }, amp: 0.02);
        double[] speech = { 140, 175, 160, 190, 150, 210, 130, 170 };
        for (double t = 8; t < 19.5; t += 2.5) g.VocalTone(t, t + 1.6, speech, 0.2, amp: 0.12, vibratoCents: 10);
        g.Pink(-36, 20, 34);
        double tg = 20.4;
        while (tg < 33.5)
        {
            int shots = 4 + rng.Next(3);
            for (int k = 0; k < shots; k++) g.Snare(tg + k * 0.085, 0.45);
            tg += shots * 0.085 + 0.6 + rng.NextDouble() * 1.4;
        }
        foreach (double te in new[] { 21.3, 24.1, 26.9, 29.2, 32.6 })
        {
            g.Kick(te, 0.95);
            g.White(-20, te, te + 0.35);
        }
        for (double t = 34.5; t < 43.5; t += 3) g.VocalTone(t, t + 1.8, speech, 0.2, amp: 0.06, vibratoCents: 10);
        for (double t = 34.3; t < 44; t += 0.5 + rng.NextDouble() * 0.15) g.Snare(t, 0.05);
        g.PadChord(44, 52, new[] { 146.8, 220.0, 293.7 }, amp: 0.03);
        g.PadChord(52, 62, new[] { 146.8, 220.0, 293.7, 370.0 }, amp: 0.07);
        foreach (double tb in new[] { 50.0, 53.4, 56.3, 58.7, 60.9 })
        {
            g.Kick(tb, 0.9);
            g.BrassTone(tb, tb + 1.2, new[] { 73.4 }, 1.2, amp: 0.14);
        }
        return g.Buf;
    }
}

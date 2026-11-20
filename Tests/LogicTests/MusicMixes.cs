// ==================================================
// MusicMixes.cs - テスト用の合成した曲（ドラム・ベース・ギター・声・パッドを重ね、キックでダッキングして、バスのコンプレッサーをかける）
//   ハウス・ロック（パワーコード）・ヒップホップ（ハーフタイム）・バラード（ピアノの前奏）・EDM（ドロップとビルド）・ドラムンベース。
//   本物のキックの時刻と大きさ、拍ごとの中身（キック・スネア・ドラムがあるか・どの部分か）も返す
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace LogicTests;

/// <summary>拍ごとの中身: 時刻・小節・小節の中の拍・キックの大きさ（0 = なし）・スネアの大きさ・ドラムがあるか・部分の名前</summary>
public sealed record MixBeat(double T, int Bar, int InBar, double Kick, double Snare, bool Drums, string Sec);

/// <summary>合成した曲</summary>
public sealed class MixSong
{
    public required string Name;
    public required double Bpm;
    public float[] Buf = Array.Empty<float>();
    public readonly List<MixBeat> Beats = new();
    /// <summary>本物のキックの時刻と大きさ（拍の上にないキックも）</summary>
    public readonly List<(double T, double A)> Kicks = new();
    public double Sec => Buf.Length / (double)MusicMixes.Fs;
}

public static class MusicMixes
{
    public const int Fs = 48000;

    // ドラムの層と、それ以外の層（キックでダッキングする）
    private sealed class Mix
    {
        public readonly SignalGen Dr, Mu;
        public readonly List<(double T, double A)> Kicks = new();
        public Mix(double sec, int seed) { Dr = new SignalGen(Fs, sec, seed); Mu = new SignalGen(Fs, sec, seed + 100); }
        public void K(double t, double a) { Dr.Kick(t, a); Kicks.Add((t, a)); }

        /// <summary>音楽の層をキックでダッキング（サイドチェイン）して足し、バスのコンプレッサー（−16 dBFS から 3:1）とやわらかいリミッターをかける</summary>
        public float[] Render(double duck = 0.6, double duckSec = 0.12)
        {
            int n = Dr.Buf.Length;
            var o = new float[n];
            var ks = Kicks.Select(k => k.T).OrderBy(x => x).ToArray();
            int ki = 0;
            double lastK = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Fs;
                while (ki < ks.Length && ks[ki] <= t) lastK = ks[ki++];
                double d = 1 - duck * Math.Exp(-(t - lastK) / duckSec);
                o[i] = (float)(Dr.Buf[i] + Mu.Buf[i] * d);
            }
            double env = 0, aA = 1 - Math.Exp(-1 / (0.005 * Fs)), aR = 1 - Math.Exp(-1 / (0.12 * Fs));
            double thr = Math.Pow(10, -16 / 20.0);
            for (int i = 0; i < n; i++)
            {
                double x = Math.Abs(o[i]);
                env += (x > env ? aA : aR) * (x - env);
                double g = env > thr ? Math.Pow(env / thr, 1 / 3.0 - 1) : 1;
                o[i] = (float)(Math.Tanh(1.6 * o[i] * g) / 1.6 * 1.4);
            }
            return o;
        }

        public MixSong Finish(MixSong s, double duck = 0.6, double duckSec = 0.12)
        {
            s.Buf = Render(duck, duckSec);
            s.Kicks.AddRange(Kicks.OrderBy(k => k.T));
            return s;
        }
    }

    /// <summary>シンバル（クラッシュ）: 1.8 秒で消える雑音</summary>
    private static void Crash(SignalGen g, double t0, double amp)
    {
        var r = new Random((int)(t0 * 1000));
        int s0 = (int)(t0 * Fs), n = (int)(1.8 * Fs);
        double p1 = 0;
        for (int i = 0; i < n && s0 + i < g.Buf.Length; i++)
        {
            double w = r.NextDouble() * 2 - 1;
            double hp = w - p1; p1 = w;
            double env = Math.Exp(-i / (0.7 * Fs)) * Math.Min(1, i / (0.001 * Fs));
            g.Buf[s0 + i] = (float)Math.Clamp(g.Buf[s0 + i] + amp * env * hp * 0.5, -1, 1);
        }
    }

    /// <summary>ビルドの上がっていく雑音（ライザー）</summary>
    private static void Riser(SignalGen g, double from, double to, double amp)
    {
        var r = new Random(77);
        int a = (int)(from * Fs), b = (int)(to * Fs);
        double lp = 0;
        for (int i = a; i < b && i < g.Buf.Length; i++)
        {
            double u = (i - a) / (double)(b - a);
            double fc = 300 + 6000 * u * u;
            double k = 1 - Math.Exp(-2 * Math.PI * fc / Fs);
            lp += k * ((r.NextDouble() * 2 - 1) - lp);
            g.Buf[i] = (float)Math.Clamp(g.Buf[i] + amp * u * u * lp, -1, 1);
        }
    }

    /// <summary>のこぎり波のシンセ（倍音 10 個）</summary>
    private static void Saw(SignalGen g, double f, double amp, double from, double to)
    {
        int a = (int)(from * Fs), b = (int)(to * Fs);
        for (int i = a; i < b && i < g.Buf.Length; i++)
        {
            double t = (i - a) / (double)Fs;
            double env = Math.Min(1, t / 0.005) * Math.Min(1, (b - i) / (0.01 * Fs));
            double v = 0;
            for (int h = 1; h <= 10; h++) v += Math.Sin(2 * Math.PI * f * h * t + h) / h;
            g.Buf[i] = (float)Math.Clamp(g.Buf[i] + amp * env * v * 0.6, -1, 1);
        }
    }

    private static readonly double[] Am = { 220, 261.6, 329.6 }, Fc = { 174.6, 220, 261.6 }, C = { 261.6, 329.6, 392 }, G = { 196, 246.9, 293.7 };
    private static readonly double[][] Prog = { Am, Fc, C, G };
    private static double[] Power(double root) => new[] { root, root * 1.498, root * 2 };

    /// <summary>ハウス 124 BPM: 前奏（弱いキック）・A メロ・間奏（ドラムなし。最後にスネアのロール）・ドロップ。裏拍にベース</summary>
    public static MixSong House()
    {
        double bpm = 124, p = 60 / bpm, bar = 4 * p, t0 = 0.5;
        int bars = 28;
        var m = new Mix(t0 + bars * bar + 1, 11);
        var s = new MixSong { Name = "house124", Bpm = bpm };
        for (int b = 0; b < bars; b++)
        {
            string sec = b < 8 ? "intro" : b < 16 ? "verse" : b < 20 ? "break" : "drop";
            double kick = sec switch { "intro" => 0.45, "verse" => 0.6, "break" => 0, _ => 0.9 };
            var ch = Prog[(b / 2) % 4];
            m.Mu.PadChord(t0 + b * bar, t0 + (b + 1) * bar, ch, sec == "drop" ? 0.05 : 0.035, seed: b);
            for (int k = 0; k < 4; k++)
            {
                double t = t0 + b * bar + k * p, sn = 0;
                if (kick > 0) m.K(t, kick);
                if (sec != "intro" && sec != "break" && (k == 1 || k == 3)) { sn = sec == "drop" ? 0.45 : 0.3; m.Dr.Snare(t, sn); }
                if (sec == "break" && b == 19) for (int q = 0; q < 4; q++) m.Dr.Snare(t + q * p / 4, 0.1 + 0.08 * (k * 4 + q) / 4.0);
                if (sec != "break") { m.Dr.Hat(t + p / 2, sec == "drop" ? 0.09 : 0.05); m.Dr.Hat(t + p / 4, 0.02); m.Dr.Hat(t + 3 * p / 4, 0.02); }
                if (sec == "drop" && k == 0 && (b == 20 || b == 24)) Crash(m.Dr, t, 0.35);
                if (sec is "verse" or "drop") { m.Mu.Tone(ch[0] / 4, 0.12, t + p / 2, t + p - 0.02); m.Mu.Tone(ch[0] / 2, 0.05, t + p / 2, t + p - 0.02); }
                s.Beats.Add(new MixBeat(t, b, k, kick, sn, kick > 0, sec));
            }
            if (sec is "break" or "drop") m.Mu.VocalTone(t0 + b * bar, t0 + b * bar + bar - 0.1, new[] { 440.0, 392, 349.2, 392 }, p, amp: 0.06);
        }
        return m.Finish(s);
    }

    /// <summary>ロック 120 BPM: A メロ（1・3 拍目と裏のキック、8 分のパワーコード）・サビ前（スネアが増える）・サビ（2 拍目の裏にもキック）</summary>
    public static MixSong Rock()
    {
        double bpm = 120, p = 60 / bpm, bar = 4 * p, t0 = 0.5;
        int bars = 24;
        var m = new Mix(t0 + bars * bar + 1, 21);
        var s = new MixSong { Name = "rock120", Bpm = bpm };
        double[] roots = { 82.4, 110, 98, 73.4 };
        for (int b = 0; b < bars; b++)
        {
            string sec = b < 8 ? "verse" : b < 10 ? "pre" : b < 18 ? "chorus" : "verse";
            double root = roots[(b / 2) % 4];
            for (int k = 0; k < 4; k++)
            {
                double t = t0 + b * bar + k * p, kick = 0, sn = 0;
                if (sec == "verse")
                {
                    if (k == 0 || k == 2) kick = 0.5;
                    if (k == 2 && b % 2 == 1) m.K(t + p / 2, 0.35);
                    if (k == 1 || k == 3) sn = 0.3;
                    m.Dr.Hat(t, 0.035); m.Dr.Hat(t + p / 2, 0.03);
                    m.Mu.Strum(t, Power(root), 0.045, 0.004); m.Mu.Strum(t + p / 2, Power(root), 0.04, 0.004);
                }
                else if (sec == "pre")
                {
                    if (k == 0) kick = 0.6;
                    double u = ((b - 8) * 4 + k) / 8.0;
                    sn = 0.2 + 0.3 * u;
                    m.Dr.Snare(t + p / 2, sn);
                    m.Mu.Strum(t, Power(root), 0.08, 0.01);
                }
                else
                {
                    if (k == 0 || k == 2) kick = 0.85;
                    if (k == 1) m.K(t + p / 2, 0.6);
                    if (k == 1 || k == 3) sn = 0.55;
                    m.Dr.Hat(t, 0.07); m.Dr.Hat(t + p / 2, 0.05);
                    m.Mu.Strum(t, Power(root), 0.15, 0.012); m.Mu.Strum(t + p / 2, Power(root * 2), 0.07, 0.008);
                }
                if (kick > 0) m.K(t, kick);
                if (sn > 0) m.Dr.Snare(t, sn);
                if (sec == "chorus" && k == 0 && (b == 10 || b == 14)) Crash(m.Dr, t, 0.4);
                m.Mu.Tone(root, sec == "chorus" ? 0.12 : 0.08, t + 0.01, t + p - 0.02);
                s.Beats.Add(new MixBeat(t, b, k, kick, sn, true, sec));
            }
            if (sec == "chorus") m.Mu.VocalTone(t0 + b * bar, t0 + (b + 1) * bar - 0.1, new[] { 329.6, 392, 440, 392 }, p, amp: 0.07);
        }
        return m.Finish(s, duck: 0.15);
    }

    /// <summary>ヒップホップ 88 BPM（ハーフタイム）: 1 拍目と 2 拍目の裏にキックと 808 のベース、3 拍目にスネア、16 分のハイハットとラップ</summary>
    public static MixSong HipHop()
    {
        double bpm = 88, p = 60 / bpm, bar = 4 * p, t0 = 0.5;
        int bars = 16;
        var m = new Mix(t0 + bars * bar + 1, 31);
        var s = new MixSong { Name = "hiphop88", Bpm = bpm };
        double[] f808 = { 55, 55, 49, 41.2 };
        var rng = new Random(5);
        for (int b = 0; b < bars; b++)
        {
            string sec = b < 8 ? "a" : "b";
            double ka = sec == "a" ? 0.55 : 0.85;
            for (int k = 0; k < 4; k++)
            {
                double t = t0 + b * bar + k * p, kick = 0, sn = 0;
                if (k == 0) kick = ka;
                if (k == 1) { m.K(t + p / 2, ka * 0.8); m.Mu.Tone(f808[b % 4], 0.14, t + p / 2, t + p * 1.4); }
                if (k == 3 && b % 2 == 1) m.K(t + 0.75 * p, ka * 0.6);
                if (k == 2) sn = sec == "a" ? 0.35 : 0.5;
                for (int q = 0; q < 4; q++) m.Dr.Hat(t + q * p / 4, q == 0 ? 0.04 : 0.02);
                if (kick > 0) { m.K(t, kick); m.Mu.Tone(f808[b % 4], 0.16, t, t + p * 0.9); }
                if (sn > 0) m.Dr.Snare(t, sn);
                s.Beats.Add(new MixBeat(t, b, k, kick, sn, true, sec));
                for (int q = 0; q < 4; q++) if (rng.NextDouble() < 0.6) m.Mu.VocalTone(t + q * p / 4, t + q * p / 4 + p / 4 - 0.02, new[] { 180.0 + 30 * rng.NextDouble() }, p / 4, amp: 0.05, vibratoCents: 5);
            }
            m.Mu.PadChord(t0 + b * bar, t0 + (b + 1) * bar, Prog[(b / 2) % 4], 0.02, seed: b);
        }
        return m.Finish(s, duck: 0.2);
    }

    /// <summary>バラード 72 BPM: ピアノ（和音の弾き語り）と声だけの前奏 6 小節のあと、ドラムとベースが入る</summary>
    public static MixSong Ballad()
    {
        double bpm = 72, p = 60 / bpm, bar = 4 * p, t0 = 0.5;
        int bars = 14;
        var m = new Mix(t0 + bars * bar + 1, 41);
        var s = new MixSong { Name = "ballad72", Bpm = bpm };
        for (int b = 0; b < bars; b++)
        {
            string sec = b < 6 ? "piano" : "band";
            var ch = Prog[b % 4];
            for (int k = 0; k < 4; k++)
            {
                double t = t0 + b * bar + k * p, kick = 0, sn = 0;
                m.Mu.Strum(t, ch, k == 0 ? 0.06 : 0.04, 0.03);
                if (sec == "band")
                {
                    if (k == 0 || k == 2) kick = 0.35;
                    if (k == 1 || k == 3) sn = 0.2;
                    m.Dr.Hat(t, 0.02); m.Dr.Hat(t + p / 2, 0.015);
                }
                if (kick > 0) m.K(t, kick);
                if (sn > 0) m.Dr.Snare(t, sn);
                s.Beats.Add(new MixBeat(t, b, k, kick, sn, sec == "band", sec));
            }
            m.Mu.VocalTone(t0 + b * bar + 0.2, t0 + (b + 1) * bar - 0.2, new[] { 440.0, 493.9, 523.3, 493.9 }, p, amp: 0.06);
            if (sec == "band") m.Mu.Tone(ch[0] / 2, 0.08, t0 + b * bar, t0 + (b + 1) * bar - 0.02);
        }
        return m.Finish(s, duck: 0.1);
    }

    /// <summary>EDM 128 BPM: ドロップ（4 つ打ち・のこぎり波のベース）・ビルド（キックなし。スネアのロールが細かくなり、ライザーが上がる）・ドロップ</summary>
    public static MixSong Edm()
    {
        double bpm = 128, p = 60 / bpm, bar = 4 * p, t0 = 0.5;
        int bars = 24;
        var m = new Mix(t0 + bars * bar + 1, 51);
        var s = new MixSong { Name = "edm128", Bpm = bpm };
        for (int b = 0; b < bars; b++)
        {
            string sec = b < 8 ? "drop1" : b < 16 ? "build" : "drop2";
            var ch = Prog[(b / 2) % 4];
            for (int k = 0; k < 4; k++)
            {
                double t = t0 + b * bar + k * p, kick = 0, sn = 0;
                if (sec != "build")
                {
                    kick = sec == "drop1" ? 0.85 : 0.95;
                    if (k == 1 || k == 3) sn = 0.4;
                    m.Dr.Hat(t + p / 2, 0.08);
                    Saw(m.Mu, ch[0] / 4, 0.12, t + 0.02, t + p - 0.02);
                }
                else
                {
                    int sub = b < 12 ? 1 : b < 14 ? 2 : 4;
                    double u = (b - 8 + k / 4.0) / 8.0;
                    for (int q = 0; q < sub; q++) m.Dr.Snare(t + q * p / sub, 0.15 + 0.35 * u);
                }
                if (kick > 0) m.K(t, kick);
                if (sn > 0) m.Dr.Snare(t, sn);
                if (k == 0 && (b == 0 || b == 4 || b == 16 || b == 20)) Crash(m.Dr, t, 0.4);
                s.Beats.Add(new MixBeat(t, b, k, kick, sn, sec != "build", sec));
            }
            m.Mu.PadChord(t0 + b * bar, t0 + (b + 1) * bar, ch, sec == "build" ? 0.04 : 0.05, seed: b);
        }
        Riser(m.Mu, t0 + 8 * bar, t0 + 16 * bar, 0.25);
        return m.Finish(s, duck: 0.7, duckSec: 0.15);
    }

    /// <summary>ドラムンベース 174 BPM: 1 拍目と 3 拍目の裏にキック、2・4 拍目にスネア（テンポの追跡は 87 BPM でつかむことが多い）</summary>
    public static MixSong Dnb()
    {
        double bpm = 174, p = 60 / bpm, bar = 4 * p, t0 = 0.5;
        int bars = 32;
        var m = new Mix(t0 + bars * bar + 1, 61);
        var s = new MixSong { Name = "dnb174", Bpm = bpm };
        double[] notes = { 41.2, 41.2, 49.0, 36.7 };
        for (int b = 0; b < bars; b++)
        {
            string sec = b < 16 ? "a" : "b";
            double ka = sec == "a" ? 0.55 : 0.9;
            for (int k = 0; k < 4; k++)
            {
                double t = t0 + b * bar + k * p, kick = 0, sn = 0;
                if (k == 0) kick = ka;
                if (k == 2) m.K(t + p / 2, ka * 0.8);
                if (k == 1 || k == 3) sn = sec == "a" ? 0.4 : 0.55;
                if (k == 1) m.Dr.Snare(t + 0.75 * p, 0.1);
                m.Dr.Hat(t, 0.03); m.Dr.Hat(t + p / 2, 0.05);
                if (kick > 0) m.K(t, kick);
                if (sn > 0) m.Dr.Snare(t, sn);
                if (k == 0 && (b == 16 || b == 24)) Crash(m.Dr, t, 0.4);
                s.Beats.Add(new MixBeat(t, b, k, kick, sn, true, sec));
            }
            double f = notes[(b / 2) % 4];
            if (sec == "b") Saw(m.Mu, f, 0.08, t0 + b * bar + 0.01, t0 + (b + 1) * bar - 0.01);
            else m.Mu.Tone(f, 0.1, t0 + b * bar + 0.01, t0 + (b + 1) * bar - 0.01);
            m.Mu.PadChord(t0 + b * bar, t0 + (b + 1) * bar, Prog[(b / 4) % 4], 0.025, seed: b);
        }
        return m.Finish(s, duck: 0.3);
    }
}

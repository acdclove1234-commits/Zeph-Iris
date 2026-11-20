// ==================================================
// BeatHueMusicTests.cs - 「ビートで色替え」が、決まった間隔の点滅ではなく、曲のドラム・区切りに合わせて光ることを確かめる
//   （合成した曲: ハウス・ロック・ヒップホップ。どれもサイドチェイン・バスのコンプレッサーとリミッターをかけた、実際の曲に近い混ぜ方。
//    アプリと同じ流れ: 本物の分析に 10 ミリ秒ずつ流し、60 コマ／秒で描き、RgbDeviceChannel の送る・送らない・デバイスの遅れまで通す。
//    拍の AI の代わりに、曲の本当の拍を返す）
//   調べる用: BHD=1 で 6 曲 × 6 デバイスの表を出す（BHD_OUT=ファイル、BHD_SONGS=house124,…、BHD_DEV=strip,…、BHD_AI=1 で本物の拍の AI、
//            BHD_OLD=1 で 6.15.10 までの光り方、BHD_TUNE=名前=値,…、BHD_DUMP=デバイス:曲 で拍ごとの記録、BHD_EV=1 で出来事の記録）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Text;
using ProFanController;

namespace LogicTests;

public static class BeatHueMusicTests
{
    private const double Tick = 1.0 / 60, T0 = 1000, Skip = 8;
    private const int Fs = 48000;
    // バスのコンプレッサー（しきい値 dBFS・比）
    private const double CompThrDb = -16, CompRatio = 3;

    private sealed record Beat(double T, int Bar, int InBar, double Kick, double Snare, bool Crash, bool SecStart, bool Drums, string Sec);

    private sealed class Song
    {
        public required string Name;
        public required double Bpm;
        public required float[] Buf;
        public readonly List<Beat> Beats = new();
        public string[] Loud = Array.Empty<string>(), Quiet = Array.Empty<string>();
    }

    private sealed class Lane
    {
        public required string Dev;
        public required bool Rf;
        public required RgbDeviceChannel Ch;
        public Rgb[] Seen = Array.Empty<Rgb>();
        public readonly List<double> A = new();
        public readonly List<Rgb> C = new();
        public readonly List<double> Steps = new();
        public double LastStep = double.NegativeInfinity;
        public double LatSec;
        public readonly List<double> SendT = new();
        /// <summary>送る間隔の中央値（秒）</summary>
        public double Gap => SendT.Count < 3 ? Tick : SendT.Zip(SendT.Skip(1), (a, b) => b - a).OrderBy(x => x).ElementAt((SendT.Count - 1) / 2);
    }

    // ===== 曲を作る

    private sealed class Mix
    {
        public readonly SignalGen Dr, Mu;
        public readonly List<double> Kicks = new();
        public Mix(double sec, int seed) { Dr = new SignalGen(Fs, sec, seed); Mu = new SignalGen(Fs, sec, seed + 100); }
        public void K(double t, double a) { Dr.Kick(t, a); Kicks.Add(t); }

        /// <summary>音楽の層をキックでダッキング（サイドチェイン）して足し、バスのコンプレッサーをかける</summary>
        public float[] Render(double duck = 0.6, double duckSec = 0.12)
        {
            int n = Dr.Buf.Length;
            var o = new float[n];
            var ks = Kicks.OrderBy(x => x).ToArray();
            int ki = 0;
            double lastK = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Fs;
                while (ki < ks.Length && ks[ki] <= t) lastK = ks[ki++];
                double d = 1 - duck * Math.Exp(-(t - lastK) / duckSec);
                o[i] = (float)(Dr.Buf[i] + Mu.Buf[i] * d);
            }
            // コンプレッサー（-18 dBFS から 4:1、アタック 5 ms・リリース 120 ms）＋ やわらかいリミッター
            double env = 0, aA = 1 - Math.Exp(-1 / (0.005 * Fs)), aR = 1 - Math.Exp(-1 / (0.12 * Fs));
            double thr = Math.Pow(10, CompThrDb / 20.0);
            for (int i = 0; i < n; i++)
            {
                double x = Math.Abs(o[i]);
                env += (x > env ? aA : aR) * (x - env);
                double g = env > thr ? Math.Pow(env / thr, 1 / CompRatio - 1) : 1;
                o[i] = (float)(Math.Tanh(1.6 * o[i] * g) / 1.6 * 1.4);
            }
            return o;
        }
    }

    private static void Crash(SignalGen g, double t0, double amp)
    {
        var r = new Random((int)(t0 * 1000));
        int s0 = (int)(t0 * Fs), n = (int)(1.8 * Fs);
        double p1 = 0, p2 = 0;
        for (int i = 0; i < n && s0 + i < g.Buf.Length; i++)
        {
            double w = r.NextDouble() * 2 - 1;
            double hp = w - p1 - (p1 - p2) * 0.0; p2 = p1; p1 = w;
            double env = Math.Exp(-i / (0.7 * Fs)) * Math.Min(1, i / (0.001 * Fs));
            g.Buf[s0 + i] = (float)Math.Clamp(g.Buf[s0 + i] + amp * env * hp * 0.5, -1, 1);
        }
    }

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

    private static Song House()
    {
        double bpm = 124, p = 60 / bpm, bar = 4 * p, t0 = 0.5;
        int bars = 28;
        var m = new Mix(t0 + bars * bar + 1, 11);
        var s = new Song { Name = "house124", Bpm = bpm, Buf = Array.Empty<float>(), Loud = new[] { "drop" }, Quiet = new[] { "intro" } };
        for (int b = 0; b < bars; b++)
        {
            string sec = b < 8 ? "intro" : b < 16 ? "verse" : b < 20 ? "break" : "drop";
            double kick = sec switch { "intro" => 0.45, "verse" => 0.6, "break" => 0, _ => 0.9 };
            var ch = Prog[(b / 2) % 4];
            m.Mu.PadChord(t0 + b * bar, t0 + (b + 1) * bar, ch, sec == "drop" ? 0.05 : 0.035, seed: b);
            for (int k = 0; k < 4; k++)
            {
                double t = t0 + b * bar + k * p;
                double sn = 0;
                bool crash = sec == "drop" && k == 0 && (b == 20 || b == 24);
                if (kick > 0) m.K(t, kick);
                if (sec != "intro" && sec != "break" && (k == 1 || k == 3)) { sn = sec == "drop" ? 0.45 : 0.3; m.Dr.Snare(t, sn); }
                if (sec == "break" && b == 19) for (int q = 0; q < 4; q++) m.Dr.Snare(t + q * p / 4, 0.1 + 0.08 * (k * 4 + q) / 4.0);
                if (sec != "break") { m.Dr.Hat(t + p / 2, sec == "drop" ? 0.09 : 0.05); m.Dr.Hat(t + p / 4, 0.02); m.Dr.Hat(t + 3 * p / 4, 0.02); }
                if (crash) Crash(m.Dr, t, 0.35);
                if (sec is "verse" or "drop") { m.Mu.Tone(ch[0] / 4, 0.12, t + p / 2, t + p - 0.02); m.Mu.Tone(ch[0] / 2, 0.05, t + p / 2, t + p - 0.02); }
                s.Beats.Add(new Beat(t, b, k, kick, sn, crash, k == 0 && (b == 0 || b == 8 || b == 16 || b == 20), kick > 0, sec));
            }
            if (sec is "break" or "drop") m.Mu.VocalTone(t0 + b * bar, t0 + b * bar + bar - 0.1, new[] { 440.0, 392, 349.2, 392 }, p, amp: 0.06);
        }
        s.Buf = m.Render();
        return s;
    }

    private static Song Rock()
    {
        double bpm = 120, p = 60 / bpm, bar = 4 * p, t0 = 0.5;
        int bars = 24;
        var m = new Mix(t0 + bars * bar + 1, 21);
        var s = new Song { Name = "rock120", Bpm = bpm, Buf = Array.Empty<float>(), Loud = new[] { "chorus" }, Quiet = new[] { "verse" } };
        double[] roots = { 82.4, 110, 98, 73.4 };
        for (int b = 0; b < bars; b++)
        {
            string sec = b < 8 ? "verse" : b < 10 ? "pre" : b < 18 ? "chorus" : "verse";
            double root = roots[(b / 2) % 4];
            for (int k = 0; k < 4; k++)
            {
                double t = t0 + b * bar + k * p, kick = 0, sn = 0;
                bool crash = sec == "chorus" && k == 0 && (b == 10 || b == 14);
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
                    sn = 0.2 + 0.3 * u; m.Dr.Snare(t + p / 2, sn);
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
                if (crash) Crash(m.Dr, t, 0.4);
                m.Mu.Tone(root, sec == "chorus" ? 0.12 : 0.08, t + 0.01, t + p - 0.02);
                s.Beats.Add(new Beat(t, b, k, kick, sn, crash, k == 0 && (b == 0 || b == 8 || b == 10 || b == 18), true, sec));
            }
            if (sec == "chorus") m.Mu.VocalTone(t0 + b * bar, t0 + (b + 1) * bar - 0.1, new[] { 329.6, 392, 440, 392 }, p, amp: 0.07);
        }
        s.Buf = m.Render(duck: 0.15);
        return s;
    }

    private static Song HipHop()
    {
        double bpm = 88, p = 60 / bpm, bar = 4 * p, t0 = 0.5;
        int bars = 16;
        var m = new Mix(t0 + bars * bar + 1, 31);
        var s = new Song { Name = "hiphop88", Bpm = bpm, Buf = Array.Empty<float>(), Loud = new[] { "b" }, Quiet = new[] { "a" } };
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
                s.Beats.Add(new Beat(t, b, k, kick, sn, false, k == 0 && (b == 0 || b == 8), true, sec));
                // ラップ: 16 分の短い声
                for (int q = 0; q < 4; q++) if (rng.NextDouble() < 0.6) m.Mu.VocalTone(t + q * p / 4, t + q * p / 4 + p / 4 - 0.02, new[] { 180.0 + 30 * rng.NextDouble() }, p / 4, amp: 0.05, vibratoCents: 5);
            }
            m.Mu.PadChord(t0 + b * bar, t0 + (b + 1) * bar, Prog[(b / 2) % 4], 0.02, seed: b);
        }
        s.Buf = m.Render(duck: 0.2);
        return s;
    }

    private static Song Ballad()
    {
        double bpm = 72, p = 60 / bpm, bar = 4 * p, t0 = 0.5;
        int bars = 14;
        var m = new Mix(t0 + bars * bar + 1, 41);
        var s = new Song { Name = "ballad72", Bpm = bpm, Buf = Array.Empty<float>(), Loud = new[] { "band" }, Quiet = new[] { "piano" } };
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
                s.Beats.Add(new Beat(t, b, k, kick, sn, false, k == 0 && (b == 0 || b == 6), sec == "band", sec));
            }
            m.Mu.VocalTone(t0 + b * bar + 0.2, t0 + (b + 1) * bar - 0.2, new[] { 440.0, 493.9, 523.3, 493.9 }, p, amp: 0.06);
            if (sec == "band") m.Mu.Tone(ch[0] / 2, 0.08, t0 + b * bar, t0 + (b + 1) * bar - 0.02);
        }
        s.Buf = m.Render(duck: 0.1);
        return s;
    }

    private static Song Edm()
    {
        double bpm = 128, p = 60 / bpm, bar = 4 * p, t0 = 0.5;
        int bars = 24;
        var m = new Mix(t0 + bars * bar + 1, 51);
        var s = new Song { Name = "edm128", Bpm = bpm, Buf = Array.Empty<float>(), Loud = new[] { "drop2" }, Quiet = new[] { "build" } };
        for (int b = 0; b < bars; b++)
        {
            string sec = b < 8 ? "drop1" : b < 16 ? "build" : "drop2";
            var ch = Prog[(b / 2) % 4];
            for (int k = 0; k < 4; k++)
            {
                double t = t0 + b * bar + k * p, kick = 0, sn = 0;
                bool crash = k == 0 && (b == 0 || b == 4 || b == 16 || b == 20);
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
                if (crash) Crash(m.Dr, t, 0.4);
                s.Beats.Add(new Beat(t, b, k, kick, sn, crash, k == 0 && (b == 0 || b == 8 || b == 16), sec != "build", sec));
            }
            m.Mu.PadChord(t0 + b * bar, t0 + (b + 1) * bar, ch, sec == "build" ? 0.04 : 0.05, seed: b);
        }
        Riser(m.Mu, t0 + 8 * bar, t0 + 16 * bar, 0.25);
        s.Buf = m.Render(duck: 0.7, duckSec: 0.15);
        return s;
    }

    private static Song Dnb()
    {
        double bpm = 174, p = 60 / bpm, bar = 4 * p, t0 = 0.5;
        int bars = 32;
        var m = new Mix(t0 + bars * bar + 1, 61);
        var s = new Song { Name = "dnb174", Bpm = bpm, Buf = Array.Empty<float>(), Loud = new[] { "b" }, Quiet = new[] { "a" } };
        double[] notes = { 41.2, 41.2, 49.0, 36.7 };
        for (int b = 0; b < bars; b++)
        {
            string sec = b < 16 ? "a" : "b";
            double ka = sec == "a" ? 0.55 : 0.9;
            for (int k = 0; k < 4; k++)
            {
                double t = t0 + b * bar + k * p, kick = 0, sn = 0;
                bool crash = k == 0 && (b == 16 || b == 24);
                if (k == 0) kick = ka;
                if (k == 2) m.K(t + p / 2, ka * 0.8);
                if (k == 1 || k == 3) sn = sec == "a" ? 0.4 : 0.55;
                if (k == 1) m.Dr.Snare(t + 0.75 * p, 0.1);
                m.Dr.Hat(t, 0.03); m.Dr.Hat(t + p / 2, 0.05);
                if (kick > 0) m.K(t, kick);
                if (sn > 0) m.Dr.Snare(t, sn);
                if (crash) Crash(m.Dr, t, 0.4);
                s.Beats.Add(new Beat(t, b, k, kick, sn, crash, k == 0 && (b == 0 || b == 16), true, sec));
            }
            double f = notes[(b / 2) % 4];
            if (sec == "b") { Saw(m.Mu, f, 0.08, t0 + b * bar + 0.01, t0 + (b + 1) * bar - 0.01); }
            else m.Mu.Tone(f, 0.1, t0 + b * bar + 0.01, t0 + (b + 1) * bar - 0.01);
            m.Mu.PadChord(t0 + b * bar, t0 + (b + 1) * bar, Prog[(b / 4) % 4], 0.025, seed: b);
        }
        s.Buf = m.Render(duck: 0.3);
        return s;
    }

    // ===== 流す

    private static RgbDevice DeviceOf(string name) => name switch
    {
        "strip" => new RgbDevice(0, "Strip", 24, 4, null, "HID: x"),
        "zone4" => new RgbDevice(1, "Small", 4, 4, null, "HID: x"),
        "led1" => new RgbDevice(2, "One", 1, 4, null, "HID: x"),
        "direct" => new RgbDevice(3, "GpuDirect", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }) { DirectGpu = true },
        "dram" => new RgbDevice(5, "DRAM", 8, RgbDevice.TypeDram, null, "I2C: dram"),
        "gpu" => new RgbDevice(6, "GPU", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }),
        _ => throw new ArgumentException(name),
    };

    private static readonly Lazy<BeatThis?> Model = new(() => BeatThis.TryCreate(out _));

    /// <summary>
    /// 曲を流して、デバイス devs × 「激しい点滅を抑える」オフ・オン（rfs）を描く。realAi なら本物の拍の AI、そうでなければ曲の本当の拍を返す代わりの AI
    /// </summary>
    private static (List<Lane> Lanes, List<BeatLock> Lock) Play(Song s, string[] devs, string intensity, bool realAi, bool[]? rfs = null)
    {
        var baseCfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyIntensity(baseCfg.Music, intensity);
        var engine = new AudioEngine(Fs) { Settings = MusicTuning.EngineSettings(baseCfg) };
        double audioT = T0;
        BeatAssist? assist;
        if (realAi) assist = Model.Value is { } md ? new BeatAssist(md, Fs) { Inline = true } : null;
        else
        {
            // 窓の終わり = いま流し終えた音の時刻（Inline なので、Push の中で調べる）
            var beats = s.Beats.Select(b => T0 + b.T).ToList();
            assist = new BeatAssist((a, n) =>
            {
                double end = audioT, start = end - n / (double)BeatThis.SampleRate;
                return (beats.Where(t => t >= start && t <= end).Select(t => t - start).ToList(), new List<double>());
            }, engine.AnalysisRate) { Inline = true };
        }
        if (assist is not null) engine.Assist = assist;
        var lanes = new List<Lane>();
        foreach (var dn in devs)
            foreach (bool rf in rfs ?? new[] { false, true })
            {
                var dev = DeviceOf(dn);
                var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = Environment.GetEnvironmentVariable("BHD_STYLE") ?? MusicStyles.BeatHue, Brightness = 100 };
                MusicTuning.ApplyIntensity(cfg.Music, intensity);
                cfg.Music.ReduceFlashing = rf;
                var ch = new RgbDeviceChannel(dev, T0) { SharedColor = new SharedColor() };
                ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = dev.Name }, false);
                lanes.Add(new Lane { Dev = dn, Rf = rf, Ch = ch, Seen = new Rgb[dev.LedCount], LatSec = ch.EffectiveLatencyMs / 1000 });
            }
        var locks = new List<BeatLock>();
        bool evDump = Environment.GetEnvironmentVariable("BHD_EV") == "1";
        long evCur = 0;
        var evList = new List<AudioEvent>();
        var buf = s.Buf;
        int pos = 0, chunk = Fs / 100;
        for (int k = 0; ; k++)
        {
            double sec = k * Tick;
            if (sec + 0.15 > buf.Length / (double)Fs) break;
            while (pos < buf.Length && pos / (double)Fs < sec + 0.1)
            {
                int m = Math.Min(chunk, buf.Length - pos);
                audioT = T0 + (pos + m) / (double)Fs;
                engine.Push(new ReadOnlySpan<float>(buf, pos, m), T0 + pos / (double)Fs);
                pos += m;
            }
            double t = T0 + sec;
            var beat = engine.Beat;
            locks.Add(engine.BeatLock);
            if (evDump)
            {
                evList.Clear();
                engine.Events.Read(ref evCur, t, evList);
                foreach (var ev in evList)
                {
                    var fr = new AudioFrame();
                    engine.Features.Sample(ev.T + 0.03, fr);
                    Console.WriteLine($"EV {ev.Kind,-6} {ev.T - T0:F3} s {ev.Strength:F2} ldb {fr.LoudDb:F1} bassE {fr.BassE:F2} kenv {fr.KickEnv:F2} percL {fr.PercLow:F2} percM {fr.PercMid:F2} percH {fr.PercHigh:F2} trE {fr.TrebleE:F2}");
                }
            }
            foreach (var L in lanes)
            {
                var ch = L.Ch;
                ch.Prepare(t, Tick, engine.Features, engine.Events, beat, null, false, false);
                bool drawn = ch.Render();
                if (drawn)
                {
                    var reason = ch.ShouldSend(t, Tick, true);
                    if (reason != SendReason.None)
                    {
                        ch.MarkSent(t, Tick, reason, 1);
                        L.Seen = (Rgb[])ch.Frame.Clone();
                        L.SendT.Add(sec);
                    }
                }
                var sc = ch.SharedColor!; double st = double.IsNaN(sc.FadeEndT) ? sc.LastStepT : sc.FadeEndT;
                if (st != L.LastStep && !double.IsInfinity(st)) { L.Steps.Add(st - T0); L.LastStep = st; }
                double sum = 0;
                foreach (var q in L.Seen) sum += Math.Max(q.R, Math.Max(q.G, q.B)) / 255.0;
                L.A.Add(sum / Math.Max(1, L.Seen.Length));
                L.C.Add(L.Seen.Length > 0 ? L.Seen[L.Seen.Length / 2] : default);
            }
        }
        assist?.Dispose();
        return (lanes, locks);
    }

    // ===== 測る

    /// <summary>見えている明るさ（LED の値の平均を、見た目の明るさにする: 1/2.2 乗）。時刻は音の時刻（送ってから光るまでの遅れを足す）</summary>
    private static double PAt(Lane l, double audioT)
    {
        int i = (int)Math.Round((audioT - l.LatSec) * 60);
        if (i < 0 || i >= l.A.Count) return 0;
        return Math.Pow(l.A[i], 1 / 2.2);
    }

    private static double MaxP(Lane l, double a, double b) { double m = 0; for (double t = a; t <= b; t += Tick) m = Math.Max(m, PAt(l, t)); return m; }
    private static double MinP(Lane l, double a, double b) { double m = 9; for (double t = a; t <= b; t += Tick) m = Math.Min(m, PAt(l, t)); return m >= 9 ? 0 : m; }
    private static double ArgMaxP(Lane l, double a, double b) { double m = -1, at = a; for (double t = a; t <= b; t += Tick) { double v = PAt(l, t); if (v > m) { m = v; at = t; } } return at; }

    private static double Spearman(IList<double> x, IList<double> y)
    {
        if (x.Count < 4) return double.NaN;
        double[] R(IList<double> v)
        {
            var idx = Enumerable.Range(0, v.Count).OrderBy(i => v[i]).ToArray();
            var r = new double[v.Count];
            for (int i = 0; i < idx.Length;)
            {
                int j = i;
                while (j + 1 < idx.Length && v[idx[j + 1]] == v[idx[i]]) j++;
                for (int q = i; q <= j; q++) r[idx[q]] = (i + j) / 2.0;
                i = j + 1;
            }
            return r;
        }
        var a = R(x); var b = R(y);
        double ma = a.Average(), mb = b.Average(), num = 0, da = 0, db = 0;
        for (int i = 0; i < a.Length; i++) { num += (a[i] - ma) * (b[i] - mb); da += (a[i] - ma) * (a[i] - ma); db += (b[i] - mb) * (b[i] - mb); }
        return da <= 0 || db <= 0 ? double.NaN : num / Math.Sqrt(da * db);
    }

    /// <summary>見て分かる光り始め（見た目の明るさが 0.1 秒以内に 0.12 以上上がった始まり、0.1 秒以上あける）</summary>
    private static List<double> Flashes(Lane l)
    {
        var res = new List<double>();
        int last = -1000; bool run = false;
        for (int i = 1; i < l.A.Count; i++)
        {
            double v = Math.Pow(l.A[i], 1 / 2.2), lo = v;
            for (int j = Math.Max(0, i - 6); j < i; j++) lo = Math.Min(lo, Math.Pow(l.A[j], 1 / 2.2));
            bool up = v - lo >= 0.12;
            if (up && !run && i - last >= 6) { res.Add(i / 60.0 + l.LatSec); last = i; }
            run = up;
        }
        return res;
    }

    private static int StrongMax(Lane l)
    {
        var st = new List<double>(); bool run = false;
        for (int i = 1; i < l.A.Count; i++)
        {
            double lo = l.A[i];
            for (int j = Math.Max(0, i - 6); j < i; j++) lo = Math.Min(lo, l.A[j]);
            bool up = l.A[i] - lo >= FlashGuard.StrongRise - 1e-4f;
            if (up && !run && i / 60.0 > Skip) st.Add(i / 60.0);
            run = up;
        }
        int best = 0;
        for (int i = 0; i < st.Count; i++) { int c = 0; for (int j = i; j < st.Count && st[j] - st[i] < 1; j++) c++; best = Math.Max(best, c); }
        return best;
    }

    private static string F(double v, string f = "F2") => double.IsNaN(v) ? "  -  " : v.ToString(f);

    /// <summary>
    /// 測った値（見た目の明るさ = LED の値の 1/2.2 乗。光の高さ = 拍の直後のいちばん明るい値 − 拍の前のいちばん暗い値）:
    ///   On = 光り始めのうち拍に乗った割合、LitK = キックの拍で光り始めた割合、LatMs = キックの拍からいちばん明るくなるまで（中央値）、
    ///   HK = キックの拍の光の高さ、NoK・SnO・ND = ドラムの鳴っている曲で何もない拍・スネアだけの拍・ドラムのないところの拍の、キックの拍とくらべた高さ、
    ///   Steps = 色を進めた回数、Down = そのうち小節の頭の割合、BreakSteps = ドラムのないところで進めた回数、
    ///   SecHit / SecN = 区切り（区間の頭・クラッシュ）で色を進めた数、SMax = 強い光り始めのどの 1 秒の間でもの最大
    /// </summary>
    private sealed record Result(double On, double LitK, double LatMs, double HK, double NoK, double SnO, double ND, int Steps, double Down,
        int BreakSteps, int SecHit, int SecN, int SMax, string Line, double LoudQuietRatio);

    private static Result Score(Song s, Lane l, List<BeatLock> locks)
    {
        double p = 60 / s.Bpm;
        var bs = s.Beats.Where(b => b.T >= Skip && b.T < s.Buf.Length / (double)Fs - 1).ToList();
        double g = Math.Max(0, l.Gap - Tick);
        double H(Beat b) => Math.Max(0, MaxP(l, b.T - 0.04, b.T + 0.16 + g) - MinP(l, b.T - Math.Max(0.12, 0.35 * p) - g, b.T - 0.05));
        var kb = bs.Where(b => b.Kick > 0).ToList();
        var nk = bs.Where(b => b.Kick == 0 && b.Snare == 0 && b.Drums).ToList();
        var so = bs.Where(b => b.Kick == 0 && b.Snare > 0 && b.Drums).ToList();
        var nd = bs.Where(b => !b.Drums).ToList();
        double hk = kb.Count > 0 ? kb.Average(H) : double.NaN;
        double rho = Spearman(kb.Select(b => b.Kick + (b.Crash ? 0.2 : 0)).ToList(), kb.Select(H).ToList());
        var hall = bs.Where(b => b.Drums).Select(H).ToList();
        double cv = hall.Count > 1 ? Math.Sqrt(hall.Select(h => (h - hall.Average()) * (h - hall.Average())).Average()) / Math.Max(1e-6, hall.Average()) : double.NaN;
        double noK = nk.Count > 0 ? nk.Average(H) / hk : double.NaN;
        double snO = so.Count > 0 ? so.Average(H) / hk : double.NaN;
        var loud = kb.Where(b => s.Loud.Contains(b.Sec)).ToList(); var quiet = bs.Where(b => s.Quiet.Contains(b.Sec)).ToList();
        double lq = loud.Count > 0 && quiet.Count > 0 ? loud.Average(H) / Math.Max(0.01, quiet.Average(H)) : double.NaN;
        // 光り始めが拍（キック・スネアの時刻）に乗っているか
        var fl = Flashes(l).Where(t => t >= Skip).ToList();
        var hits = s.Beats.Where(b => b.Kick > 0 || b.Snare > 0).Select(b => b.T).ToList();
        double on = fl.Count > 0 ? fl.Count(t => s.Beats.Any(b => Math.Abs(b.T - t) <= 0.07)) / (double)fl.Count : double.NaN;
        double litK = kb.Count > 0 ? kb.Count(b => fl.Any(t => Math.Abs(t - b.T) <= 0.07)) / (double)kb.Count : double.NaN;
        // 拍に乗った光の遅れ（キックの拍から、見た目の明るさがいちばん明るくなるまで）
        var lat = kb.Select(b => ArgMaxP(l, b.T - 0.05, b.T + 0.2) - b.T).OrderBy(x => x).ToList();
        double latMs = lat.Count > 0 ? 1000 * lat[lat.Count / 2] : double.NaN;
        // 色の切り替え
        var st = l.Steps.Where(t => t >= Skip).ToList();
        var gaps = st.Zip(st.Skip(1), (a, b) => b - a).ToList();
        double scv = gaps.Count > 1 ? Math.Sqrt(gaps.Select(g => (g - gaps.Average()) * (g - gaps.Average())).Average()) / gaps.Average() : double.NaN;
        Beat? Near(double t) => bs.Where(b => Math.Abs(b.T - t) <= 0.09).OrderBy(b => Math.Abs(b.T - t)).FirstOrDefault();
        int onDown = st.Count(t => Near(t) is { InBar: 0 });
        int onAcc = st.Count(t => Near(t) is { } b && (b.Crash || b.SecStart || (b.Kick >= 0.8 && b.InBar == 0)));
        int noHit = st.Count(t => Near(t) is { } b ? b.Kick == 0 && b.Snare == 0 : true);
        var secStarts = bs.Where(b => b.SecStart || b.Crash).ToList();
        int secHit = secStarts.Count(b => st.Any(t => Math.Abs(t - b.T) <= 0.09));
        int inNd = st.Count(t => Near(t) is { Drums: false });
        // 色を進めた拍で、光のいちばん明るいところの色が、前の拍の色に近い（新しい色で光っていない）回数
        (double, double, double)? ColAt(double audioT)
        {
            int i = (int)Math.Round((audioT - l.LatSec) * 60);
            if (i < 0 || i >= l.C.Count) return null;
            var q = l.C[i]; double mx = Math.Max(q.R, Math.Max(q.G, q.B));
            return mx < 8 ? null : (q.R / mx, q.G / mx, q.B / mx);
        }
        double Dist((double, double, double) a, (double, double, double) b) => Math.Abs(a.Item1 - b.Item1) + Math.Abs(a.Item2 - b.Item2) + Math.Abs(a.Item3 - b.Item3);
        int lateN = 0, lateOf = 0;
        foreach (var t in st)
        {
            if (Near(t) is not { } b) continue;
            var pk = ColAt(ArgMaxP(l, b.T - 0.04, b.T + 0.16));
            var old = ColAt(ArgMaxP(l, b.T - p - 0.04, b.T - p + 0.16));
            var nw = ColAt(ArgMaxP(l, b.T + p - 0.04, b.T + p + 0.16));
            if (pk is null || old is null || nw is null || Dist(old.Value, nw.Value) < 0.2) continue;
            lateOf++;
            if (Dist(pk.Value, old.Value) < Dist(pk.Value, nw.Value)) lateN++;
        }
        // テンポをつかむまで
        int li = locks.FindIndex(x => x == BeatLock.Locked);
        double lockT = li < 0 ? double.NaN : li / 60.0;
        double lockedShare = locks.Skip((int)(Skip * 60)).Count(x => x != BeatLock.Unlocked) / (double)Math.Max(1, locks.Count - Skip * 60);
        double meanQ = quiet.Count > 0 ? quiet.Average(b => MaxP(l, b.T, b.T + p * 0.9)) : double.NaN;
        int sMax = StrongMax(l);
        string line = $"{s.Name,-9} {l.Dev,-6} RF{(l.Rf ? "+" : "-")} lock {F(lockT, "F1"),4}s {F(lockedShare * 100, "F0"),3}% | on {F(on * 100, "F0"),3}% litK {F(litK * 100, "F0"),3}% lat {F(latMs, "F0"),4} | " +
               $"hK {F(hk)} rho {F(rho)} cv {F(cv)} noK {F(noK)} snO {F(snO)} nD {F(nd.Count > 0 ? nd.Average(H) / hk : double.NaN)} L/Q {F(lq)} | steps {st.Count,3} cv {F(scv)} down {F(100.0 * onDown / Math.Max(1, st.Count), "F0"),3}% acc {F(100.0 * onAcc / Math.Max(1, st.Count), "F0"),3}% noHit {noHit,2} nd {inNd,2} sec {secHit}/{secStarts.Count} late {lateN}/{lateOf} | sMax {sMax}";
        double ndRel = nd.Count > 0 ? nd.Average(H) / hk : double.NaN;
        return new Result(on, litK, latMs, hk, noK, snO, ndRel, st.Count, st.Count > 0 ? onDown / (double)st.Count : double.NaN, inNd, secHit, secStarts.Count, sMax, line, lq);
    }

    public static void TestBeatHueDiag()
    {
        if (Environment.GetEnvironmentVariable("BHD") != "1") { T.Skip("BHD=1 のときだけ"); return; }
        var saved = BeatTuneSweepTests.Snapshot();
        try
        {
            if (Environment.GetEnvironmentVariable("BHD_OLD") == "1") { BeatFlash.Tune.HitDriven = false; BeatDriver.HitSteps = false; BeatHueEffect.Tune.SlowBeatOnBed = false; }
            BeatTuneSweepTests.Apply(Environment.GetEnvironmentVariable("BHD_TUNE")?.Replace(',', ';') ?? "");
            Diag();
        }
        finally { BeatTuneSweepTests.Restore(saved); }
    }

    private static void Diag()
    {
        string intensity = Environment.GetEnvironmentVariable("BHD_INT") ?? Intensities.Moderate;
        bool ai = Environment.GetEnvironmentVariable("BHD_AI") == "1";
        string[] devs = (Environment.GetEnvironmentVariable("BHD_DEV") ?? "strip,zone4,led1,direct,dram,gpu").Split(',');
        var only = Environment.GetEnvironmentVariable("BHD_SONGS")?.Split(',');
        var songs = new Func<Song>[] { House, Rock, HipHop, Ballad, Edm, Dnb };
        var sb = new StringBuilder();
        foreach (var mk in songs)
        {
            var s = mk();
            if (only is not null && !only.Contains(s.Name)) continue;
            var (lanes, locks) = Play(s, devs, intensity, ai);
            foreach (var l in lanes) { string line = Score(s, l, locks).Line; Console.WriteLine(line); sb.AppendLine(line); }
            if (Environment.GetEnvironmentVariable("BHD_DUMP") is { } dd && dd.Split(':') is [var dn, var sname] && sname == s.Name)
            {
                var l = lanes.First(x => x.Dev == dn && !x.Rf);
                double p = 60 / s.Bpm;
                foreach (var b in s.Beats.Where(b => b.T >= Skip))
                {
                    double h = Math.Max(0, MaxP(l, b.T - 0.04, b.T + 0.16) - MinP(l, b.T - Math.Max(0.12, 0.35 * p), b.T - 0.05));
                    Console.WriteLine($"  {b.T,7:F3} bar{b.Bar,2}.{b.InBar} {b.Sec,-6} k{b.Kick:F2} s{b.Snare:F2}{(b.Crash ? " C" : "  ")} h {h:F2} pk {MaxP(l, b.T - 0.04, b.T + 0.16):F2} step {(l.Steps.Any(t => Math.Abs(t - b.T) < 0.09) ? "*" : " ")} lock {locks[Math.Min(locks.Count - 1, (int)(b.T * 60))]}");
                }
            }
        }
        if (Environment.GetEnvironmentVariable("BHD_OUT") is { } o) File.WriteAllText(o, sb.ToString());
        T.Info("done");
    }

    public static void TestBeatHueRespondsToTimbreBetweenColourSteps()
    {
        var c = new EffectContext { P = new[] { 0.5f }, F0 = 1, Dt = 1f / 60,
            Music = ResolvedMusic.Default with { Effect = MusicStyles.BeatHue, TempoMode = TempoModes.Auto } };
        var effect = new BeatHueEffect(followBeat: true);
        effect.Reset(c);
        LinColor Render(float bass, float treble)
        {
            c.Frame.BassE = bass;
            c.Frame.TrebleE = treble;
            for (int frame = 0; frame < 120; frame++)
            {
                c.TPrev = c.T;
                c.T += c.Dt;
                effect.Update(c);
            }
            var pixel = new LinColor[1];
            effect.Render(c, new LedSegment(0, 1, false, false), pixel);
            return pixel[0];
        }
        var low = Render(0.9f, 0.1f);
        var high = Render(0.1f, 0.9f);
        float distance = Math.Max(Math.Abs(low.R - high.R), Math.Max(Math.Abs(low.G - high.G), Math.Abs(low.B - high.B)));
        T.Check(distance > 0.2f, $"with no beat or brightness change, bass and treble phrases produce distinct colours (RGB difference {distance:F2})");
    }

    // ===== テスト（ハウス・ロック・ヒップホップ。メモリ（1 秒に 8 回）とテープ（60 回）、「激しい点滅を抑える」オフ・オン）

    private sealed record Run(Song S, List<Lane> Lanes, List<BeatLock> Lock)
    {
        public Lane L(string dev, bool rf) => Lanes.First(x => x.Dev == dev && x.Rf == rf);
        public Result R(string dev, bool rf = false) => Score(S, L(dev, rf), Lock);
    }

    private static readonly string[] TestDevs = { "strip", "dram" };

    /// <summary>今の光り方（old = false）と 6.15.10 までの光り方（old = true）で、3 曲を 1 回ずつ流した結果（テストどうしで使い回す）</summary>
    private static readonly Lazy<Run[]> NewRuns = new(() => PlayAll(false)), OldRuns = new(() => PlayAll(true));

    private static Run[] PlayAll(bool old)
    {
        bool hd = BeatFlash.Tune.HitDriven, hs = BeatDriver.HitSteps, sb = BeatHueEffect.Tune.SlowBeatOnBed;
        if (old) { BeatFlash.Tune.HitDriven = false; BeatDriver.HitSteps = false; BeatHueEffect.Tune.SlowBeatOnBed = false; }
        try
        {
            return new Func<Song>[] { House, Rock, HipHop }.Select(mk =>
            {
                var s = mk();
                var (lanes, locks) = Play(s, TestDevs, Intensities.Moderate, realAi: false);
                return new Run(s, lanes, locks);
            }).ToArray();
        }
        finally { BeatFlash.Tune.HitDriven = hd; BeatDriver.HitSteps = hs; BeatHueEffect.Tune.SlowBeatOnBed = sb; }
    }

    private static Run Get(bool old, string name) => (old ? OldRuns : NewRuns).Value.First(r => r.S.Name == name);

    /// <summary>
    /// 光の高さは、その拍で鳴ったドラムで決まる（「決まった間隔で点滅しているだけ」という声があった）:
    /// ヒップホップのキックもスネアもない拍は、キックの拍の 0.3 倍以下（6.15.10 は 0.46 倍）、ロックのスネアだけの拍は 0.8 倍以下（6.15.10 は 0.94 倍）
    /// </summary>
    public static void TestBeatHueFlashFollowsTheDrums()
    {
        var hip = Get(false, "hiphop88").R("strip");
        var house = Get(false, "house124").R("strip");
        T.Check(house.LoudQuietRatio >= 1.25, $"house: stronger drop kicks rise higher than intro kicks ({house.LoudQuietRatio:F2})");
        var rock = Get(false, "rock120").R("strip");
        var hipOld = Get(true, "hiphop88").R("strip");
        var rockOld = Get(true, "rock120").R("strip");
        T.Info($"hip-hop: beat without a hit {hip.NoK:F2} of a kick (6.15.10 {hipOld.NoK:F2}); rock: snare-only beat {rock.SnO:F2} (6.15.10 {rockOld.SnO:F2})");
        T.Check(hip.NoK <= 0.3, $"hip-hop strip: a beat with no kick or snare is a small flash ({hip.NoK:F2} of a kick beat)");
        T.Check(rock.SnO <= 0.8, $"rock strip: a snare-only beat is weaker than a kick beat ({rock.SnO:F2} of a kick beat)");
        T.Check(hipOld.NoK >= 0.4 && rockOld.SnO >= 0.85, $"(reference) 6.15.10 flashed every beat about the same ({hipOld.NoK:F2}, {rockOld.SnO:F2})");
    }

    /// <summary>
    /// 色の切り替えは、決まった拍の数ごとではなく、曲に合わせる: 小節の頭（和音・ベースの音が変わる拍）で半分以上
    /// （6.15.10 は小節の頭の見当がずれて 0%）、ハウスのキックのない間奏（ドラムが鳴らない 3 小節）では切り替えない（6.15.10 は 2 拍ごとに切り替え続けた）、
    /// 区切り（区間の頭・クラッシュ）の半分以上で切り替える
    /// </summary>
    public static void TestBeatHueColourStepsOnTheMusic()
    {
        foreach (string song in new[] { "house124", "rock120" })
        {
            var r = Get(false, song).R("strip");
            var o = Get(true, song).R("strip");
            T.Info($"{song}: {r.Steps} steps, {r.Down:P0} on the bar head, sections {r.SecHit}/{r.SecN} (6.15.10: {o.Steps} steps, {o.Down:P0}, {o.SecHit}/{o.SecN})");
            T.Check(r.Down >= 0.5, $"{song} strip: most colour steps land on the bar head ({r.Down:P0})");
            T.Check(r.SecHit * 2 >= r.SecN, $"{song} strip: the colour changes on most section starts ({r.SecHit}/{r.SecN})");
            T.Check(o.Down <= 0.2, $"(reference) 6.15.10 stepped off the bar head on {song} ({o.Down:P0})");
        }
        // ハウスの間奏: 16〜18 小節はキックもスネアもない（19 小節目はスネアの連打）
        double BreakSteps(bool old)
        {
            var run = Get(old, "house124");
            double from = run.S.Beats.First(b => b.Bar == 16).T + 0.3, to = run.S.Beats.First(b => b.Bar == 19).T - 0.1;
            return run.L("strip", false).Steps.Count(t => t >= from && t < to);
        }
        T.Check(BreakSteps(false) == 0, $"house strip: no colour step in the break without drums ({BreakSteps(false)} steps)");
        T.Check(BreakSteps(true) >= 2, $"(reference) 6.15.10 kept stepping through the break ({BreakSteps(true)} steps)");
    }

    /// <summary>
    /// 拍に合わせる確かさは 6.15.10 より悪くしない: 光り始めの 9 割以上が拍に乗り、キックの拍の 95% 以上で光り、遅れは 34 ミリ秒以内（テープ）。
    /// メモリ（1 秒に 8 回）でも拍が見える: キックの拍の 8 割以上で光る（6.15.10 は低音の明るさが拍の光と同じくらいで、2 割以下）
    /// </summary>
    public static void TestBeatHueKeepsTheBeat()
    {
        foreach (string song in new[] { "house124", "rock120", "hiphop88" })
        {
            var r = Get(false, song).R("strip");
            var o = Get(true, song).R("strip");
            T.Check(r.On >= Math.Min(0.9, o.On - 0.02) && r.LitK >= Math.Min(0.95, o.LitK - 0.02) && r.LatMs <= 34,
                $"{song} strip: flashes stay on the beat (on {r.On:P0}, kick beats lit {r.LitK:P0}, {r.LatMs:F0} ms; 6.15.10 {o.On:P0}, {o.LitK:P0}, {o.LatMs:F0} ms)");
        }
        foreach (string song in new[] { "house124", "rock120" })
        {
            var r = Get(false, song).R("dram");
            var o = Get(true, song).R("dram");
            T.Check(r.LitK >= 0.8, $"{song} DRAM: the beat is visible on a slow device ({r.LitK:P0} of kick beats lit; 6.15.10 {o.LitK:P0})");
            T.Check(o.LitK <= 0.3, $"(reference) 6.15.10 hid the beat on DRAM ({o.LitK:P0})");
        }
    }

    public static void TestBeatHueHearsKicksBetweenSnareBeats()
    {
        var song = Dnb();
        var (lanes, locks) = Play(song, new[] { "strip" }, Intensities.Moderate, realAi: false);
        var score = Score(song, lanes.Single(l => l.Rf), locks);
        T.Check(score.LitK >= 0.75 && score.SnO < 1 && score.SMax <= FlashGuard.MaxPerSecond,
            $"drum and bass: kick beats lit {score.LitK:P0}, snare/kick {score.SnO:F2}, strong flashes/s {score.SMax}");
    }

    /// <summary>「激しい点滅を抑える」がオンなら、どの曲・デバイスでも強い光り始めは 1 秒に 3 回まで</summary>
    public static void TestBeatHueFlashGuard()
    {
        foreach (var run in NewRuns.Value)
            foreach (string dev in TestDevs)
            {
                var r = run.R(dev, rf: true);
                T.Check(r.SMax <= 3, $"{run.S.Name} {dev} reduce flashing: at most 3 strong flashes per second ({r.SMax})");
            }
    }
}

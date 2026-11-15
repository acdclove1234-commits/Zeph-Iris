// ==================================================
// SignalGen.cs - テスト用の合成音 (キック・ハイハット・スネア・サイン波・白色／ピンクノイズ・ループ・ランダムな打音・
//                ベースライン・声に似た音・パッド・金管に似た音・はじいた和音)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace LogicTests;

/// <summary>
/// テスト用の音を作る（モノラル、−1〜1）。乱数の種を決めてあるので、毎回同じ音になる。
/// 鳴らした時刻（バッファの先頭からの秒）を KickTimes などに記録する
/// </summary>
public sealed class SignalGen
{
    public readonly int Fs;
    public readonly float[] Buf;
    public readonly List<double> KickTimes = new();
    public readonly List<double> HatTimes = new();
    public readonly List<double> SnareTimes = new();
    private readonly Random _rng;
    // ピンクノイズのフィルターの状態（Paul Kellet の近似）
    private double _p0, _p1, _p2, _p3, _p4, _p5, _p6;

    public SignalGen(int fs, double seconds, int seed = 1)
    {
        Fs = fs;
        Buf = new float[(int)Math.Round(seconds * fs)];
        _rng = new Random(seed);
    }

    public double Seconds => Buf.Length / (double)Fs;

    private double Noise() => _rng.NextDouble() * 2 - 1;

    private void Add(int i, double v)
    {
        if (i >= 0 && i < Buf.Length) Buf[i] = (float)Math.Clamp(Buf[i] + v, -1, 1);
    }

    /// <summary>
    /// キック: 周波数が 140 Hz から 50 Hz へ下がるサイン波（ドン）＋ 最初の 2 ミリ秒のノイズ（カチッ。3 kHz 以下）
    /// </summary>
    public void Kick(double t0, double amp = 0.8, bool click = true)
    {
        int s0 = (int)Math.Round(t0 * Fs);
        KickTimes.Add(s0 / (double)Fs);
        int n = (int)(0.40 * Fs);
        double a = 1 - Math.Exp(-2 * Math.PI * 3000 / Fs), lp = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Fs;
            double phase = 2 * Math.PI * (50 * t + 90 * 0.025 * (1 - Math.Exp(-t / 0.025)));
            double env = Math.Exp(-t / 0.15) * Fade(i, n);
            double v = amp * env * Math.Sin(phase);
            if (click && t < 0.002)
            {
                lp += a * (Noise() - lp);
                v += amp * 0.8 * lp * (1 - t / 0.002);
            }
            Add(s0 + i, v);
        }
    }

    /// <summary>ハイハット: 高い音だけのノイズ（差分を 2 回とって低音を消す）、25 ミリ秒で消える</summary>
    public void Hat(double t0, double amp = 0.12)
    {
        int s0 = (int)Math.Round(t0 * Fs);
        HatTimes.Add(s0 / (double)Fs);
        int n = (int)(0.08 * Fs);
        double x1 = 0, x2 = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Fs;
            double x = Noise();
            double hp = x - 2 * x1 + x2;
            x2 = x1;
            x1 = x;
            Add(s0 + i, amp * 0.5 * hp * Math.Exp(-t / 0.025) * Fade(i, n));
        }
    }

    /// <summary>スネア: 190 Hz の胴の音（40 ミリ秒で消える）＋ 1 kHz 以上のノイズ（響き線の音。90 ミリ秒で消える）</summary>
    public void Snare(double t0, double amp = 0.4)
    {
        int s0 = (int)Math.Round(t0 * Fs);
        SnareTimes.Add(s0 / (double)Fs);
        int n = (int)(0.25 * Fs);
        double a = 1 / (1 + 2 * Math.PI * 1000 / Fs);
        double x1 = 0, h1 = 0, y1 = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Fs;
            double x = Noise();
            double h = a * (h1 + x - x1);   // 1 次のハイパス（1 kHz）を 2 回
            double y = a * (y1 + h - h1);
            x1 = x;
            h1 = h;
            y1 = y;
            double v = 0.6 * Math.Sin(2 * Math.PI * 190 * t) * Math.Exp(-t / 0.04) + 0.7 * y * Math.Exp(-t / 0.09);
            Add(s0 + i, amp * v * Fade(i, n));
        }
    }

    /// <summary>終わりの 30% をなめらかに 0 にする（音が急に切れると、それ自体が「カチッ」という立ち上がりになるため）</summary>
    private static double Fade(int i, int n)
    {
        double u = (double)(n - i) / (0.3 * n);
        return u >= 1 ? 1 : 0.5 - 0.5 * Math.Cos(Math.PI * Math.Max(0, u));
    }

    /// <summary>同期テストのクリック: 1 kHz のサイン波を 5 ミリ秒</summary>
    public void Click(double t0, double amp = 0.5)
    {
        int s0 = (int)Math.Round(t0 * Fs);
        int n = (int)(0.005 * Fs);
        for (int i = 0; i < n; i++) Add(s0 + i, amp * Math.Sin(2 * Math.PI * 1000 * i / Fs));
    }

    /// <summary>サイン波（始まりと終わりは 5 ミリ秒でなめらかに）</summary>
    public void Tone(double freq, double amp, double from, double to)
    {
        int a = (int)(from * Fs), b = Math.Min(Buf.Length, (int)(to * Fs));
        int fade = (int)(0.005 * Fs);
        for (int i = a; i < b; i++)
        {
            double g = Math.Min(1, Math.Min((i - a) / (double)fade, (b - i) / (double)fade));
            Add(i, amp * g * Math.Sin(2 * Math.PI * freq * i / Fs));
        }
    }

    /// <summary>白色ノイズ（rmsDb = 2 乗平均の dBFS）</summary>
    public void White(double rmsDb, double from, double to)
    {
        double rms = Math.Pow(10, rmsDb / 20);
        double k = rms * Math.Sqrt(3); // 一様分布 −1〜1 の RMS は 1/√3
        int a = (int)(from * Fs), b = Math.Min(Buf.Length, (int)(to * Fs));
        for (int i = a; i < b; i++) Add(i, k * Noise());
    }

    /// <summary>ピンクノイズ（1 オクターブあたりのパワーが同じ。rmsDb はおおよそ）</summary>
    public void Pink(double rmsDb, double from, double to)
    {
        int a = (int)(from * Fs), b = Math.Min(Buf.Length, (int)(to * Fs));
        var tmp = new double[b - a];
        double sum = 0;
        for (int i = 0; i < tmp.Length; i++)
        {
            double w = Noise();
            _p0 = 0.99886 * _p0 + w * 0.0555179;
            _p1 = 0.99332 * _p1 + w * 0.0750759;
            _p2 = 0.96900 * _p2 + w * 0.1538520;
            _p3 = 0.86650 * _p3 + w * 0.3104856;
            _p4 = 0.55000 * _p4 + w * 0.5329522;
            _p5 = -0.7616 * _p5 - w * 0.0168980;
            double v = _p0 + _p1 + _p2 + _p3 + _p4 + _p5 + _p6 + w * 0.5362;
            _p6 = w * 0.115926;
            tmp[i] = v;
            sum += v * v;
        }
        double k = Math.Pow(10, rmsDb / 20) / Math.Sqrt(sum / Math.Max(1, tmp.Length));
        for (int i = 0; i < tmp.Length; i++) Add(a + i, k * tmp[i]);
    }

    /// <summary>
    /// ループ: 毎拍キック、拍の中間（裏拍）にハイハット。snare = true なら 2・4 拍目はキックの代わりにスネア。
    /// accentFirst = true なら 1 拍目のキックだけ強く（他は 0.55 倍）
    /// </summary>
    public void Loop(double bpm, double from, double to, bool hats = true, bool snare = false, bool accentFirst = false, double kickAmp = 0.8)
    {
        double p = 60 / bpm;
        int k = 0;
        for (double t = from; t < to - 1e-9; t += p, k++)
        {
            double a = accentFirst && k % 4 != 0 ? kickAmp * 0.55 : kickAmp;
            if (snare && k % 2 == 1) Snare(t);
            else Kick(t, a);
            if (hats) Hat(t + p / 2);
        }
    }

    /// <summary>
    /// 音の多い曲: 毎拍キック（2・4 拍目はスネアも同時）、16 分音符のハイハット、2 拍ごとに音が変わるベース、
    /// 和音（パッド）、小さなノイズ（−32 dBFS）
    /// </summary>
    public void DenseMix(double bpm, double from, double to)
    {
        double p = 60 / bpm;
        double[] notes = { 55, 55, 65.4, 49 };
        int k = 0;
        for (double t = from; t < to - 1e-9; t += p, k++)
        {
            Kick(t, 0.6);
            if (k % 2 == 1) Snare(t, 0.3);
            for (int s = 1; s < 4; s++) Hat(t + s * p / 4, s == 2 ? 0.08 : 0.04);
            double f = notes[(k / 2) % 4];
            double end = Math.Min(to, t + p - 0.01);
            Tone(f, 0.12, t + 0.02, end);
            Tone(2 * f, 0.05, t + 0.02, end);
        }
        Tone(261.6, 0.04, from, to);
        Tone(329.6, 0.03, from, to);
        Tone(392, 0.03, from, to);
        Tone(523.3, 0.02, from, to);
        Pink(-32, from, to);
    }

    // ===== 楽器らしさの確かめ用（InstrumentFeatures）

    /// <summary>ベースの音の出だしの時刻（バッファの先頭からの秒）と高さ（Hz）</summary>
    public readonly List<double> BassNoteTimes = new();
    public readonly List<double> BassNoteHz = new();

    /// <summary>
    /// ベースライン（はじいたベース）: 1 秒に notesPerSec 音、notesHz を順に。倍音 6 本（1/h^1.5）・立ち上がり 6 ミリ秒・
    /// 0.35 秒で減衰・次の音の 10 ミリ秒前に止める
    /// </summary>
    public void BassLine(double from, double to, double notesPerSec, double[] notesHz, double amp = 0.3)
    {
        double step = 1 / notesPerSec;
        int k = 0;
        for (double t = from; t < to - 1e-9; t += step, k++)
        {
            double f = notesHz[k % notesHz.Length];
            int s0 = (int)Math.Round(t * Fs);
            BassNoteTimes.Add(s0 / (double)Fs);
            BassNoteHz.Add(f);
            int n = (int)((Math.Min(step, to - t) - 0.010) * Fs);
            int rel = (int)(0.010 * Fs);
            for (int i = 0; i < n + rel; i++)
            {
                double tt = i / (double)Fs;
                double env = Math.Min(1, tt / 0.006) * Math.Exp(-tt / 0.35);
                if (i >= n) env *= 1 - (i - n) / (double)rel;
                double v = 0;
                for (int h = 1; h <= 6; h++) v += Math.Sin(2 * Math.PI * f * h * tt) / Math.Pow(h, 1.5);
                Add(s0 + i, amp * env * v);
            }
        }
    }

    /// <summary>
    /// 声に似た音: 声帯の音（倍音 1/h、5 kHz まで）を 3 つの共鳴（「あ」の母音 730・1,090・2,440 Hz）で形づくり、
    /// 0.15 秒後から vibratoHz・±vibratoCents のビブラート。立ち上がり 40 ミリ秒・音の終わり 50 ミリ秒
    /// </summary>
    public void VocalTone(double from, double to, double[] notesHz, double noteSec, double amp = 0.15, double vibratoHz = 5.5, double vibratoCents = 40)
    {
        double[] fc = { 730, 1090, 2440 }, bw = { 100, 120, 170 }, g = { 1.0, 0.5, 0.25 };
        static double Formant(double f, double[] fc, double[] bw, double[] g)
        {
            double a = 0.02;
            for (int i = 0; i < fc.Length; i++)
            {
                double u = (f - fc[i]) / (bw[i] / 2);
                a += g[i] / (1 + u * u);
            }
            return a;
        }
        int k = 0;
        for (double t = from; t < to - 1e-9; t += noteSec, k++)
        {
            double f0 = notesHz[k % notesHz.Length];
            int s0 = (int)Math.Round(t * Fs), n = (int)(Math.Min(noteSec, to - t) * Fs);
            double phase = 0;
            int hMax = (int)(5000 / f0);
            for (int i = 0; i < n; i++)
            {
                double tt = i / (double)Fs;
                double ramp = Math.Clamp((tt - 0.15) / 0.15, 0, 1);
                double f = f0 * Math.Pow(2, vibratoCents / 1200 * ramp * Math.Sin(2 * Math.PI * vibratoHz * tt));
                phase += 2 * Math.PI * f / Fs;
                double env = Math.Min(1, tt / 0.040) * Math.Min(1, (n - i) / (0.050 * Fs));
                double v = 0;
                for (int h = 1; h <= hMax; h++) v += Formant(h * f, fc, bw, g) / h * Math.Sin(h * phase);
                Add(s0 + i, amp * env * v);
            }
        }
    }

    /// <summary>
    /// パッド: 和音の音ごとに 3 つの少しずらした音（±detuneCents）、倍音 8 本（1/h²）。立ち上がり・終わりとも 0.5 秒。
    /// seed を変えると位相とずらし方が変わる（左右で別の音にして、横に広げる）
    /// </summary>
    public void PadChord(double from, double to, double[] chordHz, double amp = 0.05, double detuneCents = 7, int seed = 1)
    {
        var rng = new Random(seed);
        int a = (int)(from * Fs), b = Math.Min(Buf.Length, (int)(to * Fs));
        foreach (double fn in chordHz)
        {
            for (int v = -1; v <= 1; v++)
            {
                double f = fn * Math.Pow(2, (v * detuneCents + (rng.NextDouble() - 0.5) * 2) / 1200);
                double ph = rng.NextDouble() * 2 * Math.PI;
                for (int i = a; i < b; i++)
                {
                    double tt = (i - a) / (double)Fs;
                    double env = Math.Min(1, Math.Min(tt / 0.5, (b - i) / (0.5 * Fs)));
                    double s = 0;
                    for (int h = 1; h <= 8; h++) s += Math.Sin(h * (2 * Math.PI * f * tt + ph)) / (h * h);
                    Add(i, amp / 3 * env * s);
                }
            }
        }
    }

    /// <summary>
    /// 金管に似た音: 倍音がたくさんあり（8 kHz まで）、1.5 kHz あたりがいちばん強い明るい音。大きいほど高い倍音が強い。
    /// 立ち上がり 60 ミリ秒・音の終わり 60 ミリ秒・音と音の間は 30 ミリ秒あける
    /// </summary>
    public void BrassTone(double from, double to, double[] notesHz, double noteSec, double amp = 0.12)
    {
        int k = 0;
        for (double t = from; t < to - 1e-9; t += noteSec, k++)
        {
            double f0 = notesHz[k % notesHz.Length];
            int s0 = (int)Math.Round(t * Fs), n = (int)((Math.Min(noteSec, to - t) - 0.030) * Fs);
            int hMax = (int)(8000 / f0);
            for (int i = 0; i < n; i++)
            {
                double tt = i / (double)Fs;
                double env = Math.Min(1, tt / 0.060) * Math.Min(1, (n - i) / (0.060 * Fs));
                double v = 0;
                for (int h = 1; h <= hMax; h++)
                {
                    double f = h * f0;
                    double shape = f < 1500 ? Math.Sqrt(f / 1500) : Math.Pow(1500 / f, 1.2);
                    v += shape * Math.Pow(env, 0.2 * (h - 1)) * Math.Sin(2 * Math.PI * f * tt);
                }
                Add(s0 + i, amp * env * v / 4);
            }
        }
    }

    /// <summary>ギター・鍵盤に似た音: 和音の音を spreadSec ずつずらしてはじく（倍音 8 本 1/h^1.2・0.4 秒で減衰）</summary>
    public void Strum(double t0, double[] chordHz, double amp = 0.08, double spreadSec = 0.012)
    {
        for (int j = 0; j < chordHz.Length; j++)
        {
            int s0 = (int)Math.Round((t0 + j * spreadSec) * Fs), n = (int)(1.6 * Fs);
            for (int i = 0; i < n; i++)
            {
                double tt = i / (double)Fs;
                double env = Math.Exp(-tt / 0.4) * Math.Min(1, tt / 0.002) * Fade(i, n);
                double v = 0;
                for (int h = 1; h <= 8; h++) v += Math.Sin(2 * Math.PI * chordHz[j] * h * tt) / Math.Pow(h, 1.2);
                Add(s0 + i, amp * env * v);
            }
        }
    }

    /// <summary>ランダムな打音（間隔は指数分布、平均 meanGap 秒・最短 minGap 秒）。キック 60%・スネア 20%・ハイハット 20%</summary>
    public void RandomHits(double from, double to, double meanGap = 0.45, double minGap = 0.12)
    {
        double t = from;
        while (true)
        {
            t += minGap - Math.Log(1 - _rng.NextDouble()) * (meanGap - minGap);
            if (t >= to) break;
            double r = _rng.NextDouble();
            if (r < 0.6) Kick(t);
            else if (r < 0.8) Snare(t);
            else Hat(t, 0.2);
        }
    }
}

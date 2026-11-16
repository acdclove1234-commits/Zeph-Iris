// ==================================================
// MusicLightTests.cs - 音から LED の光までを通しで動かして、光り方を数値で測る
//   (合成した音を分析しながら 60 コマ/秒で描き、LED の明るさの変化を記録する。
//    速いアルペジオでちかちかしないか・高音のメロディに反応するか・キックがはっきり光るか)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

/// <summary>音を流しながら光を描いた結果（1 コマごとの、ゾーン全体の明るさの平均。0〜1）</summary>
public sealed record LightRun(double[] T, double[] Brightness)
{
    /// <summary>1 コマごとの色（明るさをそろえた RGB の向き。色の変わり方を見る）</summary>
    public double[][] Hue { get; init; } = Array.Empty<double[]>();
    /// <summary>流しているあいだの出来事（キック・スネアなど）</summary>
    public List<AudioEvent> Events { get; init; } = new();

    /// <summary>色がはっきり変わった回数（1 秒あたり。前の 3 コマから色の向きが 0.25 以上変わったら 1 回）</summary>
    public double ColorChangesPerSec(double from, double to)
    {
        int n = 0;
        double lastChange = double.NegativeInfinity;
        for (int i = 3; i < T.Length; i++)
        {
            if (T[i] < from || T[i] >= to) continue;
            double d = 0;
            for (int c = 0; c < 3; c++) d += Math.Abs(Hue[i][c] - Hue[i - 3][c]);
            if (d >= 0.25 && T[i] - lastChange > 0.1) { n++; lastChange = T[i]; }
        }
        return n / (to - from);
    }

    /// <summary>[from, to) 秒のコマ</summary>
    public IEnumerable<(double T, double B)> Between(double from, double to)
    {
        for (int i = 0; i < T.Length; i++) if (T[i] >= from && T[i] < to) yield return (T[i], Brightness[i]);
    }

    /// <summary>はっきりした明るさの山（前後の谷から prominence 以上高い山）の 1 秒あたりの数</summary>
    public double PeaksPerSec(double from, double to, double prominence = 0.12)
    {
        var b = Between(from, to).Select(x => x.B).ToArray();
        if (b.Length < 3) return 0;
        int peaks = 0;
        double lastValley = b[0], candidate = double.NaN;
        bool rising = true;
        for (int i = 1; i < b.Length; i++)
        {
            if (rising)
            {
                if (b[i] < b[i - 1])
                {
                    candidate = b[i - 1];
                    rising = false;
                }
                else if (b[i] < lastValley) lastValley = b[i];
            }
            else
            {
                if (b[i] > b[i - 1])
                {
                    // 山の両側の谷のうち高いほうから測る
                    double valley = Math.Max(lastValley, b[i - 1]);
                    if (candidate - valley >= prominence) peaks++;
                    lastValley = b[i - 1];
                    rising = true;
                }
            }
        }
        return peaks / (to - from);
    }

    /// <summary>
    /// 明るさの向きが 4 コマ（60 コマ／秒で 0.07 秒）以内に戻った回数（1 秒あたり。2 段（2/255）以上の変化だけ数える。
    /// ModeSyncBench の jit と同じ数え方を、ゾーン全体の明るさで）
    /// </summary>
    public double QuickReversalsPerSec(double from, double to)
    {
        var b = Between(from, to).Select(x => x.B).ToArray();
        int dir = 0, flip = int.MinValue / 2, n = 0;
        for (int i = 1; i < b.Length; i++)
        {
            double d = b[i] - b[i - 1];
            int s = Math.Abs(d) >= 2 / 255.0 ? Math.Sign(d) : 0;
            if (s == 0) continue;
            if (dir != 0 && s != dir) { if (i - flip <= 4) n++; flip = i; }
            dir = s;
        }
        return n / (to - from);
    }

    /// <summary>細かい揺れ: 前後 50 ミリ秒（±3 コマ）の平均からのずれの二乗平均の平方根（ハイハットのたびの小さな明滅など）</summary>
    public double Ripple(double from, double to)
    {
        var b = Between(from, to).Select(x => x.B).ToArray();
        double s = 0;
        int n = 0;
        for (int i = 3; i + 3 < b.Length; i++)
        {
            double m = 0;
            for (int k = -3; k <= 3; k++) m += b[i + k];
            double d = b[i] - m / 7;
            s += d * d;
            n++;
        }
        return n > 0 ? Math.Sqrt(s / n) : 0;
    }

    /// <summary>1 秒あたりの明るさの変わり幅の合計（コマ間の差の絶対値の合計。大きいほど激しく変わる）</summary>
    public double ChangePerSec(double from, double to)
    {
        var b = Between(from, to).Select(x => x.B).ToArray();
        double s = 0;
        for (int i = 1; i < b.Length; i++) s += Math.Abs(b[i] - b[i - 1]);
        return s / (to - from);
    }

    /// <summary>明るさの上位 10% と下位 10% の差（光が音に合わせて動いている幅）</summary>
    public double Swing(double from, double to)
    {
        var b = Between(from, to).Select(x => x.B).OrderBy(x => x).ToArray();
        if (b.Length == 0) return 0;
        return b[(int)(0.9 * (b.Length - 1))] - b[(int)(0.1 * (b.Length - 1))];
    }

    /// <summary>時刻のリストの直後 [a, b) 秒の明るさの平均</summary>
    public double MeanAfter(IEnumerable<double> times, double a, double b)
    {
        var list = new List<double>();
        foreach (double t in times)
            list.AddRange(Between(t + a, t + b).Select(x => x.B));
        return list.Count > 0 ? list.Average() : 0;
    }
}

public static class MusicLight
{
    /// <summary>描くコマの時刻を、音より何秒遅らせるか（アプリの「音の遅れ」と同じ考え方。分析が追いつくのを待つ）</summary>
    private const double Latency = 0.08;

    /// <summary>buf を流しながら、30 LED のテープを 60 コマ/秒で描く</summary>
    /// <param name="settings">テープの設定（null なら既定。ゾーンの「反応する音」を決めるときに使う。デバイスの名前は「Strip」）</param>
    public static LightRun Run(float[] buf, int fs, ResolvedMusic m, RgbConfig? engineCfg = null, RgbDeviceSettings? settings = null)
    {
        var e = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(engineCfg ?? new RgbConfig { Mode = RgbModes.Music }) };
        var dev = new RgbDevice(2, "Strip", 30, 4, null, "HID: x");
        var r = new DeviceRenderer(dev);
        var input = new RenderInput
        {
            Cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = false },
            Music = m,
            Device = settings ?? new RgbDeviceSettings { Name = dev.Name },
        };
        var output = new Rgb[dev.LedCount];
        const double t0 = 1000;
        long cursor = 0;
        int pos = 0, chunk = 480;
        double last = double.NaN;
        var ts = new List<double>();
        var bs = new List<double>();
        var hs = new List<double[]>();
        var evs = new List<AudioEvent>();
        double total = buf.Length / (double)fs;
        for (int k = 0; ; k++)
        {
            double sec = k / 60.0;
            if (sec + Latency > total) break;
            // 描く時刻 + 遅れの分まで、音を流しておく
            while (pos < buf.Length && pos / (double)fs < sec + Latency)
            {
                int n = Math.Min(chunk, buf.Length - pos);
                e.Push(new ReadOnlySpan<float>(buf, pos, n), t0 + pos / (double)fs);
                pos += n;
            }
            double tEval = t0 + sec;
            input.T = input.TEval = tEval;
            input.TPrevEval = double.IsNaN(last) ? tEval : last;
            input.Dt = double.IsNaN(last) ? 0 : tEval - last;
            input.Beat = e.Beat;
            input.Features = e.Features;
            if (!e.Features.Sample(tEval, input.Frame)) input.Frame.Clear();
            input.Events.Clear();
            e.Events.Read(ref cursor, tEval, input.Events);
            evs.AddRange(input.Events);
            r.Render(input, output);
            last = tEval;
            var l = r.LastLight;
            double v = 0, cr = 0, cg = 0, cb = 0;
            for (int i = 0; i < dev.LedCount; i++) { v += l[i].MaxChannel; cr += l[i].R; cg += l[i].G; cb += l[i].B; }
            double sum = cr + cg + cb;
            ts.Add(sec);
            bs.Add(v / dev.LedCount);
            hs.Add(sum > 1e-6 ? new[] { cr / sum, cg / sum, cb / sum } : new[] { 1 / 3.0, 1 / 3.0, 1 / 3.0 });
        }
        return new LightRun(ts.ToArray(), bs.ToArray()) { Hue = hs.ToArray(), Events = evs };
    }

    /// <summary>今の設定（強さ Moderate・ビートで色替え）の光り方</summary>
    public static ResolvedMusic Default(string style = MusicStyles.BeatHue)
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = style };
        return MusicTuning.Resolve(cfg);
    }

    /// <summary>減衰する音（倍音つき）。撥弦（アルペジオ）やベルの代わり</summary>
    public static void Pluck(SignalGen g, double t, double freq, double amp, double decaySec, double harmonics = 3)
    {
        int fs = g.Fs;
        int i0 = (int)(t * fs), n = (int)(Math.Min(decaySec * 5, 2.0) * fs);
        for (int i = 0; i < n && i0 + i < g.Buf.Length; i++)
        {
            double tt = i / (double)fs;
            double env = Math.Exp(-tt / decaySec) * Math.Min(1, tt / 0.003);
            double s = 0;
            for (int h = 1; h <= harmonics; h++) s += Math.Sin(2 * Math.PI * freq * h * tt) / (h * h);
            g.Buf[i0 + i] += (float)(amp * env * s);
        }
    }

    /// <summary>速いアルペジオ（ドラムなし、1 秒に notesPerSec 音、和音を上下する）と小さなパッド</summary>
    public static (SignalGen G, List<double> Notes) Arpeggio(double notesPerSec = 12, double seconds = 16, double register = 1)
    {
        var g = new SignalGen(48000, seconds, seed: 31);
        double[] chord = new[] { 523.3, 659.3, 784.0, 1046.5, 784.0, 659.3 }.Select(f => f * register).ToArray();
        var notes = new List<double>();
        int k = 0;
        for (double t = 0.3; t < seconds - 0.3; t += 1 / notesPerSec, k++)
        {
            Pluck(g, t, chord[k % chord.Length] * (k / 24 % 2 == 0 ? 1 : 0.89), 0.18, 0.18);
            notes.Add(t);
        }
        g.Tone(261.6, 0.02, 0.2, seconds);
        g.Pink(-48, 0.2, seconds);
        return (g, notes);
    }

    /// <summary>ゆっくりした高音のベルのメロディ（ドラムなし、1 秒に 2 音、2〜4 kHz）</summary>
    public static (SignalGen G, List<double> Notes) TrebleMelody(double seconds = 16)
    {
        var g = new SignalGen(48000, seconds, seed: 32);
        double[] mel = { 2093, 2349, 2637, 3136, 2637, 2349, 3520, 3136 };
        var notes = new List<double>();
        int k = 0;
        for (double t = 0.4; t < seconds - 0.5; t += 0.5, k++)
        {
            Pluck(g, t, mel[k % mel.Length], 0.14, 0.45, 2);
            notes.Add(t);
        }
        g.Tone(196, 0.02, 0.2, seconds);
        g.Pink(-50, 0.2, seconds);
        return (g, notes);
    }
}

public static class MusicLightTests
{
    /// <summary>調査用: 環境変数 LIGHT_REPORT=1 のとき、3 つの合成音での光り方の数値を表示する（BEAT_TUNE も使える）</summary>
    public static void TestLightReport()
    {
        if (Environment.GetEnvironmentVariable("LIGHT_REPORT") != "1") return;
        // LIGHT_SWEEP に「名前=値;…|…」を入れると、組ごとに表示する
        foreach (var spec in (Environment.GetEnvironmentVariable("LIGHT_SWEEP") ?? Environment.GetEnvironmentVariable("BEAT_TUNE") ?? "").Split('|'))
        {
            var saved = BeatTuneSweepTests.Snapshot();
            try
            {
                BeatTuneSweepTests.Apply(spec);
                T.Info($"[{(spec.Length == 0 ? "current" : spec)}]");
                Report();
            }
            finally { BeatTuneSweepTests.Restore(saved); }
        }
    }

    private static void Report()
    {
        var m = MusicLight.Default();
        foreach (var (rate, reg, name) in new[] { (12.0, 1.0, "mid 12/s"), (8.0, 1.0, "mid 8/s"), (8.0, 0.25, "low 8/s"), (6.0, 0.25, "low 6/s"), (12.0, 0.5, "low-mid 12/s"), (8.0, 0.125, "bass 8/s"), (4.0, 0.125, "bass 4/s") })
        {
            var (arp, _) = MusicLight.Arpeggio(rate, 16, reg);
            var la = MusicLight.Run(arp.Buf, arp.Fs, m);
            double secs = 11.5;
            T.Info($"arpeggio {name,-12}: peaks {la.PeaksPerSec(4, 15.5):F1}/s  swing {la.Swing(4, 15.5):F2}  color changes {la.ColorChangesPerSec(4, 15.5):F1}/s  kick {la.Events.Count(x => x.Kind == AudioEventKind.Kick && x.T > 1004) / secs:F1}/s onset {la.Events.Count(x => x.Kind == AudioEventKind.Onset && x.T > 1004) / secs:F1}/s");
        }
        var (mel, notes) = MusicLight.TrebleMelody();
        var lm = MusicLight.Run(mel.Buf, mel.Fs, m);
        var late = notes.Where(t => t > 4).ToList();
        T.Info($"treble melody 2/s: peaks {lm.PeaksPerSec(4, 15.5):F1}/s  swing {lm.Swing(4, 15.5):F2}  after note {lm.MeanAfter(late, 0.03, 0.15):F2} vs before {lm.MeanAfter(late, -0.12, -0.02):F2}  color changes {lm.ColorChangesPerSec(4, 15.5):F1}/s");
        var c = MusicBench.Cases().First(x => x.Name.StartsWith("rock"));
        var lr = MusicLight.Run(c.Gen.Buf, c.Gen.Fs, m);
        var beats = c.Beats.Where(t => t > 8).ToList();
        var kicks = c.Gen.KickTimes.Where(t => t > 8).ToList();
        T.Info($"rock 120: kick {lr.MeanAfter(kicks, 0.0, 0.08):F2} vs between {lr.MeanAfter(beats, 0.20, 0.30):F2}  peaks {lr.PeaksPerSec(8, 23):F1}/s  swing {lr.Swing(8, 23):F2}  color changes {lr.ColorChangesPerSec(8, 23):F1}/s");
    }
}

public static class MelodyDiagTests
{
    /// <summary>調査用: 環境変数 MELODY_DIAG=1 のとき、ベルのメロディで、音の前後の中音・高音の値と出来事を表示する</summary>
    public static void TestMelodyDiag()
    {
        if (Environment.GetEnvironmentVariable("MELODY_DIAG") != "1") return;
        foreach (var (name, gen) in new[] { ("bells", MusicLight.TrebleMelody()), ("arp low 6/s", MusicLight.Arpeggio(6, 16, 0.25)) })
        {
            var (g, notes) = gen;
            var e = new AudioEngine(48000) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
            var bands = new List<(double T, double[] Bn)>();
            e.Onsets.DiagHop = (t, odf, bn) => bands.Add((t, (double[])bn.Clone()));
            var run = new EngineRun(e).Feed(g.Buf);
            T.Info($"--- {name}: events/s " + string.Join(" ", Enum.GetValues<AudioEventKind>().Select(k => $"{k} {run.Events.Count(x => x.Kind == k && x.T > run.T0 + 4) / 11.5:F1}")));
            foreach (double tn in notes.Where(t => t > 6).Take(3))
            {
                foreach (double d in new[] { -0.05, 0.03, 0.1, 0.25, 0.45 })
                {
                    var f = run.Frames.FirstOrDefault(x => x.T - run.T0 >= tn + d);
                    var b = bands.FirstOrDefault(x => x.T - run.T0 >= tn + d).Bn ?? new double[4];
                    if (f is null) continue;
                    T.Info($"  note {tn:F2} {d,5:+0.00;-0.00}s  Mid {f.Mid:F2} MidE {f.MidE:F2}  Treble {f.Treble:F2} TrebleE {f.TrebleE:F2}  Bass {f.Bass:F2} BassE {f.BassE:F2} KickEnv {f.KickEnv:F2}  flux G2 {b[2]:F1} G3 {b[3]:F1}");
                }
            }
        }
    }
}

public static class BeatHueLightTests
{
    /// <summary>
    /// 「ビートで色替え」: 高音のメロディ（ドラムなし）の 1 音ずつで明るくなり、ロックのキックははっきり光り、
    /// 速いアルペジオではちかちかしない（音から LED までを通しで動かして測る）
    /// </summary>
    public static void TestBeatHueFollowsMelodyKickWithoutFlicker()
    {
        var m = MusicLight.Default();
        var (mel, notes) = MusicLight.TrebleMelody();
        var lm = MusicLight.Run(mel.Buf, mel.Fs, m);
        var late = notes.Where(t => t > 4).ToList();
        double after = lm.MeanAfter(late, 0.03, 0.15), before = lm.MeanAfter(late, -0.12, -0.02);
        T.Check(after >= before + 0.04 && lm.Swing(4, 15.5) >= 0.2,
            $"treble melody: brighter right after each note ({after:F2} vs {before:F2}), swing {lm.Swing(4, 15.5):F2} ≥ 0.20");

        var c = MusicBench.Cases().First(x => x.Name.StartsWith("rock"));
        var lr = MusicLight.Run(c.Gen.Buf, c.Gen.Fs, m);
        double kick = lr.MeanAfter(c.Gen.KickTimes.Where(t => t > 8), 0, 0.08), between = lr.MeanAfter(c.Beats.Where(t => t > 8), 0.20, 0.30);
        T.Check(kick - between >= 0.18, $"rock: kicks stand out ({kick:F2} vs {between:F2} between beats)");

        // 12 音／秒のアルペジオは 6 音で 1 周（0.5 秒 = 120 BPM）なので、テンポをつかんで 1 周に 1 回（1 秒に 2 回）光るのはよい。
        // 1 音ずつ光らない（はっきりした山は 1 秒に 2 回まで）・短い間に明るさの向きが戻らない（ちらつかない）ことを見る。
        // （6.15.7 までは山 0.8 回／秒と数えていたが、拍の光の途中の小さなへこみ（ちらつき）で山が 2 つに割れて、低く数えていた。
        //  そのころの短い間の向きの戻りは 4.0 回／秒（12 音）・4.8 回／秒（6 音）。低い 6 音／秒は 1 音ずつが低音として光る）
        // 明るさの幅（swing）は、拍の光だけで光るようにしてから（BeatFlash）、拍の頭の光がはっきりした分だけ広い（12 音: 0.25 → 0.32）。
        // 1 音ずつのちらつきは、山の数と短い間の向きの戻りで見る
        foreach (var (rate, reg, name, maxPeaks, maxQuick, maxSwing) in new[] { (12.0, 1.0, "mid 12/s", 2.0, 0.5, 0.35), (6.0, 0.25, "low 6/s", 3.0, 1.5, 0.3) })
        {
            var (arp, _) = MusicLight.Arpeggio(rate, 16, reg);
            var la = MusicLight.Run(arp.Buf, arp.Fs, m);
            double quick = la.QuickReversalsPerSec(4, 15.5);
            T.Check(la.PeaksPerSec(4, 15.5) <= maxPeaks && la.Swing(4, 15.5) <= maxSwing && quick <= maxQuick,
                $"arpeggio {name}: no flicker ({la.PeaksPerSec(4, 15.5):F1} clear peaks/s ≤ {maxPeaks}, swing {la.Swing(4, 15.5):F2} ≤ {maxSwing:F2}, quick reversals {quick:F2}/s ≤ {maxQuick})");
        }
    }
}

/// <summary>ドラムの多い曲（人が叩く揺れ・16 分のハイハット・高音のリード入り）での光り方</summary>
public static class DrumLight
{
    public sealed record Song(string Name, SignalGen G, List<double> Beats);

    public static IEnumerable<Song> Songs()
    {
        const double sec = 24;
        {
            var g = new SignalGen(48000, sec, seed: 41);
            var b = MusicBench.Pattern(g, 120, 0.2, sec, "x.....x...x.....", "....x.......x...", "xxxxxxxxxxxxxxxx", jitterMs: 12, seed: 41, hatAmp: 0.07);
            MusicBench.Backing(g, 120, 0.2, sec);
            Lead(g, 120, 0.2, sec);
            yield return new Song("pop 120, 16th hats, lead", g, b);
        }
        {
            var g = new SignalGen(48000, sec, seed: 42);
            var b = MusicBench.Pattern(g, 128, 0.2, sec, "x...x...x...x...", "....x.......x...", "..o...o...o...o.", seed: 42, hatAmp: 0.08);
            MusicBench.Backing(g, 128, 0.2, sec);
            Lead(g, 128, 0.2, sec);
            yield return new Song("edm 128, open hats, lead", g, b);
        }
        {
            var g = new SignalGen(48000, sec, seed: 43);
            var b = MusicBench.Pattern(g, 100, 0.2, sec, "x.......x.x.....", "....x.......x..g", "x.x.x.x.x.x.x.xx", jitterMs: 20, seed: 43, hatAmp: 0.08);
            MusicBench.Backing(g, 100, 0.2, sec);
            yield return new Song("live rock 100, loose", g, b);
        }
        {
            // テンポがつかみにくい曲（シンコペーションが強い。実際の曲でテンポの確かさが下がった区間の代わり）
            var c = MusicBench.Cases().First(x => x.Name.StartsWith("funk"));
            Lead(c.Gen, 105, 0.2, MusicBench.Seconds);
            yield return new Song("funk 105 (no lock), lead", c.Gen, c.Beats);
        }
    }

    /// <summary>高音のリード（8 分音符のフレーズ、1〜2 kHz）</summary>
    private static void Lead(SignalGen g, double bpm, double from, double to)
    {
        double p = 60 / bpm / 2;
        double[] mel = { 1046.5, 1174.7, 1318.5, 1568, 1318.5, 1174.7, 1046.5, 880 };
        int k = 0;
        for (double t = from; t < to - 0.3; t += p, k++)
            if (k % 8 != 7) MusicLight.Pluck(g, t, mel[k % mel.Length], 0.05, 0.12, 3);
    }

    /// <summary>メリハリ: 拍の直後（0〜80 ms）の明るさと、明るさの下から 30% の値の差</summary>
    public static double Accent(LightRun r, List<double> beats, double from, double to)
    {
        var all = r.Between(from, to).Select(x => x.B).OrderBy(x => x).ToArray();
        double p30 = all.Length > 0 ? all[(int)(0.3 * (all.Length - 1))] : 0;
        return r.MeanAfter(beats.Where(t => t >= from && t < to), 0, 0.08) - p30;
    }

    public static string Format(Song s, LightRun r)
    {
        const double from = 8, to = 23;
        return $"{s.Name,-26} peaks {r.PeaksPerSec(from, to):F1}/s small {r.PeaksPerSec(from, to, 0.05):F1}/s  ripple {r.Ripple(from, to):F3}  change {r.ChangePerSec(from, to):F2}/s  accent {Accent(r, s.Beats, from, to):F2}  swing {r.Swing(from, to):F2}  " +
               $"beats {s.Beats.Count(t => t >= from && t < to) / (to - from):F1}/s";
    }
}

public static class DrumLightReportTests
{
    /// <summary>調査用: 環境変数 DRUM_REPORT=1 のとき、ドラムの曲での光り方の数値（LIGHT_SWEEP で値を変えて比べる）</summary>
    public static void TestDrumLightReport()
    {
        if (Environment.GetEnvironmentVariable("DRUM_REPORT") != "1") return;
        var songs = DrumLight.Songs().ToList();
        foreach (var spec in (Environment.GetEnvironmentVariable("LIGHT_SWEEP") ?? "").Split('|'))
        {
            var saved = BeatTuneSweepTests.Snapshot();
            try
            {
                BeatTuneSweepTests.Apply(spec);
                T.Info($"[{(spec.Length == 0 ? "current" : spec)}]");
                // 感度は使っている人の値（2）でも測る（DRUM_SENS）
                double sens = double.TryParse(Environment.GetEnvironmentVariable("DRUM_SENS"), out double sv) ? sv : 1;
                var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = MusicStyles.BeatHue, Sensitivity = sens };
                foreach (var s in songs) T.Info("  " + DrumLight.Format(s, MusicLight.Run(s.G.Buf, s.G.Fs, MusicTuning.Resolve(cfg), cfg)));
            }
            finally { BeatTuneSweepTests.Restore(saved); }
        }
    }
}

/// <summary>
/// 音の少ない版と多い版の比べ: 同じドラムとベースに、ギターのストローク（8 分）と高音のリードを重ねる。
/// 音が増えても、キックとスネアの見え方が変わらず、ギターに反応しすぎないか
/// </summary>
public static class DenseMixLight
{
    public sealed record Mix(string Name, SignalGen G, List<double> Beats, List<double> Kicks, List<double> Snares, List<double> Strums);

    public static IEnumerable<Mix> Mixes()
    {
        foreach (bool dense in new[] { false, true })
        {
            const double sec = 24, bpm = 120;
            var g = new SignalGen(48000, sec, seed: 51);
            var beats = MusicBench.Pattern(g, bpm, 0.2, sec, "x.......x.x.....", "....x.......x...", "x.x.x.x.x.x.x.x.", jitterMs: 8, seed: 51, hatAmp: 0.06);
            MusicBench.Backing(g, bpm, 0.2, sec);
            var strums = new List<double>();
            if (dense)
            {
                // ギター: 6 本の弦の和音を 8 分で（弦ごとに 8 ミリ秒ずつずらす）。倍音が多く 80 Hz〜3 kHz に広がる
                double[][] chords = { new[] { 82.4, 123.5, 164.8, 207.7, 246.9, 329.6 }, new[] { 110, 164.8, 220, 277.2, 329.6, 440 } };
                double p = 60 / bpm / 2;
                int k = 0;
                for (double t = 0.2; t < sec - 0.3; t += p, k++)
                {
                    var ch = chords[(k / 8) % 2];
                    for (int s = 0; s < ch.Length; s++) MusicLight.Pluck(g, t + 0.008 * s, ch[s], 0.035, 0.22, 8);
                    strums.Add(t);
                }
                double[] mel = { 1046.5, 1174.7, 1318.5, 1568, 1318.5, 1174.7, 1046.5, 880 };
                int m = 0;
                for (double t = 0.2; t < sec - 0.3; t += p, m++)
                    if (m % 8 != 7) MusicLight.Pluck(g, t + 0.004, mel[m % mel.Length], 0.04, 0.12, 3);
            }
            yield return new Mix(dense ? "dense (+guitar, lead)" : "sparse (drums, bass)", g, beats, g.KickTimes.ToList(), g.SnareTimes.ToList(), strums);
        }
    }

    /// <summary>ギターだけの瞬間（キック・スネアから 60 ms 以上離れたストローク。[from, to) 秒）</summary>
    public static List<double> GuitarOnly(Mix x, double from = 8, double to = 23)
    {
        var drums = x.Kicks.Concat(x.Snares).ToList();
        return x.Strums.Where(s => s >= from && s < to && drums.All(d => Math.Abs(d - s) >= 0.06)).ToList();
    }

    /// <summary>キック・スネア・ギターだけの瞬間（ドラムから 60 ms 以上離れたストローク）の直後の明るさと、ふだんの明るさ（下から 30%）</summary>
    public static (double Kick, double Snare, double Guitar, double Floor) Measure(Mix x, LightRun r, double from = 8, double to = 23)
    {
        var all = r.Between(from, to).Select(v => v.B).OrderBy(v => v).ToArray();
        double floor = all[(int)(0.3 * (all.Length - 1))];
        var drums = x.Kicks.Concat(x.Snares).ToList();
        var guitarOnly = x.Strums.Where(s => s >= from && s < to && drums.All(d => Math.Abs(d - s) >= 0.06)).ToList();
        return (r.MeanAfter(x.Kicks.Where(t => t >= from && t < to), 0, 0.08),
                r.MeanAfter(x.Snares.Where(t => t >= from && t < to), 0, 0.08),
                guitarOnly.Count > 0 ? r.MeanAfter(guitarOnly, 0, 0.08) : double.NaN,
                floor);
    }
}

public static class DenseMixReportTests
{
    /// <summary>調査用: 環境変数 DENSE_REPORT=1 のとき、音の少ない版と多い版での、キック・スネア・ギターの見え方（LIGHT_SWEEP で比べる）</summary>
    public static void TestDenseMixReport()
    {
        if (Environment.GetEnvironmentVariable("DENSE_REPORT") != "1") return;
        var mixes = DenseMixLight.Mixes().ToList();
        double sens = double.TryParse(Environment.GetEnvironmentVariable("DRUM_SENS"), out double sv) ? sv : 2;
        foreach (var spec in (Environment.GetEnvironmentVariable("LIGHT_SWEEP") ?? "").Split('|'))
        {
            var saved = BeatTuneSweepTests.Snapshot();
            try
            {
                BeatTuneSweepTests.Apply(spec);
                T.Info($"[{(spec.Length == 0 ? "current" : spec)}]");
                var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = MusicStyles.BeatHue, Sensitivity = sens };
                foreach (var x in mixes)
                {
                    var r = MusicLight.Run(x.G.Buf, x.G.Fs, MusicTuning.Resolve(cfg), cfg);
                    var (k, s, gtr, fl) = DenseMixLight.Measure(x, r);
                    T.Info($"  {x.Name,-22} kick {k:F2} snare {s:F2} guitar-only {(double.IsNaN(gtr) ? "  - " : gtr.ToString("F2"))} floor {fl:F2}  → kick +{k - fl:F2} snare +{s - fl:F2} guitar +{(double.IsNaN(gtr) ? 0 : gtr - fl):F2}  ripple {r.Ripple(8, 23):F3} small peaks {r.PeaksPerSec(8, 23, 0.05):F1}/s");
                }
            }
            finally { BeatTuneSweepTests.Restore(saved); }
        }
    }
}

public static class DenseMixLightTests
{
    /// <summary>
    /// 音の多い曲（ギターのストローク・リード入り）で、テンポをつかめていない区間でも、キックが主役（ギターだけの瞬間の 3 倍以上）で、
    /// ギターだけの瞬間には光りすぎず（+0.06 まで）、明るさの底上げもない（テンポの確かさを 0 にして確かめる。実際の曲では、確かさが下がる区間が半分ほどある）。
    /// 楽器ごとの役割で、ギターを和音のゾーンで見せるのは別に確かめる（TestDenseMixGuitarHasItsOwnChannel）
    /// </summary>
    public static void TestDenseMixKeepsDrumsInFront()
    {
        var saved = BeatTuneSweepTests.Snapshot();
        try
        {
            foreach (bool unlocked in new[] { false, true })
            {
                if (unlocked) BeatTuneSweepTests.Apply("RatioZero=5;RatioFull=6");
                var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = MusicStyles.BeatHue, Sensitivity = 2 };
                var dense = DenseMixLight.Mixes().Last();
                var r = MusicLight.Run(dense.G.Buf, dense.G.Fs, MusicTuning.Resolve(cfg), cfg);
                var (k, s, g, fl) = DenseMixLight.Measure(dense, r);
                string state = unlocked ? "unlocked" : "locked";
                // 全体で 1 色の光り方（ビートで色替え）では、ギターだけの瞬間は今までどおり +0.06 まで（楽器ごとの役割を足しても緩めない）
                T.Check(k - fl >= 0.25 && g - fl <= 0.06 && k - fl >= 3 * (g - fl),
                    $"dense mix ({state}): kick +{k - fl:F2} stands out, guitar-only +{g - fl:F2} stays small (≤ +0.06) and at most 1/3 of the kick");
                T.Check(fl <= 0.45, $"dense mix ({state}): no lifted floor ({fl:F2})");
            }
        }
        finally { BeatTuneSweepTests.Restore(saved); }
    }

    /// <summary>
    /// 楽器ごとの役割で分けたとき（ゾーンごとの決まり）: 音の多い曲で、
    ///   キック・スネア・ハイハットのゾーンは、ギターだけの瞬間に光らない（+0.06 まで）。キックのゾーンはキックではっきり光る
    ///   和音のゾーンは、ギターだけの瞬間の少し後（0.04〜0.16 秒。はじいた瞬間を、ドラムでないと確かめてから光らせる）に明るくなり、
    ///   ギターのない版（同じドラムとベース）の同じ時刻ではほとんど明るくならない。ギターが鳴っている間は、下地もない版より明るい
    /// </summary>
    public static void TestDenseMixGuitarHasItsOwnChannel()
    {
        var saved = BeatTuneSweepTests.Snapshot();
        try
        {
            foreach (bool unlocked in new[] { false, true })
            {
                if (unlocked) BeatTuneSweepTests.Apply("RatioZero=5;RatioFull=6");
                string state = unlocked ? "unlocked" : "locked";
                var mixes = DenseMixLight.Mixes().ToList();
                var (sparse, dense) = (mixes[0], mixes[1]);
                var strums = DenseMixLight.GuitarOnly(dense);
                foreach (var sound in new[] { MusicSounds.Kick, MusicSounds.Snare, MusicSounds.Hat })
                {
                    var r = InstrumentRoleRun.Run(dense.G.Buf, sound, MusicRoleSets.Band);
                    var (k, _, g, fl) = DenseMixLight.Measure(dense, r);
                    bool kickOk = sound != MusicSounds.Kick || k - fl >= 0.2;
                    T.Check(g - fl <= 0.06 && kickOk, $"dense mix ({state}): {sound} zone ignores guitar-only moments (+{g - fl:F2}){(sound == MusicSounds.Kick ? $", kick +{k - fl:F2}" : "")}");
                }
                var hd = InstrumentRoleRun.Run(dense.G.Buf, MusicSounds.Harmony, MusicRoleSets.Band);
                var hs = InstrumentRoleRun.Run(sparse.G.Buf, MusicSounds.Harmony, MusicRoleSets.Band);
                double fd = DenseMixLight.Measure(dense, hd).Floor, fs = DenseMixLight.Measure(sparse, hs).Floor;
                double withGuitar = hd.MeanAfter(strums, 0.04, 0.16) - fd, without = hs.MeanAfter(strums, 0.04, 0.16) - fs;
                // 変更の理由: ギターのない版のこの時刻（8 分のハイハットとベース）では、6.15.8 の既定の「早く出すスネア」が、ハイハットとベースの重なりを
                // スネアと取り違えて出し、それが和音の候補を打ち消していた（+0.00）。録音した 7 曲で取り違えが多い（スネアが 1 秒あたり 0.84 → 1.19）ので
                // 既定では早く出すスネアを使わない（OnsetDetector.EventTune.FastSnare）。そのためハイハットの瞬間に和音のゾーンが少し明るくなる（+0.07）。
                // 既定では、ギターのない版が +0.08 以下で、ギターのある版のほうが明るいことを確かめ、6.15.8 と同じ厳しさ（+0.03 以下）は早く出すスネアをオンにして確かめる
                T.Check(withGuitar >= 0.06 && without <= 0.08 && withGuitar > without,
                    $"dense mix ({state}): harmony zone lights after guitar-only strums (+{withGuitar:F2}), less at the same times without guitar (+{without:F2})");
                bool fsSaved = OnsetDetector.EventTune.FastSnare, epSaved = OnsetDetector.SnareTune.EarlyPositive;
                try
                {
                    OnsetDetector.EventTune.FastSnare = OnsetDetector.SnareTune.EarlyPositive = true;
                    var hdF = InstrumentRoleRun.Run(dense.G.Buf, MusicSounds.Harmony, MusicRoleSets.Band);
                    var hsF = InstrumentRoleRun.Run(sparse.G.Buf, MusicSounds.Harmony, MusicRoleSets.Band);
                    double wF = hdF.MeanAfter(strums, 0.04, 0.16) - DenseMixLight.Measure(dense, hdF).Floor, oF = hsF.MeanAfter(strums, 0.04, 0.16) - DenseMixLight.Measure(sparse, hsF).Floor;
                    T.Check(wF >= 0.06 && oF <= 0.03,
                        $"dense mix ({state}, fast snare on): harmony zone lights after guitar-only strums (+{wF:F2}), not at the same times without guitar (+{oF:F2})");
                }
                finally
                {
                    OnsetDetector.EventTune.FastSnare = fsSaved;
                    OnsetDetector.SnareTune.EarlyPositive = epSaved;
                }
                double md = InstrumentRoleRun.Mean(hd, 8, 23), ms = InstrumentRoleRun.Mean(hs, 8, 23);
                T.Check(md >= ms + 0.05, $"dense mix ({state}): harmony zone glows while the guitar plays ({md:F2} vs {ms:F2} without guitar)");
            }
        }
        finally { BeatTuneSweepTests.Restore(saved); }
    }
}

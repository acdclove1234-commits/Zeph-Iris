// ==================================================
// AiClockTests.cs - 拍の AI・楽器の AI に渡す音の時刻が、どのサンプリング周波数・どの大きさで流し込んでも、取り込みの時刻とずれない
//   （前は 192 / 96 kHz で、AI の変換器が出す数が多すぎ、出てきた数で進めていた AI の時計が最大 0.25 秒ずれていた）。
//   サンプリング周波数が変わった・取り込み直したときに、前の音が AI に残らない。AI のモデルは使わない（代わりの関数で、AI に渡る音そのものを調べる）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class AiClockTests
{
    private const double T0 = 1000;
    private static readonly int[] Rates = { 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000 };
    /// <summary>44.1 / 48 kHz の系列でない周波数（整数分の 1 にせず、そのまま変える）</summary>
    private static readonly int[] OtherRates = { 32000, 64000, 50000, 44056 };
    /// <summary>AI に渡る音の時刻と、本当の時刻の差の上限（秒）</summary>
    private const double MaxErrSec = 0.001;
    /// <summary>
    /// 441 Hz の音を混ぜたときの、もっと細かい上限（秒）。目印の山の位置は、混ぜた正弦波の傾きだけで最大 0.043 ミリ秒動く
    /// （振幅 0.05 × 2π × 441 Hz ÷ (0.2 ÷ 幅²)）。低域フィルターの遅れ（0.05〜0.08 ミリ秒）を時刻から引かないと、これを超える
    /// </summary>
    private const double TightErrSec = 0.00006;

    /// <summary>
    /// 合成の音: 小さな正弦波（Hz・振幅）に、決まった時刻の目印（幅 0.25 ミリ秒のガウスの山。振幅 0.2）を足す。サイドはミッドの 0.3 倍。
    /// 0 番目の音の時刻 = Start、目印 j の時刻 = Start + Offset + j × Spacing
    /// </summary>
    private sealed class Marked
    {
        public readonly int Fs;
        public readonly double Start, Hz, Amp, Spacing, Offset;
        public const double PulseAmp = 0.2, Sigma = 0.00025;

        public Marked(int fs, double start, double hz = 441, double amp = 0.05, double spacing = 1.3713, double offset = 0.5)
        {
            Fs = fs;
            Start = start;
            Hz = hz;
            Amp = amp;
            Spacing = spacing;
            Offset = offset;
        }

        public double Pulse(long j) => Start + Offset + j * Spacing;

        /// <summary>t（秒）にいちばん近い目印の時刻</summary>
        public double NearestPulse(double t) => Pulse(Math.Max(0, (long)Math.Round((t - Start - Offset) / Spacing)));

        /// <summary>n0 番目の音から dst.Length 個（サイドは side に。null なら作らない）</summary>
        public void Fill(Span<float> dst, Span<float> side, long n0)
        {
            double ph = 2 * Math.PI * Frac(Hz * n0 / Fs), c = Math.Cos(ph), s = Math.Sin(ph);
            double dc = Math.Cos(2 * Math.PI * Hz / Fs), ds = Math.Sin(2 * Math.PI * Hz / Fs);
            for (int i = 0; i < dst.Length; i++)
            {
                dst[i] = (float)(Amp * s);
                (c, s) = (c * dc - s * ds, s * dc + c * ds);
            }
            double tA = n0 / (double)Fs, tB = (n0 + dst.Length) / (double)Fs, reach = 6 * Sigma;
            for (long j = Math.Max(0, (long)Math.Ceiling((tA - reach - Offset) / Spacing)); Offset + j * Spacing - reach < tB; j++)
            {
                double ct = Offset + j * Spacing;
                long a = Math.Max(n0, (long)Math.Floor((ct - reach) * Fs)), b = Math.Min(n0 + dst.Length, (long)Math.Ceiling((ct + reach) * Fs));
                for (long n = a; n < b; n++)
                {
                    double x = (n / (double)Fs - ct) / Sigma;
                    dst[(int)(n - n0)] += (float)(PulseAmp * Math.Exp(-0.5 * x * x));
                }
            }
            if (side.Length > 0)
                for (int i = 0; i < dst.Length; i++) side[i] = 0.3f * dst[i];
        }

        private static double Frac(double x) => x - Math.Floor(x);
    }

    /// <summary>AI に渡った音を調べた結果（拍の AI の窓・楽器の AI の窓ごと）</summary>
    private sealed class Probe
    {
        public Marked Sig = null!;
        /// <summary>この時刻より前の目印は、調べない（取り込み直した直後の、前の音の目印）</summary>
        public double From = double.NegativeInfinity;
        public int BeatJobs, SceneWindows, SideWindows, Missing;
        public double BeatMaxErr, SceneMaxErr, SideMaxErr, LastBeatErr;
        public double BeatFirstStart = double.NaN;
        public readonly List<double> BeatHz = new(), SceneHz = new();
        /// <summary>前の音の正弦波（OldHz）の大きさ ÷ 今の正弦波の大きさ（窓ごとの最大。取り込み直したあとに前の音が残っていないか）</summary>
        public double OldHz = double.NaN, MaxOldRatio;
        public float[]? Job;
        private readonly List<float[]> _windows = new();

        /// <summary>同じ AI のまま、調べる音を sig に替えて数え直す（取り込み直したあと）</summary>
        public void Restart(Marked sig, double oldHz)
        {
            Sig = sig;
            OldHz = oldHz;
            BeatJobs = SceneWindows = SideWindows = Missing = 0;
            BeatMaxErr = SceneMaxErr = SideMaxErr = LastBeatErr = MaxOldRatio = 0;
            BeatFirstStart = double.NaN;
            BeatHz.Clear();
            SceneHz.Clear();
        }

        public (List<double>, List<double>) Analyze(float[] buf, int len)
        {
            Job = buf[..len];
            return (Peaks(Job, BeatThis.SampleRate, 0.12), new List<double>());
        }

        public void OnBeat(BeatAssist.Result r)
        {
            var job = Job!;
            double start = r.WindowEndT - job.Length / (double)BeatThis.SampleRate;
            if (double.IsNaN(BeatFirstStart)) BeatFirstStart = start;
            BeatJobs++;
            double worst = Check(r.Beats, start, r.WindowEndT, ref Missing);
            BeatMaxErr = Math.Max(BeatMaxErr, worst);
            LastBeatErr = worst;
            BeatHz.Add(Freq(job, BeatThis.SampleRate, r.Beats.Select(b => b - start).ToList(), Sig.Hz));
            if (double.IsFinite(OldHz)) MaxOldRatio = Math.Max(MaxOldRatio, Goertzel(job, BeatThis.SampleRate, OldHz) / Math.Max(1e-12, Goertzel(job, BeatThis.SampleRate, Sig.Hz)));
        }

        public bool Classify(float[] w, float[] scores)
        {
            Array.Clear(scores);
            _windows.Add((float[])w.Clone());
            return true;
        }

        public void OnScene(double endT)
        {
            double start = endT - CedTiny.WindowSamples / (double)CedTiny.SampleRate;
            for (int k = 0; k < _windows.Count && k < 2; k++)
            {
                var w = _windows[k];
                float max = w.Max();
                var p = Peaks(w, CedTiny.SampleRate, 0.6 * max);
                double worst = Check(p.Select(x => start + x).ToList(), start, endT, ref Missing);
                if (k == 0)
                {
                    SceneWindows++;
                    SceneMaxErr = Math.Max(SceneMaxErr, worst);
                    SceneHz.Add(Freq(w, CedTiny.SampleRate, p, Sig.Hz));
                }
                else
                {
                    SideWindows++;
                    SideMaxErr = Math.Max(SideMaxErr, worst);
                }
            }
            _windows.Clear();
        }

        /// <summary>
        /// 見つけた目印（found。秒）と本当の目印の差の最大（窓の両端 5 ミリ秒を除く）。窓の中（両端 7 ミリ秒を除く）の本当の目印を、
        /// すべて見つけたか・目印でない山がないかも数える（足りない・余計な山があれば missing を 1 つ増やす）
        /// </summary>
        private double Check(List<double> found, double start, double end, ref int missing)
        {
            double lo = Math.Max(start + 0.005, From), hi = end - 0.005, worst = 0;
            var hit = new HashSet<double>();
            bool stray = false;
            foreach (double t in found)
            {
                if (t < lo || t > hi) continue;
                double p = Sig.NearestPulse(t);
                worst = Math.Max(worst, Math.Abs(t - p));
                if (Math.Abs(t - p) > 0.01) stray = true;
                hit.Add(p);
            }
            for (long j = 0; Sig.Pulse(j) <= hi - 0.002; j++)
                if (Sig.Pulse(j) >= lo + 0.002 && !hit.Contains(Sig.Pulse(j))) stray = true;
            if (stray) missing++;
            return worst;
        }
    }

    /// <summary>a（rate の音）の中で、thr より大きく前後 2 ミリ秒でいちばん高い山の位置（秒。前後の 3 点の放物線で細かく）</summary>
    private static List<double> Peaks(float[] a, int rate, double thr)
    {
        var res = new List<double>();
        int w = rate / 500;
        for (int i = 1; i < a.Length - 1; i++)
        {
            if (a[i] < thr || a[i] < a[i - 1] || a[i] <= a[i + 1]) continue;
            bool top = true;
            for (int k = Math.Max(0, i - w); k <= Math.Min(a.Length - 1, i + w) && top; k++) if (a[k] > a[i]) top = false;
            if (!top) continue;
            double y0 = a[i - 1], y1 = a[i], y2 = a[i + 1], den = y0 - 2 * y1 + y2;
            res.Add((i + (den != 0 ? 0.5 * (y0 - y2) / den : 0)) / rate);
        }
        return res;
    }

    /// <summary>正弦波の周波数（目印の前後 4 ミリ秒を除いた、0 を下から上に横切る間隔から）</summary>
    private static double Freq(float[] a, int rate, List<double> pulses, double hz)
    {
        double sum = 0, prev = double.NaN;
        int periods = 0;
        for (int i = 1; i < a.Length; i++)
        {
            if (!(a[i - 1] < 0 && a[i] >= 0)) continue;
            double t = (i - 1 + a[i - 1] / (double)(a[i - 1] - a[i])) / rate;
            bool near = pulses.Any(p => Math.Abs(p - t) < 0.004);
            if (!near && double.IsFinite(prev) && Math.Abs((t - prev) * hz - 1) < 0.2)
            {
                sum += t - prev;
                periods++;
            }
            prev = near ? double.NaN : t;
        }
        return periods > 0 ? periods / sum : double.NaN;
    }

    /// <summary>a の中の hz の正弦波の大きさ（Goertzel）</summary>
    private static double Goertzel(float[] a, int rate, double hz)
    {
        double w = 2 * Math.Cos(2 * Math.PI * hz / rate), s1 = 0, s2 = 0;
        foreach (float x in a)
        {
            double s0 = x + w * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - w * s1 * s2)) / a.Length;
    }

    /// <summary>
    /// アプリと同じつなぎ方: 分析の周波数（AnalysisRate）で拍の AI・楽器の AI を作り（AudioCapture）、AI の代わりに probe で調べる。
    /// full = 取り込みの周波数で作る（エンジンは届いたままの音を渡し、AI の側で落とす。調べる用の作り方）
    /// </summary>
    private static (AudioEngine E, BeatAssist A, SceneTagger S) Make(int fs, Probe probe, bool full = false)
    {
        var e = new AudioEngine(fs);
        int rate = full ? e.SampleRate : e.AnalysisRate;
        var a = new BeatAssist(probe.Analyze, rate) { Inline = true };
        a.Analyzed = probe.OnBeat;
        var s = new SceneTagger(probe.Classify, rate) { Inline = true, Diag = (t, _, _) => probe.OnScene(t) };
        e.Assist = a;
        e.Tagger = s;
        return (e, a, s);
    }

    /// <summary>流し込む大きさ: 10 ミリ秒（取り込みと同じ）・512 個・いろいろ混ぜたもの</summary>
    private static Func<int> Sizes(string kind, int fs, int seed)
    {
        var rng = new Random(seed);
        return kind switch
        {
            "10ms" => () => fs / 100,
            "512" => () => 512,
            _ => () => rng.Next(4) switch { 0 => fs / 100, 1 => 512, 2 => 441, _ => rng.Next(1, fs / 25) },
        };
    }

    /// <summary>sig を n0 番目から sec 秒、size の大きさずつエンジンに流す（ステレオ）。次の番号を返す</summary>
    private static long Feed(AudioEngine e, Marked sig, long n0, double sec, Func<int> size)
    {
        long end = n0 + (long)Math.Round(sec * sig.Fs);
        var mid = new float[sig.Fs / 10];
        var side = new float[mid.Length];
        while (n0 < end)
        {
            int n = (int)Math.Min(Math.Min(size(), mid.Length), end - n0);
            sig.Fill(mid.AsSpan(0, n), side.AsSpan(0, n), n0);
            e.Push(mid.AsSpan(0, n), side.AsSpan(0, n), sig.Start + n0 / (double)sig.Fs);
            n0 += n;
        }
        return n0;
    }

    /// <summary>
    /// 44.1〜384 kHz のそれぞれで、取り込みと同じつなぎ方（AudioEngine が分析の周波数に落とした音を、分析の周波数で作った AI に渡す）で 10 分流し、
    /// AI に渡る音の目印の時刻が本当の時刻と 1 ミリ秒以内（途中のどの窓でも・最後の窓でも）、22,050 / 16,000 Hz にした音の数がちょうど、正弦波の高さが変わらない。
    /// 流し込む大きさは、10 ミリ秒・512 個・いろいろ混ぜたもの（10 分はいろいろ混ぜたものだけ。ほかは 3 分）。
    /// 取り込みの周波数で作った AI（AI の側で落とす）も 3 分（10 ミリ秒ずつ）。44.1 / 48 kHz の系列でない周波数（32・64・50・44.056 kHz）も 3 分（混ぜたもの）
    /// </summary>
    public static void TestAiClockExactAtAnyRate()
    {
        // 周波数と流し込む大きさの組ごとに、別々のスレッドで流す（速く終わるように。結果はあとで順に出す）
        var runs = Rates.SelectMany(fs => new[] { "mix", "10ms", "512", "full" }.Select(kind => (Fs: fs, Kind: kind)))
            .Concat(OtherRates.Select(fs => (Fs: fs, Kind: "other"))).ToList();
        var done = new (Probe P, AudioEngine E, BeatAssist A, SceneTagger S, long N)[runs.Count];
        Parallel.For(0, runs.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
        {
            var (fs, kind) = runs[i];
            var probe = new Probe { Sig = new Marked(fs, T0) };
            var (e, a, s) = Make(fs, probe, full: kind == "full");
            long n = Feed(e, probe.Sig, 0, kind == "mix" ? 600 : 180, Sizes(kind switch { "full" => "10ms", "other" => "mix", _ => kind }, fs, fs + kind.Length));
            done[i] = (probe, e, a, s, n);
        });
        for (int i = 0; i < runs.Count; i++)
        {
            var (fs, kind) = runs[i];
            var (probe, e, a, s, n) = done[i];
            double sec = kind == "mix" ? 600 : 180;
            var rb = a.Resampler;
            var rsc = s.Resampler;
            // AI に渡した数は、落とした数（取り込みの周波数で作った AI なら届いた数）とちょうど同じ。
            // 22,050 / 16,000 Hz にした数は、そのちょうど（入力の数 × 出力 ÷ 入力）から 2 個以内
            int rate = a.InputRate;
            long inExpect = n / (fs / rate);
            double outB = rb.InputCount * (double)BeatThis.SampleRate / rate, outS = rsc.InputCount * (double)CedTiny.SampleRate / rate;
            double bHz = probe.BeatHz.Count > 0 ? probe.BeatHz[^1] : double.NaN, sHz = probe.SceneHz.Count > 0 ? probe.SceneHz.Average() : double.NaN;
            string name = $"{fs} Hz, {kind switch { "full" => $"AI made at {fs} Hz, 10ms", "other" => "mix", _ => kind }} pushes, {sec / 60:F0} min";
            T.Info($"{name}: beat-AI windows {probe.BeatJobs} max error {probe.BeatMaxErr * 1000:F3} ms (last {probe.LastBeatErr * 1000:F3} ms), " +
                   $"scene windows {probe.SceneWindows} max error {probe.SceneMaxErr * 1000:F3} ms (side {probe.SideWindows}: {probe.SideMaxErr * 1000:F3} ms), " +
                   $"22,050 Hz length {rb.OutputCount - outB:+0.0;-0.0} samples, 16 kHz {rsc.OutputCount - outS:+0.0;-0.0}, tone {bHz:F3} / {sHz:F3} Hz of {probe.Sig.Hz}, missed markers in {probe.Missing} windows");
            bool ok = probe.BeatJobs >= (sec - 20) / 15 && probe.SceneWindows >= sec / 2 && probe.SideWindows >= sec / 2
                      && probe.BeatMaxErr <= MaxErrSec && probe.SceneMaxErr <= MaxErrSec && probe.SideMaxErr <= MaxErrSec && probe.Missing == 0;
            T.Check(ok, $"{name}: every marker the AIs see is within 1 ms of its real time, all through the run (beat {probe.BeatMaxErr * 1000:F3} ms, scene {probe.SceneMaxErr * 1000:F3} ms, side {probe.SideMaxErr * 1000:F3} ms)");
            T.Check(Math.Max(probe.BeatMaxErr, Math.Max(probe.SceneMaxErr, probe.SideMaxErr)) <= TightErrSec,
                    $"{name}: no filter delay is left in the AI clocks (worst {Math.Max(probe.BeatMaxErr, Math.Max(probe.SceneMaxErr, probe.SideMaxErr)) * 1000:F3} ms; the tone alone moves a marker by up to 0.043 ms)");
            T.Check(Math.Abs(rb.InputCount - inExpect) <= 1 && Math.Abs(rb.OutputCount - outB) <= 2 && Math.Abs(rsc.OutputCount - outS) <= 2,
                    $"{name}: exact lengths (fed {rb.InputCount} of {inExpect} at {rate} Hz; 22,050 Hz {rb.OutputCount - outB:+0.0;-0.0}, 16 kHz {rsc.OutputCount - outS:+0.0;-0.0} samples)");
            T.Check(Math.Abs(bHz / probe.Sig.Hz - 1) < 1e-4 && Math.Abs(sHz / probe.Sig.Hz - 1) < 1e-4, $"{name}: the tone keeps its pitch ({bHz:F3} / {sHz:F3} Hz)");
            a.Dispose();
            s.Dispose();
        }
    }

    /// <summary>
    /// 楽器の AI のサイド: サイドのない回（無音で埋めた回・モノラル）や、届かなかった間（無音で埋める）があっても、サイドの変換器は
    /// ミッドと同じ数・同じ位置の音を出し続け、サイドのリングは「サイドのない回にサイドの無音を流した」のとまったく同じ音になる。
    /// 1 回に出てくる数がちょうどでない大きさ（44.1 kHz で 448 個 = 162.5 個 など）で調べる
    /// （前は、サイドのない回のあとにサイドの変換器だけ最初からにしていたので、ミッドと出てくる数がずれ、それをそろえるために
    /// サイドに 0 を挟んだり、本物の音を上書きしたりしていた）
    /// </summary>
    public static void TestSceneSideStaysInStepWithMid()
    {
        foreach (var (fs, size) in new[] { (44100, 448), (48000, 441), (32000, 300), (96000, 1000), (192000, 1797) })
        {
            var sig = new Marked(fs, T0);
            var tagger = new SceneTagger((_, s) => { Array.Clear(s); return true; }, fs) { Inline = true };
            // 比べる相手: 同じ設定の変換器 2 つに、サイドのない回はサイドに無音を流したもの
            var refMid = new AiResampler(fs, CedTiny.SampleRate);
            var refSide = new AiResampler(fs, CedTiny.SampleRate);
            var outMid = new List<float>();
            var outSide = new List<float>();
            SamplesSink sm = s => { foreach (float x in s) outMid.Add(x); };
            SamplesSink ss = s => { foreach (float x in s) outSide.Add(x); };
            var mid = new float[size];
            var side = new float[size];
            var zeros = new float[size];
            long n0 = 0;
            int monoPushes = 0, gaps = 0;
            for (int k = 0; k < 3000; k++)
            {
                if (k % 37 == 36)
                {
                    n0 += fs / 50;   // 20 ミリ秒の音が届かない（無音で埋める）
                    gaps++;
                }
                sig.Fill(mid, side, n0);
                double t = sig.Start + n0 / (double)fs;
                bool mono = k % 7 == 3 || k % 50 is >= 20 and < 24;   // サイドのない回（モノラル・無音で埋めた回）
                if (mono)
                {
                    tagger.Push(mid, t);
                    monoPushes++;
                }
                else tagger.Push(mid, side, t);
                long fill = refMid.Sync(t);
                if (fill < 0)
                {
                    refSide.Reset();
                    fill = 0;
                }
                if (fill > 0)
                {
                    refMid.PushSilence(fill, sm);
                    refSide.PushSilence(fill, ss);
                }
                refMid.Push(mid, sm);
                refSide.Push(mono ? zeros : side, ss);
                n0 += size;
            }
            var (rm, rsd) = tagger.RingTail(30000);
            int n = Math.Min(rm.Length, outMid.Count);
            double dMid = 0, dSide = 0, peak = 0;
            for (int i = 0; i < n; i++)
            {
                dMid = Math.Max(dMid, Math.Abs(rm[rm.Length - n + i] - outMid[outMid.Count - n + i]));
                dSide = Math.Max(dSide, Math.Abs(rsd[rsd.Length - n + i] - outSide[outSide.Count - n + i]));
                peak = Math.Max(peak, Math.Abs(outSide[outSide.Count - n + i]));
            }
            string name = $"{fs} Hz, {size}-sample pushes ({size * (double)CedTiny.SampleRate / fs:F2} outputs each), {monoPushes} pushes without side, {gaps} gaps";
            T.Info($"{name}: last {n} samples differ from the reference by at most {dMid:E1} (mid) / {dSide:E1} (side, peak {peak:F3}); outputs {tagger.Resampler.OutputCount} vs {refMid.OutputCount} / {refSide.OutputCount}");
            T.Check(n >= 30000 && dMid == 0 && dSide == 0 && refMid.OutputCount == refSide.OutputCount && tagger.Resampler.OutputCount == refMid.OutputCount,
                    $"{name}: the side ring stays in step with the mid ring (mid diff {dMid:E1}, side diff {dSide:E1})");
            tagger.Dispose();
        }
    }

    /// <summary>
    /// 出力先のサンプリング周波数が変わった（48 → 192 kHz・44.1 → 384 kHz）: AudioCapture と同じく、AI を捨てて新しい分析・新しい周波数の AI を作る。
    /// 変わったあとの目印の時刻が 1 ミリ秒以内で、前の音（別の高さの正弦波）が AI に残らない。同じ周波数で取り込み直した（AudioEngine.Reset）ときも、
    /// 前の音が変換器に残らない。取り込みが少し途切れた（30 ミリ秒）ら無音で埋めて時刻を保ち、大きく飛んだ（1 秒）らたまった音を捨てる
    /// </summary>
    public static void TestAiClockAcrossRateChange()
    {
        foreach (var (ra, rb) in new[] { (48000, 192000), (44100, 384000), (192000, 48000) })
        {
            var pa = new Probe { Sig = new Marked(ra, T0, hz: 997) };
            var (ea, aa, sa) = Make(ra, pa);
            Feed(ea, pa.Sig, 0, 40, Sizes("10ms", ra, 1));
            // 取り込み直す（出力先の周波数が変わった）: AudioCapture と同じく、AI を捨てて、新しい分析と新しい周波数の AI を作る
            aa.Dispose();
            sa.Dispose();
            double tB = T0 + 40 + 0.3;
            var pb = new Probe { Sig = new Marked(rb, tB, hz: 613, spacing: 0.9871, offset: 0.37), OldHz = 997 };
            var (eb, ab, sb) = Make(rb, pb);
            long n = Feed(eb, pb.Sig, 0, 30, Sizes("mix", rb, 2));
            string name = $"{ra} → {rb} Hz";
            // 違う周波数の AI を付けた（作り直し忘れ）: 違う周波数の音は渡さず（44.1 kHz の AI に 48 kHz の系列の音を入れない）、ログに 1 回だけ書く
            var stale = new BeatAssist(pb.Analyze, rb % 44100 == 0 ? 48000 : 44100) { Inline = true };
            var staleTagger = new SceneTagger(pb.Classify, rb % 44100 == 0 ? 48000 : 44100) { Inline = true };
            var es = new AudioEngine(rb) { Assist = stale, Tagger = staleTagger };
            Feed(es, pb.Sig, 0, 2, Sizes("10ms", rb, 6));
            T.Info($"{name}: an AI made for {stale.InputRate} Hz on a {rb} Hz engine got {stale.Resampler.InputCount} / {staleTagger.Resampler.InputCount} samples, {es.AiRateMismatches} warnings");
            T.Info($"{name}: before the change {pa.BeatJobs} beat windows (max {pa.BeatMaxErr * 1000:F3} ms), after {pb.BeatJobs} (max {pb.BeatMaxErr * 1000:F3} ms), scene {pb.SceneWindows} (max {pb.SceneMaxErr * 1000:F3} ms); " +
                   $"first beat window after the change starts {(pb.BeatFirstStart - tB) * 1000:F1} ms after it; old tone / new tone at most {pb.MaxOldRatio:E1}");
            T.Check(pa.BeatJobs > 0 && pa.BeatMaxErr <= MaxErrSec && pa.SceneMaxErr <= MaxErrSec, $"{name}: before the change the AI clocks are right ({pa.BeatMaxErr * 1000:F3} / {pa.SceneMaxErr * 1000:F3} ms)");
            T.Check(pb.BeatJobs > 0 && pb.BeatMaxErr <= MaxErrSec && pb.SceneMaxErr <= MaxErrSec && pb.SideMaxErr <= MaxErrSec && pb.Missing == 0,
                    $"{name}: after the change the AI clocks are right ({pb.BeatMaxErr * 1000:F3} / {pb.SceneMaxErr * 1000:F3} / {pb.SideMaxErr * 1000:F3} ms)");
            T.Check(pb.BeatFirstStart >= tB - 0.002 && pb.MaxOldRatio < 0.01, $"{name}: nothing from before the change reaches the new AIs (first window from {(pb.BeatFirstStart - tB) * 1000:F1} ms, old tone {pb.MaxOldRatio:E1})");
            T.Check(stale.Resampler.InputCount == 0 && staleTagger.Resampler.InputCount == 0 && es.AiRateMismatches == 2,
                    $"{name}: AIs made for another rate are not fed and are logged once each ({stale.Resampler.InputCount} / {staleTagger.Resampler.InputCount} samples, {es.AiRateMismatches} warnings)");
            stale.Dispose();
            staleTagger.Dispose();

            // 同じ周波数で取り込み直した（AudioEngine.Reset。出力先が同じ周波数のまま変わった）: 同じ AI のまま、前の音を捨てる
            eb.Reset();
            double tC = tB + n / (double)rb + 0.2;
            var sigC = new Marked(rb, tC, hz: 997, spacing: 1.1111, offset: 0.21);
            pb.Restart(sigC, 613);
            long m = Feed(eb, sigC, 0, 15, Sizes("10ms", rb, 3));
            // 少し途切れる（30 ミリ秒の音が届かない）: 無音で埋めて、時刻はそのまま
            m += (long)(0.03 * rb);
            m = Feed(eb, sigC, m, 15, Sizes("10ms", rb, 4));
            T.Info($"{name}, restarted at the same rate (Reset) with a 30 ms gap later: {pb.BeatJobs} beat windows (max {pb.BeatMaxErr * 1000:F3} ms), scene {pb.SceneWindows} (max {pb.SceneMaxErr * 1000:F3} ms), " +
                   $"first window from {(pb.BeatFirstStart - tC) * 1000:F1} ms, old tone {pb.MaxOldRatio:E1}");
            T.Check(pb.BeatJobs > 0 && pb.BeatMaxErr <= MaxErrSec && pb.SceneMaxErr <= MaxErrSec && pb.Missing == 0 && pb.BeatFirstStart >= tC - 0.002 && pb.MaxOldRatio < 0.01,
                    $"{name}: after Reset and a 30 ms gap the AI clocks are right and the old sound is gone ({pb.BeatMaxErr * 1000:F3} / {pb.SceneMaxErr * 1000:F3} ms, old tone {pb.MaxOldRatio:E1})");

            // 大きく飛んだ（1 秒）: たまった音を捨てて、そこから貯め直す
            var (snapBefore, _) = ab.Snapshot();
            double tJump = tC + m / (double)rb + 1.0;
            var pd = new Marked(rb, tJump, hz: 997, spacing: 1.1111, offset: 0.21);
            Feed(eb, pd, 0, 0.5, Sizes("10ms", rb, 5));
            var (snap, start) = ab.Snapshot();
            T.Check(snapBefore.Length > 10 * BeatThis.SampleRate && snap.Length <= 0.5 * BeatThis.SampleRate + 2 && Math.Abs(start - tJump) < 0.002,
                    $"{name}: a 1 s jump empties the beat-AI ring ({snapBefore.Length} → {snap.Length} samples, starting {(start - tJump) * 1000:F2} ms from the jump)");
            ab.Dispose();
            sb.Dispose();
        }
    }
}

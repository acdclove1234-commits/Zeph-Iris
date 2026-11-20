// ==================================================
// SoundSyncTests.cs - 音楽連動は、音楽でも映画・ゲームの音でも、選んだ光り方のとおりに光ることを確かめる（合成した音。録音がなくても動く）
//   映画・ゲームの切り替えはない: 静かな場面で落ち着く・曲が止まったら前の曲の拍で光らない・拍の AI が古い拍を押しつけない・
//   「激しい点滅を抑える」が遅いデバイスでも守られる・ずっと続く音（エンジン・風）で小さいゾーンがちらつかない、を見る。
//   アプリと同じ流れ（本物の分析に 10 ミリ秒ずつ流し、60 コマ／秒で描き、RgbDeviceChannel の送る・送らないまで通す）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class SoundSyncTests
{
    private const double Tick = 1.0 / 60, T0 = 1000;

    /// <summary>1 つの光り方 × デバイス × 「激しい点滅を抑える」の、送って LED に出ている明るさ（60 コマ／秒）</summary>
    private sealed class Lane
    {
        public required string Style, Dev;
        public required bool Rf;
        public required RgbDeviceChannel Ch;
        public required (int S, int Len)[] Areas;
        public Rgb[] Seen = Array.Empty<Rgb>();
        /// <summary>コマごとの区域の平均（区域 0 = デバイス全体）</summary>
        public readonly List<float[]> A = new();
        /// <summary>コマごとの、最初の LED の色（明るさを一定にするグラボでは、色だけが変わる）</summary>
        public readonly List<Rgb> C = new();
        public int[] Prev = Array.Empty<int>(), Dir = Array.Empty<int>(), Flip = Array.Empty<int>();
        public readonly List<double> FlipT = new();
        /// <summary>コマごとの、波紋がそれまでに出した輪の数（波紋のときだけ。RippleEffect.Spawned）</summary>
        public readonly List<int> Rings = new();
        /// <summary>コマごとの、波紋が最後に出した輪の生まれた時刻（音の時刻。波紋のときだけ。RippleEffect.LastT0）</summary>
        public readonly List<double> RingT0 = new();
        /// <summary>送った時刻（秒）</summary>
        public readonly List<double> SendT = new();
        /// <summary>コマごとの、LED に出ている色の、いちばん強い成分（R・G・B の最大）がいちばん小さい LED の値（0〜1）</summary>
        public readonly List<float> MinLed = new();
        public string Key => $"{Style} {Dev} RF {(Rf ? "on" : "off")}";
    }

    private sealed class Run
    {
        public readonly List<Lane> Lanes = new();
        public readonly List<BeatLock> Lock = new();
        public int Steered;
        public readonly List<double> SteerT = new();
        /// <summary>コマごとの公開しているテンポ（BPM。テンポがなければ 0）</summary>
        public readonly List<double> Bpm = new();
        public double B(Lane l, int f) => l.A[f][0];
    }

    private static RgbDevice DeviceOf(string name) => name switch
    {
        "strip24" => new RgbDevice(0, "Strip", 24, 4, null, "HID: x"),
        "zone4" => new RgbDevice(1, "Small", 4, 4, null, "HID: x"),
        "led1" => new RgbDevice(2, "One", 1, 4, null, "HID: x"),
        "direct" => new RgbDevice(3, "GpuDirect", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }) { DirectGpu = true },
        "dram" => new RgbDevice(5, "DRAM", 8, RgbDevice.TypeDram, null, "I2C: dram"),
        "gpu" => new RgbDevice(6, "GPU", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }),
        _ => throw new ArgumentException(name),
    };

    /// <summary>FlashGuard と同じ区域: デバイス全体・2 つ以上ならゾーンごと・8 個以上なら 4 分の 1 ずつ</summary>
    private static (int, int)[] AreasOf(RgbDevice d)
    {
        var l = new List<(int, int)> { (0, d.LedCount) };
        if (d.Zones is { Count: > 1 } z)
        {
            int s = 0;
            foreach (var n in z) { l.Add((s, n)); s += n; }
        }
        if (d.LedCount >= FlashGuard.QuarterMinLeds)
            for (int q = 0; q < 4; q++) { int s = q * d.LedCount / 4, e = (q + 1) * d.LedCount / 4; l.Add((s, e - s)); }
        return l.ToArray();
    }

    /// <summary>
    /// 音を流して、指定した光り方・デバイス・「激しい点滅を抑える」の組み合わせを全部描く。
    /// ai を渡したら、拍の AI の代わりに使う（音の時刻を受け取って、窓の中の拍を返す）
    /// </summary>
    private static Run Play(float[] buf, int fs, IEnumerable<(string Style, string Dev, bool Rf)> lanes, string intensity = Intensities.Moderate,
        Func<double, double, List<double>>? ai = null)
    {
        var baseCfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyIntensity(baseCfg.Music, intensity);
        var engine = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(baseCfg) };
        double audioT = T0;
        BeatAssist? assist = null;
        if (ai is not null)
        {
            // 窓の終わり = いま流し終えた音の時刻（Inline なので、Push の中で調べる）
            assist = new BeatAssist((a, n) =>
            {
                double end = audioT, start = end - n / (double)BeatThis.SampleRate;
                return (ai(start, end).Select(t => t - start).ToList(), new List<double>());
            }, engine.AnalysisRate) { Inline = true };
            engine.Assist = assist;
        }
        var run = new Run();
        foreach (var (style, devName, rf) in lanes)
        {
            var dev = DeviceOf(devName);
            var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = style, Brightness = 100 };
            MusicTuning.ApplyIntensity(cfg.Music, intensity);
            cfg.Music.ReduceFlashing = rf;
            var ch = new RgbDeviceChannel(dev, T0) { SharedColor = new SharedColor() };
            ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = dev.Name }, false);
            int n = dev.LedCount;
            run.Lanes.Add(new Lane { Style = style, Dev = devName, Rf = rf, Ch = ch, Areas = AreasOf(dev), Seen = new Rgb[n], Prev = new int[n], Dir = new int[n], Flip = new int[n] });
        }
        int pos = 0, chunk = fs / 100, lastSteer = 0;
        for (int k = 0; ; k++)
        {
            double sec = k * Tick;
            if (sec + 0.15 > buf.Length / (double)fs) break;
            while (pos < buf.Length && pos / (double)fs < sec + 0.1)
            {
                int m = Math.Min(chunk, buf.Length - pos);
                audioT = T0 + (pos + m) / (double)fs;
                engine.Push(new ReadOnlySpan<float>(buf, pos, m), T0 + pos / (double)fs);
                pos += m;
            }
            double t = T0 + sec;
            var beat = engine.Beat;
            run.Lock.Add(engine.BeatLock);
            run.Bpm.Add(beat.HasTempo ? beat.Bpm : 0);
            if (assist is not null && assist.Steered != lastSteer) { lastSteer = assist.Steered; run.SteerT.Add(sec); }
            foreach (var L in run.Lanes)
            {
                var ch = L.Ch;
                ch.Prepare(t, Tick, engine.Features, engine.Events, beat, null, false, false);
                bool drawn = ch.Render();
                L.Rings.Add(ch.Renderer.CurrentEffect is RippleEffect rp ? rp.Spawned : 0);
                L.RingT0.Add(ch.Renderer.CurrentEffect is RippleEffect rq ? rq.LastT0 - T0 : double.NaN);
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
                Record(L, sec);
            }
        }
        run.Steered = assist?.Steered ?? 0;
        assist?.Dispose();
        return run;
    }

    private static void Record(Lane L, double sec)
    {
        var f = L.Seen;
        var ar = new float[L.Areas.Length];
        for (int a = 0; a < ar.Length; a++)
        {
            var (s, len) = L.Areas[a];
            int sum = 0;
            for (int i = s; i < s + len; i++) sum += Math.Max(f[i].R, Math.Max(f[i].G, f[i].B));
            ar[a] = sum / (255f * Math.Max(1, len));
        }
        // ちらつき: LED ごとの明るさの向き（2 段以上の変化）が、4 コマ以内に戻った回数
        int frame = L.A.Count;
        for (int i = 0; i < f.Length; i++)
        {
            int v = Math.Max(f[i].R, Math.Max(f[i].G, f[i].B));
            if (frame > 0)
            {
                int d = Math.Abs(v - L.Prev[i]) >= 2 ? Math.Sign(v - L.Prev[i]) : 0;
                if (d != 0)
                {
                    if (L.Dir[i] != 0 && d != L.Dir[i])
                    {
                        if (frame - L.Flip[i] <= 4) L.FlipT.Add(sec);
                        L.Flip[i] = frame;
                    }
                    L.Dir[i] = d;
                }
            }
            L.Prev[i] = v;
        }
        L.A.Add(ar);
        L.C.Add(f.Length > 0 ? f[0] : default);
        int minLed = 255;
        foreach (var q in f) minLed = Math.Min(minLed, Math.Max(q.R, Math.Max(q.G, q.B)));
        L.MinLed.Add(f.Length > 0 ? minLed / 255f : 0);
    }

    // ===== 測り方

    private static IEnumerable<int> Frames(Lane l, double a, double b)
    {
        for (int i = Math.Max(0, (int)(a * 60)); i < Math.Min(l.A.Count, (int)(b * 60)); i++) yield return i;
    }

    private static double P95(Lane l, double a, double b)
    {
        var x = Frames(l, a, b).Select(i => (double)l.A[i][0]).OrderBy(v => v).ToList();
        return x.Count == 0 ? 0 : x[(int)(0.95 * (x.Count - 1))];
    }

    private static double Mean(Lane l, double a, double b) => Frames(l, a, b).Select(i => (double)l.A[i][0]).DefaultIfEmpty(0).Average();

    /// <summary>見て分かる光り始め: デバイス全体の明るさが 0.1 秒以内に 0.1 以上上がった始まり（0.1 秒以上あける）</summary>
    private static List<double> Flashes(Lane l, double thr = 0.1)
    {
        var res = new List<double>();
        bool run = false;
        int last = -1000;
        for (int i = 1; i < l.A.Count; i++)
        {
            float lo = l.A[i - 1][0];
            for (int j = Math.Max(0, i - 6); j < i; j++) lo = Math.Min(lo, l.A[j][0]);
            bool up = l.A[i][0] - lo >= thr;
            if (up && !run && i - last >= 6) { res.Add(i / 60.0); last = i; }
            run = up;
        }
        return res;
    }

    private static double Rate(List<double> ts, double a, double b) => ts.Count(t => t >= a && t < b) / (b - a);

    /// <summary>FlashGuard の決まり（0.1 秒以内に 0.2 以上明るくなった始まり）を、LED に出ている値で数えた、どの 1 秒の間でもの最大（区域ごとの最大）</summary>
    private static int StrongMax(Lane l, double a, double b)
    {
        int best = 0;
        for (int k = 0; k < l.Areas.Length; k++)
        {
            var starts = new List<double>();
            bool run = false;
            for (int i = 1; i < l.A.Count; i++)
            {
                float m = l.A[i][k], lo = m;
                for (int j = i - 1; j >= 0; j--)
                {
                    if (j < i - 1 && (i - j) / 60.0 > 0.1 + 0.004) break;
                    lo = Math.Min(lo, l.A[j][k]);
                }
                bool up = m - lo >= FlashGuard.StrongRise - 1e-4f;
                if (up && !run && i / 60.0 >= a && i / 60.0 < b) starts.Add(i / 60.0);
                run = up;
            }
            for (int i = 0; i < starts.Count; i++)
            {
                int c = 0;
                for (int j = i; j < starts.Count && starts[j] - starts[i] < 1 - 1e-4; j++) c++;
                best = Math.Max(best, c);
            }
        }
        return best;
    }

    /// <summary>ちらつき（LED 1 個・1 秒あたり、向きが 4 コマ以内に戻った回数）</summary>
    private static double Jit(Lane l, int leds, double a, double b) => l.FlipT.Count(t => t >= a && t < b) / (b - a) / Math.Max(1, leds);

    // ===== 合成した音

    /// <summary>低い音だけを残した雑音（エンジン・風のゴーッという音。fc は 1 次のフィルターの角の周波数）。rmsDb はおおよそ</summary>
    private static void Rumble(SignalGen g, Random r, double rmsDb, double from, double to, double fc, double gustHz = 0)
    {
        int a = (int)(from * g.Fs), b = Math.Min(g.Buf.Length, (int)(to * g.Fs));
        double k = 1 - Math.Exp(-2 * Math.PI * fc / g.Fs), y1 = 0, y2 = 0, sum = 0;
        var tmp = new double[b - a];
        for (int i = 0; i < tmp.Length; i++)
        {
            y1 += k * (r.NextDouble() * 2 - 1 - y1);
            y2 += k * (y1 - y2);
            double gust = gustHz > 0 ? 0.7 + 0.3 * Math.Sin(2 * Math.PI * gustHz * i / g.Fs) : 1;
            tmp[i] = y2 * gust;
            sum += tmp[i] * tmp[i];
        }
        double s = Math.Pow(10, rmsDb / 20) / Math.Sqrt(sum / Math.Max(1, tmp.Length));
        for (int i = 0; i < tmp.Length; i++) g.Buf[a + i] = (float)Math.Clamp(g.Buf[a + i] + s * tmp[i], -1, 1);
    }

    private static readonly double[] Speech = { 140, 175, 160, 190, 150, 210, 130, 170 };

    /// <summary>せりふ: 1.6 秒しゃべって 0.9 秒あける（音の高さは話し声くらい。ところどころに「パ・タ」の破裂音）</summary>
    private static void Dialogue(SignalGen g, Random r, double from, double to, double amp)
    {
        for (double t = from; t < to - 1.6; t += 2.5)
        {
            g.VocalTone(t, t + 1.6, Speech, 0.2, amp: amp, vibratoCents: 10);
            for (double s = t; s < t + 1.6; s += 0.2) if (r.NextDouble() < 0.4) g.Snare(s, amp * 0.6);
        }
    }

    /// <summary>
    /// 映画の静かな場面: 0〜10 秒 撃ち合い（−20 dBFS の雑音・銃声・爆発）、10〜40 秒 夜の環境音（−48 dBFS の雑音と虫の声）、40〜46 秒 小さなせりふ
    /// </summary>
    private static SignalGen QuietSceneAfterAction()
    {
        var g = new SignalGen(48000, 46, seed: 11);
        var r = new Random(11);
        g.Pink(-58, 0, 46);
        g.Pink(-20, 0.5, 10);
        for (double t = 1; t < 9.5; t += 0.3 + r.NextDouble() * 0.8) g.Snare(t, 0.5);
        foreach (double te in new[] { 1.5, 4.2, 7.9 }) { g.Kick(te, 0.9); g.White(-18, te, te + 0.4); }
        g.Pink(-49, 10, 40);
        for (double t = 10.3; t < 40; t += 0.25 + r.NextDouble() * 0.6) g.Tone(4200 + 300 * r.NextDouble(), 0.004, t, t + 0.04);
        Dialogue(g, r, 40, 46, 0.05);
        return g;
    }

    /// <summary>無音のあとの部屋の音: 0〜4 秒 −62 dBFS、4〜12 秒 デジタルの無音、12〜30 秒 −55 dBFS の部屋の音と、ときどき衣ずれ（−46 dBFS）</summary>
    private static SignalGen RoomToneAfterSilence()
    {
        var g = new SignalGen(48000, 30, seed: 12);
        g.Pink(-62, 0, 4);
        g.Pink(-55, 12, 30);
        foreach (double t in new[] { 16.0, 21.5, 26.0 }) g.White(-46, t, t + 0.35);
        return g;
    }

    private static readonly string[] Fast = { "strip24", "zone4", "led1" };

    private static IEnumerable<(string, string, bool)> All(string[] devs, string[]? styles = null, bool rfOff = true)
    {
        foreach (var d in devs)
            foreach (var s in styles ?? MusicStyles.All)
            {
                yield return (s, d, true);
                if (rfOff) yield return (s, d, false);
            }
    }

    // ===== テスト

    /// <summary>
    /// 撃ち合いのあとの夜の環境音（−48 dBFS。自動の音量調整が数秒で「最大」に合わせてしまう音）で、どの光り方も落ち着く
    /// （速いデバイス: 明るさの上位 5% が 0.3 以下・光り始めが 1 秒に 0.3 回以下。メモリ: 光り始めが 1 秒に 0.3 回以下）。
    /// 小さなせりふが始まったら、すぐまた音に合わせて光る（せりふの間の平均が、静かな場面より明るい）。
    /// 直す前（6.15.10 のあと）は、静かな場面のほうが明るく、ビートで色替えで 1 秒に 2 回、小さいゾーンの波紋で 4 回光っていた
    /// </summary>
    public static void TestQuietSceneAfterActionIsCalm()
    {
        var g = QuietSceneAfterAction();
        var run = Play(g.Buf, g.Fs, All(Fast.Append("dram").ToArray()));
        foreach (var L in run.Lanes)
        {
            double p95 = P95(L, 16, 40), fl = Rate(Flashes(L), 16, 40);
            double quiet = Mean(L, 16, 40), talk = Mean(L, 40.2, 46);
            T.Info($"{L.Key,-24} quiet scene p95 {p95:F2} flashes {fl:F2}/s mean {quiet:F4}; dialogue mean {talk:F4}");
            T.Check(fl <= 0.3, $"{L.Key}: calm in a quiet film scene after action (flashes {fl:F2}/s)");
            if (L.Dev != "dram")
            {
                T.Check(p95 <= 0.3, $"{L.Key}: dim in a quiet film scene after action (p95 {p95:F2})");
                T.Check(talk > quiet, $"{L.Key}: reacts again when dialogue starts (mean {talk:F2} > {quiet:F2})");
            }
        }
    }

    /// <summary>
    /// デジタルの無音のあとの部屋の音（−55 dBFS と小さな衣ずれ）でも、どの光り方も落ち着く（明るさの上位 5% が 0.3 以下・光り始めが 1 秒に 0.3 回以下）。
    /// 前の音量と比べられないので、音量そのもの（−44 dB より小さい音で暗く）で見る
    /// </summary>
    public static void TestRoomToneAfterSilenceIsCalm()
    {
        var g = RoomToneAfterSilence();
        var run = Play(g.Buf, g.Fs, All(Fast));
        foreach (var L in run.Lanes)
        {
            // 衣ずれ（小さな音そのもの）への反応は数えない: 部屋の音だけのところ
            var spans = new[] { (14.0, 15.6), (17.0, 21.1), (22.5, 25.6), (27.0, 30.0) };
            double p95 = spans.Max(sp => P95(L, sp.Item1, sp.Item2)), fl = Flashes(L).Count(t => spans.Any(sp => t >= sp.Item1 && t < sp.Item2)) / spans.Sum(sp => sp.Item2 - sp.Item1);
            T.Info($"{L.Key,-24} room tone p95 {p95:F2} flashes {fl:F2}/s");
            T.Check(fl <= 0.3 && p95 <= 0.3, $"{L.Key}: calm in room tone after silence (p95 {p95:F2}, flashes {fl:F2}/s)");
        }
    }

    /// <summary>
    /// 曲（120 BPM）が 15 秒で止まり、間隔がばらばらの足音（0.6〜0.95 秒おき）だけが続く: テンポの追跡は 5 秒以内（入れたときは 4.1 秒）にロックを外し、
    /// そのあとはロックしない（直す前は、拍の位置を取り直すたびに確かさの低い拍の数が 0 に戻り、19 秒も前の曲の拍で光っていた）。
    /// 曲の間は今までどおりロックする
    /// </summary>
    public static void TestTempoLetsGoWhenMusicStops()
    {
        var g = new SignalGen(48000, 40, seed: 21);
        var r = new Random(21);
        g.DenseMix(120, 0, 15);
        g.Pink(-50, 15, 40);
        var steps = new List<double>();
        for (double t = 15.4; t < 39.5; t += 0.6 + 0.35 * r.NextDouble()) { g.Kick(t, 0.3, click: false); steps.Add(t); }
        var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.Ripple, "strip24", true), (MusicStyles.BeatHue, "strip24", true) });
        double Locked(double a, double b) => Enumerable.Range((int)(a * 60), (int)((b - a) * 60)).Count(i => i < run.Lock.Count && run.Lock[i] != BeatLock.Unlocked) / ((b - a) * 60);
        double music = Locked(8, 15), after = Locked(19, 40);
        int release = Enumerable.Range(15 * 60, run.Lock.Count - 15 * 60).FirstOrDefault(i => run.Lock[i] == BeatLock.Unlocked, run.Lock.Count);
        T.Info($"locked during music {music:P0}, released at {release / 60.0:F1} s, locked/coasting after 19 s {after:P0}");
        T.Check(music >= 0.9, $"the tempo locks during the music ({music:P0})");
        T.Check(release / 60.0 <= 20, $"the lock is released within 5 s after the music stops ({release / 60.0:F1} s)");
        T.Check(after <= 0.05, $"irregular footsteps do not keep or re-take the old tempo ({after:P0} locked after 19 s)");
        foreach (var L in run.Lanes)
        {
            // 足音から 80 ミリ秒以上離れた光（拍の予測だけの光）
            var stray = Flashes(L).Where(t => t >= 19 && !steps.Any(s => t >= s - 0.03 && t <= s + 0.08)).ToList();
            double rate = stray.Count / 21.0;
            T.Info($"{L.Key,-24} flashes with no footstep after 19 s: {rate:F2}/s");
            T.Check(rate <= 0.15, $"{L.Key}: no flashes on the old beat grid after the music stops ({rate:F2}/s)");
        }
    }

    /// <summary>
    /// 曲（124 BPM）が 20 秒で止まり、銃声だけが続く（1 発ずつと 8〜12 発の連射）: 24 秒より後は、連射の最初の 1 発でもはっきり光る
    /// （直す前は、止まった曲の拍のまま 13 秒ロックが残り、連射の最初の 1 発は光らなかった）
    /// </summary>
    public static void TestGunfireAfterMusicIsSeen()
    {
        var g = new SignalGen(48000, 44, seed: 22);
        var r = new Random(22);
        g.DenseMix(124, 0, 20);
        g.Pink(-48, 20, 44);
        var firsts = new List<double>();
        for (double t = 21; t < 42.5;)
        {
            int n = r.NextDouble() < 0.5 ? 1 : 8 + r.Next(5);
            if (t >= 24) firsts.Add(t);
            for (int k = 0; k < n; k++) { g.Snare(t + k * 0.1, 0.55); g.Kick(t + k * 0.1, 0.35, click: false); }
            t += n * 0.1 + 0.8 + 1.2 * r.NextDouble();
        }
        var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.BeatHue, "strip24", true), (MusicStyles.BeatHue, "strip24", false) });
        foreach (var L in run.Lanes)
        {
            int seen = 0;
            foreach (double t in firsts)
            {
                double before = Frames(L, t - 0.1, t).Select(i => (double)L.A[i][0]).DefaultIfEmpty(0).Min();
                double peak = Frames(L, t, t + 0.15).Select(i => (double)L.A[i][0]).DefaultIfEmpty(0).Max();
                if (peak - before >= 0.05) seen++;
            }
            double share = seen / (double)Math.Max(1, firsts.Count);
            T.Info($"{L.Key,-24} first shots seen {seen}/{firsts.Count}");
            T.Check(share >= 0.8, $"{L.Key}: the first shot of each burst lights up after the music stops ({seen}/{firsts.Count})");
        }
        int release = Enumerable.Range(20 * 60, run.Lock.Count - 20 * 60).FirstOrDefault(i => run.Lock[i] == BeatLock.Unlocked, run.Lock.Count);
        T.Check(release / 60.0 <= 25, $"the lock is released within 5 s after the music stops ({release / 60.0:F1} s)");
    }

    /// <summary>
    /// 拍の AI が、曲が止まったあとも窓に残った前の曲の拍（120 BPM）を返し続ける（本物の AI でもせりふに 10 秒ほど続いた）:
    /// 追跡がロックを外したあと、その古い拍で並びを押しつけない（直す前は、外した直後に AI に合わせて 13 秒ロックし直していた）。
    /// 曲の間は、AI があっても今までどおりロックする
    /// </summary>
    public static void TestAiDoesNotBringBackTheOldGrid()
    {
        var g = new SignalGen(48000, 45, seed: 23);
        var r = new Random(23);
        g.DenseMix(120, 0, 20);
        g.Pink(-52, 20, 45);
        Dialogue(g, r, 20.5, 45, 0.12);
        // 「AI」: 曲の拍の並びを、曲が止まって 10 秒まで返す
        var grid = g.KickTimes.Where(t => t < 20).Select(t => t + T0).ToList();
        double p = 0.5;
        for (double t = grid[^1] + p; t < T0 + 30; t += p) grid.Add(t);
        var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.BeatHue, "strip24", true) }, ai: (a, b) => grid.Where(t => t >= a && t <= b).ToList());
        double Locked(double a, double b) => Enumerable.Range((int)(a * 60), (int)((b - a) * 60)).Count(i => i < run.Lock.Count && run.Lock[i] != BeatLock.Unlocked) / ((b - a) * 60);
        int late = run.SteerT.Count(t => t >= 21);
        T.Info($"steers {string.Join(", ", run.SteerT.Select(t => t.ToString("F1")))}; locked 10-20 s {Locked(10, 20):P0}, 26-45 s {Locked(26, 45):P0}");
        T.Check(Locked(10, 20) >= 0.9, $"the tempo locks during the music with the beat AI ({Locked(10, 20):P0})");
        T.Check(late == 0, $"the beat AI does not steer to the old grid after the music stops ({late} steers)");
        T.Check(Locked(26, 45) <= 0.05, $"dialogue after the music is not lit on the old grid ({Locked(26, 45):P0} locked)");
    }

    /// <summary>
    /// 拍の AI の並びは、拍がほとんど抜けていないときだけ使う（話し声で、2 秒以上あいた拍を「抜けた拍」として並びにしていた。
    /// 18 拍の枠に 11 拍しかない並びは使わない。曲の拍はほとんど抜けない）
    /// </summary>
    public static void TestAiGridNeedsFewMissingBeats()
    {
        var dense = Enumerable.Range(0, 24).Select(k => 100 + 0.5 * k).ToList();
        T.Check(BeatAssist.EstimateGrid(dense, new List<double>(), 112.5 + BeatAssist.EdgeSec) is not null, "a regular beat list gives a grid");
        // 拍を 1 つだけ落としても使える
        var one = dense.Where((_, k) => k != 7).ToList();
        T.Check(BeatAssist.EstimateGrid(one, new List<double>(), 112.5 + BeatAssist.EdgeSec) is not null, "one missing beat still gives a grid");
        // 0.5 秒の枠のうち 11 拍だけ（2 秒・2.5 秒の穴）
        int[] keep = { 0, 1, 2, 3, 8, 9, 10, 15, 16, 17, 18 };
        var sparse = keep.Select(k => 100 + 0.5 * k).ToList();
        T.Check(BeatAssist.EstimateGrid(sparse, new List<double>(), 109 + BeatAssist.EdgeSec) is null, "a beat list with long gaps (11 of 19 slots) gives no grid");
    }

    /// <summary>
    /// 「激しい点滅を抑える」がオンなら、1 秒に 10 回しか送らない直接書き込むグラボ・1 秒に 8 回のメモリでも、
    /// LED に出ている値で数えて、強い点滅（0.1 秒以内に 0.2 以上）が、どの区域でもどの 1 秒の間でも 3 回まで
    /// （直す前は、描いたコマで数えていたので、送ったコマの間の 1 段の上がりを数えそこねて 4〜5 回になっていた）
    /// </summary>
    public static void TestFlashLimitHoldsOnSlowDevices()
    {
        var talk = new SignalGen(48000, 30, seed: 31);
        var r = new Random(31);
        talk.Pink(-55, 0, 30);
        Dialogue(talk, r, 1, 30, 0.15);
        var music = new SignalGen(48000, 30, seed: 32);
        music.DenseMix(128, 0, 30);
        string[] styles = { MusicStyles.Meter, MusicStyles.Ripple, MusicStyles.Role, MusicStyles.Spectrum, MusicStyles.BeatHue };
        foreach (var (name, g) in new[] { ("dialogue", talk), ("music 128", music) })
            foreach (var inten in new[] { Intensities.Moderate, Intensities.Intense })
            {
                var run = Play(g.Buf, g.Fs, All(new[] { "direct", "dram" }, styles, rfOff: false), inten);
                foreach (var L in run.Lanes)
                {
                    int m = StrongMax(L, 2, 30);
                    T.Check(m <= FlashGuard.MaxPerSecond, $"{name} {inten} {L.Key}: at most {FlashGuard.MaxPerSecond} strong flashes in any second as sent ({m})");
                }
            }
    }

    /// <summary>
    /// ずっと続く低い音（エンジンのうなり 42・64 Hz とゴーッという雑音・風）で、小さいゾーン（4 個・1 個）の波紋・スペクトラム・
    /// 音量メーター・ビートで色替えも、24 個のテープのビートで色替えも、ちらつかない（光り始めが 1 秒に 0.3 回以下・
    /// ちらつきが MusicFlickerTests の上限 1.0 未満）。直す前は、小さいゾーンの波紋が 1 秒に 4 回光り、ちらつきが 7〜10 あった
    /// </summary>
    public static void TestSteadySoundDoesNotFlicker()
    {
        var g = new SignalGen(48000, 34, seed: 41);
        var r = new Random(41);
        g.Pink(-60, 0, 34);
        // エンジン（2〜16 秒）: 42・64 Hz のうなりと倍音・230 Hz より下の雑音
        foreach (var (f, a) in new[] { (42.0, 0.10), (64.0, 0.07), (84.0, 0.04), (128.0, 0.02) }) g.Tone(f, a, 2, 16);
        Rumble(g, r, -24, 2, 16, 230);
        // 風（18〜34 秒）: 400 Hz より下の雑音が、ゆっくり強くなったり弱くなったり
        Rumble(g, r, -28, 18, 34, 400, gustHz: 0.15);
        string[] small = { MusicStyles.Ripple, MusicStyles.Spectrum, MusicStyles.Meter, MusicStyles.BeatHue };
        var lanes = All(new[] { "zone4", "led1" }, small).Concat(new[] { (MusicStyles.BeatHue, "strip24", true), (MusicStyles.BeatHue, "strip24", false) });
        var run = Play(g.Buf, g.Fs, lanes);
        foreach (var L in run.Lanes)
        {
            int leds = L.Dev == "strip24" ? 24 : L.Dev == "zone4" ? 4 : 1;
            foreach (var (label, a, b) in new[] { ("engine", 4.0, 16.0), ("wind", 20.0, 34.0) })
            {
                double fl = Rate(Flashes(L), a, b), jit = Jit(L, leds, a, b);
                T.Info($"{L.Key,-24} {label}: flashes {fl:F2}/s jit {jit:F2}");
                T.Check(fl <= 0.3 && jit < MusicFlickerTests.MaxJit, $"{L.Key}: steady {label} sound does not flicker (flashes {fl:F2}/s, jit {jit:F2})");
            }
        }
    }

    /// <summary>
    /// せりふだけ（話し声と「パ・タ」の破裂音）: テンポの追跡はロックしない（拍のない音で拍をでっち上げない）。
    /// パーツごとの役割・楽器で光る・ビートで色替えも、せりふを決まった速さの拍のように光らせない
    /// （見て分かる光り始めが 1 秒に 2.5 回以下で、間隔がそろっていない: 間隔のばらつき（変動係数）が 0.3 以上）
    /// </summary>
    public static void TestDialogueHasNoFakeTempo()
    {
        var g = new SignalGen(48000, 32, seed: 51);
        var r = new Random(51);
        g.Pink(-55, 0, 32);
        Dialogue(g, r, 1, 32, 0.15);
        string[] styles = { MusicStyles.Role, MusicStyles.Ensemble, MusicStyles.BeatHue };
        var run = Play(g.Buf, g.Fs, All(new[] { "strip24" }, styles));
        double locked = Enumerable.Range(4 * 60, 28 * 60).Count(i => i < run.Lock.Count && run.Lock[i] != BeatLock.Unlocked) / (28.0 * 60);
        T.Info($"dialogue: locked/coasting {locked:P0}");
        T.Check(locked <= 0.1, $"dialogue does not make the tempo lock ({locked:P0})");
        foreach (var L in run.Lanes)
        {
            var fl = Flashes(L).Where(t => t >= 4).ToList();
            double rate = fl.Count / 28.0;
            var gaps = fl.Zip(fl.Skip(1), (a, b) => b - a).ToList();
            double mean = gaps.Count == 0 ? 1 : gaps.Average();
            double cv = gaps.Count < 3 ? 1 : Math.Sqrt(gaps.Select(x => (x - mean) * (x - mean)).Average()) / mean;
            T.Info($"{L.Key,-24} dialogue: flashes {rate:F2}/s, gap cv {cv:F2}");
            T.Check(rate <= 2.5, $"{L.Key}: dialogue is not lit like a pumping beat ({rate:F2} flashes/s)");
            T.Check(gaps.Count < 3 || cv >= 0.3, $"{L.Key}: flashes on dialogue are not evenly spaced like an invented tempo (cv {cv:F2})");
        }
    }

    /// <summary>
    /// 曲（120 BPM）の上に、せりふ（10〜40 秒）と銃声・爆発（20〜30 秒、間隔はばらばら）が重なる: テンポの追跡はロックしたまま
    /// （8〜40 秒の 85% 以上）、ビートで色替えの光り始めの 70% 以上が曲のキックに乗る（効果音で拍がずれない）
    /// </summary>
    public static void TestMusicUnderDialogueAndEffectsKeepsTheBeat()
    {
        var g = new SignalGen(48000, 40, seed: 52);
        var r = new Random(52);
        g.DenseMix(120, 0, 40);
        var kicks = g.KickTimes.ToList();
        Dialogue(g, r, 10, 40, 0.15);
        for (double t = 20.3; t < 30;)
        {
            int n = r.NextDouble() < 0.5 ? 1 : 3 + r.Next(4);
            for (int k = 0; k < n; k++) g.Snare(t + k * 0.09, 0.5);
            if (r.NextDouble() < 0.3) { g.Kick(t + 0.03, 0.8); g.White(-20, t, t + 0.3); }
            t += n * 0.09 + 0.5 + 1.1 * r.NextDouble();
        }
        var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.BeatHue, "strip24", true), (MusicStyles.BeatHue, "strip24", false) });
        double locked = Enumerable.Range(8 * 60, 32 * 60).Count(i => i < run.Lock.Count && run.Lock[i] != BeatLock.Unlocked) / (32.0 * 60);
        T.Info($"music under dialogue and effects: locked/coasting {locked:P0}");
        T.Check(locked >= 0.85, $"the tempo stays locked on music under dialogue and effects ({locked:P0})");
        foreach (var L in run.Lanes)
        {
            var fl = Flashes(L).Where(t => t >= 10).ToList();
            double onKick = fl.Count(t => kicks.Any(k => t >= k - 0.04 && t <= k + 0.08)) / (double)Math.Max(1, fl.Count);
            T.Info($"{L.Key,-24} music under effects: {fl.Count} flashes, {onKick:P0} on the song's kicks");
            T.Check(fl.Count >= 30 && onKick >= 0.7, $"{L.Key}: follows the song's beat under dialogue and effects ({onKick:P0} of {fl.Count} on kicks)");
        }
    }

    /// <summary>
    /// 曲では、小さいゾーン（4 個）の波紋・スペクトラムも、今までどおりキックごとにはっきり光る
    /// （キックの 150 ミリ秒以内の上がりの平均が 0.3 以上。ずっと続く音をならす処理は、打つ音が続いている間は使わない）。
    /// </summary>
    public static void TestSmallZonesStillPulseOnKicks()
    {
        var g = new SignalGen(48000, 20, seed: 42);
        g.Loop(120, 0.5, 20, hats: true);
        var kicks = g.KickTimes.Where(t => t > 4).ToList();
        var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.Ripple, "zone4", true), (MusicStyles.Spectrum, "zone4", true), (MusicStyles.BeatHue, "zone4", true) });
        foreach (var L in run.Lanes)
        {
            double rise = kicks.Select(t =>
                Frames(L, t, t + 0.15).Select(i => (double)L.A[i][0]).DefaultIfEmpty(0).Max() -
                Frames(L, t - 0.1, t).Select(i => (double)L.A[i][0]).DefaultIfEmpty(0).Min()).Average();
            double need = 0.3;
            T.Info($"{L.Key,-24} rise per kick {rise:F2}");
            T.Check(rise >= need, $"{L.Key}: small zones still light on every kick ({rise:F2}, need {need:F1})");
        }
    }

    /// <summary>
    /// せりふだけ（拍のない話し声）: 小さいゾーン（4 個・1 個）の波紋は、強い光り始めが 1 秒に 1.1 回まで
    /// （以前の波紋は低音のパルスで、話し声の高さの音でも 1 秒に 1.6 回、映画のせりふでは点滅の制限いっぱいまで光っていた）。
    /// スペクトラムは 95% 点が 0.85 まで（以前は 0.95）。曲なら、同じ小さいゾーンは拍で光る（光り始めの 95% 以上がキックの 0.07 秒以内、キックの 90% 以上で光る）
    /// </summary>
    public static void TestSmallZoneDialogueIsCalm()
    {
        var g = new SignalGen(48000, 34, seed: 31);
        var r = new Random(31);
        g.Pink(-58, 0, 34);
        Dialogue(g, r, 2, 32, 0.15);
        var styles = new[] { MusicStyles.Ripple, MusicStyles.Spectrum };
        var run = Play(g.Buf, g.Fs, new[] { "zone4", "led1" }.SelectMany(d => styles.Select(s => (s, d, true))).ToList());
        foreach (var L in run.Lanes)
        {
            double strong = StrongStarts(L, 4, 32).Count / 28.0, p95 = P95(L, 4, 32);
            T.Info($"{L.Key,-24} dialogue: strong {strong:F2}/s p95 {p95:F2} visible {Rate(Flashes(L), 4, 32):F2}/s");
            if (L.Style == MusicStyles.Spectrum) T.Check(p95 <= 0.85, $"{L.Key}: speech does not keep the zone bright (p95 {p95:F2})");
            else T.Check(strong <= 1.1, $"{L.Key}: speech does not flash the small zone (strong {strong:F2}/s, p95 {p95:F2})");
        }

        var m = new SignalGen(48000, 20, seed: 43);
        m.Loop(128, 0.5, 20, hats: true);
        var kicks = m.KickTimes.Where(t => t > 4 && t < 19.5).ToList();
        var music = Play(m.Buf, m.Fs, styles.Where(s => s != MusicStyles.Spectrum).Select(s => (s, "zone4", true)).ToList());
        foreach (var L in music.Lanes)
        {
            var fl = Flashes(L).Where(t => t > 4 && t < 19.5).ToList();
            double onBeat = fl.Count == 0 ? 0 : fl.Count(t => kicks.Any(k => Math.Abs(k - t) <= 0.07)) / (double)fl.Count;
            double cover = kicks.Count(k => fl.Any(t => Math.Abs(k - t) <= 0.07)) / (double)kicks.Count;
            T.Info($"{L.Key,-24} music: on beat {onBeat:P0} of {fl.Count}, kicks lit {cover:P0}");
            T.Check(onBeat >= 0.95 && cover >= 0.9, $"{L.Key}: music still lights the small zone on the beat ({onBeat:P0} on beat, {cover:P0} of kicks)");
        }
    }

    /// <summary>
    /// 曲が急に止まって無音になった（アプリの切り替えなど）: 波紋は、テンポをつかんだままでも、予測したビートの輪を無音の中で出さない
    /// （以前は 1.5 秒ほど輪を出し続けた）。曲が戻ったら、最初のキックで 0.1 秒以内に輪を出す。「激しい点滅を抑える」のオン・オフとも
    /// </summary>
    public static void TestRippleStopsInSilence()
    {
        const double cut = 16, back = 24;
        var g = new SignalGen(48000, 32, seed: 44);
        g.Loop(128, 0.5, 32, hats: true);
        for (int i = (int)(cut * g.Fs); i < (int)(back * g.Fs); i++) g.Buf[i] = 0;
        var run = Play(g.Buf, g.Fs, new[] { "strip24", "zone4" }.SelectMany(d => new[] { (MusicStyles.Ripple, d, true), (MusicStyles.Ripple, d, false) }).ToList());
        T.Check(run.Lock[(int)((cut - 0.1) * 60)] == BeatLock.Locked, "the tempo is locked when the music stops");
        double tk = g.KickTimes.First(t => t >= back);
        foreach (var L in run.Lanes)
        {
            int atGate = L.Rings[(int)((cut + 0.3) * 60)], atEnd = L.Rings[(int)(back * 60) - 1], after = L.Rings[(int)((tk + 0.1) * 60)];
            T.Info($"{L.Key,-24} rings: {L.Rings[(int)(cut * 60)]} at the cut, {atGate} 0.3 s later, {atEnd} at the end of the silence, {after} 0.1 s after the first kick back");
            T.Check(atEnd == atGate, $"{L.Key}: no predicted rings in digital silence ({atEnd - atGate} rings)");
            T.Check(after > atEnd, $"{L.Key}: the first kick after the music comes back spawns a ring within 0.1 s");
        }
    }

    /// <summary>
    /// 5 秒の無音のあと曲が戻った: 楽器で光る（Ensemble）も、戻って最初のキックで 0.1 秒以内に 0.08 以上明るくなる
    /// （以前は「激しい点滅を抑える」がオンだと、戻った瞬間の見えない光で点滅の枠を使い、最初の拍が弱くなって 0.8 秒遅れた）。
    /// 強い光り始めは 1 秒に 3 回まで
    /// </summary>
    public static void TestEnsembleRecoversAfterSilence()
    {
        const double cut = 14, back = 19;
        var g = new SignalGen(48000, 26, seed: 45);
        g.Loop(128, 0.5, 26, hats: true, snare: true);
        g.BassLine(0.5, 26, 4.27, new[] { 55.0, 65.4, 73.4, 82.4 }, amp: 0.35);
        g.PadChord(0.5, 26, new[] { 220.0, 277.2, 329.6 }, amp: 0.06);
        for (int i = (int)(cut * g.Fs); i < (int)(back * g.Fs); i++) g.Buf[i] = 0;
        double tk = g.KickTimes.First(t => t >= back);
        var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.Ensemble, "strip24", true), (MusicStyles.Ensemble, "zone4", true), (MusicStyles.Ensemble, "led1", true) });
        foreach (var L in run.Lanes)
        {
            double first = Flashes(L, 0.08).Where(t => t >= back - 0.02).DefaultIfEmpty(double.NaN).First();
            int strong = StrongMax(L, 0, 26);
            T.Info($"{L.Key,-24} first flash {first - back:F2} s after the music comes back (first kick at {tk - back:F2} s), strong max {strong}/s");
            T.Check(first - tk <= 0.1, $"{L.Key}: the first kick after the silence is seen ({first - back:F2} s after the return)");
            T.Check(strong <= 3, $"{L.Key}: at most 3 strong flashes per second ({strong})");
        }
    }

    /// <summary>
    /// 曲が拍の途中で急に止まった（次の予測の拍は 0.19 秒後。音の入口はまだ開いている）: 波紋は無音の中で輪を出さない
    /// （直す前は 1 つ出していた。小さいゾーンで 0.8 くらいまで明るくなった）。
    /// 曲の中の 1 拍分の休み（休みの中の拍は無音、次の拍でキック）では、休みの中の拍で輪を出さず、戻ったキックの拍では、その拍の時刻の輪を出す
    /// （0.1 秒以内に、拍の時刻から 0.02 秒以内の輪）。
    /// とても小さな音の曲（キックの間はほとんど無音）でも、波紋は今までどおり拍ごとに輪を出す
    /// </summary>
    public static void TestNoBeatLightRightAfterAStop()
    {
        double p = 60 / 128.0;
        double Beat(int k) => 0.5 + k * p;
        double cut = Beat(33) + 0.28, back = 24;
        var g = new SignalGen(48000, 30, seed: 49);
        g.Loop(128, 0.5, 30, hats: true);
        for (int i = (int)(cut * g.Fs); i < (int)(back * g.Fs); i++) g.Buf[i] = 0;
        var lanes = new[] { "strip24", "zone4", "led1" }.SelectMany(d => new[] { (MusicStyles.Ripple, d, true), (MusicStyles.Ripple, d, false) }).ToList();
        var run = Play(g.Buf, g.Fs, lanes);
        T.Check(run.Lock[(int)((cut - 0.1) * 60)] == BeatLock.Locked, "the tempo is locked when the music stops");
        foreach (var L in run.Lanes)
        {
            double peak = Frames(L, cut + 0.15, back - 0.5).Select(i => (double)L.A[i][0]).DefaultIfEmpty(0).Max();
            if (L.Style == MusicStyles.Ripple)
            {
                int rings = L.Rings[(int)(back * 60) - 1] - L.Rings[(int)(cut * 60)];
                T.Info($"{L.Key,-24} after the stop: {rings} rings, brightest {peak:F2}");
                T.Check(rings == 0, $"{L.Key}: no predicted ring into the silence ({rings} rings)");
            }
            else
            {
                T.Info($"{L.Key,-24} after the stop: brightest {peak:F2}");
                T.Check(peak <= 0.35, $"{L.Key}: predicted beats in the silence stay dim ({peak:F2})");
            }
        }

        // 曲の中の 1 拍分の休み: 20 拍目のキックのあと 0.25 秒から、22 拍目のキックの直前まで無音
        var s = new SignalGen(48000, 16, seed: 50);
        s.Loop(128, 0.5, 16, hats: true);
        double b20 = Beat(20), b22 = Beat(22);
        for (int i = (int)((b20 + 0.25) * s.Fs); i < (int)Math.Round(b22 * s.Fs); i++) s.Buf[i] = 0;
        var gap = Play(s.Buf, s.Fs, new[] { (MusicStyles.Ripple, "strip24", true), (MusicStyles.Ripple, "zone4", true) });
        foreach (var L in gap.Lanes)
        {
            if (L.Style == MusicStyles.Ripple)
            {
                int inGap = L.Rings[(int)((b22 - 0.02) * 60)] - L.Rings[(int)((b20 + 0.1) * 60)];
                int back1 = L.Rings[(int)((b22 + 0.1) * 60)] - L.Rings[(int)((b22 - 0.02) * 60)];
                double t0 = L.RingT0[(int)((b22 + 0.1) * 60)];
                T.Info($"{L.Key,-24} one-beat break: {inGap} rings in the break, {back1} on the kick back (ring time {t0 - b22:+0.000;-0.000} s from the beat)");
                T.Check(inGap == 0, $"{L.Key}: no ring on the silent beat in the break ({inGap})");
                T.Check(back1 >= 1 && Math.Abs(t0 - b22) <= 0.02, $"{L.Key}: the kick after the break spawns a ring on the beat ({back1} rings, {t0 - b22:+0.000;-0.000} s)");
            }
            else
            {
                double rise = Frames(L, b22, b22 + 0.15).Select(i => (double)L.A[i][0]).DefaultIfEmpty(0).Max() -
                    Frames(L, b22 - 0.1, b22).Select(i => (double)L.A[i][0]).DefaultIfEmpty(0).Min();
                T.Info($"{L.Key,-24} one-beat break: rise on the kick back {rise:F2}");
                T.Check(rise >= 0.4, $"{L.Key}: the kick after the break lights the small zone ({rise:F2})");
            }
        }

        // とても小さな音の曲（キックの間はほとんど無音）でも、波紋は拍ごとに輪を出す（拍の頭が無音でも、すぐ来るキックを待つ）
        var q = new SignalGen(48000, 16, seed: 52);
        q.Loop(128, 0.5, 16, hats: true, kickAmp: 0.03);
        var quiet = Play(q.Buf, q.Fs, new[] { (MusicStyles.Ripple, "strip24", true), (MusicStyles.Ripple, "zone4", true) });
        foreach (var L in quiet.Lanes)
        {
            var locked = Enumerable.Range(0, 200).Select(k => Beat(k)).Where(t => t > 6 && t < 15 && quiet.Lock[(int)(t * 60)] == BeatLock.Locked).ToList();
            int rings = L.Rings[(int)(15 * 60)] - L.Rings[(int)(6 * 60)];
            T.Info($"{L.Key,-24} quiet song: {rings} rings for {locked.Count} locked beats");
            T.Check(locked.Count >= 10 && rings >= locked.Count - 1, $"{L.Key}: a very quiet song still gets a ring on every beat ({rings} rings, {locked.Count} beats)");
        }
    }

    /// <summary>
    /// レースゲームのブレーキ: エンジンの音の上に 2 秒のタイヤのきしみ（1.5〜2.5 kHz をさまよう細い音。80 ミリ秒で立ち上がる。テンポもキックもない）。
    /// ビートで色替えは、きしみで明るく光りっぱなしにならない（強さ Moderate・Intense、速いデバイスで、きしみの間の 95% 点が 0.6 まで・平均 0.25 まで・
    /// 0.8 秒後からの最大が 0.5 まで。直す前は 95% 点 1.0・平均 0.6〜0.7）。
    /// メモリ（遅いデバイス）も 95% 点 0.8 まで（直す前は 1.0）。
    /// ドラムのない高音のベルの曲では、今までどおり 1 音ごとに明るくなる（音のすぐあとが、すぐ前より 0.05 以上明るい）
    /// </summary>
    public static void TestBeatHueTyreSquealIsNotAFlash()
    {
        var g = new SignalGen(48000, 20, seed: 51);
        var r = new Random(51);
        EngineHum(g, r, 1, 20, 0.12);
        double[] squeals = { 6.0, 12.5 };
        foreach (double ts in squeals) Squeal(g, r, ts, 2.0, 0.10);
        var lanes = new[] { "strip24", "zone4", "led1", "direct", "dram" }.Select(d => (MusicStyles.BeatHue, d, true))
            .Concat(new[] { (MusicStyles.BeatHue, "strip24", false) }).ToList();
        foreach (var inten in new[] { Intensities.Moderate, Intensities.Intense })
        {
            var run = Play(g.Buf, g.Fs, lanes, inten);
            foreach (var L in run.Lanes)
            {
                foreach (double ts in squeals)
                {
                    double p95 = P95(L, ts, ts + 2.3), mean = Mean(L, ts, ts + 2.3);
                    double late = Frames(L, ts + 0.8, ts + 2.3).Select(i => (double)L.A[i][0]).DefaultIfEmpty(0).Max();
                    T.Info($"{L.Key,-24} {inten,-8} squeal at {ts:F1} s: mean {mean:F2} p95 {p95:F2} late max {late:F2}");
                    if (L.Dev == "dram") T.Check(p95 <= 0.8, $"{L.Key} {inten}: a tyre squeal does not light the memory up ({p95:F2})");
                    else T.Check(p95 <= 0.6 && mean <= 0.25 && late <= 0.5, $"{L.Key} {inten}: a tyre squeal is not a flash (mean {mean:F2}, p95 {p95:F2}, late max {late:F2})");
                }
            }
        }
        var (mel, notes) = MusicLight.TrebleMelody();
        var lm = MusicLight.Run(mel.Buf, mel.Fs, MusicLight.Default());
        var later = notes.Where(t => t > 4).ToList();
        double after = lm.MeanAfter(later, 0.03, 0.15), before = lm.MeanAfter(later, -0.12, -0.02);
        T.Info($"treble bells: {after:F3} right after each note, {before:F3} just before");
        T.Check(after - before >= 0.05, $"treble bells still light on each note ({after:F2} vs {before:F2})");
    }

    /// <summary>車のエンジンに似た音: 低い音（90〜130 Hz をゆっくり上下）の倍音 10 本と、低い雑音</summary>
    private static void EngineHum(SignalGen g, Random r, double from, double to, double amp)
    {
        int a = (int)(from * g.Fs), b = Math.Min(g.Buf.Length, (int)(to * g.Fs));
        double ph = 0, lp = 0;
        for (int i = a; i < b; i++)
        {
            double t = (i - a) / (double)g.Fs;
            ph += (110 + 20 * Math.Sin(2 * Math.PI * 0.15 * t)) / g.Fs;
            lp += 0.03 * (r.NextDouble() * 2 - 1 - lp);
            double v = 0;
            for (int h = 1; h <= 10; h++) v += Math.Sin(2 * Math.PI * h * ph + 0.7 * h) / h;
            double fade = Math.Min(1, Math.Min(t / 0.5, (b - i) / (0.5 * g.Fs)));
            g.Buf[i] = (float)Math.Clamp(g.Buf[i] + amp * fade * (0.5 * v + 2 * lp), -1, 1);
        }
    }

    /// <summary>タイヤのきしみ: 1.5〜2.5 kHz をさまよう細い音（倍音 1 本）。大きさがゆらぐ。立ち上がり 80 ミリ秒・終わり 0.25 秒</summary>
    private static void Squeal(SignalGen g, Random r, double t0, double dur, double amp)
    {
        int s0 = (int)(t0 * g.Fs), n = (int)(dur * g.Fs);
        double ph = 0, am = 0.5, fw = 0;
        for (int i = 0; i < n && s0 + i < g.Buf.Length; i++)
        {
            double t = i / (double)g.Fs;
            fw += 0.0005 * (r.NextDouble() * 2 - 1 - fw);
            am += 0.002 * (r.NextDouble() - am);
            ph += 2 * Math.PI * (2000 + 500 * Math.Sin(2 * Math.PI * 0.7 * t) + 3000 * fw) / g.Fs;
            double env = Math.Min(1, t / 0.08) * Math.Min(1, (dur - t) / 0.25);
            g.Buf[s0 + i] = (float)Math.Clamp(g.Buf[s0 + i] + amp * env * (0.5 + am) * (Math.Sin(ph) + 0.25 * Math.Sin(2 * ph)), -1, 1);
        }
    }

    /// <summary>区域 0（デバイス全体）の強い光り始め（0.1 秒以内に 0.2 以上明るくなった始まり）の時刻</summary>
    private static List<double> StrongStarts(Lane l, double a, double b)
    {
        var res = new List<double>();
        bool run = false;
        for (int i = 1; i < l.A.Count; i++)
        {
            float lo = l.A[i][0];
            for (int j = i - 1; j >= 0 && (j == i - 1 || (i - j) / 60.0 <= 0.1 + 0.004); j--) lo = Math.Min(lo, l.A[j][0]);
            bool up = l.A[i][0] - lo >= FlashGuard.StrongRise - 1e-4f;
            if (up && !run && i / 60.0 >= a && i / 60.0 < b) res.Add(i / 60.0);
            run = up;
        }
        return res;
    }

    /// <summary>明るさの、ずらした時間 lagA〜lagB 秒での自己相関のいちばん大きい値（決まった間隔で明るくなっているか。変化がなければ 0）</summary>
    private static double AutoCorrPeak(Lane l, double a, double b, double lagA, double lagB)
    {
        var x = Frames(l, a, b).Select(i => (double)l.A[i][0]).ToArray();
        double m = x.DefaultIfEmpty(0).Average(), v = x.Sum(y => (y - m) * (y - m));
        if (v < 1e-6) return 0;
        double best = 0;
        for (int k = (int)(lagA * 60); k <= (int)(lagB * 60) && k < x.Length; k++)
        {
            double s = 0;
            for (int i = 0; i + k < x.Length; i++) s += (x[i] - m) * (x[i + k] - m);
            best = Math.Max(best, s / v);
        }
        return best;
    }

    private static double Sd(Lane l, double a, double b)
    {
        var x = Frames(l, a, b).Select(i => (double)l.A[i][0]).ToArray();
        double m = x.DefaultIfEmpty(0).Average();
        return x.Length == 0 ? 0 : Math.Sqrt(x.Sum(y => (y - m) * (y - m)) / x.Length);
    }

    /// <summary>銃声: 広い帯域の「パン」（12 ミリ秒）＋低い「ドン」（90→45 Hz、60 ミリ秒）＋こもった残り（180 ミリ秒）</summary>
    private static void Gunshot(SignalGen g, Random r, double t0, double amp)
    {
        int s0 = (int)(t0 * g.Fs), n = (int)(0.6 * g.Fs);
        double lp = 0, a = 1 - Math.Exp(-2 * Math.PI * 900.0 / g.Fs);
        for (int i = 0; i < n && s0 + i < g.Buf.Length; i++)
        {
            double t = i / (double)g.Fs, w = r.NextDouble() * 2 - 1;
            lp += a * (w - lp);
            double crack = w * Math.Exp(-t / 0.012);
            double thump = Math.Sin(2 * Math.PI * (45 * t + 45 * 0.03 * (1 - Math.Exp(-t / 0.03)))) * Math.Exp(-t / 0.06);
            double tail = lp * 2.2 * Math.Exp(-t / 0.18) * Math.Min(1, t / 0.004);
            g.Buf[s0 + i] = (float)Math.Clamp(g.Buf[s0 + i] + amp * (0.75 * crack + 0.6 * thump + 0.35 * tail), -1, 1);
        }
    }

    // ===== テンポの追跡と拍の AI の受け渡し

    /// <summary>
    /// 曲の間に無音のないまま、128 BPM の曲から 96 BPM・85 BPM の曲に変わる（曲名の知らせはない）: 拍が合わなくなってロックを外したあと、
    /// 覚えた前の曲のテンポを忘れて、新しいテンポで早く取り直す（直す前は、前の曲のテンポに引っぱられ、変わってから 12〜15 秒かかった）
    /// </summary>
    public static void TestGaplessTempoChangeRelocksSoon()
    {
        foreach (double bpm in new[] { 96.0, 85.0 })
        {
            var g = new SignalGen(48000, 45, seed: 31);
            g.DenseMix(128, 0, 25);
            g.DenseMix(bpm, 25, 45);
            var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.BeatHue, "strip24", true) });
            int n = run.Lock.Count;
            int release = Enumerable.Range(25 * 60, n - 25 * 60).FirstOrDefault(i => run.Lock[i] == BeatLock.Unlocked, n);
            int relock = Enumerable.Range(release, n - release).FirstOrDefault(i => run.Lock[i] != BeatLock.Unlocked && Math.Abs(run.Bpm[i] / bpm - 1) < 0.03, n);
            T.Info($"128 -> {bpm:F0} BPM: released at {release / 60.0:F1} s, locked on {bpm:F0} BPM at {relock / 60.0:F1} s");
            T.Check(release / 60.0 <= 30, $"{bpm:F0} BPM: the old grid is let go within 5 s after the change ({release / 60.0:F1} s)");
            T.Check(relock / 60.0 <= 35, $"{bpm:F0} BPM: locks on the new tempo within 10 s after the change ({relock / 60.0:F1} s)");
        }
    }

    /// <summary>
    /// 拍の AI が半分のテンポ（84 BPM の曲で 42 BPM。追跡の範囲より遅い）を言う: 倍の速さに直して比べ、追跡の 84 BPM のまま。
    /// 直す前は、合わないとして合わせ直し、範囲の端で切られて、どのテンポとも合わない 63 BPM で 3 秒ほど光っていた
    /// </summary>
    public static void TestAiHalfTempoIsFoldedIntoRange()
    {
        // AI の拍の並びだけを見る: 42 BPM の拍は 84 BPM の並びに直る
        var slow = Enumerable.Range(0, 12).Select(k => 100 + k * 60 / 42.0).ToList();
        var grid = BeatAssist.EstimateGrid(slow, new List<double>(), slow[^1] + BeatAssist.EdgeSec + 0.1);
        double gridBpm = grid is { } gg ? 60 / gg.Period : 0;
        T.Check(Math.Abs(gridBpm / 84 - 1) < 0.01, $"a 42 BPM beat list gives an 84 BPM grid ({gridBpm:F1} BPM)");
        // 範囲の外の周期を外から入れても、倍・半分にして範囲に入れる
        var tr = new BeatTracker(100);
        for (int i = 0; i < 300; i++) tr.Process(0, 0, 0, false, true, i / 100.0, 1);
        tr.Steer(60 / 42.0, 2.5);
        T.Check(Math.Abs(tr.PeriodSec / (60 / 84.0) - 1) < 0.01, $"steering to 42 BPM sets 84 BPM ({60 / tr.PeriodSec:F1} BPM)");
        tr.Steer(60 / 300.0, 2.5);
        T.Check(Math.Abs(tr.PeriodSec / (60 / 150.0) - 1) < 0.01, $"steering to 300 BPM sets 150 BPM ({60 / tr.PeriodSec:F1} BPM)");

        // 84 BPM の曲と、1 拍おきの拍だけを返す AI
        var g = new SignalGen(48000, 40, seed: 32);
        g.DenseMix(84, 0, 40);
        var half = g.KickTimes.Where((_, k) => k % 2 == 0).Select(t => t + T0).ToList();
        var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.BeatHue, "strip24", true) }, ai: (a, b) => half.Where(t => t >= a && t <= b).ToList());
        int from = 10 * 60, n = run.Lock.Count - from;
        var off = Enumerable.Range(from, n).Where(i => run.Lock[i] != BeatLock.Unlocked && Math.Abs(run.Bpm[i] / 84 - 1) >= 0.03).ToList();
        double locked = Enumerable.Range(from, n).Count(i => run.Lock[i] != BeatLock.Unlocked) / (double)n;
        T.Info($"steers {string.Join(", ", run.SteerT.Select(t => t.ToString("F1")))}; locked after 10 s {locked:P0}, frames on a wrong tempo {off.Count}"
            + (off.Count > 0 ? $" (first {off[0] / 60.0:F1} s, {run.Bpm[off[0]]:F1} BPM)" : ""));
        T.Check(locked >= 0.9, $"the 84 BPM song stays locked ({locked:P0})");
        T.Check(off.Count == 0, $"the half-tempo AI does not move the tempo away from 84 BPM ({off.Count} frames)");
        T.Check(run.Steered == 0, $"the AI agrees with the tracker, no steer ({run.Steered})");
    }

    /// <summary>
    /// 少しばらつく足音（2 Hz）が止まり、環境音だけになる。拍の AI は、窓に残った足音の拍の並びを返す:
    /// ロックしていないときは、その並びに今も音があるときだけ合わせる（直す前は、足音が止まったあとに足音の並びでロックし、音のない間に光っていた）。
    /// 足音がまた続けば、今までどおり拍をつかむ
    /// </summary>
    public static void TestAiGridNeedsSoundNow()
    {
        var g = new SignalGen(48000, 46, seed: 33);
        var r = new Random(33);
        g.Pink(-50, 0, 46);
        var steps = new List<double>();
        for (double t = 6; t < 14.4; t += 0.5 + (r.NextDouble() - 0.5) * 0.08) { g.Kick(t, 0.3, click: false); steps.Add(t); }
        for (int k = 0; k < 47; k++) { double t = 22 + 0.5 * k + (r.NextDouble() - 0.5) * 0.06; g.Kick(t, 0.3, click: false); steps.Add(t); }
        var times = steps.Select(t => t + T0).ToList();
        var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.Ripple, "strip24", true) }, ai: (a, b) => times.Where(t => t >= a && t <= b).ToList());
        double Locked(double a, double b) => Enumerable.Range((int)(a * 60), (int)((b - a) * 60)).Count(i => i < run.Lock.Count && run.Lock[i] != BeatLock.Unlocked) / ((b - a) * 60);
        T.Info($"steers {string.Join(", ", run.SteerT.Select(t => t.ToString("F1")))}; locked 15.5-22 s {Locked(15.5, 22):P0}, 34-45 s {Locked(34, 45):P0}");
        T.Check(!run.SteerT.Any(t => t >= 14.4 && t < 22), "no steer to the footstep grid after the footsteps stop");
        T.Check(Locked(15.5, 22) == 0, $"not locked while only the room tone is heard ({Locked(15.5, 22):P0})");
        T.Check(Locked(34, 45) >= 0.9, $"the footsteps that go on are followed again ({Locked(34, 45):P0} locked)");
    }

    /// <summary>時刻 te の出来事での明るさの上がり（te〜te + 0.15 秒のいちばん明るい値 − te の前 0.1 秒のいちばん暗い値）</summary>
    private static double Rise(Lane l, double te) =>
        Frames(l, te, te + 0.15).Select(i => (double)l.A[i][0]).DefaultIfEmpty(0).Max() -
        Frames(l, te - 0.1, te).Select(i => (double)l.A[i][0]).DefaultIfEmpty(0).Min();

    /// <summary>
    /// 6 秒ほとんど無音（−66 dBFS）のあとの、大きなドン（爆発）: 楽器で光る（Ensemble）も、0.15 秒の間にはっきり明るくなる
    /// （速いデバイス 0.2 以上・直接書き込むグラボ 0.1 以上。強い光り始めは 1 秒に 3 回まで）。
    /// 直す前は、キックの全体の光が止まっているときの光（ムードの灯り）と同じくらいで、上がりは 0.00〜0.02 だった（暗くなることもあった）
    /// </summary>
    public static void TestEnsembleFirstHitAfterSilenceIsSeen()
    {
        var g = new SignalGen(48000, 14, seed: 47);
        var r = new Random(47);
        g.Pink(-66, 0, 14);
        g.Loop(120, 0.5, 4, hats: true, snare: true);
        Explosion(g, r, 10, 0.9);
        var run = Play(g.Buf, g.Fs, All(new[] { "strip24", "zone4", "led1", "direct" }, new[] { MusicStyles.Ensemble }));
        foreach (var L in run.Lanes)
        {
            double rise = Rise(L, 10);
            int strong = StrongMax(L, 0, 14);
            T.Info($"{L.Key,-24} rise in 0.15 s {rise:F2} strong max {strong}/s");
            double need = L.Dev == "direct" ? 0.1 : 0.2;
            T.Check(rise >= need, $"{L.Key}: the first big hit after silence is seen (rise {rise:F2}, needs {need:F2})");
            if (L.Rf) T.Check(strong <= 3, $"{L.Key}: at most 3 strong flashes per second ({strong})");
        }
    }

    /// <summary>
    /// 消えていく音のあとに 5 秒の無音、そのあと大きなドン（爆発）: 盛り上がり（SectionMeter）は測り直して 1 から始まるので、
    /// きらめき・波紋も 0.15 秒の間にはっきり明るくなる（きらめき 0.3 以上・小さいゾーンの波紋 0.5 以上）。
    /// 直す前は、前の音の消えぎわの値（ほぼ 0）を持ち越して「静かな部分」として弱く始まり、上がりはきらめき 0.09・波紋 0.35 だった。
    /// 1.5 秒の無音なら、今までどおり止まった値のまま（曲の中の短い切れ目で変わらない）
    /// </summary>
    public static void TestSectionStartsFullAfterSilence()
    {
        static AudioFrame F(float db, bool open = true) => new() { LoudDb = db, GateOpen = open };
        float Run(SectionMeter m, float db, double sec, bool open = true)
        {
            float v = m.Value;
            for (; sec > 0; sec -= Tick) v = m.Update(F(db, open), Tick);
            return v;
        }
        var s = new SectionMeter();
        Run(s, -12, 10);
        float tail = Run(s, -30, 4);
        T.Check(tail < 0.1f, $"the fading tail is a quiet section ({tail:F2})");
        Run(s, -120, 1.5, open: false);
        float shortGap = s.Update(F(-12), Tick);
        T.Check(shortGap < 0.2f, $"after a 1.5 s gap the last value is kept ({shortGap:F2})");
        Run(s, -30, 4);
        Run(s, -120, 5, open: false);
        float back = s.Update(F(-12), Tick);
        T.Check(back == 1, $"after 5 s of silence the next sound starts full ({back:F2})");

        var g = new SignalGen(48000, 16, seed: 48);
        var r = new Random(48);
        g.Pink(-66, 0, 16);
        g.Loop(120, 0.5, 4, hats: true, snare: true);
        // 4 秒から 2 秒かけて消えていく低い音、そのあと 5 秒の無音
        int a = 4 * g.Fs, b = 6 * g.Fs;
        for (int i = a; i < b; i++)
        {
            double t = (i - a) / (double)g.Fs;
            g.Buf[i] += (float)(0.5 * Math.Exp(-t / 0.5) * Math.Sin(2 * Math.PI * 110 * t));
        }
        Explosion(g, r, 11, 0.9);
        var run = Play(g.Buf, g.Fs, All(new[] { "strip24", "zone4", "led1" }, new[] { MusicStyles.Sparkle })
            .Concat(All(new[] { "zone4", "led1" }, new[] { MusicStyles.Ripple })));
        foreach (var L in run.Lanes)
        {
            double rise = Rise(L, 11);
            T.Info($"{L.Key,-24} rise in 0.15 s {rise:F2}");
            double need = L.Style == MusicStyles.Ripple ? 0.5 : 0.3;
            T.Check(rise >= need, $"{L.Key}: a big hit after silence is seen fully (rise {rise:F2}, needs {need:F2})");
        }
    }

    /// <summary>
    /// 大きな音（−12 dB）を 8 秒描いたあと、12 秒は音楽連動で描かない（ほかのモードにした）。そのあと小さな音（−42 dB）で 7 秒描くと、
    /// 音の手ごたえは測り直して 1 から始まる（描かない間の時間は数えていないので、前の大きな音を覚えたままにしない）。
    /// ずっと描いていたデバイスは、今までどおり静かな場面として暗くなる
    /// </summary>
    public static void TestPresenceStartsFreshAfterOtherModes()
    {
        var ring = new FeatureRing();
        var events = new EventRing();
        RgbDeviceChannel Make()
        {
            var dev = DeviceOf("strip24");
            var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = MusicStyles.BeatHue, Brightness = 100 };
            var ch = new RgbDeviceChannel(dev, T0) { SharedColor = new SharedColor() };
            ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = dev.Name }, false);
            return ch;
        }
        var back = Make();
        var kept = Make();
        var f = new AudioFrame { GateOpen = true, Activity = 1 };
        double fed = T0;
        for (double sec = 0; sec < 27; sec += Tick)
        {
            double t = T0 + sec;
            for (; fed <= t; fed += 0.01)
            {
                f.T = fed;
                f.LoudDb = fed - T0 < 8 ? -12 : -42;
                ring.Write(f);
            }
            foreach (var ch in new[] { back, kept })
            {
                // back は 8〜20 秒の間、音楽連動で描かない
                if (ch == back && sec >= 8 && sec < 20) continue;
                ch.Prepare(t, Tick, ring, events, BeatState.None, null, false, false);
                ch.Render();
            }
        }
        float pb = back.Renderer.Context.Presence, pk = kept.Renderer.Context.Presence;
        T.Info($"presence after other modes {pb:F2}, kept drawing {pk:F2}");
        T.Check(pb >= 0.9f, $"after 12 s in other modes, the presence starts fresh ({pb:F2})");
        T.Check(pk <= 0.3f, $"a device that kept drawing still sees a quiet scene ({pk:F2})");
    }

    /// <summary>足音: こもった「トン」（250 Hz より下の雑音、25 ミリ秒で消える）＋小さな「コツ」（4 ミリ秒）。0.15 秒</summary>
    private static void Footstep(SignalGen g, Random r, double t0, double amp)
    {
        int s0 = (int)(t0 * g.Fs), n = (int)(0.15 * g.Fs);
        double lp = 0, a = 1 - Math.Exp(-2 * Math.PI * 250.0 / g.Fs), x1 = 0;
        for (int i = 0; i < n && s0 + i < g.Buf.Length; i++)
        {
            double t = i / (double)g.Fs, w = r.NextDouble() * 2 - 1;
            lp += a * (w - lp);
            double hp = w - x1; x1 = w;
            g.Buf[s0 + i] = (float)Math.Clamp(g.Buf[s0 + i] + amp * (3.0 * lp * Math.Exp(-t / 0.025) + 0.25 * hp * Math.Exp(-t / 0.004)), -1, 1);
        }
    }

    /// <summary>爆発: 70→28 Hz に下がる低いドン（0.5 秒で消える）＋こもった地鳴り（0.9 秒）＋はじめの「バン」。3 秒</summary>
    private static void Explosion(SignalGen g, Random r, double t0, double amp)
    {
        int s0 = (int)(t0 * g.Fs), n = (int)(3.0 * g.Fs);
        double lp = 0, lp2 = 0, a = 1 - Math.Exp(-2 * Math.PI * 350.0 / g.Fs);
        for (int i = 0; i < n && s0 + i < g.Buf.Length; i++)
        {
            double t = i / (double)g.Fs, w = r.NextDouble() * 2 - 1;
            lp += a * (w - lp);
            lp2 += a * (lp - lp2);
            double boom = Math.Sin(2 * Math.PI * (28 * t + 42 * 0.12 * (1 - Math.Exp(-t / 0.12)))) * Math.Exp(-t / 0.5);
            double rumble = lp2 * 4.0 * Math.Exp(-t / 0.9) * Math.Min(1, t / 0.01);
            double crack = w * Math.Exp(-t / 0.02) * 0.5;
            g.Buf[s0 + i] = (float)Math.Clamp(g.Buf[s0 + i] + amp * (0.8 * boom + 0.6 * rumble + crack), -1, 1);
        }
    }

    /// <summary>
    /// 点滅の制限（FlashLimiter）: 受け付けた光より 6 dB 以上大きい音は、間隔が短くても 1 回だけ弱めずに通す。
    /// そのとき間隔の起点は動かさない（次の光は、受け付けた光から 333 ミリ秒で受け付ける）。同じくらいの音は今までどおり 0.35 倍。
    /// 音量を渡さない呼び方は、今までとまったく同じ
    /// </summary>
    public static void TestFlashLimiterLetsALouderHitThrough()
    {
        var a = new FlashLimiter();
        T.Check(a.Decide(0, -30) == 1f, "the first flash is accepted");
        T.Check(a.Decide(0.2, -10) == 1f && a.Raised == 1, "a hit 20 dB louder 0.2 s later is not weakened");
        T.Check(a.Decide(0.34, -10) == 1f && a.Accepted == 2, "the next flash is accepted 0.34 s after the first one (the gap is still measured from 0 s)");

        var b = new FlashLimiter();
        b.Decide(0, -20);
        T.Check(b.Decide(0.3, -20) == FlashLimiter.RejectedGain, "a hit of the same loudness 0.3 s later is weakened as before");
        T.Check(b.Decide(0.31, -15) == FlashLimiter.RejectedGain, "a hit only 5 dB louder is weakened");

        var c = new FlashLimiter();
        c.Decide(0, -40);
        T.Check(c.Decide(0.1, -30) == 1f, "one louder hit gets through");
        T.Check(c.Decide(0.2, -10) == FlashLimiter.RejectedGain && c.Raised == 1, "only one louder hit gets through per accepted flash");

        var d = new FlashLimiter();
        float[] got = { d.Decide(0), d.Decide(0.03), d.Decide(0.2), d.Decide(0.34), d.Decide(0.5) };
        T.Check(got.SequenceEqual(new[] { 1f, 1f, FlashLimiter.RejectedGain, 1f, FlashLimiter.RejectedGain }) && d.Raised == 0,
            $"without a level it works as before ({string.Join(" ", got)})");

        var e = new FlashLimiter { Enabled = false };
        T.Check(e.Decide(0, -40) == 1f && e.Decide(0.1, -40) == 1f, "off: every flash keeps its strength");
    }

    /// <summary>
    /// 静かな環境音（−55 dBFS）の中の 1 発ずつの銃声（0・−12・−24 dB をくり返す。1.5 秒おき。最初は 5 秒の無音のあと）: 小さな銃声は小さく光る。
    /// −24 dB の銃声での明るさの上がりは、0 dB の銃声とくらべて、音量メーター 4 個のゾーン 0.7 倍以下・スペクトラム 4 個のゾーン 0.8 倍以下・
    /// ビートで色替え 24 個のテープ 0.7 倍以下・直接書き込むグラボのパーツごとの役割 0.8 倍以下。無音のあとの最初の銃声は、音量メーター・スペクトラムの 4 個のゾーンで 0.85 以上（控えめでは、あとの 0 dB の銃声の 0.85 倍以上）まで光る。
    /// 「激しい点滅を抑える」がオンなら、強い点滅は 1 秒に 3 回まで。強さの設定 4 つとも見る。
    /// 直す前は、自動の音量調整が 0.3 秒で大きな音に追いつくので、どの大きさの銃声もほぼ同じ明るさだった（音量メーター 0.94〜0.98）
    /// </summary>
    public static void TestQuieterShotsLookWeaker()
    {
        var g = new SignalGen(48000, 22, seed: 43);
        var r = new Random(43);
        g.Pink(-55, 5, 22);
        double[] pattern = { 0, -12, -24 };
        var shots = new List<(double T, double Db)>();
        for (int k = 0; k < 10; k++)
        {
            double t = 5 + 1.5 * k, db = pattern[k % 3];
            Gunshot(g, r, t, 0.9 * Math.Pow(10, db / 20));
            shots.Add((t, db));
        }
        var lanes = new[]
        {
            (MusicStyles.Meter, "zone4", true), (MusicStyles.Meter, "zone4", false),
            (MusicStyles.Spectrum, "zone4", true), (MusicStyles.Spectrum, "zone4", false),
            (MusicStyles.BeatHue, "strip24", true), (MusicStyles.BeatHue, "strip24", false),
            (MusicStyles.Role, "direct", true), (MusicStyles.Role, "direct", false),
        };
        foreach (var inten in Intensities.All)
        {
            var run = Play(g.Buf, g.Fs, lanes, inten);
            foreach (var L in run.Lanes)
            {
                double At(double db) => shots.Skip(1).Where(s => s.Db == db).Select(s => Rise(L, s.T)).Average();
                double loud = At(0), mid = At(-12), quiet = At(-24), first = Rise(L, shots[0].T);
                double limit = L.Style == MusicStyles.Meter || L.Style == MusicStyles.BeatHue ? 0.7 : 0.8;
                T.Info($"{inten,-8} {L.Key,-24} rise 0/-12/-24 dB {loud:F2}/{mid:F2}/{quiet:F2}, first shot {first:F2}");
                T.Check(quiet <= limit * loud, $"{inten} {L.Key}: a -24 dB shot looks weaker than a 0 dB shot ({quiet:F2} <= {limit} x {loud:F2})");
                if (L.Dev == "zone4")
                    T.Check(first >= 0.85 * loud && (inten == Intensities.Subtle || first >= 0.85), $"{inten} {L.Key}: the first shot after silence still lights up fully ({first:F2}, later 0 dB shots {loud:F2})");
                if (L.Rf)
                {
                    int m = StrongMax(L, 4, 21);
                    T.Check(m <= FlashGuard.MaxPerSecond, $"{inten} {L.Key}: at most {FlashGuard.MaxPerSecond} strong flashes in any second ({m})");
                }
            }
        }
    }

    /// <summary>
    /// 小さな銃声（−18 dB）の 0.24 秒・0.30 秒あとの大きな爆発（0 dB）が、「激しい点滅を抑える」で消されない
    /// （ビートで色替え 24 個のテープ: 上がり 0.18 以上で、オフとの差 0.03 以内。音を重ねる: オフの 0.8 倍以上）。
    /// 強い点滅は、オンなら 1 秒に 3 回まで。強さの設定 4 つとも見る。
    /// 直す前は、点滅の制限が先に来た光だけを通したので、爆発がほとんど光らなかった（ビートで色替え 0.00、音を重ねる 0.05）
    /// </summary>
    public static void TestBigHitAfterSmallShotIsNotSwallowed()
    {
        var g = new SignalGen(48000, 22, seed: 44);
        var r = new Random(44);
        g.Pink(-55, 0, 22);
        var booms = new List<double>();
        for (int k = 0; k < 4; k++)
        {
            double t = 3 + 4.4 * k, d = k % 2 == 0 ? 0.24 : 0.30;
            Gunshot(g, r, t, 0.9 * Math.Pow(10, -18 / 20.0));
            Explosion(g, r, t + d, 0.9);
            booms.Add(t + d);
        }
        string[] styles = { MusicStyles.BeatHue, MusicStyles.Ensemble };
        foreach (var inten in Intensities.All)
        {
            var run = Play(g.Buf, g.Fs, All(new[] { "strip24" }, styles), inten);
            foreach (var style in styles)
            {
                var on = run.Lanes.First(l => l.Style == style && l.Rf);
                var off = run.Lanes.First(l => l.Style == style && !l.Rf);
                double rOn = booms.Select(t => Rise(on, t)).Average(), rOff = booms.Select(t => Rise(off, t)).Average();
                int m = StrongMax(on, 2, 21);
                T.Info($"{inten,-8} {style,-9} explosion rise RF on {rOn:F2} / off {rOff:F2}, strong {m}/s");
                if (style == MusicStyles.BeatHue)
                    T.Check(rOn >= 0.18 && Math.Abs(rOn - rOff) <= 0.03, $"{inten} {style}: the explosion after a small shot lights up with RF on ({rOn:F2}, off {rOff:F2})");
                else
                    T.Check(rOn >= 0.8 * rOff, $"{inten} {style}: the explosion after a small shot is not weakened by RF ({rOn:F2} >= 0.8 x {rOff:F2})");
                T.Check(m <= FlashGuard.MaxPerSecond, $"{inten} {style} RF on: at most {FlashGuard.MaxPerSecond} strong flashes in any second ({m})");
            }
        }
    }

    // ===== 低いうなりをスネアにしない（エンジンの音・長く続く低いベースの音）

    /// <summary>
    /// のこぎり波のうなり（エンジンの音）。f0 Hz・倍音 harm 本・ゆっくりの揺れ drift（0.08 Hz）・細かい揺れ vib（7 Hz）・
    /// 低い雑音 noise・音の高さのばらつき jitter。0.5 秒で出入りする
    /// </summary>
    private static void Buzz(float[] b, int fs, double from, double to, double amp, double f0, double drift, double vib, int harm,
        double noise = 0, double jitter = 0, int seed = 1)
    {
        var r = new Random(seed);
        int a0 = (int)(from * fs), a1 = Math.Min(b.Length, (int)(to * fs));
        double ph = 0, jit = 0, lp = 0;
        for (int i = a0; i < a1; i++)
        {
            double t = (i - a0) / (double)fs;
            if (jitter > 0 && i % 480 == 0) jit = 0.9 * jit + jitter * (r.NextDouble() * 2 - 1);
            double f = f0 * (1 + drift * Math.Sin(2 * Math.PI * 0.08 * t)) * (1 + vib * Math.Sin(2 * Math.PI * 7 * t)) * (1 + jit);
            ph += 2 * Math.PI * f / fs;
            double v = 0;
            for (int h = 1; h <= harm; h++) v += Math.Sin(h * ph) / h;
            lp += 0.05 * (r.NextDouble() * 2 - 1 - lp);
            double fade = Math.Min(1, Math.Min(t / 0.5, (a1 - i) / (0.5 * fs)));
            b[i] = (float)Math.Clamp(b[i] + amp * fade * (v * 0.5 + noise * lp * 4), -1, 1);
        }
    }

    /// <summary>分析だけ流して、キック・スネアの時刻を集める（時刻は音の始まりから）</summary>
    private static List<(AudioEventKind Kind, double T)> EventsOf(float[] buf, int fs)
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        var engine = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(cfg) };
        var evs = new List<AudioEvent>();
        long cursor = 0;
        for (int pos = 0; pos < buf.Length; pos += fs / 100)
        {
            int n = Math.Min(fs / 100, buf.Length - pos);
            engine.Push(new ReadOnlySpan<float>(buf, pos, n), T0 + pos / (double)fs);
            engine.Events.Read(ref cursor, double.PositiveInfinity, evs);
        }
        return evs.Select(e => (e.Kind, e.T - T0)).ToList();
    }

    /// <summary>
    /// ずっと続く低いうなり（25〜48 Hz ののこぎり波。倍音 8・24 本、揺れあり・なし、雑音・ばらつきあり）は、スネアにならない
    /// （3〜13 秒で 1 秒に 0.2 回以下）。直す前は、FFT の窓の中で倍音どうしが揺れるのをスネアと取り違えて、10 秒で 22〜125 回出ていた
    /// （響き線の帯の上がりを見ていなかったため。直前のいちばん大きいところと比べると、うなりは 1 dB も上がらない）
    /// </summary>
    public static void TestSteadyBuzzIsNotASnare()
    {
        const int Fs = 48000;
        var cases = new List<(string Name, Action<float[]> Fill)>();
        foreach (double f0 in new[] { 25.0, 30, 36, 42, 48 })
        {
            cases.Add(($"{f0:F0} Hz steady", b => Buzz(b, Fs, 1, 13, 0.25, f0, 0, 0, 8)));
            cases.Add(($"{f0:F0} Hz 24 harmonics drifting", b => Buzz(b, Fs, 1, 13, 0.25, f0, 0.15, 0.004, 24)));
            cases.Add(($"{f0:F0} Hz noise + jitter", b => Buzz(b, Fs, 1, 13, 0.25, f0, 0, 0, 24, noise: 0.05, jitter: 0.01)));
        }
        foreach (var (name, fill) in cases)
        {
            var buf = new float[14 * Fs];
            fill(buf);
            var evs = EventsOf(buf, Fs);
            double rate = evs.Count(e => e.Kind == AudioEventKind.Snare && e.T >= 3 && e.T < 13) / 10.0;
            T.Check(rate <= 0.2, $"steady buzz {name}: no snare events ({rate:F1}/s)");
        }
    }

    /// <summary>
    /// エンジンの音（48 Hz のうなり。ゆっくり揺れる）で、音量メーターは強さの設定 4 つとも、きらめき・パーツごとの役割は
    /// 「控えめ」「ふつう」で光らない（24 個のテープ・4 個・1 個、「激しい点滅を抑える」オン・オフ。光り始めが 1 秒に 0.3 回以下）。
    /// 直す前は、うなりがスネアになって、音量メーターが 1 秒に 0.7〜1.3 回、「ふつう」のきらめきが 0.5〜0.7 回光っていた。
    /// 「強め」「とても強い」のきらめき・パーツごとの役割の 0.1〜1.4 回は、スネアではなく、立ち上がり（Onset）の出来事から（まだ直していない）
    /// </summary>
    public static void TestEngineHumDoesNotFlashTheDrums()
    {
        const int Fs = 48000;
        var buf = new float[14 * Fs];
        Buzz(buf, Fs, 1, 13, 0.25, 48, 0.15, 0.004, 8);
        string[] styles = { MusicStyles.Meter, MusicStyles.Sparkle, MusicStyles.Role };
        foreach (var inten in Intensities.All)
        {
            bool strong = inten is Intensities.High or Intensities.Intense;
            var run = Play(buf, Fs, All(Fast, strong ? new[] { MusicStyles.Meter } : styles), inten);
            foreach (var L in run.Lanes)
            {
                double fl = Rate(Flashes(L), 3, 13);
                T.Check(fl <= 0.3, $"{inten} {L.Key}: engine hum does not flash ({fl:F2}/s)");
            }
        }
    }

    /// <summary>
    /// 120 BPM のドラム（キック・スネア・ハイハット）に、長く続く低いのこぎり波のベース（36.7・41.2・43.65 Hz、倍音 24 本）を重ねても、
    /// 本物のスネアは見つかり（10 回のうち 5 回以上）、うそのスネアは 10 秒で 25 回まで。ベースのないドラムでは、10 回とも見つかり、うそは 0 回。
    /// 直す前は、ベースを重ねるとうそのスネアが 10 秒で 50〜102 回出ていた
    /// </summary>
    public static void TestSawBassUnderDrumsKeepsRealSnares()
    {
        const int Fs = 48000;
        foreach (double bass in new[] { 0.0, 36.7, 41.2, 43.65 })
            foreach (double amp in new[] { 0.12, 0.25 })
            {
                if (bass == 0 && amp > 0.12) continue;
                var g = new SignalGen(Fs, 14, 3);
                g.Loop(120, 1, 14, hats: true, snare: true);
                if (bass > 0) Buzz(g.Buf, Fs, 1, 14, amp, bass, 0, 0, 24);
                var sn = EventsOf(g.Buf, Fs).Where(e => e.Kind == AudioEventKind.Snare && e.T >= 3 && e.T < 13).Select(e => e.T).ToList();
                var truth = g.SnareTimes.Where(t => t >= 3 && t < 13).ToList();
                int found = truth.Count(t => sn.Any(s => Math.Abs(s - t) < 0.05));
                int fake = sn.Count(s => !truth.Any(t => Math.Abs(s - t) < 0.05));
                string name = bass == 0 ? "no bass" : $"bass {bass} Hz x{amp}";
                T.Info($"{name}: snares found {found}/{truth.Count}, fake {fake}");
                if (bass == 0)
                    T.Check(truth.Count >= 8 && found == truth.Count && fake == 0, $"drums without bass: every snare found, no fake ones ({found}/{truth.Count}, fake {fake})");
                else
                    T.Check(found * 2 >= truth.Count && fake <= 25, $"drums + low saw {name}: real snares found ({found}/{truth.Count}), few fake ones ({fake} in 10 s)");
            }
    }

    /// <summary>
    /// 響き線の帯の上がり（TransientPath.RiseOverMaxAt）: ずっと続くうなりは、直前のいちばん大きいところより 1 dB も上がらない。
    /// スネアの「シャッ」は 9 dB より大きく上がる
    /// </summary>
    public static void TestWireRiseSeparatesBuzzFromSnare()
    {
        const int Fs = 48000;
        var buf = new float[3 * Fs];
        Buzz(buf, Fs, 0, 3, 0.25, 42, 0, 0, 24);
        var g = new SignalGen(Fs, 3, 5);
        g.Snare(2.5, 0.4);
        var buzz = TransientPath.ForSnare(Fs);
        var mix = TransientPath.ForSnare(Fs);
        double maxBuzz = double.NegativeInfinity, snare = double.NaN;
        int at = (int)(2.53 * Fs);
        for (int i = 0; i < buf.Length; i++)
        {
            buzz.Sample(buf[i]);
            mix.Sample(buf[i] * 0.3 + g.Buf[i]);
            if (i % 512 == 511) { buzz.EndHop(); mix.EndHop(); }
            if (i >= Fs && i < 2 * Fs && i % 480 == 0) maxBuzz = Math.Max(maxBuzz, buzz.RiseOverMaxAt((int)(0.03 * Fs), 0.035, 0.045));
            if (i == at) snare = mix.RiseOverMaxAt((int)(0.045 * Fs), 0.035, 0.045);
        }
        T.Check(maxBuzz < 1.0, $"steady buzz: the wire band does not rise over its recent maximum ({maxBuzz:F2} dB)");
        T.Check(snare > 9.0, $"snare: the wire band rises clearly ({snare:F1} dB)");
    }

    /// <summary>
    /// 合成したドラムの曲 11 曲（MusicBench.Cases）で、本物のスネアは同じだけ見つかり（304 回のうち 218 回）、うそのスネアはほとんど出ない
    /// （全部で 6 回以下・1 曲で 1 回以下）。直す前は、うそのスネアが全部で 19 回（バラードで 4 回など）出ていた
    /// </summary>
    public static void TestDrumSongsKeepRealSnaresWithoutFakes()
    {
        int found = 0, all = 0, fakes = 0;
        foreach (var c in MusicBench.Cases())
        {
            var sn = EventsOf(c.Gen.Buf, c.Gen.Fs).Where(e => e.Kind == AudioEventKind.Snare).Select(e => e.T).ToList();
            var truth = c.Gen.SnareTimes;
            int f = truth.Count(t => sn.Any(s => Math.Abs(s - t) < 0.05)), k = sn.Count(s => !truth.Any(t => Math.Abs(s - t) < 0.05));
            T.Info($"{c.Name,-30} snares found {f}/{truth.Count}, fake {k}");
            T.Check(k <= 1, $"{c.Name}: at most one fake snare ({k})");
            found += f;
            all += truth.Count;
            fakes += k;
        }
        T.Check(found >= 216 && fakes <= 6, $"drum songs: real snares found ({found}/{all}), few fake ones ({fakes})");
    }

    // ===== スペクトラムの小さいゾーン（MixEffect）

    /// <summary>曲（128 BPM、キック・スネア・ハイハット・ベース）。12〜17 秒だけ 12 dB くらい小さくする</summary>
    private static SignalGen SongWithQuietPart(int seed)
    {
        var g = new SignalGen(48000, 24, seed: seed);
        double[] bass = { 55, 55, 65.4, 49 };
        g.Loop(128, 0.5, 12, hats: true, snare: true);
        g.BassLine(0.5, 12, 128 / 60.0, bass, 0.25);
        g.Loop(128, 12, 17, hats: true, snare: true, kickAmp: 0.2);
        g.BassLine(12, 17, 128 / 60.0, bass, 0.06);
        g.Loop(128, 17, 24, hats: true, snare: true);
        g.BassLine(17, 24, 128 / 60.0, bass, 0.25);
        return g;
    }

    /// <summary>コマ from〜to の、2 つの値の相関</summary>
    private static double Corr(double[] x, double[] y, int from, int to)
    {
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        int c = 0;
        for (int i = from; i < Math.Min(to, Math.Min(x.Length, y.Length)); i++)
        {
            sx += x[i]; sy += y[i]; sxx += x[i] * x[i]; syy += y[i] * y[i]; sxy += x[i] * y[i];
            c++;
        }
        if (c < 2) return 0;
        double vx = sxx - sx * sx / c, vy = syy - sy * sy / c;
        return vx <= 1e-12 || vy <= 1e-12 ? 0 : (sxy - sx * sy / c) / Math.Sqrt(vx * vy);
    }

    /// <summary>
    /// 曲では、スペクトラムの小さいゾーン（4 個・1 個）も 6.15.10 と同じくらい明るく光る: 明るさの 95% 点が 0.95 以上、平均が 0.38 以上、
    /// 音の大きさと明るさの相関が 0.3 以上（大きい音ほど明るい）。直す前は、明るさを 3 つの帯の平均で決めていたので、95% 点が 0.70 で止まり、
    /// 相関も 0.29 だった（6.15.10 の 95% 点は 0.99）
    /// </summary>
    public static void TestSmallZoneSpectrumKeepsMusicPeaks()
    {
        var g = SongWithQuietPart(51);
        var run = Play(g.Buf, g.Fs, new[] { "zone4", "led1" }.SelectMany(d => new[] { (MusicStyles.Spectrum, d, true), (MusicStyles.Spectrum, d, false) }).ToList());
        // 音の大きさ（前後 0.25 秒の 2 乗平均、dB）と、0.5 秒でならした明るさの相関
        int n = run.Lanes[0].A.Count;
        var db = new double[n];
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, (int)((i * Tick - 0.25) * g.Fs)), b = Math.Min(g.Buf.Length, (int)((i * Tick + 0.25) * g.Fs));
            double s2 = 0;
            for (int j = a; j < b; j++) s2 += g.Buf[j] * (double)g.Buf[j];
            db[i] = 10 * Math.Log10(Math.Max(1e-12, s2 / Math.Max(1, b - a)));
        }
        foreach (var L in run.Lanes)
        {
            var sm = new double[n];
            double acc = 0;
            for (int i = 0; i < n; i++) { acc += L.A[i][0]; if (i >= 30) acc -= L.A[i - 30][0]; sm[i] = acc / Math.Min(i + 1, 30); }
            double corr = Corr(sm, db, 6 * 60, 23 * 60);
            double p95 = P95(L, 6, 12), mean = Mean(L, 6, 12), quiet = Mean(L, 13, 17), loud = Mean(L, 18.5, 23.5);
            T.Info($"{L.Key,-24} music p95 {p95:F2} mean {mean:F2}, quiet part {quiet:F2} vs loud {loud:F2}, loudness corr {corr:F2}");
            T.Check(p95 >= 0.95 && mean >= 0.38, $"{L.Key}: music lights the small zone as brightly as 6.15.10 (p95 {p95:F2}, mean {mean:F2})");
            T.Check(corr >= 0.3, $"{L.Key}: louder parts of the song are brighter (corr {corr:F2})");
        }
    }

    /// <summary>
    /// 予告編: 大きな打つ音のあと、打つ音のない続く音（ライザー: 中音・高音だけの雑音と上がっていく音、4 秒で −40 → −14 dB）。
    /// スペクトラムの小さいゾーンは、だんだん明るくなる（95% 点が 0.25 以上、最後の 1 秒の平均が最初の 1 秒の 2 倍以上）。
    /// 直す前は、明るさを 3 つの帯の平均で決めていたので、上がりきっても暗いままだった（95% 点 0.11）
    /// </summary>
    public static void TestSmallZoneSpectrumRiserBrightens()
    {
        var g = new SignalGen(48000, 11, seed: 52);
        var r = new Random(52);
        g.Pink(-62, 0, 11);
        foreach (double t in new[] { 0.5, 1.7, 2.4, 3.3 }) { g.Kick(t, 0.7); g.White(-20, t, t + 0.3); }
        double hp = 0, prev = 0, ph = 0;
        int a = 5 * g.Fs, b = 9 * g.Fs;
        for (int i = a; i < b; i++)
        {
            double u = (i - a) / (double)(b - a);
            double amp = Math.Pow(10, (-40 + 26 * u) / 20) * 1.41;
            double w = r.NextDouble() * 2 - 1;
            hp = 0.9 * (hp + w - prev);   // 1 次の高い音を通すフィルター（およそ 800 Hz 以上）
            prev = w;
            ph += 2 * Math.PI * (1500 + 2500 * u) / g.Fs;
            g.Buf[i] += (float)(amp * (0.6 * hp + 0.5 * Math.Sin(ph)));
        }
        var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.Spectrum, "zone4", true), (MusicStyles.Spectrum, "led1", true), (MusicStyles.Spectrum, "zone4", false) });
        foreach (var L in run.Lanes)
        {
            double p95 = P95(L, 5, 9), first = Mean(L, 5, 6), last = Mean(L, 8, 9);
            T.Info($"{L.Key,-24} riser p95 {p95:F2}, first second {first:F2}, last second {last:F2}");
            T.Check(p95 >= 0.25 && last >= 2 * first, $"{L.Key}: a rising sound brightens the small zone (p95 {p95:F2}, {first:F2} -> {last:F2})");
        }
    }

    /// <summary>
    /// 部屋の音（−50 dBFS）の上の、1 秒に 1 回の短い電子音（880 Hz・0.15 秒。試合開始のカウントダウン）: スペクトラムの小さいゾーンと
    /// 遅いデバイス（直接書き込むグラボ・メモリ・明るさが一定のグラボ）は、電子音のときだけ光る。電子音のない間の光り始め（明るさが一定の
    /// グラボは色の変化）は 1 秒に 0.3 回まで、小さいゾーンのちらつきは 1.0 まで、電子音では小さいゾーンが 0.3 以上明るくなる。
    /// 直す前は、電子音の間の部屋の音の小さなゆらぎでも同じくらい光っていた（電子音のない光り始めが 1 秒に 1.3〜2.2 回、ちらつき 1.9）
    /// </summary>
    public static void TestSmallZoneBeepsOverRoomToneAreCalm()
    {
        var g = new SignalGen(48000, 16, seed: 53);
        g.Pink(-50, 0, 16);
        var beeps = Enumerable.Range(0, 10).Select(k => 3.0 + k).ToList();
        foreach (double t in beeps) g.Tone(880, 0.3, t, t + 0.15);
        var devs = new[] { "zone4", "led1", "direct", "dram", "gpu" };
        var run = Play(g.Buf, g.Fs, devs.SelectMany(d => new[] { (MusicStyles.Spectrum, d, true), (MusicStyles.Spectrum, d, false) }).ToList());
        bool NearBeep(double t) => beeps.Any(b => t >= b - 0.05 && t <= b + 0.35);
        // 色は、電子音のあと元の色へ戻るのに少しかかる
        bool NearBeepColour(double t) => beeps.Any(b => t >= b - 0.05 && t <= b + 0.6);
        foreach (var L in run.Lanes)
        {
            double from = 3.5, to = 13;
            double orphan = Rate(Flashes(L).Where(t => !NearBeep(t)).ToList(), from, to);
            // 色の変化: 0.25 秒前と比べて、明るさをそろえた色が 0.3 以上変わった始まり（0.25 秒以上あける）
            var col = new List<double>();
            for (int i = 15; i < L.C.Count; i++)
            {
                Rgb c0 = L.C[i - 15], c1 = L.C[i];
                double m0 = Math.Max(c0.R, Math.Max(c0.G, c0.B)), m1 = Math.Max(c1.R, Math.Max(c1.G, c1.B));
                if (m0 < 13 || m1 < 13) continue;
                double d = Math.Abs(c1.R / m1 - c0.R / m0) + Math.Abs(c1.G / m1 - c0.G / m0) + Math.Abs(c1.B / m1 - c0.B / m0);
                if (d >= 0.3 && (col.Count == 0 || i / 60.0 - col[^1] > 0.25)) col.Add(i / 60.0);
            }
            double colOrphan = Rate(col.Where(t => !NearBeepColour(t)).ToList(), from, to);
            int leds = DeviceOf(L.Dev).LedCount;
            double jit = Jit(L, leds, from, to);
            double resp = beeps.Where(b => b > 3.5).Select(b =>
                Frames(L, b, b + 0.2).Select(i => (double)L.A[i][0]).DefaultIfEmpty(0).Max() -
                Frames(L, b - 0.15, b).Select(i => (double)L.A[i][0]).DefaultIfEmpty(0).Min()).Average();
            T.Info($"{L.Key,-24} beeps over room tone: flashes without a beep {orphan:F2}/s, colour moves without a beep {colOrphan:F2}/s, jitter {jit:F2}, beep rise {resp:F2}");
            // 明るさが一定のグラボは、色だけが変わる（ほかのデバイスは、電子音の色からゆっくり戻るのも色の変化に数えてしまうので、明るさで見る）
            T.Check(orphan <= 0.3 && (L.Dev != "gpu" || colOrphan <= 0.3), $"{L.Key}: room tone between beeps does not light the device ({orphan:F2}/s, colour {colOrphan:F2}/s)");
            if (L.Dev is "zone4" or "led1")
                T.Check(jit <= 1.0 && resp >= 0.3, $"{L.Key}: no flicker between beeps, and each beep still lights the zone (jitter {jit:F2}, rise {resp:F2})");
        }
    }

    // ===== テンポの追跡: シンコペーションの曲・曲のあとの話し声・ジングル・大声

    private static readonly double[] BandBass = { 55, 55, 65.4, 49 };

    /// <summary>バンドの伴奏（キック・2 拍目と 4 拍目のスネア・ハイハット・ベース）。拍の時刻を返す</summary>
    private static List<double> Band(SignalGen g, double bpm, double from, double to)
    {
        var beats = new List<double>();
        double p = 60 / bpm;
        int k = 0;
        for (double t = from; t < to - 1e-9; t += p, k++)
        {
            beats.Add(t);
            g.Kick(t, 0.7);
            if (k % 2 == 1) g.Snare(t, 0.32);
            g.Hat(t + p / 2, 0.07);
            g.Hat(t + p / 4, 0.03);
            g.Hat(t + 3 * p / 4, 0.03);
            double f = BandBass[(k / 2) % 4], end = Math.Min(to, t + p - 0.01);
            g.Tone(f, 0.11, t + 0.02, end);
            g.Tone(2 * f, 0.04, t + 0.02, end);
        }
        return beats;
    }

    /// <summary>ドラムンベース 174 BPM: キックは 1 拍目と 3 拍目の裏、スネアは 2・4 拍目、16 分のハイハット、うなるベース（44 秒）</summary>
    private static SignalGen DrumAndBass174()
    {
        var g = new SignalGen(48000, 44, seed: 31);
        double p = 60 / 174.0;
        int k = 0;
        double[] notes = { 41.2, 41.2, 49.0, 36.7 };
        for (double t = 0.5; t < 43.5 - 1e-9; t += p, k++)
        {
            int b = k % 4;
            if (b == 0) g.Kick(t, 0.75);
            if (b == 2) g.Kick(t + p / 2, 0.6);
            if (b == 1 || b == 3) g.Snare(t, 0.42);
            g.Hat(t, 0.04);
            g.Hat(t + p / 2, 0.06);
            g.Hat(t + p / 4, 0.02);
            g.Hat(t + 3 * p / 4, 0.02);
            if (b == 0)
            {
                double f = notes[(k / 4) % 4], end = Math.Min(43.5, t + 4 * p - 0.01);
                g.Tone(f, 0.10, t + 0.01, end);
                g.Tone(f * 1.012, 0.08, t + 0.01, end);
                g.Tone(2 * f, 0.05, t + 0.01, end);
                g.Tone(3 * f * 1.006, 0.03, t + 0.01, end);
            }
        }
        g.PadChord(0.5, 43.5, new[] { 220.0, 261.6, 329.6 }, 0.03);
        g.Pink(-36, 0.5, 43.5);
        return g;
    }

    /// <summary>生演奏 120 BPM: テンポが ±4% で periodSec 秒ごとにゆれ、どの音も ±8 ミリ秒ずれる（46 秒）</summary>
    private static SignalGen LiveBand120(double periodSec)
    {
        var g = new SignalGen(48000, 46, seed: 35);
        var rng = new Random(35);
        double t = 0.5;
        int k = 0;
        while (t < 45.5)
        {
            double p = 60 / (120 * (1 + 0.04 * Math.Sin(2 * Math.PI * t / periodSec)));
            double J() => (rng.NextDouble() - 0.5) * 0.016;
            g.Kick(t + J(), 0.7);
            if (k % 2 == 1) g.Snare(t + J(), 0.34);
            g.Hat(t + p / 2 + J(), 0.07);
            g.Hat(t + J(), 0.04);
            double f = BandBass[(k / 2) % 4];
            g.Tone(f, 0.11, t + 0.02, t + p - 0.01);
            g.Tone(2 * f, 0.04, t + 0.02, t + p - 0.01);
            t += p;
            k++;
        }
        g.PadChord(0.5, 45.5, new[] { 261.6, 329.6, 392 }, 0.035);
        g.Pink(-34, 0.5, 45.5);
        return g;
    }

    /// <summary>話し声（音の高さが話し声くらいの声を、0.8〜2.4 秒しゃべって 0.25〜0.95 秒あける）</summary>
    private static void Talk(SignalGen g, Random rng, double from, double to, double amp)
    {
        for (double t = from; t < to - 0.5;)
        {
            double len = Math.Min(to - t, 0.8 + rng.NextDouble() * 1.6);
            g.VocalTone(t, t + len, Speech, 0.12 + rng.NextDouble() * 0.15, amp: amp, vibratoCents: 10);
            t += len + 0.25 + rng.NextDouble() * 0.7;
        }
    }

    private static double LockedShare(Run run, double a, double b) =>
        Enumerable.Range((int)(a * 60), (int)((b - a) * 60)).Count(i => i < run.Lock.Count && run.Lock[i] != BeatLock.Unlocked) / ((b - a) * 60);

    /// <summary>
    /// シンコペーションの曲（ドラムンベース 174 BPM。キックが 3 拍目の裏）とテンポのゆれる生演奏（120 BPM、24 秒ごとに ±4%）: 曲の途中でロックを外さない
    /// （ドラムンベースは 98% 以上、生演奏は 88% 以上。6.15.10 と同じ）。
    /// 直す前は、拍の位置の取り直しで確かさが下がり、2 秒たつとロックを外していた（ドラムンベース 89%・生演奏 68%）
    /// </summary>
    public static void TestSyncopatedSongKeepsTheLock()
    {
        var dnb = Play(DrumAndBass174().Buf, 48000, new[] { (MusicStyles.BeatHue, "strip24", true) });
        double d = LockedShare(dnb, 8, 43);
        var live = Play(LiveBand120(24).Buf, 48000, new[] { (MusicStyles.BeatHue, "strip24", true) });
        double l = LockedShare(live, 8, 45);
        T.Info($"locked/coasting: drum & bass 174 {d:P0}, live 120 (24 s drift) {l:P0}");
        T.Check(d >= 0.98, $"drum & bass 174 keeps the lock through the song ({d:P0})");
        T.Check(l >= 0.88, $"live band with a drifting tempo keeps the lock ({l:P0})");
    }

    /// <summary>
    /// ポッドキャスト: 曲（110 BPM）が 12〜14 秒で消えて話し声だけになり、24 秒から小さな曲（−18 dB）が話し声の下に戻る。
    /// 曲が消えたらすぐロックを外し（15.6 秒まで）、話し声の上で前の曲の拍で光らない（ビートで色替え: 14.3〜18 秒に前の曲の拍の上の光は 3 つまで）。
    /// 戻った曲には 4 秒以内にロックする。直す前は 17.5 秒までロックが残り、前の曲の拍で 7 回光っていた
    /// </summary>
    public static void TestTalkAfterMusicLetsTheOldBeatGo()
    {
        const int fs = 48000;
        var gm = new SignalGen(fs, 34, seed: 37);
        var intro = Band(gm, 110, 0.5, 14);
        gm.PadChord(0.5, 14, new[] { 261.6, 329.6, 392 }, 0.035);
        var gb = new SignalGen(fs, 34, seed: 38);
        Band(gb, 110, 24.13, 34);
        gb.PadChord(24.13, 34, new[] { 261.6, 329.6, 392 }, 0.035);
        var gs = new SignalGen(fs, 34, seed: 39);
        Talk(gs, new Random(39), 14.3, 34, 0.14);
        gs.Pink(-56, 0, 34);
        var buf = new float[fs * 34];
        for (int i = 0; i < buf.Length; i++)
        {
            double t = i / (double)fs;
            double gmus = t < 12 ? 1 : t < 14 ? Math.Pow(10, -40 * (t - 12) / 2 / 20) : 0;
            buf[i] = (float)Math.Clamp(gm.Buf[i] * gmus + gb.Buf[i] * Math.Pow(10, -18 / 20.0) + gs.Buf[i], -1, 1);
        }
        var run = Play(buf, fs, new[] { (MusicStyles.BeatHue, "strip24", true), (MusicStyles.Ripple, "strip24", true) });
        int n = run.Lock.Count;
        int release = Enumerable.Range(13 * 60, n - 13 * 60).FirstOrDefault(i => run.Lock[i] == BeatLock.Unlocked, n);
        int relock = Enumerable.Range(25 * 60, n - 25 * 60).FirstOrDefault(i => run.Lock[i] != BeatLock.Unlocked, n);
        double p = 60 / 110.0;
        var old = new List<double>();
        for (double t = intro[^1] + p; t < 24; t += p) old.Add(t);
        double introLock = LockedShare(run, 6, 12);
        T.Info($"talk after music: released at {release / 60.0:F2} s, locked again on the quiet music at {relock / 60.0:F2} s, locked during the intro {introLock:P0}");
        T.Check(introLock >= 0.95, $"the intro music is locked ({introLock:P0})");
        T.Check(release / 60.0 <= 15.6, $"the lock is let go soon after the music fades out ({release / 60.0:F2} s)");
        T.Check(relock / 60.0 <= 28.2, $"the music returning under the talk is locked within 4 s ({relock / 60.0:F2} s)");
        foreach (var L in run.Lanes.Where(l => l.Style == MusicStyles.BeatHue))
        {
            int stale = Flashes(L).Count(t => t >= 14.3 && t < 18 && old.Any(b => Math.Abs(t - b) <= 0.04));
            T.Info($"{L.Key,-24} flashes on the old beat over the talk: {stale}");
            T.Check(stale <= 3, $"{L.Key}: the talk is not lit on the old beat ({stale} flashes)");
        }
    }

    /// <summary>
    /// ニュースのジングル: 話し声のあと、120 BPM の 7 拍のジングル（キック・ティンパニ・金管）が、シンバルの余韻（1.5 秒で小さくなる雑音）で終わり、また話し声。
    /// 最後のキックから 1.1 秒あとより後のロックは 1 秒まで（10% まで）、2.2 秒あとより後はロックしていない（拍の見当で光らない）。
    /// 直す前は、余韻の小さな山を拍とみなして、ジングルが終わってからロックし、話し声の上で 3 秒ほど（30%）拍の見当で光っていた
    /// </summary>
    public static void TestJingleThenTalkDoesNotKeepTheBeat()
    {
        foreach (int seed in new[] { 61, 62, 63, 64 })
        {
            var g = new SignalGen(48000, 22, seed: seed);
            var rng = new Random(seed);
            g.Pink(-58, 0, 22);
            Talk(g, rng, 1, 7.6, 0.12);
            double p = 0.5, t0 = 8;
            for (int k = 0; k < 7; k++)
            {
                double t = t0 + k * p;
                g.Kick(t, 0.5);
                g.Tone(k % 2 == 0 ? 98 : 73.4, 0.2, t, t + 0.25);
                g.Tone(k % 2 == 0 ? 1568 : 2093, 0.05, t, t + 0.12);
                g.Hat(t + p / 2, 0.05);
            }
            g.BrassTone(t0, t0 + 6 * p, new[] { 392.0, 392, 523.3, 466.2, 523.3, 587.3 }, p, 0.14);
            double end = t0 + 6 * p;
            for (double t = end; t < end + 1.5; t += 0.05) g.White(-17 - 26 * (t - end) / 1.5, t, t + 0.05);
            g.BrassTone(end, end + 1.2, new[] { 784.0 }, 1.2, 0.17);
            Talk(g, rng, end + 0.8, 22, 0.12);
            var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.Ripple, "strip24", true) });
            double after = LockedShare(run, end + 1.1, 21.5), later = LockedShare(run, end + 2.2, 21.5);
            T.Info($"seed {seed}: locked/coasting during the jingle {LockedShare(run, t0, end + 0.3):P0}, after the jingle {after:P1}, from 2.2 s after the last kick {later:P1}");
            T.Check(after <= 0.1 && later <= 0.005, $"seed {seed}: no beat is kept over the talk after the jingle ({after:P1}, {later:P1})");
        }
    }

    /// <summary>
    /// 大声の言い合い（2 人が交互に、ときどき重なってしゃべる。声帯の音を 3 つの共鳴に通した声で、音節は 1 秒に 4〜6 個、
    /// 音節の頭に破裂音・摩擦音。最後に強くひずませる）
    /// </summary>
    private static void Argument(float[] b, int fs, Random r, double from, double to, double amp)
    {
        double N() => r.NextDouble() * 2 - 1;
        void Add(int i, double v) { if (i >= 0 && i < b.Length) b[i] = (float)Math.Clamp(b[i] + v, -1, 1); }
        double[][] vowels = { new[] { 730.0, 1090, 2440 }, new[] { 530.0, 1840, 2480 }, new[] { 270.0, 2290, 3010 },
                              new[] { 570.0, 840, 2410 }, new[] { 440.0, 1020, 2240 }, new[] { 300.0, 870, 2240 }, new[] { 660.0, 1720, 2410 } };
        double[] bw = { 90, 110, 170 };
        void Vowel(double t0, double dur, double f0a, double f0b, double[] fa, double[] fb, double a)
        {
            int s0 = (int)(t0 * fs), n = (int)(dur * fs);
            double ph = 0, lp = 0;
            var y1 = new double[3];
            var y2 = new double[3];
            for (int i = 0; i < n; i++)
            {
                double u = i / (double)n;
                ph += (f0a + (f0b - f0a) * u) / fs;
                double x = 0;
                if (ph >= 1) { ph -= 1; x = 1; }
                lp += 0.25 * (x - lp);
                double src = lp + 0.006 * N();
                double env = Math.Min(1, i / (0.025 * fs)) * Math.Min(1, (n - i) / (0.04 * fs));
                double o = 0;
                for (int k = 0; k < 3; k++)
                {
                    double f = fa[k] + (fb[k] - fa[k]) * u, rr = Math.Exp(-Math.PI * bw[k] / fs);
                    double y = (1 - rr) * src * (k == 0 ? 1.0 : k == 1 ? 0.6 : 0.3) * 12 + 2 * rr * Math.Cos(2 * Math.PI * f / fs) * y1[k] - rr * rr * y2[k];
                    y2[k] = y1[k];
                    y1[k] = y;
                    o += y;
                }
                Add(s0 + i, a * env * o);
            }
        }
        void Phrase(double t0, double dur, double f0Hi, double f0Lo, double a0)
        {
            for (double t = t0; t < t0 + dur - 0.1;)
            {
                double syl = 0.12 + r.NextDouble() * 0.11, gap = 0.02 + r.NextDouble() * 0.06, a = a0 * (0.55 + 0.45 * r.NextDouble());
                int kind = r.Next(4);
                int s0 = (int)(t * fs);
                if (kind == 0)
                {
                    int n = (int)((0.05 + 0.05 * r.NextDouble()) * fs);
                    double hp = 0, prev = 0;
                    for (int i = 0; i < n; i++) { double w = N(); hp = 0.6 * (hp + w - prev); prev = w; Add(s0 + i, a * 0.35 * Math.Sin(Math.PI * i / n) * hp); }
                }
                else if (kind == 1)
                    for (int i = 0; i < (int)(0.015 * fs); i++) Add(s0 + i, a * 0.6 * Math.Exp(-i / (0.004 * fs)) * N());
                double f0a = f0Hi + (f0Lo - f0Hi) * (t - t0) / dur + 15 * N();
                Vowel(t + (kind == 0 ? 0.04 : 0.01), syl, f0a, f0a - 10 - 20 * r.NextDouble(), vowels[r.Next(vowels.Length)], vowels[r.Next(vowels.Length)], a);
                t += syl + gap + (kind == 0 ? 0.04 : 0.01);
            }
        }
        bool male = true;
        for (double t = from; t < to - 1;)
        {
            double dur = Math.Min(to - t, 0.8 + r.NextDouble() * 2.2);
            if (male) Phrase(t, dur, 230, 170, amp);
            else Phrase(t, dur, 360, 290, amp * 0.9);
            male = !male;
            double next = r.NextDouble() < 0.35 ? dur - 0.25 - 0.3 * r.NextDouble() : dur + 0.08 + 0.3 * r.NextDouble();
            t += Math.Max(0.4, next);
        }
        double k0 = Math.Tanh(1.6);
        for (int i = (int)(from * fs); i < Math.Min(b.Length, (int)(to * fs)); i++) b[i] = (float)(Math.Tanh(1.6 * b[i]) / k0 * 0.9);
    }

    /// <summary>
    /// 大声の言い合い（音楽なし。たまたま声がそろって追跡がロックする 5 通り）: ロックしても、確かさが下がればすぐ外す
    /// （ロック・ロックの続きは 1 通り 3.5 秒まで、合計 13 秒まで）。直す前は 1 回ロックすると拍の見当で 3.6〜7.9 秒（合計 28 秒）光っていた。
    /// 曲（120 BPM）のあとに大きな話し声が続き、拍の AI が話し声の上にそろった拍（123 BPM）を返し続ける: 曲が止まったらロックを外し、
    /// そのあとの音だけで決めた AI の並びに合わせても、追跡が拍に音がないと分かればすぐ外す（10〜30 秒のロック・ロックの続きが 10% まで。直す前は 26%）
    /// </summary>
    public static void TestShoutingDoesNotHoldAFakeTempo()
    {
        double total = 0;
        foreach (int seed in new[] { 13, 14, 24, 25, 30 })
        {
            var g = new SignalGen(48000, 24, seed: seed);
            g.Pink(-55, 0, 24);
            Argument(g.Buf, g.Fs, new Random(seed), 1, 23, 0.75);
            var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.BeatHue, "strip24", true) });
            double sec = LockedShare(run, 2, 23) * 21;
            total += sec;
            T.Info($"seed {seed}: locked/coasting {sec:F2} s");
            T.Check(sec <= 3.5, $"seed {seed}: a lock on shouting is let go soon ({sec:F2} s)");
        }
        T.Info($"locked/coasting in total {total:F2} s");
        T.Check(total <= 13, $"shouting does not hold a made-up tempo ({total:F2} s in total)");

        var gm = new SignalGen(48000, 32, seed: 77);
        gm.Pink(-55, 0, 32);
        gm.DenseMix(120, 0, 9);
        Talk(gm, new Random(77), 9.3, 31, 0.3);
        var ai = Play(gm.Buf, gm.Fs, new[] { (MusicStyles.BeatHue, "strip24", true) },
            ai: (a, b) => Enumerable.Range(0, 80).Select(k => T0 + 0.2 + k * 60 / 123.0).Where(t => t >= a && t <= b).ToList());
        double music = LockedShare(ai, 6, 9), talk = LockedShare(ai, 10, 30);
        T.Info($"music then talk with an AI grid on the voices: locked during the music {music:P0}, during the talk {talk:P0}, steers at {string.Join(", ", ai.SteerT.Select(t => t.ToString("F1")))}");
        T.Check(music >= 0.9, $"the music before the talk is locked ({music:P0})");
        T.Check(talk <= 0.1, $"an AI grid on the talk does not keep a made-up tempo ({talk:P0})");
    }

    // ===== 大きな音のあと・小さいゾーン・話し声・生演奏（3 回目の見直し）

    /// <summary>明るさをそろえた色（最初の LED）が、0.25 秒前より 0.3 以上変わった始まり（0.25 秒以上あける）</summary>
    private static List<double> ColourMoves(Lane l, double a, double b)
    {
        var col = new List<double>();
        for (int i = Math.Max(15, (int)(a * 60)); i < Math.Min(l.C.Count, (int)(b * 60)); i++)
        {
            Rgb c0 = l.C[i - 15], c1 = l.C[i];
            double m0 = Math.Max(c0.R, Math.Max(c0.G, c0.B)), m1 = Math.Max(c1.R, Math.Max(c1.G, c1.B));
            if (m0 < 13 || m1 < 13) continue;
            double d = Math.Abs(c1.R / m1 - c0.R / m0) + Math.Abs(c1.G / m1 - c0.G / m0) + Math.Abs(c1.B / m1 - c0.B / m0);
            if (d >= 0.3 && (col.Count == 0 || i / 60.0 - col[^1] > 0.25)) col.Add(i / 60.0);
        }
        return col;
    }

    /// <summary>時刻 t の音での、区域ごとの上がりのいちばん大きい値（流れる帯は一部の区域から光る）</summary>
    private static double RiseInArea(Lane l, double t)
    {
        double best = 0;
        for (int k = 0; k < l.Areas.Length; k++)
        {
            double hi = Frames(l, t, t + 0.15).Select(i => (double)l.A[i][k]).DefaultIfEmpty(0).Max();
            double lo = Frames(l, t - 0.15, t).Select(i => (double)l.A[i][k]).DefaultIfEmpty(0).Min();
            best = Math.Max(best, hi - lo);
        }
        return best;
    }

    /// <summary>時刻 t の音での上がり（t 〜 t + 0.15 秒の最大 − t − 0.15 〜 t 秒の最小）</summary>
    private static double RiseAt(Lane l, double t) =>
        Frames(l, t, t + 0.15).Select(i => (double)l.A[i][0]).DefaultIfEmpty(0).Max() -
        Frames(l, t - 0.15, t).Select(i => (double)l.A[i][0]).DefaultIfEmpty(0).Min();

    /// <summary>
    /// 大きな音（0 dB の「ドン」）のすぐあとの静かな曲（低い音と高い音を 0.6 秒ごとに交互。−24・−30・−36 dBFS くらい）でも、
    /// スペクトラムの明るさを一定にするグラボ・メモリは、音ごとに色が変わる（16 音のうち 14 音以上、最初の 6 音のうち 5 音以上。
    /// 0.5 秒以内に明るさをそろえた色が 0.3 以上変わる）。
    /// 直す前は、直近の大きな音より 18〜26 dB 小さい音を部屋の音とみなして、グラボの色を止めていた（−36 dBFS で 5 音だけ）
    /// </summary>
    public static void TestSpectrumFollowsQuietNotesAfterAHit()
    {
        foreach (double amp in new[] { 0.06, 0.03, 0.015 })
        {
            var g = new SignalGen(48000, 16, seed: 77);
            g.Pink(-60, 0, 16);
            g.Kick(4.0, 0.95);
            g.Pink(-8, 4.0, 4.25);
            var notes = new List<double>();
            for (int k = 0; 4.5 + k * 0.6 < 14; k++)
            {
                double t = 4.5 + k * 0.6;
                notes.Add(t);
                if (k % 2 == 0) { g.Tone(82.4, amp, t, t + 0.55); g.Tone(164.8, amp * 0.4, t, t + 0.55); }
                else { g.Tone(3520, amp, t, t + 0.55); g.Tone(1760, amp * 0.5, t, t + 0.55); }
            }
            var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.Spectrum, "gpu", true), (MusicStyles.Spectrum, "gpu", false), (MusicStyles.Spectrum, "dram", true) });
            foreach (var L in run.Lanes)
            {
                int follow = 0, early = 0;
                for (int k = 0; k < notes.Count; k++)
                {
                    double t = notes[k];
                    int i0 = (int)Math.Round((t - 0.02) * 60), i1 = Math.Min(L.C.Count - 1, (int)Math.Round((t + 0.5) * 60));
                    Rgb c0 = L.C[i0];
                    double best = 0;
                    for (int i = i0; i <= i1; i++)
                    {
                        Rgb c1 = L.C[i];
                        double m0 = Math.Max(c0.R, Math.Max(c0.G, c0.B)), m1 = Math.Max(c1.R, Math.Max(c1.G, c1.B));
                        if (m0 < 3 || m1 < 3) continue;
                        best = Math.Max(best, Math.Abs(c1.R / m1 - c0.R / m0) + Math.Abs(c1.G / m1 - c0.G / m0) + Math.Abs(c1.B / m1 - c0.B / m0));
                    }
                    if (best < 0.3) continue;
                    follow++;
                    if (k < 6) early++;
                }
                T.Info($"{L.Key,-24} notes at amp {amp}: colour follows {follow} of {notes.Count} (first 6: {early})");
                T.Check(follow >= 14 && early >= 5, $"{L.Key}: quiet notes after a big hit still change the colour (amp {amp}: {follow} of {notes.Count}, first 6: {early})");
            }
        }
    }

    /// <summary>
    /// 曲で、小さいゾーン（4 個・1 個）の波紋は強さの設定に合う（6.15.10 と同じく、「ふつう」以上は拍で明るさいっぱい、
    /// 「控えめ」はそれより暗い）、拍の間は暗くなる: 128 BPM のキックとベースの曲で、「ふつう」「強め」「激しい」の 95% 点が 0.95 以上・
    /// 95% 点 − 5% 点が 0.8 以上、「控えめ」の 95% 点が「激しい」より小さい。
    /// ワルツ（150 BPM。1 拍目にキックとベース、2・3 拍目は和音だけ）では、1 拍目がほかの拍より明るい。
    /// 直す前は、どの拍もどの強さの設定も同じ明るさ（95% 点 0.83〜0.87）で、拍の間も 0.3 ほど残っていた
    /// </summary>
    public static void TestSmallZoneRippleFollowsBeatAndIntensity()
    {
        var g = new SignalGen(48000, 20, seed: 56);
        g.Loop(128, 0.5, 20, hats: true, snare: true);
        g.BassLine(0.5, 20, 2.13, new[] { 55.0, 65.4, 73.4, 82.4 }, amp: 0.2);
        g.PadChord(0.5, 20, new[] { 220.0, 277.2, 329.6 }, amp: 0.04);
        var p95 = new Dictionary<string, double>();
        foreach (var intensity in new[] { Intensities.Subtle, Intensities.Moderate, Intensities.High, Intensities.Intense })
        {
            var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.Ripple, "zone4", true), (MusicStyles.Ripple, "led1", true), (MusicStyles.Ripple, "zone4", false) }, intensity);
            foreach (var L in run.Lanes)
            {
                var x = Frames(L, 6, 19.5).Select(i => (double)L.A[i][0]).OrderBy(v => v).ToList();
                double hi = x[(int)(0.95 * (x.Count - 1))], lo = x[(int)(0.05 * (x.Count - 1))];
                p95[$"{intensity} {L.Key}"] = hi;
                T.Info($"{intensity,-9} {L.Key,-22} music: p05 {lo:F2} p95 {hi:F2}");
                if (intensity != Intensities.Subtle)
                    T.Check(hi >= 0.95 && hi - lo >= 0.8, $"{intensity} {L.Key}: beats light the small zone fully and it gets dark between them (p95 {hi:F2}, p95 - p05 {hi - lo:F2})");
            }
        }
        foreach (var key in p95.Keys.Where(k => k.StartsWith(Intensities.Subtle + " ")).ToList())
        {
            string lane = key[(Intensities.Subtle.Length + 1)..];
            T.Check(p95[key] < p95[$"{Intensities.Intense} {lane}"], $"{lane}: Subtle is dimmer than Intense ({p95[key]:F2} < {p95[$"{Intensities.Intense} {lane}"]:F2})");
        }

        // ワルツ: 1 拍目（キック・ベース）と 2・3 拍目（和音とハイハットだけ）
        var w = new SignalGen(48000, 22, seed: 57);
        double p = 60 / 150.0;
        var down = new List<double>();
        var off = new List<double>();
        int k = 0;
        for (double t = 0.5; t < 21.5; t += p, k++)
        {
            if (k % 3 == 0)
            {
                down.Add(t);
                w.Kick(t, 0.8);
                w.Tone(55, 0.25, t + 0.01, t + 0.35);
                w.Tone(110, 0.08, t + 0.01, t + 0.35);
            }
            else
            {
                off.Add(t);
                w.Strum(t, new[] { 220.0, 277.2, 329.6 }, amp: 0.05);
                w.Hat(t, 0.06);
            }
        }
        var wr = Play(w.Buf, w.Fs, new[] { (MusicStyles.Ripple, "zone4", true), (MusicStyles.Ripple, "led1", false) });
        foreach (var L in wr.Lanes)
        {
            double Peak(double t) => Frames(L, t - 0.02, t + 0.2).Select(i => (double)L.A[i][0]).DefaultIfEmpty(0).Max();
            double dn = down.Where(t => t > 8 && t < 21).Average(Peak), of = off.Where(t => t > 8 && t < 21).Average(Peak);
            T.Info($"{L.Key,-24} waltz: first beat {dn:F2}, other beats {of:F2}");
            T.Check(dn >= of + 0.1, $"{L.Key}: the waltz's first beat (kick and bass) is brighter than the other beats ({dn:F2} vs {of:F2})");
        }
    }

    /// <summary>
    /// 話し声が続くとキックの光を弱くする（TalkFatigue。EffectsTests.TestBeatHueTalkFatigue）ようにしても、
    /// ドラムのないピアノ（0.5〜1.4 秒ごとの音を 22 秒）では「ビートで色替え」は今までどおり光る（見て分かる光り始めが、8 秒より後の音の 50% 以上。
    /// ストリップ・小さいゾーン・グラボ直接。「激しい点滅を抑える」のオン・オフとも）
    /// </summary>
    public static void TestBeatHueDrumlessPianoStillLights()
    {
        var lanes = new[] { "strip24", "zone4", "direct" }.SelectMany(dv => new[] { (MusicStyles.BeatHue, dv, true), (MusicStyles.BeatHue, dv, false) }).ToList();
        var pg = new SignalGen(48000, 24, seed: 58);
        var rng = new Random(58);
        pg.Pink(-60, 0, 24);
        double[] scale = { 261.6, 293.7, 329.6, 392.0, 440.0, 523.3 };
        var notes = new List<double>();
        for (double t = 1; t < 23; t += 0.5 + 0.9 * rng.NextDouble())
        {
            notes.Add(t);
            pg.Strum(t, new[] { scale[rng.Next(scale.Length)] }, amp: 0.06 + 0.04 * rng.NextDouble());
        }
        var piano = Play(pg.Buf, pg.Fs, lanes);
        foreach (var L in piano.Lanes)
        {
            var fl = Flashes(L).Where(t => t >= 8 && t < 23).ToList();
            int lit = notes.Count(t => t >= 8 && t < 23 && fl.Any(f => f >= t - 0.03 && f <= t + 0.2));
            int all = notes.Count(t => t >= 8 && t < 23);
            T.Info($"{L.Key,-24} drumless piano: {lit} of {all} notes lit");
            T.Check(lit >= 0.5 * all, $"{L.Key}: drumless piano notes still light ({lit} of {all})");
        }
    }

    /// <summary>
    /// 生演奏（120 BPM、16 秒ごとにテンポが ±4% ゆれる）: 曲の途中でロックを外さない（8〜45 秒の 95% 以上。6.15.10 は 100%）。
    /// 直す前は、テンポのゆれで拍の位置が少しずつずれると、曲が続いているとみなさず、2 回（合わせて 5.6 秒）外していた（85%）
    /// </summary>
    public static void TestLiveBandWithFastDriftKeepsTheLock()
    {
        var live = Play(LiveBand120(16).Buf, 48000, new[] { (MusicStyles.Ripple, "zone4", true) });
        double l = LockedShare(live, 8, 45);
        T.Info($"live band 120 with a 16 s drift: locked/coasting {l:P0}");
        T.Check(l >= 0.95, $"a live band with a faster tempo drift keeps the lock ({l:P0})");
    }

    // ===== 6.15.11 の 3 回目の見直し

    /// <summary>ピッという音（正弦波。始まりと終わりは 3 ミリ秒でなめらかに。ゲームのカウントダウン・メニューの音）</summary>
    private static void Beep(SignalGen g, double t0, double freq, double dur, double amp)
    {
        int s0 = (int)(t0 * g.Fs), n = (int)(dur * g.Fs), fade = (int)(0.003 * g.Fs);
        for (int i = 0; i < n && s0 + i < g.Buf.Length; i++)
        {
            double k = Math.Min(1, Math.Min(i / (double)fade, (n - i) / (double)fade));
            g.Buf[s0 + i] = (float)Math.Clamp(g.Buf[s0 + i] + amp * k * Math.Sin(2 * Math.PI * freq * i / g.Fs), -1, 1);
        }
    }

    /// <summary>
    /// デジタルの無音の中の、1 秒ごとの 10 回のピッ（880 Hz・0.15 秒）: 1 回のピッで光るのは 1 回だけで、どのピッも見える。
    /// ピッのない時刻（ピッの間・最後のピッのあと）では強く光らない（ビートで色替え・きらめき。
    /// 「ふつう」「強め」、「激しい点滅を抑える」のオン・オフ）。
    /// 直す前は、ピッの終わりの切れ目を前の光より 6 dB 大きい音と読んで、0.15 秒後にもう一度強く光っていた
    /// （鳴り始めに読んだ音量が、上がりきる前の値だった）。6.15.10 から、120 BPM と見た拍の頭（ピッの間）でも光っていた。
    /// 見ないもの（6.15.10 と同じ）: 4 秒の無音のあとの最初のピッ（鳴り始めのキックが弱く出て、終わりの切れ目で光る）と、
    /// 「激しい点滅を抑える」がオフのきらめきの 2 回目の光（立ち上がりごとに光る光り方）
    /// </summary>
    public static void TestBeepCountdownFlashesOncePerBeep()
    {
        var g = new SignalGen(48000, 16, seed: 61);
        var all = Enumerable.Range(0, 10).Select(k => 4.0 + k).ToList();
        foreach (double t in all) Beep(g, t, 880, 0.15, 0.3);
        var beeps = all.Skip(1).ToList();
        var lanes = new List<(string, string, bool)>();
        foreach (bool rf in new[] { true, false })
        {
            foreach (var d in new[] { "strip24", "zone4", "led1" }) lanes.Add((MusicStyles.BeatHue, d, rf));
            foreach (var d in new[] { "strip24", "zone4" }) lanes.Add((MusicStyles.Sparkle, d, rf));
        }
        foreach (var intensity in new[] { Intensities.Moderate, Intensities.High })
        {
            var run = Play(g.Buf, g.Fs, lanes, intensity);
            foreach (var L in run.Lanes)
            {
                var strong = StrongStarts(L, 4.5, 15.5);
                var again = strong.Where(t => beeps.Any(b => t > b + 0.1 && t <= b + 0.25)).ToList();
                var away = strong.Where(t => !beeps.Any(b => t >= b - 0.02 && t <= b + 0.25)).ToList();
                bool onsetEach = L.Style == MusicStyles.Sparkle && !L.Rf;
                int seen = beeps.Count(b => Frames(L, b, b + 0.15).Select(i => (double)L.A[i][0]).DefaultIfEmpty(0).Max()
                    - Frames(L, b - 0.1, b).Select(i => (double)L.A[i][0]).DefaultIfEmpty(0).Min() >= 0.05);
                T.Info($"{intensity,-9} {L.Key,-24} strong {strong.Count}, again {again.Count}, away {away.Count}, beeps seen {seen}/{beeps.Count}");
                if (!onsetEach) T.Check(again.Count == 0, $"{intensity} {L.Key}: one beep gives one flash, not a second one at its end ({string.Join(" ", again.Select(t => t.ToString("F2")))})");
                T.Check(away.Count == 0, $"{intensity} {L.Key}: no strong flash where no beep sounds ({string.Join(" ", away.Select(t => t.ToString("F2")))})");
                T.Check(seen == beeps.Count, $"{intensity} {L.Key}: every beep is seen ({seen}/{beeps.Count})");
            }
        }
    }

    /// <summary>
    /// 手ごたえ（PresenceMeter）: 部屋の音（−48 dBFS）が 3 秒続くと下がる。大きな出来事（−8 dB）の Lift ですぐ 1 になる。
    /// 部屋の音と同じくらいの出来事（−56 dB）では変わらない
    /// </summary>
    public static void TestPresenceLiftsOnLoudEvents()
    {
        var p = new PresenceMeter();
        var f = new AudioFrame { GateOpen = true, LoudDb = -48, RmsDb = -48 };
        for (int k = 0; k < 180; k++) p.Update(f, 1 / 60.0);
        float before = p.Value;
        T.Check(before < 0.7, $"room tone at -48 dBFS lowers the presence ({before:F2})");
        p.Lift(-56);
        T.Check(Math.Abs(p.Value - before) < 1e-6, $"a sound as quiet as the room does not lift it ({p.Value:F2})");
        p.Lift(-8);
        T.Check(p.Value >= 0.999f, $"a loud event lifts it to 1 at once ({p.Value:F2})");
    }

    /// <summary>
    /// メモリ（1 秒に約 10 回）: 曲が止まって部屋の音（−48 dBFS）が続いたあとの爆発で、楽器で光る・アンサンブルがはっきり光る
    /// （0.15 秒の間の上がりが、楽器で光る 0.3 以上・アンサンブル 0.1 以上。足音より大きい）。
    /// 直す前は、手ごたえ（PresenceMeter）が 1〜2 コマ遅れて上がるので、爆発の光のいちばん明るいところを、暗くしたまま送っていた
    /// </summary>
    public static void TestDramSeesTheBlastAfterAQuietRoom()
    {
        var g = new SignalGen(48000, 24, seed: 62);
        var r = new Random(62);
        g.DenseMix(124, 0.5, 10);
        g.Pink(-48, 0, 24);
        var steps = new List<double>();
        for (double t = 13; t < 18; t += 0.5) { steps.Add(t); g.Kick(t, 0.08, click: false); }
        Explosion(g, r, 20, 0.9);
        var run = Play(g.Buf, g.Fs, All(new[] { "dram" }, new[] { MusicStyles.Role, MusicStyles.Ensemble }));
        foreach (var L in run.Lanes)
        {
            double blast = Rise(L, 20), step = steps.Select(t => Rise(L, t)).Average();
            double need = L.Style == MusicStyles.Role ? 0.3 : 0.1;
            T.Info($"{L.Key,-24} blast rise {blast:F2}, footsteps {step:F2}");
            T.Check(blast >= need, $"{L.Key}: the blast after a quiet room is clearly seen (rise {blast:F2}, need {need:F2})");
            T.Check(blast > step, $"{L.Key}: the blast is stronger than the footsteps ({blast:F2} > {step:F2})");
        }
    }

    /// <summary>
    /// 明るさを一定にするグラボ（OpenRGB）: 大きな低い音のあと、デジタルの無音で止まっているときの光り方へ切り替わる間も、
    /// どの LED も明るさが下がって見えない（送った色のいちばん強い成分が 0.9 以上。楽器で光る・スペクトラム。曲の長さを変えて、色の組み合わせを変える）。
    /// 直す前は、曲の色と止まっているときの色を光の量のまま混ぜたので、色が遠いと途中が灰色になり、暗くなった
    /// </summary>
    public static void TestGpuStaysBrightWhenTheSoundStops()
    {
        foreach (double len in new[] { 4.0, 5.5, 7.0, 8.5 })
        {
            var g = new SignalGen(48000, len + 9, seed: 63);
            var r = new Random(63);
            g.DenseMix(124, 0.5, len);
            Explosion(g, r, len + 0.5, 0.9);
            double stop = len + 3.5;
            for (int i = (int)(stop * g.Fs); i < g.Buf.Length; i++) g.Buf[i] = 0;
            var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.Ensemble, "gpu", true), (MusicStyles.Spectrum, "gpu", true) });
            foreach (var L in run.Lanes)
            {
                var fr = Frames(L, stop, stop + 5.5).ToList();
                float low = fr.Select(i => L.MinLed[i]).DefaultIfEmpty(0).Min();
                T.Info($"music {len:F1} s {L.Key,-22} lowest LED after the sound stops {low:F2}");
                T.Check(low >= 0.9f, $"music {len:F1} s {L.Key}: the graphics card does not dim while it fades to the idle look ({low:F2})");
            }
        }
    }

    /// <summary>
    /// グラボに直接書き込む（1 秒に約 10 回）スペクトラム: テンポをつかむ前の短いジングル（120 BPM のキック 7 回）でも、
    /// キックの光は 0.06 秒以内に届く（上がりの 8 割に届く時刻。キックの 80% 以上）。送る回数は増えない。
    /// 直す前は、キックのコマですぐ送ったので、帯の明るさが上がる前のコマが届き、上がったコマは 0.1 秒後の定期便まで遅れた
    /// </summary>
    public static void TestSpectrumDirectGpuSendsTheRisenFrameOnKicks()
    {
        var g = new SignalGen(48000, 16, seed: 64);
        var r = new Random(64);
        Dialogue(g, r, 0.5, 8, 0.12);
        var kicks = Enumerable.Range(0, 7).Select(k => 8.5 + 0.5 * k).ToList();
        foreach (double t in kicks)
        {
            g.Kick(t, 0.8);
            g.Tone(55, 0.2, t + 0.01, t + 0.3);
            g.Strum(t, new[] { 220.0, 277.2, 329.6 }, amp: 0.05);
        }
        Dialogue(g, r, 12.2, 16, 0.12);
        var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.Spectrum, "direct", true), (MusicStyles.Spectrum, "direct", false) });
        double locked = Enumerable.Range((int)(8.5 * 60), 3 * 60).Count(i => run.Lock[i] != BeatLock.Unlocked) / 180.0;
        T.Info($"jingle locked {locked:P0}");
        foreach (var L in run.Lanes)
        {
            int inTime = 0;
            var late = new List<string>();
            foreach (double tk in kicks.Skip(1))
            {
                double lo = Frames(L, tk - 0.1, tk).Select(i => (double)L.A[i][0]).DefaultIfEmpty(0).Min();
                double hi = Frames(L, tk, tk + 0.25).Select(i => (double)L.A[i][0]).DefaultIfEmpty(0).Max();
                int at = Frames(L, tk, tk + 0.25).FirstOrDefault(i => L.A[i][0] >= lo + 0.8 * (hi - lo), -1);
                double dt = at < 0 ? double.PositiveInfinity : at / 60.0 - tk;
                if (dt <= 0.06) inTime++;
                else late.Add($"{tk:F1}+{dt * 1000:F0}ms");
            }
            int n = kicks.Count - 1;
            double rate = L.SendT.Count(t => t >= 8.5 && t < 12) / 3.5, cap = 1 / L.Ch.Interval + 2;
            T.Info($"{L.Key,-24} kicks in time {inTime}/{n} {string.Join(" ", late)}, writes {rate:F1}/s");
            T.Check(inTime >= 0.8 * n, $"{L.Key}: the risen kick frame is sent within 0.06 s ({inTime}/{n})");
            T.Check(rate <= cap, $"{L.Key}: it does not send more often ({rate:F1}/s, at most {cap:F1}/s)");
        }
    }

    /// <summary>
    /// 小さいゾーンの波紋: テンポをつかんだ 100・128 BPM の曲（キックは 1・3 拍目、2・4 拍目はスネアだけ。拍ごとにベース）で、
    /// キックの拍は明るく光り（いちばん明るい値の平均が「控えめ」0.85 以上・「ふつう」0.95 以上）、スネアだけの拍は光らない（0.3 以下）。
    /// 6.15.10 までは、予測した拍ごとに光るので、スネアだけの拍もキックの拍と同じくらい光っていた
    /// </summary>
    public static void TestSmallZoneRippleSubtleBeatsAreBright()
    {
        foreach (double bpm in new[] { 100.0, 128.0 })
        foreach (var intensity in new[] { Intensities.Subtle, Intensities.Moderate })
        {
            var g = new SignalGen(48000, 20, seed: 66);
            g.Loop(bpm, 0.5, 20, hats: true, snare: true);
            g.BassLine(0.5, 20, bpm / 60, new[] { 55.0, 65.4, 73.4, 82.4 }, amp: 0.2);
            g.PadChord(0.5, 20, new[] { 220.0, 277.2, 329.6 }, amp: 0.04);
            var kicks = new List<double>();
            var snares = new List<double>();
            int k = 0;
            for (double t = 0.5; t < 20; t += 60 / bpm, k++) (k % 2 == 0 ? kicks : snares).Add(t);
            var run = Play(g.Buf, g.Fs, new[] { (MusicStyles.Ripple, "zone4", true), (MusicStyles.Ripple, "led1", true) }, intensity);
            foreach (var L in run.Lanes)
            {
                double Peak(double t) => Frames(L, t - 0.02, t + 0.2).Select(i => (double)L.A[i][0]).DefaultIfEmpty(0).Max();
                double kick = kicks.Where(t => t > 8 && t < 19).Average(Peak), snare = snares.Where(t => t > 8 && t < 19).Average(Peak);
                double need = intensity == Intensities.Subtle ? 0.85 : 0.95;
                T.Info($"{bpm:F0} BPM {intensity,-9} {L.Key,-22} kick beats {kick:F2}, snare-only beats {snare:F2}");
                T.Check(kick >= need, $"{bpm:F0} BPM {intensity} {L.Key}: the kick beats light the small zone brightly ({kick:F2}, need {need:F2})");
                T.Check(snare <= 0.3, $"{bpm:F0} BPM {intensity} {L.Key}: beats with only a snare stay dark ({snare:F2})");
            }
        }
    }

    /// <summary>
    /// 映画・ゲームの音の波紋: せりふ（2〜20 秒）・足音（21〜31 秒。0.4〜0.6 秒おき）では輪がほとんど出ず（1 分に 5 個まで）、
    /// 銃声（33〜45 秒。0 dB と −6 dB を 2 秒おき）では、どの 1 発にも輪が出る（銃声の 0.05 秒前〜0.15 秒後）。
    /// 6.15.10 は、テンポをつかんでいないとき、キックの出来事（話し声の音節・足音も含む）ごとに輪を出していたので、
    /// せりふ・足音で 1 分に 60〜110 個の輪が出ていた
    /// </summary>
    public static void TestRippleRingsOnBigSoundsNotOnTalk()
    {
        var g = new SignalGen(48000, 47, seed: 71);
        var r = new Random(71);
        g.Pink(-58, 0, 47);
        Dialogue(g, r, 2, 20, 0.12);
        g.Pink(-50, 21, 31);
        for (double t = 21.2; t < 30.8; t += 0.4 + 0.2 * r.NextDouble()) Footstep(g, r, t, 0.12 + 0.06 * r.NextDouble());
        var shots = new List<double>();
        for (int k = 0; k < 6; k++)
        {
            double t = 33 + 2 * k;
            Gunshot(g, r, t, 0.9 * Math.Pow(10, (k % 2 == 0 ? 0 : -6) / 20.0));
            shots.Add(t);
        }
        var run = Play(g.Buf, g.Fs, All(new[] { "strip24", "zone4" }, new[] { MusicStyles.Ripple }));
        foreach (var L in run.Lanes)
        {
            var rings = new List<double>();
            for (int i = 1; i < L.Rings.Count; i++) if (L.Rings[i] > L.Rings[i - 1]) rings.Add(L.RingT0[i]);
            double PerMin(double a, double b) => rings.Count(t => t >= a && t < b) / ((b - a) / 60);
            double talk = PerMin(2.5, 20), steps = PerMin(21, 31);
            int hit = shots.Count(s => rings.Any(t => t >= s - 0.05 && t <= s + 0.15));
            T.Info($"{L.Key,-24} rings: dialogue {talk:F1}/min, footsteps {steps:F1}/min, shots {hit}/{shots.Count} ({(L.Ch.Renderer.CurrentEffect is RippleEffect fx ? $"fast {fx.FastRings} slow {fx.SlowRings} early {fx.PatternRings} big {fx.BigRings}" : "")})");
            T.Check(talk <= 5, $"{L.Key}: dialogue does not make rings ({talk:F1}/min)");
            T.Check(steps <= 5, $"{L.Key}: footsteps do not make rings ({steps:F1}/min)");
            T.Check(hit == shots.Count, $"{L.Key}: every gunshot makes a ring ({hit}/{shots.Count})");
        }
    }
}

// ==================================================
// SparkleGuitarTests.cs - 光り方「きらめき」が、ギターの多い曲でもキックを取りこぼさず、ギターの音の出だしで色の帯が走ることを確かめる
//   （「多くの曲によく反応するが、ギターにはほとんど反応しない」という声のため。合成した曲。録音がなくても動く）
//   アプリと同じ流れ（本物の分析に 10 ミリ秒ずつ流し、60 コマ／秒で描き、RgbDeviceChannel の送る・送らない・送ってから光るまでの遅れまで通す）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class SparkleGuitarTests
{
    private const double Tick = 1.0 / 60, T0 = 1000;
    /// <summary>テンポをつかむまでの時間は数えない（秒）</summary>
    private const double Skip = 6;

    private sealed class Lane
    {
        public required string Dev;
        public required RgbDeviceChannel Ch;
        public required double LatSec;
        public Rgb[] Seen = Array.Empty<Rgb>();
        /// <summary>コマごと・LED ごとの見た目の明るさ（0〜1）</summary>
        public readonly List<float[]> P = new();

        /// <summary>音の時刻 t に見えているコマの番号</summary>
        public int Idx(double t) => (int)Math.Round((t - LatSec) * 60);
    }

    private sealed class Run
    {
        public readonly List<AudioEvent> Events = new();
        /// <summary>ギターの光を出した時刻（曲の頭からの秒）</summary>
        public readonly List<double> Sweeps = new();
        public readonly List<Lane> Lanes = new();
        public double Sec;
    }

    private static RgbDevice DeviceOf(string name) => name switch
    {
        "strip" => new RgbDevice(0, "Strip", 24, 4, null, "HID: x"),
        "zone4" => new RgbDevice(1, "Small", 4, 4, null, "HID: x"),
        _ => throw new ArgumentException(name),
    };

    /// <summary>曲を流して、きらめきをデバイスごとに描く</summary>
    private static Run Play(float[] buf, string[] devs, bool rf = false)
    {
        var run = new Run { Sec = buf.Length / (double)GuitarMixes.Fs };
        var baseCfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyIntensity(baseCfg.Music, Intensities.Moderate);
        var engine = new AudioEngine(GuitarMixes.Fs) { Settings = MusicTuning.EngineSettings(baseCfg) };
        foreach (var dn in devs)
        {
            var dev = DeviceOf(dn);
            var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = MusicStyles.Sparkle, Brightness = 100 };
            MusicTuning.ApplyIntensity(cfg.Music, Intensities.Moderate);
            cfg.Music.ReduceFlashing = rf;
            var ch = new RgbDeviceChannel(dev, T0) { SharedColor = new SharedColor() };
            ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = dev.Name }, false);
            run.Lanes.Add(new Lane { Dev = dn, Ch = ch, LatSec = ch.EffectiveLatencyMs / 1000, Seen = new Rgb[dev.LedCount] });
        }
        SparkleEffect.DiagGuitar = (t, _) => run.Sweeps.Add(t - T0);
        try
        {
            long cursor = 0;
            var evs = new List<AudioEvent>();
            int pos = 0, chunk = GuitarMixes.Fs / 100;
            for (int k = 0; ; k++)
            {
                double sec = k * Tick;
                if (sec + 0.15 > run.Sec) break;
                while (pos < buf.Length && pos / (double)GuitarMixes.Fs < sec + 0.1)
                {
                    int m = Math.Min(chunk, buf.Length - pos);
                    engine.Push(new ReadOnlySpan<float>(buf, pos, m), T0 + pos / (double)GuitarMixes.Fs);
                    pos += m;
                }
                double t = T0 + sec;
                evs.Clear();
                engine.Events.Read(ref cursor, t, evs);
                foreach (var e in evs) run.Events.Add(e with { T = e.T - T0 });
                foreach (var L in run.Lanes)
                {
                    var ch = L.Ch;
                    ch.Prepare(t, Tick, engine.Features, engine.Events, engine.Beat, null, false, false);
                    if (ch.Render())
                    {
                        var reason = ch.ShouldSend(t, Tick, true);
                        if (reason != SendReason.None)
                        {
                            ch.MarkSent(t, Tick, reason, 1);
                            Array.Copy(ch.Frame, L.Seen, L.Seen.Length);
                        }
                    }
                    var pv = new float[L.Seen.Length];
                    for (int j = 0; j < pv.Length; j++) { var q = L.Seen[j]; pv[j] = (float)Math.Pow(Math.Max(q.R, Math.Max(q.G, q.B)) / 255.0, 1 / 2.2); }
                    L.P.Add(pv);
                }
            }
        }
        finally { SparkleEffect.DiagGuitar = null; }
        // 同じ時刻の光は 1 回に数える（デバイスごとに描くので、同じ光が何回も届く）
        var uniq = run.Sweeps.Distinct().OrderBy(x => x).ToList();
        run.Sweeps.Clear();
        run.Sweeps.AddRange(uniq);
        return run;
    }

    /// <summary>本物のキックのうち、40 ミリ秒以内にキックの出来事が出た割合と、本物のキックから離れたキックの数</summary>
    private static (double Recall, int Fake) Kicks(GuitarSong s, List<AudioEvent> ev)
    {
        double end = s.Sec - 1;
        var ks = s.Kicks.Where(t => t >= Skip && t < end).ToList();
        var kev = ev.Where(e => e.Kind == AudioEventKind.Kick && e.T >= Skip && e.T < end).ToList();
        int hit = ks.Count(t => kev.Any(e => Math.Abs(e.T - t) <= 0.04));
        int fake = kev.Count(e => !ks.Any(t => Math.Abs(e.T - t) <= 0.04));
        return (ks.Count == 0 ? double.NaN : hit / (double)ks.Count, fake);
    }

    /// <summary>ギターだけの音（ドラムから 80 ミリ秒以上離れた音）のうち、ギターの光が出た割合と、出るまでの時間の中央値（ミリ秒）</summary>
    private static (double Share, double LatMs, int N) GuitarSeen(GuitarSong s, Run r)
    {
        var g = s.GuitarOnly(Skip, s.Sec - 1);
        var lat = new List<double>();
        foreach (double h in g)
        {
            var sw = r.Sweeps.Where(x => x >= h - 0.03 && x <= h + 0.06).ToList();
            if (sw.Count > 0) lat.Add(1000 * (sw[0] - h));
        }
        lat.Sort();
        return (g.Count == 0 ? double.NaN : lat.Count / (double)g.Count, lat.Count == 0 ? double.NaN : lat[lat.Count / 2], g.Count);
    }

    private static double SweepRate(Run r) => r.Sweeps.Count(x => x >= Skip && x < r.Sec - 1) / (r.Sec - 1 - Skip);

    /// <summary>粒の光り始め（1 秒あたり）: LED が 0.1 秒以内に 0.15 以上明るくなり、ゾーンの平均はそれより 0.1 以上小さくしか上がっていない</summary>
    private static double ParticleStarts(Lane l, double from, double to)
    {
        int a = Math.Max(6, l.Idx(from)), b = Math.Min(l.P.Count - 1, l.Idx(to));
        int n = l.P[0].Length, c = 0;
        var m = l.P.Select(x => (double)x.Average()).ToArray();
        for (int j = 0; j < n; j++)
        {
            int last = -1000;
            for (int i = a; i <= b; i++)
            {
                double lo = 9, mlo = 9;
                for (int q = i - 6; q < i; q++) { lo = Math.Min(lo, l.P[q][j]); mlo = Math.Min(mlo, m[q]); }
                double rise = l.P[i][j] - lo;
                if (rise >= 0.15 && rise - (m[i] - mlo) >= 0.1 && i - last >= 6) { c++; last = i; }
            }
        }
        return c / (to - from);
    }

    /// <summary>ゾーン全体の光り始め（見た目の明るさの平均が 0.1 秒以内に rise 以上上がった始まり）の時刻</summary>
    private static List<double> ZoneFlashes(Lane l, double from, double to, double rise = 0.12)
    {
        int a = Math.Max(6, l.Idx(from)), b = Math.Min(l.P.Count - 1, l.Idx(to));
        var m = l.P.Select(x => (double)x.Average()).ToArray();
        var res = new List<double>();
        int last = -1000; bool up0 = false;
        for (int i = a; i <= b; i++)
        {
            double lo = 9;
            for (int q = i - 6; q < i; q++) lo = Math.Min(lo, m[q]);
            bool up = m[i] - lo >= rise;
            if (up && !up0 && i - last >= 6) { res.Add(i / 60.0); last = i; }
            up0 = up;
        }
        return res;
    }

    /// <summary>見た目の明るさの平均（ゾーンの LED とコマの平均）</summary>
    private static double MeanP(Lane l, double from, double to)
    {
        int a = Math.Max(0, l.Idx(from)), b = Math.Min(l.P.Count - 1, l.Idx(to));
        double s = 0;
        for (int i = a; i <= b; i++) s += l.P[i].Average();
        return b >= a ? s / (b - a + 1) : 0;
    }

    private static readonly Lazy<GuitarSong> Rock = new(GuitarMixes.Rock), Dense = new(GuitarMixes.RockDense),
        Arpeggio = new(GuitarMixes.Arpeggio), Alone = new(GuitarMixes.GuitarAlone);
    private static readonly Lazy<Run> RockRun = new(() => Play(Rock.Value.With, new[] { "strip", "zone4" })),
        DenseRun = new(() => Play(Dense.Value.With, new[] { "strip", "zone4" }));

    /// <summary>
    /// 打楽器らしさ: 音の多い曲（1〜5 kHz がキックの包絡線から 20 dB 以内）では、目立ちが 6.5 dB でもキックにする。
    /// ベースの音（1〜5 kHz がずっと小さい）は今までどおり
    /// </summary>
    public static void TestDenseMixKickPercussiveness()
    {
        double dense = OnsetDetector.Percussiveness(1.0, -12, 6.5);
        double bass = OnsetDetector.Percussiveness(1.0, -30, 5.5);
        double bassProm = OnsetDetector.Percussiveness(1.0, -30, 6.5);
        T.Check(dense >= 0.5, $"dense mix: prominence 6.5 dB with 1-5 kHz 12 dB below the kick counts as a kick ({dense:F2} >= 0.5)");
        T.Check(bass == 0, $"bass note (1-5 kHz 30 dB below): prominence 5.5 dB stays 0 ({bass:F2})");
        T.Check(bassProm < 0.5, $"bass note (1-5 kHz 30 dB below): prominence 6.5 dB stays below the gate ({bassProm:F2})");
        double old = OnsetDetector.KickTune.DenseRelDb;
        try
        {
            OnsetDetector.KickTune.DenseRelDb = 0;
            double off = OnsetDetector.Percussiveness(1.0, -12, 6.5);
            T.Check(off < 0.5, $"without the dense-mix rule the same cue is not a kick ({off:F2} < 0.5)");
        }
        finally { OnsetDetector.KickTune.DenseRelDb = old; }
    }

    /// <summary>
    /// ひずんだギターを 8 分で弾き続ける曲でも、本物のキックの 9 割以上がキックの出来事になる
    /// （今までは「カチッ」がギターに埋もれて、キックの 5〜8 割をベースの音にしていた。きらめきの「キックで全体がふわっと」が消えていた）
    /// </summary>
    public static void TestKickRecallUnderGuitarWall()
    {
        foreach (var (s, r) in new[] { (Rock.Value, RockRun.Value), (Dense.Value, DenseRun.Value) })
        {
            var (rec, fake) = Kicks(s, r.Events);
            double old = OnsetDetector.KickTune.DenseRelDb;
            (double Recall, int Fake) off;
            try
            {
                OnsetDetector.KickTune.DenseRelDb = 0;
                var engine = new AudioEngine(GuitarMixes.Fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
                var er = new EngineRun(engine, T0).Feed(s.With);
                off = Kicks(s, er.Events.Select(e => e with { T = e.T - T0 }).ToList());
            }
            finally { OnsetDetector.KickTune.DenseRelDb = old; }
            T.Info($"{s.Name}: kick recall {rec * 100:F0}% fake {fake} (without the dense-mix rule {off.Recall * 100:F0}% fake {off.Fake})");
            T.Check(rec >= 0.9, $"{s.Name}: at least 90% of the kicks are found under the guitar wall ({rec * 100:F0}%)");
            T.Check(fake <= off.Fake + 2, $"{s.Name}: no new false kicks ({fake} vs {off.Fake})");
            T.Check(off.Recall < rec - 0.2, $"{s.Name}: the dense-mix rule is what finds them ({off.Recall * 100:F0}% without it)");
        }
    }

    /// <summary>ベースとひずんだギターだけ（ドラムなし）では、キックが増えない（ベースの音をキックにしない）</summary>
    public static void TestBassAndGuitarWithoutDrumsAddNoKicks()
    {
        foreach (bool wall in new[] { true, false })
        {
            var buf = GuitarMixes.BassAndGuitar(wall);
            double Rate()
            {
                var engine = new AudioEngine(GuitarMixes.Fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music }) };
                var er = new EngineRun(engine, T0).Feed(buf);
                return er.Events.Count(e => e.Kind == AudioEventKind.Kick && e.T - T0 >= 2) / 14.0;
            }
            double on = Rate(), old = OnsetDetector.KickTune.DenseRelDb, off;
            try { OnsetDetector.KickTune.DenseRelDb = 0; off = Rate(); }
            finally { OnsetDetector.KickTune.DenseRelDb = old; }
            T.Check(on <= off + 0.1, $"bass + distorted guitar ({(wall ? "8th wall" : "whole notes")}), no drums: kicks {on:F2}/s (without the dense-mix rule {off:F2}/s)");
        }
    }

    /// <summary>
    /// ギターだけの音（ドラムから離れた音）の半分以上で、ギターの光（色の帯）が 15 ミリ秒以内に出る。
    /// テープの粒の光り始めも、Tonal の出来事を出さないときより増える
    /// </summary>
    public static void TestGuitarOnsetsLight()
    {
        foreach (var s in new[] { Arpeggio.Value, Alone.Value, Rock.Value })
        {
            var r = ReferenceEquals(s, Rock.Value) ? RockRun.Value : Play(s.With, new[] { "strip" });
            var (share, lat, n) = GuitarSeen(s, r);
            Run off;
            bool old = OnsetDetector.EventTune.Tonal;
            try { OnsetDetector.EventTune.Tonal = false; off = Play(s.With, new[] { "strip" }); }
            finally { OnsetDetector.EventTune.Tonal = old; }
            double end = s.Sec - 1;
            double pst = ParticleStarts(r.Lanes[0], Skip, end), pstOff = ParticleStarts(off.Lanes[0], Skip, end);
            T.Info($"{s.Name}: guitar-only onsets {n}, lit {share * 100:F0}% (median {lat:F0} ms), sweeps {SweepRate(r):F2}/s | strip particle starts {pst:F1}/s (no Tonal {pstOff:F1}/s)");
            T.Check(share >= 0.5, $"{s.Name}: at least half of the guitar-only onsets get a guitar light ({share * 100:F0}%)");
            T.Check(lat <= 15, $"{s.Name}: the guitar light starts within 15 ms of the onset ({lat:F0} ms)");
            T.Check(off.Sweeps.Count == 0, $"{s.Name}: no guitar light without the Tonal events ({off.Sweeps.Count})");
            T.Check(pst >= pstOff * 1.05, $"{s.Name}: the strip shows more particle starts with the guitar light ({pst:F1}/s vs {pstOff:F1}/s)");
        }
    }

    /// <summary>ギターの音がない曲・せりふ・映画では、ギターの光がほとんど出ない（1 秒に 0.1 回まで。映画は 0）</summary>
    public static void TestNoGuitarLightWithoutGuitar()
    {
        foreach (var (name, buf, max) in new (string, Func<float[]>, double)[]
        {
            ("hiphop", () => MusicMixes.HipHop().Buf, 0.1), ("edm", () => MusicMixes.Edm().Buf, 0.1),
            ("dialogue", GuitarMixes.Dialogue, 0.1), ("film", GuitarMixes.Film, 0),
        })
        {
            var r = Play(buf(), new[] { "strip" });
            double rate = SweepRate(r);
            T.Check(rate <= max, $"{name}: guitar lights {rate:F2}/s (at most {max:F1}/s)");
        }
    }

    /// <summary>
    /// ギターの多い曲で、きらめきが暗くならない: テープの明るさの平均と、ゾーン全体の光（キック）の回数が、ギターのない同じ曲の 9 割以上。
    /// 6 個未満のゾーンも、キック・スネアの光の回数を減らさない
    /// </summary>
    public static void TestGuitarSongsNotDuller()
    {
        foreach (var (s, r) in new[] { (Rock.Value, RockRun.Value), (Dense.Value, DenseRun.Value) })
        {
            var o = Play(s.Without, new[] { "strip", "zone4" });
            double end = s.Sec - 1, dur = end - Skip;
            double mp = MeanP(r.Lanes[0], Skip, end), mpO = MeanP(o.Lanes[0], Skip, end);
            double zf = ZoneFlashes(r.Lanes[0], Skip, end).Count / dur, zfO = ZoneFlashes(o.Lanes[0], Skip, end).Count / dur;
            double sz = ZoneFlashes(r.Lanes[1], Skip, end).Count / dur, szO = ZoneFlashes(o.Lanes[1], Skip, end).Count / dur;
            T.Info($"{s.Name}: strip meanP {mp:F2} (no guitar {mpO:F2}), zone flashes {zf:F2}/s ({zfO:F2}/s), zone4 flashes {sz:F2}/s ({szO:F2}/s)");
            T.Check(mp >= 0.9 * mpO, $"{s.Name}: the strip is not duller with the guitar ({mp:F2} vs {mpO:F2})");
            T.Check(zf >= 0.9 * zfO, $"{s.Name}: whole-zone kick glows are kept with the guitar ({zf:F2}/s vs {zfO:F2}/s)");
            T.Check(sz >= 0.9 * szO, $"{s.Name}: the small zone keeps its flash rate ({sz:F2}/s vs {szO:F2}/s)");
        }
    }

    /// <summary>「激しい点滅を抑える」がオンなら、6 個未満のゾーンの強い光（平均の明るさが 0.25 以上上がる）は、どの 1 秒にも 3 回まで（ギターの光が増えても）</summary>
    public static void TestSmallZoneFlashGuardWithGuitar()
    {
        foreach (var s in new[] { Rock.Value, Alone.Value })
        {
            int Worst()
            {
                var r = Play(s.With, new[] { "zone4" }, rf: true);
                var fl = ZoneFlashes(r.Lanes[0], Skip, s.Sec - 1, 0.25);
                int w = 0;
                foreach (double t in fl) w = Math.Max(w, fl.Count(x => x >= t && x < t + 1.0));
                return w;
            }
            int worst = Worst(), off;
            bool old = OnsetDetector.EventTune.Tonal;
            try { OnsetDetector.EventTune.Tonal = false; off = Worst(); }
            finally { OnsetDetector.EventTune.Tonal = old; }
            T.Info($"{s.Name}: worst second {worst} flashes (no Tonal {off})");
            T.Check(worst <= 3, $"{s.Name}: with reduce flashing, at most 3 strong small-zone flashes in any second ({worst})");
            T.Check(worst <= off, $"{s.Name}: the guitar light adds no strong small-zone flashes ({worst} vs {off} without it)");
        }
    }

    // ===== 描き方だけ（分析を通さない）

    private sealed class Manual
    {
        public readonly DeviceRenderer R;
        public readonly RenderInput In = new();
        private readonly Rgb[] _out;
        private double _lastT = double.NaN;

        public Manual(int leds, bool locked, bool reduce = false)
        {
            var dev = new RgbDevice(2, "Strip", leds, 4, null, "HID: x");
            R = new DeviceRenderer(dev);
            _out = new Rgb[leds];
            In.Cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = false };
            In.Music = ResolvedMusic.Default with { Effect = MusicStyles.Sparkle, Palette = Palettes.Neon, MinBrightness = 0, ReduceFlashing = reduce };
            In.Device = new RgbDeviceSettings { Name = dev.Name, Mirror = false };
            In.Frame.GateOpen = true;
            In.Frame.Activity = 1;
            In.Frame.MidE = 0;
            In.Frame.RmsDb = -20;
            if (locked) In.Beat = new BeatState(0.5, 1.0, 0, 0.95, true, 0);
        }

        public void Step(double t, params AudioEvent[] events)
        {
            In.T = t;
            In.TEval = t;
            In.TPrevEval = double.IsNaN(_lastT) ? t : _lastT;
            In.Dt = double.IsNaN(_lastT) ? 0 : t - _lastT;
            In.Frame.T = t;
            In.Events.Clear();
            In.Events.AddRange(events);
            R.Render(In, _out);
            _lastT = t;
        }

        /// <summary>LED ごとの明るさ − いちばん暗い LED の明るさ（ゾーン全体が同じだけ明るい下地は数えない）</summary>
        public float[] Above()
        {
            var l = R.LastLight;
            float lo = float.MaxValue;
            for (int i = 0; i < l.Length; i++) lo = Math.Min(lo, l[i].MaxChannel);
            var a = new float[l.Length];
            for (int i = 0; i < l.Length; i++) a[i] = l[i].MaxChannel - lo;
            return a;
        }

        public float[] Levels()
        {
            var l = R.LastLight;
            var a = new float[l.Length];
            for (int i = 0; i < l.Length; i++) a[i] = l[i].MaxChannel;
            return a;
        }
    }

    private static AudioEvent Ev(AudioEventKind k, double t, float s = 2f) => new(1, k, t, s);

    /// <summary>
    /// ギターの光: テンポをつかんでいるとき、続けて並んだ LED（3 個以上）が端から順に光る（45 ミリ秒かけて）。
    /// つかんでいないときは光らない（話し声・映画の効果音で光らせない）
    /// </summary>
    public static void TestGuitarSweepShape()
    {
        var x = new Manual(60, locked: true);
        for (double t = 1.20; t < 1.30; t += Tick) x.Step(t);
        x.Step(1.30, Ev(AudioEventKind.Tonal, 1.30));
        int first = x.Above().Count(v => v > 0.03f);
        for (double t = 1.30 + Tick; t < 1.36; t += Tick) x.Step(t);
        x.Step(1.36);
        var a = x.Above();
        var lit = Enumerable.Range(0, 60).Where(i => a[i] > 0.03f).ToList();
        bool run = lit.Count >= 3 && lit.All(i => lit.Contains((i + 1) % 60) || lit.Contains((i + 59) % 60));
        T.Check(first >= 1 && first < lit.Count, $"the guitar light runs along the strip ({first} LEDs at the onset, {lit.Count} after 60 ms)");
        T.Check(run, $"the guitar light is a run of adjacent LEDs ({string.Join(",", lit)})");
        x.Step(2.5);
        T.Check(x.Above().Max() < 0.03f, "the guitar light fades out");

        var y = new Manual(60, locked: false);
        for (double t = 1.20; t < 1.30; t += Tick) y.Step(t);
        y.Step(1.30, Ev(AudioEventKind.Tonal, 1.30));
        y.Step(1.36);
        T.Check(y.Above().Max() < 0.03f, "no guitar light while the tempo is not locked");
    }

    /// <summary>
    /// ギターの光は、ハイハット・スネアの粒を消さず、次の粒も止めない（粒とは別に覚える）。
    /// 試作では同じ入れ物を使い、6 個未満のゾーンの光が 1 秒あたり 3.77 → 2.86 回に減った
    /// </summary>
    public static void TestGuitarLightKeepsDrumParticles()
    {
        int bad = 0, trials = 30;
        for (int k = 0; k < trials; k++)
        {
            var x = new Manual(60, locked: true);
            // 乱数の進み方を試行ごとに変える（光る場所を変える）
            for (int i = 0; i < k; i++) x.Step(0.2 + 0.2 * i, Ev(AudioEventKind.Hat, 0.2 + 0.2 * i, 1.2f));
            double t0 = 0.6 + 0.2 * k;
            x.Step(t0 - Tick);
            x.Step(t0, Ev(AudioEventKind.Tonal, t0));
            for (double t = t0 + Tick; t < t0 + 0.1; t += Tick) x.Step(t);
            x.Step(t0 + 0.1);
            var before = x.Levels();
            x.Step(t0 + 0.1 + 0.001, Ev(AudioEventKind.Snare, t0 + 0.1005));
            var after = x.Levels();
            int rose = Enumerable.Range(0, 60).Count(i => after[i] - before[i] > 0.05f);
            // スネア（強さ 2）は round(0.12 × 60) = 7 個の LED。ギターの光と重なっても全部光る
            if (rose != 7) bad++;
        }
        T.Check(bad == 0, $"a snare right after a guitar light always lights its 7 LEDs ({bad} of {trials} trials did not)");

        // 先に光った粒は、ギターの光で暗くならない（足し合わせるだけ）
        var a = new Manual(60, locked: true);
        var b = new Manual(60, locked: true);
        foreach (var m in new[] { a, b }) { for (double t = 1.20; t < 1.30; t += Tick) m.Step(t); }
        a.Step(1.30, Ev(AudioEventKind.Hat, 1.30), Ev(AudioEventKind.Snare, 1.30), Ev(AudioEventKind.Tonal, 1.30));
        b.Step(1.30, Ev(AudioEventKind.Hat, 1.30), Ev(AudioEventKind.Snare, 1.30));
        a.Step(1.34); b.Step(1.34);
        var la = a.R.LastLight.ToArray(); var lb = b.R.LastLight.ToArray();
        int dimmer = Enumerable.Range(0, 60).Count(i => la[i].R < lb[i].R - 1e-4f || la[i].G < lb[i].G - 1e-4f || la[i].B < lb[i].B - 1e-4f);
        T.Check(dimmer == 0, $"no LED gets dimmer when a guitar light is added on top of hat/snare particles ({dimmer} LEDs)");
    }

    /// <summary>6 個未満のゾーン: ギターでゾーン全体が控えめに光る。「激しい点滅を抑える」がオンなら、詰めて鳴った 2 回目は 0.35 倍</summary>
    public static void TestSmallZoneGuitarTint()
    {
        float Rise(bool reduce, out float second)
        {
            var x = new Manual(4, locked: true, reduce);
            for (double t = 1.20; t < 1.30; t += Tick) x.Step(t);
            var b0 = x.Levels().Average();
            x.Step(1.30, Ev(AudioEventKind.Tonal, 1.30));
            float r1 = x.Levels().Average() - b0;
            for (double t = 1.30 + Tick; t < 1.45; t += Tick) x.Step(t);
            var b1 = x.Levels().Average();
            x.Step(1.45, Ev(AudioEventKind.Tonal, 1.45));
            second = x.Levels().Average() - b1;
            return r1;
        }
        float on = Rise(false, out float on2), rf = Rise(true, out float rf2);
        T.Check(on > 0.03f && on < 0.4f, $"small zone: a guitar onset tints the whole zone softly (+{on:F2})");
        T.Check(rf2 < 0.6f * on2, $"small zone with reduce flashing: a guitar onset 0.15 s after the last one is dimmed ({rf2:F2} vs {on2:F2})");
        T.Check(Math.Abs(rf - on) < 0.01f, $"small zone with reduce flashing: the first guitar onset is not dimmed ({rf:F2} vs {on:F2})");
    }
}

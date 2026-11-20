// ==================================================
// RoleIdentityTests.cs - 光り方「パーツごとの役割」で、どのゾーンがどの役割か見て分かり、グラボの光が遅れないことを確かめる
//   （「パーツごとに光り方は違うが、どれがどれか分かりにくい。特にグラボの役割が音楽に合っていない（遅れている？）」という声のため）
//   合成した曲（MusicMixes。ドラム・ベース・パッド・声を重ね、キックでダッキングしてバスのコンプレッサーをかけたもの）を
//   アプリと同じ流れ（本物の分析に 10 ミリ秒ずつ流し、60 コマ／秒で描き、RgbDeviceChannel の送る・送らない・送ってから光るまでの遅れ）で見る
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class RoleIdentityTests
{
    private const double Tick = 1.0 / 60, T0 = 1000;
    /// <summary>テンポをつかむまでの時間は数えない（秒）</summary>
    private const double Skip = 8;

    private sealed record Zone(string Name, int Start, int Count);

    private sealed class Lane
    {
        public required string Dev;
        public required RgbDeviceChannel Ch;
        public required Zone[] Zones;
        public readonly Queue<(double At, Rgb[] V)> Pending = new();
        public Rgb[] Seen = Array.Empty<Rgb>();
        /// <summary>コマごとに見えている色（送ってから光るまでの遅れのあと）</summary>
        public readonly List<Rgb[]> Hist = new();
        public int Sends;

        public Zone Z(string name) => Zones.First(z => z.Name == name);

        /// <summary>ゾーンの明るさ（いちばん強い色の成分の平均。0〜1）のコマごとの並び</summary>
        public double[] Level(Zone z) => Hist.Select(h =>
        {
            double a = 0;
            for (int i = z.Start; i < z.Start + z.Count; i++) a += Math.Max(h[i].R, Math.Max(h[i].G, h[i].B));
            return a / z.Count / 255;
        }).ToArray();

        /// <summary>ゾーンの平均の色の色相（度）。暗い・灰色に近いコマは NaN</summary>
        public double[] Hue(Zone z) => Hist.Select(h =>
        {
            double r = 0, g = 0, b = 0;
            for (int i = z.Start; i < z.Start + z.Count; i++) { r += h[i].R; g += h[i].G; b += h[i].B; }
            double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            if (mx < 25 * z.Count || mx - mn < 0.15 * mx) return double.NaN;
            double d = mx - mn, hh = mx == r ? 60 * (((g - b) / d) % 6) : mx == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
            return (hh + 360) % 360;
        }).ToArray();
    }

    private static RgbDevice DeviceOf(string name) => name switch
    {
        // ASRock のマザーボード: 12V の端子・ARGB の端子 3 つ・PCB → 低音・低音・中音・高音・中音（MusicSounds.Spread）
        "board" => new RgbDevice(0, "ASRock B860", 34, RgbDevice.TypeMotherboard, new[] { 1, 8, 8, 8, 9 }, "HID: t")
        {
            ZoneNames = new[] { "RGB LED 1 Header", "Addressable Header 1", "Addressable Header 2", "Addressable Header 3", "PCB" },
        },
        // 直接書き込むグラボ（ロゴ 4・前面 20・矢印 17）→ 中音・高音・低音
        "direct" => new RgbDevice(1, "GpuDirect", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }) { DirectGpu = true },
        // OpenRGB を通すグラボ（明るさは一定で、色だけ変わる）
        "gpu" => new RgbDevice(2, "Gpu", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }),
        _ => throw new ArgumentException(name),
    };

    private static Zone[] ZonesOf(string name) => name switch
    {
        "board" => new[] { new Zone("bass", 1, 8), new Zone("mid", 9, 8), new Zone("treble", 17, 8), new Zone("pcb", 25, 9) },
        _ => new[] { new Zone("mid", 0, 4), new Zone("treble", 4, 20), new Zone("bass", 24, 17) },
    };

    private static List<Lane> Play(MixSong s, string[] devs, bool rf)
    {
        var baseCfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyIntensity(baseCfg.Music, Intensities.Moderate);
        var engine = new AudioEngine(MusicMixes.Fs) { Settings = MusicTuning.EngineSettings(baseCfg) };
        var lanes = new List<Lane>();
        var shared = new SharedColor();
        foreach (var dn in devs)
        {
            var dev = DeviceOf(dn);
            var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = MusicStyles.Role, Brightness = 100 };
            MusicTuning.ApplyIntensity(cfg.Music, Intensities.Moderate);
            cfg.Music.ReduceFlashing = rf;
            cfg.Music.Palette = Palettes.Rainbow;
            var ch = new RgbDeviceChannel(dev, T0) { SharedColor = shared };
            ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = dev.Name }, false);
            lanes.Add(new Lane { Dev = dn, Ch = ch, Zones = ZonesOf(dn), Seen = new Rgb[dev.LedCount] });
        }
        var buf = s.Buf;
        int pos = 0, chunk = MusicMixes.Fs / 100;
        for (int k = 0; ; k++)
        {
            double sec = k * Tick;
            if (sec + 0.15 > s.Sec) break;
            // 分析は描く時刻より 30 ミリ秒先まで進んでいる（音を受け取る間隔の分）
            while (pos < buf.Length && pos / (double)MusicMixes.Fs < sec + 0.03)
            {
                int m = Math.Min(chunk, buf.Length - pos);
                engine.Push(new ReadOnlySpan<float>(buf, pos, m), T0 + pos / (double)MusicMixes.Fs);
                pos += m;
            }
            double t = T0 + sec;
            foreach (var L in lanes)
            {
                var ch = L.Ch;
                ch.Prepare(t, Tick, engine.Features, engine.Events, engine.Beat, null, false, false);
                if (ch.Render())
                {
                    var reason = ch.ShouldSend(t, Tick, true);
                    if (reason != SendReason.None)
                    {
                        ch.MarkSent(t, Tick, reason, 1);
                        L.Pending.Enqueue((sec + ch.EffectiveLatencyMs / 1000, (Rgb[])ch.Frame.Clone()));
                        if (sec >= Skip) L.Sends++;
                    }
                }
                while (L.Pending.Count > 0 && L.Pending.Peek().At <= sec + 1e-9) L.Seen = L.Pending.Dequeue().V;
                L.Hist.Add(L.Seen);
            }
        }
        return lanes;
    }

    private static readonly Lazy<(MixSong Song, List<Lane> Lanes)> HouseRf = new(() =>
    {
        var s = MusicMixes.House();
        return (s, Play(s, new[] { "board", "direct", "gpu" }, rf: true));
    });

    /// <summary>ハウスの曲の、ドラムのある部分の拍の時刻（キック）と裏拍のハイハットの時刻</summary>
    private static (double[] Kicks, double[] Hats) Grid(MixSong s)
    {
        double p = 60 / s.Bpm;
        var drums = s.Beats.Where(b => b.Sec != "break" && b.Kick > 0 && b.T >= Skip).ToList();
        return (drums.Select(b => b.T).ToArray(), drums.Select(b => b.T + p / 2).ToArray());
    }

    /// <summary>
    /// 音の時刻 te での光の上がり方: 上がった量（te のあとの 0.2 秒（次の音の 20 ミリ秒前まで）のいちばん大きい値 − te の前 80 ミリ秒のいちばん小さい値）と、
    /// その半分まで上がるまでの時間（秒。上がらなければ NaN）
    /// </summary>
    private static (double Rise, double Lat50) Response(double[] v, double te, double next)
    {
        int a = (int)Math.Ceiling((te - 0.08) / Tick), b = (int)Math.Floor(te / Tick);
        double lo = double.PositiveInfinity;
        for (int f = Math.Max(0, a); f <= b && f < v.Length; f++) lo = Math.Min(lo, v[f]);
        int e = (int)Math.Floor(Math.Min(te + 0.2, next - 0.02) / Tick);
        double hi = double.NegativeInfinity;
        for (int f = b; f <= e && f < v.Length; f++) hi = Math.Max(hi, v[f]);
        double rise = hi - lo;
        if (!(rise > 0.02)) return (Math.Max(0, rise), double.NaN);
        double thr = lo + 0.5 * rise;
        for (int f = Math.Max(1, b); f <= e && f < v.Length; f++)
            if (v[f] >= thr)
            {
                double u = v[f] > v[f - 1] ? Math.Clamp((thr - v[f - 1]) / (v[f] - v[f - 1]), 0, 1) : 1;
                return (rise, Math.Max(0, (f - 1 + u) * Tick - te));
            }
        return (rise, double.NaN);
    }

    private static (double Rise, double Lat50) Mean(double[] v, double[] at, double[] all)
    {
        var sorted = all.OrderBy(x => x).ToArray();
        var r = at.Select(te =>
        {
            int i = Array.FindIndex(sorted, x => x > te + 1e-6);
            return Response(v, te, i < 0 ? te + 1 : sorted[i]);
        }).ToList();
        var lats = r.Select(x => x.Lat50).Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
        return (r.Average(x => x.Rise), lats.Count == 0 ? double.NaN : lats[lats.Count / 2]);
    }

    private static double Corr(double[] a, double[] b, int from)
    {
        int n = Math.Min(a.Length, b.Length) - from;
        double ma = a.Skip(from).Take(n).Average(), mb = b.Skip(from).Take(n).Average(), sab = 0, saa = 0, sbb = 0;
        for (int i = from; i < from + n; i++) { sab += (a[i] - ma) * (b[i] - mb); saa += (a[i] - ma) * (a[i] - ma); sbb += (b[i] - mb) * (b[i] - mb); }
        return sab / Math.Sqrt(Math.Max(1e-12, saa * sbb));
    }

    /// <summary>色相の散らばり（度。円の上の標準偏差）</summary>
    private static double HueSpread(double[] h, int from)
    {
        double x = 0, y = 0; int n = 0;
        for (int i = from; i < h.Length; i++)
        {
            if (double.IsNaN(h[i])) continue;
            x += Math.Cos(h[i] * Math.PI / 180); y += Math.Sin(h[i] * Math.PI / 180); n++;
        }
        if (n == 0) return double.NaN;
        double r = Math.Clamp(Math.Sqrt(x * x + y * y) / n, 1e-9, 1);
        return Math.Sqrt(-2 * Math.Log(r)) * 180 / Math.PI;
    }

    private static double HueMean(double[] h, int from)
    {
        double x = 0, y = 0;
        for (int i = from; i < h.Length; i++)
            if (!double.IsNaN(h[i])) { x += Math.Cos(h[i] * Math.PI / 180); y += Math.Sin(h[i] * Math.PI / 180); }
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    private static double HueDist(double a, double b) { double d = Math.Abs(a - b) % 360; return d > 180 ? 360 - d : d; }

    /// <summary>
    /// 役割ごとの色は、曲の間ずっと同じ（6.15.10 は拍ごとに全部の色が進み、どのゾーンも 1 曲で色相環をひと回りした。散らばり 130° くらい）。
    /// 低音・中音・高音の色は離れている。明るさを変えない（OpenRGB を通す）グラボだけは、今までどおり色が進む
    /// </summary>
    public static void TestRoleColoursStayWithTheRole()
    {
        var (_, lanes) = HouseRf.Value;
        int from = (int)(Skip / Tick);
        foreach (var dn in new[] { "board", "direct" })
        {
            var L = lanes.First(l => l.Dev == dn);
            var means = new Dictionary<string, double>();
            foreach (var name in new[] { "bass", "mid", "treble" })
            {
                var h = L.Hue(L.Z(name));
                double sp = HueSpread(h, from);
                means[name] = HueMean(h, from);
                T.Check(sp <= 10, $"{dn} {name}: the zone keeps its colour through the song (hue spread {sp:F1}°; before: 120-135°)");
            }
            double d1 = HueDist(means["bass"], means["mid"]), d2 = HueDist(means["mid"], means["treble"]), d3 = HueDist(means["bass"], means["treble"]);
            T.Check(Math.Min(d1, Math.Min(d2, d3)) >= 90, $"{dn}: bass / mid / treble colours are far apart ({d1:F0}° / {d2:F0}° / {d3:F0}°)");
        }
        var g = lanes.First(l => l.Dev == "gpu");
        double gs = HueSpread(g.Hue(g.Z("treble")), from);
        T.Check(gs >= 30, $"graphics card through OpenRGB (constant brightness): the colour still moves with the beat ({gs:F0}°)");
    }

    /// <summary>
    /// 速いデバイス（マザーボード）: 高音のパーツはハイハットで光り、キックでは光らない。中音・低音とも別々に動く。
    /// 6.15.10 はキックの打つ音の高音もハイハットとして光り、キックの光も 0.45 分けていたので、高音のパーツもキックで光っていた
    /// （キックでの上がり 0.45・ハイハット 0.32、低音との相関 0.41。「激しい点滅を抑える」では、ハイハットの光が 36〜41 ミリ秒遅れた）
    /// </summary>
    public static void TestRoleTrebleZoneFollowsHats()
    {
        var (s, lanes) = HouseRf.Value;
        var L = lanes.First(l => l.Dev == "board");
        var (kicks, hats) = Grid(s);
        var all = kicks.Concat(hats).ToArray();
        var tr = L.Level(L.Z("treble"));
        var bass = L.Level(L.Z("bass"));
        var trH = Mean(tr, hats, all);
        var trK = Mean(tr, kicks, all);
        var bK = Mean(bass, kicks, all);
        int from = (int)(Skip / Tick);
        double r = Corr(tr, bass, from);
        T.Info($"board treble: hats rise {trH.Rise:F2} lat50 {trH.Lat50 * 1000:F0} ms, kicks rise {trK.Rise:F2}; bass kicks rise {bK.Rise:F2} lat50 {bK.Lat50 * 1000:F0} ms; treble~bass r {r:F2}");
        T.Check(trH.Rise >= 2 * trK.Rise, $"treble zone rises more at hi-hats than at kicks ({trH.Rise:F2} / {trK.Rise:F2}; before: 0.32 / 0.37)");
        T.Check(trH.Lat50 <= 0.02, $"treble zone reaches half of the hi-hat flash within 20 ms ({trH.Lat50 * 1000:F0} ms; before: 58 ms)");
        T.Check(r < 0.2, $"treble and bass zones move separately (correlation {r:F2}; before: 0.39)");
        T.Check(bK.Rise >= 0.6 && bK.Lat50 <= 0.02, $"bass zone still flashes on every kick, on time (rise {bK.Rise:F2}, lat50 {bK.Lat50 * 1000:F0} ms)");
    }

    /// <summary>
    /// 直接書き込むグラボ（1 秒に 10 回）: 高音のパーツ（前面 20 個）のハイハットの光は定期便を待たずに送る。
    /// 6.15.10 は次の定期便まで待ったので、半分まで上がるのに 70 ミリ秒くらいかかっていた。送る回数は 1 秒に 11 回まで
    /// </summary>
    public static void TestRoleDirectGpuHatsOnTime()
    {
        var (s, lanes) = HouseRf.Value;
        var L = lanes.First(l => l.Dev == "direct");
        var (kicks, hats) = Grid(s);
        var all = kicks.Concat(hats).ToArray();
        var trH = Mean(L.Level(L.Z("treble")), hats, all);
        var bK = Mean(L.Level(L.Z("bass")), kicks, all);
        double span = s.Sec - 0.15 - Skip, rate = L.Sends / span;
        T.Info($"direct GPU: treble hats rise {trH.Rise:F2} lat50 {trH.Lat50 * 1000:F0} ms; bass kicks rise {bK.Rise:F2} lat50 {bK.Lat50 * 1000:F0} ms; {rate:F1} sends/s");
        T.Check(trH.Lat50 <= 0.05, $"direct GPU treble zone: half of the hi-hat flash within 50 ms ({trH.Lat50 * 1000:F0} ms; before: 69 ms)");
        T.Check(bK.Lat50 <= 0.025 && bK.Rise >= 0.4, $"direct GPU bass zone: kicks on time (rise {bK.Rise:F2}, lat50 {bK.Lat50 * 1000:F0} ms)");
        T.Check(rate <= 11, $"direct GPU: at most 11 sends per second ({rate:F1})");
    }

    // ===== 出来事を直接渡す（分析を通さない）

    private sealed class Run
    {
        public readonly DeviceRenderer R;
        public readonly RenderInput In = new();
        private readonly Rgb[] _out;
        private double _last = double.NaN;

        public Run(RgbDevice dev)
        {
            R = new DeviceRenderer(dev);
            _out = new Rgb[dev.LedCount];
            In.Cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = false };
            In.Music = ResolvedMusic.Default with { Effect = MusicStyles.Role, Palette = Palettes.Rainbow, MinBrightness = 0, TempoMode = TempoModes.Off };
            In.Device = new RgbDeviceSettings { Name = dev.Name };
            In.Frame.GateOpen = true;
            In.Frame.Activity = 1;
        }

        public void Step(double t, params AudioEvent[] events)
        {
            In.T = In.TEval = In.Frame.T = t;
            In.TPrevEval = double.IsNaN(_last) ? t : _last;
            In.Dt = double.IsNaN(_last) ? 0 : t - _last;
            In.Events.Clear();
            In.Events.AddRange(events);
            R.Render(In, _out);
            _last = t;
        }

        public float Avg(int from, int n)
        {
            var l = R.LastLight;
            float s = 0;
            for (int i = from; i < from + n; i++) s += l[i].MaxChannel;
            return s / n;
        }
    }

    public static void TestAllRoleShowsAnIsolatedKick()
    {
        var c = new EffectContext { T = 1, Dt = 0.02f };
        var effect = new RoleEffect();
        effect.Reset(c);
        c.Events.Add(new AudioEvent(1, AudioEventKind.Kick, c.T, 2f));
        effect.Update(c);
        T.Check(effect.LevelOf(MusicRole.All) >= 0.4f, "the all-sounds zone shows an isolated impact");
    }

    public static void TestDirectRoleWaitsForRegularSendInNoise()
    {
        var c = new EffectContext { T = 1, Dt = 0.02f, SlowDevice = true, DirectWrite = true };
        var effect = new RoleEffect();
        effect.Reset(c);
        c.Events.Add(new AudioEvent(1, AudioEventKind.Hat, c.T, 2f));
        effect.Update(c);
        T.Check(!c.SendNow, "an isolated treble event in unmetered sound uses the regular GPU update");
    }

    public static void TestRoleKickClickIsNotAHat()
    {
        var r = new Run(DeviceOf("board"));
        int bass = 1, treble = 17;
        for (double t = 1.0; t <= 1.5; t += Tick) r.Step(t);
        float b0 = r.Avg(bass, 8), t0 = r.Avg(treble, 8);
        r.Step(1.5 + Tick, new AudioEvent(1, AudioEventKind.Kick, 1.5, 2f), new AudioEvent(2, AudioEventKind.Hat, 1.5, 2f));
        float bK = r.Avg(bass, 8), tK = r.Avg(treble, 8);
        T.Check(bK - b0 >= 0.3f && tK - t0 < 0.2f,
            $"kick with its click: bass zone flashes ({b0:F2} → {bK:F2}), treble zone hardly moves ({t0:F2} → {tK:F2}; before: 0.81)");
        // キックのすぐあと（20 ミリ秒）に、別のコマで届いたキックの打つ音の高音
        r.Step(1.5 + 2 * Tick, new AudioEvent(3, AudioEventKind.Hat, 1.52, 2f));
        float tK2 = r.Avg(treble, 8);
        T.Check(tK2 - t0 < 0.2f, $"a hi-hat 20 ms after the kick (next frame) does not flash the treble zone ({t0:F2} → {tK2:F2})");
        for (double t = 1.5 + 3 * Tick; t < 1.75; t += Tick) r.Step(t);
        float t1 = r.Avg(treble, 8);
        r.Step(1.75 + Tick, new AudioEvent(4, AudioEventKind.Hat, 1.75, 2f));
        float tH = r.Avg(treble, 8);
        T.Check(tH - t1 >= 0.3f, $"an off-beat hi-hat without a kick flashes the treble zone ({t1:F2} → {tH:F2})");
    }
}

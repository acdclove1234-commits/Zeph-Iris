// ==================================================
// RippleKickTests.cs - 光り方「波紋」の輪が、本物のキック（バスドラム）から出ることを確かめる
//   （「テンポが合えば音楽連動に見えるが、つかむのが難しく、バスドラムが鳴っていないときにも光る」という声のため。合成した曲。録音がなくても動く）
//   アプリと同じ流れ（本物の分析に 10 ミリ秒ずつ流し、60 コマ／秒で描き、RgbDeviceChannel の送る・送らない・送ってから光るまでの遅れまで通す）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class RippleKickTests
{
    private const double Tick = 1.0 / 60, T0 = 1000;
    /// <summary>テンポをつかむまでの時間は数えない（秒）</summary>
    private const double Skip = 8;
    /// <summary>輪がキックから出たとみなすずれ（秒）</summary>
    private const double Win = 0.07;

    private sealed class Lane
    {
        public required string Dev;
        public required RgbDeviceChannel Ch;
        public required double LatSec;
        public Rgb[] Seen = Array.Empty<Rgb>();
        /// <summary>コマごとの見た目の明るさ（0〜1。テープは輪の出る真ん中の 2 つの LED、それ以外は全体の平均）</summary>
        public readonly List<double> P = new();
        /// <summary>輪の生まれた時刻（曲の頭からの秒）</summary>
        public readonly List<double> Rings = new();
        public int Spawned;
        public RippleEffect? Fx;

        /// <summary>音の時刻 t に見えている明るさ（送ってから光るまでの遅れの分、前のコマ）</summary>
        public double At(double t)
        {
            int i = (int)Math.Round((t - LatSec) * 60);
            return i < 0 || i >= P.Count ? 0 : P[i];
        }

        public double Peak(double a, double b)
        {
            double m = 0;
            for (double t = a; t <= b; t += Tick) m = Math.Max(m, At(t));
            return m;
        }

        public double Low(double a, double b)
        {
            double m = double.PositiveInfinity;
            for (double t = a; t <= b; t += Tick) m = Math.Min(m, At(t));
            return double.IsInfinity(m) ? 0 : m;
        }

        /// <summary>拍 t の光の高さ: 拍のあと 0.16 秒の明るさのいちばん大きい値 − 拍の前（0.35 拍前〜0.05 秒前）のいちばん小さい値（下地・いちばん暗い明るさは数えない）</summary>
        public double Height(double t, double period) => Math.Max(0, Peak(t - 0.04, t + 0.16) - Low(t - Math.Max(0.12, 0.35 * period), t - 0.05));

        /// <summary>見て分かる光り始め（明るさが 0.1 秒以内に 0.12 以上上がった始まり。0.1 秒以上あける）。音の時刻</summary>
        public List<double> Flashes()
        {
            var res = new List<double>();
            int last = -1000;
            bool run = false;
            for (int i = 1; i < P.Count; i++)
            {
                double lo = P[i];
                for (int j = Math.Max(0, i - 6); j < i; j++) lo = Math.Min(lo, P[j]);
                bool up = P[i] - lo >= 0.12;
                if (up && !run && i - last >= 6) { res.Add(i / 60.0 + LatSec); last = i; }
                run = up;
            }
            return res;
        }
    }

    private static RgbDevice DeviceOf(string name) => name switch
    {
        "strip" => new RgbDevice(0, "Strip", 24, 4, null, "HID: x"),
        "zone4" => new RgbDevice(1, "Small", 4, 4, null, "HID: x"),
        "direct" => new RgbDevice(3, "GpuDirect", 41, RgbDevice.TypeGpu, new[] { 4, 20, 17 }) { DirectGpu = true },
        _ => throw new ArgumentException(name),
    };

    /// <summary>
    /// 曲を流して、波紋をデバイスごとに描く。lead = 分析が描く時刻より先に進んでいる時間（秒。アプリでは 0.01 秒くらい）
    /// </summary>
    private static List<Lane> Play(MixSong s, string[] devs, string intensity = Intensities.Moderate, double lead = 0.1, bool rf = false)
    {
        var baseCfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyIntensity(baseCfg.Music, intensity);
        var engine = new AudioEngine(MusicMixes.Fs) { Settings = MusicTuning.EngineSettings(baseCfg) };
        var lanes = new List<Lane>();
        foreach (var dn in devs)
        {
            var dev = DeviceOf(dn);
            var cfg = new RgbConfig { Mode = RgbModes.Music, MusicStyle = MusicStyles.Ripple, Brightness = 100 };
            MusicTuning.ApplyIntensity(cfg.Music, intensity);
            cfg.Music.ReduceFlashing = rf;
            var ch = new RgbDeviceChannel(dev, T0) { SharedColor = new SharedColor() };
            ch.Configure(cfg, MusicTuning.Resolve(cfg), new RgbDeviceSettings { Name = dev.Name }, false);
            lanes.Add(new Lane { Dev = dn, Ch = ch, LatSec = ch.EffectiveLatencyMs / 1000, Seen = new Rgb[dev.LedCount] });
        }
        var buf = s.Buf;
        int pos = 0, chunk = MusicMixes.Fs / 100;
        for (int k = 0; ; k++)
        {
            double sec = k * Tick;
            if (sec + 0.15 > s.Sec) break;
            while (pos < buf.Length && pos / (double)MusicMixes.Fs < sec + lead)
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
                bool drawn = ch.Render();
                if (ch.Renderer.CurrentEffect is RippleEffect fx)
                {
                    L.Fx = fx;
                    if (fx.Spawned > L.Spawned) { L.Spawned = fx.Spawned; L.Rings.Add(fx.LastT0 - T0); }
                }
                if (drawn)
                {
                    var reason = ch.ShouldSend(t, Tick, true);
                    if (reason != SendReason.None)
                    {
                        ch.MarkSent(t, Tick, reason, 1);
                        Array.Copy(ch.Frame, L.Seen, L.Seen.Length);
                    }
                }
                double V(Rgb q) => Math.Pow(Math.Max(q.R, Math.Max(q.G, q.B)) / 255.0, 1 / 2.2);
                double v;
                if (L.Dev == "strip") v = Math.Max(V(L.Seen[11]), V(L.Seen[12]));
                else { v = 0; foreach (var q in L.Seen) v += V(q); v /= Math.Max(1, L.Seen.Length); }
                L.P.Add(v);
            }
        }
        return lanes;
    }

    private static readonly Lazy<MixSong> House = new(MusicMixes.House), Rock = new(MusicMixes.Rock), HipHop = new(MusicMixes.HipHop),
        Ballad = new(MusicMixes.Ballad), Edm = new(MusicMixes.Edm), Dnb = new(MusicMixes.Dnb);

    /// <summary>キックのない輪（1 分あたり）と、拍のキック（大きさ 0.3 以上）のうち輪が出た割合</summary>
    private static (double NoKick, double Recall) Score(MixSong s, Lane L)
    {
        double end = s.Sec - 1;
        bool HasKick(double t) => s.Kicks.Any(k => Math.Abs(k.T - t) <= Win);
        var rings = L.Rings.Where(r => r >= Skip && r < end).ToList();
        double noKick = rings.Count(r => !HasKick(r)) / ((end - Skip) / 60);
        var main = s.Kicks.Where(k => k.A >= 0.3 && k.T >= Skip && k.T < end).ToList();
        double recall = main.Count(k => L.Rings.Any(r => Math.Abs(r - k.T) <= Win)) / (double)Math.Max(1, main.Count);
        return (noKick, recall);
    }

    /// <summary>
    /// ロック・ヒップホップ（ハーフタイム・裏拍のキック）・ドラムンベース（テンポの追跡は 87 BPM・スネアの拍でつかむ）で、
    /// 輪はキックから出る: キックのない輪は 1 分に 10 個以下、キックのうち輪が出た割合はロック 90%・ヒップホップとドラムンベース 95% 以上。
    /// 小さいゾーンでは、キックのない拍の光の高さが、キックの拍の 0.2 倍以下。「控えめ」でもキックの 85% 以上で輪が出る。
    /// 6.15.10 は、テンポをつかむと予測した拍ごとに輪を出していたので、キックのない輪が 1 分に 64〜83 個、
    /// キックのうち輪が出たのはロック 75%・ヒップホップ 48%・ドラムンベース 15% だった（スネアの拍ばかりで光っていた）
    /// </summary>
    public static void TestRippleRingsComeFromKicks()
    {
        foreach (var (song, need) in new[] { (Rock.Value, 0.90), (HipHop.Value, 0.95), (Dnb.Value, 0.95) })
        {
            var lanes = Play(song, new[] { "strip", "zone4" });
            foreach (var L in lanes)
            {
                var (noKick, recall) = Score(song, L);
                T.Info($"{song.Name,-10} {L.Dev,-6} rings without a kick {noKick:F1}/min, kicks with a ring {recall:P0}");
                T.Check(noKick <= 10, $"{song.Name} {L.Dev}: rings come from kicks, not from beats without one ({noKick:F1}/min)");
                T.Check(recall >= need, $"{song.Name} {L.Dev}: the kicks get a ring ({recall:P0}, need {need:P0})");
                if (L.Dev != "zone4") continue;
                // 小さいゾーン: キックのない拍（前後にもキックがない）と、キックの拍の明るさ
                var kickBeats = song.Beats.Where(b => b.Kick >= 0.3 && b.T >= Skip && b.T < song.Sec - 1).ToList();
                var emptyBeats = song.Beats.Where(b => b.T >= Skip && b.T < song.Sec - 1 && !song.Kicks.Any(k => k.T > b.T - 0.25 && k.T < b.T + 0.2)).ToList();
                if (emptyBeats.Count == 0) continue;
                double kb = kickBeats.Average(b => L.Height(b.T, 60 / song.Bpm)), eb = emptyBeats.Average(b => L.Height(b.T, 60 / song.Bpm));
                T.Info($"{song.Name,-10} {L.Dev,-6} beats with a kick {kb:F2}, beats without one {eb:F2}");
                T.Check(eb <= 0.2 * kb, $"{song.Name} {L.Dev}: a small zone stays dark on beats without a kick ({eb:F2} vs {kb:F2})");
            }
        }
        foreach (var song in new[] { Rock.Value, HipHop.Value })
        {
            var L = Play(song, new[] { "zone4" }, Intensities.Subtle, rf: true)[0];
            var (noKick, recall) = Score(song, L);
            T.Info($"Subtle {song.Name,-10} {L.Dev,-6} rings without a kick {noKick:F1}/min, kicks with a ring {recall:P0}");
            T.Check(noKick <= 10 && recall >= 0.85, $"Subtle {song.Name} {L.Dev}: rings come from kicks ({noKick:F1}/min without one, {recall:P0} of kicks)");
        }
    }

    /// <summary>
    /// ドラムのないところでは輪を出さない: ハウスの間奏・EDM のビルド（スネアのロールとライザー）の拍の光の高さが、ドロップのキックの拍の 0.15 倍以下。
    /// バラードのピアノの前奏（ドラムが入る前。無音のあとの最初の音は大きな出来事なので数えない）では、輪が 1 つまで。ドラムが入ったら、キックの 90% 以上で輪が出る。
    /// 6.15.10 は、テンポをつかんだまま拍ごとに輪を出していたので、間奏・ビルドがドロップの 0.4〜0.95 倍、ピアノの前奏ではキックの拍より明るかった
    /// </summary>
    public static void TestRippleStaysDarkWithoutDrums()
    {
        foreach (var (song, quiet, loud) in new[] { (House.Value, "break", "drop"), (Edm.Value, "build", "drop2") })
        {
            foreach (var L in Play(song, new[] { "strip", "zone4" }))
            {
                double q = song.Beats.Where(b => b.Sec == quiet).Average(b => L.Height(b.T, 60 / song.Bpm));
                double k = song.Beats.Where(b => b.Sec == loud && b.Kick > 0).Average(b => L.Height(b.T, 60 / song.Bpm));
                T.Info($"{song.Name,-10} {L.Dev,-6} {quiet} beats {q:F2}, {loud} kick beats {k:F2}");
                T.Check(q <= 0.15 * k, $"{song.Name} {L.Dev}: no rings in the {quiet} without drums ({q:F2} vs {k:F2})");
            }
        }
        var ballad = Ballad.Value;
        double drumsIn = ballad.Kicks.Min(x => x.T);
        foreach (var L in Play(ballad, new[] { "strip", "zone4" }))
        {
            int before = L.Rings.Count(r => r >= 1 && r < drumsIn - 0.1);
            var (_, recall) = Score(ballad, L);
            T.Info($"{ballad.Name,-10} {L.Dev,-6} rings in the piano intro {before} [{string.Join(" ", L.Rings.Where(r => r >= 1 && r < drumsIn - 0.1).Select(r => r.ToString("F2")))}], kicks with a ring after the drums come in {recall:P0}");
            T.Check(before <= 1, $"{ballad.Name} {L.Dev}: piano chords before the drums do not make rings ({before})");
            T.Check(recall >= 0.9, $"{ballad.Name} {L.Dev}: the kicks get a ring once the drums come in ({recall:P0})");
        }
    }

    /// <summary>
    /// 分析が描く時刻の 0.01 秒先までしか届いていないとき（アプリと同じくらい）でも、4 つ打ちのキックに遅れずに光る:
    /// キックから見て分かる光り始めまでの中央値が 0.02 秒以下（テープ・4 個のゾーン）。続くキックへの先回りが外れるのは、1 曲で 3 回まで。
    /// 直接書き込むグラボ（約 0.1 秒先を描く）でも、中央値 0.045 秒以下
    /// </summary>
    public static void TestRippleOnTimeWithRealisticLead()
    {
        foreach (var (song, sections) in new[] { (House.Value, new[] { "verse", "drop" }), (Edm.Value, new[] { "drop1", "drop2" }) })
        {
            foreach (var L in Play(song, new[] { "strip", "zone4", "direct" }, lead: 0.01))
            {
                var flashes = L.Flashes();
                var lat = new List<double>();
                foreach (var b in song.Beats.Where(b => b.T >= Skip && b.Kick > 0 && sections.Contains(b.Sec)))
                {
                    double f = flashes.Where(x => x >= b.T - 0.05 && x <= b.T + 0.12).DefaultIfEmpty(double.NaN).Min();
                    if (!double.IsNaN(f)) lat.Add(f - b.T);
                }
                lat.Sort();
                double med = lat.Count == 0 ? double.PositiveInfinity : lat[lat.Count / 2];
                int miss = L.Fx?.PatternMiss ?? 0;
                double limit = L.Dev == "direct" ? 0.045 : 0.02;
                T.Info($"{song.Name,-10} {L.Dev,-6} kick to flash median {med * 1000:F0} ms ({lat.Count} kicks), missed early rings {miss}");
                T.Check(med <= limit, $"{song.Name} {L.Dev}: the light comes on with the kick (median {med * 1000:F0} ms, at most {limit * 1000:F0} ms)");
                if (L.Dev == "strip") T.Check(miss <= 3, $"{song.Name}: early rings for the coming kicks rarely miss ({miss})");
            }
        }
    }
}

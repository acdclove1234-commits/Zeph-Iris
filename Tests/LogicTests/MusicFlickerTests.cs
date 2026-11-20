// ==================================================
// MusicFlickerTests.cs - 音楽連動の光り方ごとに、ちらつかないことを確かめる（合成した曲。録音がなくても動く）
//   ModeSyncBench と同じ流れ（本物の分析に 10 ミリ秒ずつ流し、60 コマ／秒で描く）で、音の多い曲（テンポ 92・118・128・140）を光らせ、
//   LED ごとの送った値（0〜255）の向きの戻り・消えたり点いたりを数える
//     jit    … 2 段以上の変化で、4 コマ（0.07 秒）以内に向きが戻った回数（LED 1 個・1 秒あたり）
//     toggle … 1 段の変化でも数えた jit（ディザリングの 2・3・2・3 も数える）
//     blink  … 消える（0）と点く（1 以上）の行き来
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;
using static LogicTests.ModeSyncBenchTests;

namespace LogicTests;

public static class MusicFlickerTests
{
    /// <summary>合成する曲のテンポ（ModeSyncBench の TestWriteSynthSongs と同じ）</summary>
    private static readonly double[] Bpms = { 92, 118, 128, 140 };

    /// <summary>ちらつきの上限（LED 1 個・1 秒あたり）</summary>
    public const double MaxJit = 1.0, MaxToggle = 1.0, MaxBlink = 0.2;

    private static readonly Dictionary<string, Dictionary<string, ModeScore>> Cache = new();
    private static List<(double Bpm, float[] Buf, int Fs)>? _songs;

    private static List<(double Bpm, float[] Buf, int Fs)> Songs()
    {
        if (_songs is not null) return _songs;
        _songs = new();
        foreach (double bpm in Bpms)
        {
            var g = new SignalGen(48000, 60, seed: (int)bpm);
            g.DenseMix(bpm, 0.5, 59);
            _songs.Add((bpm, g.Buf, g.Fs));
        }
        return _songs;
    }

    /// <summary>
    /// 光り方ごとの、4 曲の平均（同じ条件は 1 回だけ計算して使い回す）。
    /// key = 条件の名前、device = 光らせるデバイス（null なら 24 個のテープ）、tweak = 設定の変え方、
    /// scenes = 楽器の AI の場面の代わり（曲の頭からの秒 → 場面。ModeSyncBench.RunSong と同じ）
    /// </summary>
    public static Dictionary<string, ModeScore> Scores(string key = "default", string[]? modes = null, RgbDevice? device = null, Func<ResolvedMusic, ResolvedMusic>? tweak = null,
        IReadOnlyList<(string Label, RgbDeviceSettings Settings)>? settings = null, Func<double, MusicScene>? scenes = null)
    {
        if (Cache.TryGetValue(key, out var hit)) return hit;
        modes ??= MusicStyles.All.ToArray();
        var all = new Dictionary<string, List<ModeScore>>();
        foreach (var (_, buf, fs) in Songs())
            foreach (var s in RunSong(buf, fs, modes, out _, out _, device, tweak, settings, scenes))
                (all.TryGetValue(s.Mode, out var l) ? l : all[s.Mode] = new()).Add(s);
        var avg = all.ToDictionary(kv => kv.Key, kv =>
        {
            var l = kv.Value;
            return new ModeScore(kv.Key, l.Average(x => x.Hit), l.Average(x => x.Beat), l.Average(x => x.OffMs), l.Average(x => x.Resp),
                l.Average(x => x.LagMs), l.Average(x => x.Loud), l.Average(x => x.Range), l.Average(x => x.Jit), (int)l.Average(x => x.Flashes))
                { Toggle = l.Average(x => x.Toggle), Blink = l.Average(x => x.Blink), Jits = l.Select(x => x.Jit).ToArray() };
        });
        Cache[key] = avg;
        return avg;
    }

    private static ResolvedMusic WithIntensity(string intensity, bool reduceFlashing)
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyIntensity(cfg.Music, intensity);
        cfg.Music.ReduceFlashing = reduceFlashing;
        return MusicTuning.Resolve(cfg);
    }

    /// <summary>
    /// 光が LED の上を動く光り方（メーター・波紋・スペクトラム）。いちばん暗いときの明るさが 3% の「激しい」では、
    /// 光が通り過ぎるたびに LED が消えて点く（拍ごとに 1 回点いて 1 回消える = 140 BPM で 1 秒に 4.7 回まで）のは光り方そのもの
    /// </summary>
    private static bool Moving(string mode) => mode is MusicStyles.Meter or MusicStyles.Ripple or MusicStyles.Spectrum;

    /// <summary>光が通り過ぎて消えたり点いたりする回数の上限（LED 1 個・1 秒あたり。Moving の光り方）</summary>
    public const double MaxBlinkMoving = 3.0;

    internal static void CheckSteady(string label, ModeScore s)
    {
        string per = string.Join(" / ", s.Jits.Select(j => j.ToString("F2")));
        double maxBlink = Moving(s.Mode) ? MaxBlinkMoving : MaxBlink;
        T.Check(s.Jit < MaxJit && s.Toggle < MaxToggle && s.Blink < maxBlink,
            $"{label} {s.Mode}: no flicker (jit {s.Jit:F2}/s per LED [{per}], 1-step toggles {s.Toggle:F2}/s, blinks {s.Blink:F2}/s < {maxBlink})");
    }

    /// <summary>
    /// どの光り方も、音の多い曲でちらつかない（6.15.7 では、スペクトラム 5.67・パーツごとの役割 2.56・きらめき 2.48・
    /// ミックス 2.30・ビートで色替え 2.29・テンポ追従 1.73 回／秒。暗い LED のディザリングで 1 段の行き来は 4〜14 回／秒）
    /// </summary>
    public static void TestNoMusicStyleFlickers()
    {
        foreach (var s in Scores().Values)
        {
            T.Info(Format(s));
            CheckSteady("strip", s);
        }
    }

    /// <summary>
    /// ちらつきを止めても、拍に合って光るのはそのまま（6.15.7 の値: hit = 拍に乗った立ち上がりの割合、beat = 光った拍の割合）。
    /// 光り始めは遅らせない（拍に乗った立ち上がりのずれの中央値が、拍より 25 ミリ秒以上遅れない）
    /// </summary>
    public static void TestSteadyLightsStillFollowTheBeat()
    {
        // 6.15.7 の値（4 曲の平均）から 5 ポイントまで下がってよい
        var before = new Dictionary<string, (double Hit, double Beat)>
        {
            [MusicStyles.BeatHue] = (0.96, 1.00),
            [MusicStyles.Meter] = (0.99, 1.00), [MusicStyles.Spectrum] = (0.58, 1.00),
            [MusicStyles.Ripple] = (0.98, 1.00), [MusicStyles.Sparkle] = (0.97, 1.00), [MusicStyles.Role] = (0.98, 1.00),
            // 楽器で光る（新しい光り方。入れたときの値 hit 98%・beat 100% から 2 ポイント下げた値を元にする）
            [MusicStyles.Ensemble] = (0.96, 1.00),
        };
        var scores = Scores();
        foreach (var (mode, (hit, beat)) in before)
        {
            var s = scores[mode];
            T.Check(s.Hit >= hit - 0.05 && s.Beat >= beat - 0.05 && !(s.OffMs > 25),
                $"{mode}: still on the beat (hit {s.Hit:P0} vs {hit:P0} before, beats lit {s.Beat:P0} vs {beat:P0}, offset {s.OffMs:F0} ms)");
        }
    }

    /// <summary>
    /// パーツごとの役割: 低音・中音・高音のゾーンもちらつかない（高音のゾーンはハイハットで光るので、16 分のハイハットで
    /// ゾーン全体が 1 秒に何回も点滅しやすい）。強さ「激しい」（ハイハットの間隔 0.05 秒）でも
    /// </summary>
    public static void TestRoleZonesDoNotFlicker()
    {
        var devices = new[]
        {
            new RgbDevice(1, "Keyboard", 24, RgbDevice.TypeKeyboard, null, "HID: kb"),   // 高音
            new RgbDevice(2, "Cooler", 24, RgbDevice.TypeCooler, null, "HID: cooler"),   // 低音
            new RgbDevice(3, "Board", 24, RgbDevice.TypeMotherboard, new[] { 24 }, "HID: board") { ZoneNames = new[] { "PCB" } },   // 中音
        };
        foreach (var dev in devices)
        foreach (var (intensity, reduce) in new[] { (Intensities.Moderate, true), (Intensities.Intense, false) })
        {
            var s = Scores($"role-{dev.Name}-{intensity}", new[] { MusicStyles.Role }, dev, _ => WithIntensity(intensity, reduce))[MusicStyles.Role];
            T.Info($"{dev.Name,-8} {intensity,-8} " + Format(s));
            CheckSteady($"{dev.Name} {intensity}", s);
        }
    }

    /// <summary>
    /// パーツごとの役割の、楽器ごとの役割（キック・スネア・ハイハット・ベース・ボーカル・和音・パッド）もちらつかない。
    /// 24 個のテープ（スネア・ハイハットは粒）と 4 個の小さいゾーン（スネアはゾーン全体・ハイハットは色の変化）で、
    /// 強さ「ふつう」と、「激しい」・点滅の制限オフ
    /// </summary>
    public static void TestInstrumentRoleZonesDoNotFlicker()
    {
        string[] sounds = { MusicSounds.Kick, MusicSounds.Snare, MusicSounds.Hat, MusicSounds.BassLine, MusicSounds.Vocal, MusicSounds.Harmony, MusicSounds.Pad };
        foreach (int leds in new[] { 24, 4 })
        {
            var dev = new RgbDevice(7, "Strip", leds, 4, null, "HID: strip");
            var lanes = sounds.Where(x => leds >= EffectContext.SmallZoneLeds || x is MusicSounds.Snare or MusicSounds.Hat or MusicSounds.Kick)
                .Select(x =>
                {
                    var ds = new RgbDeviceSettings { Name = dev.Name };
                    ds.Zones.Add(new RgbZoneSettings { Name = dev.ZoneName(0), Sound = x });
                    return (Label: x, Settings: ds);
                }).ToList();
            foreach (var (intensity, reduce) in new[] { (Intensities.Moderate, true), (Intensities.Intense, false) })
            foreach (var s in Scores($"inst-{leds}-{intensity}", new[] { MusicStyles.Role }, dev, _ => WithIntensity(intensity, reduce) with { RoleSet = MusicRoleSets.Band }, lanes).Values)
            {
                T.Info($"{leds,2} LEDs {intensity,-8} " + Format(s));
                CheckSteady($"{leds} LEDs {intensity}", s);
            }
        }
    }

    /// <summary>強さ「激しい」・点滅の制限オフ（ハイハット・スネアの粒がいちばん多い）でも、ちらつかない</summary>
    public static void TestIntenseStylesDoNotFlicker()
    {
        var modes = MusicStyles.All.ToArray();
        foreach (var s in Scores("intense", modes, null, _ => WithIntensity(Intensities.Intense, false)).Values)
        {
            T.Info(Format(s));
            CheckSteady("intense", s);
        }
    }

    /// <summary>
    /// 楽器の AI の場面の色（SceneColors）をオンにして、場面を 6 秒ごとに 場面なし → 歌 → 金管 → ギター → 鍵盤 → シンセ → 弦 と巡らせても、
    /// どの光り方もちらつかず（強さ「ふつう」・点滅を抑える／「激しい」・点滅の制限オフ）、拍に合って光るのも
    /// 場面の色がオフのときから 5 ポイント以上下がらない（場面は手で入れる。楽器の AI が実際に何と答えるかは別）
    /// </summary>
    public static void TestSceneColorsDoNotFlicker()
    {
        var cycle = SceneColorTests.Cycle(60);
        foreach (var (intensity, reduce, offKey) in new[] { (Intensities.Moderate, true, "default"), (Intensities.Intense, false, "intense") })
        {
            var modes = MusicStyles.All.ToArray();
            var off = reduce ? Scores() : Scores("intense", modes, null, _ => WithIntensity(Intensities.Intense, false));
            var on = Scores($"scenes-{intensity}", modes, null, _ => WithIntensity(intensity, reduce) with { SceneColors = true }, scenes: cycle);
            foreach (var s in on.Values)
            {
                var o = off[s.Mode];
                T.Info($"scenes {intensity,-8} " + Format(s));
                CheckSteady($"scenes {intensity}", s);
                T.Check(s.Hit >= o.Hit - 0.05 && s.Beat >= o.Beat - 0.05 && !(s.OffMs > 25),
                    $"scenes {intensity} {s.Mode}: still on the beat (hit {s.Hit:P0} vs {o.Hit:P0} without scenes, beats lit {s.Beat:P0} vs {o.Beat:P0}, offset {s.OffMs:F0} ms)");
            }
        }
    }

    /// <summary>
    /// C13 楽器で光る: パレット「選んだ色だけ」と「レインボー」、24 個・4 個・1 個の LED、強さ「ふつう」・点滅を抑える／「激しい」・点滅の制限オフ、
    /// 場面なしと場面を巡らせたとき（SceneColorTests.Cycle）の全部で、ちらつかない（jit・toggle < 1.0、blink < 0.2 / LED・秒）。
    /// 1 回の分析で全部のレーンを描く（SceneColorTests.Render。数え方は ModeSyncBench の DrawFrame と同じ）
    /// </summary>
    public static void TestEnsembleDoesNotFlicker()
    {
        var lanes = new List<SceneColorTests.LaneSpec>();
        foreach (var pal in new[] { Palettes.Solid, Palettes.Rainbow })
        foreach (int leds in new[] { 24, 4, 1 })
        foreach (var (intensity, reduce) in new[] { (Intensities.Moderate, true), (Intensities.Intense, false) })
        foreach (bool scenes in new[] { false, true })
        {
            var m = WithIntensity(intensity, reduce) with { Effect = MusicStyles.Ensemble, Palette = pal, SceneColors = scenes };
            lanes.Add(new SceneColorTests.LaneSpec($"{pal} {leds} LEDs {intensity}{(scenes ? " scenes" : "")}", new RgbDevice(0, "Strip", leds, 4, null, "HID: x"), m,
                scenes ? SceneColorTests.Cycle(60) : null));
        }
        int nl = lanes.Count;
        var jit = new double[nl];
        var tog = new double[nl];
        var blink = new double[nl];
        foreach (var (_, buf, fs) in Songs())
        {
            var prev = new int[nl][];
            var dir = new int[nl][];
            var flip = new int[nl][];
            var dir1 = new int[nl][];
            var flip1 = new int[nl][];
            var frame = new int[nl];
            var cj = new long[nl];
            var ct = new long[nl];
            var cb = new long[nl];
            for (int k = 0; k < nl; k++)
            {
                int n = lanes[k].Dev.LedCount;
                prev[k] = new int[n]; dir[k] = new int[n]; flip[k] = new int[n]; dir1[k] = new int[n]; flip1[k] = new int[n];
            }
            SceneColorTests.Render(buf, fs, lanes, (k, _, _, output, _) =>
            {
                int f = frame[k]++;
                for (int i = 0; i < output.Length; i++)
                {
                    int v = Math.Max(output[i].R, Math.Max(output[i].G, output[i].B));
                    if (f > 0)
                    {
                        int d1 = Math.Sign(v - prev[k][i]);
                        if (d1 != 0)
                        {
                            if (dir1[k][i] != 0 && d1 != dir1[k][i]) { if (f - flip1[k][i] <= 4) ct[k]++; flip1[k][i] = f; }
                            dir1[k][i] = d1;
                        }
                        if ((v == 0) != (prev[k][i] == 0)) cb[k]++;
                        int d = Math.Abs(v - prev[k][i]) >= 2 ? Math.Sign(v - prev[k][i]) : 0;
                        if (d != 0)
                        {
                            if (dir[k][i] != 0 && d != dir[k][i]) { if (f - flip[k][i] <= 4) cj[k]++; flip[k][i] = f; }
                            dir[k][i] = d;
                        }
                    }
                    prev[k][i] = v;
                }
            });
            for (int k = 0; k < nl; k++)
            {
                double per = Math.Max(1e-9, (frame[k] - 8 * 60) / 60.0 * lanes[k].Dev.LedCount);
                jit[k] += cj[k] / per / Bpms.Length;
                tog[k] += ct[k] / per / Bpms.Length;
                blink[k] += cb[k] / per / Bpms.Length;
            }
        }
        for (int k = 0; k < nl; k++)
        {
            T.Info($"{lanes[k].Label,-36} jit {jit[k]:F2}  toggle {tog[k]:F2}  blink {blink[k]:F2}");
            T.Check(jit[k] < MaxJit && tog[k] < MaxToggle && blink[k] < MaxBlink,
                $"Ensemble {lanes[k].Label}: no flicker (jit {jit[k]:F2}, 1-step toggles {tog[k]:F2}, blinks {blink[k]:F2} per LED per second)");
        }
    }
}

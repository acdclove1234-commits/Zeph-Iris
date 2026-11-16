// ==================================================
// FlashGuardTests.cs - 「激しい点滅を抑える」がオンなら、送る値で数えた強い点滅（0.1 秒で平均 +0.2）が、
//   デバイス全体でもゾーンごとでも、どの 1 秒の間でも 3 回まで（FlashGuard）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class FlashGuardTests
{
    /// <summary>
    /// 強い点滅の数え方（テストの定義）: 平均が、0.1 秒前までのコマのいちばん暗い値から 0.2 以上明るくなった、その始まり
    /// （続けて明るくなる間は 1 回）。返すのは、どの 1 秒の区間でも数えた、いちばん多い数
    /// </summary>
    internal static int MostPerSecond(IReadOnlyList<double> t, IReadOnlyList<double> m, double from = 0)
    {
        var at = new List<double>();
        bool run = false;
        for (int i = 1; i < m.Count; i++)
        {
            double lo = double.MaxValue;
            for (int k = i - 1; k >= 0 && t[i] - t[k] <= 0.1 + 1e-9; k--) lo = Math.Min(lo, m[k]);
            bool rise = lo != double.MaxValue && m[i] - lo >= 0.2;
            if (rise && !run && t[i] >= from) at.Add(t[i]);
            run = rise;
        }
        int best = 0;
        for (int a = 0, b = 0; b < at.Count; b++)
        {
            while (at[b] - at[a] >= 1.0 - 1e-9) a++;
            best = Math.Max(best, b - a + 1);
        }
        return best;
    }

    private static double MeanOf(ReadOnlySpan<LinColor> light, int s, int len)
    {
        double sum = 0;
        for (int i = s; i < s + len; i++) sum += Math.Clamp(Math.Max(light[i].R, Math.Max(light[i].G, light[i].B)), 0f, 1f);
        return sum / len;
    }

    private static double MeanOf(Rgb[] o, int s, int len)
    {
        double sum = 0;
        for (int i = s; i < s + len; i++) sum += Math.Max(o[i].R, Math.Max(o[i].G, o[i].B)) / 255.0;
        return sum / len;
    }

    /// <summary>
    /// FlashGuard だけ: いろいろな速さの点滅・ランダムな明るさの跳び・ゾーンごとにずれた点滅を、30・60・144 コマ／秒で通すと、
    /// デバイス全体もゾーンごとも 4 分の 1 ずつも、どの 1 秒の間でも強い点滅は 3 回まで（通す前は 4 回以上）。1 秒に 2 回までの点滅は少しも変えない
    /// </summary>
    public static void TestGuardKeepsStrongFlashesAtThree()
    {
        const int n = 24;
        var zones2 = new List<(int, int, int)> { (0, 12, 0), (12, 12, 1) };
        var zones1 = new List<(int, int, int)> { (0, n, 0) };
        // 光の作り方: (名前, ゾーン, コマの時刻 → LED i の明るさ)
        var cases = new List<(string Name, List<(int, int, int)> Zones, Func<double, int, float> Level, bool Busy)>();
        foreach (double rate in new[] { 2.0, 3.0, 5.0, 8.0, 12.0 })
            cases.Add(($"whole-strip flashes {rate}/s", zones1, (t, i) => (t * rate) % 1.0 < 0.3 ? 0.9f : 0.1f, rate > 3));
        cases.Add(("two zones flashing 4/s each, half a period apart", zones2,
            (t, i) => ((t * 4 + (i < 12 ? 0 : 0.5)) % 1.0) < 0.3 ? 1f : 0f, true));
        // ちょうど +0.2（送る値 20 → 71）の上がり: 計算の誤差で、点滅に数えるかどうかがどちらにもなりうる（どちらでも 3 回まで）
        foreach (double rate in new[] { 5.0, 8.0 })
            cases.Add(($"exactly +0.2 steps {rate}/s", zones1, (t, i) => (t * rate) % 1.0 < 0.3 ? 71 / 255f : 20 / 255f, false));
        cases.Add(("bass envelope 6/s (fast rise, slow fall)", zones1, (t, i) => (float)(0.15 + 0.7 * Math.Exp(-((t * 6) % 1.0) / 6 * 1 / 0.05)), true));
        for (int seed = 1; seed <= 8; seed++)
        {
            var rng = new Random(seed);
            // ランダム: 0.02 秒ごとに 20% の確率で跳ぶ（明るさも LED の範囲もばらばら）。決まった時刻の表を先に作る
            var lvl = new float[2000];
            var lo = new int[2000];
            var hi = new int[2000];
            float cur = 0.1f;
            for (int k = 0; k < lvl.Length; k++)
            {
                if (rng.NextDouble() < 0.2) cur = (float)rng.NextDouble();
                else cur *= 0.9f;
                lvl[k] = cur;
                lo[k] = rng.Next(0, 12);
                hi[k] = rng.Next(12, n + 1);
            }
            cases.Add(($"random jumps (seed {seed})", seed % 2 == 0 ? zones2 : zones1, (t, i) =>
            {
                int k = Math.Min(lvl.Length - 1, (int)(t / 0.02));
                return i >= lo[k] && i < hi[k] ? lvl[k] : 0.5f * lvl[k];
            }, false));
        }
        foreach (double fps in new[] { 30.0, 60.0, 144.0 })
        foreach (var (name, zones, level, busy) in cases)
        {
            var guard = new FlashGuard();
            guard.Layout(n, zones);
            var light = new LinColor[n];
            var output = new Rgb[n];
            var areas = new List<(int S, int L)> { (0, n) };
            if (zones.Count > 1) areas.AddRange(zones.Select(z => (z.Item1, z.Item2)));
            // 長いデバイス（8 個以上）は 4 分の 1 ずつも
            for (int q = 0; q < 4; q++) areas.Add((q * n / 4, (q + 1) * n / 4 - q * n / 4));
            var ts = new List<double>();
            var before = areas.Select(_ => new List<double>()).ToArray();
            var after = areas.Select(_ => new List<double>()).ToArray();
            bool changed = false;
            for (int f = 0; f < (int)(20 * fps); f++)
            {
                double t = 1000 + f / fps;
                for (int i = 0; i < n; i++)
                {
                    float v = level(t - 1000, i);
                    light[i] = new LinColor(v, 0.6f * v, 0.2f * v);
                }
                for (int a = 0; a < areas.Count; a++) before[a].Add(MeanOf(light, areas[a].S, areas[a].L));
                guard.Apply(light, n, t);
                // 送る値（四捨五入）で数える（DeviceRenderer と同じく、送る値にしてから Commit）
                OutputStage.Quantize(light, output, 1f);
                guard.Commit(output, n);
                for (int a = 0; a < areas.Count; a++)
                {
                    double m = MeanOf(output, areas[a].S, areas[a].L);
                    after[a].Add(m);
                    changed |= Math.Abs(MeanOf(light, areas[a].S, areas[a].L) - before[a][^1]) > 1e-6;
                }
                ts.Add(t);
            }
            int worstBefore = before.Max(m => MostPerSecond(ts, m));
            int worstAfter = after.Max(m => MostPerSecond(ts, m));
            string label = $"{fps:F0} fps, {name}";
            T.Check(worstAfter <= FlashGuard.MaxPerSecond, $"{label}: ≤ 3 strong flashes in every 1 s window, device, zones and quarters ({worstBefore} → {worstAfter})");
            if (busy) T.Check(worstBefore > 3, $"{label}: (reference) more than 3 per second without the guard ({worstBefore})");
            // （ちょうど +0.2 の段は、多めに数えて暗くしてよい）
            if (worstBefore <= 2 && !name.StartsWith("exactly")) T.Check(!changed, $"{label}: at most 2 per second: the light is not changed at all");
        }
    }

    /// 送った値で、デバイス全体もゾーンごとも 4 分の 1 ずつも、強い点滅は 1 秒に 3 回まで（どの光り方も）。点滅を抑えるがオフなら、FlashGuard は何も変えない
    /// 本物の分析と描画で、音の多い曲（128 BPM）を、24 個のテープと 2 つのゾーンのボードに、強さ「ふつう」「強め」・点滅を抑えるで描く:
    /// 送った値で、デバイス全体もゾーンごとも強い点滅は 1 秒に 3 回まで（どの光り方も）。点滅を抑えるがオフなら、FlashGuard は何も変えない
    /// </summary>
    public static void TestDenseMixStaysAtThreeStrongFlashesPerSecond()
    {
        var strip = SceneColorTests.StripDev();
        var board = new RgbDevice(5, "Board", 24, RgbDevice.TypeMotherboard, new[] { 12, 12 }, "HID: board") { ZoneNames = new[] { "A", "B" } };
        var lanes = new List<SceneColorTests.LaneSpec>();
        foreach (var intensity in new[] { Intensities.Moderate, Intensities.High })
        foreach (var s in MusicStyles.All)
        foreach (var dev in new[] { strip, board })
        {
            var cfg = new RgbConfig { Mode = RgbModes.Music };
            MusicTuning.ApplyIntensity(cfg.Music, intensity);
            cfg.Music.ReduceFlashing = true;
            lanes.Add(new SceneColorTests.LaneSpec($"{intensity}/{s}/{dev.Name}", dev, MusicTuning.Resolve(cfg) with { Effect = s, Palette = Palettes.Neon }));
        }
        var g = new SignalGen(48000, 20, seed: 128);
        g.DenseMix(128, 0.5, 19.5);
        var mono = g.Buf;

        (int[] Worst, List<string> Bad) Run(bool guardOn)
        {
            var ts = lanes.Select(_ => new List<double>()).ToArray();
            var means = lanes.Select(_ => Enumerable.Range(0, 7).Select(_ => new List<double>()).ToArray()).ToArray();
            DeviceRenderer.NoFlashGuard = !guardOn;
            try
            {
                SceneColorTests.Render(mono, 48000, lanes, (i, t, _, o, _) =>
                {
                    ts[i].Add(t);
                    means[i][0].Add(MeanOf(o, 0, o.Length));
                    means[i][1].Add(MeanOf(o, 0, 12));
                    means[i][2].Add(MeanOf(o, 12, 12));
                    for (int q = 0; q < 4; q++) means[i][3 + q].Add(MeanOf(o, q * 6, 6));
                });
            }
            finally { DeviceRenderer.NoFlashGuard = false; }
            var worst = new int[lanes.Count];
            var bad = new List<string>();
            for (int i = 0; i < lanes.Count; i++)
            {
                // テープは全体と 4 分の 1 ずつ、ボードは全体・2 つのゾーン・4 分の 1 ずつ
                var areas = lanes[i].Dev.Name == "Board" ? new[] { 0, 1, 2, 3, 4, 5, 6 } : new[] { 0, 3, 4, 5, 6 };
                foreach (int a in areas) worst[i] = Math.Max(worst[i], MostPerSecond(ts[i], means[i][a], 1.0));
                if (worst[i] > 3) bad.Add($"{lanes[i].Label} {worst[i]}");
            }
            return (worst, bad);
        }

        var (off, badOff) = Run(false);
        var (on, badOn) = Run(true);
        for (int i = 0; i < lanes.Count; i++)
            if (off[i] != on[i]) T.Info($"{lanes[i].Label}: most strong flashes in 1 s {off[i]} → {on[i]}");
        T.Info($"(reference) without the guard, over 3 per second: {(badOff.Count == 0 ? "none" : string.Join(", ", badOff))}");
        T.Check(badOn.Count == 0, $"reduce flashing: ≤ 3 strong flashes in every 1 s window on every lane, device, zones and quarters ({lanes.Count - badOn.Count}/{lanes.Count}{(badOn.Count > 0 ? ": " + string.Join(", ", badOn) : "")})");

        // 点滅を抑えるがオフ: FlashGuard を使っても使わなくても、送る値は同じ
        var offLanes = lanes.Where((_, k) => k % 6 == 0).Select(l => l with { Music = l.Music with { ReduceFlashing = false } }).ToList();
        var h1 = SceneColorTests.Render(mono, 48000, offLanes);
        DeviceRenderer.NoFlashGuard = true;
        ulong[] h2;
        try { h2 = SceneColorTests.Render(mono, 48000, offLanes); }
        finally { DeviceRenderer.NoFlashGuard = false; }
        T.Check(h1.SequenceEqual(h2), $"reduce flashing off: the guard changes nothing ({offLanes.Count} lanes identical)");
    }

    /// <summary>
    /// 本物の曲（FLASH_WAV=フォルダー、song_*.wav。ステレオ・どのサンプリング周波数でも。アプリと同じく mid と side、拍の AI あり）で、
    /// 24 個のテープ・強さ「ふつう」・点滅を抑える: どの光り方もデバイス全体で強い点滅は 1 秒に 3 回まで。FLASH_WAV がなければ飛ばす
    /// </summary>
    public static void TestRealMusicStaysAtThreeStrongFlashesPerSecond()
    {
        string? dir = Environment.GetEnvironmentVariable("FLASH_WAV");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { T.Skip("FLASH_WAV not set"); return; }
        string[] styles = (Environment.GetEnvironmentVariable("FLASH_STYLES") ?? string.Join(",", MusicStyles.All)).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var model = BeatThis.TryCreate(out _);
        var dev = SceneColorTests.StripDev();
        foreach (var file in Directory.GetFiles(dir, "song_*.wav").OrderBy(x => x))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            var (mid, side, fs) = ColorBlipTests.ReadWavStereo(file);
            var cfg = new RgbConfig { Mode = RgbModes.Music };
            var engine = new AudioEngine(fs) { Settings = MusicTuning.EngineSettings(cfg) };
            BeatAssist? assist = model is null ? null : new BeatAssist(model, fs) { Inline = true };
            if (assist is not null) engine.Assist = assist;
            var r = styles.Select(_ => new DeviceRenderer(dev)).ToArray();
            var inp = styles.Select(s => new RenderInput { Cfg = cfg, Music = ResolvedMusic.Default with { Effect = s }, Device = new RgbDeviceSettings { Name = dev.Name } }).ToArray();
            var outs = styles.Select(_ => new Rgb[dev.LedCount]).ToArray();
            var cursors = new long[styles.Length];
            var ts = new List<double>();
            var means = styles.Select(_ => new List<double>()).ToArray();
            const double t0 = 1000;
            int chunk = fs / 100;
            double next = t0, last = double.NaN;
            for (int pos = 0; pos < mid.Length; pos += chunk)
            {
                int k = Math.Min(chunk, mid.Length - pos);
                engine.Push(mid.AsSpan(pos, k), side.AsSpan(pos, k), t0 + pos / (double)fs);
                double tA = t0 + (pos + k) / (double)fs;
                while (next <= tA)
                {
                    double t = next;
                    ts.Add(t - t0);
                    for (int i = 0; i < styles.Length; i++)
                    {
                        var input = inp[i];
                        input.T = t; input.TEval = t; input.TPrevEval = double.IsNaN(last) ? t : last;
                        input.Dt = double.IsNaN(last) ? 1 / 60.0 : t - last;
                        input.Beat = engine.Beat;
                        input.Features = engine.Features;
                        engine.Features.Sample(t, input.Frame);
                        input.Events.Clear();
                        engine.Events.Read(ref cursors[i], t, input.Events);
                        r[i].Render(input, outs[i]);
                        means[i].Add(MeanOf(outs[i], 0, outs[i].Length));
                    }
                    last = t;
                    next += 1 / 60.0;
                }
            }
            assist?.Dispose();
            for (int i = 0; i < styles.Length; i++)
            {
                int worst = MostPerSecond(ts, means[i]);
                T.Check(worst <= 3, $"{id} {styles[i]}: ≤ 3 strong flashes in every 1 s window ({worst}, guard capped {r[i].Guard.Capped} frames)");
            }
        }
    }
}

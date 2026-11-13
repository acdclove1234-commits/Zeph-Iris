// ==================================================
// MusicRoleTests.cs - 音楽連動の「パーツごとの役割」「選んだ色だけ」と、音ごとに見分けやすくした「きらめき」のテスト
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class MusicRoleTests
{
    private static RgbDevice Board() => new(0, "ASRock B860", 34, RgbDevice.TypeMotherboard, new[] { 1, 8, 8, 8, 9 }, "HID: test")
    {
        ZoneNames = new[] { "RGB LED 1 Header", "Addressable Header 1", "Addressable Header 2", "Addressable Header 3", "PCB" },
    };

    private sealed class Run
    {
        public readonly DeviceRenderer R;
        public readonly RenderInput In = new();
        private readonly Rgb[] _out;
        private double _last = double.NaN;

        public Run(RgbDevice dev, ResolvedMusic m, RgbDeviceSettings ds)
        {
            R = new DeviceRenderer(dev);
            _out = new Rgb[dev.LedCount];
            In.Cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = false, StaticColor = "#FF0000" };
            In.Music = m;
            In.Device = ds;
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

        /// <summary>LED [from, from+n) の明るさ（いちばん強い色の成分）の平均</summary>
        public float Avg(int from, int n)
        {
            var l = R.LastLight;
            float s = 0;
            for (int i = from; i < from + n; i++) s += l[i].MaxChannel;
            return s / n;
        }
    }

    public static void TestRoleDefaults()
    {
        var board = Board();
        T.Check(MusicSounds.Resolve(null, board, "Addressable Header 1", null) == MusicRole.Bass, "motherboard ARGB header (fans) → bass");
        T.Check(MusicSounds.Resolve(null, board, "PCB", null) == MusicRole.Mid, "motherboard PCB lighting → mid");
        var gpu = new RgbDevice(1, "GPU", 6, RgbDevice.TypeGpu, null, "I2C: x");
        var ram = new RgbDevice(2, "RAM", 8, RgbDevice.TypeDram, null, "I2C: y");
        var kb = new RgbDevice(3, "Keyboard", 100, RgbDevice.TypeKeyboard, null, "HID: z");
        T.Check(MusicSounds.Resolve(MusicSounds.Auto, gpu, null, null) == MusicRole.Mid, "GPU → mid");
        T.Check(MusicSounds.Resolve(MusicSounds.Auto, ram, null, null) == MusicRole.Treble, "memory → treble");
        T.Check(MusicSounds.Resolve(MusicSounds.Auto, kb, null, null) == MusicRole.Treble, "keyboard → treble");
        T.Check(MusicSounds.Resolve(null, gpu, null, "front-3-up") == MusicRole.Bass, "a zone placed as front fans → bass (place wins over device type)");
        T.Check(MusicSounds.Resolve(MusicSounds.All, board, "Addressable Header 1", "front-top") == MusicRole.All, "an explicit choice wins");
        T.Check(MusicSounds.Normalize("bogus") == MusicSounds.Auto, "unknown value → Auto");
    }

    public static void TestRoleEffectLightsTheRightZone()
    {
        // 端子 1 = 低音（自動）、端子 2 = 高音（指定）
        var ds = new RgbDeviceSettings { Name = "ASRock B860" };
        ds.Zones.Add(new RgbZoneSettings { Name = "Addressable Header 2", Sound = MusicSounds.Treble });
        var m = ResolvedMusic.Default with { Effect = MusicStyles.Role, Palette = Palettes.Neon, MinBrightness = 0, TempoMode = TempoModes.Off };
        var r = new Run(Board(), m, ds);
        r.Step(1.0);
        r.Step(1.5);
        float bass0 = r.Avg(1, 8), treble0 = r.Avg(9, 8);
        r.Step(1.51, new AudioEvent(1, AudioEventKind.Kick, 1.51, 2f));
        float bassK = r.Avg(1, 8), trebleK = r.Avg(9, 8);
        T.Check(bassK > bass0 + 0.3f && trebleK > treble0 + 0.1f && trebleK - treble0 < 0.7f * (bassK - bass0),
            $"kick: the bass zone flashes fully, the others share part of the beat (bass {bass0:F2} → {bassK:F2}, treble {treble0:F2} → {trebleK:F2})");
        r.Step(2.5);
        float bass1 = r.Avg(1, 8), treble1 = r.Avg(9, 8);
        r.Step(2.51, new AudioEvent(2, AudioEventKind.Hat, 2.51, 2f));
        float bassH = r.Avg(1, 8), trebleH = r.Avg(9, 8);
        T.Check(trebleH > treble1 + 0.3f && bassH < bass1 + 0.05f, $"hi-hat lights the treble zone only (treble {treble1:F2} → {trebleH:F2}, bass {bass1:F2} → {bassH:F2})");
        r.In.Frame.Activity = 0; // 音が止まった（門が閉じた）
        r.Step(3.5);
        T.Check(r.Avg(9, 8) < 0.1f && r.Avg(1, 8) < 0.1f, "both fade out when quiet");
    }

    public static void TestRoleSpread()
    {
        var B = MusicRole.Bass; var M = MusicRole.Mid; var Tr = MusicRole.Treble; var A = MusicRole.All;
        bool[] Auto(int n) => Enumerable.Repeat(true, n).ToArray();
        string Show(MusicRole[] r) => string.Join(",", r);
        // 12V の端子（LED 1 個）・ARGB の端子 3 つ・PCB。端子は全部低音になるので、ARGB の端子から低音・中音・高音に振り分ける
        var r1 = MusicSounds.Spread(new[] { B, B, B, B, M }, Auto(5), new[] { 1, 8, 8, 8, 9 });
        T.Check(r1.SequenceEqual(new[] { B, B, M, Tr, M }), $"three fan headers → bass / mid / treble, the 1-LED header goes last, the single PCB zone stays mid ({Show(r1)})");
        // 自分で選んだゾーン（端子 2 = 高音）はそのまま。残りの端子だけで振り分ける
        var r2 = MusicSounds.Spread(new[] { B, B, Tr, B, M }, new[] { true, true, false, true, true }, new[] { 1, 8, 8, 8, 9 });
        T.Check(r2.SequenceEqual(new[] { Tr, B, Tr, M, M }), $"a zone with a chosen sound is kept, the others are spread ({Show(r2)})");
        // 自分で選んで全部低音にしたら、変えない
        var r3 = MusicSounds.Spread(new[] { B, B, B }, new bool[3], new[] { 8, 8, 8 });
        T.Check(r3.All(x => x == B), $"zones the user set to bass all stay bass ({Show(r3)})");
        // 音が重ならなければ変えない
        var r4 = MusicSounds.Spread(new[] { B, M, Tr, A }, Auto(4), new[] { 8, 8, 8, 8 });
        T.Check(r4.SequenceEqual(new[] { B, M, Tr, A }), $"no two zones share a sound → unchanged ({Show(r4)})");
        // グラボのゾーン 2 つ（中音から）、テープ 2 本（音全体 → 低音から）
        var r5 = MusicSounds.Spread(new[] { M, M }, Auto(2), new[] { 6, 6 });
        var r6 = MusicSounds.Spread(new[] { A, A }, Auto(2), new[] { 30, 30 });
        T.Check(r5.SequenceEqual(new[] { M, Tr }) && r6.SequenceEqual(new[] { B, M }), $"spread starts from the part's own sound ({Show(r5)} / {Show(r6)})");

        // ほかのメーカーのマザーボードの端子の名前も、ファン（低音）とみなす
        var board = Board();
        foreach (var name in new[] { "Aura Addressable 1", "JRAINBOW1", "D_LED1 Bottom", "ARGB Header 2" })
            T.Check(MusicSounds.Resolve(null, board, name, null) == MusicRole.Bass, $"motherboard zone \"{name}\" → bass");
        T.Check(MusicSounds.Resolve(null, board, "Aura Mainboard", null) == MusicRole.Mid, "motherboard body lighting → mid");
    }

    /// <summary>端子 3 つとも「パーツに合わせる」のマザーボードで、ハイハット・スネア・キックがそれぞれ別の端子を光らせ、色も別になる</summary>
    public static void TestRoleHeadersReactToDifferentSounds()
    {
        var m = ResolvedMusic.Default with { Effect = MusicStyles.Role, Palette = Palettes.Neon, MinBrightness = 0, TempoMode = TempoModes.Off };
        var r = new Run(Board(), m, new RgbDeviceSettings { Name = "ASRock B860" });
        // LED: 0 = 12V の端子、1〜8 = 端子 1、9〜16 = 端子 2、17〜24 = 端子 3、25〜33 = PCB
        r.Step(1.0);
        r.Step(2.5);
        float h1 = r.Avg(1, 8), h2 = r.Avg(9, 8), h3 = r.Avg(17, 8);
        var c1 = r.R.LastLight[1]; var c2 = r.R.LastLight[9]; var c3 = r.R.LastLight[17];
        static float Diff(LinColor a, LinColor b)
        {
            float ma = Math.Max(a.MaxChannel, 1e-6f), mb = Math.Max(b.MaxChannel, 1e-6f);
            return Math.Abs(a.R / ma - b.R / mb) + Math.Abs(a.G / ma - b.G / mb) + Math.Abs(a.B / ma - b.B / mb);
        }
        T.Check(Diff(c1, c2) > 0.3f && Diff(c2, c3) > 0.3f && Diff(c1, c3) > 0.3f,
            $"the three headers have different colours ({c1} / {c2} / {c3})");
        r.Step(2.51, new AudioEvent(1, AudioEventKind.Hat, 2.51, 2f));
        float h1H = r.Avg(1, 8), h3H = r.Avg(17, 8);
        T.Check(h3H > h3 + 0.3f && h1H < h1 + 0.05f, $"hi-hat lights header 3 (treble) only ({h3:F2} → {h3H:F2}, header 1 {h1:F2} → {h1H:F2})");
        r.Step(4.0);
        h1 = r.Avg(1, 8); h2 = r.Avg(9, 8);
        r.Step(4.01, new AudioEvent(2, AudioEventKind.Snare, 4.01, 2f));
        float h1S = r.Avg(1, 8), h2S = r.Avg(9, 8);
        T.Check(h2S > h2 + 0.3f && h1S < h1 + 0.05f, $"snare lights header 2 (mid), not header 1 ({h2:F2} → {h2S:F2}, header 1 {h1:F2} → {h1S:F2})");
        r.Step(5.5);
        h1 = r.Avg(1, 8); h2 = r.Avg(9, 8);
        r.Step(5.51, new AudioEvent(3, AudioEventKind.Kick, 5.51, 2f));
        float h1K = r.Avg(1, 8), h2K = r.Avg(9, 8);
        T.Check(h1K - h1 > 1.4f * (h2K - h2) && h1K > h1 + 0.3f, $"kick lights header 1 (bass) the most ({h1:F2} → {h1K:F2}, header 2 {h2:F2} → {h2K:F2})");
    }

    public static void TestSolidPalette()
    {
        var p = ColorPalette.Get(Palettes.Solid, "#FF0000");
        bool same = true;
        var first = p.ScreenAt(0);
        for (double x = 0; x < 1; x += 0.05) same &= p.ScreenAt(x) == first;
        T.Check(same && first.R > 240 && first.G < 20 && first.B < 20, $"solid palette: every position is the chosen colour ({first})");
        T.Check(!ReferenceEquals(p, ColorPalette.Get(Palettes.Solid, "#00FF00")), "solid palette depends on the chosen colour");
        T.Check(Palettes.UsesStaticColor(Palettes.Solid) && Palettes.UsesStaticColor(Palettes.Mono) && !Palettes.UsesStaticColor(Palettes.Neon), "which palettes use the chosen colour");

        // 選んだ色だけ: ビートで色替えでも色は変わらない（明るさだけ）
        var m = ResolvedMusic.Default with { Effect = MusicStyles.BeatHue, Palette = Palettes.Solid, MinBrightness = 0, TempoMode = TempoModes.Off };
        var r = new Run(new RgbDevice(5, "Strip", 10, 4, null, "HID: s"), m, new RgbDeviceSettings { Name = "Strip" });
        bool red = true;
        for (int i = 0; i < 20; i++)
        {
            double t = 1 + i * 0.25;
            r.Step(t, new AudioEvent(i + 1, AudioEventKind.Kick, t, 2f));
            var c = r.R.LastLight[0];
            if (c.MaxChannel > 0.05f) red &= c.R >= c.MaxChannel - 1e-4f && c.G < 0.1f * c.R && c.B < 0.1f * c.R;
        }
        T.Check(red, "beat-hue with the solid palette stays red on every beat");
    }

    public static void TestSparkleSoundsLookDifferent()
    {
        var m = ResolvedMusic.Default with { Effect = MusicStyles.Sparkle, Palette = Palettes.Neon, MinBrightness = 0, TempoMode = TempoModes.Off };
        var strip = new RgbDevice(5, "Strip", 100, 4, null, "HID: s");
        var ds = new RgbDeviceSettings { Name = "Strip", Mirror = false };

        // キック: ゾーン全体がそろって明るくなる
        var r = new Run(strip, m, ds);
        r.In.Frame.MidE = 0;
        r.Step(1.0);
        float before = r.Avg(0, 100);
        r.Step(1.01, new AudioEvent(1, AudioEventKind.Kick, 1.01, 2f));
        float after = r.Avg(0, 100);
        T.Check(after > before * 2 && after > 0.15f, $"kick: the whole zone swells ({before:F2} → {after:F2})");

        // スネア: 続けて並んだ LED がまとまって光る
        var r2 = new Run(strip, m, ds);
        r2.In.Frame.MidE = 0;
        r2.Step(5.0);
        r2.Step(5.01, new AudioEvent(1, AudioEventKind.Snare, 5.01, 2f));
        var lit = Enumerable.Range(0, 100).Where(i => r2.R.LastLight[i].MaxChannel > 0.3f).ToList();
        int runs = lit.Count == 0 ? 0 : 1 + lit.Zip(lit.Skip(1), (a, b) => b - a > 1 ? 1 : 0).Sum();
        T.Check(lit.Count >= 8 && runs <= 2, $"snare: one block of neighbouring LEDs ({lit.Count} lit in {runs} block(s))");

        // ハイハット: 離れた白っぽい粒
        var r3 = new Run(strip, m, ds);
        r3.In.Frame.MidE = 0;
        r3.Step(9.0);
        r3.Step(9.01, new AudioEvent(1, AudioEventKind.Hat, 9.01, 2f));
        var dots = Enumerable.Range(0, 100).Where(i => r3.R.LastLight[i].MaxChannel > 0.3f).Select(i => r3.R.LastLight[i]).ToList();
        bool whitish = dots.All(c => Math.Min(c.R, Math.Min(c.G, c.B)) > 0.25f * c.MaxChannel);
        T.Check(dots.Count is >= 1 and <= 8 && whitish, $"hi-hat: a few whitish dots ({dots.Count})");
    }
}

public static class MusicRoleBalanceTests
{
    private static RgbDevice Board() => new(0, "ASRock B860", 34, RgbDevice.TypeMotherboard, new[] { 1, 8, 8, 8, 9 }, "HID: test")
    {
        ZoneNames = new[] { "RGB LED 1 Header", "Addressable Header 1", "Addressable Header 2", "Addressable Header 3", "PCB" },
    };

    /// <summary>
    /// ふつうの曲（キック 1 秒に 2 回・スネア 1 回・ハイハット 8 回。低音は強く、中音は弱い）で、
    /// 低音・中音・高音のゾーンの平均の明るさと、明るさの揺れの大きさが、どれも近いこと
    /// </summary>
    public static void TestRolesAreBalanced()
    {
        var ds = new RgbDeviceSettings { Name = "ASRock B860" };
        ds.Zones.Add(new RgbZoneSettings { Name = "Addressable Header 1", Sound = MusicSounds.Bass });
        ds.Zones.Add(new RgbZoneSettings { Name = "Addressable Header 2", Sound = MusicSounds.Mid });
        ds.Zones.Add(new RgbZoneSettings { Name = "Addressable Header 3", Sound = MusicSounds.Treble });
        var m = ResolvedMusic.Default with { Effect = MusicStyles.Role, Palette = Palettes.Neon, MinBrightness = 0, TempoMode = TempoModes.Off };
        var dev = Board();
        var r = new DeviceRenderer(dev);
        var input = new RenderInput { Cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = false }, Music = m, Device = ds };
        input.Frame.GateOpen = true;
        input.Frame.Activity = 1;
        var output = new Rgb[dev.LedCount];
        double fps = 60, last = double.NaN;
        long id = 0;
        var sum = new double[3];
        var sumSq = new double[3];
        int n = 0;
        for (double t = 1; t < 21; t += 1 / fps)
        {
            input.Events.Clear();
            double beat = (t - 1) * 2; // 1 秒に 2 拍
            double prevBeat = double.IsNaN(last) ? -1 : (last - 1) * 2;
            if (Math.Floor(beat) > Math.Floor(prevBeat)) input.Events.Add(new AudioEvent(++id, AudioEventKind.Kick, t, 2f));
            if (Math.Floor(beat / 2 + 0.5) > Math.Floor(prevBeat / 2 + 0.5)) input.Events.Add(new AudioEvent(++id, AudioEventKind.Snare, t, 1.3f));
            if (Math.Floor(beat * 4) > Math.Floor(prevBeat * 4)) input.Events.Add(new AudioEvent(++id, AudioEventKind.Hat, t, 1.2f));
            double ph = beat - Math.Floor(beat);
            input.Frame.BassE = (float)(0.9 * Math.Exp(-ph * 4));
            input.Frame.KickEnv = input.Frame.BassE;
            input.Frame.MidE = (float)(0.25 + 0.1 * Math.Sin(t * 3));
            input.Frame.TrebleE = (float)(0.45 + 0.1 * Math.Sin(t * 7));
            input.Frame.LoudVU = 0.6f;
            input.T = input.TEval = input.Frame.T = t;
            input.TPrevEval = double.IsNaN(last) ? t : last;
            input.Dt = double.IsNaN(last) ? 0 : t - last;
            r.Render(input, output);
            last = t;
            if (t < 6) continue; // 自動の明るさ合わせが落ち着くまで待つ
            var l = r.LastLight;
            for (int k = 0; k < 3; k++)
            {
                float v = 0;
                for (int i = 1 + 8 * k; i < 9 + 8 * k; i++) v += l[i].MaxChannel;
                v /= 8;
                sum[k] += v;
                sumSq[k] += v * v;
            }
            n++;
        }
        var mean = sum.Select(s => s / n).ToArray();
        var sd = Enumerable.Range(0, 3).Select(k => Math.Sqrt(Math.Max(0, sumSq[k] / n - mean[k] * mean[k]))).ToArray();
        T.Info($"roles bass/mid/treble: mean {mean[0]:F2}/{mean[1]:F2}/{mean[2]:F2}, swing (sd) {sd[0]:F2}/{sd[1]:F2}/{sd[2]:F2}");
        T.Check(mean.Max() / mean.Min() < 1.3, "average brightness of bass / mid / treble zones within 1.3×");
        T.Check(sd.Min() > 0.05 && sd.Max() / sd.Min() < 2.5, "every zone visibly reacts, and none swings 2.5× more than another");
        T.Check(mean.Min() > 0.2, "no zone stays dark while music plays");
    }
}

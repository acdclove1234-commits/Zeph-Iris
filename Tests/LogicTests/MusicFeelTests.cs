// ==================================================
// MusicFeelTests.cs - 音楽連動の「ノリ」を数値で測る (拍の直後と次の拍の直前の明るさの差。光り方・強さごと)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class MusicFeel
{
    private static RgbDevice Board() => new(0, "ASRock B860", 34, RgbDevice.TypeMotherboard, new[] { 1, 8, 8, 8, 9 }, "HID: test")
    {
        ZoneNames = new[] { "RGB LED 1 Header", "Addressable Header 1", "Addressable Header 2", "Addressable Header 3", "PCB" },
    };

    /// <summary>
    /// 120 BPM（キック毎拍・スネア 2・4 拍目・ハイハット 8 分）を 60 fps で 16 秒描き、
    /// LED [from, from+n) の「拍の直後 0〜60 ms の明るさ」と「次の拍の直前 380〜480 ms の明るさ」の平均を返す
    /// </summary>
    public static (double On, double Off) Measure(ResolvedMusic m, int from, int n, RgbDeviceSettings? ds = null)
    {
        var dev = Board();
        var r = new DeviceRenderer(dev);
        var input = new RenderInput
        {
            Cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = false },
            Music = m,
            Device = ds ?? new RgbDeviceSettings { Name = dev.Name },
        };
        input.Frame.GateOpen = true;
        input.Frame.Activity = 1;
        var output = new Rgb[dev.LedCount];
        double last = double.NaN, on = 0, off = 0;
        int nOn = 0, nOff = 0;
        long id = 0;
        for (double t = 1; t < 17; t += 1 / 60.0)
        {
            input.Events.Clear();
            double beat = (t - 1) * 2;
            double prev = double.IsNaN(last) ? -1 : (last - 1) * 2;
            if (Math.Floor(beat) > Math.Floor(prev))
            {
                input.Events.Add(new AudioEvent(++id, AudioEventKind.Kick, t, 2f));
                if (((long)Math.Floor(beat)) % 2 == 1) input.Events.Add(new AudioEvent(++id, AudioEventKind.Snare, t, 1.3f));
            }
            if (Math.Floor(beat * 2) > Math.Floor(prev * 2)) input.Events.Add(new AudioEvent(++id, AudioEventKind.Hat, t, 1.2f));
            double ph = beat - Math.Floor(beat);
            input.Frame.BassE = (float)(0.9 * Math.Exp(-ph * 5));
            input.Frame.KickEnv = input.Frame.BassE;
            input.Frame.Bass = (float)(0.5 + 0.3 * Math.Exp(-ph * 3));
            input.Frame.MidE = (float)(0.25 + 0.35 * (((long)Math.Floor(beat)) % 2 == 1 ? Math.Exp(-ph * 6) : 0));
            input.Frame.Mid = 0.4f;
            input.Frame.TrebleE = (float)(0.3 + 0.3 * Math.Exp(-(beat * 2 - Math.Floor(beat * 2)) * 8));
            input.Frame.Treble = 0.35f;
            input.Frame.LoudVU = (float)(0.55 + 0.25 * Math.Exp(-ph * 4));
            for (int k = 0; k < AudioFrame.BandCount; k++) input.Frame.Band[k] = k < 3 ? input.Frame.Bass : k < 7 ? 0.4f : 0.35f;
            input.T = input.TEval = input.Frame.T = t;
            input.TPrevEval = double.IsNaN(last) ? t : last;
            input.Dt = double.IsNaN(last) ? 0 : t - last;
            r.Render(input, output);
            last = t;
            if (t < 5) continue;
            double sinceBeat = ph * 0.5;
            float v = 0;
            var l = r.LastLight;
            for (int i = from; i < from + n; i++) v += l[i].MaxChannel;
            v /= n;
            if (sinceBeat < 0.06) { on += v; nOn++; }
            else if (sinceBeat > 0.38 && sinceBeat < 0.48) { off += v; nOff++; }
        }
        return (on / Math.Max(1, nOn), off / Math.Max(1, nOff));
    }

    public static ResolvedMusic Preset(string preset, string? effect = null)
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyPreset(cfg, preset);
        if (effect is not null) cfg.MusicStyle = effect;
        cfg.Music.TempoMode = TempoModes.Off;
        return MusicTuning.Resolve(cfg);
    }
}

public static class MusicFeelTests
{
    public static void TestOldIntensityDefaultsAreUpgraded()
    {
        var old = new MusicConfig { Intensity = Intensities.Moderate, ReactionMs = 10, SmoothnessMs = 250, Punch = 50, MinBrightness = 20 };
        old.Normalize();
        var p = MusicTuning.Profile(Intensities.Moderate);
        T.Check(old.ReactionMs == p.ReactionMs && old.SmoothnessMs == p.SmoothnessMs && old.Punch == p.Punch && old.MinBrightness == p.MinBrightness,
            "untouched old 標準 values move to the new, punchier values");
        var mine = new MusicConfig { Intensity = Intensities.Moderate, ReactionMs = 10, SmoothnessMs = 250, Punch = 50, MinBrightness = 25 };
        mine.Normalize();
        T.Check(mine.MinBrightness == 25 && mine.Punch == 50, "values the user changed are kept");
    }

    /// <summary>グルーヴ（標準）の強さで、光り方ごとの「拍の直後の明るさ」と「次の拍の直前の明るさ」の比を表示する</summary>
    public static void TestBeatContrastByStyle()
    {
        string[] styles =
        {
            MusicStyles.BeatHue, MusicStyles.Pulse, MusicStyles.Mix, MusicStyles.Meter, MusicStyles.Spectrum, MusicStyles.Energy,
            MusicStyles.Scroll, MusicStyles.Ripple, MusicStyles.Sparkle, MusicStyles.Role, MusicStyles.Chase,
        };
        foreach (var preset in new[] { MusicPresets.Groove, MusicPresets.Party })
        {
            foreach (var s in styles)
            {
                var m = MusicFeel.Preset(preset, s);
                var (on, off) = MusicFeel.Measure(m, 1, 24);
                double contrast = on > 1e-6 ? 1 - off / on : 0;
                T.Info($"{preset,-7} {s,-9} on {on:F2} off {off:F2} contrast {contrast:F2}");
            }
        }
    }
}

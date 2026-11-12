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
        var tr = Trace(m, from, n, ds);
        double on = 0, off = 0;
        int nOn = 0, nOff = 0;
        foreach (var (sinceBeat, _, v) in tr)
        {
            if (sinceBeat < 0.06) { on += v.MaxChannel; nOn++; }
            else if (sinceBeat > 0.38 && sinceBeat < 0.48) { off += v.MaxChannel; nOff++; }
        }
        return (on / Math.Max(1, nOn), off / Math.Max(1, nOff));
    }

    /// <summary>
    /// 光り方の見た目の特徴: 色が一度に変わった回数（12 秒・24 拍の間）と、拍の直後・拍の 0.2 秒後・次の拍の直前の明るさ。
    /// 「ビートで色替え」と「パルス」の違いを数で見るため
    /// </summary>
    public static (int ColorChanges, double On, double Mid, double Off) Character(ResolvedMusic m, int from, int n)
    {
        var tr = Trace(m, from, n);
        int changes = 0;
        LinColor? refHue = null;
        double on = 0, mid = 0, off = 0;
        int nOn = 0, nMid = 0, nOff = 0;
        foreach (var (sinceBeat, _, v) in tr)
        {
            float mx = v.MaxChannel;
            if (sinceBeat < 0.06) { on += mx; nOn++; }
            else if (sinceBeat > 0.18 && sinceBeat < 0.24) { mid += mx; nMid++; }
            else if (sinceBeat > 0.38 && sinceBeat < 0.48) { off += mx; nOff++; }
            if (mx < 0.05f) continue;
            // 明るさをそろえた色（色合いだけ）。前に数えた色から大きく変わったら 1 回
            var hue = v * (1f / mx);
            if (refHue is not LinColor h) { refHue = hue; continue; }
            if (Math.Max(Math.Abs(hue.R - h.R), Math.Max(Math.Abs(hue.G - h.G), Math.Abs(hue.B - h.B))) > 0.35f) { changes++; refHue = hue; }
        }
        return (changes, on / Math.Max(1, nOn), mid / Math.Max(1, nMid), off / Math.Max(1, nOff));
    }

    /// <summary>120 BPM の合成の曲を 60 fps で 16 秒描き、5 秒以降のコマごとに（拍からの秒数, 時刻, LED [from, from+n) の平均の色）を返す</summary>
    public static List<(double SinceBeat, double T, LinColor Color)> Trace(ResolvedMusic m, int from, int n, RgbDeviceSettings? ds = null)
    {
        var trace = new List<(double, double, LinColor)>();
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
        double last = double.NaN;
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
            var sum = new LinColor(0, 0, 0);
            var l = r.LastLight;
            for (int i = from; i < from + n; i++) sum += l[i];
            trace.Add((sinceBeat, t, sum * (1f / n)));
        }
        return trace;
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

    /// <summary>
    /// 「ビートで色替え」と「パルス」は見て分かるほど違う（6.14.3 までは、どちらもビートで色が変わり、明るさの形もほぼ同じだった:
    /// 色替え 24 回 / 27 回、拍の 0.2 秒後 0.31 / 0.48）。
    ///   ビートで色替え: ビートで色が変わり（12 秒で 8 回以上）、拍の後すぐ暗くなる
    ///   パルス: 色はゆっくり（4 回以下）、残光が長い（拍の 0.2 秒後も、拍の直後の 6 割以上残る）。それでも拍で脈打つ
    /// </summary>
    public static void TestBeatHueVsPulseCharacter()
    {
        foreach (var preset in new[] { MusicPresets.Groove, MusicPresets.Party })
        {
            // テンポ「自動」: 強いキックで色が進む（テンポをつかむ分析はここでは動かさない）
            var hue = MusicFeel.Character(MusicFeel.Preset(preset, MusicStyles.BeatHue) with { Palette = Palettes.Neon, TempoMode = TempoModes.Auto }, 1, 24);
            var pulse = MusicFeel.Character(MusicFeel.Preset(preset, MusicStyles.Pulse) with { Palette = Palettes.Neon, TempoMode = TempoModes.Auto }, 1, 24);
            T.Info($"{preset,-7} BeatHue  color changes {hue.ColorChanges,2}  on {hue.On:F2}  +0.2s {hue.Mid:F2}  before next {hue.Off:F2}");
            T.Info($"{preset,-7} Pulse    color changes {pulse.ColorChanges,2}  on {pulse.On:F2}  +0.2s {pulse.Mid:F2}  before next {pulse.Off:F2}");
            T.Check(hue.ColorChanges >= 8 && pulse.ColorChanges <= 4, $"{preset}: BeatHue changes colour on beats ({hue.ColorChanges}), Pulse drifts slowly ({pulse.ColorChanges})");
            T.Check(pulse.Mid / pulse.On >= 0.6 && hue.Mid / hue.On <= 0.6, $"{preset}: Pulse glows longer after the beat ({pulse.Mid / pulse.On:F2} vs {hue.Mid / hue.On:F2})");
            T.Check(pulse.Off / pulse.On <= 0.6, $"{preset}: Pulse still pulses with the beat ({pulse.Off:F2} before the next beat vs {pulse.On:F2})");
        }
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

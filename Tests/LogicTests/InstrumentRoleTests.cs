// ==================================================
// InstrumentRoleTests.cs - 光り方「パーツごとの役割」の楽器ごとの役割 (キック・スネア・ハイハット・ベース・ボーカル・
//                          和音・パッド) と、分け方（音の高さ・楽器ごと・ドラムだけ）、前の設定の読み込み
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

/// <summary>楽器ごとの役割を、本物の分析に流して光らせる（MusicLight.Run の 30 LED のテープ。ゾーン 1 つ）</summary>
public static class InstrumentRoleRun
{
    public static readonly RgbDevice Strip = new(2, "Strip", 30, 4, null, "HID: x");

    /// <summary>テープ全体のゾーンの「反応する音」を sound にした設定</summary>
    public static RgbDeviceSettings StripWith(string sound)
    {
        var ds = new RgbDeviceSettings { Name = Strip.Name };
        ds.Zones.Add(new RgbZoneSettings { Name = Strip.ZoneName(0), Sound = sound });
        return ds;
    }

    /// <summary>光り方「パーツごとの役割」（強さ Moderate）。分け方 roleSet</summary>
    public static ResolvedMusic Role(string roleSet = MusicRoleSets.Bands) => MusicLight.Default(MusicStyles.Role) with { RoleSet = roleSet };

    public static LightRun Run(float[] buf, string sound, string roleSet = MusicRoleSets.Bands) =>
        MusicLight.Run(buf, InstrumentSources.Fs, Role(roleSet), new RgbConfig { Mode = RgbModes.Music, Sensitivity = 2 }, StripWith(sound));

    /// <summary>[from, to) 秒の明るさの平均</summary>
    public static double Mean(LightRun r, double from, double to) => r.Between(from, to).Average(x => x.B);

    /// <summary>ドラム（DenseMix と同じ型。bass なら ベースも）</summary>
    public static SignalGen Drums(double sec, int seed = 51, bool bass = true)
    {
        var g = new SignalGen(InstrumentSources.Fs, sec, seed: seed);
        MusicBench.Pattern(g, 120, 0.2, sec, "x.......x.x.....", "....x.......x...", "x.x.x.x.x.x.x.x.", jitterMs: 8, seed: seed, hatAmp: 0.06);
        if (bass) MusicBench.Backing(g, 120, 0.2, sec);
        return g;
    }
}

/// <summary>合成した分析結果・出来事で 1 台を描く（MusicRoleTests の Run と同じ作り）</summary>
public sealed class RoleRig
{
    public readonly DeviceRenderer R;
    public readonly RenderInput In = new();
    private readonly Rgb[] _out;
    private double _last = double.NaN;
    private long _id;

    public RoleRig(RgbDevice dev, ResolvedMusic m, RgbDeviceSettings ds)
    {
        R = new DeviceRenderer(dev);
        _out = new Rgb[dev.LedCount];
        In.Cfg = new RgbConfig { Mode = RgbModes.Music, Brightness = 100, Gamma = false, StaticColor = "#FF0000" };
        In.Music = m;
        In.Device = ds;
        In.Frame.GateOpen = true;
        In.Frame.Activity = 1;
    }

    public void Step(double t, params AudioEventKind[] kinds)
    {
        In.T = In.TEval = In.Frame.T = t;
        In.TPrevEval = double.IsNaN(_last) ? t : _last;
        In.Dt = double.IsNaN(_last) ? 0 : t - _last;
        In.Events.Clear();
        foreach (var k in kinds) In.Events.Add(new AudioEvent(++_id, k, t, 2f));
        R.Render(In, _out);
        _last = t;
    }

    /// <summary>LED [from, from+n) の明るさ（いちばん強い色の成分）</summary>
    public float[] Levels(int from, int n) => Enumerable.Range(from, n).Select(i => R.LastLight[i].MaxChannel).ToArray();
    public float Avg(int from, int n) => Levels(from, n).Average();
}

public static class InstrumentRoleTests
{
    private static ResolvedMusic Role(string roleSet = MusicRoleSets.Band, bool reduce = true) =>
        ResolvedMusic.Default with { Effect = MusicStyles.Role, Palette = Palettes.Neon, MinBrightness = 0, TempoMode = TempoModes.Off, RoleSet = roleSet, ReduceFlashing = reduce };

    private static RgbDevice Board() => new(0, "ASRock B860", 34, RgbDevice.TypeMotherboard, new[] { 1, 8, 8, 8, 9 }, "HID: test")
    {
        ZoneNames = new[] { "RGB LED 1 Header", "Addressable Header 1", "Addressable Header 2", "Addressable Header 3", "PCB" },
    };

    private static RgbDeviceSettings Zones(string device, params (string Zone, string Sound)[] zones)
    {
        var ds = new RgbDeviceSettings { Name = device };
        foreach (var (z, s) in zones) ds.Zones.Add(new RgbZoneSettings { Name = z, Sound = s });
        return ds;
    }

    /// <summary>
    /// 6.15.7 までの設定（「分け方」の値がない・「反応する音」が低音・中音・高音・音全体）は、同じ役割・同じ光り方のまま読み込める。
    /// 新しい「反応する音」（キックなど）は保存して読み直しても変わらない。知らない値は「パーツに合わせる」・「音の高さ」に戻る
    /// </summary>
    public static void TestOldRoleConfigsLoadUnchanged()
    {
        const string old = """
        { "SchemaVersion": 2, "Rgb": { "Mode": "Music", "MusicStyle": "Role", "Music": { "Intensity": "Moderate" },
          "DeviceSettings": [ { "Name": "ASRock B860", "Zones": [ { "Name": "Addressable Header 2", "Sound": "Treble" } ] } ] } }
        """;
        var cfg = AppConfig.FromJson(old);
        T.Check(cfg.Rgb.Music.RoleSet == MusicRoleSets.Bands, $"an old config has no role set → split by pitch, as before ({cfg.Rgb.Music.RoleSet})");
        T.Check(MusicTuning.Resolve(cfg.Rgb).RoleSet == MusicRoleSets.Bands && ResolvedMusic.Default.RoleSet == MusicRoleSets.Bands, "resolved music keeps the pitch split by default");
        var zone = cfg.Rgb.SettingsFor("ASRock B860").Zones.Single();
        T.Check(zone.Sound == MusicSounds.Treble, $"an old zone choice is kept ({zone.Sound})");

        var bogus = AppConfig.FromJson("""{ "Rgb": { "Music": { "RoleSet": "Orchestra" }, "DeviceSettings": [ { "Name": "X", "Zones": [ { "Name": "Z", "Sound": "Tuba" } ] } ] } }""");
        T.Check(bogus.Rgb.Music.RoleSet == MusicRoleSets.Bands, "an unknown role set → pitch split");
        T.Check(MusicSounds.Normalize("Tuba") == MusicSounds.Auto, "an unknown zone sound → Auto");

        var c2 = new AppConfig();
        c2.Rgb.Music.RoleSet = MusicRoleSets.Drums;
        c2.Rgb.DeviceSettings.Add(Zones("ASRock B860", ("Addressable Header 1", MusicSounds.Kick), ("PCB", MusicSounds.Harmony)));
        var back = AppConfig.FromJson(c2.ToJson());
        var zs = back.Rgb.SettingsFor("ASRock B860").Zones;
        T.Check(back.Rgb.Music.RoleSet == MusicRoleSets.Drums && zs.Any(z => z.Sound == MusicSounds.Kick) && zs.Any(z => z.Sound == MusicSounds.Harmony),
            "the role set and instrument zone choices survive saving and loading");

        // 同じ「反応する音」なら、分け方を変えても光り方は同じ（自分で選んだゾーンは分け方に左右されない。低音・中音・高音の光り方は 6.15.7 と同じ作り）
        var ds = Zones("ASRock B860", ("Addressable Header 1", MusicSounds.Bass), ("Addressable Header 2", MusicSounds.Mid), ("Addressable Header 3", MusicSounds.Treble), ("PCB", MusicSounds.All), ("RGB LED 1 Header", MusicSounds.Bass));
        var a = new RoleRig(Board(), Role(MusicRoleSets.Bands), ds);
        var b = new RoleRig(Board(), Role(MusicRoleSets.Band), ds);
        bool same = true;
        for (int i = 0; i < 240; i++)
        {
            double t = 1 + i / 60.0;
            var ev = i % 30 == 0 ? new[] { AudioEventKind.Kick } : i % 15 == 0 ? new[] { AudioEventKind.Hat, AudioEventKind.Snare } : Array.Empty<AudioEventKind>();
            a.In.Frame.BassE = b.In.Frame.BassE = (float)(0.5 + 0.4 * Math.Sin(t * 5));
            a.In.Frame.MidE = b.In.Frame.MidE = 0.3f;
            a.Step(t, ev);
            b.Step(t, ev);
            for (int k = 0; k < 34; k++) same &= a.R.LastLight[k] == b.R.LastLight[k];
        }
        T.Check(same, "zones set to bass / mid / treble / all light exactly the same whatever the role set");
    }

    /// <summary>
    /// 「パーツに合わせる」の役割は分け方で決まる: 音の高さ（これまでと同じ）・楽器ごと（ファンの端子はキック・スネア・ハイハット、
    /// 本体の飾り・グラボはボーカル、メモリ・キーボードはハイハット、テープは和音）・ドラムだけ（キック・スネア・ハイハット）。自分で選んだ音はそのまま
    /// </summary>
    public static void TestAutoRolesFollowTheRoleSet()
    {
        var board = Board();
        var gpu = new RgbDevice(1, "GPU", 10, RgbDevice.TypeGpu, null, "I2C: gpu");
        var ram = new RgbDevice(2, "RAM", 10, RgbDevice.TypeDram, null, "I2C: ram");
        var strip = new RgbDevice(3, "Strip", 30, 4, null, "HID: s");
        MusicRole[] Spread(string set)
        {
            var names = board.ZoneNames!;
            var natural = names.Select(n => MusicSounds.Resolve(null, board, n, null, set)).ToArray();
            return MusicSounds.Spread(natural, Enumerable.Repeat(true, names.Count).ToArray(), new[] { 1, 8, 8, 8, 9 }, set);
        }
        var bands = Spread(MusicRoleSets.Bands);
        T.Check(bands.SequenceEqual(new[] { MusicRole.Bass, MusicRole.Bass, MusicRole.Mid, MusicRole.Treble, MusicRole.Mid }),
            $"pitch split: the three fan headers are bass / mid / treble, as before ({string.Join(", ", bands)})");
        var band = Spread(MusicRoleSets.Band);
        T.Check(band[1..4].SequenceEqual(new[] { MusicRole.Kick, MusicRole.Snare, MusicRole.Hat }) && band[4] == MusicRole.Vocal,
            $"instruments: fan headers kick / snare / hat, board lighting vocal ({string.Join(", ", band)})");
        var drums = Spread(MusicRoleSets.Drums);
        T.Check(drums[1..4].SequenceEqual(new[] { MusicRole.Kick, MusicRole.Snare, MusicRole.Hat }) && drums[4] == MusicRole.Snare && drums.All(MusicRoles.IsDrum),
            $"drums only: every zone is a drum ({string.Join(", ", drums)})");
        T.Check(MusicSounds.Resolve(null, gpu, null, null, MusicRoleSets.Band) == MusicRole.Vocal && MusicSounds.Resolve(null, ram, null, null, MusicRoleSets.Band) == MusicRole.Hat
            && MusicSounds.Resolve(null, strip, null, null, MusicRoleSets.Band) == MusicRole.Harmony,
            "instruments: GPU → vocal, memory → hi-hat, other strips → harmony");
        T.Check(MusicSounds.Resolve(null, strip, null, null, MusicRoleSets.Drums) == MusicRole.Kick, "drums only: other strips → kick");
        T.Check(MusicSounds.Resolve(MusicSounds.Pad, board, "Addressable Header 1", null, MusicRoleSets.Drums) == MusicRole.Pad
            && MusicSounds.Resolve(MusicSounds.Mid, gpu, null, null, MusicRoleSets.Band) == MusicRole.Mid, "an explicit zone choice wins over the role set");
        var cfg = new RgbConfig { MusicStyle = MusicStyles.BeatHue };
        bool hidden = !MusicRoleSets.InUse(cfg);
        cfg.DeviceSettings.Add(new RgbDeviceSettings { Name = "Board" });
        cfg.DeviceSettings[0].Zones.Add(new RgbZoneSettings { Name = "Header 1", Effect = MusicStyles.Role });
        bool zoneShows = MusicRoleSets.InUse(cfg);
        T.Check(hidden && zoneShows && MusicRoleSets.InUse(new RgbConfig { MusicStyle = MusicStyles.Role }),
            "the role-set choice is shown when the style, a device or a zone uses parts-by-role (and hidden otherwise)");
        T.Check(MusicSounds.Ids.Select(MusicSounds.Label).Distinct().Count() == MusicSounds.Ids.Length && MusicRoleSets.All.Select(MusicRoleSets.Label).Distinct().Count() == 3,
            "every zone sound and role set has its own label");

        // デバイスの描き方にも分け方が届く（分け方を変えたら作り直す）
        var ds = new RgbDeviceSettings { Name = board.Name };
        var r = new RoleRig(board, Role(MusicRoleSets.Bands), ds);
        r.Step(1.0);
        r.Step(1.5);
        float before = r.Avg(17, 8);
        r.In.Music = Role(MusicRoleSets.Band);
        r.Step(2.0);
        r.Step(2.01, AudioEventKind.Hat);
        var lit = r.Levels(17, 8);
        T.Check(lit.Count(v => v > before + 0.25f) is >= 1 and <= 2, $"after switching to instruments, header 3 shows a hi-hat as one or two dots ({string.Join(" ", lit.Select(v => v.ToString("F2")))})");
    }

    /// <summary>
    /// キック・スネア・ハイハットのゾーンは自分の音だけで光る。キックはゾーン全体、スネアは続けて並んだ 2〜3 個（場所はスネアのたびに変わる）、
    /// ハイハットは 1〜2 個の白っぽい粒。ほかのゾーンは明るくならない
    /// </summary>
    public static void TestDrumZonesReactToTheirOwnDrum()
    {
        var ds = Zones("ASRock B860", ("Addressable Header 1", MusicSounds.Kick), ("Addressable Header 2", MusicSounds.Snare), ("Addressable Header 3", MusicSounds.Hat), ("PCB", MusicSounds.Pad));
        var r = new RoleRig(Board(), Role(), ds);
        r.Step(1.0);
        r.Step(2.0);
        float k0 = r.Avg(1, 8), s0 = r.Avg(9, 8), h0 = r.Avg(17, 8), p0 = r.Avg(25, 9);
        r.Step(2.01, AudioEventKind.Kick);
        float k1 = r.Avg(1, 8), s1 = r.Avg(9, 8), h1 = r.Avg(17, 8), p1 = r.Avg(25, 9);
        T.Check(k1 > k0 + 0.3f && r.Levels(1, 8).All(v => v > k0 + 0.25f) && s1 < s0 + 0.03f && h1 < h0 + 0.03f && p1 < p0 + 0.03f,
            $"kick: the whole kick zone lights ({k0:F2} → {k1:F2}), snare/hat/pad stay ({s0:F2}→{s1:F2} {h0:F2}→{h1:F2} {p0:F2}→{p1:F2})");

        r.Step(3.5);
        float sBase = r.Avg(9, 8), kBase = r.Avg(1, 8);
        r.Step(3.51, AudioEventKind.Snare);
        var sl = r.Levels(9, 8);
        var litS = Enumerable.Range(0, 8).Where(i => sl[i] > sBase + 0.2f).ToList();
        bool together = litS.Count >= 2 && litS.Count <= 3 && (litS.Zip(litS.Skip(1), (a, b) => b - a).All(d => d == 1) || litS.Contains(0) && litS.Contains(7));
        T.Check(together && r.Avg(1, 8) < kBase + 0.03f, $"snare: a block of {litS.Count} neighbouring LEDs in the snare zone ({string.Join(",", litS)}), kick zone stays");
        r.Step(4.5);
        float sBase2 = r.Levels(9, 8).Min();
        r.Step(4.51, AudioEventKind.Snare);
        var sl2 = r.Levels(9, 8);
        var litS2 = Enumerable.Range(0, 8).Where(i => sl2[i] > sBase2 + 0.2f).ToList();
        T.Check(litS2.Count >= 2 && !litS2.SequenceEqual(litS), $"the next snare lights another place ({string.Join(",", litS)} → {string.Join(",", litS2)})");

        r.Step(6.0);
        float hBase = r.Avg(17, 8);
        r.Step(6.01, AudioEventKind.Hat);
        var hl = r.Levels(17, 8);
        var dots = Enumerable.Range(0, 8).Where(i => hl[i] > hBase + 0.2f).ToList();
        bool whitish = dots.All(i => { var c = r.R.LastLight[17 + i]; return Math.Min(c.R, Math.Min(c.G, c.B)) > 0.2f * c.MaxChannel; });
        T.Check(dots.Count is >= 1 and <= 2 && whitish, $"hi-hat: {dots.Count} whitish dot(s) in the hi-hat zone");
    }

    /// <summary>
    /// 速い音（16 分のハイハット 1 秒に 8 回・スネアの連打 1 秒に 16 回・キックの連打 1 秒に 8 回）でも、点滅の制限の中に収まる:
    /// 粒になる大きいゾーンは、1 つの LED が光るのは 1 秒に 3 回まで。ゾーン全体が光るキックと小さいゾーンのスネアは、
    /// 「激しい点滅を抑える」がオンなら強い光は 1 秒に 3 回まで（それより詰まったものは 0.35 倍）
    /// </summary>
    public static void TestFastDrumsStayWithinFlashLimits()
    {
        var strip = new RgbDevice(4, "Strip", 24, 4, new[] { 24 }, "HID: strip") { ZoneNames = new[] { "Z" } };
        var small = new RgbDevice(5, "Fan", 4, 4, new[] { 4 }, "HID: fan") { ZoneNames = new[] { "Z" } };
        foreach (var (sound, kind, rate) in new[] { (MusicSounds.Hat, AudioEventKind.Hat, 8.0), (MusicSounds.Snare, AudioEventKind.Snare, 16.0) })
        {
            var r = new RoleRig(strip, Role(reduce: false) with { Intensity = Intensities.Intense }, Zones("Strip", ("Z", sound)));
            var onsets = new int[24];
            var prev = new float[24];
            var rising = new bool[24];
            int frames = 0;
            for (double t = 1; t < 5; t += 1 / 60.0, frames++)
            {
                bool hit = Math.Floor(t * rate) > Math.Floor((t - 1 / 60.0) * rate);
                r.Step(t, hit ? new[] { kind } : Array.Empty<AudioEventKind>());
                var l = r.Levels(0, 24);
                for (int i = 0; i < 24; i++)
                {
                    if (l[i] > prev[i] + 0.15f && !rising[i]) { onsets[i]++; rising[i] = true; }
                    else if (l[i] < prev[i]) rising[i] = false;
                    prev[i] = l[i];
                }
            }
            double worst = onsets.Max() / 4.0;
            T.Check(worst <= 3.01, $"{sound} at {rate}/s on a 24-LED zone (intense, limit off): one LED lights at most {worst:F2}/s (≤ 3)");
        }
        foreach (var (dev, sound, kind) in new[] { (small, MusicSounds.Snare, AudioEventKind.Snare), (strip, MusicSounds.Kick, AudioEventKind.Kick) })
        {
            var r = new RoleRig(dev, Role(reduce: true), Zones(dev.Name, ("Z", sound)));
            int strong = 0;
            float prev = 0;
            for (double t = 1; t < 5; t += 1 / 60.0)
            {
                bool hit = Math.Floor(t * 8) > Math.Floor((t - 1 / 60.0) * 8);
                r.Step(t, hit ? new[] { kind } : Array.Empty<AudioEventKind>());
                float v = r.Avg(0, dev.LedCount);
                if (t > 1.5 && v > prev + 0.3f) strong++;
                prev = v;
            }
            T.Check(strong / 3.5 <= 3.01, $"{sound} at 8/s on the whole {dev.LedCount}-LED zone (reduce flashing): {strong / 3.5:F2} strong flashes/s (≤ 3)");
        }
    }

    /// <summary>
    /// ベース・ボーカル・和音・パッドのゾーンは、それぞれの楽器の音で明るく、ドラムやほかの楽器では明るくならない。
    /// キックのゾーンは、ドラムでだけ光る（合成した音。本物の分析に流す）
    /// </summary>
    public static void TestMelodicRolesFollowTheirInstrument()
    {
        var src = InstrumentSources.Names.ToDictionary(n => n, n => InstrumentSources.Make(n, 10).L);
        src["drums"] = InstrumentRoleRun.Drums(10, bass: false).Buf;
        var cases = new (string Sound, string Own, string[] Others, double Margin)[]
        {
            (MusicSounds.BassLine, "bass", new[] { "drums", "vocal", "brass", "pad" }, 0.15),
            (MusicSounds.Vocal, "vocal", new[] { "drums", "guitar", "pad", "bass" }, 0.15),
            (MusicSounds.Harmony, "guitar", new[] { "drums", "vocal", "bass", "brass" }, 0.15),
            (MusicSounds.Pad, "pad", new[] { "drums", "vocal", "bass", "brass", "guitar" }, 0.12),
            (MusicSounds.Kick, "drums", new[] { "vocal", "guitar", "pad", "brass", "bass" }, 0.2),
        };
        foreach (var (sound, own, others, margin) in cases)
        {
            var m = new Dictionary<string, double>();
            foreach (var name in others.Append(own)) m[name] = InstrumentRoleRun.Mean(InstrumentRoleRun.Run(src[name], sound), 3, 9.5);
            double top = others.Max(o => m[o]);
            T.Check(m[own] >= top + margin,
                $"{sound} zone: brightest on {own} ({m[own]:F2}), others ≤ {top:F2} ({string.Join(" ", others.Select(o => $"{o} {m[o]:F2}"))})");
        }
    }

    /// <summary>分け方「ドラムだけ」: ギター・ボーカル・パッドだけの音では、どのゾーンもほとんど光らない。ドラムでは光る</summary>
    public static void TestDrumsOnlyIgnoresMelody()
    {
        foreach (var name in new[] { "guitar", "vocal", "pad", "brass" })
        {
            var buf = InstrumentSources.Make(name, 10).L;
            double v = InstrumentRoleRun.Mean(InstrumentRoleRun.Run(buf, MusicSounds.Auto, MusicRoleSets.Drums), 3, 9.5);
            double kickZone = InstrumentRoleRun.Mean(InstrumentRoleRun.Run(buf, MusicSounds.Kick), 3, 9.5);
            T.Check(v <= 0.33 && kickZone <= 0.33, $"drums only: {name} alone keeps the zone at its resting glow ({v:F2}, kick zone {kickZone:F2})");
        }
        double drums = InstrumentRoleRun.Mean(InstrumentRoleRun.Run(InstrumentRoleRun.Drums(10, bass: false).Buf, MusicSounds.Auto, MusicRoleSets.Drums), 3, 9.5);
        T.Check(drums >= 0.5, $"drums only: the drum loop lights the zone ({drums:F2})");
    }

    /// <summary>
    /// 更新の遅いデバイス（メモリ・グラボ）の楽器ごとの役割は、一瞬の光を見せず、曲の部分ごとにいちばん目立つ楽器の色だけ:
    /// 16 分のハイハット・キックの連打でもコマごとの明るさの変化は小さく、ボーカルの部分ではボーカルの色、ドラムの部分ではキックの色に寄る
    /// </summary>
    public static void TestSlowDevicesShowOnlySectionColour()
    {
        var ram = new RgbDevice(6, "RAM", 8, RgbDevice.TypeDram, null, "I2C: ram");
        var m = Role(MusicRoleSets.Band);
        var r = new RoleRig(ram, m, new RgbDeviceSettings { Name = ram.Name });
        var pal = ColorPalette.Get(Palettes.Neon, null);
        LinColor Want(MusicRole role) => pal.At(RoleEffect.OffsetOf(role), false);
        static float Dist(LinColor a, LinColor b)
        {
            float ma = Math.Max(a.MaxChannel, 1e-6f), mb = Math.Max(b.MaxChannel, 1e-6f);
            return Math.Abs(a.R / ma - b.R / mb) + Math.Abs(a.G / ma - b.G / mb) + Math.Abs(a.B / ma - b.B / mb);
        }
        double maxStep = 0;
        float prev = -1;
        LinColor vocalCol = default, drumCol = default;
        for (double t = 1; t < 13; t += 1 / 60.0)
        {
            bool vocalPart = t < 7;
            var f = r.In.Frame;
            f.Vocal = vocalPart ? 0.7f : 0f;
            f.HarmMid = vocalPart ? 0.9f : 0.1f;
            f.PercLow = vocalPart ? 0.05f : 0.9f;
            f.PercHigh = vocalPart ? 0.05f : 0.5f;
            f.LoudVU = 0.6f;
            bool hat = !vocalPart && Math.Floor(t * 8) > Math.Floor((t - 1 / 60.0) * 8);
            r.Step(t, hat ? new[] { AudioEventKind.Hat } : Array.Empty<AudioEventKind>());
            float v = r.Avg(0, 8);
            if (prev >= 0 && t > 1.5) maxStep = Math.Max(maxStep, Math.Abs(v - prev));
            prev = v;
            if (Math.Abs(t - 6.9) < 0.01) vocalCol = r.R.LastLight[0];
            if (Math.Abs(t - 12.9) < 0.01) drumCol = r.R.LastLight[0];
        }
        T.Check(maxStep < 0.03, $"slow device: no flashes, the brightness moves at most {maxStep:F3} per frame");
        T.Check(Dist(vocalCol, Want(MusicRole.Vocal)) < Dist(vocalCol, Want(MusicRole.Kick)) && Dist(drumCol, Want(MusicRole.Kick)) < Dist(drumCol, Want(MusicRole.Vocal)),
            $"slow device: vocal part shows the vocal colour ({vocalCol}), drum part the kick colour ({drumCol})");
    }
}

public static class InstrumentRoleDiagTests
{
    /// <summary>調査用: 環境変数 ROLE_DIAG=1 のとき、役割ごとの明るさ（音の多い曲・楽器ごとの音）と、楽器ごとの手がかりの値を表示する</summary>
    public static void TestInstrumentRoleDiag()
    {
        if (Environment.GetEnvironmentVariable("ROLE_DIAG") != "1") return;
        var mixes = DenseMixLight.Mixes().ToList();
        string[] sounds = { MusicSounds.Kick, MusicSounds.Snare, MusicSounds.Hat, MusicSounds.BassLine, MusicSounds.Vocal, MusicSounds.Harmony, MusicSounds.Pad, MusicSounds.Bass, MusicSounds.Mid };
        foreach (var x in mixes)
        foreach (var snd in sounds)
        {
            var r = InstrumentRoleRun.Run(x.G.Buf, snd);
            var (k, s, g, fl) = DenseMixLight.Measure(x, r);
            double late = r.MeanAfter(DenseMixLight.GuitarOnly(mixes.Last()), 0.04, 0.16) - fl;
            T.Info($"{x.Name,-22} {snd,-8} late-guitar +{late:F2} kick +{k - fl:F2} snare +{s - fl:F2} guitar +{(double.IsNaN(g) ? 0 : g - fl):F2} floor {fl:F2} mean {InstrumentRoleRun.Mean(r, 8, 23):F2} rev {r.QuickReversalsPerSec(8, 23):F2}/s");
        }
        foreach (var name in InstrumentSources.Names)
        {
            var (_, l, _, _) = InstrumentSources.Make(name, 10);
            var line = new System.Text.StringBuilder($"{name,-7}");
            foreach (var snd in sounds) line.Append($" {snd} {InstrumentRoleRun.Mean(InstrumentRoleRun.Run(l, snd), 3, 9.5):F2}");
            T.Info(line.ToString());
        }
        // 手がかりの値（平均 / 上から 10%）と出来事の数（1 秒あたり）
        var srcs = InstrumentSources.Names.Select(n => (n, InstrumentSources.Make(n, 10).L)).ToList();
        srcs.Add(("drums", InstrumentRoleRun.Drums(10, bass: false).Buf));
        srcs.Add(("dense", mixes.Last().G.Buf));
        srcs.Add(("sparse", mixes.First().G.Buf));
        foreach (var (n, buf) in srcs)
        {
            var e = new AudioEngine(InstrumentSources.Fs) { Settings = MusicTuning.EngineSettings(new RgbConfig { Mode = RgbModes.Music, Sensitivity = 2 }) };
            var run = new StereoRun(e).Feed(buf);
            var fr = run.Between(3, 9.5).ToList();
            string S(Func<AudioFrame, float> p) { var v = fr.Select(p).OrderBy(z => z).ToArray(); return $"{v.Average():F2}/{v[(int)(0.9 * (v.Length - 1))]:F2}"; }
            var evs = run.Events.Where(ev => ev.T - run.T0 >= 3 && ev.T - run.T0 < 9.5).GroupBy(ev => ev.Kind).Select(gr => $"{gr.Key}:{gr.Count() / 6.5:F1}");
            T.Info($"{n,-7} pL {S(f => f.PercLow)} pM {S(f => f.PercMid)} pH {S(f => f.PercHigh)} bass {S(f => f.BassLevel)} on {S(f => f.BassOnset)} hM {S(f => f.HarmMid)} hH {S(f => f.HarmHigh)} voc {S(f => f.Vocal)} brs {S(f => f.Brass)} gtr {S(f => f.GuitarKeys)} pad {S(f => f.Pad)} hB {S(f => f.HarmBass)}  ev {string.Join(" ", evs)}");
        }
    }
}

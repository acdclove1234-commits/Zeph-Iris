// ==================================================
// StyleMigrationTests.cs - 音楽連動の光り方を 7 種類にしたことのテスト
//   (なくしたパルス・ストロボ・ミックス・エナジー・テンポ追従の置き換え、音に反応しない光り方になった流れる波・ムードの置き換え:
//    全体・デバイス・ゾーン、プリセット、光り方の選び方。なくしたプリセット「映画・ゲーム」の置き換え。7 枚のカードの並べ方)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class StyleMigrationTests
{
    /// <summary>6.15.10 までにあって、音楽連動からなくした光り方と、残しておく音楽連動の光り方の置き換え先</summary>
    private static readonly (string Old, string New)[] Removed =
    {
        ("Pulse", MusicStyles.BeatHue), ("Strobe", MusicStyles.BeatHue), ("Chase", MusicStyles.BeatHue),
        ("Mix", MusicStyles.Spectrum), ("Energy", MusicStyles.Spectrum), ("Scroll", MusicStyles.Spectrum), ("Mood", MusicStyles.Ensemble),
    };

    /// <summary>
    /// デバイス・ゾーンで選んでいた、なくした光り方の置き換え先（光り方を上書きできないとき。ミックス・流れる波・ムードは「自動」
    /// = 全体の光り方に合わせる）
    /// </summary>
    private static readonly (string Old, string New)[] RemovedOverride =
    {
        ("Pulse", MusicStyles.BeatHue), ("Strobe", MusicStyles.BeatHue), ("Chase", MusicStyles.BeatHue), ("Mix", EffectCatalog.Auto),
        ("Energy", MusicStyles.Spectrum), ("Scroll", EffectCatalog.Auto), ("Mood", EffectCatalog.Auto),
    };

    /// <summary>画面の選択欄の値（デバイス: 自動 + 7 種類、ゾーン: デバイスと同じ + 自動 + 7 種類）</summary>
    private static readonly HashSet<string> DeviceMenu = new(MusicStyles.All) { EffectCatalog.Auto };
    private static readonly HashSet<string> ZoneMenu = new(DeviceMenu) { RgbZoneSettings.SameAsDevice };

    /// <summary>選べる光り方は、ちょうどこの 7 種類（なくした光り方は選べない）</summary>
    public static void TestExactlySevenStyles()
    {
        var want = new[] { "BeatHue", "Meter", "Spectrum", "Ripple", "Sparkle", "Role", "Ensemble" };
        T.Check(MusicStyles.All.SequenceEqual(want), $"MusicStyles.All is exactly the 7 kept styles in card order ({string.Join(", ", MusicStyles.All)})");
        foreach (var (old, _) in Removed)
            T.Check(!MusicStyles.IsKnown(old) && !MusicStyles.All.Contains(old), $"{old} can no longer be picked as a music style");
        foreach (var s in MusicStyles.All)
        {
            var (label, description) = MusicTuning.Describe(s);
            T.Check(label != s && description.Length > 0 && MusicTuning.Follows(s).Length > 0, $"{s}: has a name, a description and what it reacts to ({label})");
        }
        foreach (var old in new[] { "Chase", "Scroll", "Mood" })
            T.Check(MusicTuning.Follows(old).Length == 0 && MusicTuning.Describe(old).Description.Length == 0, $"{old}: no music style card text any more");
        // 遅いデバイス用の版も含めて、どの光り方も 7 種類のどれかの名前で表示する
        foreach (EffectVariant v in Enum.GetValues<EffectVariant>())
            T.Check(MusicStyles.IsKnown(EffectCatalog.NameOf(v)), $"{v}: shown with a kept style name ({EffectCatalog.NameOf(v)})");
        T.Check(!Enum.GetNames<EffectVariant>().Any(n => n is "Chase" or "Scroll" or "Mood"), "no Chase / Scroll / Mood music variant is left");
    }

    /// <summary>全体の光り方: なくした光り方は置き換え先に、知らない名前は「ビートで色替え」になる（設定ファイルを読んだとき）</summary>
    public static void TestGlobalStyleMigrates()
    {
        foreach (var (old, now) in Removed)
        foreach (string mode in new[] { RgbModes.Music, RgbModes.Rainbow })
        {
            var cfg = AppConfig.FromJson("{ \"Rgb\": { \"Mode\": \"" + mode + "\", \"MusicStyle\": \"" + old + "\" } }");
            T.Check(cfg.Rgb.MusicStyle == now, $"{mode}: saved global style {old} → {now} ({cfg.Rgb.MusicStyle})");
            T.Check(MusicTuning.Resolve(cfg.Rgb).Effect == now, $"{mode}, {old}: LEDs would draw {now} in music mode");
        }
        T.Check(AppConfig.FromJson("{ \"Rgb\": { \"MusicStyle\": \"NoSuchStyle\" } }").Rgb.MusicStyle == MusicStyles.BeatHue, "unknown global style → BeatHue");
        T.Check(AppConfig.FromJson("{ \"Rgb\": { \"MusicStyle\": \"Ripple\" } }").Rgb.MusicStyle == MusicStyles.Ripple, "kept styles are left as they are");
        // Normalize の前の設定でも、置き換え先で描く
        T.Check(MusicTuning.Resolve(new RgbConfig { MusicStyle = "Mix" }).Effect == MusicStyles.Spectrum, "Resolve maps a removed name too (Mix → Spectrum)");
        T.Check(MusicTuning.Resolve(new RgbConfig { MusicStyle = "Chase" }).Effect == MusicStyles.BeatHue, "Resolve maps Chase → BeatHue");
        T.Check(MusicStyles.Normalize("Strobe") == MusicStyles.BeatHue && MusicStyles.Normalize(null) == MusicStyles.BeatHue
            && MusicStyles.Normalize("Bogus", "") == "" && MusicStyles.Normalize(MusicStyles.Ensemble) == MusicStyles.Ensemble
            && MusicStyles.Normalize("Scroll") == MusicStyles.Spectrum && MusicStyles.Normalize("Mood") == MusicStyles.Ensemble, "MusicStyles.Normalize");
    }

    /// <summary>
    /// 全体で音楽連動の「流れる波」「ムード」を使っていた設定を読む。「ムード」は音に反応しない光り方になる。
    /// 色の組み合わせは、音楽連動で選んでいた色の組み合わせのまま。音楽連動でなかった（使っていなかった）なら、光り方は変えない
    /// </summary>
    public static void TestRemovedWaveAndStandaloneMoodMigration()
    {
        foreach (var (old, mode, style) in new[] { ("Mood", RgbModes.Mood, MusicStyles.Ensemble) })
        {
            var cfg = AppConfig.FromJson("{ \"Rgb\": { \"Mode\": \"Music\", \"MusicStyle\": \"" + old + "\", \"Speed\": 2.5, \"Music\": { \"Palette\": \"Neon\" } } }");
            T.Check(cfg.Rgb.Mode == mode, $"Music + {old} → the standalone {mode} mode ({cfg.Rgb.Mode})");
            T.Check(cfg.Rgb.ModePalette == Palettes.Neon, $"Music + {old}: the new mode keeps the music palette ({cfg.Rgb.ModePalette})");
            T.Check(cfg.Rgb.MusicStyle == style && cfg.Rgb.Music.Palette == Palettes.Neon && cfg.Rgb.Speed == 2.5,
                $"Music + {old}: the music style for later is {style}, palette and speed are kept ({cfg.Rgb.MusicStyle}, {cfg.Rgb.Music.Palette}, {cfg.Rgb.Speed})");
            T.Check(RgbEffects.IsAnimated(mode), $"{mode}: drawn every frame");
            // 保存し直して読んでも同じ（2 回目は何も変えない）
            var again = AppConfig.FromJson(cfg.ToJson());
            T.Check(again.Rgb.Mode == mode && again.Rgb.MusicStyle == style && again.Rgb.ModePalette == Palettes.Neon, $"{old}: a second load keeps the result");

            var other = AppConfig.FromJson("{ \"Rgb\": { \"Mode\": \"Rainbow\", \"MusicStyle\": \"" + old + "\", \"Music\": { \"Palette\": \"Neon\" } } }");
            T.Check(other.Rgb.Mode == RgbModes.Rainbow && other.Rgb.MusicStyle == style && other.Rgb.ModePalette == Palettes.Rainbow,
                $"Rainbow + unused {old}: the mode stays Rainbow, the style becomes {style}, the mode palette is the default ({other.Rgb.Mode}, {other.Rgb.MusicStyle}, {other.Rgb.ModePalette})");
        }
        var wave = AppConfig.FromJson("""{ "Rgb": { "Mode": "Wave", "Speed": 2.5, "StaticColor": "#36A04F", "DeviceSettings": [{ "Name": "strip", "LightMode": "Wave" }] } }""");
        T.Check(wave.Rgb.Mode == RgbModes.Wave && wave.Rgb.Speed == 2.5 && wave.Rgb.StaticColor == "#36A04F"
            && wave.Rgb.DeviceSettings.Single().LightMode == RgbModes.Wave && !RgbModes.All.Contains("FlowWave")
            && !RgbLightModes.Overrides.Contains("FlowWave"), "Wave keeps its speed, colour and device selection, FlowWave is absent from both menus");
        var chase = AppConfig.FromJson("{ \"Rgb\": { \"Mode\": \"Music\", \"MusicStyle\": \"Chase\" } }");
        T.Check(chase.Rgb.Mode == RgbModes.Music && chase.Rgb.MusicStyle == MusicStyles.BeatHue, $"Music + Chase → still music, BeatHue ({chase.Rgb.Mode}, {chase.Rgb.MusicStyle})");
        // 新しい光り方の名前も、そのまま読める。知らない色の組み合わせは「レインボー」
        var fresh = AppConfig.FromJson("{ \"Rgb\": { \"Mode\": \"FlowWave\", \"ModePalette\": \"Bogus\" } }");
        T.Check(fresh.Rgb.Mode == RgbModes.Temperature && fresh.Rgb.ModePalette == Palettes.Rainbow, $"FlowWave falls back to the default, unknown mode palette → Rainbow ({fresh.Rgb.ModePalette})");
        T.Check(new RgbConfig().ModePalette == Palettes.Rainbow, "the mode palette defaults to Rainbow");

        // 6.15.10 のプリセット「チル」（ムード・控えめ・オーシャン）: 光り方はムード（音に反応しない）、音楽連動はチル（楽器で光る・控えめ・オーシャン）のまま
        var old615 = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyPreset(old615, MusicPresets.Chill);
        old615.MusicStyle = "Mood";
        old615.Music.Preset = MusicPresets.Chill;
        var app = new AppConfig { Rgb = old615 };
        app.Normalize();
        T.Check(app.Rgb.Mode == RgbModes.Mood && app.Rgb.ModePalette == Palettes.Ocean && app.Rgb.MusicStyle == MusicStyles.Ensemble
            && app.Rgb.Music.Preset == MusicPresets.Chill,
            $"6.15.10 Chill (Mood): standalone Mood in Ocean; the music side is still the Chill preset ({app.Rgb.Mode}, {app.Rgb.ModePalette}, {app.Rgb.MusicStyle}, {app.Rgb.Music.Preset})");
    }

    /// <summary>
    /// デバイス・ゾーンで選んでいた音楽連動の「流れる波」「ムード」: 全体が音楽連動で、光り方を上書きしていなければ、そのデバイス・ゾーンの
    /// 光り方を音に反応しない「流れる波」「ムード」にする。ゾーンでほかの音楽の光り方を選んでいるデバイス・光り方を上書きしているもの・
    /// 全体が音楽連動でない設定では「自動」。テンポ追従は「ビートで色替え」
    /// </summary>
    public static void TestDeviceAndZoneStandaloneMigration()
    {
        var cfg = AppConfig.FromJson("""
            { "Rgb": { "Mode": "Music", "MusicStyle": "BeatHue", "Music": { "Palette": "Sunset" }, "DeviceSettings": [
                { "Name": "A", "Effect": "Scroll" },
                { "Name": "B", "Effect": "Mood", "LightMode": "Static" },
                { "Name": "C", "Effect": "Scroll", "Zones": [ { "Name": "Z1", "Effect": "Meter" } ] },
                { "Name": "D", "Effect": "Auto", "Zones": [ { "Name": "Z1", "Effect": "Mood" }, { "Name": "Z2", "Effect": "Scroll", "LightMode": "Fire" } ] },
                { "Name": "E", "Effect": "Scroll", "Zones": [ { "Name": "Z1", "Effect": "Mood" }, { "Name": "Z2", "Effect": "Scroll" }, { "Name": "Z3", "Effect": "Device", "Reverse": true } ] },
                { "Name": "F", "Effect": "Chase", "Zones": [ { "Name": "Z1", "Effect": "Chase" } ] }
            ] } }
            """);
        var d = cfg.Rgb.DeviceSettings.ToDictionary(x => x.Name);
        T.Check(cfg.Rgb.Mode == RgbModes.Music && cfg.Rgb.MusicStyle == MusicStyles.BeatHue, "the global music style is left as it is");
        T.Check(d["A"].LightMode == "" && d["A"].Effect == EffectCatalog.Auto, $"device Scroll → Auto ({d["A"].LightMode}, {d["A"].Effect})");
        T.Check(d["B"].LightMode == RgbModes.Static && d["B"].Effect == EffectCatalog.Auto, $"device Mood under a Static override → Auto, Static kept ({d["B"].LightMode}, {d["B"].Effect})");
        T.Check(d["C"].LightMode == "" && d["C"].Effect == EffectCatalog.Auto && d["C"].ZoneFor("Z1")?.Effect == MusicStyles.Meter,
            $"device Scroll with a music zone → Auto, the zone keeps Meter ({d["C"].LightMode}, {d["C"].Effect})");
        var dz1 = d["D"].ZoneFor("Z1");
        var dz2 = d["D"].ZoneFor("Z2");
        T.Check(dz1 is { LightMode: RgbModes.Mood, Effect: RgbZoneSettings.SameAsDevice }, $"zone Mood → zone mode Mood ({dz1?.LightMode}, {dz1?.Effect})");
        T.Check(dz2 is { LightMode: RgbModes.Fire, Effect: EffectCatalog.Auto }, $"zone Scroll under a Fire override → Auto, Fire kept ({dz2?.LightMode}, {dz2?.Effect})");
        T.Check(d["E"].LightMode == "" && d["E"].ZoneFor("Z1")?.LightMode == RgbModes.Mood && d["E"].ZoneFor("Z2")?.Effect == EffectCatalog.Auto
            && d["E"].ZoneFor("Z3")?.Reverse == true,
            "device Scroll with Mood / Scroll / same-as-device zones → Auto; the Mood zone gets Mood; the Scroll zone simply follows the device");
        T.Check(d["F"].Effect == MusicStyles.BeatHue && d["F"].ZoneFor("Z1")?.Effect == MusicStyles.BeatHue && d["F"].LightMode == "",
            "device and zone Chase → BeatHue (still music)");
        T.Check(cfg.Rgb.ModePalette == Palettes.Sunset, $"a device moved to a standalone mode keeps the music palette ({cfg.Rgb.ModePalette})");
        bool listed = cfg.Rgb.DeviceSettings.All(x => DeviceMenu.Contains(x.Effect) && x.Zones.All(z => ZoneMenu.Contains(z.Effect))
            && RgbLightModes.Normalize(x.LightMode) == x.LightMode && x.Zones.All(z => RgbLightModes.Normalize(z.LightMode) == z.LightMode));
        T.Check(listed, "every saved device / zone style and mode is one of the menu choices (no blank ComboBox)");
        var again = AppConfig.FromJson(cfg.ToJson());
        T.Check(again.ToJson() == cfg.ToJson(), "a second load changes nothing");

        // 全体が音楽連動でないとき（デバイスの音楽の光り方は使われていなかった）: 「自動」
        var off = AppConfig.FromJson("""
            { "Rgb": { "Mode": "Rainbow", "Music": { "Palette": "Sunset" }, "DeviceSettings": [
                { "Name": "A", "Effect": "Scroll", "Zones": [ { "Name": "Z1", "Effect": "Mood", "Reverse": true } ] } ] } }
            """);
        var a = off.Rgb.DeviceSettings.Single();
        T.Check(off.Rgb.Mode == RgbModes.Rainbow && a.LightMode == "" && a.Effect == EffectCatalog.Auto && a.ZoneFor("Z1")?.Effect == EffectCatalog.Auto
            && (a.ZoneFor("Z1")?.LightMode ?? "") == "" && off.Rgb.ModePalette == Palettes.Rainbow,
            "not in music mode: device and zone Scroll / Mood → Auto, no mode change");

        // 旧「流れる波」の移行後も、デバイスで選んだムードを保つ
        var both = AppConfig.FromJson("""
            { "Rgb": { "Mode": "Music", "MusicStyle": "Scroll", "DeviceSettings": [ { "Name": "GPU", "Effect": "Mood" }, { "Name": "RAM", "Effect": "Meter" } ] } }
            """);
        var bd = both.Rgb.DeviceSettings.ToDictionary(x => x.Name);
        T.Check(both.Rgb.Mode == RgbModes.Music && both.Rgb.MusicStyle == MusicStyles.Spectrum && bd["GPU"].LightMode == RgbModes.Mood && bd["RAM"].Effect == MusicStyles.Meter && bd["RAM"].LightMode == "",
            "global Scroll + device Mood → global music / Spectrum, the device stays Mood; other device styles are kept for later");
    }

    /// <summary>
    /// 置き換えたら、プリセットは同じ組み合わせのものがあればそれ、なければ「カスタム」。置き換えなければプリセットは変えない
    /// </summary>
    public static void TestPresetAfterMigration()
    {
        var pulse = new RgbConfig();
        MusicTuning.ApplyPreset(pulse, MusicPresets.Chill);
        pulse.MusicStyle = "Pulse";
        var cfg = new AppConfig { Rgb = pulse };
        cfg.Normalize();
        T.Check(cfg.Rgb.MusicStyle == MusicStyles.BeatHue && cfg.Rgb.Music.Preset == MusicPresets.Custom,
            $"Pulse with the Chill values → BeatHue, which no preset uses with those values, so Custom ({cfg.Rgb.MusicStyle}, {cfg.Rgb.Music.Preset})");

        var club = new RgbConfig();
        MusicTuning.ApplyPreset(club, MusicPresets.Club);
        club.MusicStyle = "Strobe";
        club.Music.Preset = MusicPresets.Custom;
        cfg = new AppConfig { Rgb = club };
        cfg.Normalize();
        T.Check(cfg.Rgb.MusicStyle == MusicStyles.BeatHue && cfg.Rgb.Music.Preset == MusicPresets.Club,
            $"Strobe with the Club values → BeatHue, which is the Club preset ({cfg.Rgb.Music.Preset})");

        var chase = new RgbConfig();
        MusicTuning.ApplyPreset(chase, MusicPresets.Groove);
        chase.MusicStyle = "Chase";
        chase.Music.Preset = MusicPresets.Custom;
        cfg = new AppConfig { Rgb = chase };
        cfg.Normalize();
        T.Check(cfg.Rgb.MusicStyle == MusicStyles.BeatHue && cfg.Rgb.Music.Preset == MusicPresets.Groove,
            $"Chase with the Groove values → BeatHue, which is the Groove preset ({cfg.Rgb.Music.Preset})");

        var groove = new RgbConfig();
        MusicTuning.ApplyPreset(groove, MusicPresets.Groove);
        cfg = new AppConfig { Rgb = groove };
        cfg.Normalize();
        T.Check(cfg.Rgb.Music.Preset == MusicPresets.Groove, "no migration → the preset is kept");
    }

    /// <summary>
    /// なくしたプリセット「映画・ゲーム」（どの光り方も映画・ゲームの音に連動するので、分けて選ぶ必要がない）:
    /// 読み込んだとき、強さ・色・反応のしかたの値はそのままで、同じ組み合わせのプリセットがあればそれ、なければ「カスタム」。
    /// 知らないプリセットの名前も同じ
    /// </summary>
    public static void TestRemovedCinemaPreset()
    {
        T.Check(MusicPresets.All.SequenceEqual(new[] { "Chill", "Groove", "Party", "Club", "Visualizer", "Custom" }),
            $"presets: Chill, Groove, Party, Club, Visualizer, Custom ({string.Join(", ", MusicPresets.All)})");
        T.Check(MusicPresets.Label("Cinema") == "Cinema", "Cinema has no label any more");

        // 6.15.11 の途中の「映画・ゲーム」（ムード・控えめ・炎、メリハリ 30%・いちばん暗いとき 40%）: ムードは音に反応しない光り方になった
        var cfg = AppConfig.FromJson("""
            { "Rgb": { "Mode": "Music", "MusicStyle": "Mood", "Music": { "Preset": "Cinema", "Intensity": "Subtle", "Palette": "Fire",
                "ReactionMs": 40, "SmoothnessMs": 500, "Punch": 30, "MinBrightness": 40, "GainDb": 3, "AudioDelayMs": 120 } } }
            """);
        var m = cfg.Rgb.Music;
        T.Check(m.Preset == MusicPresets.Custom, $"saved Cinema → Custom when no preset has those values ({m.Preset})");
        T.Check(cfg.Rgb.MusicStyle == MusicStyles.Ensemble && m.Intensity == Intensities.Subtle && m.Palette == Palettes.Fire
            && m.Punch == 30 && m.MinBrightness == 40 && m.ReactionMs == 40 && m.SmoothnessMs == 500 && m.GainDb == 3 && m.AudioDelayMs == 120,
            $"saved Cinema: intensity, palette and tuning values are kept ({cfg.Rgb.MusicStyle}, {m.Intensity}, {m.Palette}, {m.Punch}%, {m.MinBrightness}%)");
        T.Check(cfg.Rgb.Mode == RgbModes.Mood && cfg.Rgb.ModePalette == Palettes.Fire, $"saved Cinema (Mood): the LEDs show the standalone Mood in Fire ({cfg.Rgb.Mode})");

        // 6.15.10 の「映画・ゲーム」（パルス・控えめ・炎、メリハリ 30%・いちばん暗いとき 40%）: パルスは「ビートで色替え」に、
        // ほかの値はそのまま。同じ組み合わせのプリセットはないので「カスタム」
        var old = AppConfig.FromJson("""
            { "Rgb": { "Mode": "Music", "MusicStyle": "Pulse", "Music": { "Preset": "Cinema", "Intensity": "Subtle", "Palette": "Fire",
                "ReactionMs": 40, "SmoothnessMs": 500, "Punch": 30, "MinBrightness": 40 } } }
            """);
        var om = old.Rgb.Music;
        T.Check(old.Rgb.MusicStyle == MusicStyles.BeatHue && om.Preset == MusicPresets.Custom && om.Intensity == Intensities.Subtle
            && om.Palette == Palettes.Fire && om.Punch == 30 && om.MinBrightness == 40,
            $"saved 6.15.10 Cinema (Pulse): BeatHue, Custom, other values kept ({old.Rgb.MusicStyle}, {om.Preset}, {om.Punch}%, {om.MinBrightness}%)");

        // 値がほかのプリセットと同じなら、そのプリセット
        var chill = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyPreset(chill, MusicPresets.Chill);
        chill.Music.Preset = "Cinema";
        var app = new AppConfig { Rgb = chill };
        app.Normalize();
        T.Check(app.Rgb.Music.Preset == MusicPresets.Chill, $"saved Cinema with the Chill values → Chill ({app.Rgb.Music.Preset})");

        // 知らないプリセットの名前も同じ（「パーティー」と同じ値なら「パーティー」）
        var party = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyPreset(party, MusicPresets.Party);
        party.Music.Preset = "NoSuchPreset";
        app = new AppConfig { Rgb = party };
        app.Normalize();
        T.Check(app.Rgb.Music.Preset == MusicPresets.Party && app.Rgb.MusicStyle == MusicStyles.Ripple,
            $"unknown preset with the Party values → Party ({app.Rgb.Music.Preset})");
        // 保存し直しても「Cinema」は残らない
        T.Check(!AppConfig.FromJson(cfg.ToJson()).ToJson().Contains("Cinema"), "saving again does not write Cinema back");
    }

    /// <summary>
    /// デバイス・ゾーンの光り方（光り方を上書きしないとき）: なくした光り方は置き換え先（ミックス・流れる波・ムードは "Auto"）に、
    /// 知らない名前は "Auto" になる（描くときも自動として描くのと同じ。画面の選択欄にない値が残って、空の欄にならないように）。
    /// 空はデバイスなら "Auto"・ゾーンなら「デバイスと同じ」
    /// </summary>
    public static void TestDeviceAndZoneStylesMigrate()
    {
        foreach (var (old, now) in RemovedOverride)
        {
            // 全体が音楽連動でない（デバイスの光り方にはしない）
            var cfg = AppConfig.FromJson("{ \"Rgb\": { \"Mode\": \"Static\", \"DeviceSettings\": [ { \"Name\": \"Strip\", \"Effect\": \"" + old
                + "\", \"Zones\": [ { \"Name\": \"Header 1\", \"Effect\": \"" + old + "\", \"Reverse\": true } ] } ] } }");
            var d = cfg.Rgb.DeviceSettings.Single();
            T.Check(d.Effect == now, $"device style {old} → {now} ({d.Effect})");
            var z = d.ZoneFor("Header 1");
            T.Check(z is not null && z.Effect == now, $"zone style {old} → {now} ({z?.Effect})");
            T.Check(DeviceMenu.Contains(d.Effect) && z is not null && ZoneMenu.Contains(z.Effect),
                $"{old}: the device and zone ComboBoxes still have the value ({d.Effect}, {z?.Effect})");
            T.Check(MusicStyles.NormalizeOverride(old) == now, $"NormalizeOverride({old}) = {now}");
        }
        var other = AppConfig.FromJson("""
            { "Rgb": { "DeviceSettings": [
                { "Name": "A", "Effect": "Bogus", "Zones": [
                    { "Name": "Z1", "Effect": "Bogus", "Reverse": true }, { "Name": "Z2", "Effect": "Auto" }, { "Name": "Z3", "Effect": "Ripple" },
                    { "Name": "Z4", "Effect": "Device", "Reverse": true }, { "Name": "Z5", "Effect": "", "Reverse": true } ] },
                { "Name": "B", "Effect": "Auto" }, { "Name": "C", "Effect": "Ensemble" }, { "Name": "D", "Effect": "" }
            ] } }
            """);
        var devs = other.Rgb.DeviceSettings.ToDictionary(x => x.Name);
        T.Check(devs["A"].Effect == EffectCatalog.Auto && devs["B"].Effect == EffectCatalog.Auto && devs["D"].Effect == EffectCatalog.Auto,
            "unknown or empty device style → Auto");
        T.Check(devs["C"].Effect == MusicStyles.Ensemble, "kept device style is left as it is");
        var zones = devs["A"].Zones.ToDictionary(x => x.Name);
        T.Check(zones["Z1"].Effect == EffectCatalog.Auto, $"unknown zone style → Auto ({zones["Z1"].Effect})");
        T.Check(zones["Z4"].Effect == RgbZoneSettings.SameAsDevice && zones["Z5"].Effect == RgbZoneSettings.SameAsDevice,
            "empty zone style → same as the device");
        // 描くときも、知らないゾーンの名前は「自動」（全体の光り方）として描く。置き換えたあとも見た目は変わらない
        // （例: デバイスは「波紋」、ゾーンだけ知らない名前 → そのゾーンは全体の光り方のまま。「デバイスと同じ」にはしない）
        foreach (bool slow in new[] { false, true })
            T.Check(EffectCatalog.Resolve(MusicStyles.Ensemble, "Bogus", slow) == EffectCatalog.Resolve(MusicStyles.Ensemble, EffectCatalog.Auto, slow)
                && EffectCatalog.Resolve(MusicStyles.Ensemble, "Bogus", slow) != EffectCatalog.Resolve(MusicStyles.Ensemble, MusicStyles.Ripple, slow),
                $"an unknown zone name draws like Auto on a {(slow ? "slow" : "fast")} device, not like the device's own style");
        T.Check(zones["Z2"].Effect == EffectCatalog.Auto && zones["Z3"].Effect == MusicStyles.Ripple, "zone Auto and kept zone styles are left as they are");

        // どの値も、画面の選択欄（デバイス: 自動 + 7 種類、ゾーン: デバイスと同じ + 自動 + 7 種類）にある
        bool listed = other.Rgb.DeviceSettings.All(x => DeviceMenu.Contains(x.Effect) && x.Zones.All(z => ZoneMenu.Contains(z.Effect)));
        T.Check(listed, "every saved device and zone style is one of the menu choices (no blank ComboBox)");
    }

    /// <summary>描くときも、なくした光り方の名前は置き換え先で描く（遅いデバイスの置き換えも今の光り方と同じ）</summary>
    public static void TestRemovedNamesResolveLikeTheirReplacement()
    {
        foreach (var (old, now) in Removed)
        foreach (bool slow in new[] { false, true })
            T.Check(EffectCatalog.Resolve(old, EffectCatalog.Auto, slow) == EffectCatalog.Resolve(now, EffectCatalog.Auto, slow),
                $"global {old} on a {(slow ? "slow" : "fast")} device draws like {now}");
        foreach (var (old, now) in RemovedOverride)
        foreach (string global in MusicStyles.All)
        foreach (bool slow in new[] { false, true })
            T.Check(EffectCatalog.Resolve(global, old, slow) == EffectCatalog.Resolve(global, now, slow),
                $"per-device {old} with global {global} on a {(slow ? "slow" : "fast")} device draws like {now}");
        T.Check(EffectCatalog.Resolve(MusicStyles.Spectrum, EffectCatalog.Auto, true) == EffectVariant.MixSlow
            && EffectCatalog.NameOf(EffectVariant.MixSlow) == MusicStyles.Spectrum, "Spectrum on slow devices: the slow mix, shown as Spectrum");
        T.Check(EffectCatalog.Resolve(MusicStyles.Meter, EffectCatalog.Auto, true) == EffectVariant.PulseLoudSlow
            && EffectCatalog.NameOf(EffectVariant.PulseLoudSlow) == MusicStyles.Meter, "Meter on slow devices: the loudness pulse, shown as Meter");
        // デバイス・ゾーンで「ミックス」を選んでいた遅いデバイスは「自動」になり、全体の光り方の遅い版で光る
        // （全体がスペクトラムなら、今までと同じ全体で 1 色の遅い版。LED ごとの帯のスペクトラムにはならない）
        T.Check(EffectCatalog.Resolve(MusicStyles.Spectrum, "Mix", true) == EffectVariant.MixSlow
            && EffectCatalog.Resolve(MusicStyles.BeatHue, "Mix", true) == EffectVariant.BeatHueSlow
            && EffectCatalog.Resolve(MusicStyles.Ensemble, "Mix", false) == EffectVariant.Ensemble,
            "explicit per-device Mix follows the global style (the slow look on slow devices, not the full per-LED Spectrum)");
    }

    /// <summary>どのプリセットも 7 種類の光り方を使い、当てたあと MatchingPreset で同じプリセットと分かる。チルは「楽器で光る」の控えめ・オーシャン</summary>
    public static void TestPresetsUseKeptStyles()
    {
        var seen = new HashSet<string>();
        foreach (var preset in MusicPresets.All)
        {
            if (preset == MusicPresets.Custom) continue;
            var cfg = new RgbConfig();
            MusicTuning.ApplyPreset(cfg, preset);
            T.Check(MusicStyles.IsKnown(cfg.MusicStyle), $"{preset}: uses a kept style ({cfg.MusicStyle})");
            T.Check(MusicTuning.MatchingPreset(cfg) == preset, $"{preset}: MatchingPreset finds it again ({MusicTuning.MatchingPreset(cfg)})");
            T.Check(seen.Add($"{cfg.MusicStyle}/{cfg.Music.Intensity}/{cfg.Music.Palette}"), $"{preset}: a combination no other preset uses");
        }
        var chill = new RgbConfig();
        MusicTuning.ApplyPreset(chill, MusicPresets.Chill);
        T.Check(chill.MusicStyle == MusicStyles.Ensemble && chill.Music.Intensity == Intensities.Subtle && chill.Music.Palette == Palettes.Ocean,
            $"Chill: Ensemble, Subtle, Ocean ({chill.MusicStyle}, {chill.Music.Intensity}, {chill.Music.Palette})");
    }

    /// <summary>
    /// 音に反応しない光り方「ムード」: 全体・デバイス・ゾーン・タスクトレイ（RgbModes.All）で選べ、名前があり、
    /// 速さと色の組み合わせを使い、「色」は使わない（色の組み合わせが「選んだ色」のときだけ、その色を使う）
    /// </summary>
    public static void TestStandaloneModesAreListed()
    {
        foreach (var mode in new[] { RgbModes.Mood })
        {
            T.Check(RgbModes.IsKnown(mode) && RgbModes.All.Contains(mode) && RgbLightModes.Overrides.Contains(mode) && RgbLightModes.Normalize(mode) == mode,
                $"{mode}: selectable globally, per device and per zone");
            T.Check(RgbModes.Label(mode) != mode && RgbModes.UsesSpeed(mode) && RgbModes.UsesPalette(mode) && !RgbModes.UsesColor(mode),
                $"{mode}: has a label ({RgbModes.Label(mode)}), uses speed and the mode palette, not the plain colour");
            T.Check(!MusicStyles.IsKnown(mode), $"{mode}: not a music style");
            T.Check(RgbEffects.IsAnimated(mode) && RgbLightModes.IsAnimated(mode), $"{mode}: drawn every frame, also as a device override");
        }
        T.Check(RgbModes.All[^1] == RgbModes.Music && RgbModes.All.Distinct().Count() == RgbModes.All.Length, "Music stays the last mode in the menu, no mode twice");
        T.Check(MusicStyles.StandaloneMode("Scroll") is null && MusicStyles.StandaloneMode("Mood") == RgbModes.Mood
            && MusicStyles.StandaloneMode("Chase") is null && MusicStyles.StandaloneMode(MusicStyles.BeatHue) is null, "StandaloneMode");
    }

    /// <summary>行が欠けないカードの並べ方（AdaptiveWrapPanel の CompleteRows）: 割り切れる列の数を選び、なければ行ごとの枚数をそろえる</summary>
    public static void TestCompleteRowColumns()
    {
        // 10 枚: 入る列の数 → 選ぶ列の数（並べ方の決まりそのもの）
        int[] want = { 1, 2, 2, 2, 5, 5, 5, 5, 5, 10, 10 };
        var got = Enumerable.Range(1, 11).Select(fit => CardGrid.BalancedColumns(10, fit)).ToArray();
        T.Check(got.SequenceEqual(want), $"10 cards: columns for 1..11 fitting columns = {string.Join(" ", got)}");
        T.Check(CardGrid.BalancedColumns(7, 4) == 4 && CardGrid.BalancedColumns(7, 3) == 3 && CardGrid.BalancedColumns(12, 5) == 4
            && CardGrid.BalancedColumns(9, 4) == 3 && CardGrid.BalancedColumns(0, 3) == 1, "other counts: 7 → 4 (4 + 3), 12 → 4 × 3, 9 → 3 × 3");
        var (c3, _) = CardGrid.Layout(1100, 10, 208, double.PositiveInfinity, completeRows: false);
        T.Check(c3 == 5, $"without CompleteRows the old rule still applies ({c3} columns)");
        var (c4, s4) = CardGrid.Layout(100, 10, 208, double.PositiveInfinity, completeRows: true);
        T.Check(c4 == 1 && s4 <= 100, $"narrower than one card: 1 column, no overflow ({s4:F0})");

        // 音楽の表現の 7 枚（ItemMinWidth 176）: 7 列入れば 1 行、4〜6 列なら 4 列で 4 + 3、3 列なら 3 + 2 + 2、2 列しか入らなければ 1 列（7 は 2 で割り切れず、列は入る数の半分まで減らせるため）。カードは幅いっぱい
        int n = MusicStyles.All.Length;
        T.Check(n == 7, $"7 music style cards ({n})");
        foreach (var (width, cols, rows) in new[] { (1300.0, 7, "7"), (1231.0, 4, "4 3"), (900.0, 4, "4 3"), (704.0, 4, "4 3"), (703.0, 3, "3 2 2"), (400.0, 1, "1 1 1 1 1 1 1") })
        {
            var (c, slot) = CardGrid.Layout(width, n, 176, double.PositiveInfinity, completeRows: true);
            string sizes = string.Join(" ", CardGrid.RowSizes(n, c, true));
            T.Check(c == cols && sizes == rows && Math.Abs(slot * c - width) < 1e-6, $"{width} px: {cols} columns, rows {rows} filling the width ({c} × {slot:F0}, rows {sizes})");
        }

        // 行ごとの枚数: 割り切れないときも、行の数は増やさず、各行の差は 1 枚まで（多い行が上）。CompleteRows でなければ左から詰める
        T.Check(CardGrid.RowSizes(7, 3, true).SequenceEqual(new[] { 3, 2, 2 }) && CardGrid.RowSizes(13, 5, true).SequenceEqual(new[] { 5, 4, 4 })
            && CardGrid.RowSizes(10, 5, true).SequenceEqual(new[] { 5, 5 }) && CardGrid.RowSizes(7, 3, false).SequenceEqual(new[] { 3, 3, 1 }),
            "row sizes: 7 in 3 columns → 3 2 2, 13 in 5 → 5 4 4, 10 in 5 → 5 5; without CompleteRows 7 in 3 → 3 3 1");
        bool rowsOk = true, widthOk = true;
        string firstBad = "";
        for (int count = 1; count <= 40; count++)
        for (int fit = 1; fit <= count; fit++)
        {
            int cols = CardGrid.BalancedColumns(count, fit);
            var sizes = CardGrid.RowSizes(count, cols, true);
            bool ok = sizes.Sum() == count && sizes.Max() <= cols && sizes.Max() - sizes.Min() <= 1
                && (count % cols == 0 || sizes.Length == (count + fit - 1) / fit);   // 割り切れないときは、入る列の数で並べたときと同じ行の数
            if (!ok && rowsOk) firstBad = $"{count} cards, {fit} fit → {cols} columns: {string.Join(" ", sizes)}";
            rowsOk &= ok;
            // カードの幅は、入る列の数で等分した幅の 2 倍まで（列は入る数の半分までしか減らさない）
            widthOk &= cols * 2 >= fit && cols <= fit;
        }
        T.Check(rowsOk, $"for 1..40 cards, every row differs by at most one card and the row count does not grow without a divisor {firstBad}");
        T.Check(widthOk, "for 1..40 cards, a card is at most twice as wide as the fitting width");
    }
}

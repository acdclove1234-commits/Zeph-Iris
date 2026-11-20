// ==================================================
// LightingUiTests.cs - ライティング画面が頼りにしている決まりごとのテスト
//   (プリセット・強さの当て方・表示名・デバイス設定の既定値・テンポの倍率・遅いデバイスの判定・イベントの読み方)
//   画面そのもの（WPF）はここでは動かせないので、画面が使う Core 側の約束を確かめる
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class LightingUiTests
{
    /// <summary>プリセットを選ぶと、その名前のまま（「カスタム」にならない）で、使える値だけになる</summary>
    public static void TestApplyPresetKeepsPresetName()
    {
        foreach (var preset in MusicPresets.All)
        {
            if (preset == MusicPresets.Custom) continue;
            var cfg = new RgbConfig();
            MusicTuning.ApplyPreset(cfg, preset);
            T.Check(cfg.Music.Preset == preset, $"{preset}: Preset stays {preset}");
            T.Check(MusicStyles.All.Contains(cfg.MusicStyle), $"{preset}: effect is a known style ({cfg.MusicStyle})");
            T.Check(Intensities.All.Contains(cfg.Music.Intensity), $"{preset}: intensity known");
            T.Check(Palettes.All.Contains(cfg.Music.Palette), $"{preset}: palette known");
        }
    }

    /// <summary>「強さ」を選ぶと、反応の速さ・滑らかさ・メリハリ・いちばん暗いときがその強さの値になる（プリセット名は変えない）</summary>
    public static void TestApplyIntensitySetsProfileValues()
    {
        foreach (var id in Intensities.All)
        {
            var m = new MusicConfig { Preset = MusicPresets.Groove };
            MusicTuning.ApplyIntensity(m, id);
            var p = MusicTuning.Profile(id);
            T.Check(m.Intensity == id, $"{id}: Intensity set");
            T.Check(m.ReactionMs == p.ReactionMs && m.SmoothnessMs == p.SmoothnessMs && m.Punch == p.Punch && m.MinBrightness == p.MinBrightness,
                $"{id}: reaction/smoothness/punch/min brightness follow the profile");
            T.Check(m.Preset == MusicPresets.Groove, $"{id}: ApplyIntensity itself does not touch Preset (the screen marks Custom)");
            // スライダーの範囲（画面の Minimum〜Maximum）に入っている
            T.Check(m.ReactionMs is >= 0 and <= 40 && m.SmoothnessMs is >= 60 and <= 600 && m.Punch is >= 0 and <= 100 && m.MinBrightness is >= 0 and <= 40,
                $"{id}: values inside the slider ranges");
        }
    }

    /// <summary>画面・タスクトレイに出す名前と説明が、すべての選択肢にある</summary>
    public static void TestLabelsExistForAllChoices()
    {
        var labels = new HashSet<string>();
        foreach (var style in MusicStyles.All)
        {
            var (label, description) = MusicTuning.Describe(style);
            T.Check(label.Length > 0 && label != style, $"{style}: has a Japanese label ({label})");
            T.Check(description.Length > 0, $"{style}: has a description");
            // どの音に反応するかを 1 行目に（どの音で光っているのか分からない、という声があった）
            string follows = MusicTuning.Follows(style);
            T.Check(follows.Length > 0 && !follows.Contains('\n') && description.StartsWith(follows + "\n"), $"{style}: the description starts with the sound it follows ({follows})");
            T.Check(labels.Add(label), $"{style}: label is unique");
        }
        foreach (var id in MusicPresets.All) T.Check(MusicPresets.Label(id) != id, $"preset {id} has a label");
        foreach (var id in Intensities.All) T.Check(Intensities.Label(id) != id, $"intensity {id} has a label");
        foreach (var id in Palettes.All) T.Check(Palettes.Label(id) != id, $"palette {id} has a label");
        foreach (var id in IdleEffects.All) T.Check(IdleEffects.Label(id) != id, $"idle {id} has a label");
    }

    /// <summary>
    /// デバイスの設定の既定値。画面は「既定と違うときだけ一覧に入れる」ので、既定値が変わると判定がずれる。
    /// SettingsFor は一覧に追加しない（見るだけで設定が増えない）
    /// </summary>
    public static void TestDeviceSettingsDefaults()
    {
        var s = new RgbDeviceSettings();
        T.Check(s.Effect == "Auto" && s.Mirror is null && !s.Reverse && s.MaxFps == 0 && s.LatencyMs == -1, "RgbDeviceSettings defaults (Auto / null / false / 0 / -1)");
        var cfg = new RgbConfig();
        var got = cfg.SettingsFor("Some Device");
        T.Check(got.Name == "Some Device" && cfg.DeviceSettings.Count == 0, "SettingsFor does not add an entry");
        cfg.DeviceSettings.Add(new RgbDeviceSettings { Name = "A", Effect = MusicStyles.Ripple, Mirror = true });
        T.Check(cfg.SettingsFor("A").Effect == MusicStyles.Ripple && cfg.SettingsFor("A").Mirror == true, "SettingsFor finds the saved entry");
    }

    /// <summary>×2・÷2 のボタンは 0.5 / 1 / 2 だけを使う。保存のときの正規化でもその 3 つにそろう</summary>
    public static void TestTempoFactorSnaps()
    {
        foreach (var (input, expected) in new[] { (0.5, 0.5), (1.0, 1.0), (2.0, 2.0), (0.3, 0.5), (0.9, 1.0), (1.6, 2.0), (4.0, 2.0) })
        {
            var m = new MusicConfig { TempoFactor = input };
            m.Normalize();
            T.Near(m.TempoFactor, expected, 1e-9, $"TempoFactor {input} → {expected}");
        }
    }

    /// <summary>「更新が遅いデバイス」の印（I2C・SMBus・GPU・メモリ）</summary>
    public static void TestSlowDeviceBadge()
    {
        T.Check(new RgbDevice(0, "ASRock B650E", 120, 0, null, "HID: \\\\?\\hid#vid_26ce").IsSlow == false, "USB HID motherboard is fast");
        T.Check(new RgbDevice(1, "NVIDIA GeForce RTX 4070", 1, RgbDevice.TypeGpu).IsSlow, "GPU type is slow");
        T.Check(new RgbDevice(2, "Kingston Fury DDR5", 12, RgbDevice.TypeDram).IsSlow, "DRAM type is slow");
        T.Check(new RgbDevice(3, "Some Controller", 8, 6, null, "I2C: PIIX4, address 0x58").IsSlow, "I2C location is slow");
        T.Check(new RgbDevice(3, "Some Controller", 8, 6, null, "I2C: PIIX4").DefaultLatencyMs == 20, "I2C default latency 20 ms");
    }

    /// <summary>
    /// 画面のビートのランプは「前回より新しい通し番号の出来事」だけを数える（時刻の窓が重なっても二重に数えない）
    /// </summary>
    public static void TestEventIdsForBeatLamp()
    {
        var ring = new EventRing();
        long first = ring.Add(AudioEventKind.Kick, 1.00, 2f);
        ring.Add(AudioEventKind.Hat, 1.05, 1.5f);
        long last = ring.Add(AudioEventKind.Kick, 1.10, 1.8f);
        var seen = new List<AudioEvent>();
        ring.Between(-1, 2, seen);
        T.Check(seen.Count == 3 && seen[0].Id == first && seen[^1].Id == last, "Between returns oldest first with increasing ids");

        long cursor = first; // ランプが 1 個目まで読んだところ
        int newKicks = 0;
        foreach (var e in seen)
        {
            if (e.Id <= cursor) continue;
            cursor = e.Id;
            if (e.Kind == AudioEventKind.Kick) newKicks++;
        }
        T.Check(newKicks == 1 && cursor == last, "only events after the cursor are counted");
        T.Check(ring.LastId == last, "LastId skips old events when the engine changes");
    }
}

// ==================================================
// LightingHistoryTests.cs - ライティングの「元に戻す・やり直す」（LightingHistory）のテスト
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class LightingHistoryTests
{
    public static void TestUndoBringsBackTheResetValues()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        MusicTuning.ApplyPreset(cfg, MusicPresets.Groove);
        cfg.Sensitivity = 2;
        cfg.Music.Punch = 70;
        cfg.Music.Preset = MusicPresets.Custom;
        var h = new LightingHistory(cfg);

        MusicTuning.ResetTuning(cfg); // 「初期値に戻す」
        T.Check(h.Checkpoint(cfg), "the reset is one step");
        T.Check(h.CanUndo && !h.CanRedo, "undo is available after the reset");

        T.Check(h.Undo(cfg, out bool lists) && !lists, "undo works (no device lists touched)");
        T.Check(cfg.Sensitivity == 2 && cfg.Music.Punch == 70, $"the custom values are back ({cfg.Sensitivity}, {cfg.Music.Punch})");
        T.Check(cfg.Music.Preset == MusicPresets.Custom, $"the preset name is back ({cfg.Music.Preset})");
        T.Check(!h.CanUndo && h.CanRedo, "redo is available after undo");

        T.Check(h.Redo(cfg, out _), "redo works");
        T.Check(cfg.Sensitivity == 1 && cfg.Music.Punch == 55 && cfg.Music.Preset == MusicPresets.Groove, "redo applies the reset again");
        T.Check(!h.Checkpoint(cfg), "undo and redo do not add steps of their own");
    }

    public static void TestUnsavedChangeIsAStepAndNewChangeClearsRedo()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        var h = new LightingHistory(cfg);
        cfg.Brightness = 60; // まだ Checkpoint していない変更（保存の予約中）
        T.Check(h.Undo(cfg, out _), "an unsaved change can be undone right away");
        T.Check(cfg.Brightness == 100, $"brightness is back ({cfg.Brightness})");

        cfg.MusicStyle = MusicStyles.Ripple;
        h.Checkpoint(cfg);
        T.Check(!h.CanRedo, "a new change clears redo");
    }

    public static void TestKeptValuesAreNotUndone()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music, Enabled = true };
        cfg.Music.ReduceFlashing = false;
        cfg.MusicStyle = MusicStyles.Ripple;
        var h = new LightingHistory(cfg);

        cfg.MusicStyle = MusicStyles.BeatHue;
        cfg.Music.ReduceFlashing = true; // 確認を通してオンにした安全の設定
        cfg.Enabled = false;             // 連携のオン・オフ
        cfg.Port = 6800;
        h.Checkpoint(cfg);

        T.Check(h.Undo(cfg, out _), "undo works");
        T.Check(cfg.Music.ReduceFlashing, "reduce flashing stays on (not turned off without the warning)");
        T.Check(cfg.MusicStyle == MusicStyles.Ripple, $"the style is undone ({cfg.MusicStyle})");
        T.Check(!cfg.Enabled && cfg.Port == 6800, "the connection settings are kept");

        // 戻さない値だけを変えても、元に戻す段は増えない
        var h2 = new LightingHistory(cfg);
        cfg.LightsOff = true;
        cfg.Music.ReduceFlashing = false;
        T.Check(!h2.Checkpoint(cfg), "changing only kept values adds no step");
    }

    public static void TestDeviceListsAreUndone()
    {
        var cfg = new RgbConfig { Mode = RgbModes.Music };
        var h = new LightingHistory(cfg);
        cfg.Devices = new List<string> { "Strip" };
        cfg.DeviceSettings.Add(new RgbDeviceSettings { Name = "Strip", Mirror = true, Reverse = true });
        h.Checkpoint(cfg);

        T.Check(h.Undo(cfg, out bool lists) && lists, "undo reports that the device lists changed");
        T.Check(cfg.Devices.Count == 0 && cfg.DeviceSettings.Count == 0, "device selection and per-device settings are back");
        T.Check(h.Redo(cfg, out lists) && lists && cfg.Devices.SequenceEqual(new[] { "Strip" })
                && cfg.DeviceSettings.Single().Reverse, "redo puts them back");
    }

    public static void TestHistoryIsLimited()
    {
        var cfg = new RgbConfig();
        var h = new LightingHistory(cfg);
        for (int i = 0; i < LightingHistory.Limit + 10; i++)
        {
            cfg.Speed = 1 + i * 0.01;
            h.Checkpoint(cfg);
        }
        int n = 0;
        while (h.Undo(cfg, out _)) n++;
        T.Check(n == LightingHistory.Limit, $"keeps the last {LightingHistory.Limit} steps ({n})");
    }
}

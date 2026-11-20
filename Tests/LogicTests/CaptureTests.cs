// ==================================================
// CaptureTests.cs - 音の取り込みまわりのテスト (形式の変換・音の時刻の計算・分析結果の記録)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Buffers.Binary;
using ProFanController;

namespace LogicTests;

public static class CaptureTests
{
    public static void TestPcmDetect()
    {
        T.Check(PcmConvert.Detect(PcmConvert.EncodingIeeeFloat, 32, null) == PcmKind.Float32, "IEEE float 32");
        T.Check(PcmConvert.Detect(PcmConvert.EncodingExtensible, 32, PcmConvert.SubTypeIeeeFloat) == PcmKind.Float32, "extensible float");
        T.Check(PcmConvert.Detect(PcmConvert.EncodingExtensible, 32, PcmConvert.SubTypePcm) == PcmKind.Int32, "extensible PCM 32 is integer, not float");
        T.Check(PcmConvert.Detect(PcmConvert.EncodingExtensible, 24, PcmConvert.SubTypePcm) == PcmKind.Int24, "extensible PCM 24");
        T.Check(PcmConvert.Detect(PcmConvert.EncodingPcm, 16, null) == PcmKind.Int16, "PCM 16");
        T.Check(PcmConvert.Detect(PcmConvert.EncodingPcm, 8, null) == PcmKind.Unsupported, "PCM 8 unsupported");
    }

    public static void TestPcmToMono()
    {
        var dst = new float[8];
        // float 2ch: (0.5, -0.5) → 0, (1, 0.5) → 0.75
        var f = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(f.AsSpan(0), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(f.AsSpan(4), -0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(f.AsSpan(8), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(f.AsSpan(12), 0.5f);
        int n = PcmConvert.ToMono(f, PcmKind.Float32, 2, dst);
        T.Check(n == 2 && dst[0] == 0 && Math.Abs(dst[1] - 0.75f) < 1e-6, "float stereo → mono average");

        var s = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(s.AsSpan(0), 16384);
        BinaryPrimitives.WriteInt16LittleEndian(s.AsSpan(2), -32768);
        n = PcmConvert.ToMono(s, PcmKind.Int16, 1, dst);
        T.Check(n == 2 && Math.Abs(dst[0] - 0.5f) < 1e-4 && Math.Abs(dst[1] + 1f) < 1e-6, "int16 scale");

        // 24bit: 0x400000 = +0.5, 0x800000 = -1
        var b24 = new byte[] { 0x00, 0x00, 0x40, 0x00, 0x00, 0x80 };
        n = PcmConvert.ToMono(b24, PcmKind.Int24, 1, dst);
        T.Check(n == 2 && Math.Abs(dst[0] - 0.5f) < 1e-6 && Math.Abs(dst[1] + 1f) < 1e-6, $"int24 sign and scale ({dst[0]}, {dst[1]})");

        var b32 = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(b32.AsSpan(0), int.MaxValue / 2);
        BinaryPrimitives.WriteInt32LittleEndian(b32.AsSpan(4), int.MinValue);
        n = PcmConvert.ToMono(b32, PcmKind.Int32, 1, dst);
        T.Check(n == 2 && Math.Abs(dst[0] - 0.5f) < 1e-6 && Math.Abs(dst[1] + 1f) < 1e-6, "int32 scale");

        n = PcmConvert.ToMono(new byte[7], PcmKind.Float32, 2, dst);
        T.Check(n == 0, "partial frame is ignored");
        n = PcmConvert.ToMono(new byte[4 * 2 * 20], PcmKind.Float32, 2, dst);
        T.Check(n == dst.Length, "never writes past the destination");
    }

    public static void TestSampleClock()
    {
        // 48 kHz、10 ミリ秒（480 個）ずつ、受け取りの遅れが 0〜8 ミリ秒ばらつく
        var clock = new SampleClock(48000);
        var rng = new Random(1);
        double start = 100.0;
        double maxErr = 0;
        for (int k = 0; k < 400; k++)
        {
            double trueEnd = start + (k + 1) * 0.010;          // この塊の最後の音が鳴り終わった時刻
            double arrival = trueEnd + rng.NextDouble() * 0.008; // 受け取りは少し遅れる
            double tFirst = clock.Observe(480, arrival);
            double trueFirst = start + k * 0.010;
            if (k > 50) maxErr = Math.Max(maxErr, Math.Abs(tFirst - trueFirst));
        }
        T.Info($"時刻の誤差（最大）: {maxErr * 1000:F2} ms");
        T.Check(maxErr < 0.0015, "sample clock removes arrival jitter (< 1.5 ms)");

        // 無音で進めたあと、本物の音が届く
        double tSil = clock.AdvanceSilence(480, start + 4.02);
        T.Check(tSil > start + 3.99 && tSil < start + 4.02, $"silence continues the timeline ({tSil - start:F4})");
        T.Check(clock.LatestTime > tSil, "latest time advances with silence");
    }

    public static void TestFeatureRingInterpolation()
    {
        var ring = new FeatureRing();
        var f = new AudioFrame();
        for (int i = 0; i < 10; i++)
        {
            f.T = i * 0.01;
            f.Bass = i / 10f;
            f.Band[3] = i;
            ring.Write(f);
        }
        var dst = new AudioFrame();
        ring.Sample(0.045, dst);
        T.Check(Math.Abs(dst.Bass - 0.45f) < 1e-4 && Math.Abs(dst.Band[3] - 4.5f) < 1e-4 && !dst.Extrapolated, "interpolates between frames");
        ring.Sample(1.0, dst);
        T.Check(Math.Abs(dst.Bass - 0.9f) < 1e-6 && dst.Extrapolated, "future time returns newest, flagged");
        ring.Sample(-1, dst);
        T.Check(dst.Bass == 0 && !dst.Extrapolated, "too old returns oldest");
        for (int i = 10; i < 300; i++) { f.T = i * 0.01; f.Bass = 0.5f; ring.Write(f); }
        T.Check(ring.Count == FeatureRing.Capacity && Math.Abs(ring.LatestT - 2.99) < 1e-9, "ring keeps the newest 128");
    }

    public static void TestEventRingCursor()
    {
        var ring = new EventRing();
        var got = new List<AudioEvent>();
        long cursor = 0;
        ring.Add(AudioEventKind.Kick, 1.00, 2);
        ring.Add(AudioEventKind.Hat, 1.05, 1.5f);
        ring.Read(ref cursor, 1.02, got);
        T.Check(got.Count == 1 && got[0].Kind == AudioEventKind.Kick, "reads only events up to the evaluation time");
        got.Clear();
        // 検出が遅れて、すでに通り過ぎた時刻の出来事が後から追加されても読み落とさない
        ring.Add(AudioEventKind.Snare, 1.01, 1.3f);
        ring.Read(ref cursor, 1.10, got);
        T.Check(got.Count == 2 && got[0].Kind == AudioEventKind.Hat && got[1].Kind == AudioEventKind.Snare, "late-detected events are not missed");
        got.Clear();
        ring.Read(ref cursor, 2, got);
        T.Check(got.Count == 0, "nothing read twice");
        for (int i = 0; i < 200; i++) ring.Add(AudioEventKind.Onset, 3 + i * 0.01, 1);
        ring.Read(ref cursor, 10, got);
        T.Check(got.Count == EventRing.Capacity, "overwritten events are skipped safely");
    }

    public static void TestBeatStateMath()
    {
        var b = new BeatState(0.5, 10.0, 100, 0.9, true, 0);
        T.Near(b.Phase(10.25), 0.5, 1e-9, "phase half way");
        T.Check(b.Index(10.75) == 101 && b.Index(9.9) == 99, "index forward and backward");
        T.Check(b.Crossed(10.4, 10.6) && !b.Crossed(10.6, 10.9), "crossed beat boundary");
        T.Near(b.NextBeatAfter(10.6), 11.0, 1e-9, "next beat");
        T.Check(b.Bar(10.0) == 0 && b.Bar(10.5) == 1 && b.Bar(12.0) == 0, "bar position");
        T.Check(!BeatState.None.HasTempo && !BeatState.None.Crossed(0, 100), "no tempo → never crosses");
    }

    public static void TestMusicConfigNormalizeAndPresets()
    {
        var cfg = new AppConfig();
        cfg.Rgb.Music.GainDb = 99;
        cfg.Rgb.Music.Palette = "nope";
        cfg.Rgb.Music.TempoFactor = 1.9;
        cfg.Rgb.MusicStyle = "Weird";
        cfg.Rgb.DeviceSettings.Add(new RgbDeviceSettings { Name = "GPU", MaxFps = 1000 });
        cfg.Rgb.DeviceSettings.Add(new RgbDeviceSettings { Name = "GPU", Reverse = true });
        cfg.Normalize();
        T.Check(cfg.Rgb.Music.GainDb == 12 && cfg.Rgb.Music.Palette == Palettes.Rainbow && cfg.Rgb.Music.TempoFactor == 2.0, "music settings clamped");
        T.Check(cfg.Rgb.MusicStyle == MusicStyles.BeatHue, "unknown effect falls back");
        T.Check(cfg.Rgb.DeviceSettings.Count == 1 && cfg.Rgb.DeviceSettings[0].Reverse, "duplicate device settings merged (last wins)");

        MusicTuning.ApplyPreset(cfg.Rgb, MusicPresets.Party);
        T.Check(cfg.Rgb.MusicStyle == MusicStyles.Ripple && cfg.Rgb.Music.Intensity == Intensities.High && cfg.Rgb.Music.Preset == MusicPresets.Party, "preset applies effect + intensity");
        var r = MusicTuning.Resolve(cfg.Rgb);
        T.Check(r.ColorStepBeats == 1 && r.FlashAmp > 0.8 && !r.DownbeatAccent, "resolved intensity values");
        cfg.Rgb.Music.Intensity = Intensities.Intense;
        T.Check(MusicTuning.Resolve(cfg.Rgb).DownbeatAccent, "downbeat accent only when intense");
        var es = MusicTuning.EngineSettings(cfg.Rgb);
        T.Check(es.Punch is >= 0 and <= 1 && es.AutoBalance is >= 0 and <= 1, "engine settings use 0..1 fractions");
    }
}

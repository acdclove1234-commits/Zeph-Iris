// ==================================================
// PnyDirectTests.cs - PNY Epic-X のグラボへ直接書き込む処理のロジックテスト
//                     (1 色のゾーンは 1 回で塗る・変わった LED だけ書く・LED の番号・失敗したら書き直す・速いデバイス扱い)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class PnyDirectTests
{
    /// <summary>書いた内容を覚える偽物の I2C（fail を true にすると書き込みに失敗する）</summary>
    private sealed class FakeBus : II2cBlockWriter
    {
        public readonly List<(byte Reg, byte[] Data)> Log = new();
        public bool Fail;

        public bool WriteBlock(byte register, ReadOnlySpan<byte> data)
        {
            Log.Add((register, data.ToArray()));
            return !Fail;
        }
    }

    private static Rgb[] Uniform(Rgb c) => Enumerable.Repeat(c, PnyEpicXEncoder.LedCount).ToArray();

    private static int Apply(PnyEpicXEncoder enc, Rgb[] frame, FakeBus bus, bool full = false)
    {
        bus.Log.Clear();
        int writes = 0;
        T.Check(enc.Apply(frame, full, bus, ref writes), "apply succeeds");
        return writes;
    }

    private static RgbDevice Pny(string name = "PNY GeForce RTX 5070Ti ARGB Epic-X OC", int leds = 41, int[]? zones = null,
        string location = "I2C: Nvidia NvAPI I2C on GPU 0, address 0x60") =>
        new(0, name, leds, RgbDevice.TypeGpu, zones ?? new[] { 4, 20, 17 }, location, "Direct", 8);

    public static void TestUniformFrameIsOneWritePerZone()
    {
        var enc = new PnyEpicXEncoder();
        var bus = new FakeBus();
        var red = new Rgb(255, 0, 0);
        T.Check(Apply(enc, Uniform(red), bus) == 2, $"one colour: 2 writes ({bus.Log.Count})");
        T.Check(bus.Log.Select(l => l.Reg).OrderBy(r => r).SequenceEqual(new byte[] { 0x02, 0x04 }), "writes the ARROW and FRONT zones");
        T.Check(bus.Log.All(l => l.Data.SequenceEqual(new byte[] { 0x01, 0xFF, 0x00, 0x00, 255, 0, 0 })), "static mode command [01 FF 00 00 R G B]");
        T.Check(Apply(enc, Uniform(red), bus) == 0, "same colour again: nothing written");
        T.Check(Apply(enc, Uniform(red), bus, full: true) == 2, "full refresh: written again");
        T.Check(Apply(enc, Uniform(new Rgb(0, 0, 255)), bus) == 2, "new colour: 2 writes");
    }

    public static void TestMixedZoneWritesOnlyChangedLeds()
    {
        var enc = new PnyEpicXEncoder();
        var bus = new FakeBus();
        var blue = new Rgb(0, 0, 255);
        Apply(enc, Uniform(blue), bus);
        var frame = Uniform(blue);
        frame[30] = new Rgb(0, 255, 0);   // 矢印の 6 番（矢印は 24 番目から）
        int w = Apply(enc, frame, bus);
        T.Check(w == 1 + 17, $"arrow becomes mixed: mode + 17 LEDs ({w}); front untouched");
        T.Check(bus.Log[0].Reg == 0x02 && bus.Log[0].Data[0] == 0x09 && bus.Log[0].Data[2] == 0x00, "switches ARROW to direct mode first");
        T.Check(bus.Log.Any(l => l.Reg == 0x02 && l.Data.SequenceEqual(new byte[] { 0x09, 0xFF, 6, 0x00, 0, 255, 0 })), "arrow LED 6 green");
        frame[31] = new Rgb(10, 20, 30);
        w = Apply(enc, frame, bus);
        T.Check(w == 1 && bus.Log[0].Data.SequenceEqual(new byte[] { 0x09, 0xFF, 7, 0x00, 10, 20, 30 }), $"one more LED changed: 1 write ({w})");
        T.Check(Apply(enc, Uniform(blue), bus) == 1, "arrow back to one colour: 1 static write (front unchanged)");
    }

    /// <summary>
    /// 書き込みの CPU を抑える: 見て分からない小さな変化は書かない・送る回数はふだん 1 秒に 10 回・書き込みが休みなく続かない
    /// （直接書き込みのグラボで、音楽連動のとき毎コマ 41 個を書き、アプリが 1 コア近くを使っていた）
    /// </summary>
    public static void TestDirectWritesStayLight()
    {
        var enc = new PnyEpicXEncoder();
        var bus = new FakeBus();
        var frame = Uniform(new Rgb(0, 0, 200));
        frame[30] = new Rgb(0, 200, 0);
        Apply(enc, frame, bus);
        // 矢印のゾーン全体が 2 段だけ暗くなる（残光が少しずつ消えるときなど）
        var dim = (Rgb[])frame.Clone();
        for (int i = 24; i < 41; i++) dim[i] = new Rgb((byte)Math.Max(0, frame[i].R - 2), (byte)Math.Max(0, frame[i].G - 2), (byte)Math.Max(0, frame[i].B - 2));
        T.Check(Apply(enc, dim, bus) == 0, "changes of 2 levels or less are not written");
        var dimmer = (Rgb[])frame.Clone();
        for (int i = 24; i < 41; i++) dimmer[i] = new Rgb((byte)Math.Max(0, frame[i].R - 3), (byte)Math.Max(0, frame[i].G - 3), (byte)Math.Max(0, frame[i].B - 3));
        T.Check(Apply(enc, dimmer, bus) == 17, "once the change passes 2 levels the LEDs are written");
        T.Check(Apply(enc, dimmer, bus, full: true) > 0, "the full refresh still writes everything");

        T.Check(RgbDeviceChannel.MaxFpsFor(Pny() with { DirectGpu = true }, new RgbDeviceSettings()) == RgbDeviceChannel.DirectGpuMaxFps
                && RgbDeviceChannel.DirectGpuMaxFps <= 12, "a directly written GPU sends about 10 times a second by default (beats still go out early)");
        T.Check(PnyEpicXGpu.RestMs(35) == 35 && PnyEpicXGpu.RestMs(0) == 0 && PnyEpicXGpu.RestMs(500) == 100,
            "after a 35 ms write the writer rests 35 ms (writes at most half the time), capped at 100 ms");
    }

    public static void TestLogoAndFrontLedNumbers()
    {
        var enc = new PnyEpicXEncoder();
        var bus = new FakeBus();
        var frame = Uniform(Rgb.Black);
        frame[0] = new Rgb(1, 1, 1);    // ロゴの最初 → FRONT の 20 番
        frame[4] = new Rgb(2, 2, 2);    // 前面の最初 → FRONT の 0 番
        frame[23] = new Rgb(3, 3, 3);   // 前面の最後 → FRONT の 19 番
        Apply(enc, frame, bus);
        byte? LedOf(byte v) => bus.Log.FirstOrDefault(l => l.Reg == 0x04 && l.Data[0] == 0x09 && l.Data[4] == v).Data?[2];
        T.Check(LedOf(1) == 20, $"logo LED 0 → FRONT index 20 ({LedOf(1)})");
        T.Check(LedOf(2) == 0, $"front LED 0 → FRONT index 0 ({LedOf(2)})");
        T.Check(LedOf(3) == 19, $"front LED 19 → FRONT index 19 ({LedOf(3)})");
        T.Check(bus.Log.Count(l => l.Reg == 0x04) == 1 + 24, "first mixed frame writes the mode and all 24 FRONT LEDs");
    }

    public static void TestFailureForcesRewrite()
    {
        var enc = new PnyEpicXEncoder();
        var bus = new FakeBus { Fail = true };
        int writes = 0;
        T.Check(!enc.Apply(Uniform(new Rgb(9, 9, 9)), false, bus, ref writes), "failed write is reported");
        bus.Fail = false;
        T.Check(Apply(enc, Uniform(new Rgb(9, 9, 9)), bus) >= 1, "after a failure the same colour is written again");
    }

    public static void TestFallsBackAfterRepeatedFailures()
    {
        var bus = new FakeBus { Fail = true };
        var gpu = new PnyEpicXGpu(bus, "test");
        var frame = Uniform(new Rgb(1, 2, 3));
        for (int i = 0; i < PnyEpicXGpu.FallbackAfterFailures - 1; i++)
        {
            gpu.Submit(frame);
            T.Check(gpu.Flush(2000), "frame written in time");
        }
        T.Check(!gpu.Faulted, "keeps trying before the limit");
        gpu.Submit(frame);
        gpu.Flush(2000);
        T.Check(gpu.Faulted, $"gives up after {PnyEpicXGpu.FallbackAfterFailures} failures in a row");
        int n = bus.Log.Count;
        gpu.Submit(frame);
        gpu.Flush(2000);
        T.Check(bus.Log.Count == n, "no more I2C writes after falling back to OpenRGB");
    }

    public static void TestMeasuresWriteTime()
    {
        var gpu = new PnyEpicXGpu(new FakeBus(), "test");
        T.Check(gpu.AverageWriteMs is null, "nothing measured before the first write");
        gpu.Submit(Uniform(new Rgb(5, 6, 7)));
        gpu.Flush(2000);
        T.Check(gpu.AverageWriteMs is double ms && ms >= 0, $"write time measured ({gpu.AverageWriteMs})");
    }

    public static void TestCandidateDetection()
    {
        T.Check(PnyEpicXGpu.IsCandidate(Pny(), out int idx) && idx == 0, "5070 Ti Epic-X on NvAPI GPU 0 is a candidate");
        T.Check(PnyEpicXGpu.IsCandidate(Pny(location: "I2C: Nvidia NvAPI I2C on GPU 1, address 0x60"), out idx) && idx == 1, "GPU index is taken from the location");
        T.Check(!PnyEpicXGpu.IsCandidate(Pny(name: "PNY GeForce RTX 5090 ARGB Epic-X OC"), out _), "large variant (5090) is not");
        T.Check(PnyEpicXGpu.IsVerified(Pny()) && PnyEpicXGpu.Allowed(Pny(), allowUnverified: false), "the verified 5070 Ti is written directly by default");
        var other = Pny(name: "PNY GeForce RTX 5070 ARGB Epic-X OC");
        T.Check(PnyEpicXGpu.IsCandidate(other, out _) && !PnyEpicXGpu.Allowed(other, allowUnverified: false) && PnyEpicXGpu.Allowed(other, allowUnverified: true),
            "an unverified model with the same layout goes through OpenRGB unless the user opts in");
        T.Check(!PnyEpicXGpu.IsCandidate(Pny(zones: new[] { 41 }), out _), "unexpected zones are not");
        T.Check(!PnyEpicXGpu.IsCandidate(Pny(location: "I2C: Nvidia NvAPI I2C on GPU 0, address 0x49"), out _), "other addresses are not");
        T.Check(!PnyEpicXGpu.IsCandidate(Pny(name: "ASUS GeForce RTX 5070Ti"), out _), "other vendors are not");
    }

    public static void TestDirectGpuIsTreatedAsFast()
    {
        var viaOpenRgb = Pny();
        var direct = Pny() with { DirectGpu = true };
        T.Check(viaOpenRgb.IsSlow && viaOpenRgb.IsGpu && viaOpenRgb.IsLowWrite, "through OpenRGB: slow, low-write GPU");
        T.Check(!direct.IsSlow && !direct.IsGpu && !direct.IsLowWrite, "direct: not slow, no GPU write limits");
        T.Check(direct.DefaultLatencyMs == 8, $"direct: latency 8 ms ({direct.DefaultLatencyMs})");
        var settings = new RgbDeviceSettings { Name = direct.Name };
        // 毎コマ（60 回）書くと、NvAPI の I2C で CPU を使い続けるので、ふだんは 10 回（ビートの瞬間は早めに送る）
        T.Check(RgbDeviceChannel.MaxFpsFor(direct, settings) == RgbDeviceChannel.DirectGpuMaxFps, "direct: about 10 sends per second by default");
        T.Check(RgbDeviceChannel.MaxFpsFor(direct, new RgbDeviceSettings { Name = direct.Name, MaxFps = 30 }) == 30, "direct: the user can raise it");
        T.Check(RgbDeviceChannel.MaxFpsFor(viaOpenRgb, settings) == RgbDeviceChannel.GpuMaxFps, "through OpenRGB: still capped");
        var ch = new RgbDeviceChannel(direct, 0);
        T.Check(!ch.SendsEveryFrame(RgbSync.AnimatedPeriod), "direct: no dithering (keeps zones in one colour)");
        T.Check(EffectCatalog.Resolve(MusicStyles.Meter, EffectCatalog.Auto, direct.IsSlow, false) == EffectVariant.Meter,
            "direct: the volume meter is drawn as a meter (not the slow pulse)");
    }
}

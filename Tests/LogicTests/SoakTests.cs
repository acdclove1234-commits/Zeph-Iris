// ==================================================
// SoakTests.cs - 長い時間動かしたときの確かめ (ファン制御を 6 時間・LED を全部の光り方で 5 分ずつ早回し)
//   温度の乱高下・センサーの読み取り失敗・書き込みの失敗・部品の全滅を混ぜて、
//   例外で止まらない・安全の約束を破らない・メモリが増え続けない・LED の毎コマの処理で new しない、を見る
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class SoakTests
{
    private const string Cpu = "/intelcpu/0/temperature/0";
    private const string Gpu = "/gpu-nvidia/0/temperature/0";
    private const string Fan0 = "/lpc/nct6686d/0/control/0";
    private const string Rpm0 = "/lpc/nct6686d/0/fan/0";
    private const string PumpCtl = "/lpc/nct6686d/0/control/1";
    private const string Fan2 = "/lpc/nct6686d/0/control/2";

    /// <summary>
    /// ファン制御を 6 時間分（21,600 周期）動かす。毎周期: 書いた値は 0〜100%、カーブのファンは最低回転より下げない、
    /// ポンプは固定の 100% を下げない、温度が緊急温度を超えた次の周期には全開。
    /// 例外で止まらない・警告を出しすぎない・メモリが増え続けない
    /// </summary>
    public static void TestFanEngineSixHours()
    {
        var cfg = new AppConfig();
        var p = new ProfileConfig { Name = "標準" };
        p.Fans.Add(new FanConfig
        {
            Name = "Radiator", ControlId = Fan0, RpmSensorId = Rpm0, MinDuty = 25,
            Curves =
            {
                new CurveConfig { Name = "CPU", SensorId = Cpu, Points = new() { new() { Temp = 40, Duty = 25 }, new() { Temp = 70, Duty = 60 }, new() { Temp = 85, Duty = 100 } } },
                new CurveConfig { Name = "GPU", SensorId = Gpu, Points = new() { new() { Temp = 45, Duty = 25 }, new() { Temp = 80, Duty = 90 } } },
            },
        });
        p.Fans.Add(new FanConfig { Name = "Pump", ControlId = PumpCtl, Role = FanRoles.Pump, Mode = FanModes.Fixed, FixedDuty = 100, MinDuty = 60 });
        p.Fans.Add(new FanConfig { Name = "Case", ControlId = Fan2, Mode = FanModes.Follow, FollowControlId = Fan0, MinDuty = 20 });
        cfg.Profiles.Add(p);
        cfg.Normalize();

        var hw = new FakeHardware();
        var radiator = hw.AddControl(Fan0, 40);
        var pump = hw.AddControl(PumpCtl, 100);
        var caseFan = hw.AddControl(Fan2, 40);
        hw.Values[Cpu] = 45;
        hw.Values[Gpu] = 40;
        hw.Values[Rpm0] = 1200;
        var rig = new Rig(cfg, hw);
        var rng = new Random(20261110);

        double cpu = 45, gpu = 40;
        int violations = 0, exceptions = 0, emergencyMisses = 0;
        string firstViolation = "";
        long memAfterWarmup = 0;
        const int Ticks = 6 * 3600;
        int hotRun = 0;
        for (int i = 0; i < Ticks; i++)
        {
            // 温度: ゆっくり動く負荷 + ゆらぎ。ときどき急な負荷（ゲーム開始）と、まれに緊急温度を超える熱
            double load = 0.5 + 0.5 * Math.Sin(i / 900.0) + (rng.NextDouble() < 0.002 ? 1 : 0);
            cpu += (40 + 45 * Math.Clamp(load, 0, 1.2) - cpu) * 0.05 + rng.NextDouble() * 2 - 1;
            gpu += (35 + 40 * Math.Clamp(load, 0, 1.2) - gpu) * 0.03 + rng.NextDouble() - 0.5;
            if (i % 5000 is >= 4000 and < 4003) cpu = 104;   // 緊急温度（100°C）を 3 周期超える
            hw.Values[Cpu] = rng.NextDouble() < 0.003 ? null : (float)cpu;       // 読み取り失敗が 1 回だけ混ざる
            hw.Values[Gpu] = (float)gpu;
            hw.Values[Rpm0] = 600 + (float)radiator.Register * 15;
            // まれに: センサーを読むと例外・書き込みが失敗・部品が全部読めない（数周期続く）
            if (i % 3000 == 1500) hw.ThrowOnRead.Add(Gpu);
            if (i % 3000 == 1510) hw.ThrowOnRead.Remove(Gpu);
            radiator.ThrowBelow = i % 4000 is >= 2000 and < 2003 ? 101 : 0;
            hw.FailAll = i % 7000 is >= 6000 and < 6004;

            int before = radiator.Writes.Count;
            try { rig.Tick(); }
            catch (Exception ex)
            {
                exceptions++;
                if (exceptions == 1) firstViolation = $"tick {i}: {ex.GetType().Name}: {ex.Message}";
                continue;
            }

            void Bad(string what)
            {
                violations++;
                if (firstViolation.Length == 0) firstViolation = $"tick {i}: {what}";
            }
            foreach (var c in new[] { radiator, pump, caseFan })
                foreach (var w in c.Writes.Skip(c == radiator ? before : Math.Max(0, c.Writes.Count - 1)))
                    if (!(w >= 0 && w <= 100)) Bad($"{c.Id} wrote {w}");
            if (radiator.Writes.Count > before && radiator.LastWrite < 25 - 0.5) Bad($"radiator below its minimum ({radiator.LastWrite})");
            if (pump.Writes.Count > 0 && pump.LastWrite < 100 - 0.5) Bad($"pump below its fixed 100% ({pump.LastWrite})");
            // 緊急温度を超えて 2 周期目には全開（1 周期だけの跳ねは読み違いとして無視するため、本物の上昇は 1 周期遅れてよい）
            hotRun = hw.Values[Cpu] is > 100 ? hotRun + 1 : 0;
            // （書き込みをわざと失敗させている周期は、チップが受け付けないので数えない）
            if (hotRun >= 2 && !hw.FailAll && radiator.ThrowBelow == 0 && (radiator.Register < 99 || !rig.Last!.Emergency)) emergencyMisses++;
            if (i == 1000)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                memAfterWarmup = GC.GetTotalMemory(true);
            }
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long memEnd = GC.GetTotalMemory(true);
        double growMb = (memEnd - memAfterWarmup) / 1048576.0;
        T.Info($"6 h: {Ticks} ticks, {radiator.Writes.Count} radiator writes, {rig.Alerts.Count} alerts, memory +{growMb:F2} MB after warm-up");
        T.Check(exceptions == 0, $"no exception escapes the engine ({exceptions}; {firstViolation})");
        T.Check(violations == 0, $"every write stays within 0–100% and above the minimum / fixed duty ({violations}; {firstViolation})");
        T.Check(emergencyMisses == 0, $"full speed by the 2nd tick over the emergency temperature ({emergencyMisses} misses)");
        T.Check(rig.Alerts.Count < 200, $"alerts do not flood ({rig.Alerts.Count} in 6 h)");
        T.Check(growMb < 8, $"memory does not keep growing (+{growMb:F2} MB over 6 h)");
    }

    /// <summary>
    /// LED: 全部の光り方（音楽連動の 13 種類と、温度・虹・呼吸・固定色）を、速いテープ・ゾーンのあるマザーボード・遅いグラボで
    /// 5 分ずつ（60 fps で 18,000 コマ）早回しする。キック・スネア・ハイハットを乱数で混ぜ、テンポも途中で変える。
    /// どの色も 0〜255 の範囲で NaN を出さない・例外で止まらない・慣れたあとは毎コマの処理で new しない
    /// </summary>
    public static void TestRendererFiveMinutesEveryEffect()
    {
        var devices = new[]
        {
            new RgbDevice(0, "Strip", 120, 4, null, "HID: x"),
            new RgbDevice(1, "Board", 34, 0, new[] { 10, 8, 8, 8 }, "HID: x") { ZoneNames = new[] { "Board", "Header 1", "Header 2", "Header 3" } },
            new RgbDevice(2, "NVIDIA GeForce RTX 5070 Ti", 6, RgbDevice.TypeGpu, new[] { 1, 1, 1, 1, 1, 1 }, "I2C: x"),
        };
        var styles = MusicStyles.All.Select(s => (Mode: RgbModes.Music, Style: s))
            .Concat(new[] { RgbModes.Temperature, RgbModes.Rainbow, RgbModes.RainbowStatic, RgbModes.Breathing, RgbModes.Static }.Select(m => (Mode: m, Style: MusicStyles.BeatHue)))
            .ToList();
        int bad = 0, exceptions = 0;
        string firstBad = "";
        long worstAlloc = 0;
        string worstWhere = "";
        const int Frames = 18_000;
        foreach (var (mode, style) in styles)
        foreach (var dev in devices)
        {
            var rng = new Random(dev.Index * 31 + style.GetHashCode() % 1000);
            var cfg = new RgbConfig { Mode = mode, Brightness = 100, Gamma = true, StaticColor = "#6A00FF", Speed = 1 };
            var r = new DeviceRenderer(dev);
            var input = new RenderInput
            {
                Cfg = cfg, Music = ResolvedMusic.Default with { Effect = style, StrobeAllowed = true },
                Device = new RgbDeviceSettings { Name = dev.Name, Zones = dev.Index == 1 ? new() { new RgbZoneSettings { Name = "Header 2", Fans = 2 } } : new() },
                Temperature = 50,
            };
            input.Frame.GateOpen = true;
            input.Frame.Activity = 1;
            var output = new Rgb[dev.LedCount];
            long id = 0;
            double period = 0.5;
            var beat = new BeatState(period, 0, 0, 0.9, true, 0);
            long allocInSteadyState = 0;
            for (int f = 0; f < Frames; f++)
            {
                double t = f / 60.0;
                input.T = t;
                input.TEval = t;
                input.TPrevEval = Math.Max(0, t - 1 / 60.0);
                input.Dt = 1 / 60.0;
                input.Temperature = 40 + 30 * (0.5 + 0.5 * Math.Sin(t / 20));
                var fr = input.Frame;
                fr.T = t;
                fr.Bass = fr.BassE = (float)rng.NextDouble();
                fr.Mid = fr.MidE = (float)rng.NextDouble();
                fr.Treble = fr.TrebleE = (float)rng.NextDouble();
                fr.Sub = fr.SubE = (float)rng.NextDouble();
                fr.KickEnv = (float)rng.NextDouble();
                fr.LoudVU = (float)rng.NextDouble();
                fr.GateOpen = f % 3000 < 2700;   // 30 秒ごとに 5 秒の無音（音が止まったときの光り方）
                // テンポは 1 分ごとに変える（拍の位置も作り直す）。BeatState はここで作るので、描画の new には数えない
                if (f % 3600 == 0)
                {
                    period = 60.0 / (90 + rng.Next(90));
                    beat = new BeatState(period, t, f / 30, 0.9, true, 0);
                }
                input.Beat = beat;
                input.Events.Clear();
                if (fr.GateOpen)
                {
                    if (rng.NextDouble() < 0.04) input.Events.Add(new AudioEvent(++id, AudioEventKind.Kick, t, 1 + (float)rng.NextDouble() * 2));
                    if (rng.NextDouble() < 0.02) input.Events.Add(new AudioEvent(++id, AudioEventKind.Snare, t, 1 + (float)rng.NextDouble() * 2));
                    if (rng.NextDouble() < 0.08) input.Events.Add(new AudioEvent(++id, AudioEventKind.Hat, t, 1 + (float)rng.NextDouble() * 2));
                    if (rng.NextDouble() < 0.03) input.Events.Add(new AudioEvent(++id, AudioEventKind.Onset, t, 1 + (float)rng.NextDouble() * 2));
                }
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                try { r.Render(input, output); }
                catch (Exception ex)
                {
                    exceptions++;
                    if (firstBad.Length == 0) firstBad = $"{mode}/{style}/{dev.Name} frame {f}: {ex.GetType().Name}: {ex.Message}";
                    break;
                }
                // 最初の 10 秒（配列を作る・光り方を切り替える）のあとは、毎コマの new を数える
                if (f >= 600) allocInSteadyState += GC.GetAllocatedBytesForCurrentThread() - a0;
                foreach (var c in r.LastLight)
                {
                    if (float.IsFinite(c.R) && float.IsFinite(c.G) && float.IsFinite(c.B) && c.R >= -1e-4f && c.G >= -1e-4f && c.B >= -1e-4f) continue;
                    bad++;
                    if (firstBad.Length == 0) firstBad = $"{mode}/{style}/{dev.Name} frame {f}: {c.R},{c.G},{c.B}";
                    break;
                }
            }
            if (allocInSteadyState > worstAlloc)
            {
                worstAlloc = allocInSteadyState;
                worstWhere = $"{mode}/{style}/{dev.Name}";
            }
        }
        double perFrame = worstAlloc / (double)(Frames - 600);
        T.Info($"{styles.Count} looks × {devices.Length} devices × {Frames} frames; worst steady-state allocation {perFrame:F1} bytes/frame ({worstWhere})");
        T.Check(exceptions == 0, $"no exception while rendering ({exceptions}; {firstBad})");
        T.Check(bad == 0, $"every LED value is finite and not negative ({bad}; {firstBad})");
        // 音が止まった・戻ったときなどのまれな切り替えで少し作るのは許す（平均で 1 コマ 64 バイト未満）
        T.Check(perFrame < 64, $"rendering does not allocate every frame once warmed up ({perFrame:F1} bytes/frame, {worstWhere})");
    }

    /// <summary>書き直しで値をずらすとき、100% を超えない（100% のときは下へずらす）</summary>
    public static void TestReassertNudgeStaysInRange()
    {
        T.Check(FanSafety.Nudged(100f) < 100f && FanSafety.Nudged(100f) > 99.99f, $"100% is nudged down ({FanSafety.Nudged(100f)})");
        T.Check(FanSafety.Nudged(50f) > 50f && FanSafety.Nudged(50f) < 50.01f, "50% is nudged up");
        T.Check(FanSafety.Nudged(0f) > 0f, "0% is nudged up");
    }
}

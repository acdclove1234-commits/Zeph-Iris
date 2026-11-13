// ==================================================
// ReviewSafetyTests.cs - 外部レビューで見つかった条件の安全のテスト
//   （温度カーブのない固定回転のファンの測定中に CPU の温度だけ読めなくなる・温度が 1 つも読めない・同じ型番の GPU が 2 枚）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class ReviewSafetyTests
{
    private const string Fan0 = "/lpc/nct6686d/0/control/0";
    private const string Rpm0 = "/lpc/nct6686d/0/fan/0";
    private const string CpuPkg = "/intelcpu/0/temperature/16";
    private const string Gpu0Core = "/gpu-nvidia/0/temperature/0";
    private const string Gpu0Hot = "/gpu-nvidia/0/temperature/2";
    private const string Gpu1Core = "/gpu-nvidia/1/temperature/0";
    private const string Gpu1Hot = "/gpu-nvidia/1/temperature/2";

    private static AppConfig FixedFanConfig()
    {
        var cfg = new AppConfig();
        var p = new ProfileConfig { Name = "標準" };
        p.Fans.Add(new FanConfig { Name = "Case", ControlId = Fan0, RpmSensorId = Rpm0, MinDuty = 20, Mode = FanModes.Fixed, FixedDuty = 60 });
        cfg.Profiles.Add(p);
        cfg.Normalize();
        return cfg;
    }

    private static FakeHardware Hw(bool withTemps = true, bool twoGpus = false)
    {
        var hw = new FakeHardware();
        hw.AddControl(Fan0, 60);
        hw.Values[Rpm0] = 1200;
        if (!withTemps) return hw;
        hw.Values[CpuPkg] = 50;
        hw.Values[Gpu0Core] = 40;
        hw.Values[Gpu0Hot] = 50;
        hw.Temps.Add(new SensorInfo(CpuPkg, "CPU Package", "Intel Core Ultra", "Cpu", "Temperature"));
        hw.Temps.Add(new SensorInfo(Gpu0Core, "GPU Core", "NVIDIA GeForce RTX", "GpuNvidia", "Temperature"));
        hw.Temps.Add(new SensorInfo(Gpu0Hot, "GPU Hot Spot", "NVIDIA GeForce RTX", "GpuNvidia", "Temperature"));
        if (twoGpus)
        {
            hw.Values[Gpu1Core] = 45;
            hw.Values[Gpu1Hot] = 55;
            hw.Temps.Add(new SensorInfo(Gpu1Core, "GPU Core", "NVIDIA GeForce RTX", "GpuNvidia", "Temperature"));
            hw.Temps.Add(new SensorInfo(Gpu1Hot, "GPU Hot Spot", "NVIDIA GeForce RTX", "GpuNvidia", "Temperature"));
        }
        return hw;
    }

    /// <summary>温度カーブのない固定回転のファン: 測定中に CPU の温度だけ読めなくなったら、GPU が読めても中止する</summary>
    public static void TestFixedFanCalibrationStopsWhenOneWatchedTempIsLost()
    {
        var hw = Hw();
        var rig = new Rig(FixedFanConfig(), hw);
        string? message = null;
        rig.E.CalibrationFinished += (_, r, m) => message = r is null ? m : "ok";
        rig.Tick(2);
        rig.E.StartCalibration(Fan0);
        rig.Tick(3);
        T.Check(rig.Fan(Fan0).State == FanState.Calibrating, "calibrating while CPU and GPU temperatures are readable");
        hw.Values[CpuPkg] = null;
        rig.Tick(3);
        T.Check(rig.Fan(Fan0).State != FanState.Calibrating, $"stops when the CPU temperature is lost even though the GPU is readable (state {rig.Fan(Fan0).State})");
        T.Check(message is not null && message.Contains("温度"), $"the reason mentions the temperature ({message})");
        T.Check(hw.Controls[Fan0].LastWrite >= 60, $"back to the fixed 60% (last write {hw.Controls[Fan0].LastWrite:F0}%)");
    }

    /// <summary>温度が 1 つも読めない PC: 回転数が読めても、測定を始めない</summary>
    public static void TestCalibrationRefusedWithoutAnyTemperature()
    {
        var hw = Hw(withTemps: false);
        var rig = new Rig(FixedFanConfig(), hw);
        string? message = null;
        rig.E.CalibrationFinished += (_, r, m) => message = r is null ? m : "ok";
        rig.Tick(2);
        rig.E.StartCalibration(Fan0);
        rig.Tick(5);
        T.Check(rig.Fan(Fan0).State != FanState.Calibrating, $"does not calibrate without any readable temperature (state {rig.Fan(Fan0).State})");
        T.Check(message is not null && message.Contains("温度"), $"tells why ({message})");
        T.Check(hw.Controls[Fan0].Writes.All(w => w >= 60), "never lowered below the fixed duty");
    }

    /// <summary>同じ型番の GPU が 2 枚: 2 枚目のコア・Hot Spot も緊急全開で見る</summary>
    public static void TestTwoIdenticalGpusAreBothWatched()
    {
        var ids = FanEngine.AlwaysWatchedTemps(Hw(twoGpus: true).Temps).ToList();
        T.Check(ids.Contains(Gpu0Core) && ids.Contains(Gpu0Hot) && ids.Contains(Gpu1Core) && ids.Contains(Gpu1Hot),
            $"both GPUs' core and hot spot are watched ({string.Join(", ", ids)})");

        var hw = Hw(twoGpus: true);
        var cfg = FixedFanConfig();
        cfg.EmergencyTemperature = 100;
        cfg.EmergencyHotspotTemperature = 108;
        var rig = new Rig(cfg, hw);
        rig.Tick(3);
        T.Check(!rig.Last!.Emergency, "normal temperatures: no emergency");
        hw.Values[Gpu1Core] = 105;
        hw.Values[Gpu1Hot] = 110;
        rig.Tick(3);
        T.Check(rig.Last!.Emergency && hw.Controls[Fan0].LastWrite == 100, $"second GPU 105°C/110°C → full speed (last write {hw.Controls[Fan0].LastWrite:F0}%)");
    }

    public static void TestHardwareKey()
    {
        T.Check(FanEngine.HardwareKey(new SensorInfo("/gpu-nvidia/1/temperature/2", "GPU Hot Spot", "RTX", "GpuNvidia", "Temperature")) == "/gpu-nvidia/1", "device part of the id");
        T.Check(FanEngine.HardwareKey(new SensorInfo("gpu0", "GPU Core", "RTX", "GpuNvidia", "Temperature")) == "name:RTX", "other ids fall back to the name");
    }
}

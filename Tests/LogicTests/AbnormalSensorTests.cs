// ==================================================
// AbnormalSensorTests.cs - LibreHardwareMonitor が返しうるおかしな値でのファンの動き（外部レビューの指摘で足したテスト）
//   NaN・無限大・255°C・-128°C などの値 / 途中で消えたセンサー / 1 回だけの跳ね
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class AbnormalSensorTests
{
    private const string Cpu = "/intelcpu/0/temperature/0";
    private const string Fan0 = "/lpc/nct6686d/0/control/0";
    private const string Rpm0 = "/lpc/nct6686d/0/fan/0";

    private static AppConfig Config()
    {
        var cfg = new AppConfig();
        var p = new ProfileConfig { Name = "標準" };
        p.Fans.Add(new FanConfig
        {
            Name = "CPU", ControlId = Fan0, RpmSensorId = Rpm0, MinDuty = 20,
            Curves = { new CurveConfig { Name = "CPU", SensorId = Cpu, Points = new() { new() { Temp = 30, Duty = 20 }, new() { Temp = 90, Duty = 100 } } } },
        });
        cfg.Profiles.Add(p);
        cfg.Normalize();
        cfg.EmergencyTemperature = 100;
        return cfg;
    }

    private static (Rig Rig, FakeHardware Hw) Start(float cpu)
    {
        var hw = new FakeHardware();
        hw.AddControl(Fan0, 40);
        hw.Values[Cpu] = cpu;
        hw.Values[Rpm0] = 1200;
        hw.Temps.Add(new SensorInfo(Cpu, "CPU Package", "Intel Core", "Cpu", "Temperature"));
        return (new Rig(Config(), hw), hw);
    }

    public static void TestPlausibleTemps()
    {
        foreach (float v in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 255f, 150f, -1f, -128f, 127f, 128f, 0f })
            T.Check(!FanSafety.IsPlausibleTemp(v), $"{v} °C is not a real temperature");
        T.Check(FanSafety.IsPlausibleTemp(149.9f) && FanSafety.IsPlausibleTemp(45f), "normal readings are accepted");
    }

    /// <summary>温度がおかしな値のまま続いたら、温度が分からないとみなして全開にする（低い回転のままにしない）</summary>
    public static void TestAbnormalValuesGoFullSpeed()
    {
        foreach (float v in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 255f, -128f, 0f, 127f, 128f })
        {
            var (rig, hw) = Start(40);
            rig.Tick(5);
            hw.Values[Cpu] = v;
            rig.Tick(3);
            var f = rig.Fan(Fan0);
            T.Check(f.State == FanState.SensorError && hw.Controls[Fan0].LastWrite == 100, $"CPU reads {v} → sensor error and 100% (state {f.State}, last write {hw.Controls[Fan0].LastWrite:F0}%)");
        }
    }

    /// <summary>熱いときにセンサーが消えても（ドライバーの不調など）、最後に必要だった回転より下げない。戻れば元どおり</summary>
    public static void TestVanishedSensorHoldsLastDemand()
    {
        var (rig, hw) = Start(85);
        rig.Tick(40);
        double before = hw.Controls[Fan0].LastWrite;
        T.Check(before >= 85, $"hot CPU drives the fan high ({before:F0}%)");
        hw.Values.Remove(Cpu);
        double lowest = 100;
        for (int i = 0; i < 30; i++)
        {
            rig.Tick();
            lowest = Math.Min(lowest, hw.Controls[Fan0].LastWrite);
        }
        T.Check(rig.Fan(Fan0).State == FanState.SensorMissing, $"state is SensorMissing ({rig.Fan(Fan0).State})");
        T.Check(lowest >= before - 0.5, $"the fan never drops below its last demand while the temperature is unknown (lowest {lowest:F0}%, before {before:F0}%)");
        hw.Values[Cpu] = 50;
        rig.Tick(3);
        T.Check(rig.Fan(Fan0).State == FanState.Active, "the sensor comes back → normal control again");
    }

    /// <summary>はじめから無いセンサー（部品を外した など）は、これまでどおり決めた回転（MissingSensorDuty）</summary>
    public static void TestNeverSeenSensorUsesMissingDuty()
    {
        var hw = new FakeHardware();
        hw.AddControl(Fan0, 40);
        hw.Values[Rpm0] = 1200;
        var rig = new Rig(Config(), hw);
        rig.Tick(40);
        double duty = hw.Controls[Fan0].LastWrite;
        T.Check(rig.Fan(Fan0).State == FanState.SensorMissing && Math.Abs(duty - new AppConfig().Safety.MissingSensorDuty) < 1.5,
            $"a sensor missing from the start uses MissingSensorDuty ({duty:F0}%)");
    }

    /// <summary>緊急温度を 1 回だけ超えた値でも、安全側（全開）に倒す</summary>
    public static void TestOneTickSpikeAboveEmergency()
    {
        var (rig, hw) = Start(50);
        rig.Tick(5);
        hw.Values[Cpu] = 140;
        rig.Tick();
        T.Check(hw.Controls[Fan0].LastWrite == 100, $"a single reading at 140 °C writes 100% at once (last write {hw.Controls[Fan0].LastWrite:F0}%)");
    }
}

/// <summary>目標温度モードでセンサーが消えたとき（外部レビュー 2 回目の指摘）</summary>
public static class TargetMissingSensorTests
{
    private const string Cpu = "/intelcpu/0/temperature/0";
    private const string Fan0 = "/lpc/nct6686d/0/control/0";
    private const string Rpm0 = "/lpc/nct6686d/0/fan/0";

    private static AppConfig Config()
    {
        var cfg = new AppConfig();
        var p = new ProfileConfig { Name = "標準" };
        p.Fans.Add(new FanConfig { Name = "CPU", ControlId = Fan0, RpmSensorId = Rpm0, MinDuty = 20, Mode = FanModes.Target, TargetSensorId = Cpu, TargetTemp = 60 });
        cfg.Profiles.Add(p);
        cfg.Normalize();
        return cfg;
    }

    /// <summary>はじめから無いセンサー: 起動したときの回転（BIOS が高く回していても）のまま固まらず、MissingSensorDuty に落ち着く</summary>
    public static void TestNeverSeenTargetSensorSettlesAtMissingDuty()
    {
        var hw = new FakeHardware();
        hw.AddControl(Fan0, 95);
        hw.Values[Rpm0] = 1800;
        var rig = new Rig(Config(), hw);
        rig.Tick(120);
        double duty = hw.Controls[Fan0].LastWrite;
        T.Check(Math.Abs(duty - new AppConfig().Safety.MissingSensorDuty) < 1.5, $"settles at MissingSensorDuty, not frozen at the starting 95% ({duty:F0}%)");
    }

    /// <summary>熱いときに消えた: 温度が読めていたときの最後の出力より下げない</summary>
    public static void TestVanishedTargetSensorHoldsLastOutput()
    {
        var hw = new FakeHardware();
        hw.AddControl(Fan0, 40);
        hw.Values[Rpm0] = 1200;
        hw.Values[Cpu] = 85;
        hw.Temps.Add(new SensorInfo(Cpu, "CPU Package", "Intel Core", "Cpu", "Temperature"));
        var rig = new Rig(Config(), hw);
        rig.Tick(90);
        double before = hw.Controls[Fan0].LastWrite;
        hw.Values.Remove(Cpu);
        double lowest = 100;
        for (int i = 0; i < 30; i++) { rig.Tick(); lowest = Math.Min(lowest, hw.Controls[Fan0].LastWrite); }
        T.Check(before > 70 && lowest >= before - 1, $"the fan stays at least at the last PI output while the sensor is gone (before {before:F0}%, lowest {lowest:F0}%)");
    }
}

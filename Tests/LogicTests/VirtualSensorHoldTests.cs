// ==================================================
// VirtualSensorHoldTests.cs - 仮想センサーの元のセンサーの一部が読めないときのテスト
//   （平均値は一瞬の失敗なら最後の値を使い続ける・続いたら残りだけで計算して一度だけ知らせる・戻ったらまた知らせられる）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class VirtualSensorHoldTests
{
    private const string A = "/intelcpu/0/temperature/0", B = "/gpu-nvidia/0/temperature/0", V = "/virtual/test0001";
    private const string Fan0 = "/lpc/nct6686d/0/control/0";

    private static Rig Make(string kind, out FakeHardware hw)
    {
        hw = new FakeHardware();
        hw.AddControl(Fan0, 40);
        hw.Values[A] = 70;
        hw.Values[B] = 35;
        var cfg = new AppConfig();
        cfg.VirtualSensors.Add(new VirtualSensorConfig { Id = V, Name = "CPU+GPU", Kind = kind, Sources = { A, B } });
        var p = new ProfileConfig { Name = "標準" };
        p.Fans.Add(new FanConfig
        {
            Name = "Front", ControlId = Fan0, MinDuty = 20,
            Curves = { new CurveConfig { Name = "CPU+GPU", SensorId = V, Points = new() { new() { Temp = 30, Duty = 20 }, new() { Temp = 90, Duty = 100 } } } },
        });
        cfg.Profiles.Add(p);
        cfg.Normalize();
        return new Rig(cfg, hw);
    }

    private static float? Value(Rig rig) => rig.Last!.Values.TryGetValue(V, out var v) ? v : null;
    private static int PartialAlerts(Rig rig) => rig.Alerts.Count(a => a.Message.Contains("仮想センサー") && a.Message.Contains("CPU+GPU"));

    /// <summary>
    /// 平均値（70, 35）: 片方が 2 秒読めないだけなら 52.5 のまま（35 を抜いた 70 へ跳ばない）・知らせない。
    /// 10 秒続いたら残りだけ（70）で計算して一度だけ知らせる。戻ったら 52.5 に戻り、また失うともう一度知らせる
    /// </summary>
    public static void TestAverageHoldsMissingSourceBriefly()
    {
        var rig = Make(VirtualSensorKinds.Average, out var hw);
        rig.Tick(5);
        T.Near(Value(rig)!.Value, 52.5, 0.01, "both sources read: average 52.5");

        hw.Values[B] = null;
        rig.Tick(2);
        T.Near(Value(rig)!.Value, 52.5, 0.01, "source lost for 2 s: last good value kept, average stays 52.5");
        T.Check(PartialAlerts(rig) == 0, "no alert for a brief read failure");
        T.Check(rig.Fan(Fan0).State == FanState.Active, "fan control unaffected");

        rig.Tick(12);
        T.Near(Value(rig)!.Value, 70, 0.01, "lost for more than 10 s: average of the remaining source (70)");
        T.Check(PartialAlerts(rig) == 1, $"partial loss alerted once ({PartialAlerts(rig)})");
        T.Check(rig.Alerts.Any(a => a.Level == AlertLevel.Warning && a.Message.Contains(B)), "alert names the missing source");
        rig.Tick(20);
        T.Check(PartialAlerts(rig) == 1, "not repeated every tick");

        hw.Values[B] = 35;
        rig.Tick(2);
        T.Near(Value(rig)!.Value, 52.5, 0.01, "source back: average 52.5 again");
        hw.Values[B] = null;
        rig.Tick(13);
        T.Check(PartialAlerts(rig) == 2, "a new loss after recovery is alerted again");

        // 全部読めない: 仮想センサーごと読めない（カーブはセンサー異常）。一部の喪失としては知らせない
        hw.Values[A] = null;
        rig.Tick(13);
        T.Check(Value(rig) is null, "all sources lost (past the hold): virtual value unreadable");
        T.Check(rig.Fan(Fan0).State == FanState.SensorError, $"curve on it falls back to the sensor-error path ({rig.Fan(Fan0).State})");
    }

    /// <summary>
    /// 平均値の元のセンサーが同時に全部読めなくなったら、最後の値で埋めずにすぐ読めない扱い（センサー異常）にする。
    /// 以前は 10 秒間、古い平均のまま動き、全開になるのが 10 秒遅れていた
    /// </summary>
    public static void TestAverageAllSourcesLostIsSensorErrorAtOnce()
    {
        var rig = Make(VirtualSensorKinds.Average, out var hw);
        rig.Tick(5);
        hw.Values[A] = null;
        hw.Values[B] = null;
        rig.Tick(1);
        T.Check(Value(rig) is null, "both sources lost on the same tick: virtual value unreadable at once");
        T.Check(rig.Fan(Fan0).State == FanState.SensorError, $"curve on it is a sensor error at once ({rig.Fan(Fan0).State})");
        rig.Tick(2);
        T.Near(rig.Hw.Controls[Fan0].Register, 100, 0.5, "full speed after the usual 2-tick confirmation");
        T.Check(PartialAlerts(rig) == 0, "not reported as a partial loss");

        // 片方が一瞬読めない間にもう片方も読めなくなった: その時点で読めない扱い
        var r2 = Make(VirtualSensorKinds.Average, out var hw2);
        r2.Tick(5);
        hw2.Values[B] = null;
        r2.Tick(3);
        T.Near(Value(r2)!.Value, 52.5, 0.01, "one source briefly lost: held average");
        hw2.Values[A] = null;
        r2.Tick(1);
        T.Check(Value(r2) is null, "the other one lost too: unreadable at once, not the stale 52.5");
    }

    /// <summary>最大値は、これまでどおり読めるものだけで計算する（保持しない）。続いたら同じように知らせる。差分は片方でも読めなければ読めない</summary>
    public static void TestMaxAndDeltaUnchanged()
    {
        var rig = Make(VirtualSensorKinds.Max, out var hw);
        rig.Tick(3);
        hw.Values[A] = null;
        rig.Tick(1);
        T.Near(Value(rig)!.Value, 35, 0.01, "max: remaining source used at once (unchanged behaviour)");
        T.Check(PartialAlerts(rig) == 0, "max: no alert yet");
        rig.Tick(12);
        T.Check(PartialAlerts(rig) == 1, "max: partial loss alerted after 10 s");

        var d = Make(VirtualSensorKinds.Delta, out var hw2);
        d.Tick(3);
        T.Near(Value(d)!.Value, 35, 0.01, "delta 70 − 35");
        hw2.Values[B] = null;
        d.Tick(1);
        T.Check(Value(d) is null, "delta: unreadable as soon as one source is missing (unchanged)");
        d.Tick(12);
        T.Check(PartialAlerts(d) == 0, "delta: no partial-loss alert (the curve already reports a sensor error)");
    }
}

// センサーの画面の最小・最大・平均（SensorStats）
using ProFanController;

namespace LogicTests;

public static class SensorStatsTests
{
    private static Dictionary<string, float?> V(params (string Id, float? Value)[] values) => values.ToDictionary(v => v.Id, v => v.Value);

    public static void TestMinMaxAverage()
    {
        var s = new SensorStats();
        s.Add(V(("/cpu/t", 40f), ("/fan/1", 900f)));
        s.Add(V(("/cpu/t", 55f), ("/fan/1", 1100f)));
        s.Add(V(("/cpu/t", 38f), ("/fan/1", null)));
        var cpu = s.Get("/cpu/t")!;
        T.Near(cpu.Min, 38, 1e-6, "最小");
        T.Near(cpu.Max, 55, 1e-6, "最大");
        T.Near(cpu.Average, (40 + 55 + 38) / 3.0, 1e-4, "平均");
        T.Check(cpu.Count == 3, "回数");
        var fan = s.Get("/fan/1")!;
        T.Check(fan.Count == 2, "読めなかった値（null）は数えない");
        T.Near(fan.Average, 1000, 1e-3, "読めた値だけで平均");
        T.Check(s.Get("/CPU/T") is not null, "ID の大文字・小文字は区別しない");
        T.Check(s.Get("/none") is null, "まだ値がないセンサーは null");
    }

    public static void TestBadValuesIgnored()
    {
        var s = new SensorStats();
        s.Add(V(("a", float.NaN)));
        T.Check(s.Get("a") is { Count: 0 } e && float.IsNaN(e.Min) && float.IsNaN(e.Average), "NaN だけなら、最小・平均は空のまま");
        s.Add(V(("a", float.PositiveInfinity), ("a2", -5f)));
        T.Check(s.Get("a")!.Count == 0, "無限大は数えない");
        T.Near(s.Get("a2")!.Min, -5, 1e-6, "負の値（差の仮想センサーなど）も数える");
    }

    public static void TestReset()
    {
        var s = new SensorStats();
        s.Add(V(("t", 80f)));
        var at = new DateTime(2026, 11, 13, 15, 0, 0);
        s.Reset(at);
        T.Check(s.Get("t") is null, "リセットで消える");
        T.Check(s.Since == at, "数え始めた時刻");
        s.Add(V(("t", 42f)));
        T.Near(s.Get("t")!.Max, 42, 1e-6, "リセット後の値だけで数える");
    }
}

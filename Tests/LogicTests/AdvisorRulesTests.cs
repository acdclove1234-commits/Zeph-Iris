// AI 冷却アドバイザー: アプリが決まったルールで作る提案・温度の目安・記録の写し（AI に数値を作らせない）
using ProFanController;

namespace LogicTests;

public static class AdvisorRulesTests
{
    private static List<CurvePoint> P(params (double t, double d)[] pts) => pts.Select(x => new CurvePoint { Temp = x.t, Duty = x.d }).ToList();

    public static void TestSuggestCurve()
    {
        var gpu = P((43, 30), (66, 43), (73, 59), (79, 79), (85, 100));
        var quiet = AdvisorPrompt.SuggestCurve(gpu, sensorMax: 59, limit: 83, out double s);
        T.Check(quiet is not null && s == 4, $"24 °C margin: shift right, capped so it does not pass the 100% point ({s})");
        T.Check(quiet![^1].Temp == 85 && quiet[^1].Duty == 100, "the full-speed point stays");
        T.Check(AdvisorPrompt.CurveDuty(quiet, 59) < AdvisorPrompt.CurveDuty(gpu, 59), "quieter at the hottest recorded temperature");
        for (int i = 1; i < quiet.Count; i++) T.Check(quiet[i].Temp > quiet[i - 1].Temp, $"points stay in order ({quiet[i - 1].Temp} < {quiet[i].Temp})");

        var flat = P((38, 25), (71, 25), (78, 25), (84, 62), (90, 100));
        T.Check(AdvisorPrompt.SuggestCurve(flat, 49, 90, out _) is null, "curve already at its floor in the recorded range: no suggestion");

        var mid = P((40, 30), (70, 60), (85, 100));
        T.Check(AdvisorPrompt.SuggestCurve(mid, 72, 83, out _) is null, "11 °C margin: leave it");

        var hot = P((40, 30), (70, 60), (85, 100));
        var cooler = AdvisorPrompt.SuggestCurve(hot, 80, 83, out double s2);
        T.Check(cooler is not null && s2 == -5 && AdvisorPrompt.CurveDuty(cooler, 80) > AdvisorPrompt.CurveDuty(hot, 80), $"3 °C margin: spin up earlier ({s2})");

        var odd = P((40, 60), (60, 30), (80, 100));
        T.Check(AdvisorPrompt.SuggestCurve(odd, 50, 90, out _) is null, "curves that go down somewhere are not touched");
    }

    public static void TestLimits()
    {
        T.Check(AdvisorPrompt.LimitFor("/gpu-nvidia/0/temperature/2", "GPU Hot Spot") == 95, "hot spot limit");
        T.Check(AdvisorPrompt.LimitFor("/gpu-nvidia/0/temperature/0", "GPU") == 83, "GPU core limit");
        T.Check(AdvisorPrompt.LimitFor("/intelcpu/0/temperature/16", "CPU") == 90, "CPU limit");
        T.Check(AdvisorPrompt.LimitFor("/nvme/2/temperature/0", "SSD") == 70, "SSD limit");
        T.Check(AdvisorPrompt.LimitFor("/lpc/nct6686d/0/temperature/1", "Inside the case") is null, "board sensors get no limit (what they measure varies)");
    }

    public static void TestHistorySnapshot()
    {
        var h = new HistoryStore();
        var t0 = new DateTime(2026, 11, 12, 10, 0, 0);
        // リングバッファが一周しても、写しは古い順に同じ値を持つ
        int n = HistoryStore.Capacity + 5;
        for (int i = 0; i < n; i++) h.AddRaw(t0.AddSeconds(i), new Dictionary<string, float> { ["x"] = i });
        var c = h.Snapshot();
        T.Check(c.Count == h.Count, "same count");
        bool same = true;
        foreach (int i in new[] { 0, 1, 4, 5, 1000, h.Count - 1 })
            same &= c.ValueAt("x", i) == h.ValueAt("x", i) && c.TimeAt(i) == h.TimeAt(i);
        T.Check(same, "same values and times after the ring wrapped");
        h.AddRaw(t0.AddSeconds(n), new Dictionary<string, float> { ["x"] = -1 });
        T.Check(c.ValueAt("x", c.Count - 1) == n - 1, "the copy does not change when the original gets new samples");
    }
}

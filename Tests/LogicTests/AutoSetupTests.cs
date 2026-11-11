// 自動セットアップ: 手順の組み立てと、GPU の内蔵負荷で測るときの動き
using ProFanController;

namespace LogicTests;

public static class AutoSetupTests
{
    private static readonly SensorKind[] Cpu = { SensorKind.Cpu };
    private static readonly SensorKind[] Gpu = { SensorKind.Gpu };
    private static readonly SensorKind[] Both = { SensorKind.Cpu, SensorKind.GpuHotspot };
    private static readonly SensorKind[] None = { SensorKind.Other };

    public static void TestSetupPlanOrderAndFans()
    {
        var fans = new List<SetupFan>
        {
            new("rad", "Radiator", Cpu, CanCalibrate: true, Calibrated: false),
            new("case", "Case", Both, CanCalibrate: true, Calibrated: true),
            new("gpu", "GPU Fan", Gpu, CanCalibrate: true, Calibrated: false),
            new("sys", "System", None, CanCalibrate: false, Calibrated: false),
        };
        var steps = AutoSetupPlan.Build(fans, cpu: true, gpu: true, calibrateMissing: true);
        T.Check(steps.Select(s => s.Kind).SequenceEqual(new[] { SetupStepKind.Calibrate, SetupStepKind.Calibrate, SetupStepKind.TuneCpu, SetupStepKind.TuneGpu }),
            "calibrations first (before any load), then CPU, then GPU");
        T.Check(steps.Where(s => s.Kind == SetupStepKind.Calibrate).Select(s => s.Fans[0].ControlId).SequenceEqual(new[] { "rad", "gpu" }),
            "only uncalibrated fans that can be calibrated (no rpm sensor: skipped)");
        T.Check(steps.First(s => s.Kind == SetupStepKind.TuneCpu).Fans.Select(f => f.ControlId).SequenceEqual(new[] { "rad", "case", "sys" }),
            "CPU run: CPU-linked fans and fans linked to neither");
        T.Check(steps.First(s => s.Kind == SetupStepKind.TuneGpu).Fans.Select(f => f.ControlId).SequenceEqual(new[] { "case", "gpu" }),
            "GPU run: GPU-linked fans (hot spot counts)");

        var noCal = AutoSetupPlan.Build(fans, cpu: true, gpu: false, calibrateMissing: false);
        T.Check(noCal.Count == 1 && noCal[0].Kind == SetupStepKind.TuneCpu, "calibration off and GPU off: CPU run only");
        var gpuOnly = AutoSetupPlan.Build(new List<SetupFan> { fans[0] }, cpu: false, gpu: true, calibrateMissing: false);
        T.Check(gpuOnly.Count == 0, "no GPU-linked fan: no empty GPU run");
        T.Check(AutoSetupPlan.EstimateMinutes(steps) == (int)Math.Ceiling((2 * AutoSetupPlan.CalibrateSec + 2 * AutoSetupPlan.TuneSec) / 60),
            "estimate sums the steps");
    }

    private sealed class FakeLoad : ILoadGenerator
    {
        public int Starts, Stops;
        public string? Error { get; set; }
        public void Start() => Starts++;
        public void Stop() => Stops++;
    }

    private static EngineSnapshot Snap(DateTime t, float temp, float load)
        => new(t, "標準", new List<FanStatus>(), new Dictionary<string, float?> { ["gpuT"] = temp, ["gpuLoad"] = load }, false, false, false);

    /// <summary>内蔵の GPU 負荷: 使用率が 10 秒続けて 70% 以上になってから全開で温め始める（負荷をかけたことを確かめる）</summary>
    public static void TestBuiltInGpuLoadIsVerified()
    {
        var load = new FakeLoad();
        var tuner = new AutoTuner(new FanEngine(), new() { ("g", "GPU Fan") }, "gpuT", "GPU", "gpuLoad", 75, 90, 30, load);
        var t = new DateTime(2026, 11, 11, 9, 0, 0);
        int i = 0;
        for (; i <= 21; i++) tuner.OnSnapshot(Snap(t.AddSeconds(i), 40, 2));
        T.Check(load.Starts == 1 && tuner.Current == AutoTuner.Phase.WaitLoad, "after 20 s idle: built-in GPU load started, waiting for usage");
        for (; i <= 25; i++) tuner.OnSnapshot(Snap(t.AddSeconds(i), 45, 99));
        T.Check(tuner.Current == AutoTuner.Phase.WaitLoad, "usage must stay high for 10 s before measuring");
        for (; i <= 40; i++) tuner.OnSnapshot(Snap(t.AddSeconds(i), 55, 99));
        T.Check(tuner.Current == AutoTuner.Phase.Warmup, $"usage held: warm-up at full fan ({tuner.Current})");
        T.Check(tuner.StatusText.Length > 0, "status text");
        tuner.Dispose();
        T.Check(load.Stops >= 1, "load stopped on dispose");
    }

    /// <summary>内蔵の GPU 負荷で使用率が上がらない・負荷をかけられないときは、すぐ分かる理由で中止する（3 分待たない）</summary>
    public static void TestBuiltInGpuLoadFailures()
    {
        var t = new DateTime(2026, 11, 11, 9, 0, 0);

        var idle = new FakeLoad();
        var tuner = new AutoTuner(new FanEngine(), new() { ("g", "GPU Fan") }, "gpuT", "GPU", "gpuLoad", 75, 90, 30, idle);
        for (int i = 0; i <= 80; i++) tuner.OnSnapshot(Snap(t.AddSeconds(i), 40, 5));
        T.Check(tuner.Current == AutoTuner.Phase.Done && tuner.Result?.AbortReason is { } why && why.Contains("GPU"),
            $"usage never rises: aborted within ~45 s of starting the load ({tuner.Result?.AbortReason})");
        T.Check(idle.Stops >= 1, "load stopped after abort");
        string report = tuner.Result!.ToText(75);
        T.Check(!report.Contains("全開"), "report of a run with no steps does not claim the target is unreachable");

        var broken = new FakeLoad { Error = "no device" };
        var t2 = new AutoTuner(new FanEngine(), new() { ("g", "GPU Fan") }, "gpuT", "GPU", "gpuLoad", 75, 90, 30, broken);
        for (int i = 0; i <= 22; i++) t2.OnSnapshot(Snap(t.AddSeconds(i), 40, 5));
        T.Check(t2.Current == AutoTuner.Phase.Done && t2.Result?.AbortReason?.Contains("no device") == true,
            $"load error: aborted with the reason ({t2.Result?.AbortReason})");

        // 外部の負荷（ゲームなど）を待つ方式は、これまでどおり 3 分待つ
        var ext = new AutoTuner(new FanEngine(), new() { ("g", "GPU Fan") }, "gpuT", "GPU", "gpuLoad", 75, 90, 30);
        for (int i = 0; i <= 80; i++) ext.OnSnapshot(Snap(t.AddSeconds(i), 40, 5));
        T.Check(ext.Current == AutoTuner.Phase.WaitLoad, "external load: still waiting after 60 s");
        ext.Dispose();
    }
}

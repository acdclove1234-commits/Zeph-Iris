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

    // =====================================================================
    // 測定の確かさ: 負荷の一瞬の落ち込み・途中で終わった測定・読み取りの間隔
    // =====================================================================

    /// <summary>落ち着く温度: 100% で 66°C、40% で 86°C くらいの PC（LegacyTests B6 と同じ）</summary>
    private static double SteadyAt(double duty) => 40 + 26 / (0.3 + 0.7 * duty / 100);

    /// <summary>
    /// 熱モデルで自動調整を動かす（外部の負荷 = ゲームなどで測る方式）。
    /// load(経過秒, 調整) で GPU 使用率を決める（40% 未満のあいだは負荷がないので、温度は待機時へ下がっていく）
    /// </summary>
    private static (AutoTuner Tuner, double Seconds) SimulateTune(double intervalSec, Func<double, AutoTuner, float> load,
        double target = 80, double maxSeconds = 3600)
    {
        var tuner = new AutoTuner(new FanEngine(), new() { ("f1", "Front") }, "cpuT", "CPU Package", "load", target, 97, 25);
        var t0 = new DateTime(2026, 11, 15, 10, 0, 0);
        var rnd = new Random(7);
        double temp = 45, s = 0;
        for (; s < maxSeconds && tuner.Current != AutoTuner.Phase.Done; s += intervalSec)
        {
            float l = load(s, tuner);
            double duty = tuner.Current is AutoTuner.Phase.Warmup or AutoTuner.Phase.Steps ? tuner.CurrentDuty : 30;
            double goal = l >= 40 && tuner.Current is not AutoTuner.Phase.Idle ? SteadyAt(duty) : 45;
            temp += (goal - temp) * (1 - Math.Exp(-intervalSec / 25)) + (rnd.NextDouble() - 0.5) * 0.3;
            var snap = new EngineSnapshot(t0.AddSeconds(s), "標準",
                new List<FanStatus> { new("Front", "f1", "Fan", FanState.Manual, duty, duty, 600 + duty * 12, Array.Empty<CurveStatus>()) },
                new Dictionary<string, float?> { ["cpuT"] = (float)temp, ["load"] = l }, false, false, false);
            tuner.OnSnapshot(snap);
        }
        return (tuner, s);
    }

    /// <summary>ふつうの負荷: 待機（20 秒）のあいだは低く、そのあとはずっと 97%</summary>
    private static float Steady97(double s, AutoTuner t) => t.Current == AutoTuner.Phase.Idle ? 3f : 97f;

    /// <summary>
    /// ゲームの読み込み・場面の切り替えでの一瞬の落ち込み（温め中に 1 回、段の途中で 4 秒）では中止しない。
    /// 落ち込んだ段は、記録する時間（直近 15 秒）から落ち込みが外れるまで待つので、測った温度はモデルどおり
    /// </summary>
    public static void TestLoadDipDoesNotAbort()
    {
        double warmAt = -1, dipStart = -1;
        var (tuner, _) = SimulateTune(1, (s, t) =>
        {
            if (t.Current == AutoTuner.Phase.Idle) return 3f;
            if (t.Current == AutoTuner.Phase.Warmup && warmAt < 0) warmAt = s;
            if (t.Current == AutoTuner.Phase.Warmup && s == warmAt + 30) return 25f;   // 温め中の 1 回だけ
            if (t.Current == AutoTuner.Phase.Steps && t.Steps.Count == 1 && dipStart < 0) dipStart = s + 40;
            return dipStart >= 0 && s >= dipStart && s < dipStart + 4 ? 20f : 97f;
        });
        var r = tuner.Result!;
        T.Check(warmAt > 0 && dipStart > 0, "a 1-sample dip in warm-up and a 4 s dip in the 2nd step happened");
        T.Check(r.AbortReason is null, $"the dips do not abort the run ({r.AbortReason ?? "no abort"})");
        T.Check(r.Steps.Count >= 4 && r.NeedKnown(80), $"run completed with every step ({r.Steps.Count} steps)");
        foreach (var st in r.Steps)
            T.Check(Math.Abs(st.SteadyTemp - SteadyAt(st.Duty)) < 2.0, $"{st.Duty:F0}%: {st.SteadyTemp:F1} °C vs model {SteadyAt(st.Duty):F1} (dip not recorded)");
        var (need, insuff) = AutoTuner.RequiredDuty(r.Steps, 80);
        T.Check(!insuff && Math.Abs(need - 53.6) < 6, $"required duty close to theory ({need:F1}%)");
    }

    /// <summary>負荷が本当になくなった（40% 未満が 6 秒続いた）ら中止する。最初の低い値から 6 秒（＋読み取り 1 回分まで）で中止</summary>
    public static void TestLoadLostAbortsAfterSeconds()
    {
        foreach (double interval in new[] { 1.0, 0.25, 2.0 })
        {
            double lostAt = -1;
            var (tuner, seconds) = SimulateTune(interval, (s, t) =>
            {
                if (t.Current == AutoTuner.Phase.Idle) return 3f;
                if (t.Current == AutoTuner.Phase.Steps && t.Steps.Count == 1 && lostAt < 0) lostAt = s + 20;
                return lostAt >= 0 && s >= lostAt ? 5f : 97f;
            });
            var r = tuner.Result!;
            double after = seconds - interval - lostAt;
            T.Check(r.AbortReason?.Contains("GPU の負荷") == true, $"{interval} s polling: load lost → aborted ({r.AbortReason})");
            T.Check(after >= 6 - 1e-9 && after <= 6 + interval + 1e-9, $"{interval} s polling: aborted {after:F2} s after the load dropped (6 s debounce)");
        }
    }

    /// <summary>
    /// 途中で終わった測定は、完了した測定として扱わない:
    /// ・どの段も目標以下のまま終わった → 必要な回転数は「測った段以下」としか分からないので、カーブは作らない（NeedKnown = false）
    /// ・目標を超えた段まで測れていた → 測った段の間で決まるので、測れた段だけで作ってよい
    /// ・最後まで測れた → 決まる
    /// </summary>
    public static void TestPartialRunIsNotAppliedAsComplete()
    {
        // 100% と 80% を測ったところでゲームが終わった（80% の温度は目標より低い）
        double lostAt = -1;
        var (early, _) = SimulateTune(1, (s, t) =>
        {
            if (t.Current == AutoTuner.Phase.Idle) return 3f;
            if (t.Steps.Count == 2 && lostAt < 0) lostAt = s + 10;
            return lostAt >= 0 && s >= lostAt ? 5f : 97f;
        });
        var r = early.Result!;
        T.Check(r.AbortReason is not null && r.Steps.Count == 2 && r.Usable, $"aborted after 2 steps ({r.Steps.Count}, {r.AbortReason})");
        T.Check(!r.ReachedLowestStep, "the lowest planned step was not reached");
        T.Check(r.Steps.All(st => st.SteadyTemp <= 80 - 1.5), "both measured steps are under the goal (need is only 'at most 80%')");
        T.Check(!r.NeedKnown(80), "partial run with every step under the goal: need not known → no curve is applied");
        T.Check(AutoTuner.RequiredDuty(r.Steps, 80).Duty == 80, "RequiredDuty alone would have taken the lowest measured step (80%) as need");
        T.Check(r.ToText(80).Contains("以下"), "the report says 'at most', not 'about'");
        T.Check(r.NeedKnown(68), "same run, lower target where 80% is too hot: need lies between the measured steps → known");

        // 目標を超えた段まで測ってから終わった → 測れた段の間で決まる
        double lost2 = -1;
        var (bracket, _) = SimulateTune(1, (s, t) =>
        {
            if (t.Current == AutoTuner.Phase.Idle) return 3f;
            if (t.Steps.Count == 3 && lost2 < 0) lost2 = s + 10;
            return lost2 >= 0 && s >= lost2 ? 5f : 97f;
        }, target: 72);
        var b = bracket.Result!;
        T.Check(b.AbortReason is not null && !b.ReachedLowestStep && b.Steps.Any(st => st.SteadyTemp > 72 - 1.5),
            $"partial run that measured a step above the goal ({string.Join(", ", b.Steps.Select(st => $"{st.Duty:F0}%:{st.SteadyTemp:F1}"))})");
        T.Check(b.NeedKnown(72), "need is bracketed by measured steps → curve from the measured steps is allowed");

        var (full, _) = SimulateTune(1, Steady97);
        T.Check(full.Result!.AbortReason is null && full.Result.NeedKnown(80), "complete run (stopped once a step was 6 °C over the target): need known");

        // 純粋な判断
        var two = new List<AutoTuneStep> { new() { Duty = 100, SteadyTemp = 66 }, new() { Duty = 80, SteadyTemp = 70 } };
        T.Check(!AutoTuner.NeedMeasured(two, 80, reachedLowest: false), "all under goal, lowest not reached: unknown");
        T.Check(AutoTuner.NeedMeasured(two, 80, reachedLowest: true), "all under goal, lowest reached: need = lowest step");
        T.Check(AutoTuner.NeedMeasured(two, 70, reachedLowest: false), "a step above the goal: known");
        T.Check(AutoTuner.NeedMeasured(two, 60, reachedLowest: false), "even full speed too hot (insufficient): known");
        T.Check(!AutoTuner.NeedMeasured(new List<AutoTuneStep>(), 80, true), "no steps: unknown");
        T.Check(new AutoTuneResult { SensorId = "x", SensorName = "x", IdleTemp = 40, Steps = two, Fans = new() }.NeedKnown(80),
            "results made elsewhere (no ReachedLowestStep given) keep the old meaning");
    }

    /// <summary>「落ち着いた」の判断は記録の秒数で見る（読み取りの間隔が 2 秒・5 秒でも、20 回に届かないせいで毎段 3 分待たない）</summary>
    public static void TestSteadyIsTimeBased()
    {
        var t0 = new DateTime(2026, 11, 15, 10, 0, 0);
        List<(DateTime T, double Temp)> W(double interval, double seconds, Func<double, double> temp)
        {
            var w = new List<(DateTime, double)>();
            for (double s = 0; s <= seconds + 1e-9; s += interval) w.Add((t0.AddSeconds(s), temp(s)));
            return w;
        }
        T.Check(AutoTuner.IsSteady(W(2, 30, _ => 70)), "2 s polling, 30 s flat (16 samples): steady");
        T.Check(AutoTuner.IsSteady(W(5, 30, _ => 70)), "5 s polling, 30 s flat (7 samples): steady");
        T.Check(AutoTuner.IsSteady(W(0.25, 30, s => 70 + 0.2 * Math.Sin(s))), "250 ms polling, small ripple: steady");
        T.Check(!AutoTuner.IsSteady(W(1, 20, _ => 70)), "only 20 s of data: not yet");
        T.Check(!AutoTuner.IsSteady(W(2, 30, s => 70 + s * 0.04)), "still rising 1.2 °C over 30 s: not steady");
        T.Check(!AutoTuner.IsSteady(W(5, 30, s => 70 + s * 0.04)), "5 s polling, still rising: not steady");

        // 2 秒ごとの読み取りでも、各段は温度が落ち着いたら次へ進む（以前は 20 回に届かず、毎段 180 秒の上限まで待った）
        var (tuner, _) = SimulateTune(2, Steady97);
        var r = tuner.Result!;
        T.Check(r.AbortReason is null && r.Steps.Count >= 4, $"2 s polling: run completed ({r.Steps.Count} steps)");
        T.Check(r.Steps.Skip(1).All(st => st.Seconds < 150), $"2 s polling: steps end when steady, not at the 180 s cap ({string.Join(", ", r.Steps.Select(st => $"{st.Seconds:F0}s"))})");
        T.Check(r.Steps.All(st => Math.Abs(st.SteadyTemp - SteadyAt(st.Duty)) < 2.0), "2 s polling: measured temps close to the model");
    }
}

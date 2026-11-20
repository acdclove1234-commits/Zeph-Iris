// 自動セットアップ: 簡易水冷の冷却液がゆっくり温まる PC でも、落ち着く温度を正しく測り、なだらかなカーブを作る
//   熱モデル（ダイ = 速い遅れ・冷却液やヒートシンク = 遅い遅れ）を、本物の AutoTuner に 1 秒ごとのスナップショットで流す。
//   実際のファンは動かさない（FanEngine は Start しないので、固定の命令はキューに溜まるだけ）
using ProFanController;

namespace LogicTests;

public static class AutoTuneSoakTests
{
    /// <summary>冷え方のモデル</summary>
    private sealed class Cooler
    {
        public required string Name { get; init; }
        /// <summary>全力の負荷で、その回転数のまま十分待ったときに落ち着くダイの温度（回転数の低い順）</summary>
        public required (double Duty, double Temp)[] Steady { get; init; }
        /// <summary>冷却液（簡易水冷）・ヒートシンク（空冷）の時定数（秒）</summary>
        public required double TauCool { get; init; }
        /// <summary>ダイの時定数（秒）</summary>
        public double TauDie { get; init; } = 5;
        /// <summary>負荷をかけたときの、ダイと冷却液の温度差</summary>
        public double LoadOffset { get; init; } = 16;
        public double IdleDie { get; init; } = 45;
        public double IdleOffset { get; init; } = 3;

        public double SteadyAt(double duty)
        {
            var s = Steady;
            if (duty <= s[0].Duty) return s[0].Temp;
            for (int i = 1; i < s.Length; i++)
                if (duty <= s[i].Duty)
                    return s[i - 1].Temp + (duty - s[i - 1].Duty) / (s[i].Duty - s[i - 1].Duty) * (s[i].Temp - s[i - 1].Temp);
            return s[^1].Temp;
        }
    }

    /// <summary>
    /// 簡易水冷（時定数 8 分）: 全開で 58°C、30% で 66°C、5% では十分待つと 82°C（目標 80°C を超える）。
    /// ファンを下げてすぐの 3 分では、冷却液がまだ温まりきらず 70°C 前後にしか見えない
    /// </summary>
    private static Cooler Aio(double tauMinutes = 8) => new()
    {
        Name = $"AIO τ={tauMinutes:F0} min",
        TauCool = tauMinutes * 60,
        Steady = new[] { (5.0, 82.0), (30, 66.0), (40, 63.2), (50, 61.4), (65, 59.8), (80, 58.8), (100, 58.0) },
    };

    /// <summary>空冷（ヒートシンクの時定数 90 秒）: 全開で 62°C、25% で 80.5°C</summary>
    private static Cooler Air() => new()
    {
        Name = "air τ=90 s",
        TauCool = 90,
        Steady = new[] { (25.0, 80.5), (30, 78.0), (40, 73.5), (50, 70.0), (65, 66.5), (80, 64.0), (100, 62.0) },
    };

    private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    /// <summary>
    /// モデルで自動調整を最後まで動かす（外部の負荷で測る方式: 待機のあとは使用率 97%）。
    /// stopInLowestAfter: いちばん低い段に入ってからこの秒数で、ユーザーが中止する
    /// </summary>
    private static (AutoTuneResult R, double Seconds) Simulate(Cooler c, double target, double minDuty,
        double noise = 0.4, bool quantize = true, int seed = 11, double interval = 1, double? stopInLowestAfter = null)
    {
        var tuner = new AutoTuner(new FanEngine(), new() { ("f1", "Radiator") }, "cpuT", "CPU Package", "load", target, 97, minDuty);
        var t0 = new DateTime(2026, 11, 18, 10, 0, 0);
        var rnd = new Random(seed);
        double cool = c.IdleDie - c.IdleOffset, die = c.IdleDie, s = 0, lowestAt = -1;
        for (; s < 4 * 3600 && tuner.Current != AutoTuner.Phase.Done; s += interval)
        {
            bool loaded = tuner.Current is not AutoTuner.Phase.Idle;
            double duty = tuner.Current is AutoTuner.Phase.Warmup or AutoTuner.Phase.Steps ? tuner.CurrentDuty : 30;
            double coolGoal = loaded ? c.SteadyAt(duty) - c.LoadOffset : c.IdleDie - c.IdleOffset;
            cool += (coolGoal - cool) * (1 - Math.Exp(-interval / c.TauCool));
            die += (cool + (loaded ? c.LoadOffset : c.IdleOffset) - die) * (1 - Math.Exp(-interval / c.TauDie));
            double reading = die + Gauss(rnd) * noise;
            if (quantize) reading = Math.Round(reading);
            var snap = new EngineSnapshot(t0.AddSeconds(s), "標準",
                new List<FanStatus> { new("Radiator", "f1", "Fan", FanState.Manual, duty, duty, 600 + duty * 12, Array.Empty<CurveStatus>()) },
                new Dictionary<string, float?> { ["cpuT"] = (float)reading, ["load"] = loaded ? 97f : 3f }, false, false, false);
            tuner.OnSnapshot(snap);
            if (stopInLowestAfter is double stop && tuner.Current == AutoTuner.Phase.Steps && tuner.CurrentDuty <= minDuty + 1e-9)
            {
                if (lowestAt < 0) lowestAt = s;
                if (s - lowestAt >= stop) tuner.Abort("ユーザーが中止しました");
            }
        }
        return (tuner.Result!, s);
    }

    private static string Describe(AutoTuneResult r, Cooler c)
        => string.Join(", ", r.Steps.Select(st => $"{st.Duty:F0}%: {st.SteadyTemp:F1}{(st.StillRising ? "↑" : "")} (true {c.SteadyAt(st.Duty):F1}, {st.Seconds:F0}s)"));

    /// <summary>カーブの温度での回転数（点の間は直線）</summary>
    private static double DutyAt(IReadOnlyList<CurvePoint> curve, double temp)
    {
        if (temp <= curve[0].Temp) return curve[0].Duty;
        for (int i = 1; i < curve.Count; i++)
            if (temp <= curve[i].Temp)
                return curve[i - 1].Duty + (temp - curve[i - 1].Temp) / (curve[i].Temp - curve[i - 1].Temp) * (curve[i].Duty - curve[i - 1].Duty);
        return curve[^1].Duty;
    }

    /// <summary>そのカーブで 60 分間全力の負荷をかけたときの、最後の 5 分の温度と回転数の平均（ファンは 1 秒に 2% ずつ追いかける）</summary>
    private static (double Temp, double Duty) HoldWithCurve(Cooler c, IReadOnlyList<CurvePoint> curve)
    {
        double cool = c.IdleDie - c.IdleOffset, die = c.IdleDie, duty = curve[0].Duty, sumT = 0, sumD = 0;
        int n = 0;
        for (int s = 0; s < 3600; s++)
        {
            double want = DutyAt(curve, die);
            duty += Math.Clamp(want - duty, -2, 2);
            cool += (c.SteadyAt(duty) - c.LoadOffset - cool) * (1 - Math.Exp(-1 / c.TauCool));
            die += (cool + c.LoadOffset - die) * (1 - Math.Exp(-1 / c.TauDie));
            if (s >= 3300) { sumT += die; sumD += duty; n++; }
        }
        return (sumT / n, sumD / n);
    }

    private static double Steepest(IReadOnlyList<CurvePoint> curve)
        => curve.Zip(curve.Skip(1)).Max(z => (z.Second.Duty - z.First.Duty) / (z.Second.Temp - z.First.Temp));

    private static string CurveText(IReadOnlyList<CurvePoint> curve) => string.Join(" → ", curve.Select(p => $"{p.Temp}°C:{p.Duty}%"));

    /// <summary>
    /// 以前の「落ち着いた」の判断（直近 30 秒の最初と最後の 3 分の 1 の差が 0.5°C 未満・段は 60〜180 秒）は、
    /// 冷却液が 1 分に 1°C くらいずつ温まり続けるのを「落ち着いた」と見誤り、いちばん低い段を 10°C 前後低く測っていた
    /// </summary>
    public static void TestOldRuleMissesHeatSoak()
    {
        var c = Aio();
        // いちばん有利な場合でも: 30% で冷却液が温まりきってから 5% に下げる
        double cool = c.SteadyAt(30) - c.LoadOffset, die = c.SteadyAt(30);
        var window = new List<(double S, double T)>();
        double recorded = double.NaN, at = 0;
        for (int s = 1; s <= 180; s++)
        {
            cool += (c.SteadyAt(5) - c.LoadOffset - cool) * (1 - Math.Exp(-1 / c.TauCool));
            die += (cool + c.LoadOffset - die) * (1 - Math.Exp(-1 / c.TauDie));
            window.Add((s, die));
            window.RemoveAll(w => s - w.S > 30);
            double first = window.Where(w => w.S - window[0].S <= 10).Average(w => w.T);
            double last = window.Where(w => s - w.S <= 10).Average(w => w.T);
            bool oldSteady = s - window[0].S >= 25 && Math.Abs(last - first) < 0.5;
            if ((s >= 60 && oldSteady) || s == 180)
            {
                recorded = window.Where(w => s - w.S <= 15).Average(w => w.T);
                at = s;
                break;
            }
        }
        T.Info($"old rule: 5% recorded at {at:F0} s as {recorded:F1} °C, true steady {c.SteadyAt(5):F1} °C");
        T.Check(c.SteadyAt(5) - recorded > 8, $"old rule under-reads the lowest step by {c.SteadyAt(5) - recorded:F1} °C (heat soak not seen)");
    }

    /// <summary>
    /// 簡易水冷（時定数 6・8・10 分）: 新しい測り方は、必要な回転数が決まるいちばん低い段を長く測り、
    /// 曲線から落ち着く温度を予測するので、本当の温度との差が 2°C 以内。必要な回転数も安全側
    /// </summary>
    public static void TestAioHeatSoakMeasured()
    {
        foreach (double tau in new[] { 6.0, 8.0, 10.0 })
        {
            var c = Aio(tau);
            var (r, seconds) = Simulate(c, 80, minDuty: 5);
            T.Info($"{c.Name}: {seconds / 60:F1} min — {Describe(r, c)}");
            T.Check(r.AbortReason is null && r.ReachedLowestStep && r.NeedKnown(80), $"{c.Name}: run completed ({r.AbortReason ?? "ok"})");
            var low = r.Steps.Single(st => st.Duty == 5);
            T.Check(Math.Abs(low.SteadyTemp - c.SteadyAt(5)) <= 2.0, $"{c.Name}: lowest step {low.SteadyTemp:F1} °C within 2 °C of the true {c.SteadyAt(5):F1} (measured {low.MeasuredTemp:F1})");
            T.Check(low.SafeTemp >= c.SteadyAt(5) - 0.5, $"{c.Name}: the value used for the need is not below the truth ({low.SafeTemp:F1})");
            // 目標に近い段（必要な回転数に効く段）は 2°C 以内。目標より十分低い段は短く測るだけ（必要な回転数には効かない）
            var near = r.Steps.Where(st => c.SteadyAt(st.Duty) >= 80 - AutoTuner.NeedMargin - 6).ToList();
            T.Check(near.All(st => Math.Abs(st.SteadyTemp - c.SteadyAt(st.Duty)) <= 2.0),
                $"{c.Name}: steps near the target within 2 °C ({string.Join(", ", near.Select(st => $"{st.Duty:F0}%: {st.SteadyTemp - c.SteadyAt(st.Duty):+0.0;-0.0}"))})");
            var (need, insufficient) = AutoTuner.RequiredDuty(r.Steps, 80);
            // 本当に必要な回転数: 78.5°C になる回転数（5〜30% の間）
            double trueNeed = 5 + (82 - 78.5) / (82 - 66) * 25;
            T.Check(!insufficient && need >= trueNeed - 2 && need <= trueNeed + 12, $"{c.Name}: need {need:F1}% is safe and close (true {trueNeed:F1}%)");
            T.Check(seconds <= AutoSetupPlan.TuneLongSec, $"{c.Name}: finished within the long estimate ({seconds / 60:F1} min ≤ {AutoSetupPlan.TuneLongSec / 60:F0})");
            var curve = AutoTuner.BuildCurve(r.Steps, r.IdleTemp, 80, 5);
            var (hold, duty) = HoldWithCurve(c, curve);
            T.Check(hold <= 80, $"{c.Name}: the curve holds 80 °C under a 60 min load ({hold:F1} °C at {duty:F0}%): {CurveText(curve)}");
        }
    }

    /// <summary>空冷（ヒートシンクの時定数 90 秒）: すぐ落ち着くので、これまでと同じくらいの時間で、どの段も 1.5°C 以内</summary>
    public static void TestAirCoolerStillQuick()
    {
        var c = Air();
        var (r, seconds) = Simulate(c, 80, minDuty: 25);
        T.Info($"{c.Name}: {seconds / 60:F1} min — {Describe(r, c)}");
        T.Check(r.AbortReason is null && r.NeedKnown(80), "air: run completed");
        var near = r.Steps.Where(st => c.SteadyAt(st.Duty) >= 80 - AutoTuner.NeedMargin - 6).ToList();
        T.Check(near.Count >= 2 && near.All(st => Math.Abs(st.SteadyTemp - c.SteadyAt(st.Duty)) <= 1.5), "air: steps near the target within 1.5 °C of the truth");
        T.Check(r.Steps.All(st => st.SteadyTemp >= c.SteadyAt(st.Duty) - 1.5), "air: no step is read more than 1.5 °C too cool");
        var (need, _) = AutoTuner.RequiredDuty(r.Steps, 80);
        double trueNeed = 25 + (80.5 - 78.5) / (80.5 - 78) * 5;
        T.Check(Math.Abs(need - trueNeed) <= 4, $"air: need {need:F1}% close to the true {trueNeed:F1}%");
        T.Check(seconds <= AutoSetupPlan.TuneSec, $"air: within the usual estimate ({seconds / 60:F1} min ≤ {AutoSetupPlan.TuneSec / 60:F0})");
        var curve = AutoTuner.BuildCurve(r.Steps, r.IdleTemp, 80, 25);
        var (hold, duty) = HoldWithCurve(c, curve);
        T.Check(hold <= 80, $"air: the curve holds 80 °C ({hold:F1} °C at {duty:F0}%): {CurveText(curve)}");
    }

    /// <summary>読み取りが荒い（1°C 刻み・ばらつき ±1°C くらい）・間隔が 2 秒でも、いちばん低い段は 2°C 以内</summary>
    public static void TestNoisySensorStillClose()
    {
        foreach (var (c, min) in new[] { (Aio(), 5.0), (Air(), 25.0) })
        {
            foreach (int seed in new[] { 3, 21 })
            {
                var (r, _) = Simulate(c, 80, min, noise: 1.0, seed: seed, interval: 2);
                var low = r.Steps.OrderBy(st => st.Duty).First();
                T.Check(r.NeedKnown(80) && Math.Abs(low.SteadyTemp - c.SteadyAt(low.Duty)) <= 2.0,
                    $"{c.Name}, noisy, seed {seed}: lowest {low.Duty:F0}% {low.SteadyTemp:F1} °C vs true {c.SteadyAt(low.Duty):F1}");
            }
        }
    }

    /// <summary>
    /// いちばん低い段の途中（4 分）でユーザーが中止しても、その段は予測と余裕を足して残す（実際に測った温度より安全側）。
    /// 1 分も測っていなければ残さない。簡易水冷で 1〜3 分しか測っていない（曲線がまだ合わない）ときも残さない
    /// （残すと、予測が低すぎて、足りない 5% を「目標を守れる」と決めていた）
    /// </summary>
    public static void TestStopEarlyStaysSafe()
    {
        var c = Aio();
        var (r, _) = Simulate(c, 80, minDuty: 5, stopInLowestAfter: 240);
        var low = r.Steps.SingleOrDefault(st => st.Duty == 5);
        T.Check(r.AbortReason is not null && low is not null, $"stopped in the lowest step, step kept ({Describe(r, c)})");
        if (low is null) return;
        T.Check(low.StillRising && low.SoakMargin > 0, "kept step is marked as still rising with a margin");
        T.Check(low.SafeTemp >= c.SteadyAt(5) - 1.0 && low.SafeTemp > low.MeasuredTemp + 3,
            $"value used for the need {low.SafeTemp:F1} °C ≥ truth − 1 ({c.SteadyAt(5):F1}); measured only {low.MeasuredTemp:F1}");
        T.Check(r.NeedKnown(80), "lowest planned step reached → a curve can be made from it");

        var (r2, _) = Simulate(c, 80, minDuty: 5, stopInLowestAfter: 30);
        T.Check(r2.Steps.All(st => st.Duty != 5) && !r2.NeedKnown(80), "stopped 30 s into the lowest step: not kept, no curve");

        // 1〜3 分で止めたとき: 残すなら安全側（落ち着く温度 − 1°C 以上）、そうでなければ残さず、カーブも作らない
        foreach (double stop in new[] { 65.0, 120, 180 })
        {
            var (r3, _) = Simulate(c, 80, minDuty: 5, stopInLowestAfter: stop);
            var low3 = r3.Steps.SingleOrDefault(st => st.Duty == 5);
            T.Check(low3 is null ? !r3.NeedKnown(80) : low3.SafeTemp >= c.SteadyAt(5) - 1.0,
                $"stopped {stop:F0} s into the lowest step: {(low3 is null ? "not kept, no curve" : $"kept at {low3.SafeTemp:F1} °C (truth {c.SteadyAt(5):F1})")}");
        }
    }

    /// <summary>
    /// 不具合の報告にあった測定値（簡易水冷・目標 80°C・最低回転 5%）から作るカーブ:
    /// 以前は 78°C まで 5% のまま、そこから 1°C に 8% ずつ跳ね上がっていた。今は目標の 10°C 前から少しずつ上がり、
    /// 78°C で 35%、90°C で全開
    /// </summary>
    public static void TestReportedCurveRampsGradually()
    {
        var steps = new List<AutoTuneStep>
        {
            new() { Duty = 100, SteadyTemp = 55.6, AvgRpm = 1771 },
            new() { Duty = 80, SteadyTemp = 56.4 },
            new() { Duty = 65, SteadyTemp = 57.3 },
            new() { Duty = 50, SteadyTemp = 57.2 },
            new() { Duty = 40, SteadyTemp = 58.6 },
            new() { Duty = 30, SteadyTemp = 57.8 },
            new() { Duty = 5, SteadyTemp = 66.7, AvgRpm = 698 },
        };
        var old = new List<CurvePoint> { new() { Temp = 54, Duty = 5 }, new() { Temp = 71, Duty = 5 }, new() { Temp = 78, Duty = 5 }, new() { Temp = 84, Duty = 53 }, new() { Temp = 90, Duty = 100 } };
        var curve = AutoTuner.BuildCurve(steps, 50.8, 80, 5);
        T.Info("before: " + CurveText(old));
        T.Info("after:  " + CurveText(curve));
        T.Check(AutoTuner.RequiredDuty(steps, 80).Duty == 5, "need from these numbers is still the lowest step (5%)");
        T.Check(DutyAt(curve, 70) >= 15 && DutyAt(old, 70) == 5, $"at 70 °C the fan is already rising ({DutyAt(curve, 70):F0}%, was 5%)");
        T.Check(DutyAt(curve, 78) >= AutoTuner.CurveFloor, $"by 78 °C at least {AutoTuner.CurveFloor:F0}% ({DutyAt(curve, 78):F0}%)");
        T.Check(Steepest(curve) <= 6 && Steepest(old) >= 8, $"no surge: steepest {Steepest(curve):F1} %/°C (was {Steepest(old):F1})");
        T.Check(curve[^1].Duty == 100 && curve[^1].Temp <= 90, "100% by 90 °C");
        T.Check(curve.Zip(curve.Skip(1)).All(z => z.Second.Temp >= z.First.Temp + 2 && z.Second.Duty >= z.First.Duty), "monotonic, 2 °C spacing");

        // 同じ PC の熱モデルで 60 分全力: 以前のカーブは 78°C を超えるまで 5% のまま。今のカーブはもっと手前で落ち着く
        var c = Aio();
        var (oldT, oldD) = HoldWithCurve(c, old);
        var (newT, newD) = HoldWithCurve(c, curve);
        T.Info($"60 min load on the AIO model: before {oldT:F1} °C at {oldD:F0}%, after {newT:F1} °C at {newD:F0}%");
        T.Check(newT < oldT - 3 && newT <= 80, $"new curve settles lower ({newT:F1} °C vs {oldT:F1} °C)");
    }

    /// <summary>どんな測定結果・最低回転・待機温度・目標でも、カーブは右上がり・2°C 間隔・急に跳ね上がらない（CPU も GPU も同じ作り方）</summary>
    public static void TestCurveShapeSweep()
    {
        int bad = 0, cases = 0;
        string? firstBad = null;
        foreach (double target in new[] { 60.0, 70, 75, 80, 90 })
        foreach (double idle in new[] { 30.0, 45, 60 })
        foreach (double min in new[] { 0.0, 5, 25, 40 })
        foreach (double needTemp in new[] { 50.0, 70, 78, 85, 95 })
        {
            // 全開で target − 10、いちばん低い回転で needTemp になる 2 段
            var steps = new List<AutoTuneStep> { new() { Duty = 100, SteadyTemp = target - 10 }, new() { Duty = Math.Max(min, 5), SteadyTemp = needTemp } };
            var curve = AutoTuner.BuildCurve(steps, idle, target, min);
            var (need, insufficient) = AutoTuner.RequiredDuty(steps, target);
            cases++;
            bool ok = curve.Zip(curve.Skip(1)).All(z => z.Second.Temp >= z.First.Temp + 2 && z.Second.Duty >= z.First.Duty)
                      && curve[^1].Duty == 100 && curve[^1].Temp <= target + 10
                      && curve[0].Duty == Math.Round(min)
                      && (insufficient || DutyAt(curve, target - 2) >= Math.Max(Math.Max(need, min), AutoTuner.CurveFloor) - 0.5)
                      && (insufficient || idle + 3 > target - 14 || Steepest(curve) <= 6);
            if (!ok) { bad++; firstBad ??= $"target {target} idle {idle} min {min} need@{needTemp}: {CurveText(curve)}"; }
        }
        T.Check(bad == 0, $"{cases} curves: monotonic, 2 °C apart, floor by target − 2, ≤ 6 %/°C, 100% by target + 10 ({firstBad ?? "all ok"})");
    }

    /// <summary>指数の曲線の当てはめ: 落ち着く温度と時定数を拾う。平らな記録では、ほぼ今の温度のまま</summary>
    public static void TestFitApproach()
    {
        var rnd = new Random(5);
        var rise = Enumerable.Range(0, 600).Select(s => ((double)s, 70 + 12 * (1 - Math.Exp(-s / 480.0)) + Gauss(rnd) * 0.4)).ToList();
        var fit = AutoTuner.FitApproach(rise);
        T.Check(fit is { Trusted: true } f && Math.Abs(f.Final - 82) < 1.5 && f.Rising, $"10 min of an 8 min rise: final {fit?.Final:F1} (82), τ {fit?.TauSec:F0} s");
        var shortRise = rise.Take(150).ToList();
        var sf = AutoTuner.FitApproach(shortRise);
        T.Check(sf is { Trusted: false } s2 && s2.Final > shortRise[^1].Item2 + 2, $"2.5 min of it: τ hits the limit → not trusted, still predicts a rise ({sf?.Final:F1})");
        var flat = Enumerable.Range(0, 200).Select(s => ((double)s, 65 + Gauss(rnd) * 0.4)).ToList();
        var ff = AutoTuner.FitApproach(flat);
        T.Check(ff is { } f3 && Math.Abs(f3.Final - 65) < 1.0, $"flat: final {ff?.Final:F1} ≈ 65");
        T.Check(AutoTuner.FitApproach(flat.Take(50).ToList()) is null, "less than 60 s: no prediction");
    }
}

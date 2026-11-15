// ==================================================
// FanRefineTests.cs - ファン制御の精度のテスト (一瞬の跳ねの除去・更新間隔によらないならし方と上げ下げの速さ)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class FanRefineTests
{
    private const string Cpu = "/intelcpu/0/temperature/0";
    private const string Fan0 = "/lpc/nct6686d/0/control/0";

    private static CurveConfig Curve(double alpha = 1, double hysteresis = 4) => new()
    {
        Name = "CPU", SensorId = Cpu, EmaAlpha = alpha, HysteresisCelsius = hysteresis,
        Points = new() { new() { Temp = 30, Duty = 20 }, new() { Temp = 80, Duty = 100 } },
    };

    public static void TestSpikeIsIgnored()
    {
        var ev = new CurveEvaluator(Curve());
        for (int i = 0; i < 5; i++) ev.Evaluate(50);
        double steady = ev.Evaluate(50);
        double spike = ev.Evaluate(85);          // 1 回だけ跳ねた
        double after = ev.Evaluate(50);
        T.Near(spike, steady, 1e-9, "a one-sample spike does not raise the fan");
        T.Near(after, steady, 1e-9, "and nothing is left behind by the hysteresis");
        T.Near(ev.EffectiveTemp ?? -1, 50, 1e-9, "effective temperature stays at 50°C");

        // 続いて上がったときは、1 周期遅れで追従する
        ev.Evaluate(70);
        double second = ev.Evaluate(70);
        T.Near(second, CurveMath.Interpolate(new CurvePoint[] { new() { Temp = 30, Duty = 20 }, new() { Temp = 80, Duty = 100 } }, 70), 1e-9,
            "a sustained rise is followed on the 2nd sample");
        // 下がる向きの一瞬の読み違いも無視する
        ev.Evaluate(70);
        double dip = ev.Evaluate(20);
        T.Near(dip, second, 1e-9, "a one-sample dip is ignored too");
    }

    public static void TestFirstSamplesAreNotDelayed()
    {
        var ev = new CurveEvaluator(Curve());
        double first = ev.Evaluate(80);
        T.Near(first, 100, 1e-9, "the very first reading is used as is (no start-up delay)");
    }

    public static void TestSmoothingDoesNotDependOnInterval()
    {
        // 1 秒ごと 10 回と、0.25 秒ごと 40 回で、同じ時間に同じだけ追いつく
        // （跳ねの除去は 1 回分遅れるので、その差 1 秒と 0.25 秒が残る程度）
        var slow = new CurveEvaluator(Curve(alpha: 0.2, hysteresis: 0));
        var fast = new CurveEvaluator(Curve(alpha: 0.2, hysteresis: 0));
        for (int i = 0; i < 3; i++) { slow.Evaluate(40); fast.Evaluate(40, 0.25); }
        for (int i = 0; i < 10; i++) slow.Evaluate(60, 1.0);
        for (int i = 0; i < 40; i++) fast.Evaluate(60, 0.25);
        T.Info($"after 10 s: 1 s ticks {slow.EffectiveTemp:F2}°C, 0.25 s ticks {fast.EffectiveTemp:F2}°C");
        T.Near(fast.EffectiveTemp ?? 0, slow.EffectiveTemp ?? 0, 1.0, "same response time at 250 ms and 1000 ms intervals");
        T.Near(CurveMath.AlphaFor(0.2, 1), 0.2, 1e-12, "1 s step uses the configured value");
        T.Near(CurveMath.AlphaFor(1, 0.25), 1, 1e-12, "1.0 means no smoothing at any interval");
        double half = CurveMath.AlphaFor(0.2, 0.5);
        T.Near(1 - (1 - half) * (1 - half), 0.2, 1e-12, "two half-second steps equal one second");
    }

    public static void TestRampSpeedIsPerSecond()
    {
        var hw = new FakeHardware();
        hw.AddControl(Fan0, 20);
        hw.Values[Cpu] = 30;
        var fan = new FanConfig { Name = "Front", ControlId = Fan0, MinDuty = 20, MaxStepUpPerTick = 4, Curves = { Curve() } };
        var cfg = new AppConfig();
        var p = new ProfileConfig { Name = "標準" };
        p.Fans.Add(fan);
        cfg.Profiles.Add(p);
        cfg.PollingIntervalMs = 500;
        cfg.Normalize();
        var rig = new Rig(cfg, hw);
        void Step() { rig.E.StepForTest(); rig.Now += 500; }
        for (int i = 0; i < 3; i++) Step();
        hw.Values[Cpu] = 80;
        // 30 → 80°C の大きな変化は、異常値フィルターの確認（約 1 秒）と跳ねの除去（約 1 秒）のあとで上がり始める
        //（どちらも時間で決めているので、1 秒ごとの更新と同じ 2 秒）
        for (int i = 0; i < 4; i++) Step();
        double start = rig.Fan(Fan0).Duty ?? 0;
        for (int i = 0; i < 4; i++) Step();     // 2 秒
        double end = rig.Fan(Fan0).Duty ?? 0;
        T.Info($"500 ms interval: {start:F1}% → {end:F1}% in 2 s");
        T.Near(end - start, 8, 1.01, "4 %/s at a 500 ms interval rises about 8% in 2 seconds (not 16%)");
    }

    /// <summary>Silent の自動調整カーブ（83:25 → 89:62 → 95:100、約 6.2 %/°C）</summary>
    private static CurveConfig SilentCpu() => new()
    {
        Name = "CPU", SensorId = Cpu, EmaAlpha = 0.15, HysteresisCelsius = 5,
        Points = new() { new() { Temp = 83, Duty = 25 }, new() { Temp = 89, Duty = 62 }, new() { Temp = 95, Duty = 100 } },
    };

    /// <summary>
    /// 下がったあと、ヒステリシスの上乗せは 20 秒保ってから少しずつ消え、描いたカーブどおりの回転に戻る
    /// （以前は 88 → 84°C に下がると、ずっと 55.8%（84°C のカーブは 31.2%）のままだった）
    /// </summary>
    public static void TestHysteresisDecaysAfterCooling()
    {
        var c = SilentCpu();
        var pts = c.Points.ToList();
        var ev = new CurveEvaluator(c);
        for (int i = 0; i < 120; i++) ev.Evaluate(88);
        double hot = ev.Evaluate(88);
        double target = CurveMath.Interpolate(pts, 84);
        var at = new Dictionary<int, double>();
        bool belowEma = false;
        for (int i = 1; i <= 180; i++)
        {
            at[i] = ev.Evaluate(84);
            // 平滑化した温度より下（＝描いたカーブより遅い回転）にはならない
            if (ev.EffectiveTemp < 84 - 1e-9) belowEma = true;
        }
        T.Info($"88 → 84°C: hot {hot:F1}%, 15 s {at[15]:F1}%, 30 s {at[30]:F1}%, 60 s {at[60]:F1}%, 100 s {at[100]:F1}%, 180 s {at[180]:F1}% (curve at 84°C = {target:F1}%)");
        T.Near(at[15], hot, 1e-9, "for the first 20 s the hysteresis holds the duty (no change while the drop might reverse)");
        T.Check(at[60] < at[15] - 15, $"by 60 s most of the extra duty is gone ({at[60]:F1}% vs {at[15]:F1}%)");
        T.Near(at[100], target, 1.0, "by 100 s the duty is within 1% of the drawn curve");
        T.Near(at[180], target, 0.1, "and then it matches the drawn curve");
        T.Check(!belowEma, "the effective temperature never goes below the smoothed temperature");

        // 下がってすぐ戻ったときは、上乗せのまま（回転を下げてから上げ直さない）
        var ev2 = new CurveEvaluator(c);
        for (int i = 0; i < 120; i++) ev2.Evaluate(88);
        double held = 0;
        for (int i = 0; i < 10; i++) held = ev2.Evaluate(85);
        for (int i = 0; i < 10; i++) held = Math.Min(held, ev2.Evaluate(88));
        T.Near(held, hot, 1e-9, "a short dip that comes back does not change the duty");
    }

    /// <summary>
    /// 温度が上下にゆれ続けるとき: 1°C 刻みで交互にゆれるだけなら回転は変わらない。雑音が大きいときも、
    /// 上乗せが消えたあとの回転はカーブの近くで、ヒステリシスなしより細かく揺れない
    /// </summary>
    public static void TestHysteresisIgnoresNoise()
    {
        double curve84 = CurveMath.Interpolate(SilentCpu().Points.ToList(), 84);
        foreach (var (name, seed) in new[] { ("alternating 83/85°C", -1), ("84°C, σ 1.5°C noise (whole degrees)", 7), ("84°C, σ 1.5°C noise (whole degrees)", 11) })
        {
            var rng = new Random(Math.Max(seed, 0));
            double Sample(int i) => seed < 0 ? (i % 2 == 0 ? 83 : 85)
                : Math.Round(84 + 1.5 * (rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 1.5) * 2);
            var ev = new CurveEvaluator(SilentCpu());
            var none = new CurveConfig { Name = "CPU", SensorId = Cpu, EmaAlpha = 0.15, HysteresisCelsius = 0, Points = SilentCpu().Points };
            var flat = new CurveEvaluator(none);
            for (int i = 0; i < 120; i++) { ev.Evaluate(88); flat.Evaluate(88); }
            double written = ev.Evaluate(88), writtenFlat = flat.Evaluate(88);
            for (int i = 0; i < 600; i++) { ev.Evaluate(Sample(i)); flat.Evaluate(Sample(i)); }
            // 書き込みと同じく、1% 以上変わったときだけ数える
            int late = 0, lateFlat = 0;
            double sum = 0, lo = 100, hi = 0, loFlat = 100, hiFlat = 0;
            for (int i = 600; i < 1200; i++)
            {
                double s = Sample(i);
                double d = ev.Evaluate(s), df = flat.Evaluate(s);
                if (Math.Abs(d - written) >= FanSafety.WriteDeadband) { late++; written = d; }
                if (Math.Abs(df - writtenFlat) >= FanSafety.WriteDeadband) { lateFlat++; writtenFlat = df; }
                sum += d; lo = Math.Min(lo, d); hi = Math.Max(hi, d); loFlat = Math.Min(loFlat, df); hiFlat = Math.Max(hiFlat, df);
            }
            double mean = sum / 600;
            T.Info($"{name}: {late} changes ≥1% in 600 s after settling ({lateFlat} with no hysteresis), mean {mean:F1}% (curve at 84°C {curve84:F1}%), p-p {hi - lo:F1}% ({hiFlat - loFlat:F1}% with no hysteresis)");
            if (seed < 0) T.Check(late <= 3, $"{name}: at most 3 changes ≥1% in 600 s once settled ({late})");
            else T.Check(late < lateFlat, $"{name}: fewer changes than with no hysteresis ({late} vs {lateFlat})");
            T.Check(mean <= curve84 + 4, $"{name}: the extra hysteresis duty is gone (mean {mean:F1}%, latched it was about 56%)");
            T.Check(hi - lo <= Math.Max(1, hiFlat - loFlat), $"{name}: duty swings no more than with no hysteresis ({hi - lo:F1}% vs {hiFlat - loFlat:F1}%)");
        }
    }

    /// <summary>設定を変えて作り直しても、ヒステリシスが消えていく途中の状態（下がってからの時間）を引き継ぐ</summary>
    public static void TestHysteresisStateCarriesOver()
    {
        var a = new CurveEvaluator(SilentCpu());
        var b = new CurveEvaluator(SilentCpu());
        for (int i = 0; i < 120; i++) { a.Evaluate(88); b.Evaluate(88); }
        for (int i = 0; i < 30; i++) { a.Evaluate(84); b.Evaluate(84); }
        var copy = new CurveEvaluator(SilentCpu());
        copy.CopyStateFrom(b);
        double da = 0, dc = 0;
        for (int i = 0; i < 30; i++) { da = a.Evaluate(84); dc = copy.Evaluate(84); }
        T.Near(dc, da, 1e-9, "the rebuilt curve continues exactly where the old one was");
    }

    /// <summary>
    /// 跳ねの除去は「回数」ではなく「時間」: 更新間隔 250ms でも 1 秒以内の跳ね（3 回分）は無視し、
    /// 本当の上昇には 1 秒ごとのときと同じくらいの遅れ（約 1 秒）で追いつく
    /// </summary>
    public static void TestSpikeRejectionIsTimeBased()
    {
        var ev = new CurveEvaluator(Curve());
        for (int i = 0; i < 20; i++) ev.Evaluate(50, 0.25);
        double steady = ev.Evaluate(50, 0.25);
        double worst = 0;
        for (int i = 0; i < 3; i++) worst = Math.Max(worst, ev.Evaluate(85, 0.25));   // 0.75 秒の跳ね
        for (int i = 0; i < 8; i++) worst = Math.Max(worst, ev.Evaluate(50, 0.25));
        T.Near(worst, steady, 1e-9, "250 ms: a 0.75 s spike does not raise the fan (Median3 let 0.5 s spikes through)");

        int ticks = 0;
        while (ev.Evaluate(70, 0.25) < CurveMath.Interpolate(Curve().Points.ToList(), 70) - 1e-9 && ticks < 40) ticks++;
        T.Info($"250 ms: a sustained rise is followed after {(ticks + 1) * 0.25:F2} s");
        T.Check((ticks + 1) * 0.25 <= 1.25, $"a sustained rise is followed within about 1 s ({(ticks + 1) * 0.25:F2} s)");

        var med = new SpikeMedian();
        double r = 0;
        foreach (var v in new[] { 40.0, 40, 40, 90, 40 }) r = med.Next(v, 1.0);
        T.Near(r, 40, 1e-9, "1 s interval: same as the median of the last 3 readings");
        var slow = new SpikeMedian();
        foreach (var v in new[] { 40.0, 40, 90 }) r = slow.Next(v, 5.0);
        T.Near(r, 40, 1e-9, "5 s interval: still at least 3 readings");
    }

    /// <summary>異常値フィルターも時間で確かめる: 250ms ごとでも 20°C 以上の跳ねは 約 1 秒続くまで受け入れない（1 秒ごとのときは今までどおり 2 回目）</summary>
    public static void TestGuardConfirmationIsTimeBased()
    {
        var t0 = new DateTime(2026, 11, 15, 12, 0, 0);
        var g = new SensorGuard();
        g.Filter("cpu", 40, 5, t0);
        double? seen = null;
        for (int i = 1; i <= 3; i++) seen = g.Filter("cpu", 85, 5, t0.AddMilliseconds(250 * i));   // 0.75 秒の跳ね（3 回分）
        double? back = g.Filter("cpu", 41, 5, t0.AddMilliseconds(1000));
        T.Check(seen == 40 && back == 41, $"250 ms: a 0.75 s jump is held back (saw {seen}, then {back})");

        var g2 = new SensorGuard();
        g2.Filter("cpu", 40, 5, t0);
        int acceptedAt = -1;
        for (int i = 1; i <= 12 && acceptedAt < 0; i++)
            if (g2.Filter("cpu", 85, 100, t0.AddMilliseconds(250 * i)) == 85) acceptedAt = i;
        T.Check(acceptedAt * 250 is >= 750 and <= 1250, $"250 ms: a real jump is accepted after about 1 s ({acceptedAt * 250} ms)");

        var g3 = new SensorGuard();
        g3.Filter("cpu", 40, 5, t0);
        var a = g3.Filter("cpu", 85, 100, t0.AddSeconds(1));
        var b = g3.Filter("cpu", 86, 100, t0.AddSeconds(2));
        T.Check(a == 40 && b == 86, $"1 s: accepted on the 2nd reading as before ({a}, {b})");
    }
}

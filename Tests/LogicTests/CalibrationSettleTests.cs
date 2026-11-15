// ==================================================
// CalibrationSettleTests.cs - キャリブレーションのテスト (回転数が落ち着くまで待つ・回転数は Duty とともに減らない表・
//                            低い Duty で回転数が変わらない範囲の検出と、連動の回転数合わせでの使い方)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class CalibrationSettleTests
{
    private static readonly DateTime T0 = new(2026, 11, 15, 10, 0, 0);

    /// <summary>
    /// テスト用のファン: 指示した Duty の回転数へ、時定数 tau 秒でゆっくり近づく（1 次遅れ）。
    /// 毎周期 Step に今の回転数を渡し、そのあと出す Duty（CurrentDuty）で次の周期までの回転数を進める（エンジンと同じ順番）
    /// </summary>
    private static (CalibrationResult? Result, double Seconds) Run(Func<double, double> rpmFor, double tau, double startRpm,
        double interval = 1, double noise = 0, int seed = 3)
    {
        var cal = new Calibrator("f", "Fan", T0);
        var rnd = new Random(seed);
        double rpm = startRpm, t = 0;
        while (!cal.IsDone && t < 900)
        {
            t += interval;
            double target = rpmFor(cal.CurrentDuty);
            rpm += (target - rpm) * (1 - Math.Exp(-interval / tau));
            if (target == 0 && rpm < 300) rpm = 0;   // 回りきれない回転数まで落ちたら止まる
            double read = Math.Max(0, rpm + (noise > 0 ? (rnd.NextDouble() - 0.5) * 2 * noise : 0));
            cal.Step(read, T0.AddSeconds(t));
        }
        return (cal.BuildResult(), t);
    }

    private static bool Monotonic(CalibrationResult r)
    {
        var p = r.Points.OrderBy(x => x.Duty).ToList();
        for (int i = 1; i < p.Count; i++) if (p[i].Rpm < p[i - 1].Rpm) return false;
        return true;
    }

    /// <summary>ケースファン: 0〜20% はファン自身の最低回転（848 rpm）、そこから 100% の 2000 rpm まで直線</summary>
    private static double CaseFan(double d) => d <= 20 ? 848 : 848 + (d - 20) / 80 * (2000 - 848);

    /// <summary>GPU のファン: 30% 未満で止まる（ゼロ回転）、30% で 1000 rpm、100% で 3600 rpm。回転の変わり方がゆっくり</summary>
    private static double GpuFan(double d) => d < 30 ? 0 : 1000 + (d - 30) / 70 * 2600;

    /// <summary>
    /// 回転がゆっくり変わるファンでも、上がりきって（下がりきって）から測る。最大回転数が正しく、表は単調。
    /// 以前（100% を 6 秒・各段 4 秒）は、低い回転から始めた GPU のファンの最大回転数を低く測っていた
    /// </summary>
    public static void TestSlowFanSettlesBeforeMeasuring()
    {
        var (gpu, secs) = Run(GpuFan, tau: 3, startRpm: 0);
        T.Check(gpu is not null, "GPU fan: calibration finished");
        T.Near(gpu!.MaxRpm, 3600, 3600 * 0.03, "GPU fan (tau 3 s, started from 0 rpm): max rpm within 3%");
        T.Check(Monotonic(gpu), $"GPU fan: rpm never falls as duty rises ({string.Join(" ", gpu.Points.Select(p => $"{p.Duty:F0}:{p.Rpm:F0}"))})");
        foreach (double d in new[] { 90.0, 70, 50, 40 })
            T.Near(gpu.EstimateRpm(d)!.Value, GpuFan(d), GpuFan(d) * 0.04, $"GPU fan: {d}% measured close to the fan's real rpm");
        T.Check(gpu.StopDuty == 30, $"GPU fan: still spinning at 30%, stops below ({gpu.StopDuty})");
        T.Check(gpu.StartDuty is >= 30 and <= 35, $"GPU fan: starts again at 30-35% ({gpu.StartDuty})");
        T.Check(gpu.DeadZoneDuty == 0, "GPU fan: rpm rises right from the stop point (no dead zone)");
        T.Check(secs < 240, $"GPU fan: finished in {secs:F0} s (cap: 15 s at 100%, 8 s per step)");
        T.Info($"GPU fan: {secs:F0} s, max {gpu.MaxRpm:F0} rpm");

        var (caseFan, secs2) = Run(CaseFan, tau: 3, startRpm: 848, noise: 8);
        T.Check(caseFan is not null, "case fan: calibration finished");
        T.Near(caseFan!.MaxRpm, 2000, 2000 * 0.03, "case fan (tau 3 s, ±8 rpm noise): max rpm within 3%");
        T.Check(Monotonic(caseFan), "case fan: table monotonic even with noisy readings");
        T.Check(caseFan.StopDuty == 0 && caseFan.StartDuty == 0, "case fan keeps spinning at 0% (minimum rpm of its own)");
        T.Info($"case fan: {secs2:F0} s, max {caseFan.MaxRpm:F0} rpm, dead zone ≤{caseFan.DeadZoneDuty:F0}%");
    }

    /// <summary>回転数がすぐ決まるファンは、これまでどおりの時間（100% は 6 秒・各段 4 秒）で進む</summary>
    public static void TestFastFanKeepsOldTiming()
    {
        var (r, secs) = Run(d => d < 15 ? 0 : 400 + d * 12, tau: 0.01, startRpm: 900);
        T.Check(r is not null && Monotonic(r), "instant fan: finished, monotonic");
        // 6 秒 + 14 段 × 4 秒 + 回り始めを探す 15% までの 3 段 × 4 秒
        T.Check(secs == 6 + 14 * 4 + 3 * 4, $"instant fan: same duration as before ({secs:F0} s)");
        T.Check(r!.StopDuty == 15 && r.StartDuty == 15, $"stop/start found ({r.StopDuty}/{r.StartDuty})");
        T.Check(r.MaxRpm == 1600, $"max rpm exact ({r.MaxRpm})");
    }

    /// <summary>読み取りの間隔が長い（5 秒）ときも、落ち着いたかを 2 回分の読み取りで見て進む（詰まらない）</summary>
    public static void TestSlowPollingStillFinishes()
    {
        var (r, secs) = Run(CaseFan, tau: 3, startRpm: 848, interval: 5);
        T.Check(r is not null && Monotonic(r), "5 s polling: finished, monotonic");
        T.Near(r!.MaxRpm, 2000, 60, "5 s polling: max rpm");
        T.Check(secs <= 15 + 14 * 10 + 5, $"5 s polling: within the caps ({secs:F0} s)");
    }

    /// <summary>
    /// 測った表に逆転があっても（全開が上がりきる前に測れていた古い結果: 90% 3562 rpm・100% 2391 rpm）、
    /// 推定は Duty とともに回転数が減らないようにならして使う。もともと単調な表はそのまま
    /// </summary>
    public static void TestMonotoneFit()
    {
        var pts = new List<CalibrationPoint>
        {
            new() { Duty = 0, Rpm = 0 }, new() { Duty = 50, Rpm = 2000 }, new() { Duty = 80, Rpm = 3100 },
            new() { Duty = 90, Rpm = 3562 }, new() { Duty = 100, Rpm = 2391 },
        };
        var fit = CalibrationFit.Monotone(pts);
        bool mono = true;
        for (int i = 1; i < fit.Count; i++) mono &= fit[i].Rpm >= fit[i - 1].Rpm;
        T.Check(mono && fit.Count == pts.Count, "inverted pair pooled: monotonic, same duties");
        // 90・100% の平均（2976.5）はまだ 80% より低いので、80% も含めた 3 点の平均になる
        T.Near(fit[^1].Rpm, (3100 + 3562 + 2391) / 3.0, 0.01, "the inverted run becomes the average of the points it overlaps");
        T.Check(fit[^1].Rpm == fit[^2].Rpm && fit[^2].Rpm == fit[^3].Rpm, "80/90/100% pooled");
        T.Check(fit.Take(2).Select(p => p.Rpm).SequenceEqual(new double[] { 0, 2000 }), "points before the inversion unchanged");

        var legacy = new CalibrationResult { ControlId = "g", MaxRpm = 2391, Points = pts };
        T.Check(legacy.EstimateRpm(100) >= legacy.EstimateRpm(90), "old stored table: estimate does not fall from 90% to 100%");
        var good = new List<CalibrationPoint> { new() { Duty = 0, Rpm = 0 }, new() { Duty = 50, Rpm = 900 }, new() { Duty = 100, Rpm = 1800 } };
        T.Check(CalibrationFit.Monotone(good).Select(p => p.Rpm).SequenceEqual(new double[] { 0, 900, 1800 }), "monotonic table unchanged");
        T.Check(CalibrationFit.Monotone(new List<CalibrationPoint>()).Count == 0, "empty table");
    }

    /// <summary>
    /// 低い Duty で回転数が変わらない範囲（ファン自身の最低回転数）を見つけて記録する。
    /// 最低回転数（MinDuty）は自動では上げない（止まる直前 + 5% の決まりだけ）
    /// </summary>
    public static void TestDeadZoneDetected()
    {
        var (r, _) = Run(CaseFan, tau: 3, startRpm: 848, noise: 8);
        T.Near(r!.DeadZoneDuty, 20, 5, "case fan flat at 848 rpm from 0 to 20%: dead-zone edge recorded");
        T.Check(r.EffectiveDeadZone() == r.DeadZoneDuty, "effective dead zone = recorded");

        P[] flat = { new(0, 848), new(5, 848), new(10, 850), new(15, 849), new(20, 852), new(25, 950), new(30, 1030), new(50, 1300), new(100, 2000) };
        T.Check(CalibrationFit.DeadZoneEdge(Pts(flat)) == 20, "flat 0-20% then rising: edge 20%");
        P[] stopThenFlat = { new(0, 0), new(5, 0), new(10, 610), new(15, 612), new(20, 615), new(25, 700), new(60, 1200), new(100, 1800) };
        T.Check(CalibrationFit.DeadZoneEdge(Pts(stopThenFlat)) == 20, "stops below 10%, flat 10-20%: edge 20%");
        P[] rising = { new(0, 0), new(20, 500), new(25, 600), new(30, 700), new(100, 1800) };
        T.Check(CalibrationFit.DeadZoneEdge(Pts(rising)) == 0, "rpm rises from the stop point: no dead zone");
        P[] narrow = { new(0, 800), new(5, 805), new(10, 900), new(100, 2000) };
        T.Check(CalibrationFit.DeadZoneEdge(Pts(narrow)) == 0, "flat only 5% wide: not a dead zone");
        P[] dc = { new(0, 1200), new(50, 1205), new(100, 1210) };
        T.Check(CalibrationFit.DeadZoneEdge(Pts(dc)) == 0, "fan that ignores PWM (all flat): no edge");

        // 古い版で測った結果（DeadZoneDuty を記録していない）も、測定点から求める
        var old = new CalibrationResult { ControlId = "c", MaxRpm = 2000, Points = Pts(flat) };
        T.Check(old.DeadZoneDuty == 0 && old.EffectiveDeadZone() == 20, "legacy result: dead zone found from its points");
    }

    private readonly record struct P(double Duty, double Rpm);
    private static List<CalibrationPoint> Pts(IEnumerable<P> p) => p.Select(x => new CalibrationPoint { Duty = x.Duty, Rpm = x.Rpm }).ToList();

    /// <summary>
    /// 連動の回転数合わせ: 回転数が変わらない範囲の中の点は使わず、その上の端をいちばん低い Duty にする。
    /// 以前は同じ回転数の点のうち回転数の順でいちばん前（0%）を返し、リーダーが少し上がると 0% → 21% と跳んだ
    /// </summary>
    public static void TestFollowUsesDeadZoneAsLowEnd()
    {
        P[] flat = { new(0, 848), new(5, 848), new(10, 850), new(15, 849), new(20, 852), new(25, 950), new(30, 1030), new(50, 1300), new(100, 2000) };
        var cal = new CalibrationResult { ControlId = "c", MaxRpm = 2000, Points = Pts(flat), DeadZoneDuty = 20 };
        T.Check(cal.EstimateDuty(500) == 20, $"below the fan's own minimum rpm: low end is the dead-zone edge ({cal.EstimateDuty(500)})");
        T.Check(cal.EstimateDuty(850) == 20, $"at the flat rpm: dead-zone edge ({cal.EstimateDuty(850)})");
        T.Near(cal.EstimateDuty(1000)!.Value, 25 + (1000 - 950) / 80.0 * 5, 0.01, "above the dead zone: interpolated as before");
        T.Check(cal.EstimateDuty(3000) == 100, "above max: 100%");
        double prev = cal.EstimateDuty(700)!.Value;
        bool smooth = true;
        for (double rpm = 710; rpm <= 2000; rpm += 10)
        {
            double d = cal.EstimateDuty(rpm)!.Value;
            smooth &= d >= prev - 1e-9 && d - prev <= 1.5;
            prev = d;
        }
        T.Check(smooth, "duty rises smoothly with the requested rpm (no 0% → 20% jump)");

        // エンジンを通しても同じ: リーダー 10%（420 rpm）→ ケースファンは 20%
        const string Leader = "/lpc/nct6686d/0/control/0", Follower = "/lpc/nct6686d/0/control/2";
        var hw = new FakeHardware();
        var lead = hw.AddControl(Leader, 50);
        var foll = hw.AddControl(Follower, 50);
        hw.Values["/intelcpu/0/temperature/0"] = 45;
        var cfg = new AppConfig();
        var p = new ProfileConfig { Name = "標準" };
        p.Fans.Add(new FanConfig { Name = "Radiator", ControlId = Leader, Mode = FanModes.Fixed, FixedDuty = 10, MinDuty = 5, MaxStepDownPerTick = 100 });
        p.Fans.Add(new FanConfig { Name = "Case", ControlId = Follower, Mode = FanModes.Follow, FollowControlId = Leader, FollowMatchRpm = true, MinDuty = 5 });
        cfg.Profiles.Add(p);
        cfg.Calibrations.Add(new CalibrationResult { ControlId = Leader, MaxRpm = 1500, Points = Pts(new P[] { new(0, 300), new(100, 1500) }) });
        cfg.Calibrations.Add(new CalibrationResult { ControlId = Follower, MaxRpm = 2000, Points = Pts(flat) });   // 古い版の結果（記録なし）
        cfg.Normalize();
        var rig = new Rig(cfg, hw);
        rig.Tick(30);
        T.Near(lead.Register, 10, 0.5, "leader at 10% (420 rpm)");
        T.Near(foll.Register, 20, 0.5, "follower runs at the dead-zone edge (20%), not 5% inside the flat region");
    }
}

// ==================================================
// ThermalPlantTests.cs - 温度とファンをつないだ「閉ループ」のテスト
//   （ファンの回転で冷え方が変わる部品の温度をまねてエンジンを実際に動かし、目標温度モードの精度と回転の静かさを測る:
//    雑音のある一定負荷での書き込み回数・1 回だけの跳ね・ゆっくりの上げ下げでの積分のため込み・負荷の段差・共振回避帯）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

/// <summary>
/// テスト用の部品の温度（1 次遅れ）。毎周期、ファンの実際の出力（チップの PWM）から温度を進めて、センサーの値にする。
///   温度の変化 = (室温 + 消費電力 × 熱抵抗(Duty) − 温度) ÷ 時定数
///   熱抵抗(Duty) = 0.45 − 0.27 × Duty/100（°C/W。100% で 0.18、0% で 0.45）
/// 雑音（正規分布）・整数への丸め・跳ねを足せる。乱数は種を固定しているので、毎回同じ結果になる
/// </summary>
internal sealed class ThermalPlant
{
    public const string Sensor = "/intelcpu/0/temperature/0";
    public const string Control = "/lpc/nct6686d/0/control/0";

    public double Ambient = 30;
    public double TauSeconds = 25;
    public double PowerW;
    /// <summary>センサーの雑音の標準偏差（°C）</summary>
    public double NoiseSd;
    /// <summary>センサーの値を整数°C に丸める（Intel の CPU Package などと同じ）</summary>
    public bool Integer;
    /// <summary>部品の本当の温度</summary>
    public double Temp;
    /// <summary>始めてからの時間（秒）</summary>
    public double Time;
    public readonly Rig Rig;
    public readonly FakeControl Fan;
    public readonly int IntervalMs;
    /// <summary>周期ごとの (経過秒, 本当の温度, チップの出力 %, それまでの書き込み回数, PI の出力 %)</summary>
    public readonly List<(double T, double Temp, double Duty, int Writes, double Pi)> Trace = new();
    private readonly Random _rng;
    private double _spike;
    private int _spikeTicks;

    public static double Resistance(double duty) => 0.45 - 0.27 * Math.Clamp(duty, 0, 100) / 100;
    /// <summary>この電力で、この温度に落ち着く Duty</summary>
    public static double DutyFor(double powerW, double temp, double ambient = 30) => (0.45 - (temp - ambient) / powerW) / 0.27 * 100;
    public double SteadyTemp(double duty) => Ambient + PowerW * Resistance(duty);

    /// <param name="startDuty">始めのファンの出力（BIOS の値として置く）。温度はこの出力で落ち着いた値から始める</param>
    public ThermalPlant(FanConfig fan, double powerW, double startDuty, int intervalMs = 1000, int seed = 1)
    {
        PowerW = powerW;
        IntervalMs = intervalMs;
        _rng = new Random(seed);
        var hw = new FakeHardware();
        Fan = hw.AddControl(fan.ControlId, (float)startDuty);
        var cfg = new AppConfig();
        var p = new ProfileConfig { Name = "標準" };
        p.Fans.Add(fan);
        cfg.Profiles.Add(p);
        cfg.PollingIntervalMs = intervalMs;
        cfg.Normalize();
        Temp = SteadyTemp(startDuty);
        hw.Values[Sensor] = (float)Reading();
        Rig = new Rig(cfg, hw);
    }

    /// <summary>目標温度モードのファン</summary>
    public static FanConfig TargetFan(double target = 70, double aggressiveness = 5, double up = 8, double down = 2) => new()
    {
        Name = "CPU", ControlId = Control, Mode = FanModes.Target, TargetSensorId = Sensor,
        TargetTemp = target, TargetAggressiveness = aggressiveness, MinDuty = 25,
        MaxStepUpPerTick = up, MaxStepDownPerTick = down,
    };

    /// <summary>これから ticks 周期のあいだ、センサーの値に amount を足す（本当の温度は変わらない）</summary>
    public void Spike(double amount, int ticks = 1)
    {
        _spike = amount;
        _spikeTicks = ticks;
    }

    private double Gauss()
    {
        double u1 = 1 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private double Reading()
    {
        double v = Temp + (NoiseSd > 0 ? NoiseSd * Gauss() : 0);
        if (_spikeTicks > 0)
        {
            v += _spike;
            _spikeTicks--;
        }
        return Integer ? Math.Round(v) : v;
    }

    /// <summary>1 周期: センサーに今の温度を見せてエンジンを動かし、チップに書かれた出力で温度を進める</summary>
    public void Step()
    {
        Rig.Hw.Values[Sensor] = (float)Reading();
        Rig.E.StepForTest();
        Rig.Now += IntervalMs;
        double dt = IntervalMs / 1000.0;
        Time += dt;
        Temp += dt * (SteadyTemp(Fan.Register) - Temp) / TauSeconds;
        Trace.Add((Time, Temp, Fan.Register, Fan.Writes.Count, PiOutput));
    }

    public void Run(double seconds)
    {
        int n = (int)Math.Round(seconds * 1000 / IntervalMs);
        for (int i = 0; i < n; i++) Step();
    }

    /// <summary>今の PI（目標温度モード）の出力（%）</summary>
    public double PiOutput => Rig.Last?.Fans.FirstOrDefault(f => f.ControlId == Control)?.Curves.FirstOrDefault()?.Request ?? double.NaN;

    // ---- 集計（from 秒より後、to 秒まで）
    private List<(double T, double Temp, double Duty, int Writes, double Pi)> Window(double from, double to) => Trace.Where(x => x.T > from && x.T <= to).ToList();
    public double MinTemp(double from, double to) => Window(from, to).Min(x => x.Temp);
    public double MaxTemp(double from, double to) => Window(from, to).Max(x => x.Temp);
    public double MaxDuty(double from, double to) => Window(from, to).Max(x => x.Duty);
    public double RmsError(double target, double from, double to) => Math.Sqrt(Window(from, to).Average(x => (x.Temp - target) * (x.Temp - target)));
    public double DutySpread(double from, double to) => Window(from, to).Max(x => x.Duty) - Window(from, to).Min(x => x.Duty);
    /// <summary>チップへの書き込み回数</summary>
    public int WritesIn(double from, double to)
    {
        var w = Window(from, to);
        int before = Trace.LastOrDefault(x => x.T <= from).Writes;
        return w.Count == 0 ? 0 : w[^1].Writes - before;
    }
    /// <summary>出力が 1 周期で atLeast（%）以上変わった時刻（共振回避帯の端の切り替えなど）</summary>
    public List<double> Jumps(double from, double to, double atLeast)
    {
        var w = Trace.Where(x => x.T > from - IntervalMs / 1000.0 && x.T <= to).ToList();
        return w.Zip(w.Skip(1)).Where(z => Math.Abs(z.Second.Duty - z.First.Duty) >= atLeast).Select(z => z.Second.T).ToList();
    }
    /// <summary>from 秒より後で温度が最後に target ± tol を外れていた時刻までの時間（ずっと入っていれば 0。最後まで外れていれば null）</summary>
    public double? SettleTime(double target, double tol, double from)
    {
        var w = Trace.Where(x => x.T > from).ToList();
        if (w.Count == 0 || Math.Abs(w[^1].Temp - target) > tol) return null;
        var outside = w.Where(x => Math.Abs(x.Temp - target) > tol).ToList();
        return outside.Count == 0 ? 0 : outside[^1].T - from;
    }
}

public static class ThermalPlantTests
{
    private static string Sec(double? s) => s is double v ? $"{v:F0} s" : "never";

    /// <summary>雑音のある一定負荷で、目標温度モードの回転が細かく揺れ続けない（以前は 300 秒に 28 回（標準）〜 72 回（きびきび）書いていた）</summary>
    public static void TestTargetQuietSteadyState()
    {
        foreach (var (aggr, maxWrites, maxSpread) in new[] { (5.0, 4, 4.0), (10.0, 8, 6.0) })
        foreach (int seed in new[] { 1, 2, 3 })
        {
            var pl = new ThermalPlant(ThermalPlant.TargetFan(aggressiveness: aggr), 150, ThermalPlant.DutyFor(150, 70), seed: seed) { NoiseSd = 1.5, Integer = true };
            pl.Run(720);
            double writes = pl.WritesIn(120, 720) / 2.0, rms = pl.RmsError(70, 120, 720), spread = pl.DutySpread(120, 720);
            T.Info($"aggressiveness {aggr}, seed {seed}: {writes:F1} writes / 300 s, temperature rms {rms:F2}°C, duty p-p {spread:F1}%");
            T.Check(writes <= maxWrites, $"aggressiveness {aggr} seed {seed}: at most {maxWrites} writes per 300 s with 1.5°C noise ({writes:F1})");
            T.Check(rms <= 1.0, $"aggressiveness {aggr} seed {seed}: temperature stays within 1°C rms of the target ({rms:F2})");
            T.Check(spread <= maxSpread, $"aggressiveness {aggr} seed {seed}: duty peak-to-peak at most {maxSpread}% ({spread:F1})");
        }
    }

    /// <summary>1 回だけの跳ね（読み取りの乱れ）で PI の出力が動かない。更新間隔 250ms でも 1 秒以内の跳ねは無視する</summary>
    public static void TestTargetSpikeIsIgnored()
    {
        foreach (var (interval, ticks) in new[] { (1000, 1), (250, 3) })
        {
            var pl = new ThermalPlant(ThermalPlant.TargetFan(), 150, ThermalPlant.DutyFor(150, 70), intervalMs: interval);
            pl.Run(200);
            double before = pl.PiOutput, moved = 0;
            pl.Spike(15, ticks);
            for (int i = 0; i < 20_000 / interval; i++)
            {
                pl.Step();
                moved = Math.Max(moved, Math.Abs(pl.PiOutput - before));
            }
            T.Info($"{interval} ms interval, +15°C for {ticks * interval} ms: PI output moved {moved:F2}% (was 3.8% at 1000 ms before)");
            T.Check(moved < 0.5, $"{interval} ms: a +15°C spike of {ticks * interval} ms moves the PI output by less than 0.5% ({moved:F2})");
        }
    }

    /// <summary>
    /// ゆっくりの上げ下げで出力どおりに回せない間、PI が積分をため込まない（実際に出した Duty に合わせる）。
    /// 40 秒だけ負荷が上がったあと、ため込んだ分で必要以上に回して冷やしすぎない
    /// </summary>
    public static void TestTargetTrackingAntiWindup()
    {
        var pl = new ThermalPlant(ThermalPlant.TargetFan(aggressiveness: 10, up: 1, down: 0.5), 130, ThermalPlant.DutyFor(130, 70));
        pl.Run(200);
        pl.PowerW = 230; pl.Run(40);
        pl.PowerW = 130; pl.Run(500);
        double peak = pl.MaxDuty(200, 740), min = pl.MinTemp(240, 740);
        double gap = pl.Trace.Where(x => x.T > 200 && x.T <= 240).Max(x => x.Pi - x.Duty);
        T.Info($"burst 130→230→130 W, slew 1/0.5 %/s: duty peak {peak:F1}% (99.6% without tracking), lowest {min:F2}°C (60.67°C without), " +
               $"largest PI output − applied gap {gap:F1}%, settled ±1°C in {Sec(pl.SettleTime(70, 1, 240))}");
        T.Check(gap <= 35, $"the PI output stays near the slew-limited duty ({gap:F1}% apart)");
        T.Check(peak <= 95, $"no wound-up push to full speed after a short burst ({peak:F1}%)");
        T.Check(min >= 62.5, $"undershoot after the burst stays small ({min:F2}°C)");
    }

    /// <summary>負荷の段差のあと、目標の ±1°C に戻る（静かにした分、遅くなりすぎていない）</summary>
    public static void TestTargetLoadStepsStillSettle()
    {
        {
            var pl = new ThermalPlant(ThermalPlant.TargetFan(), 150, ThermalPlant.DutyFor(150, 70));
            pl.Run(300);
            pl.PowerW = 250; pl.Run(300);
            pl.PowerW = 150; pl.Run(500);
            double min = pl.MinTemp(600, 1100);
            double? settle = pl.SettleTime(70, 1, 600);
            T.Info($"150→250→150 W: hottest {pl.MaxTemp(300, 600):F2}°C, lowest after the drop {min:F2}°C, settled ±1°C in {Sec(settle)} (before: 80.74 / 65.47°C / 183 s)");
            T.Check(min >= 63.5, $"undershoot after a long full-speed period stays bounded ({min:F2}°C)");
            T.Check(settle is <= 300, $"back within ±1°C within 300 s ({Sec(settle)})");
        }
        {
            var pl = new ThermalPlant(ThermalPlant.TargetFan(), 100, ThermalPlant.DutyFor(100, 70));
            pl.Run(200);
            pl.PowerW = 200; pl.Run(400);
            double max = pl.MaxTemp(200, 600);
            double? settle = pl.SettleTime(70, 1, 200);
            T.Info($"100→200 W: hottest {max:F2}°C, settled ±1°C in {Sec(settle)} (before: 87.69°C / 292 s)");
            T.Check(max <= 90, $"overshoot after a load step stays bounded ({max:F2}°C)");
            T.Check(settle is <= 400, $"back within ±1°C within 400 s ({Sec(settle)})");
        }
    }

    /// <summary>
    /// 必要な回転が共振回避帯（55〜65%）の中にあるとき、目標温度モードが端を行ったり来たりしない
    /// （以前は 1000 秒に 40 回、10% ずつ音が変わっていた）。帯の中には留まらず、温度は目標の近くに保つ
    /// </summary>
    public static void TestTargetBandDoesNotLimitCycle()
    {
        var f = ThermalPlant.TargetFan();
        f.AvoidBands.Add(new DutyBand { From = 55, To = 65 });
        var pl = new ThermalPlant(f, 40 / ThermalPlant.Resistance(60), 60);
        pl.Run(1100);
        var jumps = pl.Jumps(100, 1100, 5);
        double gap = jumps.Zip(jumps.Skip(1)).Select(z => z.Second - z.First).DefaultIfEmpty(double.MaxValue).Min();
        double lo = pl.MinTemp(100, 1100), hi = pl.MaxTemp(100, 1100);
        bool inside = pl.Trace.Where(x => x.T > 5).Any(x => x.Duty > 55.5 && x.Duty < 64.5);
        T.Info($"band 55-65%, needed ≈ 60%: {jumps.Count} edge jumps in 1000 s, shortest gap {gap:F0} s, temperature {lo:F2}..{hi:F2}°C");
        T.Check(jumps.Count <= 8, $"at most 8 edge jumps in 1000 s ({jumps.Count})");
        T.Check(gap >= FanSafety.BandFlipHoldSeconds, $"edges are held at least {FanSafety.BandFlipHoldSeconds:F0} s ({gap:F0} s)");
        T.Check(!inside, "the fan never stays inside the band");
        T.Check(lo >= 67.8 && hi <= 72.2, $"temperature stays within about ±2°C of the target ({lo:F2}..{hi:F2})");
    }

    /// <summary>共振回避帯の端の選び方: 目標温度モードは反対側に BandFlipHoldSeconds 続いてから切り替える。待ち時間 0（カーブなど）はすぐ切り替える</summary>
    public static void TestBandSideHold()
    {
        var band = new DutyBand { From = 55, To = 65 };
        double t = 0;
        T.Check(FanSafety.BandSide(band, 63, 0, ref t, 1, 60) == 1 && FanSafety.BandSide(band, 57, 0, ref t, 1, 60) == -1, "entering the band picks the nearer edge");
        T.Check(FanSafety.BandSide(band, 60.5, 1, ref t, 1, 60) == 1 && FanSafety.BandSide(band, 59.5, 1, ref t, 1, 60) == 1, "within 10% of the middle keeps the side");
        t = 0;
        int side = 1, flippedAt = -1;
        for (int i = 1; i <= 90 && flippedAt < 0; i++)
        {
            side = FanSafety.BandSide(band, 57, side, ref t, 1, 60);
            if (side == -1) flippedAt = i;
        }
        T.Check(flippedAt == 60, $"target mode flips to the lower edge only after 60 s on that side (at {flippedAt} s)");
        t = 0;
        side = 1;
        for (int i = 0; i < 40; i++) side = FanSafety.BandSide(band, 57, side, ref t, 1, 60);
        side = FanSafety.BandSide(band, 63, side, ref t, 1, 60);
        T.Check(side == 1 && t == 0, "going back to the current side resets the wait");
        for (int i = 0; i < 40; i++) side = FanSafety.BandSide(band, 57, side, ref t, 1, 60);
        T.Check(side == 1, "the wait starts over after a return");
        t = 0;
        T.Check(FanSafety.BandSide(band, 57, 1, ref t, 1, 0) == -1, "with no hold time (curve mode) the side flips at once, as before");
        t = 0;
        side = -1;
        for (int i = 0; i < 4; i++) side = FanSafety.BandSide(band, 63, side, ref t, 0.25, 60);
        T.Check(side == -1, "the hold is in seconds: 4 ticks at 250 ms do not flip");
    }

    /// <summary>熱い（目標を BandFlipHotMargin 以上超えている）ときは、下の端から上の端へは待たずに切り替える。上から下へは熱くても待つ</summary>
    public static void TestBandSideFlipsUpAtOnceWhenHot()
    {
        var band = new DutyBand { From = 55, To = 65 };
        double t = 0;
        T.Check(FanSafety.BandSide(band, 63, -1, ref t, 1, 60, hot: true) == 1 && t == 0, "hot: lower → upper edge at once");
        t = 0;
        T.Check(FanSafety.BandSide(band, 63, -1, ref t, 1, 60, hot: false) == -1, "not hot: lower → upper edge still waits");
        t = 0;
        T.Check(FanSafety.BandSide(band, 57, 1, ref t, 1, 60, hot: true) == 1, "hot: upper → lower edge still waits (never slower at once)");
        t = 0;
        T.Check(FanSafety.BandSide(band, 60.5, -1, ref t, 1, 60, hot: true) == -1, "hot: within 10% of the middle keeps the side, as before");
    }

    private static (Rig Rig, FakeHardware Hw) BandRig(double aggressiveness, double startDuty, double missingDuty = 70)
    {
        var hw = new FakeHardware();
        hw.AddControl(ThermalPlant.Control, (float)startDuty);
        hw.Values[ThermalPlant.Sensor] = 70;
        var fan = ThermalPlant.TargetFan(aggressiveness: aggressiveness);
        fan.AvoidBands.Add(new DutyBand { From = 55, To = 65 });
        var cfg = new AppConfig();
        cfg.Safety.MissingSensorDuty = missingDuty;
        var p = new ProfileConfig { Name = "標準" };
        p.Fans.Add(fan);
        cfg.Profiles.Add(p);
        cfg.Normalize();
        return (new Rig(cfg, hw), hw);
    }

    /// <summary>
    /// 目標温度モード・共振回避帯 55〜65% の下の端にいるとき、負荷が増えて目標を大きく超えたら、60 秒待たずに上の端へ上げる
    /// （ゆったり（PI の出力がゆっくり上がる）設定で、熱いのに 1 分間 55% のまま待っていた）
    /// </summary>
    public static void TestTargetBandDoesNotWaitWhenHot()
    {
        var (rig, hw) = BandRig(aggressiveness: 1, startDuty: 59.5);
        rig.Tick(5);
        T.Near(rig.Hw.Controls[ThermalPlant.Control].Register, 55, 0.6, "at the target, PI output 59.5% sits at the lower edge");
        hw.Values[ThermalPlant.Sensor] = 76;
        rig.Tick(25);
        double duty = rig.Hw.Controls[ThermalPlant.Control].Register;
        T.Check(duty >= 64.5, $"6°C over the target: upper edge within 25 s, not after the 60 s hold ({duty:F1}%)");
    }

    /// <summary>
    /// 目標温度モード: センサーが消えたあとの「最後の回転より下げない」保持が、共振回避帯の端の待ち時間で下の端に押さえられない
    /// （保持 63% が帯の上半分なのに、60 秒間 55% のままだった）
    /// </summary>
    public static void TestTargetBandHoldNotAppliedWhileSensorMissing()
    {
        var (rig, hw) = BandRig(aggressiveness: 5, startDuty: 57, missingDuty: 63);
        rig.Tick(5);
        T.Near(rig.Hw.Controls[ThermalPlant.Control].Register, 55, 0.6, "PI output 57% sits at the lower edge");
        hw.Values.Remove(ThermalPlant.Sensor);
        rig.Tick(3);
        double duty = rig.Hw.Controls[ThermalPlant.Control].Register;
        T.Check(rig.Fan(ThermalPlant.Control).State == FanState.SensorMissing, $"sensor gone: missing-sensor hold ({rig.Fan(ThermalPlant.Control).State})");
        T.Check(duty >= 63, $"the 63% hold goes to the upper edge at once, not below the hold for 60 s ({duty:F1}%)");
    }
}

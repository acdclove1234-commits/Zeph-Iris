// ==================================================
// FanSafetyTests.cs - ファン制御の安全対策のテスト (緊急全開の即時反映・解除の保持・書き込みの確認と再送・
//                     部品ごとのエラーの切り分け・開き直し・ポンプ監視・GPU 保護・スリープ・部品交換の保留)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

/// <summary>テスト用の制御端子。LHM と同じく「同じ値の書き込みは省略」し、PWM は 0〜255 の段階に丸める</summary>
internal sealed class FakeControl : IFanControl
{
    private readonly FakeHardware _hw;

    public FakeControl(FakeHardware hw, string id, float register)
    {
        _hw = hw;
        Id = id;
        Register = register;
        BiosDuty = register;
    }

    public string Id { get; }
    /// <summary>チップの PWM 出力（読み戻しで見える値）</summary>
    public float Register;
    /// <summary>BIOS 制御に戻したときの値</summary>
    public float BiosDuty;
    /// <summary>書き込みを黙って飛ばす（他のソフトがチップを使用中のときの LHM と同じ）</summary>
    public bool DropWrites;
    /// <summary>この値（%）未満の書き込みで例外を出す（0 なら出さない。101 ならすべて）</summary>
    public float ThrowBelow;
    /// <summary>BIOS に戻す命令で例外を出す回数</summary>
    public int ThrowOnDefaultCount;
    /// <summary>BIOS に戻す命令を黙って飛ばす回数（LHM は BIOS 制御のつもりになるが、チップは手動のまま）</summary>
    public int DropDefaultCount;
    /// <summary>チップが本当に BIOS 制御になっているか（InBios は LHM 側の「つもり」）</summary>
    public bool ChipInBios = true;
    public bool InBios = true;
    public float? LastSoftware;
    public readonly List<float> Writes = new();
    public int Defaults;

    public void SetSoftware(float duty)
    {
        if (duty < ThrowBelow) throw new InvalidOperationException("write failed");
        if (!InBios && LastSoftware == duty) return; // LHM: 同じ値は書かない
        InBios = false;
        LastSoftware = duty;
        Writes.Add(duty);
        _hw.WriteLog.Add(Id);
        if (!DropWrites)
        {
            Register = (byte)(duty * 2.55) / 2.55f;
            ChipInBios = false;
        }
    }

    public void SetDefault()
    {
        if (ThrowOnDefaultCount > 0)
        {
            ThrowOnDefaultCount--;
            throw new InvalidOperationException("restore failed");
        }
        if (InBios) return; // LHM: すでに BIOS 制御なら何もしない
        InBios = true;
        if (DropDefaultCount > 0)
        {
            DropDefaultCount--; // 例外は出ない
            return;
        }
        Defaults++;
        ChipInBios = true;
        Register = BiosDuty;
    }

    public float LastWrite => Writes.Count > 0 ? Writes[^1] : float.NaN;
}

/// <summary>テスト用のハードウェア（センサーの値を自由に変えられる）</summary>
internal sealed class FakeHardware : IFanHardware
{
    public readonly Dictionary<string, float?> Values = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, FakeControl> Controls = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>書き込んだ端子の順番</summary>
    public readonly List<string> WriteLog = new();
    /// <summary>読むと例外になるセンサー</summary>
    public readonly HashSet<string> ThrowOnRead = new(StringComparer.OrdinalIgnoreCase);
    public float? GpuCore;
    public float? GpuHotSpot;
    /// <summary>ハードウェアの一覧に載せる温度センサー（名前・部品の種類。空なら一覧に温度はない）</summary>
    public readonly List<SensorInfo> Temps = new();
    /// <summary>すべての部品の読み取りに失敗する</summary>
    public bool FailAll;
    public int UpdateCalls;
    public bool Disposed;

    public FakeControl AddControl(string id, float register)
    {
        var c = new FakeControl(this, id, register);
        Controls[id] = c;
        return c;
    }

    public int Update()
    {
        UpdateCalls++;
        return FailAll ? HardwareCount : 0;
    }

    public int HardwareCount => 3;
    public bool Exists(string? id) => id is not null && (Values.ContainsKey(id) || Controls.ContainsKey(id));

    public float? Value(string? id)
    {
        if (id is null) return null;
        if (ThrowOnRead.Contains(id)) throw new InvalidOperationException($"read failed: {id}");
        if (Controls.TryGetValue(id, out var c)) return c.Register;
        return Values.TryGetValue(id, out var v) ? v : null;
    }

    public string? NameOf(string? id) => Exists(id) ? Temps.FirstOrDefault(t => t.Id == id)?.Name ?? id : null;
    public IFanControl? FindControl(string? id) => id is not null && Controls.TryGetValue(id, out var c) ? c : null;
    public float? LoadFor(string? sensorId) => null;

    public void GpuTemps(string controlId, out float? core, out float? hotSpot)
    {
        core = GpuCore;
        hotSpot = GpuHotSpot;
    }

    public Dictionary<string, float?> SnapshotValues() => new(Values, StringComparer.OrdinalIgnoreCase);

    public HardwareCatalog BuildCatalog(bool includeGpuFans)
        => new(new List<DeviceInfo>(), new List<HardwareGroup>(), Temps, new List<ControlInfo>(), true);

    public void Dispose() => Disposed = true;
}

/// <summary>エンジンを偽物のハードウェアと時計で動かす（1 周期 = 1 秒）</summary>
internal sealed class Rig
{
    public long Now = 1_000_000;
    public FakeHardware Hw;
    public readonly FanEngine E = new();
    public readonly List<(string Message, AlertLevel Level)> Alerts = new();
    public EngineSnapshot? Last;
    public int FactoryCalls;

    public Rig(AppConfig cfg, FakeHardware hw)
    {
        Hw = hw;
        E.HardwareFactory = () => { FactoryCalls++; return Hw; };
        E.ClockMs = () => Now;
        E.Alert += (m, l) => Alerts.Add((m, l));
        E.SnapshotReady += s => Last = s;
        E.InitForTest(cfg, cfg.Profiles[0].Name);
    }

    /// <summary>n 周期進める（1 周期ごとに時計を 1 秒進める）</summary>
    public void Tick(int n = 1)
    {
        for (int i = 0; i < n; i++)
        {
            E.StepForTest();
            Now += 1000;
        }
    }

    public FanStatus Fan(string id) => Last!.Fans.First(f => f.ControlId == id);
    public bool Alerted(string part, AlertLevel? level = null) => Alerts.Any(a => a.Message.Contains(part) && (level is null || a.Level == level));
}

public static class FanSafetyTests
{
    private const string Cpu = "/intelcpu/0/temperature/0";
    private const string Fan0 = "/lpc/nct6686d/0/control/0";
    private const string Rpm0 = "/lpc/nct6686d/0/fan/0";
    private const string Pump = "/lpc/nct6686d/0/control/1";
    private const string PumpRpm = "/lpc/nct6686d/0/fan/1";

    private static AppConfig Config(params FanConfig[] fans)
    {
        var cfg = new AppConfig();
        var p = new ProfileConfig { Name = "標準" };
        foreach (var f in fans) p.Fans.Add(f);
        cfg.Profiles.Add(p);
        cfg.Normalize();
        return cfg;
    }

    /// <summary>温度に関係なく 20% のカーブ（緊急全開だけを確かめるため）</summary>
    private static FanConfig FlatFan(string id = Fan0, string? rpm = null) => new()
    {
        Name = "Front",
        ControlId = id,
        RpmSensorId = rpm,
        MinDuty = 20,
        Curves =
        {
            new CurveConfig { Name = "CPU", SensorId = Cpu, Points = new() { new() { Temp = 30, Duty = 20 }, new() { Temp = 110, Duty = 20 } } },
        },
    };

    private static FanConfig FixedFan(string id, double duty, string? rpm = null) => new()
    {
        Name = id.EndsWith("/1") ? "Pump" : "Front",
        ControlId = id,
        RpmSensorId = rpm,
        Mode = FanModes.Fixed,
        FixedDuty = duty,
        MinDuty = 20,
    };

    private static FanConfig PumpFan(double duty = 60) => new()
    {
        Name = "Pump",
        ControlId = Pump,
        RpmSensorId = PumpRpm,
        Role = FanRoles.Pump,
        Mode = FanModes.Fixed,
        FixedDuty = duty,
        MinDuty = 20,
        PumpAlertRpm = 800,
    };

    // =====================================================================
    // 純粋な判断（FanSafety）
    // =====================================================================

    public static void TestPureDecisions()
    {
        // 安全のための全開は一気に、通常はゆっくり
        T.Check(FanSafety.IsDirect(FanState.Emergency, FanModes.Curve, 0) && FanSafety.IsDirect(FanState.GpuGuard, FanModes.Curve, 0)
                && FanSafety.IsDirect(FanState.PumpAlert, FanModes.Fixed, 0), "emergency / GPU guard / pump alert are direct");
        T.Check(!FanSafety.IsDirect(FanState.SensorError, FanModes.Curve, 1) && FanSafety.IsDirect(FanState.SensorError, FanModes.Curve, 2),
            "sensor error becomes direct on the 2nd consecutive tick");
        T.Check(!FanSafety.IsDirect(FanState.Active, FanModes.Curve, 0) && FanSafety.IsDirect(FanState.Active, FanModes.Follow, 0)
                && !FanSafety.IsDirect(FanState.Boost, FanModes.Curve, 0), "normal control is slew-limited (follow stays direct)");
        T.Near(FanSafety.Slew(20, 100, 8, 2, direct: false), 28, 1e-9, "slew up by max step");
        T.Near(FanSafety.Slew(60, 30, 8, 2, direct: false), 58, 1e-9, "slew down by max step");
        T.Near(FanSafety.Slew(20, 100, 8, 2, direct: true), 100, 1e-9, "direct jumps to demand");

        // 1% の不感帯
        T.Check(!FanSafety.NeedsWrite(50, 50.4, 20, false), "0.4% change is not written");
        T.Check(FanSafety.NeedsWrite(50, 51, 20, false) && FanSafety.NeedsWrite(50, 49, 20, false), "1% change is written");
        T.Check(FanSafety.NeedsWrite(20.5, 20, 20, false), "reaching the floor exactly is written");
        T.Check(FanSafety.NeedsWrite(99.6, 100, 20, false), "reaching 100% exactly is written");
        T.Check(!FanSafety.NeedsWrite(100, 100, 20, false) && FanSafety.NeedsWrite(50, 50, 20, true), "same value skipped unless forced");

        // 緊急全開の保持
        T.Check(FanSafety.NextEmergency(false, true, true, false, 0), "emergency triggers at the limit");
        T.Check(FanSafety.NextEmergency(true, false, false, false, 10_000), "emergency held for the minimum hold time");
        T.Check(!FanSafety.NextEmergency(true, false, false, false, 30_000), "emergency released after hold when cool");
        T.Check(FanSafety.NextEmergency(true, false, false, true, 600_000), "emergency stays while the trigger sensor is unreadable");
        T.Check(FanSafety.NextEmergency(true, false, true, false, 600_000), "emergency stays above limit - 5");

        // 開き直しの待ち時間と周期
        T.Check(FanSafety.ReopenDelayMs(0) == 5000 && FanSafety.ReopenDelayMs(1) == 15000 && FanSafety.ReopenDelayMs(2) == 60000 && FanSafety.ReopenDelayMs(9) == 60000,
            "reopen backoff 5 / 15 / 60 / 60 s");
        T.Check(FanSafety.NextTickMs(1000, 1500, 1000) == 2000, "fixed cadence: next = previous + interval");
        T.Check(FanSafety.NextTickMs(1000, 2600, 1000) == 3600, "late tick skips ahead instead of bursting");

        // 温度として使える値
        T.Check(FanSafety.IsPlausibleTemp(45.5f) && !FanSafety.IsPlausibleTemp(127f) && !FanSafety.IsPlausibleTemp(128f)
                && !FanSafety.IsPlausibleTemp(-128f) && !FanSafety.IsPlausibleTemp(0f) && !FanSafety.IsPlausibleTemp(null) && FanSafety.IsPlausibleTemp(127.5f),
            "sentinel temperatures 127 / 128 / -128 rejected");

        // 切り替えの確認（2 回続いたら）
        int n = 0;
        bool g = FanSafety.Debounce(false, true, ref n, 2);
        bool g2 = FanSafety.Debounce(g, true, ref n, 2);
        T.Check(!g && g2, "debounce needs 2 consecutive readings");
        n = 0;
        g = FanSafety.Debounce(false, true, ref n, 2);
        g = FanSafety.Debounce(g, false, ref n, 2);
        g = FanSafety.Debounce(g, true, ref n, 2);
        T.Check(!g, "a single spike between normal readings does not switch");
    }

    public static void TestReadbackCounter()
    {
        var s = new ReadbackCheck();
        T.Check(FanSafety.CheckReadback(ref s, 60, 59.6) == ReadbackAction.Ok, "within 2% is a match");
        T.Check(FanSafety.CheckReadback(ref s, 60, 30) == ReadbackAction.Ok, "1st mismatch: wait (timing)");
        T.Check(FanSafety.CheckReadback(ref s, 60, 30) == ReadbackAction.Reassert, "2nd mismatch: re-assert");
        FanSafety.CheckReadback(ref s, 60, 30);
        FanSafety.CheckReadback(ref s, 60, 30);
        FanSafety.CheckReadback(ref s, 60, 30);
        T.Check(s.Failures == 4 && !s.Alerted, "4 failed re-asserts: no alert yet");
        T.Check(FanSafety.CheckReadback(ref s, 60, 30) == ReadbackAction.ReassertAndAlert, "5th failed re-assert: alert");
        T.Check(FanSafety.CheckReadback(ref s, 60, 30) == ReadbackAction.Reassert, "alert only once");
        T.Check(FanSafety.CheckReadback(ref s, 60, 60.2) == ReadbackAction.Recovered && s.Failures == 0, "recovered after alert");
        // チップ側で少しずつ近づいている途中は待つ
        var r = new ReadbackCheck();
        FanSafety.CheckReadback(ref r, 100, 20);
        T.Check(FanSafety.CheckReadback(ref r, 100, 50) == ReadbackAction.Ok && FanSafety.CheckReadback(ref r, 100, 80) == ReadbackAction.Ok,
            "readback ramping toward the target is not a failure");
        // チップが回転をゆっくり変える（MSI など）: 1 周期に 0.5% ずつ近づくなら、書き直しも知らせもしない
        var slow = new ReadbackCheck();
        bool flagged = false;
        for (double v = 42; v > 36.5; v -= 0.5)
            flagged |= FanSafety.CheckReadback(ref slow, 36, v) is ReadbackAction.ReassertAndAlert or ReadbackAction.Reassert;
        T.Check(!flagged, "readback slowly approaching the target (0.5%/tick) is not a mismatch");
    }

    /// <summary>回り始めの後押し: 止まっているファンに、止まる回転と回り始める回転の間の指示を出したら、少しの間だけ回り始める回転にする</summary>
    public static void TestSpinUpKick()
    {
        const double start = 38, stop = 22;   // 38% で回り始め、22% 未満で止まるファン
        var s = new SpinUpState();
        T.Near(FanSafety.SpinUp(ref s, 30, 0, start, stop, 1000), start + FanSafety.SpinUpMargin, 1e-9, "stopped fan asked for 30% (between stop and start) → kicked to start + 3%");
        T.Near(FanSafety.SpinUp(ref s, 30, 0, start, stop, 2000), start + FanSafety.SpinUpMargin, 1e-9, "kick holds while still stopped");
        T.Near(FanSafety.SpinUp(ref s, 30, 450, start, stop, 2500), 30, 1e-9, "spinning → back to the asked duty at once");
        T.Near(FanSafety.SpinUp(ref s, 30, 450, start, stop, 3000), 30, 1e-9, "a spinning fan is never kicked");

        var low = new SpinUpState();
        T.Near(FanSafety.SpinUp(ref low, 15, 0, start, stop, 1000), 15, 1e-9, "below the stop duty (meant to stop): no kick");
        T.Near(FanSafety.SpinUp(ref low, 50, 0, start, stop, 1000), 50, 1e-9, "already above the start duty: nothing to do");
        T.Near(FanSafety.SpinUp(ref low, 30, null, start, stop, 1000), 30, 1e-9, "no RPM reading: no kick");
        T.Near(FanSafety.SpinUp(ref low, 30, 0, 0, 0, 1000), 30, 1e-9, "not calibrated: no kick");

        var dead = new SpinUpState();
        FanSafety.SpinUp(ref dead, 30, 0, start, stop, 0);
        T.Near(FanSafety.SpinUp(ref dead, 30, 0, start, stop, FanSafety.SpinUpMaxMs + 1), 30, 1e-9, "still stopped after 3 s: give up (stall warning takes over)");
        T.Near(FanSafety.SpinUp(ref dead, 30, 0, start, stop, FanSafety.SpinUpMaxMs + 1000), 30, 1e-9, "no immediate retry");
        T.Near(FanSafety.SpinUp(ref dead, 30, 0, start, stop, FanSafety.SpinUpMaxMs + FanSafety.SpinUpRetryMs + 2), start + FanSafety.SpinUpMargin, 1e-9, "retried after 30 s");
    }

    // =====================================================================
    // エンジン全体（偽物のハードウェア）
    // =====================================================================

    /// <summary>S1: 緊急全開は最初の周期で 100% を書く（以前は 8%/周期で 10 秒以上かかった）</summary>
    public static void TestEmergencyWritesFullOnFirstTick()
    {
        var hw = new FakeHardware();
        var ctl = hw.AddControl(Fan0, 20);
        hw.Values[Cpu] = 50;
        var cfg = Config(FlatFan());
        cfg.EmergencyTemperature = 90;
        var rig = new Rig(cfg, hw);
        rig.Tick(3);
        T.Near(rig.Fan(Fan0).Duty ?? -1, 20, 0.5, "fan runs at 20% before the emergency");
        T.Check(rig.Fan(Fan0).State == FanState.Active, "state active before");

        hw.Values[Cpu] = 65; rig.Tick();
        hw.Values[Cpu] = 80; rig.Tick();
        T.Check(!rig.Last!.Emergency && ctl.LastWrite < 21, "no emergency below the limit");
        hw.Values[Cpu] = 92; rig.Tick();
        T.Check(rig.Last!.Emergency && rig.Fan(Fan0).State == FanState.Emergency, "emergency triggered at 92°C");
        T.Near(ctl.LastWrite, 100, 1e-6, "100% written on the first emergency tick (S1)");
        T.Check(rig.Alerted("全ファンを 100%", AlertLevel.Danger), "danger alert raised");
    }

    /// <summary>S7: きっかけのセンサーが読めなくなっても緊急全開を続ける。最低 30 秒は続ける</summary>
    public static void TestEmergencyLatchedWhileSensorUnreadable()
    {
        var hw = new FakeHardware();
        var ctl = hw.AddControl(Fan0, 20);
        hw.Values[Cpu] = 75;
        var cfg = Config(FlatFan());
        cfg.EmergencyTemperature = 90;
        var rig = new Rig(cfg, hw);
        rig.Tick(2);
        hw.Values[Cpu] = 92; rig.Tick();
        T.Check(rig.Last!.Emergency, "emergency on");

        hw.Values[Cpu] = null; // センサーが読めなくなった
        rig.Tick(60);
        T.Check(rig.Last!.Emergency && ctl.LastWrite == 100, "emergency stays latched for 60 s while the trigger sensor is unreadable");
        T.Check(rig.Alerts.Count(a => a.Message.Contains("読めなくなりました")) == 1, "lost-sensor warning raised once");

        hw.Values[Cpu] = 50; // 読めるようになり、温度も下がっていた
        rig.Tick(3);
        T.Check(!rig.Last!.Emergency, "released once the sensor reads cool again");
        T.Check(rig.Alerted("通常の制御に戻りました", AlertLevel.Info), "release notified");

        // 最低 30 秒の保持
        var hw2 = new FakeHardware();
        hw2.AddControl(Fan0, 20);
        hw2.Values[Cpu] = 75;
        var rig2 = new Rig(cfg, hw2);
        rig2.Tick(2);
        hw2.Values[Cpu] = 92; rig2.Tick();
        hw2.Values[Cpu] = 75; // すぐに下がった
        rig2.Tick(20);
        T.Check(rig2.Last!.Emergency, "still in emergency 20 s after trigger (minimum hold 30 s)");
        rig2.Tick(12);
        T.Check(!rig2.Last!.Emergency, "released after the 30 s hold");
    }

    /// <summary>S7: 緊急温度に近い高い温度では「固着」と判定しない</summary>
    public static void TestNoStuckNearEmergency()
    {
        var t0 = new DateTime(2026, 10, 26, 12, 0, 0);
        var g = new SensorGuard { NoStuckAbove = 85 };
        double? v = null;
        for (int i = 0; i < 400; i++) v = g.Filter("cpu", 90, i < 100 ? 3 : 97, t0.AddSeconds(i));
        T.Check(v == 90 && !g.IsStuck("cpu"), "constant 90°C under changing load is NOT declared stuck when >= threshold");
        var g2 = new SensorGuard { NoStuckAbove = 85 };
        for (int i = 0; i < 400; i++) v = g2.Filter("cpu", 52, i < 100 ? 3 : 97, t0.AddSeconds(i));
        T.Check(v is null && g2.IsStuck("cpu"), "constant 52°C under changing load is still declared stuck");
    }

    /// <summary>
    /// S7b: 固着の判定に使う使用率は CPU と GPU だけ。SSD の「使用率」（読み書きの量）は、温度が 1°C 刻みで何分も同じでも普通なので使わない
    /// （SSD の温度を使うカーブで、音楽を流しているだけでファンが全開になった）
    /// </summary>
    public static void TestStuckLoadOnlyCpuAndGpu()
    {
        foreach (var t in new[] { LibreHardwareMonitor.Hardware.HardwareType.Cpu, LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia,
                                  LibreHardwareMonitor.Hardware.HardwareType.GpuAmd, LibreHardwareMonitor.Hardware.HardwareType.GpuIntel })
            T.Check(HardwareHub.LoadMovesTemperature(t), $"{t}: load is used for the stuck check");
        foreach (var t in new[] { LibreHardwareMonitor.Hardware.HardwareType.Storage, LibreHardwareMonitor.Hardware.HardwareType.Motherboard,
                                  LibreHardwareMonitor.Hardware.HardwareType.Memory, LibreHardwareMonitor.Hardware.HardwareType.Network })
            T.Check(!HardwareHub.LoadMovesTemperature(t), $"{t}: load is NOT used for the stuck check");
        // 使用率がない（null）ときは、同じ値が何分続いても固着にしない
        var t0 = new DateTime(2026, 11, 10, 6, 30, 0);
        var g = new SensorGuard { NoStuckAbove = 85 };
        double? v = null;
        for (int i = 0; i < 600; i++) v = g.Filter("ssd", 41, null, t0.AddSeconds(i));
        T.Check(v == 41 && !g.IsStuck("ssd"), "constant 41°C with no load reading for 10 min is not stuck");
    }

    /// <summary>S8: 端子が見つからないリーダーに連動するファンは、最低回転ではなく全開</summary>
    public static void TestFollowerOfMissingLeader()
    {
        var hw = new FakeHardware();
        var ctl = hw.AddControl(Fan0, 30);
        hw.Values[Cpu] = 50;
        var leader = FlatFan("/lpc/nct6686d/0/control/5");
        leader.Name = "Leader";
        var follower = new FanConfig { Name = "Follower", ControlId = Fan0, Mode = FanModes.Follow, FollowControlId = leader.ControlId, MinDuty = 20 };
        var rig = new Rig(Config(leader, follower), hw);
        rig.Tick();
        T.Check(rig.Fan(leader.ControlId).State == FanState.NotFound, "leader not found");
        T.Check(rig.Fan(Fan0).State == FanState.SensorError, "follower → sensor error (was MinDuty before)");
        rig.Tick();
        T.Near(ctl.LastWrite, 100, 1e-6, "follower at 100% by the 2nd tick");
    }

    /// <summary>連動しているファンも、自分のカーブ（連動先が見ていない温度）が高いときはそちらで回す</summary>
    public static void TestFollowerKeepsOwnCurveAsFloor()
    {
        const string Gpu = "/gpu-nvidia/0/temperature/0";
        const string Lead = "/lpc/nct6686d/0/control/5";
        var hw = new FakeHardware();
        var lead = hw.AddControl(Lead, 30);
        var ctl = hw.AddControl(Fan0, 30);
        hw.Values[Cpu] = 40;
        hw.Values[Gpu] = 80;
        var leader = FlatFan(Lead);
        leader.Name = "Radiator";
        leader.MaxStepUpPerTick = 100;
        var follower = new FanConfig
        {
            Name = "Case", ControlId = Fan0, Mode = FanModes.Follow, FollowControlId = Lead, MinDuty = 20, MaxStepUpPerTick = 100,
            Curves = { new CurveConfig { Name = "GPU", SensorId = Gpu, EmaAlpha = 1, Points = new() { new() { Temp = 40, Duty = 20 }, new() { Temp = 80, Duty = 70 } } } },
        };
        var rig = new Rig(Config(leader, follower), hw);
        rig.Tick(5);
        T.Check(ctl.LastWrite >= 65, $"hot GPU: follower runs on its own GPU curve, not the 20% leader ({ctl.LastWrite})");
        hw.Values[Gpu] = 40;
        rig.Tick(120);
        T.Check(ctl.LastWrite <= lead.LastWrite + 6, $"cool GPU: back to following the leader ({ctl.LastWrite} vs leader {lead.LastWrite})");
    }

    /// <summary>S8: ポンプの下限は、ゆっくり上げている途中でも守る</summary>
    public static void TestPumpFloorWhileRamping()
    {
        var hw = new FakeHardware();
        var pump = hw.AddControl(Pump, 10); // BIOS が 10% にしていた
        hw.Values[PumpRpm] = 2500;
        var rig = new Rig(Config(PumpFan(60)), hw);
        rig.Tick();
        T.Check(pump.Writes.Count == 1 && pump.Writes[0] >= FanEngine.PumpHardMinDuty, $"first pump write respects the 40% floor ({pump.Writes[0]})");
        rig.Tick(5);
        T.Check(pump.Writes.All(w => w >= FanEngine.PumpHardMinDuty), "pump never written below 40% while ramping");
    }

    /// <summary>S6: 書いた値が反映されていなければ書き直す（同じ値で省略されないようにずらす）。5 回で知らせる</summary>
    public static void TestReadbackReassert()
    {
        var hw = new FakeHardware();
        var ctl = hw.AddControl(Fan0, 30);
        ctl.DropWrites = true; // ほかのソフトがチップを使っていて、書き込みが黙って飛ばされる
        var f = FixedFan(Fan0, 60);
        f.MaxStepUpPerTick = 100; // 最初の周期で 60% を書く
        var rig = new Rig(Config(f), hw);
        rig.Tick();
        T.Check(ctl.Writes.Count == 1 && ctl.Register == 30, "first write dropped by the chip");
        rig.Tick();
        T.Check(ctl.Writes.Count == 1, "1st mismatch: wait one tick");
        rig.Tick();
        T.Check(ctl.Writes.Count == 2 && Math.Abs(ctl.Writes[1] - 60) < 0.01 && ctl.Writes[1] != 60f, $"2nd mismatch: re-asserted with a nudge ({ctl.Writes[1]:R})");
        rig.Tick();
        T.Check(ctl.Writes.Count == 3, "re-assert again (alternating value defeats LHM's same-value skip)");
        T.Check(rig.Fan(Fan0).Duty is double d && Math.Abs(d - 30) < 0.5, "display shows the actual duty while the write is not applied");
        rig.Tick(3);
        T.Check(rig.Alerts.Count(a => a.Message.Contains("反映されていません")) == 1, "alerted once after 5 failed re-asserts");

        ctl.DropWrites = false;
        rig.Tick(2);
        int writes = ctl.Writes.Count;
        T.Near(ctl.Register, 60, 0.5, "write applied once the chip is free");
        rig.Tick(5);
        T.Check(ctl.Writes.Count == writes, "no more writes once the readback matches");
        T.Check(rig.E.Health == EngineHealth.Ok, "health back to OK");
    }

    /// <summary>S32: 1% 未満の変化は書かない（チップの占有を減らす）</summary>
    public static void TestDeadband()
    {
        var hw = new FakeHardware();
        var ctl = hw.AddControl(Fan0, 50);
        var cfg = Config(FixedFan(Fan0, 50));
        var rig = new Rig(cfg, hw);
        rig.Tick(10);
        T.Check(ctl.Writes.Count == 1, $"steady duty written once in 10 ticks ({ctl.Writes.Count})");
        var c2 = cfg.Clone();
        c2.Profiles[0].Fans[0].FixedDuty = 50.5;
        rig.E.ApplyConfig(c2, "標準");
        rig.Tick(3);
        T.Check(ctl.Writes.Count == 1, "0.5% change not written");
        var c3 = cfg.Clone();
        c3.Profiles[0].Fans[0].FixedDuty = 52;
        rig.E.ApplyConfig(c3, "標準");
        rig.Tick();
        T.Check(ctl.Writes.Count == 2 && Math.Abs(ctl.LastWrite - 52) < 1e-4, "2% change written");
    }

    /// <summary>S4: 1 つのファンのエラーで、ほかのファン（ポンプ）の制御を止めない。ポンプを先に書く</summary>
    public static void TestPerFanFailureIsolated()
    {
        var hw = new FakeHardware();
        var fan = hw.AddControl(Fan0, 40);
        var pump = hw.AddControl(Pump, 60);
        hw.Values[Rpm0] = 900;
        hw.Values[PumpRpm] = 2500;
        // 設定の順番はファンが先でも、ポンプを先に書く
        var rig = new Rig(Config(FixedFan(Fan0, 50, Rpm0), PumpFan(70)), hw);
        rig.Tick();
        T.Check(hw.WriteLog.IndexOf(Pump) >= 0 && hw.WriteLog.IndexOf(Pump) < hw.WriteLog.IndexOf(Fan0), "pump written before other fans");
        rig.Tick(3);

        hw.ThrowOnRead.Add(Rpm0); // このファンの処理だけ毎回失敗する
        int pumpWrites = pump.Writes.Count;
        rig.Tick();
        T.Check(rig.Fan(Fan0).State == FanState.SensorError && fan.LastWrite == 100, "failing fan goes to its safe action (100%)");
        T.Check(rig.Fan(Pump).State == FanState.Active, "pump keeps being controlled");
        rig.Tick(5);
        T.Check(rig.Alerted("エラーが続いています", AlertLevel.Warning), "repeated fan failure alerted");
        T.Check(rig.E.Health == EngineHealth.Degraded, "health degraded");
        T.Check(rig.FactoryCalls == 1, "no hub re-open for a single failing fan");

        hw.ThrowOnRead.Clear();
        rig.Tick();
        T.Check(rig.Fan(Fan0).State == FanState.Active, "fan recovers when the error stops");
        T.Check(rig.E.Health == EngineHealth.Ok, "health OK again");
        T.Check(pump.Writes.Count >= pumpWrites, "pump writes continued");
    }

    /// <summary>S4: 書き込みで例外が出たファンは全開にする（全開も書けなければ BIOS に戻す）</summary>
    public static void TestWriteFailureSafeAction()
    {
        var hw = new FakeHardware();
        var fan = hw.AddControl(Fan0, 50);
        var cfg = Config(FixedFan(Fan0, 50));
        var rig = new Rig(cfg, hw);
        rig.Tick(2);
        fan.ThrowBelow = 100; // 100% 未満の書き込みだけ失敗する
        var c2 = cfg.Clone();
        c2.Profiles[0].Fans[0].FixedDuty = 40;
        rig.E.ApplyConfig(c2, "標準");
        rig.Tick();
        T.Check(rig.Fan(Fan0).State == FanState.SensorError && fan.LastWrite == 100, "write failure → safe action writes 100%");
        rig.Tick(6);
        T.Check(fan.Writes.Min() >= 50 && fan.LastWrite == 100, "never left at a lower duty while writes fail");
        fan.ThrowBelow = 0;
        rig.Tick(3);
        T.Check(rig.Fan(Fan0).State == FanState.Active && fan.LastWrite < 100, "normal control resumes (ramping down slowly)");

        // 全開も書けないときは BIOS に戻す
        var hw2 = new FakeHardware();
        var fan2 = hw2.AddControl(Fan0, 50);
        var rig2 = new Rig(cfg, hw2);
        rig2.Tick(2);
        hw2.ThrowOnRead.Add(Fan0); // 処理が失敗し、
        fan2.ThrowBelow = 101;     // 全開の書き込みも失敗する
        rig2.Tick();
        T.Check(fan2.InBios && fan2.Defaults == 1, "fan that cannot even be set to 100% is returned to BIOS control");
        rig2.Tick(3);
        T.Check(fan2.InBios, "stays under BIOS control while it keeps failing");
    }

    /// <summary>S4: 全体のエラーが続いたら BIOS に戻し、5 → 15 秒待ってハードウェアを開き直す（以前は永久に一時停止）</summary>
    public static void TestHubReopenBackoff()
    {
        var hw = new FakeHardware();
        var fan = hw.AddControl(Fan0, 40);
        var rig = new Rig(Config(FixedFan(Fan0, 50)), hw);
        rig.Tick(2);
        T.Check(!fan.InBios, "fan under software control");

        hw.FailAll = true;
        rig.Tick(4);
        T.Check(rig.E.Health != EngineHealth.Recovering && !fan.InBios, "4 failing ticks: still waiting");
        rig.Tick();
        T.Check(rig.E.Health == EngineHealth.Recovering && fan.InBios && hw.Disposed, "5th failing tick: BIOS restored, hub closed");
        T.Check(rig.Alerted("BIOS 制御に戻しました", AlertLevel.Danger), "danger alert");

        hw.FailAll = false;
        hw.Disposed = false;
        rig.Tick(4);
        T.Check(rig.FactoryCalls == 1, "no re-open before 5 s");
        rig.Tick();
        T.Check(rig.FactoryCalls == 2 && rig.E.Health == EngineHealth.Ok, "re-opened after 5 s");
        rig.Tick();
        T.Check(!fan.InBios && rig.Fan(Fan0).State == FanState.Active, "control resumed after re-open");
        T.Check(rig.Alerted("制御を再開しました", AlertLevel.Info), "recovery notified");

        // すぐにまた失敗した: 2 回目は 15 秒待つ
        hw.FailAll = true;
        rig.Tick(5);
        T.Check(rig.E.Health == EngineHealth.Recovering, "recovering again");
        hw.FailAll = false;
        rig.Tick(14);
        T.Check(rig.FactoryCalls == 2, "2nd re-open waits longer than 5 s");
        rig.Tick();
        T.Check(rig.FactoryCalls == 3 && rig.E.Health == EngineHealth.Ok, "re-opened after 15 s");

        // 開き直し自体が失敗する場合も、例外で止まらず待ち続ける
        int calls = rig.FactoryCalls;
        rig.E.HardwareFactory = () => { rig.FactoryCalls++; throw new InvalidOperationException("driver not ready"); };
        hw.FailAll = true;
        rig.Tick(5 + 60 + 60 + 1);
        T.Check(rig.FactoryCalls > calls && rig.E.Health == EngineHealth.Recovering, "failing re-open keeps retrying with backoff");
    }

    /// <summary>S19: ポンプの回転数が読めない状態が 5 秒続いたら危険として知らせ、100% にする（起動直後は待つ）</summary>
    public static void TestPumpRpmLost()
    {
        var hw = new FakeHardware();
        var pump = hw.AddControl(Pump, 60);
        hw.Values[PumpRpm] = null; // 回転数が読めない
        var rig = new Rig(Config(PumpFan(60)), hw);
        rig.Tick(12);
        T.Check(!rig.Alerted("回転数が読めません"), "no alert during the 12 s start-up grace");
        rig.Tick(4);
        T.Check(!rig.Alerted("回転数が読めません"), "no alert before 5 s of missing rpm");
        rig.Tick(2);
        T.Check(rig.Alerted("回転数が読めません", AlertLevel.Danger), "danger alert after 5 s of missing pump rpm");
        T.Check(rig.Fan(Pump).State == FanState.PumpAlert && pump.LastWrite == 100, "pump forced to 100%");

        hw.Values[PumpRpm] = 2500;
        rig.Tick(11);
        T.Check(rig.Fan(Pump).State == FanState.Active, "pump protection released after 10 good readings");

        // 端子が見つからない（制御できない）ポンプも監視を続ける
        var hw2 = new FakeHardware();
        hw2.Values[PumpRpm] = null;
        var rig2 = new Rig(Config(PumpFan(60)), hw2);
        rig2.Tick(20);
        T.Check(rig2.Fan(Pump).State == FanState.NotFound && rig2.Alerted("回転数が読めません", AlertLevel.Danger), "pump without a control is still monitored");
    }

    /// <summary>S30: GPU 保護は 127/128°C を無視し、2 回続けて熱いときだけ全開にする</summary>
    public static void TestGpuGuardDebounce()
    {
        const string gpuFan = "/gpu-nvidia/0/control/0";
        const string gpuTemp = "/gpu-nvidia/0/temperature/0";
        var hw = new FakeHardware();
        var ctl = hw.AddControl(gpuFan, 30);
        hw.Values[gpuTemp] = 60;
        hw.GpuCore = 60;
        var f = FlatFan(gpuFan);
        f.Curves[0].SensorId = gpuTemp;
        var cfg = Config(f);
        cfg.Safety.AllowGpuFanControl = true;
        var rig = new Rig(cfg, hw);
        rig.Tick(2);
        hw.GpuHotSpot = 127; // 「読めない」を表す値
        rig.Tick(3);
        T.Check(rig.Fan(gpuFan).State != FanState.GpuGuard, "hot spot sentinel 127°C ignored");
        hw.GpuHotSpot = null;
        hw.GpuCore = 90;
        rig.Tick();
        T.Check(rig.Fan(gpuFan).State != FanState.GpuGuard, "one hot reading does not trigger");
        hw.GpuCore = 60;
        rig.Tick();
        hw.GpuCore = 90;
        rig.Tick();
        rig.Tick();
        T.Check(rig.Fan(gpuFan).State == FanState.GpuGuard && ctl.LastWrite == 100, "two consecutive hot readings → GPU guard 100% immediately");
        hw.GpuCore = 79;
        rig.Tick();
        T.Check(rig.Fan(gpuFan).State == FanState.GpuGuard, "one cool reading keeps the guard");
        rig.Tick();
        T.Check(rig.Fan(gpuFan).State != FanState.GpuGuard, "released after two cool readings");
    }

    /// <summary>S1 の補足: センサー異常が 1 周期だけなら全開に跳ねない。続けば一気に全開</summary>
    public static void TestSensorErrorDebounce()
    {
        var hw = new FakeHardware();
        var ctl = hw.AddControl(Fan0, 20);
        hw.Values[Cpu] = 50;
        var rig = new Rig(Config(FlatFan()), hw);
        rig.Tick(3);
        hw.Values[Cpu] = null; // 1 回だけ読めない
        rig.Tick();
        T.Check(rig.Fan(Fan0).State == FanState.SensorError && ctl.LastWrite < 30, $"single unreadable tick only ramps ({ctl.LastWrite})");
        hw.Values[Cpu] = 50;
        rig.Tick();
        T.Check(ctl.Writes.Max() < 30, "no jump to 100% on a one-tick glitch");
        hw.Values[Cpu] = null;
        rig.Tick(2);
        T.Near(ctl.LastWrite, 100, 1e-6, "persistent sensor error → 100% on the 2nd tick");
    }

    /// <summary>S2: 失敗する命令があってもエンジンは止まらない。重複した端子でも準備に失敗しない</summary>
    public static void TestCommandFailureAndDuplicates()
    {
        var hw = new FakeHardware();
        var ctl = hw.AddControl(Fan0, 40);
        var rig = new Rig(Config(FixedFan(Fan0, 50)), hw);
        rig.E.EnqueueForTest(() => throw new InvalidOperationException("boom"));
        rig.E.SetBoost(DateTime.Now.AddMinutes(5));
        rig.Tick();
        T.Check(rig.Alerted("反映できませんでした", AlertLevel.Warning), "failing command reported");
        T.Check(rig.Fan(Fan0).State == FanState.Boost, "later commands still executed");

        // Normalize を通っていない設定（重複あり）をそのまま渡しても、2 回目の作り直しで失敗しない
        var dup = new AppConfig();
        var p = new ProfileConfig { Name = "標準" };
        p.Fans.Add(FixedFan(Fan0, 50));
        p.Fans.Add(FixedFan(Fan0.ToUpperInvariant(), 70));
        dup.Profiles.Add(p);
        rig.E.ApplyConfig(dup, "標準");
        rig.E.ApplyConfig(dup, "標準");
        rig.E.SetBoost(null);
        rig.Tick(3);
        T.Check(rig.Last!.Fans.Count == 1 && rig.Fan(Fan0).State == FanState.Active, "duplicate control id deduped (first kept)");
        T.Check(rig.Alerts.Count(a => a.Message.Contains("反映できませんでした")) == 1, "no further command errors");
    }

    /// <summary>S5: 手動テスト・識別テストは次の周期を待たずに出力だけ反映する（センサーは読まない）</summary>
    public static void TestImmediateOverride()
    {
        var hw = new FakeHardware();
        var ctl = hw.AddControl(Fan0, 40);
        var pump = hw.AddControl(Pump, 60);
        hw.Values[PumpRpm] = 2500;
        var rig = new Rig(Config(FixedFan(Fan0, 50), PumpFan(60)), hw);
        rig.Tick(2);
        int updates = hw.UpdateCalls;
        rig.E.SetOverride(Fan0, 33, TimeSpan.FromSeconds(10));
        rig.E.DrainForTest();
        T.Check(Math.Abs(ctl.LastWrite - 33) < 1e-4 && hw.UpdateCalls == updates, "override written immediately without a full tick");
        rig.E.SetOverride(Pump, 10, TimeSpan.FromSeconds(10));
        rig.E.DrainForTest();
        T.Check(pump.LastWrite >= FanEngine.PumpHardMinDuty, "pump override clamped to 40% even when immediate");
        rig.Tick();
        T.Check(rig.Fan(Fan0).State == FanState.Manual && Math.Abs(ctl.LastWrite - 33) < 1e-4, "tick keeps the override");
        rig.Now += 10_000;
        rig.Tick(2);
        T.Check(rig.Fan(Fan0).State == FanState.Active, "override expires (monotonic clock)");
        rig.E.Identify(Fan0);
        rig.E.DrainForTest();
        T.Check(ctl.LastWrite == 100, "identify written immediately");
    }

    /// <summary>S5: おまかせ調整は、同じ固定を毎周期送らない（変わったとき・5 秒ごとだけ）</summary>
    public static void TestAutoTunerOverrideThrottle()
    {
        var engine = new FanEngine(); // Start しない（命令はキューに溜まるだけ）
        var tuner = new AutoTuner(engine, new() { ("f1", "Front"), ("f2", "Top") }, "cpuT", "CPU", "load", 80, 97, 25);
        var t = new DateTime(2026, 10, 26, 20, 0, 0);
        int i = 0;
        for (; i < 40; i++)
            tuner.OnSnapshot(new EngineSnapshot(t.AddSeconds(i), "標準", new List<FanStatus>(), new Dictionary<string, float?> { ["cpuT"] = 50f, ["load"] = i < 20 ? 3f : 97f }, false, false, false));
        int before = engine.PendingCommandCount;
        for (; i < 100; i++)
            tuner.OnSnapshot(new EngineSnapshot(t.AddSeconds(i), "標準", new List<FanStatus>(), new Dictionary<string, float?> { ["cpuT"] = 70f, ["load"] = 97f }, false, false, false));
        int sent = engine.PendingCommandCount - before;
        T.Check(tuner.Current is AutoTuner.Phase.Warmup or AutoTuner.Phase.Steps, $"tuner running ({tuner.Current})");
        T.Check(sent > 0 && sent <= 2 * 14, $"60 s of warm-up sends about one override per 5 s per fan ({sent}, was 120)");
        tuner.Dispose();
    }

    /// <summary>S16: スリープでキャリブレーション・手動テストを中止し、BIOS に戻す。復帰は 4 秒後に開き直す</summary>
    public static void TestSuspendResume()
    {
        var hw = new FakeHardware();
        var fan = hw.AddControl(Fan0, 40);
        var other = hw.AddControl("/lpc/nct6686d/0/control/2", 40);
        hw.Values[Rpm0] = 1500;
        var cfg = Config(FixedFan(Fan0, 50, Rpm0), FixedFan("/lpc/nct6686d/0/control/2", 50));
        var rig = new Rig(cfg, hw);
        string? calMessage = null;
        bool resumed = false;
        rig.E.CalibrationFinished += (_, r, m) => calMessage = r is null ? m : "ok";
        rig.E.Resumed += () => resumed = true;
        rig.Tick();
        rig.E.StartCalibration(Fan0);
        rig.E.SetOverride("/lpc/nct6686d/0/control/2", 80, TimeSpan.FromMinutes(5));
        rig.Tick(2);
        T.Check(rig.Fan(Fan0).State == FanState.Calibrating && rig.Fan("/lpc/nct6686d/0/control/2").State == FanState.Manual, "calibrating + override before sleep");

        var task = rig.E.Suspend();
        rig.E.DrainForTest();
        T.Check(task.IsCompleted && task.Result, "Suspend() task completes after BIOS restore");
        T.Check(fan.InBios && other.InBios, "all fans returned to BIOS");
        T.Check(calMessage is not null && calMessage.Contains("スリープ"), $"calibration aborted ({calMessage})");
        T.Check(rig.E.Health == EngineHealth.Suspended, "health suspended");
        int updates = hw.UpdateCalls;
        rig.Tick(3);
        T.Check(hw.UpdateCalls == updates && fan.InBios, "no hardware access while suspended");

        rig.E.Resume();
        rig.Tick(4);
        T.Check(rig.FactoryCalls == 1, "waits 4 s after resume");
        rig.Tick();
        T.Check(rig.FactoryCalls == 2 && resumed && rig.E.Health == EngineHealth.Ok, "hardware re-opened after resume");
        rig.Tick();
        T.Check(rig.Fan("/lpc/nct6686d/0/control/2").State == FanState.Active, "override cleared by sleep");
        T.Check(rig.Fan(Fan0).State == FanState.Active && !fan.InBios, "normal control resumed");
    }

    /// <summary>S21: 部品がまだ見えていなかったときは、ハードウェアを開き直して一覧を作り直せる</summary>
    public static void TestRescanHardware()
    {
        var hw = new FakeHardware();
        var fan = hw.AddControl(Fan0, 40);
        var cfg = Config(FixedFan(Fan0, 50));
        int catalogs = 0;
        var rig = new Rig(cfg, hw);
        rig.E.CatalogReady += _ => catalogs++;
        rig.Tick(2);
        rig.E.RescanHardware();
        rig.E.DrainForTest();
        T.Check(fan.InBios && rig.E.Health == EngineHealth.Recovering, "rescan: BIOS control while re-opening");
        rig.Tick();
        T.Check(rig.FactoryCalls == 2 && catalogs == 1 && rig.E.Health == EngineHealth.Ok, "rescan re-opens the hardware and publishes the catalog again");
        rig.Tick();
        T.Check(!fan.InBios && rig.Fan(Fan0).State == FanState.Active, "control resumes after rescan");
        T.Check(!rig.Alerts.Any(a => a.Level == AlertLevel.Danger), "rescan raises no danger alert");
    }

    /// <summary>S3 / S6: BIOS に戻すのに失敗したら、値を書き直してからもう一度戻す（最大 3 回）</summary>
    public static void TestRestoreRetry()
    {
        T.Check(new FanEngine().Stop(), "Stop() on a never-started engine returns true");
        var hw = new FakeHardware();
        var fan = hw.AddControl(Fan0, 40);
        var rig = new Rig(Config(FixedFan(Fan0, 50)), hw);
        rig.Tick(2);
        fan.ThrowOnDefaultCount = 1;
        T.Check(rig.E.RestoreAllForTest() && fan.InBios, "restore succeeds on the 2nd attempt");
        rig.Tick(2);
        fan.ThrowOnDefaultCount = 5;
        T.Check(!rig.E.RestoreAllForTest(), "restore reports failure after 3 attempts");
    }

    // =====================================================================
    // 部品交換の検知（S21）
    // =====================================================================

    private static (AppConfig Cfg, HardwareCatalog Ready, HardwareCatalog NoBoard) HardwareCase()
    {
        var cfg = new AppConfig();
        var p = new ProfileConfig { Name = "標準" };
        var fan = new FanConfig { Name = "CPU Fan", ControlId = Fan0, RpmSensorId = Rpm0 };
        fan.Curves.Add(new CurveConfig { Name = "CPU", SensorId = Cpu });
        var pumpFan = new FanConfig { Name = "Pump", ControlId = Pump, RpmSensorId = PumpRpm, Role = FanRoles.Pump };
        pumpFan.Curves.Add(new CurveConfig { Name = "CPU", SensorId = Cpu });
        p.Fans.Add(fan);
        p.Fans.Add(pumpFan);
        cfg.Profiles.Add(p);
        cfg.KnownDevices.Add(new KnownDevice { Key = "/motherboard", Name = "ASRock B860", Kind = "Motherboard" });
        cfg.Normalize();

        SensorInfo S(string id, string name, string hw, string type, string st = "Temperature") => new(id, name, hw, type, st);
        var temps = new List<SensorInfo> { S(Cpu, "CPU Package", "Intel Core Ultra 5 245KF", "Cpu") };
        var fans = new List<SensorInfo> { S(Rpm0, "Fan #1", "Nuvoton NCT6686D", "SuperIO", "Fan"), S(PumpRpm, "Fan #2", "Nuvoton NCT6686D", "SuperIO", "Fan") };
        var controls = new List<ControlInfo> { new(Fan0, "Fan #1", "Nuvoton NCT6686D", Rpm0), new(Pump, "Fan #2", "Nuvoton NCT6686D", PumpRpm) };
        var devices = new List<DeviceInfo> { new("/motherboard", "ASRock B860", "Motherboard", "Nuvoton NCT6686D", 0, 2, 2, new List<string>()) };
        var ready = new HardwareCatalog(devices, new List<HardwareGroup> { new("all", "x", temps.Concat(fans).ToList()) }, temps, controls, true);
        // 起動直後: Super I/O がまだ見えていない（CPU の温度だけ見える）
        var noBoard = new HardwareCatalog(new List<DeviceInfo>(), new List<HardwareGroup> { new("cpu", "Cpu", temps) }, temps, new List<ControlInfo>(), true);
        return (cfg, ready, noBoard);
    }

    public static void TestHardwareChangeNotReady()
    {
        var (cfg, ready, noBoard) = HardwareCase();
        T.Check(!HardwareChange.Detect(cfg, ready).HasChanges && !HardwareChange.Detect(cfg, ready).NotReady, "ready catalog: no changes");

        var r = HardwareChange.Detect(cfg, noBoard);
        T.Check(r.NotReady && !r.HasChanges && r.NotReadyReason.Contains("Super I/O"), $"all /lpc controls missing → not ready ({r.NotReadyReason})");
        HardwareChange.Remember(cfg, noBoard);
        T.Check(cfg.KnownDevices.Count == 1 && cfg.KnownDevices[0].Name == "ASRock B860", "not-ready catalog does not overwrite known devices");

        // 待っても見えないので強制で確かめた → ユーザーが（このファンを削除する）のまま確定しても、消さずに BIOS 制御で残す
        var forced = HardwareChange.Detect(cfg, noBoard, force: true);
        T.Check(forced.HasChanges && forced.Items.Count(i => i.IsControl) == 2, "forced detect proposes the missing controls");
        HardwareChange.Apply(cfg, forced.Items, noBoard);
        var fans = cfg.Profiles[0].Fans;
        T.Check(fans.Count == 2 && fans.All(f => !f.Enabled), "fans kept (disabled) when the whole board category is missing");

        // GPU のカテゴリーが見えないときも準備中扱い
        var (cfg2, ready2, _) = HardwareCase();
        cfg2.Profiles[0].Fans[0].Curves.Add(new CurveConfig { Name = "GPU", SensorId = "/gpu-nvidia/0/temperature/0" });
        cfg2.SensorLabels["/gpu-nvidia/0/temperature/0"] = new SensorLabel { Name = "GPU Core", Hardware = "RTX", HardwareType = "GpuNvidia", SensorType = "Temperature" };
        var g = HardwareChange.Detect(cfg2, ready2);
        T.Check(g.NotReady && g.NotReadyReason.Contains("GPU"), $"GPU sensors all missing → not ready ({g.NotReadyReason})");
    }

    public static void TestRemapRejectsDuplicateTargets()
    {
        var (cfg, ready, _) = HardwareCase();
        // 2 つの古い端子を同じ新しい端子に対応付けようとした
        var items = new List<RemapItem>
        {
            new() { IsControl = true, OldId = Fan0, OldName = "CPU Fan", NewId = "/lpc/nct6686d/0/control/3" },
            new() { IsControl = true, OldId = Pump, OldName = "Pump", NewId = "/lpc/nct6686d/0/control/3" },
        };
        var conflicts = HardwareChange.ConflictingControlItems(cfg, items);
        T.Check(conflicts.Count == 1 && conflicts[0].OldId == Pump, "second mapping to the same control rejected");
        HardwareChange.Apply(cfg, items, ready);
        var fans = cfg.Profiles[0].Fans;
        T.Check(fans.Select(f => f.ControlId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == fans.Count, "no duplicate control ids after apply");
        T.Check(fans.Any(f => f.ControlId == Pump && !f.Enabled), "rejected fan keeps its id and is set to BIOS control");

        // 動かないファンがすでに使っている端子へは対応付けない
        var (cfg2, ready2, _) = HardwareCase();
        var onto = new List<RemapItem> { new() { IsControl = true, OldId = Fan0, OldName = "CPU Fan", NewId = Pump } };
        T.Check(HardwareChange.ConflictingControlItems(cfg2, onto).Count == 1, "mapping onto a control used by another fan rejected");
        // 入れ替え（A→B, B→A）は受け付ける
        var swap = new List<RemapItem>
        {
            new() { IsControl = true, OldId = Fan0, OldName = "CPU Fan", NewId = Pump },
            new() { IsControl = true, OldId = Pump, OldName = "Pump", NewId = Fan0 },
        };
        T.Check(HardwareChange.ConflictingControlItems(cfg2, swap).Count == 0, "swapping two headers is allowed");
    }

    public static void TestPumpRpmSensorRemap()
    {
        var (cfg, ready, _) = HardwareCase();
        cfg.Profiles[0].Fans[1].RpmSensorId = "/lpc/nct6686d/0/fan/9"; // 古い版の ID など、今は存在しない
        var r = HardwareChange.Detect(cfg, ready);
        var item = r.Items.FirstOrDefault(i => i.IsControl && i.OldId == Pump);
        T.Check(item is not null && item.NewId == Pump, "missing pump rpm sensor reported on its (existing) control");
        HardwareChange.Apply(cfg, r.Items, ready);
        var pump = cfg.Profiles[0].Fans[1];
        T.Check(pump.RpmSensorId == PumpRpm && pump.Enabled, $"pump rpm sensor re-attached, pump stays enabled ({pump.RpmSensorId})");
    }
}

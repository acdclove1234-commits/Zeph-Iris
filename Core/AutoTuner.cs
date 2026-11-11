// ==================================================
// AutoTuner.cs - カーブの自動調整 (負荷をかけたままファンを段階的に下げ、目標温度を守れる最も静かな回転数を測ってカーブを作る)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Text;

namespace ProFanController;

public sealed class AutoTuneStep
{
    public double Duty { get; init; }
    /// <summary>この回転数で落ち着いた温度</summary>
    public double SteadyTemp { get; init; }
    /// <summary>選んだファンの平均回転数</summary>
    public double? AvgRpm { get; init; }
    public double Seconds { get; init; }
}

public sealed class AutoTuneResult
{
    public required string SensorId { get; init; }
    public required string SensorName { get; init; }
    public required double IdleTemp { get; init; }
    public required List<AutoTuneStep> Steps { get; init; }
    public required List<(string ControlId, string Name)> Fans { get; init; }
    public string? AbortReason { get; init; }
    public DateTime MeasuredAt { get; init; } = DateTime.Now;

    public bool Usable => Steps.Count >= 2;

    public string ToText(double target)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L($"自動調整  {MeasuredAt:yyyy/MM/dd HH:mm} ／ 温度センサー: {SensorName}"));
        if (AbortReason is not null) sb.AppendLine($"※ {AbortReason}");
        sb.AppendLine(L($"待機時の温度: {IdleTemp:F1} °C"));
        foreach (var s in Steps)
            sb.AppendLine($"  {s.Duty,3:F0}% → {s.SteadyTemp:F1} °C{(s.AvgRpm is double r ? L($"（{r:N0} rpm）") : "")}");
        // 1 段も測れなかったとき（負荷がかからなかったなど）は、目標を守れるかは分からない
        if (Steps.Count == 0) return sb.ToString();
        var need = AutoTuner.RequiredDuty(Steps, target);
        var top = Steps.OrderByDescending(x => x.Duty).First();
        sb.AppendLine(need.Insufficient
            ? L($"目標 {target:F0}°C は全開でも届きません（全開で {top.SteadyTemp:F1}°C）")
            : L($"目標 {target:F0}°C を守るのに必要な回転数: 約 {need.Duty:F0}%"));
        return sb.ToString();
    }
}

/// <summary>
/// 手順（画面スレッドから毎周期 OnSnapshot を呼ぶ）:
///   1. 待機 … いつもの制御のまま 20 秒、待機時の温度を測る
///   2. 負荷 … 内蔵の CPU 負荷を開始（GPU の場合は、ゲームなどで使用率が 70% を超えるのを待つ）
///   3. 全開で温まるまで待つ
///   4. 100 → 80 → 65 → 50 → 40% … と下げ、それぞれ温度が落ち着くまで待って記録する
///      （目標 + 6°C か、危険な温度に達したら、それより下げても意味がないのでやめる）
///   5. 負荷を止めて、いつもの制御に戻す
/// 選んだファンは「手動テスト」として同じ回転数に固定する（緊急全開・ポンプ保護は常に優先）。
/// </summary>
public sealed class AutoTuner : IDisposable
{
    public enum Phase { Idle, WaitLoad, Warmup, Steps, Cooldown, Done }

    private static readonly TimeSpan IdleTime = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WaitLoadTimeout = TimeSpan.FromMinutes(3);
    /// <summary>内蔵の負荷で、使用率が上がるのを待つ上限（10 秒続けて 70% 以上になれば始める）</summary>
    private static readonly TimeSpan BuiltInLoadTimeout = TimeSpan.FromSeconds(45);
    // 簡易水冷は冷却液がゆっくり温まるので、1 段ごとに少なくとも 1 分は待つ
    private static readonly TimeSpan MinHold = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan WarmupMin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WarmupMax = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan CooldownTime = TimeSpan.FromSeconds(20);
    /// <summary>直近 30 秒で、前半と後半の平均の差がこれ未満なら「落ち着いた」とみなす</summary>
    private const double SteadyDelta = 0.5;
    private const double ExternalLoadThreshold = 70;
    /// <summary>手動テストの固定を送り直す間隔（固定は 10 秒で切れるので、その半分ごと）</summary>
    private static readonly TimeSpan OverrideRefresh = TimeSpan.FromSeconds(5);
    /// <summary>手動テストで固定する期間</summary>
    private static readonly TimeSpan OverrideDuration = TimeSpan.FromSeconds(10);

    private readonly FanEngine _engine;
    private readonly List<(string ControlId, string Name)> _fans;
    private readonly string _sensorId;
    private readonly string _sensorName;
    private readonly string? _loadSensorId;
    private readonly bool _builtInLoad;
    /// <summary>内蔵の負荷（GPU）が本当にかかったかを、使用率センサーで確かめてから測り始める</summary>
    private readonly bool _verifyLoad;
    private readonly double _target;
    private readonly double _abortTemp;
    private readonly List<double> _duties;
    private readonly ILoadGenerator _load;
    private readonly List<(DateTime T, double Temp)> _window = new();
    private readonly List<double> _idle = new();
    private readonly List<double> _rpms = new();
    private readonly List<AutoTuneStep> _steps = new();
    /// <summary>今の段階を始めた時刻（スナップショットの時刻で測る。最初のスナップショットで決まる）</summary>
    private DateTime _phaseStart = DateTime.MinValue;
    private DateTime _loadSeenSince = DateTime.MinValue;
    private int _stepIndex;
    private string? _abortReason;
    /// <summary>最後にエンジンへ送った固定の Duty と時刻（同じ値を毎周期送らないため）</summary>
    private double _sentDuty = double.NaN;
    private DateTime _sentAt = DateTime.MinValue;

    public Phase Current { get; private set; } = Phase.Idle;
    public AutoTuneResult? Result { get; private set; }
    public double LastTemp { get; private set; }
    public double CurrentDuty => Current == Phase.Steps ? _duties[_stepIndex] : 100;
    public IReadOnlyList<AutoTuneStep> Steps => _steps;
    public DateTime StartedAt { get; } = DateTime.Now;

    /// <param name="loadSensorId">負荷を確かめる使用率センサー（GPU）。null なら内蔵の CPU 負荷を使い、確かめない</param>
    /// <param name="minDuty">選んだファンの最低回転数（これより下は測らない）</param>
    /// <param name="load">
    /// loadSensorId があるときに使う内蔵の負荷（GPU の負荷など）。null なら、ゲームなどの外部の負荷がかかるのを待つ
    /// </param>
    public AutoTuner(FanEngine engine, List<(string ControlId, string Name)> fans, string sensorId, string sensorName,
        string? loadSensorId, double target, double abortTemp, double minDuty, ILoadGenerator? load = null)
    {
        _engine = engine;
        _fans = fans;
        _sensorId = sensorId;
        _sensorName = sensorName;
        _loadSensorId = loadSensorId;
        _builtInLoad = loadSensorId is null || load is not null;
        _verifyLoad = loadSensorId is not null && load is not null;
        _load = load ?? new StressLoad();
        _target = target;
        _abortTemp = abortTemp;
        _duties = new[] { 100.0, 80, 65, 50, 40, 30 }.Where(d => d >= minDuty).ToList();
        if (_duties.Count == 0 || _duties[^1] > minDuty + 4) _duties.Add(Math.Max(minDuty, 0));
    }

    /// <summary>全体の目安時間（進捗バー用）</summary>
    public TimeSpan Estimated => IdleTime + WarmupMin + TimeSpan.FromSeconds(80 * _duties.Count) + CooldownTime;

    public double Progress => Current switch
    {
        Phase.Idle => 0.02,
        Phase.WaitLoad => 0.05,
        Phase.Warmup => 0.08 + 0.12 * Math.Min(1, Math.Max(0, (DateTime.Now - _phaseStart) / WarmupMin)),
        Phase.Steps => 0.2 + 0.75 * (_stepIndex + Math.Min(1, Math.Max(0, (DateTime.Now - _phaseStart) / MinHold)) * 0.9) / _duties.Count,
        Phase.Cooldown => 0.97,
        _ => 1,
    };

    public string StatusText => Current switch
    {
        Phase.Idle => L("待機時の温度を測っています…"),
        Phase.WaitLoad when _verifyLoad => L("GPU に負荷をかけています（使用率が上がるのを待っています）…"),
        Phase.WaitLoad when _builtInLoad => L("負荷を開始しています…"),
        Phase.WaitLoad => L("ゲームやベンチマークを起動してください（GPU 使用率が 70% を超えたら自動で始まります）"),
        Phase.Warmup => L("全開で温まるまで待っています…"),
        Phase.Steps => L($"{_duties[_stepIndex]:F0}% で温度が落ち着くのを待っています…（{_stepIndex + 1} / {_duties.Count} 段目）"),
        Phase.Cooldown => L("負荷を止めて、いつもの制御に戻しています…"),
        _ => _abortReason is null ? L("完了しました") : L($"中断: {_abortReason}"),
    };

    public void OnSnapshot(EngineSnapshot s)
    {
        if (Current == Phase.Done) return;
        if (!s.Values.TryGetValue(_sensorId, out var tv) || tv is null)
        {
            Abort(L("温度センサーが読めません"));
            return;
        }
        double temp = tv.Value;
        LastTemp = temp;
        var now = s.Time;
        if (_phaseStart == DateTime.MinValue) _phaseStart = now;
        _window.Add((now, temp));
        _window.RemoveAll(w => now - w.T > TimeSpan.FromSeconds(30));

        // 危険な温度・緊急全開になったら、すぐにやめる
        if (s.Emergency || temp >= _abortTemp)
        {
            if (Current is Phase.Warmup or Phase.Steps)
            {
                if (Current == Phase.Steps) Record(now, aborted: true);
                Abort(s.Emergency ? L("緊急全開になったため中止しました") : L($"温度が {temp:F0}°C に達したため中止しました"));
                return;
            }
        }

        // 負荷がかからなくなったら中止（外部の負荷: ゲームなどがやめられた。内蔵の負荷: GPU が止めた）
        if (_loadSensorId is not null && Current is Phase.Warmup or Phase.Steps && LoadOf(s) is double load && load < 40)
        {
            Abort(_builtInLoad
                ? L("GPU の負荷が止まったため中止しました")
                : L("GPU の負荷がなくなったため中止しました（ゲームやベンチマークを動かし続けてください）"));
            return;
        }

        switch (Current)
        {
            case Phase.Idle:
                _idle.Add(temp);
                if (now - _phaseStart >= IdleTime)
                {
                    if (_builtInLoad) _load.Start();
                    Next(Phase.WaitLoad, now);
                }
                break;

            case Phase.WaitLoad:
                if (_load.Error is { } loadError)
                {
                    Abort(L($"負荷をかけられませんでした: {loadError}"));
                    break;
                }
                bool loaded = _builtInLoad && !_verifyLoad;
                if (!loaded)
                {
                    // ゲームの読み込み中などの一瞬の負荷ではなく、10 秒続いてから始める
                    if (LoadOf(s) is double l && l >= ExternalLoadThreshold)
                    {
                        if (_loadSeenSince == DateTime.MinValue) _loadSeenSince = now;
                        loaded = now - _loadSeenSince >= TimeSpan.FromSeconds(10);
                    }
                    else
                    {
                        _loadSeenSince = DateTime.MinValue;
                    }
                }
                if (loaded)
                {
                    Override(100, now);
                    Next(Phase.Warmup, now);
                }
                else if (now - _phaseStart >= (_builtInLoad ? BuiltInLoadTimeout : WaitLoadTimeout))
                {
                    Abort(_builtInLoad
                        ? L("GPU に負荷をかけましたが、使用率が上がらなかったため中止しました")
                        : L("3 分待っても GPU に負荷がかからなかったため中止しました（ゲームやベンチマークを起動してください）"));
                }
                break;

            case Phase.Warmup:
                Override(100, now);
                if (now - _phaseStart >= WarmupMax || (now - _phaseStart >= WarmupMin && Steady()))
                {
                    _stepIndex = 0;
                    _rpms.Clear();
                    Next(Phase.Steps, now);
                }
                break;

            case Phase.Steps:
                Override(_duties[_stepIndex], now);
                var rpms = _fans.Select(f => s.Fans.FirstOrDefault(x => x.ControlId == f.ControlId)?.Rpm).OfType<double>().ToList();
                if (rpms.Count > 0 && now - _phaseStart > TimeSpan.FromSeconds(10)) _rpms.Add(rpms.Average());
                if (now - _phaseStart >= MaxHold || (now - _phaseStart >= MinHold && Steady()))
                {
                    var step = Record(now, aborted: false);
                    // 目標を大きく超えたら、それより下げても意味がない
                    if (step.SteadyTemp >= _target + 6 || _stepIndex + 1 >= _duties.Count) Finish(now);
                    else
                    {
                        _stepIndex++;
                        _rpms.Clear();
                        Next(Phase.Steps, now);
                    }
                }
                break;

            case Phase.Cooldown:
                if (now - _phaseStart >= CooldownTime) Complete();
                break;
        }
    }

    private double? LoadOf(EngineSnapshot s)
        => _loadSensorId is not null && s.Values.TryGetValue(_loadSensorId, out var v) ? v : null;

    private bool Steady()
    {
        if (_window.Count < 20 || _window[^1].T - _window[0].T < TimeSpan.FromSeconds(25)) return false;
        var first = _window.Take(_window.Count / 3).Average(w => w.Temp);
        var last = _window.Skip(_window.Count * 2 / 3).Average(w => w.Temp);
        return Math.Abs(last - first) < SteadyDelta;
    }

    private AutoTuneStep Record(DateTime now, bool aborted)
    {
        var recent = _window.Where(w => now - w.T <= TimeSpan.FromSeconds(15)).Select(w => w.Temp).DefaultIfEmpty(LastTemp).Average();
        var step = new AutoTuneStep
        {
            Duty = _duties[_stepIndex],
            // 途中で止めた段は、まだ上がっている途中なので「少なくともこの温度」として記録する
            SteadyTemp = aborted ? Math.Max(recent, LastTemp) : recent,
            AvgRpm = _rpms.Count > 0 ? _rpms.Average() : null,
            Seconds = (now - _phaseStart).TotalSeconds,
        };
        _steps.Add(step);
        return step;
    }

    /// <summary>
    /// 選んだファンを手動テストとして固定する。値が変わったときと 5 秒ごとだけ送る
    /// （毎周期送ると、そのたびにエンジンが起こされて余計な処理が増えるため）
    /// </summary>
    private void Override(double duty, DateTime now)
    {
        var elapsed = now - _sentAt;
        // 時計が戻った（elapsed がマイナス）ときも送り直す
        if (duty == _sentDuty && elapsed >= TimeSpan.Zero && elapsed < OverrideRefresh) return;
        _sentDuty = duty;
        _sentAt = now;
        foreach (var f in _fans) _engine.SetOverride(f.ControlId, duty, OverrideDuration);
    }

    private void Release()
    {
        _sentDuty = double.NaN;
        foreach (var f in _fans) _engine.SetOverride(f.ControlId, null, TimeSpan.Zero);
    }

    private void Next(Phase phase, DateTime now)
    {
        Current = phase;
        _phaseStart = now;
        _window.Clear();
    }

    private void Finish(DateTime now)
    {
        _load.Stop();
        Release();
        Next(Phase.Cooldown, now);
    }

    public void Abort(string reason)
    {
        if (Current == Phase.Done) return;
        _abortReason ??= reason;
        _load.Stop();
        Release();
        Complete();
    }

    private void Complete()
    {
        _load.Stop();
        Release();
        Current = Phase.Done;
        Result = new AutoTuneResult
        {
            SensorId = _sensorId,
            SensorName = _sensorName,
            IdleTemp = _idle.Count > 0 ? _idle.Average() : LastTemp,
            Steps = _steps.ToList(),
            Fans = _fans,
            AbortReason = _abortReason,
        };
    }

    public void Dispose()
    {
        _load.Stop();
        if (Current != Phase.Done) Release();
    }

    // =====================================================================
    // 測定結果からカーブを作る（純粋な計算。テストしやすいよう static にしている）
    // =====================================================================

    /// <summary>目標温度を守れる最低の回転数（測定点の間は直線で補間）。全開でも届かなければ Insufficient</summary>
    public static (double Duty, bool Insufficient) RequiredDuty(IReadOnlyList<AutoTuneStep> steps, double target, double margin = 1.5)
    {
        var pts = steps.OrderByDescending(s => s.Duty).ToList();
        if (pts.Count == 0) return (100, true);
        double goal = target - margin;
        if (pts[0].SteadyTemp > goal) return (100, true);
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var hi = pts[i];
            var lo = pts[i + 1];
            if (lo.SteadyTemp <= goal) continue;
            // hi は目標以下、lo は目標超え → その間
            double k = (lo.SteadyTemp - goal) / Math.Max(0.1, lo.SteadyTemp - hi.SteadyTemp);
            return (Math.Clamp(lo.Duty + k * (hi.Duty - lo.Duty), lo.Duty, hi.Duty), false);
        }
        return (pts[^1].Duty, false); // いちばん低い回転でも目標以下
    }

    /// <summary>
    /// カーブ: 待機温度付近は最低回転、目標の 2°C 手前で必要な回転数、目標 + 4°C で必要な回転数と全開の中間、
    /// 目標 + 10°C で全開（必要な回転数が低いと、目標を少し超えただけで最低回転から全開へ跳ね上がり、
    /// 一瞬の温度の上下でファンの音が大きく変わるため、1°C あたりの上がり方をなだらかにする）。
    /// 全開でも届かないときは、目標の 4°C 手前で全開にする
    /// </summary>
    public static List<CurvePoint> BuildCurve(IReadOnlyList<AutoTuneStep> steps, double idleTemp, double target, double minDuty)
    {
        var (need, insufficient) = RequiredDuty(steps, target);
        need = Math.Max(need, minDuty);
        double t1 = Math.Min(idleTemp + 3, target - 14);
        double t2 = target - 9;
        var pts = new List<CurvePoint> { new() { Temp = Math.Round(t1), Duty = Math.Round(minDuty) } };
        if (insufficient || need >= 99)
        {
            pts.Add(new CurvePoint { Temp = Math.Round(t2), Duty = Math.Round(minDuty + (100 - minDuty) * 0.5) });
            pts.Add(new CurvePoint { Temp = Math.Round(target - 4), Duty = 100 });
        }
        else
        {
            pts.Add(new CurvePoint { Temp = Math.Round(t2), Duty = Math.Round(minDuty + (need - minDuty) * 0.45) });
            pts.Add(new CurvePoint { Temp = Math.Round(target - 2), Duty = Math.Round(need) });
            pts.Add(new CurvePoint { Temp = Math.Round(target + 4), Duty = Math.Round(need + (100 - need) * 0.5) });
            pts.Add(new CurvePoint { Temp = Math.Round(target + 10), Duty = 100 });
        }
        // 温度は 2°C 以上の間隔で増え、Duty は下がらないようにする
        for (int i = 1; i < pts.Count; i++)
        {
            if (pts[i].Temp < pts[i - 1].Temp + 2) pts[i].Temp = pts[i - 1].Temp + 2;
            if (pts[i].Duty < pts[i - 1].Duty) pts[i].Duty = pts[i - 1].Duty;
        }
        return pts;
    }
}

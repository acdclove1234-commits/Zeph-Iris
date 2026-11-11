// ==================================================
// CoolingTest.cs - 冷却テスト (内蔵の CPU 負荷 → 待機・負荷・冷却の 3 段階を測定してレポート化)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Numerics;
using System.Text;

namespace ProFanController;

/// <summary>測定のための負荷（CPU・GPU）。Start で始め、Stop で止める。始められなかったときは Error に理由</summary>
public interface ILoadGenerator
{
    void Start();
    void Stop();
    /// <summary>負荷をかけられなかった理由（問題なければ null）</summary>
    string? Error { get; }
}

/// <summary>全コアで浮動小数点演算を回し続けて CPU を発熱させる</summary>
public sealed class StressLoad : IDisposable, ILoadGenerator
{
    public string? Error => null;

    private CancellationTokenSource? _cts;
    private readonly List<Thread> _threads = new();

    public bool Running => _cts is not null;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            // 優先度を下げておくと、テスト中も画面やファン制御は普通に動く
            var t = new Thread(() => Burn(token)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"Stress{i}" };
            _threads.Add(t);
            t.Start();
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        foreach (var t in _threads) t.Join(2000);
        _threads.Clear();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>SIMD（AVX）で独立した 8 本の積和演算を回し、演算器を埋める</summary>
    private static void Burn(CancellationToken token)
    {
        var a = new Vector<float>(0.999999f);
        var b = new Vector<float>(0.000001f);
        Vector<float> x0 = Vector<float>.One, x1 = x0, x2 = x0, x3 = x0, x4 = x0, x5 = x0, x6 = x0, x7 = x0;
        while (!token.IsCancellationRequested)
        {
            for (int i = 0; i < 200_000; i++)
            {
                x0 = x0 * a + b; x1 = x1 * a + b; x2 = x2 * a + b; x3 = x3 * a + b;
                x4 = x4 * a + b; x5 = x5 * a + b; x6 = x6 * a + b; x7 = x7 * a + b;
            }
        }
        GC.KeepAlive(x0 + x1 + x2 + x3 + x4 + x5 + x6 + x7); // 計算を最適化で消されないように
    }

    public void Dispose() => Stop();
}

public sealed class CoolingFanResult
{
    public required string Name { get; init; }
    public double MaxRpm { get; init; }
    public double AvgDutyUnderLoad { get; init; }
}

public sealed class CoolingTestReport
{
    public DateTime StartedAt { get; init; }
    public string Profile { get; init; } = "";
    public TimeSpan LoadDuration { get; init; }
    public double BaselineTemp { get; init; }
    public double MaxTemp { get; init; }
    /// <summary>負荷の最後の 60 秒の平均（落ち着いた温度）</summary>
    public double SteadyTemp { get; init; }
    /// <summary>上昇幅の 90% に達するまでの秒数</summary>
    public double SecondsTo90 { get; init; }
    /// <summary>負荷を止めてから「待機温度 + 3°C」まで下がる秒数（下がりきらなければ null）</summary>
    public double? RecoverySeconds { get; init; }
    public double? AvgPower { get; init; }
    public double? ClockStart { get; init; }
    public double? ClockEnd { get; init; }
    public List<CoolingFanResult> Fans { get; init; } = new();
    public string? AbortReason { get; init; }

    /// <summary>クロックが 10% 以上下がっていれば、熱による性能低下（サーマルスロットリング）の可能性</summary>
    public bool ClockDropped => ClockStart is > 0 && ClockEnd is > 0 && ClockEnd < ClockStart * 0.9;

    public string Verdict => SteadyTemp switch
    {
        _ when AbortReason is not null => L("中断"),
        < 75 => L("とても良好"),
        < 85 => L("良好"),
        < 92 => L("やや高め"),
        _ => L("冷却不足"),
    };

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(L($"Zeph-Iris 冷却テスト  {StartedAt:yyyy/MM/dd HH:mm}"));
        sb.AppendLine(L($"プロファイル: {Profile} ／ 負荷時間: {LoadDuration.TotalMinutes:F0} 分（CPU 全コア）"));
        if (AbortReason is not null) sb.AppendLine($"※ {AbortReason}");
        sb.AppendLine();
        sb.AppendLine(L($"判定              : {Verdict}"));
        sb.AppendLine(L($"待機時の CPU 温度  : {BaselineTemp:F1} °C"));
        sb.AppendLine(L($"最高温度          : {MaxTemp:F1} °C"));
        sb.AppendLine(L($"安定時の温度      : {SteadyTemp:F1} °C（負荷の最後 60 秒の平均）"));
        sb.AppendLine(L($"温度が上がりきるまで: 約 {SecondsTo90:F0} 秒（上昇幅の 90%）"));
        sb.AppendLine(L($"冷えるまで        : {(RecoverySeconds is double r ? L($"約 {r:F0} 秒（待機温度 +3°C まで）") : L("測定時間内に下がりきらず"))}"));
        if (AvgPower is double p) sb.AppendLine(L($"CPU 消費電力（平均）: {p:F0} W"));
        if (ClockStart is double c0 && ClockEnd is double c1)
            sb.AppendLine(L($"CPU クロック      : {c0 / 1000:F2} GHz → {c1 / 1000:F2} GHz{(ClockDropped ? L("（低下あり: 熱で性能が落ちている可能性）") : "")}"));
        sb.AppendLine();
        foreach (var f in Fans) sb.AppendLine(L($"{f.Name,-12}: 最大 {f.MaxRpm:N0} rpm ／ 負荷時の平均 {f.AvgDutyUnderLoad:F0}%"));
        return sb.ToString();
    }
}

/// <summary>
/// 毎周期のスナップショットを受け取りながら、待機（30 秒）→ 負荷 → 冷却（最大 3 分）と進める。
/// UI スレッドから OnSnapshot を呼ぶこと。
/// </summary>
public sealed class CoolingTestRunner : IDisposable
{
    public enum Phase { Baseline, Load, Cooldown, Done }

    private static readonly TimeSpan BaselineTime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(3);

    private readonly StressLoad _load = new();
    private readonly string _tempId;
    private readonly string? _powerId;
    private readonly IReadOnlyList<string> _clockIds;
    private readonly IReadOnlyList<(string ControlId, string Name)> _fans;
    private readonly TimeSpan _loadDuration;
    private readonly double _abortTemp;
    private readonly List<(DateTime T, double Temp, double? Power, double? Clock)> _samples = new();
    private readonly Dictionary<string, List<(double Duty, double Rpm, bool Load)>> _fanSamples = new();
    private DateTime _phaseStart;
    private DateTime _loadStart, _loadEnd;
    private string? _abortReason;
    private string _profile = "";

    public Phase Current { get; private set; } = Phase.Baseline;
    public DateTime StartedAt { get; }
    public CoolingTestReport? Report { get; private set; }
    public double LastTemp { get; private set; }
    public double MaxTemp { get; private set; }

    public CoolingTestRunner(string tempId, string? powerId, IReadOnlyList<string> clockIds,
        IReadOnlyList<(string ControlId, string Name)> fans, TimeSpan loadDuration, double abortTemp)
    {
        _tempId = tempId;
        _powerId = powerId;
        _clockIds = clockIds;
        _fans = fans;
        _loadDuration = loadDuration;
        _abortTemp = abortTemp;
        StartedAt = _phaseStart = DateTime.Now;
        foreach (var f in fans) _fanSamples[f.ControlId] = new();
    }

    public TimeSpan Total => BaselineTime + _loadDuration + MaxCooldown;

    public double Progress => Current switch
    {
        Phase.Baseline => (DateTime.Now - _phaseStart) / Total,
        Phase.Load => (BaselineTime + (DateTime.Now - _phaseStart)) / Total,
        Phase.Cooldown => (BaselineTime + _loadDuration + (DateTime.Now - _phaseStart)) / Total,
        _ => 1,
    };

    public string StatusText => Current switch
    {
        Phase.Baseline => L($"待機時の温度を測定中…（{Remaining(BaselineTime)}）"),
        Phase.Load => L($"CPU に負荷をかけています…（残り {Remaining(_loadDuration)}）"),
        Phase.Cooldown => L("負荷を止めて、冷えるまでの時間を測定中…"),
        _ => _abortReason is null ? L("完了") : L($"中断: {_abortReason}"),
    };

    private string Remaining(TimeSpan phaseLength)
    {
        var left = phaseLength - (DateTime.Now - _phaseStart);
        return left < TimeSpan.Zero ? "0:00" : $"{(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    public void OnSnapshot(EngineSnapshot s)
    {
        if (Current == Phase.Done) return;
        _profile = s.Profile;
        if (!s.Values.TryGetValue(_tempId, out var tv) || tv is null) return;
        double temp = tv.Value;
        LastTemp = temp;
        MaxTemp = Math.Max(MaxTemp, temp);

        double? power = _powerId is not null && s.Values.TryGetValue(_powerId, out var pv) ? pv : null;
        var clocks = _clockIds.Select(id => s.Values.TryGetValue(id, out var c) ? c : null).Where(c => c > 0).Select(c => (double)c!.Value).ToList();
        _samples.Add((s.Time, temp, power, clocks.Count > 0 ? clocks.Average() : null));
        foreach (var f in s.Fans)
        {
            if (_fanSamples.TryGetValue(f.ControlId, out var list) && f.Rpm.HasValue)
                list.Add((f.Duty ?? 0, f.Rpm.Value, Current == Phase.Load));
        }

        var elapsed = s.Time - _phaseStart;
        switch (Current)
        {
            case Phase.Baseline when elapsed >= BaselineTime:
                _load.Start();
                _loadStart = s.Time;
                Next(Phase.Load, s.Time);
                break;

            case Phase.Load:
                if (temp >= _abortTemp)
                {
                    _abortReason = L($"CPU 温度が {temp:F0}°C に達したため、負荷を止めました");
                    EndLoad(s.Time);
                }
                else if (elapsed >= _loadDuration)
                {
                    EndLoad(s.Time);
                }
                break;

            case Phase.Cooldown:
                double baseline = Baseline();
                if ((elapsed > TimeSpan.FromSeconds(20) && temp <= baseline + 3) || elapsed >= MaxCooldown)
                    Finish();
                break;
        }
    }

    public void Cancel()
    {
        if (Current == Phase.Done) return;
        _abortReason ??= L("ユーザーが中止しました");
        if (Current == Phase.Load) EndLoad(DateTime.Now);
        Finish();
    }

    private void EndLoad(DateTime now)
    {
        _load.Stop();
        _loadEnd = now;
        Next(Phase.Cooldown, now);
    }

    private void Next(Phase phase, DateTime now)
    {
        Current = phase;
        _phaseStart = now;
    }

    private double Baseline()
    {
        var b = _samples.Where(x => x.T < (_loadStart == default ? DateTime.MaxValue : _loadStart)).Select(x => x.Temp).ToList();
        return b.Count > 0 ? b.Average() : _samples.FirstOrDefault().Temp;
    }

    private void Finish()
    {
        _load.Stop();
        Current = Phase.Done;
        if (_loadStart == default)
        {
            Report = new CoolingTestReport { StartedAt = StartedAt, Profile = _profile, AbortReason = _abortReason ?? L("負荷をかける前に終了しました") };
            return;
        }
        if (_loadEnd == default) _loadEnd = DateTime.Now;

        double baseline = Baseline();
        var load = _samples.Where(x => x.T >= _loadStart && x.T <= _loadEnd).ToList();
        var last60 = load.Where(x => x.T >= _loadEnd - TimeSpan.FromSeconds(60)).ToList();
        if (last60.Count == 0) last60 = load;
        double steady = last60.Count > 0 ? last60.Average(x => x.Temp) : baseline;
        double target90 = baseline + (steady - baseline) * 0.9;
        var reach = load.FirstOrDefault(x => x.Temp >= target90);
        var cool = _samples.Where(x => x.T > _loadEnd).FirstOrDefault(x => x.Temp <= baseline + 3);
        var firstClocks = load.Where(x => x.T <= _loadStart + TimeSpan.FromSeconds(30) && x.Clock is not null).Select(x => x.Clock!.Value).ToList();
        var lastClocks = last60.Where(x => x.Clock is not null).Select(x => x.Clock!.Value).ToList();
        var powers = load.Where(x => x.Power is not null).Select(x => x.Power!.Value).ToList();

        Report = new CoolingTestReport
        {
            StartedAt = StartedAt,
            Profile = _profile,
            LoadDuration = _loadEnd - _loadStart,
            BaselineTemp = baseline,
            MaxTemp = _samples.Max(x => x.Temp),
            SteadyTemp = steady,
            SecondsTo90 = reach.T == default ? (_loadEnd - _loadStart).TotalSeconds : (reach.T - _loadStart).TotalSeconds,
            RecoverySeconds = cool.T == default ? null : (cool.T - _loadEnd).TotalSeconds,
            AvgPower = powers.Count > 0 ? powers.Average() : null,
            ClockStart = firstClocks.Count > 0 ? firstClocks.Average() : null,
            ClockEnd = lastClocks.Count > 0 ? lastClocks.Average() : null,
            Fans = _fans.Select(f =>
            {
                var samples = _fanSamples[f.ControlId];
                var underLoad = samples.Where(x => x.Load).ToList();
                return new CoolingFanResult
                {
                    Name = f.Name,
                    MaxRpm = samples.Count > 0 ? samples.Max(x => x.Rpm) : 0,
                    AvgDutyUnderLoad = underLoad.Count > 0 ? underLoad.Average(x => x.Duty) : 0,
                };
            }).ToList(),
            AbortReason = _abortReason,
        };
    }

    public void Dispose() => _load.Stop();
}

// ==================================================
// Calibrator.cs - ファン自動キャリブレーション (Duty と回転数の関係を段階的に測定する状態機械)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 手順:
///   1. 100% で回して最大回転数を測る（最短 6 秒。回転数が落ち着くまで、最長 15 秒）
///   2. 100% → 0% へ段階的に下げ、各段で回転数を測る（止まる Duty = StopDuty を見つける）
///   3. 0% から 5% ずつ上げ、回り始める Duty = StartDuty を見つける
/// 1 段ごとに最短 4 秒保持し、回転数が落ち着くまで（最長 8 秒）待ってから測る。
/// 落ち着いた = 直近 3 秒の前半と後半の平均の差が 1%（低い回転では 15 rpm）以内。測定値は後半の平均。
/// 回転がゆっくり変わるファン（GPU のファンなど）でも、上がりきる・下がりきる前の値を記録しないため。
/// FanEngine のスレッドから毎周期 Step() を呼ぶ。
/// </summary>
public sealed class Calibrator
{
    private static readonly double[] DownSteps = { 100, 90, 80, 70, 60, 50, 40, 35, 30, 25, 20, 15, 10, 5, 0 };
    private const double SpinThresholdRpm = 50;
    private static readonly TimeSpan SpinUpTime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan SpinUpMax = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan HoldMax = TimeSpan.FromSeconds(8);
    /// <summary>落ち着いたかを見る長さ（読み取りの間隔が長いときは、その 2 倍）</summary>
    private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(3);
    private const double SettleRatio = 0.01;
    private const double SettleRpm = 15;
    private const double MaxStartSearchDuty = 60;

    private enum Phase { SpinUp, Down, Up, Done }

    private readonly List<CalibrationPoint> _points = new();
    private readonly List<(DateTime T, double Rpm)> _samples = new();
    private Phase _phase = Phase.SpinUp;
    private int _downIndex;
    private double _upDuty;
    private DateTime _stepStarted;
    private DateTime _lastStep = DateTime.MinValue;
    private TimeSpan _interval = TimeSpan.FromSeconds(1);
    private double _maxRpm;
    private double? _stopDuty;
    private double? _startDuty;

    public string ControlId { get; }
    public string FanName { get; }
    public bool IsDone => _phase == Phase.Done;
    public string? FailureReason { get; private set; }

    /// <summary>現在出力すべき Duty</summary>
    public double CurrentDuty => _phase switch
    {
        Phase.SpinUp => 100,
        Phase.Down => DownSteps[_downIndex],
        Phase.Up => _upDuty,
        _ => 100,
    };

    public double Progress => _phase switch
    {
        Phase.SpinUp => 0.02,
        Phase.Down => 0.05 + 0.7 * _downIndex / DownSteps.Length,
        Phase.Up => 0.75 + 0.25 * Math.Min(1, _upDuty / MaxStartSearchDuty),
        _ => 1,
    };

    public string StatusText => _phase switch
    {
        Phase.SpinUp => L("最大回転数を測定中…"),
        Phase.Down => L($"回転を下げながら測定中… ({DownSteps[_downIndex]:F0}%)"),
        Phase.Up => L($"回り始める回転数を探しています… ({_upDuty:F0}%)"),
        _ => L("完了"),
    };

    public Calibrator(string controlId, string fanName, DateTime now)
    {
        ControlId = controlId;
        FanName = fanName;
        _stepStarted = now;
    }

    /// <summary>1 周期分進める。rpm が取得できない場合は中止する</summary>
    public void Step(double? rpm, DateTime now)
    {
        if (IsDone) return;
        if (rpm is null)
        {
            Fail(L("回転数センサーが読めません。"));
            return;
        }

        // 読み取りの間隔（落ち着いたかを見る長さに使う。時計が戻ったときは前の値のまま）
        if (_lastStep != DateTime.MinValue && now > _lastStep) _interval = now - _lastStep;
        _lastStep = now;
        var elapsed = now - _stepStarted;
        _samples.Add((now, rpm.Value));
        var window = Window;
        _samples.RemoveAll(s => now - s.T > window || s.T > now);

        switch (_phase)
        {
            case Phase.SpinUp when Measured(elapsed, SpinUpTime, SpinUpMax, now) is double max:
                _maxRpm = max;
                if (_maxRpm < SpinThresholdRpm)
                {
                    Fail(L("100% でも回転していません。配線を確認してください。"));
                    return;
                }
                _points.Add(new CalibrationPoint { Duty = 100, Rpm = _maxRpm });
                NextStep(Phase.Down, now);
                _downIndex = 1;
                break;

            case Phase.Down when Measured(elapsed, HoldTime, HoldMax, now) is double avg:
            {
                double duty = DownSteps[_downIndex];
                _points.Add(new CalibrationPoint { Duty = duty, Rpm = avg });
                if (avg >= SpinThresholdRpm) _stopDuty = duty; // まだ回っている最低の Duty を更新し続ける

                if (_downIndex < DownSteps.Length - 1)
                {
                    _downIndex++;
                    NextStep(Phase.Down, now);
                }
                else if (avg >= SpinThresholdRpm)
                {
                    // 0% でも回り続けるファン（PWM 非対応や最低回転保証付き）
                    _stopDuty = 0;
                    _startDuty = 0;
                    Finish();
                }
                else
                {
                    _upDuty = 5;
                    NextStep(Phase.Up, now);
                }
                break;
            }

            case Phase.Up when elapsed >= HoldTime:
                // 回り始めたら、上がりきるのを待たずに決める。回らないまま落ち着いた（または最長まで待った）ら次の段へ
                if (Recent(now) >= SpinThresholdRpm)
                {
                    _startDuty = _upDuty;
                    Finish();
                }
                else if (Measured(elapsed, HoldTime, HoldMax, now) is null)
                {
                    break;
                }
                else if (_upDuty >= MaxStartSearchDuty)
                {
                    _startDuty = MaxStartSearchDuty;
                    Finish();
                }
                else
                {
                    _upDuty += 5;
                    NextStep(Phase.Up, now);
                }
                break;
        }
    }

    public CalibrationResult? BuildResult()
    {
        if (FailureReason is not null || !IsDone) return null;
        // 測り方の揺れで Duty を上げたのに回転数が下がる点が出ても、回転数は Duty とともに増える（減らない）ようにならす
        var fitted = CalibrationFit.Monotone(_points);
        return new CalibrationResult
        {
            ControlId = ControlId,
            MaxRpm = Math.Round(fitted.Count > 0 ? fitted.Max(p => p.Rpm) : _maxRpm),
            StopDuty = _stopDuty ?? 0,
            StartDuty = _startDuty ?? _stopDuty ?? 0,
            Points = fitted.Select(p => new CalibrationPoint { Duty = p.Duty, Rpm = Math.Round(p.Rpm) }).ToList(),
            DeadZoneDuty = CalibrationFit.DeadZoneEdge(fitted),
            MeasuredAt = DateTime.Now,
        };
    }

    public void Fail(string reason)
    {
        FailureReason = reason;
        _phase = Phase.Done;
    }

    private void Finish() => _phase = Phase.Done;

    private void NextStep(Phase phase, DateTime now)
    {
        _phase = phase;
        _stepStarted = now;
        _samples.Clear();
    }

    private TimeSpan Window => _interval * 2 > SettleWindow ? _interval * 2 : SettleWindow;

    /// <summary>直近（落ち着きを見る長さの後半）の平均</summary>
    private double Recent(DateTime now)
    {
        var half = Window / 2;
        var recent = _samples.Where(s => now - s.T <= half).Select(s => s.Rpm).ToList();
        return recent.Count > 0 ? recent.Average() : _samples.Count > 0 ? _samples[^1].Rpm : 0;
    }

    /// <summary>
    /// この段の測定値。最短の時間がたち、回転数が落ち着いた（または最長まで待った）ら直近の平均、まだなら null。
    /// 落ち着いた = 前半と後半の平均の差が 1%（15 rpm 未満なら 15 rpm）以内
    /// </summary>
    private double? Measured(TimeSpan elapsed, TimeSpan min, TimeSpan max, DateTime now)
    {
        if (elapsed < min) return null;
        double recent = Recent(now);
        if (elapsed >= max) return recent;
        var half = Window / 2;
        var older = _samples.Where(s => now - s.T > half).Select(s => s.Rpm).ToList();
        int newer = _samples.Count(s => now - s.T <= half);
        if (older.Count == 0 || newer == 0) return null;
        return Math.Abs(recent - older.Average()) <= Math.Max(SettleRpm, recent * SettleRatio) ? recent : null;
    }
}

/// <summary>キャリブレーションの測定点の整え方（測ったときと、推定に使うときの両方で使う）</summary>
public static class CalibrationFit
{
    /// <summary>
    /// Duty の順に並べ、回転数が Duty とともに減らないようにならす（隣り合う逆転を平均でまとめる。単調回帰）。
    /// もともと減らない表はそのまま。古い版で測った逆転のある表（全開が上がりきる前に測れていたなど）も、推定に使えるようにする
    /// </summary>
    public static List<CalibrationPoint> Monotone(IEnumerable<CalibrationPoint> points)
    {
        var pts = points.OrderBy(p => p.Duty).ToList();
        var blocks = new List<(double Sum, int Count)>();
        foreach (var p in pts)
        {
            blocks.Add((p.Rpm, 1));
            while (blocks.Count >= 2 && blocks[^2].Sum / blocks[^2].Count > blocks[^1].Sum / blocks[^1].Count)
            {
                var merged = (blocks[^2].Sum + blocks[^1].Sum, blocks[^2].Count + blocks[^1].Count);
                blocks.RemoveAt(blocks.Count - 1);
                blocks[^1] = merged;
            }
        }
        var result = new List<CalibrationPoint>(pts.Count);
        int i = 0;
        foreach (var (sum, count) in blocks)
        {
            for (int k = 0; k < count; k++, i++) result.Add(new CalibrationPoint { Duty = pts[i].Duty, Rpm = sum / count });
        }
        return result;
    }

    /// <summary>
    /// 低い Duty の「回転数が変わらない範囲」の上の端（0 = なし）。
    /// 回っているいちばん低い Duty から、回転数がそこから 3%（20 rpm 未満なら 20 rpm）以内の点が 10% 以上の幅で続くとき、その最後の Duty。
    /// ファン自身が最低回転数を守っている（例: 0〜20% はどれも 850 rpm）と、その範囲の指示では音が変わらない。
    /// 全部の点が同じ回転数（PWM で回転が変わらないファン）は、境目がないので 0
    /// </summary>
    public static double DeadZoneEdge(IReadOnlyList<CalibrationPoint> fitted)
    {
        var spin = fitted.Where(p => p.Rpm >= 50).OrderBy(p => p.Duty).ToList();
        if (spin.Count < 3) return 0;
        double baseRpm = spin[0].Rpm;
        double tol = Math.Max(20, baseRpm * 0.03);
        int last = 0;
        while (last + 1 < spin.Count && spin[last + 1].Rpm - baseRpm <= tol) last++;
        if (last == spin.Count - 1 || spin[last].Duty - spin[0].Duty < 10) return 0;
        return spin[last].Duty;
    }
}

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
    /// <summary>この回転数で落ち着く温度（測った温度と、温度の上がり方から予測した温度の高いほう）</summary>
    public double SteadyTemp { get; init; }
    /// <summary>最後の 15 秒に実際に測った温度の平均（null: ほかで作った結果）</summary>
    public double? MeasuredTemp { get; init; }
    /// <summary>温度の上がり方（指数の曲線）から予測した、落ち着く温度（予測できなかったら null）</summary>
    public double? PredictedTemp { get; init; }
    /// <summary>測り終えたとき、まだ温度が上がっていたか（長く待っても落ち着かなかった・途中で止めた）</summary>
    public bool StillRising { get; init; }
    /// <summary>まだ上がっていた段の余裕（°C）。必要な回転数は、この分だけ熱いとみなして決める</summary>
    public double SoakMargin { get; init; }
    /// <summary>必要な回転数を決めるときの温度（落ち着く温度 + 余裕）</summary>
    public double SafeTemp => SteadyTemp + SoakMargin;
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
    /// <summary>測る予定のいちばん低い回転数まで測れたか（途中で終わると、それより低くてよいかは分からない）</summary>
    public bool ReachedLowestStep { get; init; } = true;

    public bool Usable => Steps.Count >= 2;

    /// <summary>
    /// この目標で、必要な回転数が測った段から決まるか。
    /// false: 途中で終わり、どの段も目標以下だった（「いちばん低く測った段以下」までしか分からない）ので、カーブは作らない
    /// </summary>
    public bool NeedKnown(double target) => Usable && AutoTuner.NeedMeasured(Steps, target, ReachedLowestStep);

    public string ToText(double target)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L($"自動調整  {MeasuredAt:yyyy/MM/dd HH:mm} ／ 温度センサー: {SensorName}"));
        if (AbortReason is not null) sb.AppendLine($"※ {AbortReason}");
        sb.AppendLine(L($"待機時の温度: {IdleTemp:F1} °C"));
        foreach (var s in Steps)
        {
            string rpm = s.AvgRpm is double r ? L($"（{r:N0} rpm）") : "";
            // まだ上がっていた段は、実際に測った温度も残す（予測でどれだけ足したかが分かるように）
            string rising = s.StillRising && s.MeasuredTemp is double m ? L($"（まだ上昇中・実測 {m:F1} °C から予測）") : "";
            sb.AppendLine($"  {s.Duty,3:F0}% → {s.SteadyTemp:F1} °C{rpm}{rising}  {s.Seconds:F0} s");
        }
        // 1 段も測れなかったとき（負荷がかからなかったなど）は、目標を守れるかは分からない
        if (Steps.Count == 0) return sb.ToString();
        var need = AutoTuner.RequiredDuty(Steps, target);
        var top = Steps.OrderByDescending(x => x.Duty).First();
        sb.AppendLine(need.Insufficient
            ? L($"目標 {target:F0}°C は全開でも届きません（全開で {top.SteadyTemp:F1}°C）")
            : !AutoTuner.NeedMeasured(Steps, target, ReachedLowestStep)
                ? L($"目標 {target:F0}°C を守るのに必要な回転数: {need.Duty:F0}% 以下（途中で終わったため、これより低い回転は測れていません）")
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
///
/// 簡易水冷は、ファンを下げてから冷却液が温まりきるまで 6〜10 分かかる（1 分に 1°C くらいずつ上がり続ける）。
/// そのため「落ち着いた」は、直近 90 秒の温度の傾き（1 分あたり 0.25°C 未満）と、温度の上がり方に合わせた
/// 指数の曲線（あとどれだけ上がるか）で決める。必要な回転数が決まる段（いちばん低い段・目標に近い段）は最長 10 分待ち、
/// 待ちきれなかったときも、曲線から予測した落ち着く温度と余裕を足して記録する（途中で止めても安全側になる）。
/// </summary>
public sealed class AutoTuner : IDisposable
{
    public enum Phase { Idle, WaitLoad, Warmup, Steps, Cooldown, Done }

    private static readonly TimeSpan IdleTime = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WaitLoadTimeout = TimeSpan.FromMinutes(3);
    /// <summary>内蔵の負荷で、使用率が上がるのを待つ上限（10 秒続けて 70% 以上になれば始める）</summary>
    private static readonly TimeSpan BuiltInLoadTimeout = TimeSpan.FromSeconds(45);
    /// <summary>1 段の最短（温度の傾きを 90 秒で見るので、それより短くは決めない）</summary>
    private static readonly TimeSpan MinHold = TimeSpan.FromSeconds(90);
    /// <summary>必要な回転数が決まる段の最短（短い記録では、ゆっくり温まり続けているのか落ち着いたのか見分けられないため）</summary>
    private static readonly TimeSpan DecisiveMinHold = TimeSpan.FromSeconds(180);
    /// <summary>ふつうの段（目標より十分低い段。必要な回転数には効かない）の最長</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(120);
    /// <summary>必要な回転数が決まる段（いちばん低い段・目標に近い段）の最長。簡易水冷の冷却液が温まりきるまで待つ</summary>
    public static readonly TimeSpan DecisiveMaxHold = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan WarmupMin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WarmupMax = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan CooldownTime = TimeSpan.FromSeconds(20);
    /// <summary>温度の傾き（°C / 分）がこれ未満なら「落ち着いた」</summary>
    private const double SteadySlope = 0.25;
    /// <summary>温度の傾きを見る長さ</summary>
    private static readonly TimeSpan SlopeSpan = TimeSpan.FromSeconds(90);
    /// <summary>傾きを見るのに要る記録の長さ（回数ではなく秒で見る。読み取りの間隔が長くても同じに判断する）</summary>
    private static readonly TimeSpan SteadySpan = TimeSpan.FromSeconds(85);
    /// <summary>落ち着いた状態がこれだけ続いたら記録する（一瞬の読み違いで決めない）。必要な回転数が決まる段は長めに見る</summary>
    private static readonly TimeSpan SteadyFor = TimeSpan.FromSeconds(15), StrictSteadyFor = TimeSpan.FromSeconds(30);
    /// <summary>段の温度は、最後のこの時間の平均にする</summary>
    private static readonly TimeSpan RecordSpan = TimeSpan.FromSeconds(15);
    /// <summary>予測（指数の曲線）を計算し直す間隔</summary>
    private static readonly TimeSpan FitEvery = TimeSpan.FromSeconds(5);
    /// <summary>落ち着く前に止めた段の余裕（°C）: 曲線がよく合ったとき / 合わなかった・測った時間が短かったとき</summary>
    public const double SoakMarginFit = 1.0, SoakMarginRough = 3.0;
    /// <summary>温度がこの幅（目標 − 1.5°C より下）に入った段は、必要な回転数が決まる段として長く待つ</summary>
    private const double DecisiveBand = 4;
    /// <summary>必要な回転数を決めるときの、目標温度との余裕</summary>
    public const double NeedMargin = 1.5;
    /// <summary>カーブで、目標の 2°C 手前に少なくともこれだけ回す（%）</summary>
    public const double CurveFloor = 35;
    /// <summary>カーブの上がり方の上限（1°C あたりの %。全開でも届かないときを除く）</summary>
    public const double MaxCurveSlope = 6;
    private const double ExternalLoadThreshold = 70;
    /// <summary>使用率がこれ未満になったら「負荷がなくなった」とみなす</summary>
    private const double LoadLostThreshold = 40;
    /// <summary>負荷がなくなった状態がこれだけ続いたら中止する（ゲームの読み込み・場面の切り替えなどの一瞬の落ち込みでは中止しない）</summary>
    private static readonly TimeSpan LoadLostAbort = TimeSpan.FromSeconds(6);
    /// <summary>負荷が落ち込んでから、温度が戻るまでの時間（この間の温度は、落ち着いたかの判断にも記録にも使わない）</summary>
    private static readonly TimeSpan DipTaint = TimeSpan.FromSeconds(20);
    /// <summary>負荷が落ち込んだ段は、落ち込みから少なくともこれだけたってから記録する</summary>
    private static readonly TimeSpan DipSettle = TimeSpan.FromSeconds(30);
    /// <summary>何度も落ち込み続けても、段の最長からこれ以上は待たない</summary>
    private static readonly TimeSpan DipWaitMax = TimeSpan.FromSeconds(60);
    /// <summary>温度の記録を残す長さ（予測に使うので、いちばん長い段より少し長く）</summary>
    private static readonly TimeSpan TraceSpan = DecisiveMaxHold + DipWaitMax + TimeSpan.FromSeconds(30);
    /// <summary>
    /// ユーザーが中止したとき、今の段をこれ以上測っていれば、予測と余裕を足して記録する。
    /// 曲線がよく合ったときは MinHold から記録する（簡易水冷で 1〜3 分しか測っていないと、予測が低すぎて、
    /// 足りない回転数を「これで足りる」と決めてしまうため）
    /// </summary>
    private static readonly TimeSpan PartialMin = TimeSpan.FromSeconds(240);
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
    /// <summary>今の段階の温度の記録（負荷が落ち込んだ直後は入れない）</summary>
    private readonly List<(DateTime T, double Temp)> _trace = new();
    private readonly List<double> _idle = new();
    private readonly List<double> _rpms = new();
    private readonly List<AutoTuneStep> _steps = new();
    /// <summary>今の段階を始めた時刻（スナップショットの時刻で測る。最初のスナップショットで決まる）</summary>
    private DateTime _phaseStart = DateTime.MinValue;
    /// <summary>最後のスナップショットの時刻（進み具合・中止のときに使う）</summary>
    private DateTime _lastNow = DateTime.MinValue;
    private DateTime _loadSeenSince = DateTime.MinValue;
    /// <summary>負荷がなくなり始めた時刻（MinValue: 今は負荷がある）と、最後に負荷が落ち込んでいた時刻</summary>
    private DateTime _loadLostSince = DateTime.MinValue;
    private DateTime _lastLoadDip = DateTime.MinValue;
    /// <summary>落ち着いた状態になった時刻（MinValue: まだ）</summary>
    private DateTime _steadySince = DateTime.MinValue;
    /// <summary>今の段の予測（5 秒ごとに計算し直す）</summary>
    private Approach? _fit;
    private DateTime _fitAt = DateTime.MinValue;
    /// <summary>今の段が、必要な回転数が決まる段か（一度そうなったら、その段の間はそのまま）</summary>
    private bool _decisive;
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
    /// <summary>今の段で落ち着くと予測した温度（まだ予測できなければ null）</summary>
    public double? PredictedTemp => Current == Phase.Steps && _fit is { } f ? f.Final : null;
    /// <summary>今の段が、必要な回転数が決まる段か（長く待つ）</summary>
    public bool Decisive => Current == Phase.Steps && _decisive;

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

    /// <summary>段ごとの目安の長さ（秒。進み具合用）: いちばん低い段は冷却液が温まるまで長くかかることが多い</summary>
    private double ExpectedHold(int i) => i == _duties.Count - 1 ? 360 : 120;

    /// <summary>全体の目安時間</summary>
    public TimeSpan Estimated => IdleTime + TimeSpan.FromSeconds(10 + 120 + Enumerable.Range(0, _duties.Count).Sum(ExpectedHold)) + CooldownTime;

    private TimeSpan Elapsed => _lastNow == DateTime.MinValue || _lastNow < _phaseStart ? TimeSpan.Zero : _lastNow - _phaseStart;

    public double Progress => Current switch
    {
        Phase.Idle => 0.02,
        Phase.WaitLoad => 0.05,
        Phase.Warmup => 0.08 + 0.10 * Math.Min(1, Elapsed / WarmupMax),
        Phase.Steps => 0.18 + 0.79 * StepsProgress(),
        Phase.Cooldown => 0.97,
        _ => 1,
    };

    /// <summary>段の進み具合（0〜1）。段ごとの目安の長さで重みを付け、今の段はその段の最長に対する割合</summary>
    private double StepsProgress()
    {
        double total = Enumerable.Range(0, _duties.Count).Sum(ExpectedHold);
        double done = Enumerable.Range(0, _stepIndex).Sum(ExpectedHold);
        var max = _decisive ? DecisiveMaxHold : MaxHold;
        return (done + ExpectedHold(_stepIndex) * Math.Min(0.95, Elapsed / max)) / total;
    }

    public string StatusText => Current switch
    {
        Phase.Idle => L("待機時の温度を測っています…"),
        Phase.WaitLoad when _verifyLoad => L("GPU に負荷をかけています（使用率が上がるのを待っています）…"),
        Phase.WaitLoad when _builtInLoad => L("負荷を開始しています…"),
        Phase.WaitLoad => L("ゲームやベンチマークを起動してください（GPU 使用率が 70% を超えたら自動で始まります）"),
        Phase.Warmup => L("全開で温まるまで待っています…"),
        Phase.Steps => StepText(),
        Phase.Cooldown => L("負荷を止めて、いつもの制御に戻しています…"),
        _ => _abortReason is null ? L("完了しました") : L($"中断: {_abortReason}"),
    };

    private string StepText()
    {
        var e = Elapsed;
        string elapsed = $"{(int)e.TotalMinutes}:{e.Seconds:00}";
        double duty = _duties[_stepIndex];
        int no = _stepIndex + 1, count = _duties.Count;
        if (_decisive)
        {
            double max = DecisiveMaxHold.TotalMinutes;
            return L($"{duty:F0}% で温度が落ち着くのを待っています。この段で必要な回転数が決まるので、水冷の冷却液が温まりきるまで長くて {max:F0} 分待ちます（{no} / {count} 段目・{elapsed} 経過）");
        }
        double cap = MaxHold.TotalMinutes;
        return L($"{duty:F0}% で温度が落ち着くのを待っています…（{no} / {count} 段目・{elapsed} 経過・長くて {cap:F0} 分）");
    }

    public void OnSnapshot(EngineSnapshot s)
    {
        if (Current == Phase.Done) return;
        if (!s.Values.TryGetValue(_sensorId, out var tv) || tv is null)
        {
            Stop(L("温度センサーが読めません"));
            return;
        }
        double temp = tv.Value;
        LastTemp = temp;
        var now = s.Time;
        _lastNow = now;
        if (_phaseStart == DateTime.MinValue) _phaseStart = now;
        // 負荷が落ち込んでいる・落ち込んだ直後の温度は記録に入れない（下がって戻る温度で、落ち着いたと見誤らないように）
        bool dipNow = _loadSensorId is not null && LoadOf(s) is double ld && ld < LoadLostThreshold;
        bool tainted = dipNow || (_lastLoadDip != DateTime.MinValue && now >= _lastLoadDip && now - _lastLoadDip <= DipTaint);
        if (!tainted) _trace.Add((now, temp));
        _trace.RemoveAll(w => now - w.T > TraceSpan);

        // 危険な温度・緊急全開になったら、すぐにやめる
        if (s.Emergency || temp >= _abortTemp)
        {
            if (Current is Phase.Warmup or Phase.Steps)
            {
                if (Current == Phase.Steps) Record(now, aborted: true, settled: false);
                Stop(s.Emergency ? L("緊急全開になったため中止しました") : L($"温度が {temp:F0}°C に達したため中止しました"));
                return;
            }
        }

        // 負荷がかからなくなったら中止（外部の負荷: ゲームなどがやめられた。内蔵の負荷: GPU が止めた）。
        // 1 回読んだだけでは中止しない: ゲームの読み込みや場面の切り替えで一瞬下がることがあるので、6 秒続いたら中止する
        if (_loadSensorId is not null && Current is Phase.Warmup or Phase.Steps && LoadOf(s) is double load)
        {
            if (load < LoadLostThreshold)
            {
                // 時計が戻ったときは、そこから数え直す
                if (_loadLostSince == DateTime.MinValue || now < _loadLostSince) _loadLostSince = now;
                _lastLoadDip = now;
                if (now - _loadLostSince >= LoadLostAbort)
                {
                    Stop(_builtInLoad
                        ? L("GPU の負荷が止まったため中止しました")
                        : L("GPU の負荷がなくなったため中止しました（ゲームやベンチマークを動かし続けてください）"));
                    return;
                }
            }
            else
            {
                _loadLostSince = DateTime.MinValue;
            }
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
                    Stop(L($"負荷をかけられませんでした: {loadError}"));
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
                    Stop(_builtInLoad
                        ? L("GPU に負荷をかけましたが、使用率が上がらなかったため中止しました")
                        : L("3 分待っても GPU に負荷がかからなかったため中止しました（ゲームやベンチマークを起動してください）"));
                }
                break;

            case Phase.Warmup:
            {
                Override(100, now);
                RefreshFit(now);
                bool steady = UpdateSteady(now, strict: false);
                if (now - _phaseStart >= WarmupMax || (now - _phaseStart >= WarmupMin && steady))
                {
                    _stepIndex = 0;
                    _rpms.Clear();
                    Next(Phase.Steps, now);
                }
                break;
            }

            case Phase.Steps:
            {
                Override(_duties[_stepIndex], now);
                var rpms = _fans.Select(f => s.Fans.FirstOrDefault(x => x.ControlId == f.ControlId)?.Rpm).OfType<double>().ToList();
                var elapsed = now - _phaseStart;
                if (rpms.Count > 0 && elapsed > TimeSpan.FromSeconds(10)) _rpms.Add(rpms.Average());
                RefreshFit(now);
                double recent = Recent(now);
                // いちばん低い段か、温度（予測を含む）が目標に近い段は、必要な回転数が決まるので長く待つ
                double estimate = _fit is { } f ? Math.Max(recent, f.Final) : recent;
                if (_stepIndex == _duties.Count - 1 || estimate >= _target - NeedMargin - DecisiveBand) _decisive = true;
                var maxHold = _decisive ? DecisiveMaxHold : MaxHold;
                // 必要な回転数が決まる段は、傾きと予測の両方で落ち着いたと分かるまで待つ
                bool steady = UpdateSteady(now, strict: _decisive);
                // 負荷が一瞬落ち込んだばかりなら、落ち込みが記録する時間から外れるまで待つ
                bool dipRecent = _lastLoadDip != DateTime.MinValue && now >= _lastLoadDip && now - _lastLoadDip <= DipSettle
                                 && elapsed < maxHold + DipWaitMax;
                // 目標を大きく超えた段は、落ち着くまで待たない（それより下げても意味がない）
                bool hot = elapsed >= MinHold && recent >= _target + 6;
                var minHold = _decisive ? DecisiveMinHold : MinHold;
                if (!dipRecent && (hot || elapsed >= maxHold || (elapsed >= minHold && steady)))
                {
                    var step = Record(now, aborted: false, settled: steady);
                    if (step.SteadyTemp >= _target + 6 || _stepIndex + 1 >= _duties.Count) Finish(now);
                    else
                    {
                        _stepIndex++;
                        _rpms.Clear();
                        Next(Phase.Steps, now);
                    }
                }
                break;
            }

            case Phase.Cooldown:
                if (now - _phaseStart >= CooldownTime) Complete();
                break;
        }
    }

    private double? LoadOf(EngineSnapshot s)
        => _loadSensorId is not null && s.Values.TryGetValue(_loadSensorId, out var v) ? v : null;

    /// <summary>最後の 15 秒の温度の平均（記録がなければ今の温度）</summary>
    private double Recent(DateTime now)
        => _trace.Where(w => now - w.T <= RecordSpan).Select(w => w.Temp).DefaultIfEmpty(LastTemp).Average();

    private List<(double Sec, double Temp)> TraceSeconds() => _trace.Select(w => ((w.T - _phaseStart).TotalSeconds, w.Temp)).ToList();

    private void RefreshFit(DateTime now)
    {
        if (_fitAt != DateTime.MinValue && now >= _fitAt && now - _fitAt < FitEvery) return;
        _fitAt = now;
        _fit = FitApproach(TraceSeconds());
    }

    /// <summary>
    /// 落ち着いたか（15 秒続けて満たしたら）:
    ///   ・傾き: 直近 90 秒の温度の傾きが 1 分あたり 0.25°C 未満
    ///   ・予測: 指数の曲線がよく合い、変化がばらつきよりはっきり大きく、測った時間が時定数の 2 倍を超え
    ///     （曲がりきったところまで見えている）、あと 1°C も変わらない（落ち着く温度は予測で記録するので、少し手前で止めてよい）
    /// strict（必要な回転数が決まる段）のときは、短い記録の読み違いを防ぐため、
    ///   ・よく合う曲線が「まだ 1°C 以上上がる」と言っている間は、傾きでは決めない
    ///   ・30 秒続けて満たしたら
    /// </summary>
    private bool UpdateSteady(DateTime now, bool strict)
    {
        bool bySlope = IsSteady(_trace);
        bool byFit = _fit is { Trusted: true } f && f.Remaining < FitDoneWithin && f.Amplitude >= 3 * Math.Max(f.Rms, 0.3)
                     && f.SpanSec >= 2 * f.TauSec;
        bool fitSaysMore = _fit is { Trusted: true } g && g.Remaining >= FitDoneWithin;
        bool ok = byFit || (bySlope && !(strict && fitSaysMore));
        if (!ok) _steadySince = DateTime.MinValue;
        else if (_steadySince == DateTime.MinValue || now < _steadySince) _steadySince = now;
        return ok && now - _steadySince >= (strict ? StrictSteadyFor : SteadyFor);
    }

    /// <summary>
    /// 温度の傾きが小さいか: 85 秒以上の記録があり、直近 90 秒の温度の傾き（最小二乗の直線）が 1 分あたり 0.25°C 未満。
    /// 回数ではなく時間で見るので、読み取りの間隔（0.25〜5 秒）が違っても同じに判断する
    /// </summary>
    internal static bool IsSteady(IReadOnlyList<(DateTime T, double Temp)> window)
        => SlopePerMinute(window) is double slope && Math.Abs(slope) < SteadySlope;

    /// <summary>直近 90 秒の温度の傾き（°C / 分）。記録が 85 秒に足りなければ null</summary>
    internal static double? SlopePerMinute(IReadOnlyList<(DateTime T, double Temp)> window)
    {
        if (window.Count < 6) return null;
        var t1 = window[^1].T;
        var pts = window.Where(w => t1 - w.T <= SlopeSpan && w.T <= t1).ToList();
        if (pts.Count < 6 || t1 - pts[0].T < SteadySpan) return null;
        double mx = pts.Average(p => (p.T - t1).TotalSeconds), my = pts.Average(p => p.Temp);
        double sxx = 0, sxy = 0;
        foreach (var p in pts)
        {
            double dx = (p.T - t1).TotalSeconds - mx;
            sxx += dx * dx;
            sxy += dx * (p.Temp - my);
        }
        return sxx > 0 ? sxy / sxx * 60 : null;
    }

    /// <summary>
    /// 温度の上がり方に合わせた指数の曲線 T(t) = Final − (Final − T0)·exp(−t / τ) の結果。
    /// Remaining: 最後の時点から、あと何 °C 変わるか。EndSlope: 最後の時点の傾き（°C / 分）。Amplitude: 測った間に変わった幅。
    /// Trusted: よく合い、測った時間が τ より長い（短いと曲がり方が見えず、落ち着く温度は「少なくともこのくらい」としか言えない）
    /// </summary>
    internal readonly record struct Approach(double Final, double TauSec, double Rms, double Remaining, double EndSlope, double Amplitude, double SpanSec, bool Rising, bool Trusted);

    /// <summary>最初のこの秒数は使わない（ダイの温度がすぐ動く部分。冷却液・ヒートシンクのゆっくりした変化だけを見る）</summary>
    private const double FitSkipSec = 15;
    /// <summary>曲線とのずれ（°C、2 秒ごとの平均で）がこれより大きければ、予測しない（ばらつきの大きいセンサーでは広げる）</summary>
    private const double FitRmsMax = 1.5;
    /// <summary>よく合う曲線で、あとこれだけしか変わらなければ落ち着いたとみなす（°C）</summary>
    private const double FitDoneWithin = 1.0;
    /// <summary>よく合ったとみなすずれ（ばらつきの大きいセンサーでは広げる）</summary>
    private const double FitRmsGood = 1.0;
    /// <summary>予測は、最後の時点の温度からこれより上にはしない</summary>
    private const double FitMaxRise = 12;

    /// <summary>
    /// 段の温度の記録（段を始めてからの秒, 温度）に、指数の曲線を当てはめて、落ち着く温度を予測する。
    /// 2 秒ごとに平均してから、τ を 8 秒〜測った時間の 2 倍の範囲で探し、いちばんよく合うものを選ぶ。
    /// 60 秒分の記録がない・よく合わないときは null
    /// </summary>
    internal static Approach? FitApproach(IReadOnlyList<(double Sec, double Temp)> samples)
    {
        var bins = samples.Where(p => p.Sec >= FitSkipSec)
            .GroupBy(p => Math.Floor(p.Sec / 2))
            .OrderBy(g => g.Key)
            .Select(g => (Sec: g.Average(p => p.Sec), Temp: g.Average(p => p.Temp)))
            .ToList();
        if (bins.Count < 15) return null;
        double t0 = bins[0].Sec, span = bins[^1].Sec - t0;
        if (span < 60) return null;
        double tauMin = 8, tauMax = 2 * span;
        double bestSse = double.MaxValue, bestA = 0, bestB = 0, bestTau = 0;
        const int Grid = 48;
        var xs = new double[bins.Count];
        for (int k = 0; k < Grid; k++)
        {
            double tau = tauMin * Math.Pow(tauMax / tauMin, k / (double)(Grid - 1));
            double mx = 0, my = 0;
            for (int i = 0; i < bins.Count; i++)
            {
                xs[i] = Math.Exp(-(bins[i].Sec - t0) / tau);
                mx += xs[i];
                my += bins[i].Temp;
            }
            mx /= bins.Count;
            my /= bins.Count;
            double sxx = 0, sxy = 0;
            for (int i = 0; i < bins.Count; i++)
            {
                sxx += (xs[i] - mx) * (xs[i] - mx);
                sxy += (xs[i] - mx) * (bins[i].Temp - my);
            }
            if (sxx < 1e-12) continue;
            double b = sxy / sxx, a = my - b * mx, sse = 0;
            for (int i = 0; i < bins.Count; i++)
            {
                double r = bins[i].Temp - a - b * xs[i];
                sse += r * r;
            }
            if (sse < bestSse) (bestSse, bestA, bestB, bestTau) = (sse, a, b, tau);
        }
        if (bestTau == 0) return null;
        double rms = Math.Sqrt(bestSse / bins.Count);
        // 読み取りのばらつき（となり合う平均の差から見る）。ばらつきの大きいセンサーでは、合ったとみなす幅を少し広げる（2°C まで）
        double noise = Math.Sqrt(Enumerable.Range(1, bins.Count - 1).Average(i => Math.Pow(bins[i].Temp - bins[i - 1].Temp, 2)) / 2);
        double good = Math.Clamp(1.3 * noise, FitRmsGood, 2.0);
        if (rms > Math.Max(FitRmsMax, 1.5 * good)) return null;
        double endX = Math.Exp(-span / bestTau);
        double fittedEnd = bestA + bestB * endX;
        double final = Math.Min(bestA, fittedEnd + FitMaxRise);
        double remaining = Math.Abs(final - fittedEnd);
        double endSlope = Math.Abs(bestB) / bestTau * endX * 60;
        // よく合い、測った時間が時定数より長い（曲がり方が見えている）ときだけ信用する
        bool trusted = rms <= good && bestTau < span;
        // Amplitude: 曲線が測った間に変わった幅
        double amplitude = Math.Abs(bestB) * (1 - endX);
        return new Approach(final, bestTau, rms, remaining, endSlope, amplitude, span, Rising: bestB < 0, trusted);
    }

    /// <summary>
    /// 今の段を記録する。落ち着く温度 = 最後の 15 秒の平均と、予測の高いほう。
    /// 落ち着く前に止めた（まだ上がっていた）段は、予測の確かさに応じて余裕を足す
    /// </summary>
    private AutoTuneStep Record(DateTime now, bool aborted, bool settled)
    {
        double recent = Recent(now);
        // 途中で止めた段は、まだ上がっている途中なので「少なくともこの温度」として記録する
        double measured = aborted ? Math.Max(recent, LastTemp) : recent;
        var fit = FitApproach(TraceSeconds());
        var slope = SlopePerMinute(_trace);
        // 下がっている途中で終わった段は、余裕は要らない
        bool falling = slope is double sl ? sl <= -SteadySlope : fit is { Rising: false };
        bool rising = !settled && !falling;
        // 落ち着いた段は、よく合った予測だけを使う（合わない曲線を先へ延ばすと、ずれが大きくなるため）
        double? predicted = fit is { } f && (rising || f.Trusted) ? f.Final : null;
        // 余裕: まだ上がっていた段は予測の確かさに応じて。傾きで落ち着いたとみなした段（曲線は合わなかった）も、
        // 残っている傾きのまま最長 10 分（簡易水冷の冷却液の時定数くらい）上がり続けたとして足す
        double margin = rising ? (fit is { Trusted: true } ? SoakMarginFit : SoakMarginRough)
            : settled && fit is not { Trusted: true } && slope is double up && up > 0 ? Math.Min(SoakMarginRough, up * DecisiveMaxHold.TotalMinutes)
            : 0;
        var step = new AutoTuneStep
        {
            Duty = _duties[_stepIndex],
            SteadyTemp = Math.Max(measured, predicted ?? measured),
            MeasuredTemp = measured,
            PredictedTemp = predicted,
            StillRising = rising,
            SoakMargin = margin,
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
        _trace.Clear();
        _steadySince = DateTime.MinValue;
        _fit = null;
        _fitAt = DateTime.MinValue;
        _decisive = false;
    }

    private void Finish(DateTime now)
    {
        _load.Stop();
        Release();
        Next(Phase.Cooldown, now);
    }

    /// <summary>
    /// ユーザーが中止した。今の段を 4 分以上（曲線がよく合っていれば 90 秒以上）測っていれば、予測と余裕を足して記録してから終わる
    /// （途中で止めても、測れた分は安全側の値で残す。短いと記録しないので、いちばん低い段なら「測れていない」になる）
    /// </summary>
    public void Abort(string reason)
    {
        if (Current == Phase.Done) return;
        var e = Elapsed;
        if (Current == Phase.Steps && (e >= PartialMin || (e >= MinHold && FitApproach(TraceSeconds()) is { Trusted: true })))
            Record(_lastNow, aborted: true, settled: false);
        Stop(reason);
    }

    private void Stop(string reason)
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
            ReachedLowestStep = _steps.Count > 0 && _steps.Min(x => x.Duty) <= _duties[^1],
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

    /// <summary>
    /// 目標温度を守れる最低の回転数（測定点の間は直線で補間）。全開でも届かなければ Insufficient。
    /// 温度は SafeTemp（まだ上がっていた段は余裕を足した温度）で見る
    /// </summary>
    public static (double Duty, bool Insufficient) RequiredDuty(IReadOnlyList<AutoTuneStep> steps, double target, double margin = NeedMargin)
    {
        var pts = steps.OrderByDescending(s => s.Duty).ToList();
        if (pts.Count == 0) return (100, true);
        double goal = target - margin;
        if (pts[0].SafeTemp > goal) return (100, true);
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var hi = pts[i];
            var lo = pts[i + 1];
            if (lo.SafeTemp <= goal) continue;
            // hi は目標以下、lo は目標超え → その間
            double k = (lo.SafeTemp - goal) / Math.Max(0.1, lo.SafeTemp - hi.SafeTemp);
            return (Math.Clamp(lo.Duty + k * (hi.Duty - lo.Duty), lo.Duty, hi.Duty), false);
        }
        return (pts[^1].Duty, false); // いちばん低い回転でも目標以下
    }

    /// <summary>
    /// 必要な回転数が、測った段から決まるか。
    /// 目標を超えた段があれば（その間を補間できる・全開でも届かない）決まる。どの段も目標以下なら、
    /// 予定のいちばん低い回転数まで測れたときだけ決まる（途中で終わったときは「測った段以下」としか言えない）
    /// </summary>
    public static bool NeedMeasured(IReadOnlyList<AutoTuneStep> steps, double target, bool reachedLowest, double margin = NeedMargin)
    {
        if (steps.Count == 0) return false;
        double goal = target - margin;
        return reachedLowest || steps.Any(s => s.SafeTemp > goal);
    }

    /// <summary>
    /// カーブ:
    ///   ・待機温度付近（待機 + 3°C か、目標の 14°C 手前の低いほう）は最低回転
    ///   ・目標の 10°C 手前までに少し上げ、目標の 2°C 手前で「必要な回転数」か 35% の高いほうにする
    ///     （必要な回転数がとても低くても、目標の直前まで最低回転のままにしない。長い負荷で温度がじわじわ上がっても、
    ///      急に回り出さずに少しずつ上がる）
    ///   ・目標 + 4°C でその回転数と全開の中間、目標 + 10°C で全開（1°C あたりの上がり方をなだらかにする）
    /// 全開でも届かないときは、目標の 4°C 手前で全開にする
    /// </summary>
    public static List<CurvePoint> BuildCurve(IReadOnlyList<AutoTuneStep> steps, double idleTemp, double target, double minDuty)
    {
        var (need, insufficient) = RequiredDuty(steps, target);
        need = Math.Max(need, minDuty);
        double t1 = Math.Min(idleTemp + 3, target - 14);
        var pts = new List<CurvePoint> { new() { Temp = Math.Round(t1), Duty = Math.Round(minDuty) } };
        if (insufficient || need >= 99)
        {
            pts.Add(new CurvePoint { Temp = Math.Round(target - 9), Duty = Math.Round(minDuty + (100 - minDuty) * 0.5) });
            pts.Add(new CurvePoint { Temp = Math.Round(target - 4), Duty = 100 });
        }
        else
        {
            double hold = Math.Min(100, Math.Max(need, Math.Max(CurveFloor, minDuty)));
            // 目標の 10°C 手前: 最低回転と目標の 2°C 手前の間の 4 割（そこから先が 1°C あたり 6% を超えないように）
            double d1 = Math.Max(minDuty + (hold - minDuty) * 0.4, hold - MaxCurveSlope * 8);
            // 最低回転からそこまでも 1°C あたり 6% まで（必要な回転数が高いときは、待機温度の少し上から回し始める）
            pts[0].Temp = Math.Round(Math.Max(20, Math.Min(t1, target - 10 - (d1 - minDuty) / MaxCurveSlope)));
            pts.Add(new CurvePoint { Temp = Math.Round(target - 10), Duty = Math.Round(d1) });
            pts.Add(new CurvePoint { Temp = Math.Round(target - 2), Duty = Math.Round(hold) });
            pts.Add(new CurvePoint { Temp = Math.Round(target + 4), Duty = Math.Round(hold + (100 - hold) * 0.5) });
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

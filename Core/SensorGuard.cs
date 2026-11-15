// ==================================================
// SensorGuard.cs - 温度センサーの異常値フィルター (一瞬だけ跳ねた値を無視・負荷が変わっても動かない「固着」を検知)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 制御に使う温度センサーの値を、次の 2 つの異常から守る。
///   ・スパイク: 1 回だけ大きく跳ねて、次の回には元に戻る値（読み取りの瞬断など）。
///              大きな変化は 2 回続いて（かつ 約 1 秒続いて）初めて受け入れるので、本物の温度上昇は 約 1 秒遅れるだけで反映される。
///   ・固着    : 同じ値のまま変わらないのに、同じ部品の使用率は大きく変わっている（センサーが止まっている）。
///              アイドル中に温度が一定なのは普通なので、「負荷が変わったのに温度が動かない」ときだけ異常とみなす。
///              ただし緊急温度に近い高い温度（NoStuckAbove 以上）では固着と判定しない
///              （本当に熱いのに「異常値」として捨てると、緊急全開の判定から外れてしまうため）。
/// 時刻は経過時間だけを使う（FanEngine は時計合わせの影響を受けない単調な時刻を渡す）。
/// FanEngine のスレッドからだけ使う。
/// </summary>
public sealed class SensorGuard
{
    /// <summary>1 周期でこれ以上変わったら、次の周期で確認してから受け入れる</summary>
    public const double SpikeThreshold = 20;
    /// <summary>
    /// 大きく変わった値は、最初に来てからこの時間（秒）がたつまで受け入れない（同じくらいの値が続いたとき）。
    /// 1 秒ごとの更新で「2 回続いたら」と同じ長さ。回数だけで決めると、更新間隔を 250ms にしたとき 0.25 秒で通ってしまう
    /// </summary>
    public const double ConfirmSeconds = 0.8;
    /// <summary>値が毎回違っていても、最初に来てからこの時間（秒）続けて離れていれば受け入れる（1 秒ごとの更新で 3 回分）</summary>
    public const double ForceAcceptSeconds = 1.6;
    /// <summary>この時間、値がまったく変わらなければ固着を疑う</summary>
    public static readonly TimeSpan StuckTime = TimeSpan.FromMinutes(3);
    /// <summary>固着を疑っている間に、使用率がこれ以上変化していたら固着と判定する</summary>
    public const double StuckLoadSwing = 40;

    private sealed class State
    {
        public double? Accepted;
        public double? Pending;
        public int PendingCount;
        /// <summary>今の大きな変化が最初に来た時刻</summary>
        public DateTime PendingSince;
        public double LastRaw = double.NaN;
        public DateTime UnchangedSince;
        public double LoadMin = double.MaxValue;
        public double LoadMax = double.MinValue;
        public bool Stuck;
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// この温度（°C）以上では「固着」と判定しない。FanEngine が「緊急温度 − 15°C」を設定する。
    /// すでに固着と判定したセンサーでも、値がこの温度以上なら判定を取り消して値を使う
    /// </summary>
    public double NoStuckAbove { get; set; } = double.MaxValue;

    /// <summary>固着と判定したセンサーの ID → 理由</summary>
    public IReadOnlyDictionary<string, string> Suspects => _suspects;
    private readonly Dictionary<string, string> _suspects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>新しく固着と判定された（画面・通知用。呼び出し側で 1 回だけ使う）</summary>
    public event Action<string, string>? StuckDetected;
    /// <summary>固着が解消した</summary>
    public event Action<string>? StuckCleared;

    /// <summary>
    /// 生の値を受け取り、制御に使ってよい値を返す。null は「使えない」（呼び出し側で異常扱い）。
    /// </summary>
    /// <param name="load">同じ部品の使用率（%）。分からなければ null（固着の判定をしない）</param>
    public double? Filter(string id, double? raw, double? load, DateTime now)
    {
        if (!_states.TryGetValue(id, out var s))
        {
            s = new State { UnchangedSince = now };
            _states[id] = s;
        }
        if (raw is null)
        {
            s.Pending = null;
            s.PendingCount = 0;
            return null;
        }
        double v = raw.Value;

        // --- 固着
        if (v != s.LastRaw)
        {
            s.LastRaw = v;
            s.UnchangedSince = now;
            s.LoadMin = double.MaxValue;
            s.LoadMax = double.MinValue;
            if (s.Stuck)
            {
                s.Stuck = false;
                _suspects.Remove(id);
                StuckCleared?.Invoke(id);
            }
        }
        else if (load is double l)
        {
            s.LoadMin = Math.Min(s.LoadMin, l);
            s.LoadMax = Math.Max(s.LoadMax, l);
            if (!s.Stuck && v < NoStuckAbove && now - s.UnchangedSince >= StuckTime && s.LoadMax - s.LoadMin >= StuckLoadSwing)
            {
                s.Stuck = true;
                string reason = L($"使用率が {s.LoadMin:F0}〜{s.LoadMax:F0}% と変わっているのに、{(now - s.UnchangedSince).TotalMinutes:F0} 分以上 {v:F1}°C のまま変化していません");
                _suspects[id] = reason;
                StuckDetected?.Invoke(id, reason);
            }
        }
        if (s.Stuck && v >= NoStuckAbove)
        {
            // 高い温度で止まっているセンサーは、異常として捨てずに使う（安全側＝ファンを回す側に倒す）
            s.Stuck = false;
            _suspects.Remove(id);
            StuckCleared?.Invoke(id);
        }
        if (s.Stuck) return null;

        // --- スパイク
        if (s.Accepted is null || Math.Abs(v - s.Accepted.Value) < SpikeThreshold)
        {
            s.Accepted = v;
            s.Pending = null;
            s.PendingCount = 0;
            return v;
        }
        // 大きく変わった値は、同じくらいの値がもう一度来たら本物とみなす。
        // 急上昇中で毎回値が違っても、3 回続けて離れていれば本物として受け入れる。
        // どちらも回数に加えて時間（ConfirmSeconds・ForceAcceptSeconds）も待つので、更新間隔を縮めても無視できる跳ねの長さは同じ
        bool similar = s.Pending is double p && Math.Abs(v - p) < SpikeThreshold / 2;
        if (s.PendingCount == 0) s.PendingSince = now;
        s.PendingCount++;
        s.Pending = v;
        double waited = (now - s.PendingSince).TotalSeconds;
        if ((similar && s.PendingCount >= 2 && waited >= ConfirmSeconds) || (s.PendingCount >= 3 && waited >= ForceAcceptSeconds))
        {
            s.Accepted = v;
            s.Pending = null;
            s.PendingCount = 0;
            return v;
        }
        return s.Accepted;
    }

    /// <summary>固着と判定しているセンサーか</summary>
    public bool IsStuck(string id) => _suspects.ContainsKey(id);

    /// <summary>設定から外れたセンサーの状態を捨てる</summary>
    public void Retain(IEnumerable<string> ids)
    {
        var keep = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _states.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _states.Remove(id);
            _suspects.Remove(id);
        }
    }

    public void Reset()
    {
        _states.Clear();
        _suspects.Clear();
    }
}

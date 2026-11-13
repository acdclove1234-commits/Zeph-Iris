// ==================================================
// UpdateConfirm.cs - 更新した版が「ちゃんと動いている」と言えるか（続けて制御しているか）を数える
//   最初のスナップショットだけで成功にすると、起動の直後だけ動いてすぐ落ちる版を、成功として見逃してしまう
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// エンジンのスナップショット（制御の 1 周ごと。正常でも、一部の部品が読めない「Degraded」でも来る）が、
/// 途切れずに Required のあいだ続いたら、更新の見守りに知らせてよい。
/// MaxGap より長く途切れたら（エンジンが止まった・開き直している・スリープ）、数え直す。
/// 知らせる印を書けなかったら、RetryEvery ごとにやり直す
/// </summary>
public sealed class UpdateConfirm
{
    public TimeSpan Required { get; init; } = UpdateRecovery.ConfirmAfter;
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(10);

    private long? _aliveSince, _lastSnapshot, _lastTry;
    private bool _done;

    /// <summary>スナップショットが来るたびに呼ぶ。いま印を書きにいってよいなら true（そのあと Result を呼ぶ）</summary>
    public bool ShouldTry(long nowMs)
    {
        if (_done) return false;
        if (_lastSnapshot is long last && nowMs - last > MaxGap.TotalMilliseconds) _aliveSince = null;
        _aliveSince ??= nowMs;
        _lastSnapshot = nowMs;
        if (nowMs - _aliveSince.Value < Required.TotalMilliseconds) return false;
        if (_lastTry is long tried && nowMs - tried < RetryEvery.TotalMilliseconds) return false;
        _lastTry = nowMs;
        return true;
    }

    /// <summary>印を書けた（もう書かなくてよい）なら true</summary>
    public void Result(bool done) => _done |= done;

    public bool Done => _done;
}

// ==================================================
// TalkFatigue.cs - 話し声が続くとき、テンポをつかめていないキックの光を少しずつ弱くする（言葉ごとにちかちか光り続けない）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 話し声の疲れ: テンポをつかめていないときのキックの光（「ビートで色替え」など）は、話し声の言葉の出だし・破裂音でも光っていた
/// （せりふ・ニュース・言い争いで 1 秒に 1 回以上の強い光）。そこで、話し声らしい音が続くときだけ、その光を弱くしていく:
///   話し声らしい = その音の前 WindowSec 秒のスネア（話し声の言葉の出だしはスネアに見える）が SnareCount 個以上で、直前 RecentSec 秒にもある
///     （ドラムのない曲・ピアノ・足音・銃声・曲のスネアはこれより少ない。ドラムの曲はテンポをつかむか、拍の見当にそろうので弱めない）
///   音のある時間 = テンポをつかめていない・音のある区間の始まりからの秒数（RmsDb が QuietDb より小さいのが QuietSec 秒続くか、
///     テンポをつかんだら数え直す）
///   倍率 = 話し声らしく、音のある時間が StartSec 秒を過ぎたら、RampSec 秒かけて 1 から Floor まで下げる（話し声らしくなくなったらすぐ 1）
/// 前の光よりはっきり大きい音（FlashLimiter.RaiseDb 以上）は弱めない（言い争いの中のドアの音など）。呼ぶ側が見る
/// </summary>
public sealed class TalkFatigue
{
    /// <summary>
    /// 話し声らしさを見る時間（秒）と、話し声らしいとみなすスネアの数（この時間の中で。話し声は 1 秒に 2 回以上。
    /// 曲のスネアは 2・4 拍目で 1 秒に 1.5 回まで、ピアノは 1 回くらい。短く見るので、話し声が終わればすぐ弱めなくなる）
    /// </summary>
    public const double WindowSec = 2.0;
    public const int SnareCount = 3;
    /// <summary>
    /// その音の直前 RecentSec 秒にもスネアがある（話している途中。話し声が終わった直後のピアノの音は弱めない）。
    /// その音自身のスネア（OwnSec 秒以内）は数えない
    /// </summary>
    public const double RecentSec = 0.4, OwnSec = 0.05;
    /// <summary>音のある時間がこの秒数を過ぎたら弱め始め、RampSec 秒で Floor にする</summary>
    public const double StartSec = 4.0, RampSec = 2.0;
    /// <summary>いちばん弱めたときの倍率</summary>
    public const float Floor = 0.2f;
    /// <summary>これより小さい音（dBFS）が QuietSec 秒続いたら、音のある区間を数え直す</summary>
    public const float QuietDb = -50f;
    public const double QuietSec = 1.5;

    private readonly double[] _snareT = new double[24];
    private int _head, _count;
    private double _busyT = double.NaN;               // 音のある区間の始まり（NaN = まだ始まっていない）
    private double _quietFrom = double.NaN;           // 小さい音が始まった時刻
    private double _lastT = double.NegativeInfinity;  // 前のコマの時刻

    public void Reset()
    {
        _head = _count = 0;
        _busyT = _quietFrom = double.NaN;
        _lastT = double.NegativeInfinity;
    }

    /// <summary>1 コマ分を進める（このコマの出来事を数える）。locked = テンポをつかんでいる</summary>
    public void Update(EffectContext c, bool locked)
    {
        double t = c.T;
        // 時刻が戻った・長く止まっていた: 数え直す
        if (t < _lastT - 1e-6 || t - _lastT > 1.0) Reset();
        _lastT = t;
        foreach (var ev in c.Events)
        {
            if (ev.Kind == AudioEventKind.Snare && ev.Strength >= BeatDriver.FlashMinStrength)
            {
                _snareT[_head] = ev.T;
                _head = (_head + 1) % _snareT.Length;
                if (_count < _snareT.Length) _count++;
            }
            if (!locked && double.IsNaN(_busyT) && ev.Kind is AudioEventKind.Kick or AudioEventKind.Snare or AudioEventKind.Onset) _busyT = ev.T;
        }
        if (locked) { _busyT = double.NaN; return; }
        if (c.Frame.RmsDb < QuietDb)
        {
            if (double.IsNaN(_quietFrom)) _quietFrom = t;
            else if (t - _quietFrom >= QuietSec) _busyT = double.NaN;
        }
        else _quietFrom = double.NaN;
    }

    /// <summary>時刻 t の音は話し声らしい（その音の前 WindowSec 秒のスネアが SnareCount 個以上で、直前 RecentSec 秒にもある）</summary>
    public bool Talky(double t)
    {
        int n = 0;
        double last = double.NegativeInfinity;
        for (int i = 0; i < _count; i++)
        {
            double s = _snareT[i];
            if (s <= t - WindowSec || s > t - OwnSec) continue;
            n++;
            last = Math.Max(last, s);
        }
        return n >= SnareCount && t - last <= RecentSec;
    }

    /// <summary>時刻 t のキックの光の倍率（1 = 弱めない）。fit = テンポをつかんでいるか、キックが拍の見当にそろっている</summary>
    public float Scale(double t, bool fit)
    {
        if (fit || double.IsNaN(_busyT) || !Talky(t)) return 1f;
        double age = t - _busyT - StartSec;
        if (age <= 0) return 1f;
        return 1f - (1f - Floor) * (float)Math.Min(1, age / RampSec);
    }
}

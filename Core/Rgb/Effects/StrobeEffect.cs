// ==================================================
// StrobeEffect.cs - 光り方 E11「ストロボ」 (小節の頭のビートで白く強く点滅する。「激しい」かつ点滅の制限がオフのときだけ)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// ストロボ: 光の形 = 40 ミリ秒だけ 1、その後 exp(−経過/30 ミリ秒) で消える（白）。経過は、その光を最初に描いたコマから数える
/// （60 コマ／秒なら、どの光も全開 3 コマ）。
///   テンポをつかんでいれば小節の頭（1 拍目）の予測ビートで光る（時刻から計算するので遅れない）
///   つかんでいなければ、強いキック（強さ 2 以上）で光る
/// 光過敏への配慮のため、「激しい点滅を抑える」がオンのとき・更新の遅いデバイスでは使わない（「ビートで色替え」になる）
/// </summary>
public sealed class StrobeEffect : IMusicEffect
{
    /// <summary>全開で光らせる秒数</summary>
    public const double HoldSec = 0.04;
    /// <summary>その後消えていく時定数（秒）</summary>
    public const double FadeTauSec = 0.03;
    /// <summary>テンポがないとき、光らせるキックの強さの下限</summary>
    public const float MinKickStrength = 2f;

    /// <summary>光の始まりがこれより後ろへずれたら、新しい光とみなす（秒。予測した小節の頭が少し動いても、同じ光のまま）</summary>
    public const double NewFlashSec = 0.2;

    private double _lastKickT = double.NegativeInfinity;
    private float _i;
    private LinColor _floor;
    // いまの光の始まりの時刻と、その光を最初に描いたコマの時刻
    private double _flashT = double.NegativeInfinity, _shownT = double.NegativeInfinity;

    public string Name => MusicStyles.Strobe;

    public void Reset(EffectContext c)
    {
        _lastKickT = double.NegativeInfinity;
        _flashT = _shownT = double.NegativeInfinity;
    }

    public void Update(EffectContext c)
    {
        var ev = c.Events;
        for (int i = 0; i < ev.Count; i++)
            if (ev[i].Kind == AudioEventKind.Kick && ev[i].Strength >= MinKickStrength) _lastKickT = Math.Max(_lastKickT, ev[i].T);

        double age;
        if (c.Locked)
        {
            // いちばん近い過去の小節の頭からの秒数 = いまの拍の頭からの秒数 + 小節の中の拍 × 1 拍
            age = c.Phase * c.Period + c.Beat.Bar(c.T) * c.Period;
        }
        else age = c.T - _lastKickT;
        // 光の長さは、その光を最初に描いたコマから数える（光の始まりの時刻から数えると、コマの位置によって
        // 全開のコマが 2 コマになったり 3 コマになったりする。検出が遅れて届いたキックでも、同じ長さで光る）
        double start = c.T - age;
        if (age >= 0 && start > _flashT + NewFlashSec)
        {
            _flashT = start;
            _shownT = c.T;
        }
        _i = age >= 0 ? Shape(c.T - _shownT) : 0;
        _floor = c.ColorAt(0) * c.F0;
    }

    /// <summary>光ってからの秒数 age の明るさ（0〜1）</summary>
    public static float Shape(double age)
    {
        if (!(age >= 0)) return 0;
        if (age < HoldSec) return 1;
        return (float)Math.Exp(-(age - HoldSec) / FadeTauSec);
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        var col = (_floor + LinColor.White * _i).Clamp01();
        for (int j = s.Start; j < s.Start + s.Count; j++) dst[j] = col;
    }
}

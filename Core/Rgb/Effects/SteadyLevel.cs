// ==================================================
// SteadyLevel.cs - ずっと続く音（エンジン・風・雨のゴーッという音）の小さな上下で、光が揺れないようにする
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// ずっと続く音の小さな上下をならす: 低音などの値（0〜1）を、ゆっくり追う値（体）のまわり ±m では体の値にする。
/// 自動の音量調整で大きく見える雑音（エンジン・風・雨）が、小さいゾーンで 1 秒に何回も光る「ちらつき」になっていた。
///   体 = 上がるとき・下がるとき 0.25 秒で追う
///   x が 体 + m より大きい: 体 + (x − 体 − m) × (1 − 体) / (1 − 体 − m)   … 強い音はそのまま 1 まで届く（遅れない）
///   x が 体 − m より小さい: x + m
///   それ以外: 体
/// ならすのは、キック・スネア・立ち上がり（強さ 1.0 以上）が 0.6 秒以上ないときだけ（音楽のキックの光は今のまま）。
/// 0.6 秒までは 0、1.5 秒で m になる（暗くなっていく途中は広げない）
/// </summary>
public struct SteadyLevel
{
    public const double BodySec = 0.25;
    /// <summary>ならし始めるまでの、打つ音がない時間（秒）。この間に少しずつ m まで広げる</summary>
    public const double SinceHitFromSec = 0.6, SinceHitToSec = 1.5;
    /// <summary>この強さ以上のキック・スネア・立ち上がりを「打つ音」とする（曲の弱いキックでも、ならすのをやめる）</summary>
    public const float HitStrength = 1.0f;
    /// <summary>低音・帯の強さに使う幅</summary>
    public const float BandDead = 0.25f;
    /// <summary>音量に使う幅（音量メーターは、スネアにも合わせて弾ませたいので狭くする）</summary>
    public const float LoudDead = 0.1f;

    private float _body, _dead;
    private double _hitT;
    private bool _has;

    public void Reset()
    {
        _body = _dead = 0;
        _hitT = 0;
        _has = false;
        Quiet = 0;
    }

    /// <summary>打つ音がしばらくない度合い（0〜1。最後の Step で求めた値。0.6 秒までは 0、1.5 秒で 1）</summary>
    public float Quiet { get; private set; }

    /// <summary>このコマの値 x（0〜1）をならす。m はならす幅。minDead は、打つ音があっても使う幅（ふだんは 0）</summary>
    public float Step(EffectContext c, float x, float m, float minDead = 0)
    {
        var ev = c.Events;
        for (int i = 0; i < ev.Count; i++)
        {
            var e = ev[i];
            if (e.Kind is AudioEventKind.Kick or AudioEventKind.Snare or AudioEventKind.Onset && e.Strength >= HitStrength && e.T > _hitT) _hitT = e.T;
        }
        double q = Math.Clamp((c.T - _hitT - SinceHitFromSec) / (SinceHitToSec - SinceHitFromSec), 0, 1);
        Quiet = (float)(q * q * (3 - 2 * q));
        float dead = Math.Max(m * Quiet, minDead);
        _body = _has ? EffectContext.Follow(_body, x, c.Dt, BodySec, BodySec) : x;
        _has = true;
        float b = _body;
        // 暗くなっていく途中（体より下）では幅を広げない（広げると、暗くなる途中の光が少し明るく戻って見える）
        if (dead > _dead && x < b) dead = _dead;
        _dead = dead;
        if (dead <= 0) return x;
        if (x > b + dead) return b + (x - b - dead) * (1 - b) / Math.Max(1e-3f, 1 - b - dead);
        if (x < b - dead) return x + dead;
        return b;
    }
}

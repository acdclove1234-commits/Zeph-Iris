// ==================================================
// KickBounce.cs - キックの弾み (届いたキックを本当の時刻で覚え、その時刻にぱっと上がって τ で戻る値を返す。スペクトラムの低音の端・ミックス用)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// キックの弾み: 弾み(t) = max over キック i（tᵢ ≤ t）of vᵢ × exp(−(t − tᵢ) / τ)
///   vᵢ = キックの強さ（MeterEffect.BounceOf と同じ 0.6〜1）× 出来事の光の倍率（EffectContext.LoudRel）
/// キックの検出（2〜8 ミリ秒）は、FFT の低音の帯（キックの音の高さが下がりきってから上がる。光が 0.1〜0.17 秒遅れていた）より、ずっと早い。
/// 話し声は打つ音の確かめ（検出の側）で外れるので、キックにならない
/// </summary>
public sealed class KickBounce
{
    /// <summary>これより弱いキックでは弾ませない（音量メーター・波紋と同じ）</summary>
    public const float MinStrength = MeterEffect.MinStrength;
    /// <summary>覚えておく秒数</summary>
    public const double KeepSec = 2;

    // 届いたキック（本当の時刻・弾みの高さ）
    private readonly List<(double T, float V)> _kicks = new();

    /// <summary>いちばん新しいキックの時刻（なければ −∞）</summary>
    public double LastT => _kicks.Count > 0 ? _kicks[^1].T : double.NegativeInfinity;

    public void Reset() => _kicks.Clear();

    /// <summary>このコマに届いたキックを覚え、古いものを捨てる（1 コマに 1 回）</summary>
    public void Take(EffectContext c, bool weighBody = false)
    {
        var events = c.Events;
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            if (ev.Kind != AudioEventKind.Kick || ev.Strength < MinStrength) continue;
            float weight = 1f;
            if (weighBody && float.IsFinite(ev.BodyDb) && float.IsFinite(ev.RiseDb))
                weight = Math.Clamp(Math.Max((ev.BodyDb + 5f) / 5f, (ev.RiseDb - 5f) / 6f), 0f, 1f);
            if (weight > 0) _kicks.Add((ev.T, weight * MeterEffect.BounceOf(ev) * c.LoudRel(ev.T)));
        }
        while (_kicks.Count > 0 && _kicks[0].T < c.T - KeepSec) _kicks.RemoveAt(0);
    }

    /// <summary>時刻 t の弾み（τ = tau 秒で戻る）</summary>
    public float At(double t, double tau)
    {
        float b = 0;
        foreach (var (te, v) in _kicks)
            if (te <= t) b = Math.Max(b, v * (float)Math.Exp(-(t - te) / tau));
        return b;
    }

    /// <summary>時刻 t 以前のいちばん新しいキックの時刻（なければ −∞）</summary>
    public double LastAt(double t)
    {
        for (int i = _kicks.Count - 1; i >= 0; i--)
            if (_kicks[i].T <= t) return _kicks[i].T;
        return double.NegativeInfinity;
    }

    /// <summary>from より後・to 以前のキックのうち、いちばん高い弾みとその時刻（なければ (0, −∞)）</summary>
    public (float V, double T) PeakIn(double from, double to)
    {
        float bp = 0;
        double bt = double.NegativeInfinity;
        foreach (var (te, v) in _kicks)
            if (te > from && te <= to && v >= bp) { bp = v; bt = te; }
        return (bp, bt);
    }
}

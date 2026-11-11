// ==================================================
// RippleEffect.cs - 光り方 E7「波紋」 (キックのたびに、ゾーンの中央から光の輪が広がる)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 波紋: キック（強さ 1.2 以上）か、テンポをつかんでいれば予測したビートで、光の輪を 1 つ出す（最大 6 つ）。
///   輪の生まれた時刻 t0 = キックが鳴った本当の時刻。強さ A = 0.5 + 0.25 × (強さ − 1)（0.5〜1。予測ビートは 0.9）
///   中央からの距離 d = |p − 0.5|（輪になったゾーンなら近いほうの回り方）
///   半径 r = 速さ × (t − t0)、速さ = テンポをつかんでいれば 0.5 / (0.75 拍)、つかんでいなければ 1.2 / 秒
///   太さ w = max(0.05, 1.5 / LED の数)
///   輪の明るさ R = A × exp(−((d − r)/w)²) × exp(−(t − t0)/0.35 秒)
///   LED の色 = 輪の色 × R（輪が重なったら明るいほう）+ いまの色 × Ilin(0.15 × 低音)
/// 輪は生まれた時刻から計算するので、1 秒間のコマ数が違っても同じ速さで広がる。6 個未満のゾーンはパルス
/// </summary>
public sealed class RippleEffect : IMusicEffect
{
    /// <summary>同時に出せる輪の数</summary>
    public const int MaxRings = 6;
    /// <summary>輪を出すキックの強さの下限</summary>
    public const float MinStrength = 1.2f;
    /// <summary>輪が消えていく時定数（秒）</summary>
    public const double FadeTauSec = 0.35;
    /// <summary>テンポがないときの輪の広がる速さ（ゾーンの長さ / 秒）</summary>
    public const double FreeSpeed = 1.2;
    /// <summary>予測ビートで出す輪の強さ</summary>
    public const float PredictedAmp = 0.9f;
    /// <summary>静かな部分での輪の明るさの倍率（盛り上がりで 1。EffectContext.SectionGain）</summary>
    public const float SectionMin = 0.6f;
    /// <summary>下地（いまの色）の明るさ（× 低音）</summary>
    public const float BaseShare = 0.15f;
    /// <summary>輪の中心（ゾーンの中央）</summary>
    public const float Center = 0.5f;

    private struct Ring
    {
        public bool Active;
        public double T0;
        public float A;
        public LinColor Color;
    }

    private readonly Ring[] _rings = new Ring[MaxRings];
    private readonly PulseEffect _small = new();
    private double _ringColorPos;
    private double _speed = FreeSpeed;
    private LinColor _base;
    private int _spawned;

    public string Name => MusicStyles.Ripple;

    /// <summary>出した輪の数（テスト用）</summary>
    public int Spawned => _spawned;

    public void Reset(EffectContext c)
    {
        Array.Clear(_rings);
        _ringColorPos = c.Color.Target;
        _small.Reset(c);
    }

    /// <summary>輪の生まれた時刻 t0 から、時刻 t の半径（テスト用にも使う）</summary>
    public double RadiusAt(double t0, double t) => _speed * Math.Max(0, t - t0);

    public void Update(EffectContext c)
    {
        _speed = c.Locked ? 0.5 / (0.75 * c.Period) : FreeSpeed;
        if (c.Locked)
        {
            if (c.BeatCrossed) Spawn(c, c.BeatCrossT, PredictedAmp);
        }
        else
        {
            var ev = c.Events;
            for (int i = 0; i < ev.Count; i++)
            {
                if (ev[i].Kind != AudioEventKind.Kick || ev[i].Strength < MinStrength) continue;
                Spawn(c, ev[i].T, Math.Clamp(0.5f + 0.25f * (ev[i].Strength - 1), 0.5f, 1f));
            }
        }

        // 広がりきって暗くなった輪を消す
        for (int k = 0; k < MaxRings; k++)
        {
            ref var r = ref _rings[k];
            if (!r.Active) continue;
            double age = c.T - r.T0;
            double amp = r.A * Math.Exp(-Math.Max(0, age) / FadeTauSec);
            if ((RadiusAt(r.T0, c.T) > 0.6 && amp < 0.02) || age > 4) r.Active = false;
        }

        _base = c.ColorAt(0) * c.Ilin(BaseShare * c.Frame.BassE);
        if (c.HasSmallSegment) _small.Update(c);
    }

    private void Spawn(EffectContext c, double t0, float amp)
    {
        // 空いている場所、なければいちばん古い輪を置き換える
        int slot = 0;
        double oldest = double.PositiveInfinity;
        for (int k = 0; k < MaxRings; k++)
        {
            if (!_rings[k].Active) { slot = k; oldest = double.NegativeInfinity; break; }
            if (_rings[k].T0 < oldest) { oldest = _rings[k].T0; slot = k; }
        }
        _ringColorPos += c.Pal.Step;   // 輪ごとにパレットの次の色
        _rings[slot] = new Ring { Active = true, T0 = t0, A = amp * c.SectionGain(SectionMin), Color = c.PalAt(_ringColorPos) };
        _spawned++;
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        if (s.Small) { _small.Render(c, s, dst); return; }
        // ケースの中の場所を決めたゾーンは、流れの始まり（位置 0）から輪が広がる（ケース全体で 1 つの輪）
        float w = Math.Max(0.05f, 1.5f / (s.Placed ? s.Ne : s.Count));
        double t = c.T;
        for (int j = s.Start; j < s.Start + s.Count; j++)
        {
            float d = s.Placed ? c.P[j] * 0.5f : Math.Abs(c.P[j] - Center);
            if (s.Ring) d = Math.Min(d, 1 - d);
            var col = LinColor.Black;
            for (int k = 0; k < MaxRings; k++)
            {
                ref readonly var r = ref _rings[k];
                if (!r.Active) continue;
                double age = t - r.T0;
                if (age < 0) continue;
                double x = (d - _speed * age) / w;
                float amp = (float)(r.A * Math.Exp(-x * x) * Math.Exp(-age / FadeTauSec));
                if (amp > 0.002f) col = LinColor.Max(col, r.Color * amp);
            }
            dst[j] = (col + _base).FitToOne();
        }
    }
}

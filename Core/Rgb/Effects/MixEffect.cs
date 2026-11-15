// ==================================================
// MixEffect.cs - 光り方 E12「ミックス」 (低音・中音・高音の強さで色が混ざる。RGB ではなく色相で混ぜるので濁らない)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// ミックス: デバイス全体が 1 色。低音 = パレットの最初の色、中音 = 真ん中の色、高音 = 最後の色として、
/// 強い帯の色に寄せる（重み w = e⁴ / Σe⁴ で、いちばん強い帯がはっきり勝つ）。
///   色相 h = atan2(Σ w·sin h_g, Σ w·cos h_g)（色相の平均。RGB の平均だと灰色になる）
///   明るさ L・鮮やかさ C も重みで平均（パレットの色の値のまま。灰色にならない）
///   I = max(e_低, e_中, e_高, Fl)
/// 音がまったくない（Σe⁴ ≈ 0）ときは、前の色のままにする（虹色に飛ばない）。
/// 遅いデバイス用（slow）では、各帯が暗くなるのを 0.3 秒かけてゆっくりにする
/// </summary>
public sealed class MixEffect : IMusicEffect
{
    /// <summary>遅いデバイスで、暗くなるのをゆっくりにする時定数（秒）</summary>
    public const double SlowReleaseSec = 0.30;

    private readonly bool _slow;
    private float _eB, _eM, _eT;
    private float _l, _c, _h;          // いまの色（OKLCh）
    private bool _hasColor;
    private ColorPalette? _bandPal;    // 帯の色を求めたパレット
    private ColorPalette? _bandFrom, _bandTo;   // 帯の色を求めたときの場面の色（PaletteView）
    private float _bandU;
    private readonly Lab[] _band = new Lab[3];
    private LinColor _color;
    private float _i;

    public MixEffect(bool slow = false) => _slow = slow;

    public string Name => MusicStyles.Mix;

    public void Reset(EffectContext c)
    {
        _eB = _eM = _eT = 0;
        _hasColor = false;
        _bandPal = _bandFrom = _bandTo = null;
    }

    public void Update(EffectContext c)
    {
        var f = c.Frame;
        if (_slow)
        {
            _eB = EffectContext.Follow(_eB, f.BassE, c.Dt, 0, SlowReleaseSec);
            _eM = EffectContext.Follow(_eM, f.MidE, c.Dt, 0, SlowReleaseSec);
            _eT = EffectContext.Follow(_eT, f.TrebleE, c.Dt, 0, SlowReleaseSec);
        }
        else
        {
            _eB = f.BassE;
            _eM = f.MidE;
            _eT = f.TrebleE;
        }

        var view = c.View;
        if (!ReferenceEquals(_bandPal, c.Pal) || !ReferenceEquals(_bandFrom, view.From) || !ReferenceEquals(_bandTo, view.To) || _bandU != view.U)
        {
            // 帯の色（パレットの最初・真ん中・最後）は、パレット・楽器の AI の場面の色（移り変わりの途中なら毎コマ）が変わったときだけ求め直す
            _bandPal = c.Pal;
            _bandFrom = view.From;
            _bandTo = view.To;
            _bandU = view.U;
            double span = c.Pal.Span;
            _band[0] = view.LabAt(0);
            _band[1] = view.LabAt(span * 0.5);
            _band[2] = view.LabAt(span);
            if (!_hasColor) { SetColor(_band[0]); }
        }

        float wB = Pow4(_eB), wM = Pow4(_eM), wT = Pow4(_eT);
        float sum = wB + wM + wT;
        if (sum > 1e-8f)
        {
            wB /= sum; wM /= sum; wT /= sum;
            float sin = wB * MathF.Sin(_band[0].Hue) + wM * MathF.Sin(_band[1].Hue) + wT * MathF.Sin(_band[2].Hue);
            float cos = wB * MathF.Cos(_band[0].Hue) + wM * MathF.Cos(_band[1].Hue) + wT * MathF.Cos(_band[2].Hue);
            _h = MathF.Atan2(sin, cos);
            _l = wB * _band[0].L + wM * _band[1].L + wT * _band[2].L;
            _c = wB * _band[0].Chroma + wM * _band[1].Chroma + wT * _band[2].Chroma;
            _hasColor = true;
        }
        _color = OkLab.ToLed(OkLab.FitGamut(Lab.FromLch(_l, _c, _h)), c.Gamma);
        _i = Math.Max(Math.Max(_eB, _eM), Math.Max(_eT, c.Fl));
    }

    private void SetColor(Lab lab)
    {
        _l = lab.L;
        _c = lab.Chroma;
        _h = lab.Hue;
        _hasColor = true;
    }

    private static float Pow4(float e)
    {
        float x = Math.Clamp(e, 0f, 1f);
        x *= x;
        return x * x;
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        var col = _color * c.Ilin(_i);
        for (int j = s.Start; j < s.Start + s.Count; j++) dst[j] = col;
    }
}

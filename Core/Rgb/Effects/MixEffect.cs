// ==================================================
// MixEffect.cs - 光り方 E12「ミックス」 (低音・中音・高音の強さで色が混ざる。RGB ではなく色相で混ぜるので濁らない)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// ミックス: デバイス全体が 1 色。低音 = パレットの最初の色、中音 = 真ん中の色、高音 = 最後の色として、
/// 強い帯の色に寄せる（重み w = e⁴ / Σe⁴ で、いちばん強い帯がはっきり勝つ）。
///   重みは、上がるとき WeightRiseSec・下がるとき WeightFallSec の時定数でならす（1〜2 コマだけ別の帯が勝って、
///   色が一瞬だけ飛んで戻る＝色替えの途中でちかっと光るのを防ぐ）
///   色 = OKLab の (L, a, b) を重みで平均（色相の角度の平均だと、離れた 2 色の間で、どちらでもない鮮やかな色を通って
///   一瞬だけ違う色が光っていた）。鮮やかさだけは、重みで平均した鮮やかさ × √(まとまり具合) に戻す（灰色にしすぎない）
///   I = max(e_低, e_中, e_高, Fl)
/// 音がまったくない（Σe⁴ ≈ 0）ときは、前の色のままにする（虹色に飛ばない）。
/// 遅いデバイス用（slow）では、各帯が暗くなるのを 0.3 秒かけてゆっくりにする
/// </summary>
public sealed class MixEffect : IMusicEffect
{
    /// <summary>遅いデバイスで、暗くなるのをゆっくりにする時定数（秒）</summary>
    public const double SlowReleaseSec = 0.30;
    /// <summary>帯の重みをならす時定数（秒。上がるとき・下がるとき）</summary>
    public const double WeightRiseSec = 0.06, WeightFallSec = 0.15;

    private readonly bool _slow;
    private float _eB, _eM, _eT;
    private float _wB, _wM, _wT;       // ならした帯の重み
    private bool _hasW;
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

    /// <summary>重みをならし、OKLab で混ぜる（調べる用に、テストから前と比べるために変えられる。アプリの中では変えない。false なら 6.15.8 までの色相の平均）</summary>
    internal static bool Smooth { get; set; } = true;

    public void Reset(EffectContext c)
    {
        _eB = _eM = _eT = 0;
        _wB = _wM = _wT = 0;
        _hasW = false;
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
            if (!Smooth)
            {
                float sin = wB * MathF.Sin(_band[0].Hue) + wM * MathF.Sin(_band[1].Hue) + wT * MathF.Sin(_band[2].Hue);
                float cos = wB * MathF.Cos(_band[0].Hue) + wM * MathF.Cos(_band[1].Hue) + wT * MathF.Cos(_band[2].Hue);
                _h = MathF.Atan2(sin, cos);
                _l = wB * _band[0].L + wM * _band[1].L + wT * _band[2].L;
                _c = wB * _band[0].Chroma + wM * _band[1].Chroma + wT * _band[2].Chroma;
                _hasColor = true;
                _hasW = false;
            }
            else if (!_hasW) { _wB = wB; _wM = wM; _wT = wT; _hasW = true; }
            else
            {
                _wB = EffectContext.Follow(_wB, wB, c.Dt, WeightRiseSec, WeightFallSec);
                _wM = EffectContext.Follow(_wM, wM, c.Dt, WeightRiseSec, WeightFallSec);
                _wT = EffectContext.Follow(_wT, wT, c.Dt, WeightRiseSec, WeightFallSec);
            }
        }
        float ws = _wB + _wM + _wT;
        if (_hasW && ws > 1e-6f)
        {
            float iw = 1f / ws;
            float b0 = _wB * iw, b1 = _wM * iw, b2 = _wT * iw;
            float l = b0 * _band[0].L + b1 * _band[1].L + b2 * _band[2].L;
            float a = b0 * _band[0].A + b1 * _band[1].A + b2 * _band[2].A;
            float bb = b0 * _band[0].B + b1 * _band[1].B + b2 * _band[2].B;
            float meanC = b0 * _band[0].Chroma + b1 * _band[1].Chroma + b2 * _band[2].Chroma;
            float res = MathF.Sqrt(a * a + bb * bb);
            _l = l;
            if (res > 1e-5f)
            {
                _h = MathF.Atan2(bb, a);
                _c = meanC * MathF.Sqrt(Math.Clamp(res / Math.Max(meanC, 1e-6f), 0f, 1f));
            }
            else _c = 0;
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

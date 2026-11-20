// ==================================================
// MoodEffect.cs - 音楽連動で音が止まっているときの光り方「ゆっくり色が変わる」 (点滅しない)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// ムード: 点滅させず、曲の雰囲気でパレットの中の位置をゆっくり動かす。
/// 今は音楽連動で音が止まっているときの光り方（IdleEffects.Mood。idle）だけで使う（音楽連動の光り方「ムード」は、
/// 6.15.11 で音に反応しない光り方 RgbModes.Mood になった。下の音で動かす決め方は、idle でないときのもの）。
///   xm = 音の重心（低い音が中心か、明るい音か）を 150 Hz〜4 kHz で 0〜1 にしたもの（1.5 秒でならす）
///   x = lerp(0.5, xm, 音の確かさ) + 小節の数 / 見本の数（テンポをつかんでいれば 1 小節ごとに 0.6 秒かけて進む）
///   LED の色 = P(x + 0.12 × (p − 0.5) + 0.04 × sin(2πt / 20 秒)) × Ilin(0.35 + 0.65 × 音量（0.6 秒でならす）)
/// 「止まっているときの光り方」として使うとき（idle）は、明るさを 0.4 に固定し、1 分でパレットを 1 周させる
/// </summary>
public sealed class MoodEffect : IMusicEffect
{
    /// <summary>音の重心をならす時定数（秒）</summary>
    public const double CentroidTauSec = 1.5;
    /// <summary>音量をならす時定数（秒）</summary>
    public const double LoudTauSec = 0.6;
    /// <summary>小節ごとに色を進めるときのクロスフェード（秒）</summary>
    public const double BarFadeSec = 0.6;
    /// <summary>音の重心の範囲（Hz）。この範囲を 0〜1 にする</summary>
    public const double CentroidLowHz = 150, CentroidHighHz = 4000;
    /// <summary>止まっているときの明るさ</summary>
    public const float IdleLevel = 0.4f;
    /// <summary>止まっているとき、パレットを 1 周させる秒数</summary>
    public const double IdleCycleSec = 300;

    private readonly bool _idle;
    private float _xm = 0.5f;
    private float _loud;
    private double _barFrom, _barTo, _barT = double.NegativeInfinity;
    private double _x;
    private float _level;
    private bool _noFade;

    /// <param name="idle">音が止まっているときの光り方として使う（明るさ固定・ゆっくり 1 周）</param>
    public MoodEffect(bool idle = false) => _idle = idle;

    /// <summary>
    /// 止まっているときの光り方で、色を少しずつではなく、この秒数ごとに一度に進める（0 = なめらかに進める）。
    /// 書き込むたびに一瞬乱れるデバイス用。音の雰囲気では色を動かさない（無音のときのほかのデバイスと同じ色になる）
    /// </summary>
    public double IdleStepSec { get; init; }

    public void Reset(EffectContext c)
    {
        _xm = 0.5f;
        _loud = 0;
        _barFrom = _barTo = 0;
        _barT = double.NegativeInfinity;
    }

    public void Update(EffectContext c)
    {
        var f = c.Frame;
        double hz = Math.Max(1, f.CentroidHz);
        float xmRaw = (float)Math.Clamp((Math.Log2(hz) - Math.Log2(CentroidLowHz)) / (Math.Log2(CentroidHighHz) - Math.Log2(CentroidLowHz)), 0, 1);
        _xm = EffectContext.Follow(_xm, xmRaw, c.Dt, CentroidTauSec, CentroidTauSec);
        _loud = EffectContext.Follow(_loud, Math.Clamp(f.LoudVU, 0f, 1f), c.Dt, LoudTauSec, LoudTauSec);

        // テンポをつかんでいれば、小節の頭ごとに次の色へ（0.6 秒かけて。ちかちかしやすいデバイスは一度に切り替える）
        _noFade = c.NoFade;
        if (!_idle && c.Locked && c.BeatCrossed && c.Beat.Bar(c.BeatCrossT + 1e-6) == 0)
        {
            _barFrom = BarOffset(c.BeatCrossT);
            _barTo += c.Pal.Step;
            _barT = c.BeatCrossT;
        }

        // 音の確かさ（音量が小さいときは真ん中の色に寄せる）。ちらつかないよう、ならした音量を使う
        float confE = _loud / (_loud + 0.1f);
        // ゆっくりした揺れ（20 秒周期）は、止まっているときと、ちかちかしやすいデバイス（書き込むたびに乱れる）では付けない
        double x = (0.5 + (_xm - 0.5) * confE) * c.Pal.Span + BarOffset(c.T) + (_idle || _noFade ? 0 : 0.04 * Math.Sin(2 * Math.PI * c.T / 20));
        if (_idle) x += c.T / IdleCycleSec;
        // 段は、ほかのデバイスのなめらかな色の前後半分ずつ（切り捨てると、いつも少し遅れた色になる）
        if (_idle && IdleStepSec > 0) x = 0.5 * c.Pal.Span + Math.Floor(c.T / IdleStepSec + 0.5) * IdleStepSec / IdleCycleSec;
        _x = x;
        _level = _idle ? IdleLevel : 0.35f + 0.65f * _loud;
    }

    private double BarOffset(double t) => _noFade
        ? (t >= _barT ? _barTo : _barFrom)
        : _barFrom + (_barTo - _barFrom) * EffectContext.Smoothstep((t - _barT) / BarFadeSec);

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        float k = c.Ilin(_level);
        for (int j = s.Start; j < s.Start + s.Count; j++)
            dst[j] = c.PalAt(_x + 0.12 * (c.P[j] - 0.5)) * k;
    }
}

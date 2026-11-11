// ==================================================
// ChaseEffect.cs - 光り方 E9「テンポ追従」 (ビートに合わせて光の帯が「始まり」から端へ走る)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// テンポ追従（バーワイプ）: 拍の中の位置 φ から、帯の先端 xe = 1 − (1 − φ)²（最初は速く、拍の終わりでゆっくり）。
///   偶数拍: [0, xe) を点ける（帯が伸びる）。奇数拍: [xe, 1) を点ける（帯が端へ抜けていく）
///   色 = P(2 拍ごとに 1 つ進む)
///   輪になったゾーンは「彗星」: 先頭 h = (拍 + φ)/4（1 小節で 1 周）、明るさ = exp(−((h − p) mod 1)/0.12)
/// テンポをつかんでいないときは 120 BPM で自走し、明るさを 0.3 + 0.7 × 音量 にする。
/// 両者はテンポの確かさで混ぜる（確かさが上がるにつれてビートに合っていく）。6 個未満のゾーンは「ビートで色替え」
/// </summary>
public sealed class ChaseEffect : IMusicEffect
{
    /// <summary>テンポがないときに自走する 1 拍の長さ（秒）= 120 BPM</summary>
    public const double FreePeriodSec = 0.5;
    /// <summary>彗星の尾の長さ（1 周に対する割合）</summary>
    public const double CometTail = 0.12;
    /// <summary>拍の頭で帯を明るくする割合（ビートの光 × この割合。ほかの時間は 1 − この割合）</summary>
    public const float BeatAccent = 0.3f;
    /// <summary>静かな部分での帯の明るさの倍率（盛り上がりで 1。EffectContext.SectionGain）</summary>
    public const float SectionMin = 0.6f;

    private readonly BeatHueEffect _small = new();
    private LinColor _lockedColor, _freeColor;
    private double _lockedPhase, _freePhase;
    private long _lockedIdx, _freeIdx;
    private float _blend, _freeGain, _lockedGain = 1;

    public string Name => MusicStyles.Chase;

    public void Reset(EffectContext c) => _small.Reset(c);

    public void Update(EffectContext c)
    {
        double step = c.Pal.Step;
        _blend = c.HasTempo ? c.Conf : 0f;
        _lockedPhase = c.Phase;
        _lockedIdx = c.BeatIndex;
        _lockedColor = c.PalAt(FloorDiv(_lockedIdx, 2) * step);
        double tb = c.T / FreePeriodSec;
        _freeIdx = (long)Math.Floor(tb);
        _freePhase = tb - _freeIdx;
        _freeColor = c.PalAt(FloorDiv(_freeIdx, 2) * step);
        _freeGain = 0.3f + 0.7f * Math.Clamp(c.Frame.LoudVU, 0f, 1f);
        // テンポに合わせて走る帯も、拍の頭でぱっと明るく（ビートの光）、静かな部分では控えめに
        float fl = Math.Max(c.Fl, c.FlPeak * (float)Math.Exp(-Math.Max(0, c.T - c.FlPeakT) / 0.15));
        _lockedGain = c.SectionGain(SectionMin) * (1 - BeatAccent + BeatAccent * fl);
        if (c.HasSmallSegment) _small.Update(c);
    }

    private static long FloorDiv(long a, long b) => (long)Math.Floor(a / (double)b);

    /// <summary>
    /// 色の段の番号（テンポに合わせた段・自走の段。どちらかが進んだら、帯の色が一度に変わる）。
    /// 共有の色とは別に 2 拍ごとに進むので、グラボに色替えを届けるために使う
    /// </summary>
    public (long Locked, long Free) ColorStep => (FloorDiv(_lockedIdx, 2), FloorDiv(_freeIdx, 2));

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        if (s.Small) { _small.Render(c, s, dst); return; }
        float ne = s.Ne;
        for (int j = s.Start; j < s.Start + s.Count; j++)
        {
            float lockedLit = _blend > 0 ? Lit(c, s, j, _lockedPhase, _lockedIdx, ne) * _lockedGain : 0f;
            float freeLit = _blend < 1 ? Lit(c, s, j, _freePhase, _freeIdx, ne) * _freeGain : 0f;
            var a = _lockedColor * c.Ilin(lockedLit);
            var b = _freeColor * c.Ilin(freeLit);
            dst[j] = LinColor.Lerp(b, a, _blend);
        }
    }

    /// <summary>LED j がどれだけ点いているか（0〜1。帯の先端は端数の分だけ）</summary>
    private static float Lit(EffectContext c, in LedSegment s, int j, double phase, long idx, float ne)
    {
        if (s.Ring)
        {
            // 彗星: 1 小節で 1 周。先頭から後ろへ尾を引く
            double h = ((idx % 4 + 4) % 4 + phase) / 4;
            double d = h - c.P[j];
            d -= Math.Floor(d);
            return (float)Math.Exp(-d / CometTail);
        }
        double xe = 1 - (1 - phase) * (1 - phase);
        float cov = Math.Clamp((float)(xe * ne) - c.Jx[j], 0f, 1f);   // [0, xe) の中にどれだけ入っているか
        bool even = ((idx % 2) + 2) % 2 == 0;
        return even ? cov : 1 - cov;
    }
}

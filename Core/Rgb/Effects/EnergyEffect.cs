// ==================================================
// EnergyEffect.cs - 光り方 E4「エナジー」 (低音・中音・高音の帯が、それぞれの強さの分だけ「始まり」から伸びる)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// エナジー（LedFx 風）: 3 本の光の帯を重ねる。帯 g の長さ ℓ_g = e_g（低音・中音・高音の強さ。縮むときは時定数
/// LengthReleaseSec でゆっくり縮む。1〜3 コマだけ伸びて戻る帯の先が、色の違う LED を一瞬だけ光らせないように）。
///   coverage_g(j) = clamp(ℓ_g × Ne − jx, 0, 1)   … 帯の先端の LED は端数の分だけ光る（なめらかに伸び縮みする）
///   LED の色 = Σ coverage_g × 色_g + F0 × 低音の色。帯が重なったところは、いちばん届いている帯（max coverage_g・F0）より
///   明るくしない（色は混ぜる。重なりで足し合わせて明るくなると、帯の先が通るたびに、ちかっと光って見えるため）
/// 色は、いまの色（ビートで進む）からパレットの幅の 0・半分・全部ずらした 3 色。6 個未満のゾーンは「ミックス」で光る
/// </summary>
public sealed class EnergyEffect : IMusicEffect
{
    /// <summary>帯が縮むときの時定数（秒。伸びるときはすぐ）</summary>
    public const double LengthReleaseSec = 0.15;

    private readonly MixEffect _small = new();
    private readonly LinColor[] _col = new LinColor[3];
    private readonly float[] _len = new float[3];
    private double _lenT = double.NegativeInfinity;

    public string Name => MusicStyles.Energy;

    /// <summary>帯の重なりを、いちばん届いている帯の明るさまでにする・縮むのをゆっくりにする（調べる用に、テストから前と比べるために変えられる。アプリの中では変えない）</summary>
    internal static bool Smooth { get; set; } = true;

    public void Reset(EffectContext c)
    {
        _small.Reset(c);
        Array.Clear(_len);
        _lenT = double.NegativeInfinity;
    }

    public void Update(EffectContext c)
    {
        double half = c.Pal.Span * 0.5;
        for (int g = 0; g < 3; g++) _col[g] = c.ColorAt(g * half);
        if (Smooth)
        {
            // 帯の長さは、決まった時刻（2 ミリ秒ごと）の強さを順に追いかける（1 秒のコマ数で長さが変わらない）。
            // 伸びるのはすぐ、縮むのは LengthReleaseSec の時定数
            // 時刻が戻った（音の遅れの補正を変えた・作り直した）: 追いかけ直す
            if (c.LoudCount > 0 && c.LoudT[c.LoudCount - 1] < _lenT - 0.5) _lenT = double.NegativeInfinity;
            for (int i = 0; i < c.LoudCount; i++)
            {
                double ti = c.LoudT[i];
                if (ti <= _lenT) continue;
                float dt = (float)Math.Min(0.25, ti - _lenT);
                _lenT = ti;
                Track(0, c.BassV[i], dt);
                Track(1, c.MidV[i], dt);
                Track(2, c.TrebleV[i], dt);
            }
        }
        else
        {
            _len[0] = Math.Clamp(c.Frame.BassE, 0f, 1f);
            _len[1] = Math.Clamp(c.Frame.MidE, 0f, 1f);
            _len[2] = Math.Clamp(c.Frame.TrebleE, 0f, 1f);
        }
        if (c.HasSmallSegment) _small.Update(c);
    }

    private void Track(int g, float v, float dt) =>
        _len[g] = EffectContext.Follow(_len[g], Math.Clamp(v, 0f, 1f), dt, 0, LengthReleaseSec);

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        if (s.Small) { _small.Render(c, s, dst); return; }
        float ne = s.Ne;
        var floor = _col[0] * c.F0;
        bool cap = Smooth;
        for (int j = s.Start; j < s.Start + s.Count; j++)
        {
            float jx = c.Jx[j];
            var sum = floor;
            float top = c.F0;
            for (int g = 0; g < 3; g++)
            {
                float cov = Math.Clamp(_len[g] * ne - jx, 0f, 1f);
                if (cov <= 0) continue;
                sum += _col[g] * cov;
                top = Math.Max(top, cov);
            }
            if (cap)
            {
                float m = sum.MaxChannel;
                if (m > top && m > 1e-6f) sum *= top / m;
            }
            dst[j] = sum.FitToOne();
        }
    }
}

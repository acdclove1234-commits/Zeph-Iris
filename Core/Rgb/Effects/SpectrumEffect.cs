// ==================================================
// SpectrumEffect.cs - 光り方 E3「スペクトラム」 (LED の並びに低音〜高音の 10 帯（31 Hz〜16 kHz）を並べ、それぞれの強さで光る)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// スペクトラム: 「始まり」側が低音、端が高音。LED の位置 x（0〜1）の強さは 10 帯の間を直線で補う:
///   B(x) = lerp(e_k, e_k+1, u − k)、u = x × 9、k = ⌊u⌋（e = 立ち上がりを強調した帯の強さ Punch）
///   ピークの保持: h = 最大値を 0.4 秒保ち、その後 1 秒に 1.5 ずつ下げる（ゴーストとして 0.35 倍で見せる）
///   LED の色 = P(x + 少しずつ回る量) × Ilin(max(B(x), 0.35 × h))
/// 24 個以上のゾーンは、隣どうしを [0.25, 0.5, 0.25] で 1 回ぼかす（帯の境目のカクカクを消す）。
/// 30 個以上のゾーンは自動で左右対称（中央が低音）。6 個未満のゾーンは「ミックス」で光る
/// </summary>
public sealed class SpectrumEffect : IMusicEffect
{
    /// <summary>ピークを保つ秒数</summary>
    public const double HoldSec = 0.4;
    /// <summary>保った後にピークが下がる速さ（1 秒あたり）</summary>
    public const double FallPerSec = 1.5;
    /// <summary>ピークのゴーストの明るさ</summary>
    public const float GhostShare = 0.35f;
    /// <summary>この数以上の LED のゾーンはぼかす</summary>
    public const int BlurMinLeds = 24;
    /// <summary>合図 1 回あたり、色を回す量（パレットの位置）</summary>
    public const double RotatePerTrigger = 0.02;
    /// <summary>色を回すときの滑らかさ（秒）</summary>
    public const double RotateTauSec = 0.3;

    private readonly MixEffect _small = new();
    private float[] _hPeak = Array.Empty<float>();
    private double[] _tPeak = Array.Empty<double>();
    private float[] _v = Array.Empty<float>();
    private float[] _tmp = Array.Empty<float>();
    private double _rot;

    public string Name => MusicStyles.Spectrum;

    public void Reset(EffectContext c)
    {
        int n = c.LedCount;
        if (_hPeak.Length != n)
        {
            _hPeak = new float[n];
            _tPeak = new double[n];
            _v = new float[n];
            _tmp = new float[n];
        }
        Array.Clear(_hPeak);
        Array.Fill(_tPeak, double.NegativeInfinity);
        _rot = RotatePerTrigger * c.TriggerCount;
        _small.Reset(c);
    }

    public void Update(EffectContext c)
    {
        double target = RotatePerTrigger * c.TriggerCount;
        _rot += (target - _rot) * (1 - Math.Exp(-c.Dt / RotateTauSec));
        if (c.HasSmallSegment) _small.Update(c);
    }

    /// <summary>位置 x（0〜1）の帯の強さ。band は 10 帯（AudioFrame.BandCount）</summary>
    public static float BandCurve(float[] band, float x)
    {
        float u = Math.Clamp(x, 0f, 1f) * (AudioFrame.BandCount - 1);
        int k = Math.Min((int)u, AudioFrame.BandCount - 2);
        float f = u - k;
        return band[k] + (band[k + 1] - band[k]) * f;
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        if (s.Small) { _small.Render(c, s, dst); return; }
        int a = s.Start, end = s.Start + s.Count;
        double t = c.T;
        for (int j = a; j < end; j++)
        {
            float x = c.X[j];
            float now = BandCurve(c.Frame.Punch, x);
            float pk = Math.Max(now, BandCurve(c.Peak.Punch, x));
            // ピークの時刻 = この LED が見ている 2 つの帯のうち、強いほうがピークになった時刻
            int k = Math.Min((int)(Math.Clamp(x, 0f, 1f) * (AudioFrame.BandCount - 1)), AudioFrame.BandCount - 2);
            double tPk = c.Peak.Punch[k] >= c.Peak.Punch[k + 1] ? c.PunchPeakT[k] : c.PunchPeakT[k + 1];
            // ピーク: 保持時間が過ぎたら一定の速さで下げる（時刻で計算するので 1 秒間のコマ数に左右されない）
            if (pk >= HeightAt(j, tPk))
            {
                _hPeak[j] = pk;
                _tPeak[j] = Math.Max(_tPeak[j], tPk);
            }
            _v[j] = Math.Max(now, GhostShare * Math.Max(0, HeightAt(j, t)));
        }
        if (s.Count >= BlurMinLeds) Blur(a, s.Count, s.Ring);

        double span = c.Pal.Span;
        for (int j = a; j < end; j++)
            dst[j] = c.PalAt(c.X[j] * span + _rot) * c.Ilin(_v[j]);
    }

    /// <summary>LED j のピークの、時刻 t の高さ（保持時間が過ぎたら 1 秒に 1.5 ずつ下げる）</summary>
    private float HeightAt(int j, double t) => _hPeak[j] - (float)(FallPerSec * Math.Max(0, t - _tPeak[j] - HoldSec));

    /// <summary>[0.25, 0.5, 0.25] で 1 回ぼかす（端は端の値をくり返す。輪なら反対の端とつながる）</summary>
    private void Blur(int start, int count, bool ring)
    {
        Array.Copy(_v, start, _tmp, start, count);
        int last = start + count - 1;
        for (int j = start; j <= last; j++)
        {
            int l = j > start ? j - 1 : ring ? last : start;
            int r = j < last ? j + 1 : ring ? start : last;
            _v[j] = 0.25f * _tmp[l] + 0.5f * _tmp[j] + 0.25f * _tmp[r];
        }
    }
}

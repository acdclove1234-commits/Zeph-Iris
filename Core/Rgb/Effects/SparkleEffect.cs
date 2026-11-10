// ==================================================
// SparkleEffect.cs - 光り方 E8「きらめき」 (キックで全体がふわっと明るくなり、スネアでまとまった光、ハイハットで白い粒が光る)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// きらめき: どの音で光ったかが見て分かるように、音ごとに光り方を分ける。
///   下地 = P(t / 20 秒) × Ilin(0.10 + 0.25 × 中音 + 0.45 × キックの光)   … キックのたびにゾーン全体がふわっと明るくなる（τ 0.25 秒）
///   スネア（強さ s）: ランダムな位置から続けて並んだ LED（ゾーンの 12%、強いほど長い）が、いまの色の反対側の色で光る（τ 0.18 秒）
///   ハイハット: 1 個ずつ離れた LED（ゾーンの 2% × s 個。多くても 1/12）が、白に近い色で一瞬だけ光る（τ 0.07 秒）
///   LED の色 = 下地 + 粒の色 × exp(−(t − 光った時刻)/τ)
/// 粒は光った本当の時刻を覚えるので、1 秒間のコマ数が違っても同じ明るさで消えていく。
/// 6 個未満のゾーンは、スネアでゾーン全体が 0.8、ハイハットで 0.4 の明るさで光る
/// </summary>
public sealed class SparkleEffect : IMusicEffect
{
    /// <summary>下地の色がパレットを 1 周する秒数</summary>
    public const double BaseCycleSec = 20;
    /// <summary>ハイハットの粒が消える時定数（秒）</summary>
    public const float HatTauSec = 0.07f;
    /// <summary>スネアの光が消える時定数（秒）</summary>
    public const float SnareTauSec = 0.18f;
    /// <summary>キックで下地が明るくなる光の時定数（秒）</summary>
    public const double KickTauSec = 0.25;
    /// <summary>ハイハットの粒を白へ寄せる割合（白すぎると、色の付いた下地の上で粒だけが目立ちすぎる）</summary>
    public const float HatWhiteMix = 0.4f;
    /// <summary>スネアで光らせる、続けて並んだ LED の長さ（ゾーンに対する割合。強さで 0.6〜1 倍）</summary>
    public const float SnareLength = 0.12f;
    /// <summary>小さいゾーンでの、ハイハット・スネアの光の明るさ</summary>
    public const float SmallHatLevel = 0.4f;
    public const float SmallSnareLevel = 0.8f;

    private double[] _t0 = Array.Empty<double>();
    private float[] _tau = Array.Empty<float>();
    private LinColor[] _col = Array.Empty<LinColor>();
    private LinColor _base;
    private double _kickT = double.NegativeInfinity;
    private float _kickS;
    /// <summary>まとまりごとの、最後にハイハットで光らせた時刻</summary>
    private double[] _lastHatT = Array.Empty<double>();

    public string Name => MusicStyles.Sparkle;

    public void Reset(EffectContext c)
    {
        int n = c.LedCount;
        if (_t0.Length != n)
        {
            _t0 = new double[n];
            _tau = new float[n];
            _col = new LinColor[n];
        }
        Array.Fill(_t0, double.NegativeInfinity);
        Array.Fill(_tau, HatTauSec);
        Array.Clear(_col);
        _kickT = double.NegativeInfinity;
        _kickS = 0;
        _lastHatT = Array.Empty<double>();
    }

    public void Update(EffectContext c)
    {
        var ev = c.Events;
        for (int i = 0; i < ev.Count; i++)
        {
            var e = ev[i];
            if (e.Kind == AudioEventKind.Hat) SpawnHat(c, e.T, e.Strength);
            else if (e.Kind == AudioEventKind.Snare) SpawnSnare(c, e.T, e.Strength);
            else if (e.Kind == AudioEventKind.Kick)
            {
                _kickT = e.T;
                _kickS = Math.Clamp(0.6f + 0.2f * e.Strength, 0, 1);
            }
        }
        float kick = _kickS * (float)Math.Exp(-Math.Max(0, c.T - _kickT) / KickTauSec);
        // ビートの光（テンポをつかんでいれば、予測した拍で光る。キックを検出してから光るだけだと、検出にかかる時間だけ遅れて見える。
        // ほかの光り方と同じく、デバイスの遅れも先回りする）。コマの間の最大値から経過時間だけ減らした値も見る
        float beatFl = Math.Max(c.Fl, c.FlPeak * (float)Math.Exp(-Math.Max(0, c.T - c.FlPeakT) / KickTauSec));
        kick = Math.Max(kick, beatFl);
        _base = c.PalAt(c.T / BaseCycleSec) * c.Ilin(0.08f + 0.22f * c.Frame.MidE + 0.6f * kick);
    }

    /// <summary>
    /// ハイハット: 離れた LED を 1 個ずつ、白に近い色で。明るさ・粒の数・間隔は「強さ」で決める（MusicTuning.HatResponse）。
    /// 同じまとまりでは MinGapSec より詰めて光らせない（16 分音符の刻みで、ずっとちかちかしないように）
    /// </summary>
    private void SpawnHat(EffectContext c, double t0, float strength)
    {
        var (gain, minGap) = MusicTuning.HatResponse(c.Music);
        // 強いハイハット（オープン・シンバル）ほど明るく、弱い刻みは控えめに
        float level = gain * (0.6f + 0.4f * Math.Clamp((strength - 1f) / 2f, 0f, 1f));
        if (_lastHatT.Length != c.SegmentCount)
        {
            _lastHatT = new double[c.SegmentCount];
            Array.Fill(_lastHatT, double.NegativeInfinity);
        }
        for (int k = 0; k < c.SegmentCount; k++)
        {
            if (t0 - _lastHatT[k] < minGap && t0 >= _lastHatT[k]) continue;
            _lastHatT[k] = t0;
            var s = c.Segments[k];
            if (s.Small)
            {
                var zc = HatColor(c) * (SmallHatLevel * level);
                for (int j = s.Start; j < s.Start + s.Count; j++) Set(j, t0, HatTauSec, zc);
                continue;
            }
            int n = Math.Clamp((int)MathF.Round(0.02f * s.Count * strength * gain), 1, Math.Max(1, s.Count / 12));
            for (int q = 0; q < n; q++)
                Set(s.Start + c.Rng.NextInt(s.Count), t0, HatTauSec, HatColor(c) * level);
        }
    }

    /// <summary>スネア: 続けて並んだ LED のまとまりを、いまの色の反対側の色で</summary>
    private void SpawnSnare(EffectContext c, double t0, float strength)
    {
        var col = c.ColorAt(0.5);
        for (int k = 0; k < c.SegmentCount; k++)
        {
            var s = c.Segments[k];
            if (s.Small)
            {
                var zc = col * SmallSnareLevel;
                for (int j = s.Start; j < s.Start + s.Count; j++) Set(j, t0, SnareTauSec, zc);
                continue;
            }
            float scale = Math.Clamp(0.6f + 0.2f * strength, 0.6f, 1f);
            int len = Math.Clamp((int)MathF.Round(SnareLength * s.Count * scale), 2, s.Count);
            int start = c.Rng.NextInt(s.Count);
            for (int q = 0; q < len; q++)
            {
                // まとまりの両端を少し暗くして、光の塊に見えるようにする
                float edge = len <= 2 ? 1f : 1f - 0.5f * MathF.Abs(2f * q / (len - 1) - 1f);
                Set(s.Start + (start + q) % s.Count, t0, SnareTauSec, col * edge);
            }
        }
    }

    /// <summary>ハイハットの粒の色: パレットのランダムな色を白へ寄せる（OKLab で混ぜる）</summary>
    private static LinColor HatColor(EffectContext c)
        => OkLab.ToLed(Lab.Lerp(c.Pal.LabAt(c.Rng.NextFloat()), Lab.White, HatWhiteMix), c.Gamma);

    private void Set(int j, double t0, float tau, LinColor col)
    {
        _t0[j] = t0;
        _tau[j] = tau;
        _col[j] = col;
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        double t = c.T;
        for (int j = s.Start; j < s.Start + s.Count; j++)
        {
            double age = t - _t0[j];
            var col = _base;
            if (age >= 0 && age < _tau[j] * 8) col += _col[j] * (float)Math.Exp(-age / _tau[j]);
            dst[j] = col.Clamp01();
        }
    }
}

// ==================================================
// SparkleEffect.cs - 光り方 E8「きらめき」 (キックで全体がふわっと明るくなり、スネアでまとまった光、ハイハットで白い粒、ギターの音の出だしで色の帯が光る)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// きらめき: どの音で光ったかが見て分かるように、音ごとに光り方を分ける。
///   下地 = P(t / 20 秒) × Ilin(0.10 + 0.25 × 中音 + 0.45 × キックの光)   … キックのたびにゾーン全体がふわっと明るくなる（τ 0.25 秒）
///   スネア（強さ s）: ランダムな位置から続けて並んだ LED（ゾーンの 12%、強いほど長い）が、いまの色の反対側の色で光る（τ 0.18 秒）
///   ハイハット: 1 個ずつ離れた LED（ゾーンの 2% × s 個。多くても 1/12）が、白に近い色で一瞬だけ光る（τ 0.07 秒。更新の遅いデバイスは 0.12 秒）
///   ギター（Tonal の出来事。テンポをつかんでいるときだけ）: 続けて並んだ LED（ゾーンの 12%、3 個以上）が、端から端へ 45 ミリ秒かけて
///     順に光る（弦をなでるように。向きは 1 回ごとに変える）。色は下地から 1/4 周ずらした色（τ 0.26 秒。更新の遅いデバイスは 0.30 秒）
///   LED の色 = 下地 + 粒の色 × exp(−(t − 光った時刻)/τ) + ギターの光の色 × exp(−(t − 光った時刻)/τ)
/// 粒は光った本当の時刻を覚えるので、1 秒間のコマ数が違っても同じ明るさで消えていく。
/// ギターの光は粒とは別に覚える（ハイハット・スネアの粒を消したり、次の粒を止めたりしない）。
/// 6 個未満のゾーンは、スネアでゾーン全体が 0.8、ハイハットで 0.4、ギターで 0.3 の明るさで光る
/// （ギターは「激しい点滅を抑える」がオンなら 1 秒に 3 回まで。それより詰めた分は 0.35 倍）
/// </summary>
public sealed class SparkleEffect : IMusicEffect
{
    /// <summary>下地の色がパレットを 1 周する秒数</summary>
    public const double BaseCycleSec = 20;
    /// <summary>ハイハットの粒が消える時定数（秒）</summary>
    public const float HatTauSec = 0.07f;
    /// <summary>
    /// 更新の遅いデバイス（直接書き込むグラボ・メモリなど。1 秒に 10 回まで: c.SlowDevice）での、ハイハットの粒が消える時定数（秒）。
    /// 0.07 秒だと、1 秒に 10 回の送り方では粒が 1 回の送りにしか出ず、ほとんど見えない（録音 11 曲の直接書き込むグラボで、0.2 秒以上見える粒が 1 秒に 5.2 回 → 7.1 回。粒の数・書き込みの回数は変わらない）
    /// </summary>
    public const float SlowHatTauSec = 0.12f;
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
    /// <summary>静かな部分での明るさの倍率（盛り上がりで 1。EffectContext.SectionGain）</summary>
    public const float SectionMin = 0.6f;
    /// <summary>同じ LED に次の粒を置けるまでの秒数（1 つの LED が光るのは 1 秒に 3 回まで）</summary>
    public const double ParticleGapSec = 0.333;
    /// <summary>ハイハットの粒の場所を選び直す回数（少し前に光った LED に当たったとき）</summary>
    private const int PickTries = 4;

    // ===== ギター（Tonal の出来事）
    /// <summary>ギターの光が端から端へ走る秒数</summary>
    public const double GtrSweepSec = 0.045;
    /// <summary>次のギターの光を出せるまでの秒数（ゾーンごとではなく、光り方全体で）</summary>
    public const double GtrGapSec = 0.1;
    /// <summary>同じ LED に次のギターの光を置けるまでの秒数</summary>
    public const double GtrLedGapSec = 0.12;
    /// <summary>ギターの光が消える時定数（秒）と、更新の遅いデバイスでの下限</summary>
    public const float GtrTauSec = 0.26f, SlowGtrTauSec = 0.30f;
    /// <summary>ギターの光の色を、下地の色からずらす量（パレットの 1/4 周。スネアの反対側の色・ハイハットの白とは別の色）</summary>
    public const float GtrHueShift = 0.25f;
    /// <summary>ギターの光の長さ（ゾーンに対する割合。強さで 0.7〜1 倍。3 個以上）</summary>
    public const float GtrLength = 0.12f;
    /// <summary>ギターの光の明るさ（ハイハット・スネアより控えめ。0.45 では録音した曲で拍から外れた光が少し増えた）</summary>
    public const float GtrLevel = 0.40f;
    /// <summary>小さいゾーンでの、ギターの光の明るさ</summary>
    public const float SmallGtrLevel = 0.3f;
    /// <summary>調査・テスト用: ギターの光を出したとき（時刻・強さ）</summary>
    internal static Action<double, float>? DiagGuitar;

    private double[] _gT0 = Array.Empty<double>();
    private float[] _gTau = Array.Empty<float>();
    private LinColor[] _gCol = Array.Empty<LinColor>();
    private double _gLastT = double.NegativeInfinity;
    private bool _gUp;
    /// <summary>小さいゾーンのギターの光（「激しい点滅を抑える」がオンなら 1 秒に 3 回まで）</summary>
    private readonly FlashLimiter _gLimit = new();

    private double[] _t0 = Array.Empty<double>();
    private float[] _tau = Array.Empty<float>();
    private LinColor[] _col = Array.Empty<LinColor>();
    private LinColor _base;
    private double _kickT = double.NegativeInfinity;
    private float _kickS;
    /// <summary>まとまりごとの、最後にハイハットで光らせた時刻</summary>
    private double[] _lastHatT = Array.Empty<double>();

    public void Reset(EffectContext c)
    {
        int n = c.LedCount;
        if (_t0.Length != n)
        {
            _t0 = new double[n];
            _tau = new float[n];
            _col = new LinColor[n];
            _gT0 = new double[n];
            _gTau = new float[n];
            _gCol = new LinColor[n];
        }
        Array.Fill(_t0, double.NegativeInfinity);
        Array.Fill(_tau, HatTauSec);
        Array.Clear(_col);
        Array.Fill(_gT0, double.NegativeInfinity);
        Array.Fill(_gTau, GtrTauSec);
        Array.Clear(_gCol);
        _gLastT = double.NegativeInfinity;
        _gUp = false;
        _gLimit.Reset();
        _kickT = double.NegativeInfinity;
        _kickS = 0;
        _lastHatT = Array.Empty<double>();
    }

    public void Update(EffectContext c)
    {
        var ev = c.Events;
        _gLimit.Enabled = c.Music.ReduceFlashing;
        for (int i = 0; i < ev.Count; i++)
        {
            var e = ev[i];
            if (e.Kind == AudioEventKind.Hat) SpawnHat(c, e.T, e.Strength);
            // ギターの音の出だしは、テンポをつかんでいるときだけ（話し声・映画の効果音で光らせない）
            else if (e.Kind == AudioEventKind.Tonal) { if (c.Locked) SpawnGuitar(c, e.T, Math.Clamp(0.5f + 0.25f * (e.Strength - 1.2f), 0.5f, 1f)); }
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
        // 静かな部分では控えめに、盛り上がりでは大きく（キックの光と中音の分だけ。下地の明るさは変えない）
        float sec = c.SectionGain(SectionMin);
        _base = c.PalAt(c.T / BaseCycleSec) * c.Ilin(0.08f + sec * (0.22f * c.Frame.MidE + 0.6f * kick));
    }

    /// <summary>
    /// ハイハット: 離れた LED を 1 個ずつ、白に近い色で。明るさ・粒の数・間隔は「強さ」で決める（MusicTuning.HatResponse）。
    /// 同じまとまりでは MinGapSec より詰めて光らせない（16 分音符の刻みで、ずっとちかちかしないように）
    /// </summary>
    private void SpawnHat(EffectContext c, double t0, float strength)
    {
        var (gain, minGap) = MusicTuning.HatResponse(c.Music);
        // 更新の遅いデバイスは、粒を少し長く残す（送る回数が少ないので、短い粒は 1 回の送りにしか出ない）
        float tau = c.SlowDevice ? SlowHatTauSec : HatTauSec;
        // 強いハイハット（オープン・シンバル）ほど明るく、弱い刻みは控えめに
        float level = gain * c.SectionGain(SectionMin) * (0.6f + 0.4f * Math.Clamp((strength - 1f) / 2f, 0f, 1f));
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
                for (int j = s.Start; j < s.Start + s.Count; j++) Set(j, t0, tau, zc);
                continue;
            }
            int n = Math.Clamp((int)MathF.Round(0.02f * s.Count * strength * gain), 1, Math.Max(1, s.Count / 12));
            for (int q = 0; q < n; q++)
            {
                // 少し前に粒が光った LED は避けて、別の LED を選ぶ（見つからなければ、この粒は出さない）
                for (int tryNo = 0; tryNo < PickTries; tryNo++)
                    if (Set(s.Start + c.Rng.NextInt(s.Count), t0, tau, HatColor(c) * level)) break;
            }
        }
    }

    /// <summary>スネア: 続けて並んだ LED のまとまりを、いまの色の反対側の色で</summary>
    private void SpawnSnare(EffectContext c, double t0, float strength)
    {
        var col = c.ColorAt(0.5) * c.SectionGain(SectionMin);
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

    /// <summary>
    /// ギター: 続けて並んだ LED を、端から端へ GtrSweepSec かけて順に光らせる（強さ 0.5〜1）。
    /// 光り方全体で GtrGapSec より詰めない。同じ LED は GtrLedGapSec より詰めない
    /// </summary>
    private void SpawnGuitar(EffectContext c, double t0, float strength)
    {
        if (t0 - _gLastT < GtrGapSec && t0 >= _gLastT) return;
        _gLastT = t0;
        DiagGuitar?.Invoke(t0, strength);
        var col = c.PalAt(c.T / BaseCycleSec + GtrHueShift) * (GtrLevel * c.SectionGain(SectionMin) * strength);
        float tau = c.SlowDevice ? Math.Max(GtrTauSec, SlowGtrTauSec) : GtrTauSec;
        _gUp = !_gUp;
        float lim = float.NaN;
        for (int k = 0; k < c.SegmentCount; k++)
        {
            var s = c.Segments[k];
            if (s.Small)
            {
                if (float.IsNaN(lim)) lim = _gLimit.Decide(t0);
                var zc = col * (SmallGtrLevel * lim);
                for (int j = s.Start; j < s.Start + s.Count; j++) SetGuitar(j, t0, tau, zc);
                continue;
            }
            int len = Math.Clamp((int)MathF.Round(GtrLength * s.Count * (0.7f + 0.3f * strength)), 3, s.Count);
            int start = c.Rng.NextInt(s.Count);
            for (int q = 0; q < len; q++)
            {
                int qq = _gUp ? q : len - 1 - q;
                // 両端を 4 割暗くして、光の帯に見えるようにする
                float edge = 1f - 0.4f * MathF.Abs(2f * qq / (len - 1) - 1f);
                int j = s.Start + (start + qq) % s.Count;
                if (_gT0[j] < t0 && t0 - _gT0[j] < GtrLedGapSec) continue;
                SetGuitar(j, t0 + GtrSweepSec * q / (len - 1), tau, col * edge);
            }
        }
    }

    private void SetGuitar(int j, double t0, float tau, LinColor col)
    {
        _gT0[j] = t0;
        _gTau[j] = tau;
        _gCol[j] = col;
    }

    /// <summary>ハイハットの粒の色: パレットのランダムな色を白へ寄せる（OKLab で混ぜる）</summary>
    private static LinColor HatColor(EffectContext c)
        => OkLab.ToLed(Lab.Lerp(c.PalLabAt(c.Rng.NextFloat()), Lab.White, HatWhiteMix), c.Gamma);

    /// <summary>
    /// LED j に、時刻 t0 に光る粒を置く。同じ LED には ParticleGapSec より詰めて粒を置かない（置かなければ false）。
    /// ハイハット・スネアが 1 秒に何回も鳴っても、1 つの LED は 1 秒に 3 回までしか光らない（同じ LED の点滅に見えないように）
    /// </summary>
    private bool Set(int j, double t0, float tau, LinColor col)
    {
        if (t0 - _t0[j] < ParticleGapSec && t0 >= _t0[j]) return false;
        _t0[j] = t0;
        _tau[j] = tau;
        _col[j] = col;
        return true;
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        double t = c.T;
        for (int j = s.Start; j < s.Start + s.Count; j++)
        {
            double age = t - _t0[j];
            var col = _base;
            if (age >= 0 && age < _tau[j] * 8) col += _col[j] * (float)Math.Exp(-age / _tau[j]);
            double ga = t - _gT0[j];
            if (ga >= 0 && ga < _gTau[j] * 8) col += _gCol[j] * (float)Math.Exp(-ga / _gTau[j]);
            dst[j] = col.Clamp01();
        }
    }
}

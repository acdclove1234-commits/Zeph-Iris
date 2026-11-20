// ==================================================
// MeterEffect.cs - 光り方 E5「音量メーター」 (音量の分だけ端から点き、キック・スネアで弾む。ピークの点が重力でゆっくり落ちてくる)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 音量メーター（ビートで弾むグラビメーター）:
///   長さ ℓ = LoudShare × 聞こえ方の音量（LoudVU）＋ BeatShare × 弾み
///   弾み B: キック・スネアを検出した本当の時刻に、強さに応じて 0.6〜1 まで上がり、τ = 0.12 秒で戻る
///   （低音の量ではなく「叩いた瞬間」で弾ませる。低音が鳴り続ける曲でも、ふだんは半分くらいで、叩くたびに上まで伸びてすぐ戻る）
///   大きな音のすぐあとの小さな音は、弾みも小さい（EffectContext.LoudRel）
///   静かなところは短く、無音なら消える（音量の分もキックもなくなる）
///   a_j = clamp(ℓ × Ne − jx, 0, 1)、LED の色 = メーター色（緑 → 黄 → 赤、位置で決まる）× Ilin(a_j)
///   ピークの点: 長さが点より上がったら点をそこへ上げて 0.5 秒止め、その後は重力（2.5 / 秒²）で落とす。
///   点の位置 u = h × (Ne − 1)。隣の LED との間は明るさを分けて（max(0, 1 − |jx − u|)）なめらかに動かす
/// 弾み・点の動きは本当の時刻（キックの時刻・2 ミリ秒ごとの音量の記録）で計算するので、1 秒間のコマ数が違っても同じに動く。
/// 「始まり」は向き（Reverse）・左右対称（Mirror = 中央から）で変えられる。6 個未満のゾーンは音量で光るパルス
/// </summary>
public sealed class MeterEffect : IMusicEffect
{
    /// <summary>ピークの点を止めておく秒数</summary>
    public const double HoldSec = 0.5;
    /// <summary>ピークの点が落ちる加速度（メーターの長さ / 秒²）</summary>
    public const double Gravity = 2.5;
    /// <summary>点の明るさ（白の光の量）</summary>
    public const float DotLevel = 1f;
    /// <summary>長さのうち、音量で決まる分（音量 100% でメーターの半分）</summary>
    public const float LoudShare = 0.5f;
    /// <summary>長さのうち、キック・スネアで弾む分</summary>
    public const float BeatShare = 0.6f;
    /// <summary>弾んだ後に戻る時定数（秒）</summary>
    public const double BounceTauSec = 0.12;
    /// <summary>これより弱いキック・スネアでは弾ませない（波紋と同じ）</summary>
    public const float MinStrength = 1.2f;
    /// <summary>スネアの弾みはキックのこの割合</summary>
    public const float SnareScale = 0.8f;

    private readonly PulseEffect _small = new(source: PulseEffect.Source.Loudness);
    private float _h0;                 // 点を止めたときの高さ
    private double _tPeak = double.NegativeInfinity;
    private float _h;                  // いまの点の高さ（0〜1）
    private float _level;
    // 弾み: 届いたキック・スネア（本当の時刻・弾みの高さ・点を上げる高さ。2 秒より古いものは捨てる）
    private readonly List<(double T, float V, float Top)> _kicks = new();
    // 音量の記録の控え（2 ミリ秒ごと・直近 0.5 秒）。遅れて届いたキックの時刻の音量を引くのに使う
    private const int HistoryLength = 256;
    private readonly double[] _histT = new double[HistoryLength];
    private readonly float[] _histV = new float[HistoryLength];
    private int _histCount, _histHead;

    /// <summary>いまのピークの点の高さ（0〜1。テスト用）</summary>
    public float DotHeight => _h;

    /// <summary>いまのメーターの長さ（0〜1。テスト用）</summary>
    public float Level => _level;

    public void Reset(EffectContext c)
    {
        _h0 = _h = 0;
        _tPeak = double.NegativeInfinity;
        _kicks.Clear();
        _histCount = _histHead = 0;
        _small.Reset(c);
    }

    /// <summary>時刻 t の点の高さ（止めた後、重力で落ちる: h0 − ½·g·(経過 − 止める時間)²）</summary>
    private float HeightAt(double t)
    {
        double fall = Math.Max(0, t - _tPeak - HoldSec);
        return (float)Math.Max(0, _h0 - 0.5 * Gravity * fall * fall);
    }

    /// <summary>時刻 t の弾み（届いているキック・スネアのうち、t 以前のものが τ で減った値の最大）</summary>
    private float BounceAt(double t)
    {
        float b = 0;
        foreach (var (te, v, _) in _kicks)
            if (te <= t) b = Math.Max(b, v * (float)Math.Exp(-(t - te) / BounceTauSec));
        return b;
    }

    /// <summary>
    /// 時刻 t の、キック・スネアで上げた点の高さ（それぞれ、その時刻に上げて 0.5 秒止め、重力で落とした値の最大）。
    /// 音量で上げる点とは別に持つ（キックは遅れて届くので、1 つの点にまとめると、届く順番で高さが変わる）
    /// </summary>
    private float KickDotAt(double t)
    {
        float h = 0;
        foreach (var (te, _, top) in _kicks)
        {
            if (te > t) continue;
            double fall = Math.Max(0, t - te - HoldSec);
            h = Math.Max(h, (float)(top - 0.5 * Gravity * fall * fall));
        }
        return h;
    }

    /// <summary>音量の記録を控えに入れる（古いものから上書き）</summary>
    private void Remember(double t, float v)
    {
        _histT[_histHead] = t;
        _histV[_histHead] = v;
        _histHead = (_histHead + 1) % HistoryLength;
        if (_histCount < HistoryLength) _histCount++;
    }

    /// <summary>時刻 t の音量の分（控えのうち t 以前でいちばん新しいもの。控えになければ fallback）</summary>
    private float LoudAt(double t, float fallback)
    {
        for (int k = 1; k <= _histCount; k++)
        {
            int i = (_histHead - k + HistoryLength) % HistoryLength;
            if (_histT[i] <= t) return _histV[i];
        }
        return fallback;
    }

    /// <summary>キック・スネアの強さ（検出のしきい値の何倍か。1.2〜）→ 弾みの高さ（0.6〜1）</summary>
    public static float BounceOf(AudioEvent ev)
    {
        float v = Math.Clamp(0.6f + (ev.Strength - MinStrength) / 1.5f * 0.4f, 0.6f, 1f);
        return ev.Kind == AudioEventKind.Snare ? v * SnareScale : v;
    }

    public void Update(EffectContext c)
    {
        // 前のコマからの音量の記録（2 ミリ秒ごとの決まった時刻）を時刻の順にたどり、音量の分が点の高さ以上になった時刻ごとに、
        // 点をそこへ上げて止め直す（同じ高さが続く間は止まったまま。本当の時刻で数えるので、1 秒間のコマ数で点の動きが変わらない）
        for (int i = 0; i < c.LoudCount; i++)
        {
            double ti = c.LoudT[i];
            float v = LoudShare * Math.Clamp(c.LoudV[i], 0f, 1f);
            Remember(ti, v);
            if (v >= HeightAt(ti))
            {
                _h0 = v;
                _tPeak = Math.Max(_tPeak, ti);
            }
        }

        // キック・スネア: 検出は少し遅れて届く。届いたら、その本当の時刻に弾んだものとして覚え、
        // 点もその時刻にさかのぼって「その時刻の音量の分 ＋ 弾み」まで上げたものとして覚える（届いた後の時刻で上げると、コマ数で点の高さが変わる）
        float loud = LoudShare * Math.Clamp(c.Frame.LoudVU, 0f, 1f);
        var events = c.Events;
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            if (ev.Kind is not (AudioEventKind.Kick or AudioEventKind.Snare) || ev.Strength < MinStrength) continue;
            float v = BounceOf(ev) * c.LoudRel(ev.T);
            _kicks.Add((ev.T, v, Math.Clamp(LoudAt(ev.T, loud) + BeatShare * v, 0f, 1f)));
        }
        while (_kicks.Count > 0 && _kicks[0].T < c.T - 2) _kicks.RemoveAt(0);

        _level = Math.Clamp(loud + BeatShare * BounceAt(c.T), 0f, 1f);
        // 表示する点は、棒の先より下にはしない（覚えている点の高さ・止めた時刻は変えない）
        _h = Math.Max(Math.Max(HeightAt(c.T), KickDotAt(c.T)), _level);
        if (c.HasSmallSegment) _small.Update(c);
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        if (s.Small) { _small.Render(c, s, dst); return; }
        float ne = s.Ne;
        float last = Math.Max(1f, ne - 1);
        float u = _h * last;
        // 無音で点が一番下にいるときは、点を消す（いつも最初の LED が白く光らないように）
        float dotW = Math.Min(1f, _h * 10) * DotLevel;
        var meter = ColorPalette.Meter;
        double span = meter.Span;
        for (int j = s.Start; j < s.Start + s.Count; j++)
        {
            float jx = c.Jx[j];
            float a = Math.Clamp(_level * ne - jx, 0f, 1f);
            var col = meter.At(jx / last * span, c.Gamma) * c.Ilin(a);
            float dot = Math.Max(0f, 1f - Math.Abs(jx - u)) * dotW;
            if (dot > 0) col += LinColor.White * dot;
            dst[j] = col.Clamp01();
        }
    }
}

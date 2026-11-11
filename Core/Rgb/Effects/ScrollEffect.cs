// ==================================================
// ScrollEffect.cs - 光り方 E6「流れる波」 (今の音の色が「始まり」から端へ流れていく。ウォーターフォール)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 流れる波: ゾーンごとに「色のマス」の列（バッファ）を持ち、先頭に今の音の色を入れて端へ送っていく。
///   先頭の色 = Σ_g 色_g × clamp(e_g², 0, 1) + 0.3 × Fl × 白（低音・中音・高音の色を強さの 2 乗で混ぜる）
///   速さ v = テンポをつかんでいれば Ne / (8 拍)、つかんでいなければ Ne / 2 秒（LED / 秒。× 流れる速さの設定）
///   位置 acc は v × 時間で進み、1 を超えるたびにマスを 1 つ送る。マスは exp(−経過 / 0.8 秒) で暗くなる
///   LED の色 = lerp(マス[j], マス[j − 1], acc の小数部分)   … LED の間も滑らかに動く
/// 1 秒間のコマ数に左右されないように:
///   ・時間は 2 ミリ秒ごとの決まった時刻（コマとは無関係）で進め、マスを送る時刻もそこで決める
///   ・送るマスの色は、そのマスの時間（1 / v 秒）の間の「いちばん明るい先頭の色」（キックを取りこぼさない）
///   ・送った後に過ぎた時間の分だけ、マスを暗くしておく
/// 左右対称（Mirror）なら中央から両端へ流れる。6 個未満のゾーンは先頭の色で光るパルス
/// </summary>
public sealed class ScrollEffect : IMusicEffect
{
    /// <summary>マスが暗くなっていく時定数（秒）</summary>
    public const double FadeTauSec = 0.8;
    /// <summary>テンポをつかんでいるとき、端まで流れるのにかかる拍数</summary>
    public const double BeatsAcross = 8;
    /// <summary>テンポがないとき、端まで流れるのにかかる秒数</summary>
    public const double SecondsAcross = 2;
    /// <summary>ビートの光を白で足す割合</summary>
    public const float FlashWhite = 0.3f;
    /// <summary>静かな部分での明るさの倍率（盛り上がりで 1。EffectContext.SectionGain）</summary>
    public const float SectionMin = 0.6f;
    /// <summary>時間を進める刻み（秒）。分析の 1 コマ（約 10.7 ミリ秒）より細かくする</summary>
    public const double GridSec = 0.002;

    private readonly PulseEffect _small = new();
    private readonly AudioFrame _sample = new();
    private LinColor[] _buf = Array.Empty<LinColor>();
    private int[] _offset = Array.Empty<int>();
    private int[] _length = Array.Empty<int>();
    private double[] _acc = Array.Empty<double>();
    private LinColor[] _pending = Array.Empty<LinColor>();   // ゾーンごと: 前にマスを送ってからの先頭の色の最大
    private readonly LinColor[] _col = new LinColor[3];
    private LinColor _head;
    private double _gridT = double.NaN;                       // 最後に処理した刻みの時刻
    private int _layout = -1;

    public string Name => MusicStyles.Scroll;

    public void Reset(EffectContext c)
    {
        int segs = c.SegmentCount;
        if (_layout != c.LayoutVersion || _offset.Length != segs)
        {
            // ゾーンごとのマスの場所を決める（LED の並びが変わったときだけ作り直す）
            _offset = new int[segs];
            _length = new int[segs];
            _acc = new double[segs];
            _pending = new LinColor[segs];
            int total = 0;
            for (int k = 0; k < segs; k++)
            {
                _offset[k] = total;
                _length[k] = (int)Math.Ceiling(c.Segments[k].Ne) + 2;
                total += _length[k];
            }
            _buf = new LinColor[total];
            _layout = c.LayoutVersion;
        }
        Array.Clear(_buf);
        Array.Clear(_acc);
        Array.Clear(_pending);
        _gridT = double.NaN;
        _small.Reset(c);
    }

    public void Update(EffectContext c)
    {
        double half = c.Pal.Span * 0.5;
        for (int g = 0; g < 3; g++) _col[g] = c.ColorAt(g * half);
        _head = Head(c.Frame, c.Fl) * c.SectionGain(SectionMin);

        double perSec = (c.Locked ? 1 / (BeatsAcross * c.Period) : 1 / SecondsAcross) * c.Speed;
        float keep = EffectContext.Decay(c.Dt, FadeTauSec);
        for (int i = 0; i < _buf.Length; i++) _buf[i] *= keep;

        // 2 ミリ秒ごとの決まった時刻で、先頭の色を読み、マスを送る
        double t1 = c.T;
        if (double.IsNaN(_gridT) || _gridT > t1 || t1 - _gridT > 0.5)
        {
            // 始めるとき: マスの区切りを「時刻 × 速さ」にそろえる（いつ始めても、同じ時刻に同じ区切りになる）
            _gridT = Math.Floor(t1 / GridSec) * GridSec - GridSec;
            for (int k = 0; k < c.SegmentCount; k++)
            {
                double p = c.Segments[k].Ne * perSec * _gridT;
                _acc[k] = p - Math.Floor(p);
            }
        }
        while (_gridT + GridSec <= t1)
        {
            double g = _gridT + GridSec;
            var head = HeadAt(c, g);
            for (int k = 0; k < c.SegmentCount; k++)
            {
                var s = c.Segments[k];
                if (s.Small) continue;
                double v = s.Ne * perSec;   // LED / 秒
                _pending[k] = LinColor.Max(_pending[k], head);
                _acc[k] += v * GridSec;
                if (_acc[k] < 1) continue;
                // 1 つ送る: 後ろから順にずらし、先頭にこのマスの間のいちばん明るい色を入れる。
                // 送った時刻（g）から今（t1）までの分だけ暗くしておく
                int o = _offset[k], len = _length[k];
                for (int i = o + len - 1; i > o; i--) _buf[i] = _buf[i - 1];
                _buf[o] = _pending[k] * (float)Math.Exp(-(t1 - g) / FadeTauSec);
                _acc[k] -= Math.Floor(_acc[k]);
                _pending[k] = head;
            }
            _gridT = g;
        }

        if (c.HasSmallSegment)
        {
            // 小さいゾーン: 先頭の色（いちばん強い成分を 1 にそろえる）で光るパルス
            float m = _head.MaxChannel;
            _small.ColorOverride = m > 1e-4f ? _head * (1 / m) : _col[0];
            _small.Update(c);
        }
    }

    /// <summary>時刻 t の先頭の色（分析結果の記録があればその時刻の音、なければ今のコマの音）</summary>
    private LinColor HeadAt(EffectContext c, double t)
    {
        var ring = c.Features;
        // 静かな部分では控えめに、盛り上がりでは大きく
        float sec = c.SectionGain(SectionMin);
        if (ring is null || !ring.Sample(t, _sample)) return Head(c.Frame, c.FlashAt(t)) * sec;
        return Head(_sample, c.FlashAt(t)) * sec;
    }

    private LinColor Head(AudioFrame f, float fl) =>
        _col[0] * Sq(f.BassE) + _col[1] * Sq(f.MidE) + _col[2] * Sq(f.TrebleE) + LinColor.White * (FlashWhite * fl);

    private static float Sq(float e)
    {
        float x = Math.Clamp(e, 0f, 1f);
        return x * x;
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        if (s.Small) { _small.Render(c, s, dst); return; }
        int k = IndexOf(c, s);
        int o = _offset[k], len = _length[k];
        float a = (float)_acc[k];
        var floor = _col[0] * c.F0;
        for (int j = s.Start; j < s.Start + s.Count; j++)
        {
            // 位置 q（始まりから数えた LED の番号）の色。中身は a だけ進んでいるので、q − a の位置を読む
            float q = c.Jx[j] - a;
            int i = (int)MathF.Floor(q);
            float fr = q - i;
            var lo = Cell(o, len, i);
            var hi = Cell(o, len, i + 1);
            dst[j] = (LinColor.Lerp(lo, hi, fr) + floor).FitToOne();
        }
    }

    /// <summary>マスの i 番目（−1 は今の先頭の色、範囲外は黒）</summary>
    private LinColor Cell(int o, int len, int i) => i < 0 ? _head : i >= len ? LinColor.Black : _buf[o + i];

    private static int IndexOf(EffectContext c, in LedSegment s)
    {
        for (int k = 0; k < c.SegmentCount; k++)
            if (c.Segments[k].Start == s.Start) return k;
        return 0;
    }
}

// ==================================================
// AiResampler.cs - AI に渡す音を、AI のサンプリング周波数にする（拍の AI: 22,050 Hz・楽器の AI: 16,000 Hz）と、その音の時刻
//   88.2〜384 kHz は先に整数分の 1 にし（Decimator。44.1 / 48 kHz）、そこから直線の補間で変える（その前に高い音を 2 段の低域フィルターで落とす）。
//   出てくる数は、どの大きさで流し込んでもちょうど「入力の数 × 出力の周波数 ÷ 入力の周波数」（次に出す位置を分数のまま数える）。
//   出した音の時刻は、入力の音の数と、取り込みが付けた時刻から求める（出てきた数を足していかない）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>音のかたまりを受け取る</summary>
public delegate void SamplesSink(ReadOnlySpan<float> samples);

/// <summary>
/// 鳴っている音（任意のサンプリング周波数・モノラル）を、AI のサンプリング周波数にする（少しずつ流し込める。メモリを確保しない）。
/// 前は NAudio の WdlResampler を使っていたが、1 回に渡した入力の終わりで次に出す位置が入力の外に出ると、その分を捨ててしまい、
/// 出てくる数が多すぎた（192,000 Hz を 10 ミリ秒ずつ 22,050 Hz にすると 0.227% 多く、1 kHz の音が 997.7 Hz になり、
/// 出てきた数で進めていた AI の時計が、取り込みの時刻より最大 0.25 秒ずれていた）。
/// ここでは、出力の k 番目を、入力の k × (入力の周波数 ÷ 出力の周波数) 番目の位置（分数のまま）から作る。
/// フィルター（2 段の 2 次の低域。出力のナイキスト周波数の 69.3%、Q = 0.707）と直線の補間は WdlResampler の設定（補間あり・フィルター 2 段）と同じなので、
/// 48 / 44.1 kHz の音は、前とほぼ同じ音を AI に渡す
/// </summary>
public sealed class AiResampler
{
    /// <summary>取り込みの時刻がこれより飛んだ・戻ったら、続きの音ではない（中の音を捨てて最初から）</summary>
    public const double MaxJumpSec = 0.25;
    /// <summary>取り込みの時刻がこれより先に飛んだら、その間の音が届かなかったとして、無音で埋める（時刻のずれを残さない）</summary>
    public const double FillSec = 0.005;
    /// <summary>低域フィルターの位置（出力のナイキスト周波数に対する割合）と Q（WdlResampler の既定と同じ）</summary>
    private const double FilterPos = 0.693, FilterQ = 0.707;
    /// <summary>一度にフィルター・補間する入力の数（出てくる音もこのまとまりごとに渡す）</summary>
    private const int Block = 1024;

    private readonly Decimator? _dec;
    /// <summary>補間する前の音（落としたあと・フィルターをかけたあと。Block 個まで）と、出てくる音</summary>
    private readonly float[] _in = new float[Block], _out;
    // 補間: 入力の周波数 ÷ 出力の周波数 = _a / _b（約分した整数）。次に出す音の位置 = _ip + _num / _b（補間する入力の番号）
    private readonly int _a, _b;
    private readonly double _invB;
    private long _ip;
    private int _num;
    /// <summary>補間する入力をこれまでに受け取った数と、その最後の音（次のまとまりの最初の音と補間する）</summary>
    private long _fed;
    private float _last;
    // 低域フィルター 2 段（落とすときは補間の前、上げるときは補間の後）
    private readonly bool _pre, _post, _copy;
    private readonly double _b0, _b1, _b2, _a1, _a2;
    /// <summary>
    /// 2 段の低域フィルターの遅れ（低い音での群遅延。補間する入力の数）。出した音の時刻からこの分を引く
    /// （48 kHz → 22,050 Hz で約 54 マイクロ秒・→ 16,000 Hz で約 77 マイクロ秒。引かないと、AI に渡る音がその分だけ遅れて見えた）
    /// </summary>
    private readonly double _filterDelay;
    private readonly double[] _h = new double[8];
    /// <summary>Reset のあとの、入力の 0 番目の音の時刻（Sync のたびに、渡された時刻に合わせ直す）</summary>
    private double _t0 = double.NaN;
    private static readonly float[] Zeros = new float[Block];

    public AiResampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputRate));
        InputRate = inputRate;
        OutputRate = outputRate;
        Factor = Decimator.FactorFor(inputRate);
        if (Factor > 1) _dec = new Decimator(Factor);
        int mid = inputRate / Factor;
        int g = Gcd(mid, outputRate);
        _a = mid / g;
        _b = outputRate / g;
        _invB = 1.0 / _b;
        _copy = _a == _b;
        _pre = _a > _b;
        _post = _a < _b;
        // 2 次の低域フィルター（RBJ。WdlResampler と同じ式）: 落とすときは入力のナイキストの FilterPos × 出力 / 入力、上げるときは出力のナイキストの FilterPos × 入力 / 出力
        double fpos = _pre ? FilterPos * _b / _a : FilterPos * _a / _b;
        double w = Math.PI * fpos, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * FilterQ), sc = 1 / (1 + alpha);
        _b1 = (1 - c) * sc;
        _b0 = _b2 = _b1 * 0.5;
        _a1 = -2 * c * sc;
        _a2 = (1 - alpha) * sc;
        // 1 段の低い音での群遅延（その段の周波数の数）= 分子の (b1 + 2 b2) / (b0 + b1 + b2) − 分母の (a1 + 2 a2) / (1 + a1 + a2)。2 段なので 2 倍。
        // 上げるときは出力の周波数でかけるので、補間する入力の数（× a / b）に直す。直線の補間そのものは、低い音を遅らせない
        double tau = 2 * ((_b1 + 2 * _b2) / (_b0 + _b1 + _b2) - (_a1 + 2 * _a2) / (1 + _a1 + _a2));
        _filterDelay = _copy ? 0 : _pre ? tau : tau * _a / _b;
        _out = new float[Block * (int)Math.Ceiling((double)_b / _a) + 4];
    }

    public int InputRate { get; }
    public int OutputRate { get; }
    /// <summary>先に落とす倍率（1 = 落とさない）</summary>
    public int Factor { get; }
    /// <summary>Reset のあとに受け取った入力の数（埋めた無音も含む）・出した数</summary>
    public long InputCount { get; private set; }
    public long OutputCount { get; private set; }

    /// <summary>
    /// 次に出す音の位置（Reset のあとの入力の番号。小数）。低域フィルターの遅れ（低い音での群遅延）と、落としてから補間するときは
    /// 落とすフィルターの遅れ（線形位相なので、どの高さの音も同じ）を引いた位置（その音に入っている音の、元の位置）
    /// </summary>
    public double NextOutputPos
    {
        get
        {
            double p = _ip + _num * _invB - _filterDelay;
            return _dec is null ? p : p * Factor + (Factor - 1) - _dec.DelaySamples;
        }
    }

    /// <summary>次に出す音の時刻（＝出した音のいちばん新しいものの、次の音の時刻）。Sync の前は NaN</summary>
    public double EndT => _t0 + NextOutputPos / InputRate;

    /// <summary>
    /// これから Push する音の最初の時刻（取り込みが付けた時刻）を知らせる。戻り値:
    /// −1 = 前の音の続きではない（初めて・MaxJumpSec 秒より飛んだ・戻った）ので、中の音を捨てて最初からにした（呼んだ側もリングを空にする）。
    /// 0 以上 = Push の前に PushSilence で埋める無音の数（FillSec 秒より先に飛んだ = その間の音が届かなかった）。
    /// どちらでも、時刻はこの時刻に合わせ直す（小さなずれは積み重ならない）
    /// </summary>
    public long Sync(double tFirst)
    {
        if (!double.IsFinite(tFirst)) return 0;
        double expect = _t0 + InputCount / (double)InputRate;
        double d = tFirst - expect;
        if (double.IsNaN(d) || Math.Abs(d) > MaxJumpSec)
        {
            Reset();
            _t0 = tFirst;
            return -1;
        }
        long fill = d > FillSec ? (long)Math.Round(d * InputRate) : 0;
        _t0 = tFirst - (InputCount + fill) / (double)InputRate;
        return fill;
    }

    /// <summary>無音を n 個流し込む（届かなかった間を埋める）</summary>
    public void PushSilence(long n, SamplesSink sink)
    {
        while (n > 0)
        {
            int k = (int)Math.Min(n, Block);
            Push(Zeros.AsSpan(0, k), sink);
            n -= k;
        }
    }

    /// <summary>入力を流し込み、出てきた音を sink に渡す（数や値が数でない音は 0 にする）</summary>
    public void Push(ReadOnlySpan<float> input, SamplesSink sink)
    {
        InputCount += input.Length;
        var dec = _dec;
        var buf = _in;
        int n = 0;
        foreach (float v in input)
        {
            float x = float.IsFinite(v) ? v : 0;
            if (dec is not null && !dec.Push(x, out x)) continue;
            buf[n++] = x;
            if (n == Block)
            {
                Process(n, sink);
                n = 0;
            }
        }
        if (n > 0) Process(n, sink);
    }

    /// <summary>中に残っている音・フィルターの状態・時刻を捨てる（設定はそのまま。new しない）</summary>
    public void Reset()
    {
        _dec?.Reset();
        _ip = 0;
        _num = 0;
        _fed = 0;
        _last = 0;
        Array.Clear(_h);
        InputCount = 0;
        OutputCount = 0;
        _t0 = double.NaN;
    }

    /// <summary>_in の n 個（補間する周波数の音）をフィルターし、出せるだけ補間して sink に渡す</summary>
    private void Process(int n, SamplesSink sink)
    {
        var x = _in;
        var o = _out;
        int m = 0;
        if (_copy)
        {
            Array.Copy(x, o, n);
            m = n;
            _fed += n;
            _ip = _fed;
        }
        else
        {
            if (_pre)
            {
                Biquad(x, n, 0);
                Biquad(x, n, 4);
            }
            long baseIdx = _fed;
            long end = baseIdx + n;
            int a = _a, b = _b;
            while (_ip + 1 < end)
            {
                long li = _ip - baseIdx;
                float l = li < 0 ? _last : x[li];
                float r = x[li + 1];
                double f = _num * _invB;
                o[m++] = (float)(l * (1.0 - f) + r * f);
                _num += a;
                if (_num >= b)
                {
                    _ip += _num / b;
                    _num %= b;
                }
            }
            _last = x[n - 1];
            _fed = end;
            if (_post && m > 0)
            {
                Biquad(o, m, 0);
                Biquad(o, m, 4);
            }
        }
        if (m == 0) return;
        OutputCount += m;
        sink(o.AsSpan(0, m));
    }

    /// <summary>2 次の低域フィルターを、その場で 1 段かける（h = 状態の置き場所の先頭）</summary>
    private void Biquad(float[] s, int n, int h)
    {
        double b0 = _b0, b1 = _b1, b2 = _b2, a1 = _a1, a2 = _a2;
        double x1 = _h[h], x2 = _h[h + 1], y1 = _h[h + 2], y2 = _h[h + 3];
        for (int i = 0; i < n; i++)
        {
            double x = s[i];
            double y = x * b0 + x1 * b1 + x2 * b2 - y1 * a1 - y2 * a2;
            x2 = x1;
            x1 = x;
            y2 = y1;
            y1 = y;
            s[i] = (float)y;
        }
        _h[h] = x1;
        _h[h + 1] = x2;
        _h[h + 2] = y1;
        _h[h + 3] = y2;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}

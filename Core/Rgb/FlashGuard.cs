// ==================================================
// FlashGuard.cs - 「激しい点滅を抑える」がオンのとき、デバイス・ゾーンごとに、強い点滅を 1 秒に 3 回までにする（最後の守り）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 送る値（0〜1）で、デバイス全体・ゾーンごと・長いデバイスの 4 分の 1 ずつの平均の明るさ（LED ごとの R・G・B の最大）を見て、
/// 強い点滅（0.1 秒以内に 0.2 以上明るくなる。続けて明るくなる間は 1 回）を、どの 1 秒の間でも 3 回までにする。
/// FlashLimiter はビートの光の合図だけを数えるので、低音の大きさ・スネア・ハイハットの光や、それらが重なったときは数えていなかった
/// （実際の曲では 1 秒に 4〜7 回になることがあった）。3 回を使い切っている間に明るくなるときは、その区域の LED を同じ割合で暗くして、
/// 0.1 秒の間に上がるのを 0.17 まで（前のコマからも同じ傾きまで）にする（色は変えない。速く明るくなる代わりに、なめらかに少しずつ明るくなる）。
/// 数えるのは送った値（Commit）で、テストの数え方とまったく同じ。暗くするかは光の量で決め、送る値の丸めの差（上がりで 0.006 まで）を見込む。
/// 描画のスレッドからだけ使う。配列は区域の数・LED の数が変わったときだけ作り直す（毎コマ new しない）
/// </summary>
public sealed class FlashGuard
{
    /// <summary>強い点滅とみなす上がり（0〜1）と、その上がりを見る時間（秒）</summary>
    public const float StrongRise = 0.2f;
    public const double RiseWindowSec = 0.1;
    /// <summary>1 秒に何回まで</summary>
    public const int MaxPerSecond = 3;
    /// <summary>使い切っているとき、0.1 秒の間に上げてよい上限</summary>
    public const float CapRise = 0.17f;
    /// <summary>
    /// QuarterMinLeds 個以上の LED のデバイスは、全体・ゾーンのほかに 4 分の 1 ずつの区域も見る（長いテープの一部だけが強く点滅するとき:
    /// スネアの粒・音量メーターやエナジーの帯の先など。録音 7 曲で、4 分の 1 の区域の強い点滅は 1 秒に最大 4〜6 回 → 3 回。
    /// 代わりにエナジーの「光った拍」が 63% → 57%、音量メーターが 62% → 59% に下がる）。
    /// オフにできるのは調べる用（アプリは変えない）
    /// </summary>
    internal static bool Quarters { get; set; } = true;
    public const int QuarterMinLeds = 8;

    // 暗くするか決める上がり（光の量で見る）: 強い点滅のしきい値 − 0.006。送る値は光の量から 0.75 段（OutputStage.HoldBand）までしか
    // ずれないので、平均の差は 0.003 まで・上がりの差は 0.006 まで（送った値で数えて点滅になるコマは、必ずここで見る）。
    // 数えるのは送った値そのもの（Commit）なので、数えすぎない
    private const float CountRise = StrongRise - 0.006f;
    // 時刻の誤差の余裕（秒）: 0.1 秒の窓にちょうど 0.1 秒前のコマを入れる・1 秒の窓には、ちょうど 1 秒前の点滅は入れない
    // （ちょうど 1 秒に 3 回の点滅（180 BPM の拍・FlashLimiter の間隔）は、そのまま通す）
    private const double WindowEps = 0.004, SecondEps = 1e-4;
    // 上がりがちょうど 0.2 のときの、計算の誤差の余裕
    private const float TieEps = 1e-4f;
    // 覚えておくコマの数（0.1 秒に入るコマより多く。600 コマ／秒まで）
    private const int Hist = 64;

    private int _areas, _quarters;
    private int[] _start = Array.Empty<int>(), _len = Array.Empty<int>();
    private float[] _m = Array.Empty<float>();          // 区域 × Hist: 送った平均
    private double[] _t = Array.Empty<double>();        // Hist: コマの時刻
    private int _head, _count;
    private bool[] _run = Array.Empty<bool>();          // 区域ごと: 前のコマで強い上がりが続いていた
    private double[] _starts = Array.Empty<double>();   // 区域 × MaxPerSecond: 最近の強い点滅の始まりの時刻
    private int[] _sn = Array.Empty<int>();
    private float[] _now = Array.Empty<float>();
    private double _lastT = double.NaN;
    private bool _pending, _store;

    /// <summary>暗くしたコマの数（テスト・調べる用）</summary>
    public int Capped { get; private set; }
    /// <summary>数えた強い点滅の数（テスト・調べる用）</summary>
    public int Counted { get; private set; }

    /// <summary>覚えを捨てる（オフにしたとき・時刻が戻ったとき）</summary>
    public void Reset()
    {
        _head = _count = 0;
        Array.Clear(_run);
        Array.Clear(_sn);
        _lastT = double.NaN;
        _pending = false;
    }

    /// <summary>
    /// 区域を決める: デバイス全体と、2 つ以上あればゾーン（まとまり）ごと。zones = (始まりの LED, LED の数)。
    /// 中身が同じなら何もしない（覚えを捨てない）
    /// </summary>
    public void Layout(int n, IReadOnlyList<(int Start, int Length, int Zone)> zones)
    {
        int nz = zones.Count > 1 ? zones.Count : 0;
        int nq = Quarters && n >= QuarterMinLeds ? 4 : 0;
        int areas = 1 + nz + nq;
        bool same = areas == _areas && _len.Length == areas && _len[0] == n && nq == _quarters;
        for (int k = 1; same && k <= nz; k++) same = _start[k] == zones[k - 1].Start && _len[k] == zones[k - 1].Length;
        if (same) return;
        _areas = areas;
        _quarters = nq;
        _start = new int[areas];
        _len = new int[areas];
        _len[0] = n;
        for (int k = 1; k <= nz; k++)
        {
            _start[k] = Math.Clamp(zones[k - 1].Start, 0, n);
            _len[k] = Math.Clamp(zones[k - 1].Length, 0, n - _start[k]);
        }
        // 長いデバイスは 4 分の 1 ずつも見る（音量メーター・エナジーの帯の先、スネアの粒など、一部だけが強く点滅するとき）
        for (int q = 0; q < nq; q++)
        {
            int s = q * n / 4, e = (q + 1) * n / 4;
            _start[1 + nz + q] = s;
            _len[1 + nz + q] = e - s;
        }
        _m = new float[areas * Hist];
        _t = new double[Hist];
        _run = new bool[areas];
        _starts = new double[areas * MaxPerSecond];
        _sn = new int[areas];
        _now = new float[areas];
        Reset();
    }

    /// <summary>
    /// 時刻 t（秒）のコマの光 light（n 個。送る値 = 光の量 × 255）を、必要なら暗くする。Layout を先に呼び、
    /// 送る値にしたあとで必ず Commit を呼ぶこと（数えるのは送った値で）
    /// </summary>
    public void Apply(LinColor[] light, int n, double t)
    {
        _pending = false;
        if (_areas == 0 || n != _len[0]) return;
        if (!double.IsNaN(_lastT) && t < _lastT) Reset();
        // 同じ時刻のコマをもう一度描いた: 同じように直すが、覚えには入れない
        _store = double.IsNaN(_lastT) || t > _lastT;
        _lastT = t;
        _pending = true;
        // 小さい区域（4 分の 1・ゾーン）を先に、デバイス全体（区域 0）を最後に直す（全体を暗くしても、区域の上がりは増えない）
        for (int a = _areas - 1; a >= 0; a--)
        {
            int len = _len[a];
            if (len <= 0) continue;
            float m = Mean(light, _start[a], len);
            float lo = Lowest(a, t, m);
            if (m - lo < CountRise || _run[a] || Recent(a, t) < MaxPerSecond || !(m > 1e-6f)) continue;
            // 3 回を使い切っている: 0.1 秒の間の上がりを CapRise までにする。前のコマからは、0.1 秒で CapRise の傾きまで
            // （0.1 秒前の暗さだけで抑えると、0.1 秒ごとに 0.17 ずつの段になり、段ごとに小さく光って見える）
            float cap = lo + CapRise;
            if (_count > 0 && t > _t[_head])
                cap = MathF.Min(cap, _m[a * Hist + _head] + CapRise * (float)Math.Min(1.0, (t - _t[_head]) / RiseWindowSec));
            float k = Math.Clamp(cap / m, 0f, 1f);
            int s = _start[a];
            // 1 を超える光は、送るときに成分ごとに 1 で切られるので、先に切ってから暗くする（切る前に掛けると、暗くした分が見えない）
            for (int i = s; i < s + len; i++)
            {
                var c = light[i];
                light[i] = new LinColor(Math.Clamp(c.R, 0f, 1f) * k, Math.Clamp(c.G, 0f, 1f) * k, Math.Clamp(c.B, 0f, 1f) * k);
            }
            Capped++;
        }
    }

    /// <summary>
    /// Apply のあと、そのコマで送る値 output で、強い点滅を数えて覚える（テストの数え方とまったく同じ: 平均が 0.1 秒前までのいちばん暗い値から
    /// 0.2 以上明るくなった始まり）
    /// </summary>
    public void Commit(Rgb[] output, int n)
    {
        if (!_pending || n != _len[0] || output.Length < n) return;
        _pending = false;
        if (!_store) return;
        double t = _lastT;
        for (int a = 0; a < _areas; a++)
        {
            int len = _len[a];
            float m = len > 0 ? Mean(output, _start[a], len) : 0f;
            // ちょうど 0.2 の上がり（平均は 1/255 の刻みなので、ちょうどになることがある）は、計算の誤差でどちらにもなりうるので、
            // 始まりは少し低め・続きは少し高めのしきい値で見る（ちょうどのときは多めに数える）
            float rise = m - Lowest(a, t, m);
            if (rise >= StrongRise - TieEps && !_run[a]) Remember(a, t);
            _run[a] = rise >= StrongRise + TieEps;
            _now[a] = m;
        }
        _head = (_head + 1) % Hist;
        _t[_head] = t;
        for (int a = 0; a < _areas; a++) _m[a * Hist + _head] = _now[a];
        if (_count < Hist) _count++;
    }

    private static float Mean(Rgb[] o, int s, int len)
    {
        int sum = 0;
        for (int i = s; i < s + len; i++) sum += Math.Max(o[i].R, Math.Max(o[i].G, o[i].B));
        return sum / (255f * len);
    }

    private static float Mean(LinColor[] light, int s, int len)
    {
        float sum = 0;
        for (int i = s; i < s + len; i++)
        {
            var c = light[i];
            float v = MathF.Max(c.R, MathF.Max(c.G, c.B));
            sum += !(v > 0) ? 0f : v >= 1 ? 1f : v;
        }
        return sum / len;
    }

    /// <summary>
    /// 前のコマまでの、0.1 秒以内でいちばん暗い平均（なければ今の値）。すぐ前のコマはいつも入れる
    /// （1 秒に 10 コマより少ないときも、コマとコマの間の跳びは一瞬で明るくなるのと同じ）
    /// </summary>
    private float Lowest(int a, double t, float now)
    {
        float lo = now;
        for (int j = 0, idx = _head; j < _count; j++, idx = (idx + Hist - 1) % Hist)
        {
            if (j > 0 && t - _t[idx] > RiseWindowSec + WindowEps) break;
            lo = MathF.Min(lo, _m[a * Hist + idx]);
        }
        return lo;
    }

    /// <summary>最近 1 秒の強い点滅の数</summary>
    private int Recent(int a, double t)
    {
        int n = 0;
        for (int j = 0; j < _sn[a]; j++)
            if (t - _starts[a * MaxPerSecond + j] < 1.0 - SecondEps) n++;
        return n;
    }

    private void Remember(int a, double t)
    {
        Counted++;
        int b = a * MaxPerSecond;
        if (_sn[a] < MaxPerSecond) { _starts[b + _sn[a]++] = t; return; }
        // いちばん古いものと入れ替える
        int old = 0;
        for (int j = 1; j < MaxPerSecond; j++) if (_starts[b + j] < _starts[b + old]) old = j;
        _starts[b + old] = t;
    }
}

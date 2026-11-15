// ==================================================
// OnsetDetector.cs - 音の立ち上がりの検出 (SuperFlux。キック・スネア・ハイハットを周波数の帯で見分け、
//                    「本当に鳴った時刻」つきで記録する。テンポの追跡に使う立ち上がりの強さも作る)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 流れ（約 10.7 ミリ秒ごと）:
///   1. FFT の結果（強さ）を、1 オクターブに 6 個の三角形の重み（30 Hz〜16 kHz、約 46 個）でまとめ、log にする
///      Y_i = log10(1 + Σ w·|X|)
///   2. 前回と比べて増えた分だけを足す（SuperFlux）。前回の値は「両隣を含めた最大値」と比べるので、
///      ビブラートのように周波数が少し揺れるだけの音では反応しない
///   3. 4 つの帯（30〜120 Hz・120〜500 Hz・500 Hz〜4 kHz・4〜16 kHz）ごとに合計し、直近 3 秒の平均で割る
///   4. 帯ごとと全体の「ピーク検出」: 直近 16 回の中央値 + 平均の半分 + δ（+ ノイズの多さの分）を超え、
///      上がっている途中なら検出
///   5. キックは、FFT を使わないキックの包絡線（KickPath）でも 4.5 dB 以上の立ち上がりを確かめ、
///      その立ち上がりの時刻（音 1 個の精度）を「本当に鳴った時刻」にする。
///      さらに打楽器らしさ（Percussiveness:「カチッ」か、いつもより目立つ低音）が低いものはキックにせず、
///      ベースの音の出だし（BassCandidate）にする（ベースのアルペジオが、1 秒に 3〜4 回キックと数えられていた）。
///      ほかの種類は、検出した回の最後の音の時刻から EventLatencySec 戻した時刻にする
///   6. ハイハット・スネアは、短い FFT（11 ミリ秒）と FFT を使わない包絡線（TransientPath）でも見て、
///      その回のうち（または回と回の間。ProbeBetweenHops）に確かめられた音は、すぐに出来事にする（時刻 = 包絡線の立ち上がりの中点）。
///      長い FFT（43 ミリ秒）で見つけるより約 20 ミリ秒早い。確かめられなかった音は、1〜5 の決め方で今までどおり見つける
///      （同じ音は 2 回出さない）
/// 取り込みスレッドからだけ使う
/// </summary>
public sealed class OnsetDetector
{
    private const double FilterLowHz = 30;
    private const double FilterHighHz = 16000;
    private const int FiltersPerOctave = 6;
    /// <summary>4 つの帯の境目（Hz）: G0 キック・G1 低い中音・G2 中音・G3 ハイハット</summary>
    private static readonly double[] GroupEdgesHz = { 30, 120, 500, 4000 };
    /// <summary>テンポの追跡に使う強さで、4 つの帯に掛ける重み（低音ほど重い）</summary>
    /// <remarks>実際の曲で合わせるため、テストから変えられるようにしてある（アプリの中では変えない）</remarks>
    internal static double[] OdfWeights = { 1.0, 0.8, 0.35, 0.15 };
    private static double OdfWeightSum => OdfWeights[0] + OdfWeights[1] + OdfWeights[2] + OdfWeights[3];
    /// <summary>帯ごとの平均（強さをそろえる基準）の時定数（秒）</summary>
    private const double MeanSec = 3.0;
    /// <summary>
    /// 平均に足す小さな値（log10 の単位の合計）。音がほとんどない帯で、計算の誤差のような小さな揺れを
    /// 何倍にも拡大して「立ち上がり」と見なさないため（本物のドラムはこの数十倍以上）
    /// </summary>
    private const double MeanFloor = 0.02;
    /// <summary>ピーク検出が見る直近の長さ（秒。48 kHz で 16 回）</summary>
    private const double PickerHistorySec = 16 * 512 / 48000.0;
    /// <summary>同じ帯で次に検出するまでの最短の間隔（秒）。ハイハットは細かく刻むので短い</summary>
    private const double GapSec = 0.107, HatGapSec = 0.064;
    /// <summary>スネアとみなす強さの下限（しきい値の何倍か）</summary>
    private const double SnareMinStrength = 1.2;

    /// <summary>スネアの見分け方の値（実際の曲で合わせるため、テストから変えられるようにしてある。アプリの中では変えない）</summary>
    internal static class SnareTune
    {
        /// <summary>スネア用の立ち上がり検出の、しきい値に足す「ふだんの値」の倍率</summary>
        internal static double NoiseWeight = 8;
        /// <summary>スネアとみなす強さの下限（しきい値の何倍か）</summary>
        internal static double MinStrength = SnareMinStrength;
        /// <summary>キックと同時でもスネアにする強さ（0 = キックと同時ならスネアにしない）</summary>
        internal static double KickOverride = 0;
        /// <summary>ハイハットと同時でも、中音がハイハットの帯のこの倍以上ならスネアにする（0 = 低い中音がなければハイハットの漏れ）</summary>
        internal static double HatLeakRatio = 0;
        /// <summary>低い中音で検出していないときに必要な、高音の帯の立ち上がりの強さ（0 = 見ない）</summary>
        internal static double HiMin = 0;
        /// <summary>
        /// スネアの候補を見つけてから決めるまでに待つコマ数（キックが少し遅れて見つかるのを待つ）。
        /// 録音した 19 曲で比べると、1 コマ待ちは 2 コマ待ちと同じくらい正確で（2・4 拍目に合う 37.8% / 37.4%、
        /// 1・3 拍目に出る間違い 11.9% / 11.0%）、2・4 拍目を見つける割合は増えた（28% / 25%）。待たないと、遅れて見つかる
        /// キックをスネアと取り違える間違いが増える（1・3 拍目 16%）ので、1 コマだけ待つ
        /// （以前は 2 コマ。1 コマ待つごとに、スネアの光が約 11 ミリ秒遅れる）
        /// </summary>
        internal static int HoldHops = 1;
        /// <summary>
        /// キック・ハイハットかどうかがその回で分かれば、待たずに決める（CanDecideSnareNow）。
        /// キックは FFT を使わない包絡線で、上がっている途中かを確かめる（上がっていれば待つ）
        /// </summary>
        internal static bool EarlyDecide = true;
    }
    /// <summary>光らせる出来事の大きさの見分け方（テストから変えられるようにしてある。アプリの中では変えない）</summary>
    internal static class EventTune
    {
        /// <summary>
        /// ハイハット・スネア・立ち上がりの出来事にする、立ち上がりの大きさの下限（「反応の感度」1.0 のとき）。
        /// 大きさ = その帯の立ち上がり（log の差の合計） ÷ いちばん大きい帯の直近 3 秒の平均。
        /// 帯ごとの平均で割った強さだけで決めると、ほかの音がない帯（低音の大きい曲の高音など）では
        /// 小さな音も大きな音と同じように光っていた。合成音で測ると、ふつうのハイハット 約 5・6 dB 小さい 約 3.6・
        /// 14 dB 小さい 約 1.3〜1.8・20 dB 小さい 0.7〜0.9・スネア 8〜16。1.5 で、20 dB 小さい音はまったく・14 dB 小さい音も
        /// ほとんど光らず、ふつうのハイハット・スネアは今までどおり見つかる（FaintSoundTests）。
        /// 「反応の感度」を上げると下限が下がる（キックとテンポの追跡には使わない）
        /// </summary>
        internal static double MinSize = 1.5;
        /// <summary>
        /// ハイハット・立ち上がりを見つけてから、大きさを決めるまでに待つ回数（1 回 ≈ 11 ミリ秒）。
        /// 見つけた回は立ち上がりの途中なので、その回だけでは大きな音も小さく見える
        /// </summary>
        internal static int HoldHops = 1;
        /// <summary>
        /// 遅れを減らす決め方を使うか（短い FFT・包絡線の時刻・大きさが足りていれば待たない・スネアをその回で決める）。
        /// false にすると 6.15.7 と同じ決め方になる（テストで前後を比べるため。アプリの中では変えない）
        /// </summary>
        internal static bool LowLatency = true;
        /// <summary>回と回の間（音が届くたび）にもハイハット・スネアを確かめるか（ProbeBetweenHops。テストで前後を比べるため）</summary>
        internal static bool Probe = true;
    }

    /// <summary>キックの見分け方（テストから変えられるようにしてある。アプリの中では変えない）</summary>
    internal static class KickTune
    {
        /// <summary>
        /// 打楽器らしさ（Percussiveness）が低い低音の立ち上がりは、キックにしない（ベースの音の出だし）。
        /// false にすると、今までどおり低音の立ち上がりと包絡線だけで決める（テストで前後を比べるため）
        /// </summary>
        internal static bool PercGate = true;
        /// <summary>キックとみなす打楽器らしさの下限（0〜1）</summary>
        internal static double MinPercussive = KickMinPercussive;
    }

    /// <summary>
    /// キックとみなす打楽器らしさの下限（Percussiveness）。
    /// 合成音で測ると、キックは 0.59 以上（「カチッ」のないキックは 1 割ほどがこれより下）、ベースの音の出だしは 0.3 以下
    /// </summary>
    public const double KickMinPercussive = 0.5;
    /// <summary>キックの始まりで「カチッ」（1〜5 kHz の包絡線の立ち上がり）を見る範囲: 始まりの前・後（秒）</summary>
    private const double ClickBeforeSec = 0.010, ClickAfterSec = 0.006;

    /// <summary>
    /// キックの打楽器らしさ（0〜1）。キックの始まりの時点で分かることだけで決める（光を遅らせない）。
    /// 2048 点の FFT の打楽器と持続音の分け方（InstrumentFeatures）は、キックを見つけた回にはまだキックが窓の端にしか入っておらず
    /// （合成音で、キックの回でも持続音より小さく見えることが多い）、ここでは使わない。次の 2 つの大きいほう:
    ///   ・「カチッ」: 1〜5 kHz の包絡線が、キックの始まりと同時に上がった（clickRiseDb。2 dB で 0・4 dB で 1）、
    ///     かつ、その大きさがキックの包絡線から大きく下でない（clickRelDb。−34 dB で 0・−29 dB で 1）。
    ///     ベースの音の出だしは高い帯にほとんど音が出ない（合成音で、上がり 2.3 dB 以下・キックの包絡線から −36〜−56 dB）。
    ///     ハイハットと同時のベースの音は、上がっても小さすぎる
    ///   ・目立ち: キックの包絡線の山が、いつもの大きさ（512 個ごとの最大値の中央値）より大きく上（prominenceDb。6 dB で 0・10 dB で 1）。
    ///     「カチッ」のないキック（808 のような音）のため。続けて鳴るベースの音は、いつもの大きさから 7 dB 以内
    /// どれも NaN なら NaN（見ない）
    /// </summary>
    internal static double Percussiveness(double clickRiseDb, double clickRelDb, double prominenceDb)
    {
        static double Ramp(double x, double lo, double hi) => Math.Clamp((x - lo) / (hi - lo), 0, 1);
        double click = double.IsFinite(clickRiseDb) && double.IsFinite(clickRelDb) ? Math.Min(Ramp(clickRiseDb, 2, 4), Ramp(clickRelDb, -34, -29)) : double.NaN;
        double prom = double.IsFinite(prominenceDb) ? Ramp(prominenceDb, 6, 10) : double.NaN;
        if (double.IsNaN(click)) return prom;
        return double.IsNaN(prom) ? click : Math.Max(click, prom);
    }
    /// <summary>最後に確かめたキックの候補の打楽器らしさ（0〜1。テスト・調査用）</summary>
    internal double LastKickPercussiveness { get; private set; } = double.NaN;
    /// <summary>調査用: キックの候補（包絡線でも確かめられた）を見つけたとき（時刻・目立ち dB・「カチッ」の上がり dB・「カチッ」のキックからの大きさ dB）</summary>
    internal Action<double, double, double, double>? DiagKickCandidate { get; set; }

    /// <summary>
    /// 見つけたハイハット・立ち上がりの大きさを確かめてから出来事にする。
    /// 見つけた回のうちに大きさが足りていれば、すぐに出来事にする（待たない）。
    /// 足りなければ EventTune.HoldHops 回まで待って、立ち上がりの続きで決める（見つけた回は立ち上がりの途中なので、
    /// 大きな音も小さく見えることがある）。以前は大きさが足りていても必ず 1 回待っていた（光が約 11 ミリ秒遅れていた）
    /// </summary>
    private sealed class PendingEvent
    {
        private long _hop = -1;
        private double _t;
        private float _strength;
        private double _size;

        /// <summary>最後に出来事にした時刻（まだなければ負の無限大）</summary>
        public double LastT { get; private set; } = double.NegativeInfinity;

        public void Clear()
        {
            _hop = -1;
            LastT = double.NegativeInfinity;
        }

        /// <param name="sameAs">この時刻の出来事（短い FFT で先に出したもの）から DuplicateSec 以内なら、同じ音なので出さない</param>
        public void Step(bool fired, long hop, double t, float strength, double size, double minSize, AudioEventKind kind, EventRing events, double sameAs)
        {
            if (fired && _hop < 0)
            {
                _hop = hop;
                _t = t;
                _strength = strength;
                _size = 0;
            }
            if (_hop < 0) return;
            if (size > _size) _size = size;
            bool due = hop >= _hop + EventTune.HoldHops;
            if (!due && !(EventTune.LowLatency && _size >= minSize)) return;
            if (_size >= minSize && Math.Abs(_t - sameAs) > DuplicateSec)
            {
                events.Add(kind, _t, _strength);
                LastT = _t;
            }
            _hop = -1;
        }
    }

    private readonly PendingEvent _hatPend = new(), _onsetPend = new();

    /// <summary>キックと同時（± この秒数）ならスネアにしない</summary>
    private const double SnareKickWindowSec = 0.020;
    /// <summary>
    /// 検出した回の最後の音の時刻から、本当に鳴った時刻までの差（秒。キック以外）。
    /// FFT の窓（ハン窓）は新しい端がほぼ 0 なので、音が窓に入ってきてから少したって検出される。
    /// A6 の試作値は 15 ミリ秒だが、合成音で測ると、静かな背景では最初の回（平均 5 ミリ秒後）、
    /// 音の多い背景では 1〜2 回後に検出されるので、その間の 10 ミリ秒にした（テスト TestKickEventTiming）
    /// </summary>
    public const double EventLatencySec = 0.010;
    /// <summary>曲全体の音量（dB。直近 3 秒の 2 乗平均。NaN なら比べない。AudioEngine が回ごとに設定する）</summary>
    public double MixDb { get; set; } = double.NaN;
    /// <summary>短い FFT で見つけた音を、その回のうちに確かめるのに必要な、包絡線の立ち上がり（dB）</summary>
    private const double FastRiseDb = 6;
    /// <summary>短い FFT のピーク検出より 1 回早く出すときに必要な、包絡線の立ち上がり（dB）</summary>
    private const double EarlyRiseDb = 6;
    /// <summary>早く出すハイハットに必要な大きさ: 包絡線の山が、曲全体の音量（MixDb）からこの dB 以内（「反応の感度」で下がる）</summary>
    private const double HatMinVsMixDb = -16;
    /// <summary>1 回早く出すときに必要な、短い FFT の立ち上がり（しきい値の何倍か）</summary>
    private const double EarlyShare = 0.25;
    /// <summary>1 回早く出すのは、包絡線の立ち上がりがこの回の中（新しい端からこの秒数以内）にあるときだけ</summary>
    private const double EarlyMaxAgoSec = 0.012;
    /// <summary>スネアの胴の音の包絡線に必要な立ち上がり（dB。胴の音はゆっくり立ち上がるので、響き線の音より小さくする）</summary>
    private const double BodyRiseDb = 4;
    /// <summary>スネアを早く出すのは、響き線の音が立ち上がってからこの秒数以上たってから（キックでないことを確かめるため）</summary>
    private const double SnareMinAgeSec = 0.004;
    /// <summary>スネアの響き線の音と胴の音の立ち上がりが、これ以内なら同じ 1 つの音（秒）</summary>
    private const double FastBodyWindowSec = 0.008;
    /// <summary>包絡線で決めた時刻を、検出した回の最後の音からどこまで前にしてよいか（秒。約 3 回分）</summary>
    private const double TransientMaxAgoSec = 0.032;
    /// <summary>短い FFT と長い FFT で、同じ種類の出来事の時刻がこれ以内なら同じ音（2 回出さない。秒）</summary>
    private const double DuplicateSec = 0.040;
    /// <summary>キックとみなすのに必要な、キックの包絡線の立ち上がり（dB。ノイズの揺れと見分ける）</summary>
    public const double KickMinRiseDb = 4.5;
    /// <summary>キックとみなす大きさの下限（最近のキックの山から何 dB 下まで。スネアの胴の音と見分ける）</summary>
    public const double KickMaxBelowRecentDb = 10;
    /// <summary>反応の強さ（門 × アプリの割合）がこれ未満の間は、出来事を記録しない</summary>
    public const double MinActivity = 0.3;

    /// <summary>
    /// 三角形のフィルターの並び（1 オクターブに 6 個。FFT の番号の重み）。
    /// minCentreHz より低い中心のフィルターは作らない（短い FFT では、中音より上だけを使う）
    /// </summary>
    private sealed class FilterBank
    {
        public readonly int N;
        public readonly int[] First, Count, Off, Group;
        public readonly double[] W;

        public FilterBank(int sampleRate, int fftSize, double minCentreHz)
        {
            double binHz = (double)sampleRate / fftSize;
            int maxBin = fftSize / 2;

            // 中心の周波数 30·2^(i/6) を FFT の番号にして、同じ番号になったものは 1 つにまとめる
            var bins = new List<int>();
            int edgeCount = (int)Math.Ceiling(FiltersPerOctave * Math.Log2(FilterHighHz / FilterLowHz)) + 2;
            for (int i = -1; i <= edgeCount; i++)
            {
                double f = FilterLowHz * Math.Pow(2, i / (double)FiltersPerOctave);
                int b = Math.Clamp((int)Math.Round(f / binHz), 1, maxBin);
                if (bins.Count == 0 || bins[^1] != b) bins.Add(b);
                if (f > FilterHighHz) break;
            }
            // フィルター j: 下の端 bins[j−1] → 頂点 bins[j] → 上の端 bins[j+1] の三角形（重みの合計を 1 にする）
            var first = new List<int>();
            var count = new List<int>();
            var off = new List<int>();
            var group = new List<int>();
            var w = new List<double>();
            for (int j = 1; j + 1 < bins.Count; j++)
            {
                int lo = bins[j - 1], mid = bins[j], hi = bins[j + 1];
                double centreHz = mid * binHz;
                if (centreHz < FilterLowHz * 0.9 || centreHz > FilterHighHz * 1.05) continue;
                if (centreHz < minCentreHz) continue;
                int start = lo, stop = hi;
                double sum = 0;
                int o = w.Count;
                for (int b = start; b <= stop; b++)
                {
                    double v;
                    if (b <= mid) v = mid == lo ? 1 : (double)(b - lo) / (mid - lo);
                    else v = hi == mid ? 0 : (double)(hi - b) / (hi - mid);
                    // 端の番号（重み 0）も 1 つは含め、どのフィルターも少なくとも頂点の番号を見る
                    w.Add(v);
                    sum += v;
                }
                if (sum <= 0) continue;
                for (int i = o; i < w.Count; i++) w[i] /= sum;
                first.Add(start);
                count.Add(stop - start + 1);
                off.Add(o);
                int g = 0;
                for (int e = 1; e < GroupEdgesHz.Length; e++) if (centreHz >= GroupEdgesHz[e]) g = e;
                group.Add(g);
            }
            N = first.Count;
            First = first.ToArray();
            Count = count.ToArray();
            Off = off.ToArray();
            Group = group.ToArray();
            W = w.ToArray();
        }

        /// <summary>log にしたフィルターの出力 Y_i = log10(1 + Σ w·|X|)</summary>
        public void Apply(double[] mag, double[] y)
        {
            for (int i = 0; i < N; i++)
            {
                int o = Off[i], f0 = First[i], n = Count[i];
                double sum = 0;
                for (int k = 0; k < n; k++) sum += W[o + k] * mag[f0 + k];
                y[i] = Math.Log10(1 + sum);
            }
        }

        /// <summary>SuperFlux: prev（μ 回前。両隣を含めた最大値）より増えた分だけを帯ごとに band へ足す</summary>
        public void Flux(double[] y, double[] prev, double[] band)
        {
            band[0] = band[1] = band[2] = band[3] = 0;
            for (int i = 0; i < N; i++)
            {
                double m = prev[i];
                if (i > 0 && prev[i - 1] > m) m = prev[i - 1];
                if (i + 1 < N && prev[i + 1] > m) m = prev[i + 1];
                double d = y[i] - m;
                if (d > 0) band[Group[i]] += d;
            }
        }
    }

    private readonly int _sampleRate;
    private readonly FilterBank _bank;
    private readonly int _nf;                      // 三角形のフィルターの数
    private readonly double[] _y;                  // 今回の Y
    private readonly double[][] _yHist;            // 過去の Y（μ 回前と比べる）
    private readonly int _lag;                     // μ（何回前と比べるか）
    private int _yHead;
    private long _frames;
    private readonly double[] _band = new double[4];
    private readonly WarmEma[] _mean = new WarmEma[4];
    private readonly double _aMean;

    /// <summary>調査用: 帯 g の直近 3 秒の平均（立ち上がりの強さ）</summary>
    internal double BandMean(int g) => _mean[g].V;
    private readonly PeakPicker[] _pickers = new PeakPicker[4];
    private readonly PeakPicker _combined;
    private readonly int _snareWindow;
    private long _lastKickHop = long.MinValue / 4;
    private long _lastHatHop = long.MinValue / 4;
    private long _snareHop = -1;
    private bool _snareG1;
    /// <summary>決めるまでの間に、中音の立ち上がりが小さすぎない大きさになったか（EventTune.MinSize）</summary>
    private bool _snareBig;
    private double _snareT;
    private float _snareS;
    private double _snareMid, _snareHi;
    /// <summary>スネアの候補を見つけたコマの、キックの帯の立ち上がり・ハイハット・低い中音（早めに決められるかの調査用）</summary>
    private double _snareKickBandFirst;
    private bool _snareHatFirst, _snareG1First;
    private readonly PeakPicker _snarePick1, _snarePick2;

    /// <param name="sampleRate">サンプリング周波数（Hz）</param>
    /// <param name="fftSize">使う FFT の大きさ（48 kHz で 2048。窓の長さが約 43 ミリ秒になるように選ぶ）</param>
    /// <param name="frameRate">1 秒あたりの分析の回数（サンプリング周波数 / 512）</param>
    /// <param name="fastFftSize">ハイハット・スネアの響き線を見る短い FFT の大きさ（48 kHz で 512。0 なら使わず、今までどおり fftSize だけで見る）</param>
    public OnsetDetector(int sampleRate, int fftSize, double frameRate, int fastFftSize = 0)
    {
        _sampleRate = sampleRate;
        _bank = new FilterBank(sampleRate, fftSize, 0);
        _nf = _bank.N;
        _y = new double[_nf];
        // μ = 1 回（512 個）前と比べる。96 kHz では 1 回が半分の長さなので 2 回前と比べる（同じ時間の差になる）
        _lag = Math.Max(1, (int)Math.Round(frameRate / 93.75));
        _yHist = new double[_lag][];
        for (int i = 0; i < _lag; i++) _yHist[i] = new double[_nf];
        if (fastFftSize > 0)
        {
            // 短い FFT（48 kHz で 512 個 = 11 ミリ秒）の立ち上がり: 500 Hz 以上（G2・G3）だけ。スネア・ハイハットを早く見つけるのに使う。
            // 長い FFT（43 ミリ秒）は窓の新しい端がほぼ 0 なので、音が窓の真ん中に来るまで（約 2 回）立ち上がりが小さい
            _fast = new FilterBank(sampleRate, fastFftSize, GroupEdgesHz[2]);
            _yF = new double[_fast.N];
            _yP = new double[_fast.N];
            _yHistF = new double[_lag][];
            for (int i = 0; i < _lag; i++) _yHistF[i] = new double[_fast.N];
        }
        else
        {
            _yF = Array.Empty<double>();
            _yHistF = Array.Empty<double[]>();
        }

        _aMean = TimeConst.Alpha(1 / frameRate, MeanSec);
        int hist = Math.Max(4, (int)Math.Round(PickerHistorySec * frameRate));
        int gap = Math.Max(1, (int)Math.Round(GapSec * frameRate));
        int hatGap = Math.Max(1, (int)Math.Round(HatGapSec * frameRate));
        _pickers[0] = new PeakPicker(hist, gap, frameRate);
        _pickers[1] = new PeakPicker(hist, gap, frameRate);
        _pickers[2] = new PeakPicker(hist, gap, frameRate);
        _pickers[3] = new PeakPicker(hist, hatGap, frameRate);
        _combined = new PeakPicker(hist, gap, frameRate);
        _snarePick1 = new PeakPicker(hist, gap, frameRate, SnareTune.NoiseWeight);
        _snarePick2 = new PeakPicker(hist, gap, frameRate, SnareTune.NoiseWeight);
        _hatFast = new PeakPicker(hist, hatGap, frameRate);
        _snareFast2 = new PeakPicker(hist, gap, frameRate, SnareTune.NoiseWeight);
        _snareWindow = Math.Max(1, (int)Math.Round(SnareKickWindowSec * frameRate));
    }

    // ===== 短い FFT（ハイハット・スネアの中音）
    private readonly FilterBank? _fast;
    private readonly double[] _yF;
    private readonly double[][] _yHistF;
    private readonly double[] _bandF = new double[4];
    private readonly double[] _bandNormF = new double[4];
    private readonly WarmEma[] _meanF = new WarmEma[4];
    private readonly PeakPicker _hatFast, _snareFast2;
    /// <summary>短い FFT で出したハイハット・スネアの時刻と、長い FFT で出したスネアの時刻（同じ音を 2 回出さない）</summary>
    private double _fastHatT = double.NegativeInfinity, _fastSnareT = double.NegativeInfinity, _lastSnareT = double.NegativeInfinity;
    /// <summary>回と回の間に確かめるときの、短い FFT の Y と帯ごとの立ち上がり（状態は変えない）</summary>
    private readonly double[] _yP = Array.Empty<double>();
    private readonly double[] _bandP = new double[4];
    /// <summary>最後に処理した回の番号（まだなければ −1）</summary>
    private long _hopIndex = -1;

    /// <summary>三角形のフィルターの数（約 46 個）</summary>
    public int FilterCount => _nf;

    // ===== 結果（Process のたびに上書き）

    /// <summary>テンポの追跡に使う立ち上がりの強さ（4 つの帯を重みつきで平均したもの。ふだんは 1 前後）</summary>
    public double Odf { get; private set; }
    /// <summary>低音の立ち上がりの強さ（bn0 + 0.8·bn1。テンポが倍・半分かの判定に使う）</summary>
    public double Low { get; private set; }
    /// <summary>キックの帯（30〜120 Hz）の立ち上がりの合計（平均で割る前。盛り上がりの判定に使う）</summary>
    public double KickFlux => _band[0];
    /// <summary>今回、キックを検出した（キックの帯で検出し、キックの包絡線でも確かめられた。反応の強さが足りないときは false）</summary>
    public bool KickFired { get; private set; }
    /// <summary>今回、低音の立ち上がりを見つけたが、打楽器らしくないのでキックにしなかった（ベースの音の出だし。反応の強さが足りないときは false）</summary>
    public bool BassCandidate { get; private set; }
    /// <summary>その立ち上がりの時刻（BassCandidate のときだけ意味がある）</summary>
    public double BassCandidateTime { get; private set; }
    /// <summary>今回、キックか低い中音（120〜500 Hz）の帯で検出した（テンポの「音が続いているか」に使う）</summary>
    public bool LowFired { get; private set; }
    /// <summary>今回検出した立ち上がりのうち、いちばん強いもの（しきい値の何倍か。なければ 0）</summary>
    public double StrongestStrength { get; private set; }
    /// <summary>今回キックを検出したときの強さ（なければ 0）</summary>
    public double KickStrength { get; private set; }
    /// <summary>今回のキックの本当の時刻（KickFired のときだけ意味がある）</summary>
    public double KickTime { get; private set; }
    /// <summary>帯ごとの、平均で割った立ち上がりの強さ（テスト・調査用）</summary>
    public double GroupNorm(int g) => _bandNorm[g];
    /// <summary>帯ごとのピーク検出（0〜3 = 帯、4 = 全体。テスト・調査用）</summary>
    public PeakPicker Picker(int g) => g < 4 ? _pickers[g] : _combined;
    private readonly double[] _bandNorm = new double[4];
    /// <summary>調査用: スネアの候補を決めたとき（時刻・強さ・低い中音でも検出したか・キックと同時か・ハイハットの漏れとみなしたか）</summary>
    internal Action<double, float, bool, bool, bool>? DiagSnare { get; set; }
    /// <summary>調査用: スネアの候補を決めたとき（時刻, 見つけたコマのキックの帯, そのコマでハイハットも, そのコマで低い中音も, キックが近い, ハイハットだけ, キックのコマ − 候補のコマ）</summary>
    internal Action<double, double, bool, bool, bool, bool, long>? DiagSnareFirst { get; set; }
    /// <summary>調査用: 1 回ごとの時刻・立ち上がりの強さ・4 つの帯の強さ（配列は使い回すので、受け取った側で写すこと）</summary>
    internal Action<double, double, double[]>? DiagHop { get; set; }

    public void Reset()
    {
        for (int i = 0; i < _lag; i++) Array.Clear(_yHist[i]);
        _yHead = 0;
        _frames = 0;
        for (int g = 0; g < 4; g++)
        {
            _mean[g].Reset();
            _pickers[g].Reset();
            _band[g] = _bandNorm[g] = 0;
        }
        _combined.Reset();
        _snarePick1.Reset();
        _snarePick2.Reset();
        for (int i = 0; i < _yHistF.Length; i++) Array.Clear(_yHistF[i]);
        for (int g = 0; g < 4; g++)
        {
            _meanF[g].Reset();
            _bandF[g] = _bandNormF[g] = 0;
        }
        _hatFast.Reset();
        _snareFast2.Reset();
        _fastHatT = _fastSnareT = _lastSnareT = double.NegativeInfinity;
        _hopIndex = -1;
        _lastKickHop = long.MinValue / 4;
        _lastHatHop = long.MinValue / 4;
        _snareHop = -1;
        _hatPend.Clear();
        _onsetPend.Clear();
        Odf = Low = StrongestStrength = KickStrength = 0;
        KickFired = LowFired = BassCandidate = false;
    }

    /// <summary>
    /// 1 回分を処理する。
    /// mag = FFT の各番号の強さ |X|（0〜N/2 番。正規化しない値。フルスケールのサイン波が約 512 になるように）
    /// </summary>
    /// <param name="hop">何回目の分析か（0 から数える）</param>
    /// <param name="tHop">今回のいちばん新しい音の時刻（AudioClock の秒）</param>
    /// <param name="events">出来事の書き込み先（null なら書かない）</param>
    /// <param name="kick">キックの包絡線（null なら確かめずに、時刻も EventLatencySec で求める）</param>
    /// <param name="magFast">短い FFT の強さ（null ならハイハット・スネアも mag で見る。目盛りは mag と同じ「フルスケールのサイン波 ≈ 512」）</param>
    /// <param name="hatEnv">ハイハットの包絡線</param>
    /// <param name="snareEnv">スネアの響き線の包絡線</param>
    /// <param name="bodyEnv">スネアの胴の音の包絡線（magFast と包絡線がそろったときだけ、その回のうちに確かめて出す）</param>
    public void Process(double[] mag, AudioEngineSettings s, double activity, bool gateOpen, long hop, double tHop, EventRing? events, KickPath? kick = null,
        double[]? magFast = null, TransientPath? hatEnv = null, TransientPath? snareEnv = null, TransientPath? bodyEnv = null)
    {
        // 1. log にしたフィルターの出力
        _bank.Apply(mag, _y);
        bool fast = EventTune.LowLatency && _fast is not null && magFast is not null;
        if (fast) _fast!.Apply(magFast!, _yF);

        // 2. SuperFlux: μ 回前の値（両隣を含めた最大値）より増えた分だけを帯ごとに足す
        double[] prev = _yHist[_yHead];   // ちょうど μ 回前に書いた値
        _band[0] = _band[1] = _band[2] = _band[3] = 0;
        _bandF[0] = _bandF[1] = _bandF[2] = _bandF[3] = 0;
        if (_frames >= _lag)
        {
            _bank.Flux(_y, prev, _band);
            if (fast) _fast!.Flux(_yF, _yHistF[_yHead], _bandF);
        }
        Array.Copy(_y, prev, _nf);
        if (fast) Array.Copy(_yF, _yHistF[_yHead], _yF.Length);
        _yHead = (_yHead + 1) % _lag;
        _frames++;

        // 3. 帯ごとの直近 3 秒の平均で割る（曲の音量や音色が違っても、同じしきい値で比べられる）
        double odf = 0;
        for (int g = 0; g < 4; g++)
        {
            if (gateOpen) _mean[g].Step(_band[g], _aMean);
            double bn = _band[g] / (_mean[g].V + MeanFloor);
            _bandNorm[g] = bn;
            odf += OdfWeights[g] * bn;
        }
        odf /= OdfWeightSum;
        Odf = odf;
        Low = _bandNorm[0] + 0.8 * _bandNorm[1];
        DiagHop?.Invoke(tHop, odf, _bandNorm);
        // 短い FFT の帯も、それぞれの直近 3 秒の平均で割る（テンポの追跡には使わない）
        if (fast)
        {
            for (int g = 1; g < 4; g++)
            {
                if (gateOpen) _meanF[g].Step(_bandF[g], _aMean);
                _bandNormF[g] = _bandF[g] / (_meanF[g].V + MeanFloor);
            }
        }

        // 4. ピーク検出（感度が高いほど δ が小さい）
        double delta = Math.Clamp(0.8 / Math.Max(0.1, s.BeatSensitivity), 0.25, 2.0);
        bool kickFired = _pickers[0].Step(_bandNorm[0], delta, hop);
        bool g1 = _pickers[1].Step(_bandNorm[1], delta, hop);
        bool g2 = _pickers[2].Step(_bandNorm[2], delta, hop);
        bool hat = _pickers[3].Step(_bandNorm[3], delta, hop);
        // スネア用（しきい値の「ふだんの値」の倍率だけ別にできる。キック・テンポの追跡に使う検出は変えない）
        bool s1 = _snarePick1.Step(_bandNorm[1], delta, hop);
        bool s2 = _snarePick2.Step(_bandNorm[2], delta, hop);
        bool onset = _combined.Step(odf, delta, hop);
        // 短い FFT（ハイハット・スネアの響き線）。長い FFT より約 1〜2 回早く立ち上がりが見える
        bool hatFast = fast && _hatFast.Step(_bandNormF[3], delta, hop);
        bool snareFast = fast && _snareFast2.Step(_bandNormF[2], delta, hop);

        // キックは、FFT を使わない包絡線でもはっきり立ち上がっているかを確かめ、その時刻を使う
        double tKick = tHop - EventLatencySec;
        bool isKick = kickFired;
        bool bassCand = false;
        double bassCandT = double.NaN;
        if (kickFired && kick is not null)
        {
            // 「最近のキックの大きさ」は、打楽器らしさも確かめてから覚える（ベースの音を覚えないように）
            isKick = kick.TryFindOnset(KickMinRiseDb, KickMaxBelowRecentDb, false, out int ago, out _);
            if (isKick)
            {
                // 打楽器らしさが低い（「カチッ」もなく目立ちもしない = ベースの音の出だし）ときは、キックにしない
                double clickPeakDb = double.NaN;
                double click = snareEnv is null ? double.NaN : snareEnv.RiseAt(ago, ClickBeforeSec, ClickAfterSec, out clickPeakDb);
                double clickRel = clickPeakDb - kick.FoundPeakDb;
                double prominence = kick.FoundPeakDb - kick.MedianHopDb;
                double perc = Percussiveness(click, clickRel, prominence);
                LastKickPercussiveness = perc;
                DiagKickCandidate?.Invoke(tHop - ago / (double)_sampleRate, prominence, click, clickRel);
                if (KickTune.PercGate && double.IsFinite(perc) && perc < KickTune.MinPercussive)
                {
                    isKick = false;
                    bassCand = true;
                    bassCandT = tHop - ago / (double)_sampleRate;
                }
            }
            if (isKick)
            {
                kick.CommitOnset();
                tKick = tHop - ago / (double)_sampleRate;
            }
            // キックでなかったときは、次のキックまでの最短の間隔を使わなかったことにする（すぐ後の本物を逃さない）
            else _pickers[0].Cancel();
        }

        // 小さすぎる音は出来事にしない（EventTune.MinSize）。キック・テンポの追跡に使う値はそのまま。
        // 大きさ = 帯の立ち上がり ÷ いちばん大きい帯の直近の平均
        double loudest = MeanFloor;
        for (int g = 0; g < 4; g++)
            if (_mean[g].V > loudest) loudest = _mean[g].V;
        double minSize = EventTune.MinSize / Math.Max(0.1, s.BeatSensitivity);
        double hatSize = _band[3] / loudest;
        double snareSize = Math.Max(_band[1], _band[2]) / loudest;
        // 短い FFT の立ち上がりは、長い FFT の目盛りに直してから比べる（直近 3 秒の平均の比を掛ける）。
        // 下限 MinSize は長い FFT の立ち上がりで合わせてあるため（小さな音で光りすぎない。FaintSoundTests）
        double hatSizeF = fast ? _bandF[3] * (_mean[3].V + MeanFloor) / (_meanF[3].V + MeanFloor) / loudest : 0;
        double snareSizeF = fast ? _bandF[2] * (_mean[2].V + MeanFloor) / (_meanF[2].V + MeanFloor) / loudest : 0;
        double anySize = Math.Max(Math.Max(_band[0], _band[1]), Math.Max(_band[2], _band[3])) / loudest;

        bool emit = activity >= MinActivity && events is not null;
        KickFired = isKick && emit;
        BassCandidate = bassCand && emit;
        BassCandidateTime = bassCandT;
        LowFired = (isKick || g1) && emit;
        KickStrength = KickFired ? _pickers[0].Strength : 0;
        double strongest = 0;
        if (isKick) strongest = Math.Max(strongest, _pickers[0].Strength);
        if (onset) strongest = Math.Max(strongest, _combined.Strength);
        StrongestStrength = emit ? strongest : 0;
        KickTime = tKick;

        double tTrue = tHop - EventLatencySec;
        if (isKick) _lastKickHop = hop;
        if (hat) _lastHatHop = hop;
        if (!emit)
        {
            _snareHop = -1;
            _hatPend.Clear();
            _onsetPend.Clear();
            _fastHatT = _fastSnareT = _lastSnareT = double.NegativeInfinity;
            return;
        }
        if (isKick) events!.Add(AudioEventKind.Kick, tKick, (float)_pickers[0].Strength);

        // 4b. 短い FFT と包絡線で、その回のうちに確かめられたハイハット・スネアは、すぐに出来事にする（FastHit）。
        //     確かめられなかった音は、今までどおり長い FFT で見つける（同じ音を 2 回出さない。DuplicateSec）
        //     短い FFT のピーク検出がまだ出ていなくても（音が窓の新しい端にあって、立ち上がりがまだ半分しか見えない）、
        //     包絡線がこの回のうちに大きく（EarlyRiseDb 以上）立ち上がっていて、立ち上がりがしきい値の EarlyShare 以上なら出す（1 回早くなる）
        double minS = SnareTune.MinStrength;
        if (fast)
        {
            if (hatFast) TryFastHat(_bandNormF[3], hatSizeF, minSize, hatEnv, tHop, FastRiseDb, TransientMaxAgoSec, events!);
            else TryFastHat(_bandNormF[3], hatSizeF, minSize, hatEnv, tHop, EarlyRiseDb, EarlyMaxAgoSec, events!);
            if (snareFast)
            {
                if (_snareFast2.Strength >= minS) TryFastSnare(_bandNormF[2], snareSizeF, minSize, kick, snareEnv, bodyEnv, hop, tHop, FastRiseDb, TransientMaxAgoSec, events!);
            }
            else TryFastSnare(_bandNormF[2], snareSizeF, minSize, kick, snareEnv, bodyEnv, hop, tHop, EarlyRiseDb, EarlyMaxAgoSec, events!);
        }
        _hopIndex = hop;

        // ハイハット・立ち上がり: 大きさが足りていれば見つけた回に、足りなければ EventTune.HoldHops 回までの
        // いちばん大きい立ち上がりで決める
        _hatPend.Step(hat, hop, tTrue, (float)_pickers[3].Strength, hatSize, minSize, AudioEventKind.Hat, events!, _fastHatT);
        _onsetPend.Step(onset, hop, tTrue, (float)_combined.Strength, anySize, minSize, AudioEventKind.Onset, events!, double.NegativeInfinity);

        // 5. スネア: 中音の帯で検出し、直前 20 ミリ秒（約 2 回）にキックがなければスネアとする。
        //    キック・ハイハットかどうかがその回で分かれば、その回のうちに決める（SnareTune.EarlyDecide）。
        //    分からなければ 1 コマだけ待って決める（キックはほとんどその時までに見つかっている。待つほど光が遅れる。SnareTune.HoldHops）。
        //    500 Hz〜4 kHz（G2）だけで検出し、同時にハイハットも検出したときは、ハイハットの音の漏れとみなす
        //    （スネアは胴の音が 120〜500 Hz（G1）にも出る。仕様 A6 にない追加）
        double snareS = 0;
        if (s1 && _snarePick1.Strength >= minS) snareS = _snarePick1.Strength;
        if (s2 && _snarePick2.Strength >= minS) snareS = Math.Max(snareS, _snarePick2.Strength);
        if (snareS > 0)
        {
            if (_snareHop < 0)
            {
                _snareHop = hop;
                _snareT = tTrue;
                _snareS = (float)snareS;
                _snareG1 = false;
                _snareBig = false;
                _snareMid = _snareHi = 0;
                _snareKickBandFirst = _bandNorm[0];
                _snareHatFirst = hat;
                _snareG1First = s1 && _snarePick1.Strength >= minS;
            }
            else _snareS = Math.Max(_snareS, (float)snareS);
            if (s1 && _snarePick1.Strength >= minS) _snareG1 = true;
        }
        if (_snareHop >= 0)
        {
            // 決めるまでの間の、中音とハイハットの帯のいちばん強い立ち上がり（ハイハットの漏れか、スネアの響き線かを見分ける）
            _snareMid = Math.Max(_snareMid, _bandNorm[2]);
            _snareHi = Math.Max(_snareHi, _bandNorm[3]);
            if (snareSize >= minSize) _snareBig = true;
        }
        bool due = _snareHop >= 0 && hop >= _snareHop + Math.Min(SnareTune.HoldHops, _snareWindow);
        if (_snareHop >= 0 && (due || (EventTune.LowLatency && SnareTune.EarlyDecide && CanDecideSnareNow(kick))))
        {
            bool kickNear = _lastKickHop >= _snareHop - _snareWindow;
            // キックと同時でも、中音がとても強ければスネア（2・4 拍目でキックと重なるクラップなど）
            if (kickNear && SnareTune.KickOverride > 0 && _snareS >= SnareTune.KickOverride) kickNear = false;
            bool hatOnly = !_snareG1 && _lastHatHop >= _snareHop - _snareWindow;
            // ハイハットと同時でも、中音がハイハットの帯と同じくらい強ければスネア（響き線は高音にも出る。ハイハットの漏れなら中音は弱い）
            if (hatOnly && SnareTune.HatLeakRatio > 0 && _snareMid >= SnareTune.HatLeakRatio * _snareHi) hatOnly = false;
            // 中音だけの立ち上がり（低い中音も高音も出ていない）は、ボーカルやギターとみなす（スネアは胴の音か響き線の音が一緒に出る）
            if (SnareTune.HiMin > 0 && !_snareG1 && _snareHi < SnareTune.HiMin) hatOnly = true;
            DiagSnare?.Invoke(_snareT, _snareS, _snareG1, kickNear, hatOnly);
            DiagSnareFirst?.Invoke(_snareT, _snareKickBandFirst, _snareHatFirst, _snareG1First, kickNear, hatOnly, _lastKickHop - _snareHop);
            // 短い FFT で先に出したスネアと同じ音なら出さない
            if (!kickNear && !hatOnly && _snareBig && Math.Abs(_snareT - _fastSnareT) > DuplicateSec)
            {
                events!.Add(AudioEventKind.Snare, _snareT, _snareS);
                _lastSnareT = _snareT;
            }
            _snareHop = -1;
        }
    }

    /// <summary>
    /// 短い FFT の立ち上がり bn（平均で割った値）と大きさ size のハイハットを、包絡線で確かめて出す。
    /// bn がしきい値の EarlyShare 以上・大きさが minSize 以上・包絡線が minRiseDb 以上立ち上がっていて、
    /// その立ち上がりが新しい端から maxAgoSec 以内なら出す（ピーク検出が出た回は maxAgoSec を長く、出ていない回は短くする）。
    /// 時刻 = 包絡線の立ち上がりの中点。長い FFT で出したハイハットと同じ音（DuplicateSec 以内）なら出さない
    /// </summary>
    private void TryFastHat(double bn, double size, double minSize, TransientPath? env, double tNow, double minRiseDb, double maxAgoSec, EventRing events)
    {
        double thr = Math.Max(1e-9, _hatFast.Threshold);
        if (bn < EarlyShare * thr || size < minSize || env is null) return;
        if (!env.TryFindOnset(minRiseDb, out int ago, out _) || ago > maxAgoSec * _sampleRate) return;
        // 曲全体の音量より HatMinVsMixDb 以上小さいハイハットは、ここでは出さない（小さな「チッ」で光りすぎない。
        // 「反応の感度」を上げると下限が下がる）。出さなかった音も、今までどおり長い FFT で見つけられる
        double sens = EventTune.MinSize / Math.Max(1e-9, minSize);
        if (double.IsFinite(MixDb) && env.RecentPeakDb() < MixDb + HatMinVsMixDb - 20 * Math.Log10(Math.Max(0.1, sens))) return;
        double t = tNow - Math.Min(ago / (double)_sampleRate, TransientMaxAgoSec);
        if (Math.Abs(t - _fastHatT) <= DuplicateSec || Math.Abs(t - _hatPend.LastT) <= DuplicateSec) return;
        events.Add(AudioEventKind.Hat, t, (float)Math.Max(1, bn / thr));
        _fastHatT = t;
    }

    /// <summary>短い FFT のスネアの候補を、包絡線（響き線の音・胴の音・キック）で確かめて出す（TryFastHat と同じ決め方 + FastSnareBody）</summary>
    private void TryFastSnare(double bn, double size, double minSize, KickPath? kick, TransientPath? noise, TransientPath? body, long hop, double tNow,
        double minRiseDb, double maxAgoSec, EventRing events)
    {
        double thr = Math.Max(1e-9, _snareFast2.Threshold);
        if (bn < EarlyShare * thr || size < minSize) return;
        if (!FastSnareBody(kick, noise, body, hop, tNow, minRiseDb, maxAgoSec, out double t)) return;
        if (Math.Abs(t - _fastSnareT) <= DuplicateSec || Math.Abs(t - _lastSnareT) <= DuplicateSec) return;
        events.Add(AudioEventKind.Snare, t, (float)Math.Max(SnareTune.MinStrength, bn / thr));
        _fastSnareT = t;
    }

    /// <summary>
    /// 分析の回と回の間（音が届くたび。約 10 ミリ秒ごと）に、直近 512 個の短い FFT と包絡線で、ハイハット・スネアを確かめて出す。
    /// 次の回まで待たないので、回の途中で鳴った音の光が平均 5 ミリ秒ほど早くなる。
    /// 立ち上がりは、最後の回の短い FFT との差（SuperFlux と同じ）。しきい値・平均は最後の回のまま（状態は変えない）。
    /// 次の回で同じ音を見つけても、時刻が DuplicateSec 以内なので 2 回は出さない
    /// </summary>
    /// <param name="magFast">直近 512 個（48 kHz のとき）の FFT の強さ（Process の magFast と同じ目盛り）</param>
    /// <param name="tNow">いちばん新しい音の時刻</param>
    public void ProbeBetweenHops(double[] magFast, AudioEngineSettings s, double activity, double tNow, EventRing? events,
        KickPath? kick, TransientPath? hatEnv, TransientPath? snareEnv, TransientPath? bodyEnv)
    {
        if (!EventTune.LowLatency || _fast is null || events is null || activity < MinActivity || _frames <= _lag || _hopIndex < 0) return;
        _fast.Apply(magFast, _yP);
        _fast.Flux(_yP, _yHistF[(_yHead + _lag - 1) % _lag], _bandP);
        double loudest = MeanFloor;
        for (int g = 0; g < 4; g++)
            if (_mean[g].V > loudest) loudest = _mean[g].V;
        double minSize = EventTune.MinSize / Math.Max(0.1, s.BeatSensitivity);
        double bn3 = _bandP[3] / (_meanF[3].V + MeanFloor), bn2 = _bandP[2] / (_meanF[2].V + MeanFloor);
        double size3 = bn3 * (_mean[3].V + MeanFloor) / loudest, size2 = bn2 * (_mean[2].V + MeanFloor) / loudest;
        TryFastHat(bn3, size3, minSize, hatEnv, tNow, EarlyRiseDb, EarlyMaxAgoSec, events);
        TryFastSnare(bn2, size2, minSize, kick, snareEnv, bodyEnv, _hopIndex, tNow, EarlyRiseDb, EarlyMaxAgoSec, events);
    }

    /// <summary>
    /// 短い FFT で見つけたスネアの候補を、その回のうちにスネアと確かめる（FFT を使わない包絡線で）。
    ///   ・響き線の音（1〜5 kHz）と胴の音（150〜400 Hz）が、どちらも FastRiseDb 以上立ち上がっていて、
    ///     2 つの立ち上がりの時刻が FastBodyWindowSec 以内（同じ 1 つの音）
    ///   ・キックが近くで見つかっていない・キックの包絡線が上がっている途中でもない
    ///   ・響き線の音が立ち上がってから SnareMinAgeSec 以上たっている（キックの「カチッ」も響き線の帯に出るので、
    ///     キックの包絡線（150 Hz 以下・2 ミリ秒で追従）が上がるのを待ってから、キックでないことを確かめる）
    /// 胴の音がない音（ハイハット・クラップなど）は、ここではスネアにしない（今までどおり長い FFT で決める）。
    /// t = 響き線の音の立ち上がりの時刻
    /// </summary>
    private bool FastSnareBody(KickPath? kick, TransientPath? noise, TransientPath? body, long hop, double tHop, double minRiseDb, double maxAgoSec, out double t)
    {
        t = double.NaN;
        if (kick is null || noise is null || body is null) return false;
        if (_lastKickHop >= hop - _snareWindow) return false;
        if (kick.IsRising(SnareKickWindowSec, KickMinRiseDb, KickMaxBelowRecentDb)) return false;
        if (!noise.TryFindOnset(minRiseDb, out int noiseAgo, out _) || noiseAgo > maxAgoSec * _sampleRate) return false;
        if (noiseAgo < SnareMinAgeSec * _sampleRate) return false;
        if (!body.TryFindOnset(BodyRiseDb, out int bodyAgo, out _)) return false;
        if (Math.Abs(noiseAgo - bodyAgo) > FastBodyWindowSec * _sampleRate) return false;
        t = tHop - Math.Min(noiseAgo / (double)_sampleRate, TransientMaxAgoSec);
        return true;
    }

    /// <summary>
    /// スネアの候補を、待たずにその回のうちに決めてよいか（決めた結果は、待ったときと同じになるはずのときだけ true）。
    ///   ・キックがもう近くで見つかっている → スネアにしない、と決められる
    ///   ・キックの包絡線が上がっている途中（FFT のキックの検出は 1〜2 回遅れる）→ 待つ
    ///   ・低い中音（胴の音）でも見つけていて、大きさも足りている → スネア
    ///   ・中音だけで、ハイハットも見つかっている → ハイハットの漏れ、と決められる
    ///   ・それ以外（中音だけでハイハットがまだない・大きさが足りない）→ 待つ
    /// 見分け方の追加の値（KickOverride・HatLeakRatio・HiMin）を使うときは、待ってからの値で決める
    /// </summary>
    private bool CanDecideSnareNow(KickPath? kick)
    {
        if (_lastKickHop >= _snareHop - _snareWindow) return SnareTune.KickOverride <= 0;
        if (kick is null || kick.IsRising(SnareKickWindowSec, KickMinRiseDb, KickMaxBelowRecentDb)) return false;
        if (_snareG1) return _snareBig;
        if (_lastHatHop >= _snareHop - _snareWindow) return SnareTune.HatLeakRatio <= 0 && SnareTune.HiMin <= 0;
        return false;
    }
}

/// <summary>
/// ピーク検出（しきい値が自動で変わる）。
///   しきい値 = 直近 n 回の中央値 + 直近 n 回の平均 × 0.5 + δ + 20 × ふだんの値（今回の値は含めない）
///   検出 = 値 > しきい値、かつ前回以上（上がっている途中）、かつ前回の検出から最短の間隔以上
/// 上がっている途中で検出するので、山の頂上より 1 回早く知らせられる。
///
/// 「ふだんの値」（長い目で見た中央値）は仕様 A6 にない追加:
///   ドラムの曲では、立ち上がりの強さはほとんどの回で 0 に近く、ドラムの瞬間だけ大きい → ふだんの値 ≈ 0（仕様どおり）
///   ノイズ（雨音・拍手・ホワイトノイズのような音）では、毎回それなりの値が出る → ふだんの値 ≈ 0.4〜0.7。
///   ノイズの揺れは平均の 5〜9 倍に届くことがあり、仕様のしきい値（平均の約 2 倍）では何度も誤検出するので、
///   ふだんの値に比例してしきい値を上げる
/// </summary>
public sealed class PeakPicker
{
    /// <summary>しきい値に足す「ふだんの値」の倍率</summary>
    public const double NoiseWeight = 20;
    /// <summary>「ふだんの値」を追いかける速さ（1 秒あたり。正規化した強さの単位）</summary>
    private const double NoiseTrackPerSec = 0.5;

    private readonly double[] _h;
    private readonly double[] _scratch;
    private readonly int _gap;
    private readonly double _eta;
    private int _count, _head;
    private double _prev;
    private long _last, _lastBefore;
    private double _level;
    private readonly double _noiseWeight;

    /// <param name="history">直近何回を見るか</param>
    /// <param name="gap">次に検出するまでの最短の回数</param>
    /// <param name="frameRate">1 秒あたりの回数</param>
    /// <param name="noiseWeight">しきい値に足す「ふだんの値」の倍率（null なら NoiseWeight）</param>
    public PeakPicker(int history, int gap, double frameRate = 93.75, double? noiseWeight = null)
    {
        _h = new double[history];
        _scratch = new double[history];
        _gap = gap;
        _eta = NoiseTrackPerSec / frameRate;
        _noiseWeight = noiseWeight ?? NoiseWeight;
        Reset();
    }

    /// <summary>ふだんの値（長い目で見た中央値の見積もり）</summary>
    public double Level => _level;

    /// <summary>今回の検出を取り消す（ほかの確かめで違うと分かったとき。次の検出までの間隔を使わなかったことにする）</summary>
    public void Cancel()
    {
        _last = _lastBefore;
        Strength = 0;
    }

    /// <summary>今回のしきい値</summary>
    public double Threshold { get; private set; }
    /// <summary>今回検出したときの強さ（値 / しきい値。1 以上）</summary>
    public double Strength { get; private set; }

    public void Reset()
    {
        _count = _head = 0;
        _prev = 0;
        _last = _lastBefore = long.MinValue / 4;
        _level = 0;
        Threshold = Strength = 0;
    }

    public bool Step(double x, double delta, long n)
    {
        double sum = 0;
        for (int i = 0; i < _count; i++)
        {
            _scratch[i] = _h[i];
            sum += _h[i];
        }
        double median = SmallStats.MedianInPlace(_scratch, _count);
        double mean = _count > 0 ? sum / _count : 0;
        double thr = median + 0.5 * mean + delta + _noiseWeight * _level;
        Threshold = thr;
        bool fire = x > thr && x >= _prev && n - _last >= _gap;
        Strength = fire ? x / thr : 0;
        if (fire)
        {
            _lastBefore = _last;
            _last = n;
        }
        // ふだんの値: 上回れば少し上げ、下回れば少し下げる（これを続けると中央値に落ち着く）
        _level += x > _level ? _eta : -_eta;
        if (_level < 0) _level = 0;
        _h[_head] = x;
        _head = (_head + 1) % _h.Length;
        if (_count < _h.Length) _count++;
        _prev = x;
        return fire;
    }
}

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
///      ほかの種類は、検出した回の最後の音の時刻から EventLatencySec 戻した時刻にする
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
    }

    /// <summary>見つけたハイハット・立ち上がりを、大きさが分かるまで少し待ってから出来事にする</summary>
    private sealed class PendingEvent
    {
        private long _hop = -1;
        private double _t;
        private float _strength;
        private double _size;

        public void Clear() => _hop = -1;

        public void Step(bool fired, long hop, double t, float strength, double size, double minSize, AudioEventKind kind, EventRing events)
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
            if (hop < _hop + EventTune.HoldHops) return;
            if (_size >= minSize) events.Add(kind, _t, _strength);
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
    /// <summary>キックとみなすのに必要な、キックの包絡線の立ち上がり（dB。ノイズの揺れと見分ける）</summary>
    public const double KickMinRiseDb = 4.5;
    /// <summary>キックとみなす大きさの下限（最近のキックの山から何 dB 下まで。スネアの胴の音と見分ける）</summary>
    public const double KickMaxBelowRecentDb = 10;
    /// <summary>反応の強さ（門 × アプリの割合）がこれ未満の間は、出来事を記録しない</summary>
    public const double MinActivity = 0.3;

    private readonly int _sampleRate;
    private readonly int _nf;                      // 三角形のフィルターの数
    private readonly int[] _fFirst, _fCount, _fOff, _fGroup;
    private readonly double[] _fW;
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
    public OnsetDetector(int sampleRate, int fftSize, double frameRate)
    {
        _sampleRate = sampleRate;
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
        _nf = first.Count;
        _fFirst = first.ToArray();
        _fCount = count.ToArray();
        _fOff = off.ToArray();
        _fGroup = group.ToArray();
        _fW = w.ToArray();
        _y = new double[_nf];
        // μ = 1 回（512 個）前と比べる。96 kHz では 1 回が半分の長さなので 2 回前と比べる（同じ時間の差になる）
        _lag = Math.Max(1, (int)Math.Round(frameRate / 93.75));
        _yHist = new double[_lag][];
        for (int i = 0; i < _lag; i++) _yHist[i] = new double[_nf];

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
        _snareWindow = Math.Max(1, (int)Math.Round(SnareKickWindowSec * frameRate));
    }

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
        _lastKickHop = long.MinValue / 4;
        _lastHatHop = long.MinValue / 4;
        _snareHop = -1;
        _hatPend.Clear();
        _onsetPend.Clear();
        Odf = Low = StrongestStrength = KickStrength = 0;
        KickFired = LowFired = false;
    }

    /// <summary>
    /// 1 回分を処理する。
    /// mag = FFT の各番号の強さ |X|（0〜N/2 番。正規化しない値。フルスケールのサイン波が約 512 になるように）
    /// </summary>
    /// <param name="hop">何回目の分析か（0 から数える）</param>
    /// <param name="tHop">今回のいちばん新しい音の時刻（AudioClock の秒）</param>
    /// <param name="events">出来事の書き込み先（null なら書かない）</param>
    /// <param name="kick">キックの包絡線（null なら確かめずに、時刻も EventLatencySec で求める）</param>
    public void Process(double[] mag, AudioEngineSettings s, double activity, bool gateOpen, long hop, double tHop, EventRing? events, KickPath? kick = null)
    {
        // 1. log にしたフィルターの出力
        for (int i = 0; i < _nf; i++)
        {
            int o = _fOff[i], f0 = _fFirst[i], n = _fCount[i];
            double sum = 0;
            for (int k = 0; k < n; k++) sum += _fW[o + k] * mag[f0 + k];
            _y[i] = Math.Log10(1 + sum);
        }

        // 2. SuperFlux: μ 回前の値（両隣を含めた最大値）より増えた分だけを帯ごとに足す
        double[] prev = _yHist[_yHead];   // ちょうど μ 回前に書いた値
        _band[0] = _band[1] = _band[2] = _band[3] = 0;
        if (_frames >= _lag)
        {
            for (int i = 0; i < _nf; i++)
            {
                double m = prev[i];
                if (i > 0 && prev[i - 1] > m) m = prev[i - 1];
                if (i + 1 < _nf && prev[i + 1] > m) m = prev[i + 1];
                double d = _y[i] - m;
                if (d > 0) _band[_fGroup[i]] += d;
            }
        }
        Array.Copy(_y, prev, _nf);
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

        // キックは、FFT を使わない包絡線でもはっきり立ち上がっているかを確かめ、その時刻を使う
        double tKick = tHop - EventLatencySec;
        bool isKick = kickFired;
        if (kickFired && kick is not null)
        {
            isKick = kick.TryFindOnset(KickMinRiseDb, KickMaxBelowRecentDb, out int ago, out _);
            if (isKick) tKick = tHop - ago / (double)_sampleRate;
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
        double anySize = Math.Max(Math.Max(_band[0], _band[1]), Math.Max(_band[2], _band[3])) / loudest;

        bool emit = activity >= MinActivity && events is not null;
        KickFired = isKick && emit;
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
            return;
        }
        if (isKick) events!.Add(AudioEventKind.Kick, tKick, (float)_pickers[0].Strength);
        // ハイハット・立ち上がり: 検出した回は立ち上がりの途中なので、EventTune.HoldHops 回のうちのいちばん大きい立ち上がりで決める
        _hatPend.Step(hat, hop, tTrue, (float)_pickers[3].Strength, hatSize, minSize, AudioEventKind.Hat, events!);
        _onsetPend.Step(onset, hop, tTrue, (float)_combined.Strength, anySize, minSize, AudioEventKind.Onset, events!);

        // 5. スネア: 中音の帯で検出し、直前 20 ミリ秒（約 2 回）にキックがなければスネアとする。
        //    1 コマだけ待って決める（キックはほとんどその時までに見つかっている。待つほど光が遅れる。SnareTune.HoldHops）。
        //    500 Hz〜4 kHz（G2）だけで検出し、同時にハイハットも検出したときは、ハイハットの音の漏れとみなす
        //    （スネアは胴の音が 120〜500 Hz（G1）にも出る。仕様 A6 にない追加）
        double minS = SnareTune.MinStrength;
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
        if (_snareHop >= 0 && hop >= _snareHop + Math.Min(SnareTune.HoldHops, _snareWindow))
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
            if (!kickNear && !hatOnly && _snareBig) events!.Add(AudioEventKind.Snare, _snareT, _snareS);
            _snareHop = -1;
        }
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

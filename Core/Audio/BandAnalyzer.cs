// ==================================================
// BandAnalyzer.cs - 周波数の帯ごとの強さ (一般的な 10 バンドのイコライザーと同じ 1 オクターブごとの 10 帯 31 Hz〜16 kHz と、
//                   判定に使う細かい 24 帯に分け、自動音量調整・自動バランス・平滑化・立ち上がりの強調をする) と、
//                   キック（バスドラム）の遅れの少ない検出
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Runtime.CompilerServices;

namespace ProFanController;

/// <summary>
/// 流れ（約 10.7 ミリ秒ごと）:
///   1. FFT の結果（周波数ごとの強さ）を、2 通りの帯にまとめる
///      ・見せる帯（Bands = 10）: 一般的な 10 バンドのグラフィックイコライザーと同じ、1 オクターブごとの 10 帯
///        （中心 31・63・125・250・500 Hz・1・2・4・8・16 kHz = ISO 266 のオクターブの中心。境目は中心 × √2。20 Hz〜20 kHz）。
///        画面のスペクトラムと、光り方「スペクトラム」に使う（Display・PunchOut）
///      ・判定に使う細かい帯（FineBands = 24。40 Hz〜16 kHz を対数で 24 等分）: 超低音・低音・中音・高音のまとめた帯、
///        自動音量調整の基準（CeilingDb）、聞こえ方の音量、音の重心に使う。
///        まとめた帯を 1 オクターブの帯から作ると、低音（3 本）の中でベースの音がキックを埋もれさせ、拍で光る割合が下がる
///        （合成した 10 曲で、ビートで色替え・パルスの光る拍が 81% → 65〜67%）。細かく分けて自動バランスをかけると、キックの成分が浮き出る
///      どちらも、低い帯は 2048 点の FFT（周波数を細かく見られる）、高い帯は 1024 点（時間に細かい）。
///      見せる帯は 2 kHz までが 2048 点・4 kHz からが 1024 点（中音・高音の帯の中で遅れをそろえる）
///   2. dB にして、高音ほど少し持ち上げる（傾き）。帯ごとの偏りを 8 秒の平均で少しならす（自動バランス）
///   3. 全体で 1 つの基準（いちばん大きい帯を追いかける）で 0〜1 にする（自動音量調整。見せる帯と細かい帯で別々の基準。
///      1 オクターブの帯は幅が広く、同じ音でも細かい帯より大きくなるため）。
///      帯ごとに別々にそろえないので、低音だけの曲は低音だけが光る（曲のバランスが残る）
///   4. 上がるのは速く、下がるのはゆっくり（反応の速さ・滑らかさの設定）
///   5. 鳴り続ける音より、急に大きくなった瞬間を強調した値（パンチ）も作る
/// 取り込みスレッドからだけ使う（結果の配列は次の分析で上書きされる）
/// </summary>
public sealed class BandAnalyzer
{
    // ===== 見せる帯（10 バンドのイコライザーと同じ）

    public const int Bands = AudioFrame.BandCount;
    /// <summary>いちばん低い帯の下端・いちばん高い帯の上端（Hz）</summary>
    public const double LowHz = 20;
    public const double HighHz = 20000;
    /// <summary>1 kHz の帯の番号。帯 k の中心は 1 kHz × 2^(k − 5)</summary>
    private const int Band1k = 5;
    /// <summary>この番号より下の帯は大きい FFT を使う（帯 6 = 2 kHz の上端 ≈ 2,828 Hz まで）</summary>
    public const int SplitBand = 7;
    /// <summary>帯の名前（10 バンドのイコライザーでふつうに使う呼び名。Hz）</summary>
    private static readonly string[] Labels = { "31", "63", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" }; // no-loc

    // ===== 判定に使う細かい帯（40 Hz〜16 kHz を対数で 24 等分）

    public const int FineBands = 24;
    public const double FineLowHz = 40;
    public const double FineHighHz = 16000;
    /// <summary>この番号より下の細かい帯は大きい FFT を使う（帯 15 の上端 ≈ 2,172 Hz）</summary>
    public const int FineSplitBand = 16;

    /// <summary>まとめた帯の数（0 = 超低音 Sub、1 = 低音 Bass、2 = 中音 Mid、3 = 高音 Treble）</summary>
    public const int GroupCount = 4;
    // 細かい帯の番号: 超低音 40〜85 Hz・低音 40〜230 Hz・中音 230〜2,200 Hz・高音 2.2〜16 kHz
    private static readonly int[] GroupFirst = { 0, 0, 7, 16 };
    private static readonly int[] GroupLast = { 2, 6, 15, 23 };

    /// <summary>自動音量調整の基準の最小値（dBFS）。これより静かな音を最大まで明るくしない</summary>
    public const double CeilingMinDb = -55;
    /// <summary>自動で狭める幅: 静かな側 10% の位置から余白（dB）、幅の下限（dB）、1 回で動かす量（dB。上へは 1 割、下へは 9 割）</summary>
    public const double AutoRangeMarginDb = 6;
    public const double AutoRangeMinDb = 14;
    public const double LowStepDb = 0.3;
    /// <summary>基準が大きい音に追いつく時定数（秒）</summary>
    private const double CeilingRiseSec = 0.30;
    /// <summary>基準が下がる速さ（dB／秒）</summary>
    private const double CeilingFallDbPerSec = 3.0;
    /// <summary>自動バランスの平均の時定数（秒）</summary>
    private const double EqSec = 8.0;
    /// <summary>自動バランスで下げる・上げる量の上限（dB）</summary>
    private const double EqMinDb = -6, EqMaxDb = 12;
    /// <summary>パンチの床（ふだんの大きさ）が上がる・下がる時定数（秒）</summary>
    private const double FloorUpSec = 1.5, FloorDownSec = 0.050;
    /// <summary>パンチが上がる・下がる時定数（秒）</summary>
    private const double PunchUpSec = 0.005, PunchDownSec = 0.100;
    /// <summary>聞こえ方の音量（VU メーター）の上がる・下がる時定数（秒）</summary>
    private const double LoudUpSec = 0.010, LoudDownSec = 0.300;
    /// <summary>
    /// 音量メーターの目盛り: 最近いちばん大きかった音量（基準）を 80% の位置に置き、そこから「音の幅」の 3 割（9〜24 dB。既定の 40 dB なら 12 dB）下までを 0〜80% にする。
    /// 基準を 100% にすると、音量が一定の曲（いまの音楽の多く）ではいつも満タンになり、メーターが動かないため
    /// </summary>
    public const double LoudCeilingAt = 0.80;
    /// <summary>音の重心（ムードの色）の時定数（秒）</summary>
    private const double CentroidSec = 1.5;
    /// <summary>log に 0 を入れないための小さな値（パワー。−120 dB）</summary>
    private const double PowerFloor = 1e-12;

    /// <summary>
    /// 見せる帯 k の下端の周波数（Hz）。隣の中心の間 = 1 kHz × 2^(k − 5.5)（44・88・177・354・707・1,414・2,828・5,657・11,314 Hz）。
    /// 帯 0 の下端は 20 Hz、帯 10（いちばん高い帯の上端）は 20 kHz
    /// </summary>
    public static double Edge(int k)
        => k <= 0 ? LowHz : k >= Bands ? HighHz : 1000 * Math.Pow(2, k - Band1k - 0.5);
    /// <summary>見せる帯 k の中心の周波数（Hz）。1 kHz × 2^(k − 5)（31.25・62.5・125・250・500・1,000・2,000・4,000・8,000・16,000 Hz）</summary>
    public static double Centre(int k) => 1000 * Math.Pow(2, k - Band1k);
    /// <summary>見せる帯 k の名前（31・63・…・16k。単位は Hz）</summary>
    public static string Label(int k) => Labels[k];

    /// <summary>細かい帯 k の下端の周波数（Hz）。e_k = 40·400^(k/24)（隣どうしの比 1.2836）</summary>
    public static double FineEdge(int k) => FineLowHz * Math.Pow(FineHighHz / FineLowHz, k / (double)FineBands);
    /// <summary>細かい帯 k の中心の周波数（Hz）。下端と上端の幾何平均</summary>
    public static double FineCentre(int k) => Math.Sqrt(FineEdge(k) * FineEdge(k + 1));

    /// <summary>
    /// 帯の並び: 帯ごとの FFT の番号の範囲と重み（重みは平らな 1 つの配列にまとめて並べる）・傾きの位置・聞こえ方の重み
    /// </summary>
    private sealed class Layout
    {
        public readonly int Count;
        /// <summary>この番号より下の帯は大きい FFT を使う</summary>
        public readonly int Split;
        private readonly int[] _binFirst, _binCount, _wOffset;
        private readonly double[] _w;
        public readonly double[] OctFrom1k, Log2Centre, KWeight;

        public Layout(int count, int split, Func<int, double> edge, Func<int, double> centre, int sampleRate, int nLow, int nHigh)
        {
            Count = count;
            Split = split;
            _binFirst = new int[count];
            _binCount = new int[count];
            _wOffset = new int[count];
            OctFrom1k = new double[count];
            Log2Centre = new double[count];
            KWeight = new double[count];
            var weights = new List<double>();
            for (int k = 0; k < count; k++)
            {
                int n = k < split ? nLow : nHigh;
                double binHz = (double)sampleRate / n;
                double lo = edge(k), hi = edge(k + 1);
                // FFT の番号 b は周波数 b·binHz を中心に幅 binHz を受け持つ。帯と重なっている割合を重みにする（端の番号は一部だけ）
                int first = Math.Max(1, (int)Math.Floor(lo / binHz - 0.5));
                int last = Math.Min(n / 2, (int)Math.Ceiling(hi / binHz + 0.5));
                _wOffset[k] = weights.Count;
                _binFirst[k] = first;
                int cnt = 0;
                for (int b = first; b <= last; b++)
                {
                    double f = b * binHz;
                    double overlap = Math.Min(f + binHz / 2, hi) - Math.Max(f - binHz / 2, lo);
                    weights.Add(Math.Max(0, overlap) / binHz);
                    cnt++;
                }
                _binCount[k] = Math.Max(0, cnt);
                double fc = centre(k);
                OctFrom1k[k] = Math.Log2(fc / 1000);
                Log2Centre[k] = Math.Log2(fc);
                // 聞こえ方の重み（K 特性の近似）: 低音を弱め（38 Hz 以下の 4 次）、高音を最大 +4 dB 持ち上げる
                double kwDb = 10 * Math.Log10(1 / (1 + Math.Pow(38 / fc, 4))) + 4 * fc * fc / (fc * fc + 1500.0 * 1500.0);
                KWeight[k] = Math.Pow(10, kwDb / 10);
            }
            _w = weights.ToArray();
        }

        /// <summary>帯 k の |X|² の重みつきの合計（正規化の前）</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Sum(int k, double[] powLow, double[] powHigh)
        {
            double[] pw = k < Split ? powLow : powHigh;
            int off = _wOffset[k], first = _binFirst[k], cnt = _binCount[k];
            double sum = 0;
            for (int i = 0; i < cnt; i++) sum += _w[off + i] * pw[first + i];
            return sum;
        }

        public int BinCount(int k) => _binCount[k];
    }

    private readonly double _dt;
    private readonly Layout _bands;
    private readonly Layout _fine;
    private readonly double _normLow, _normHigh;

    // 毎回使い回す作業用の配列
    private readonly double[] _leq = new double[Bands];
    private readonly double[] _scratch = new double[Bands];
    private readonly double[] _fLeq = new double[FineBands];
    private readonly double[] _fScratch = new double[FineBands];

    // 状態（見せる帯）
    private readonly WarmEma[] _ref = new WarmEma[Bands];
    private CeilingTracker _bandCeil;
    private readonly AsymEnv[] _disp = new AsymEnv[Bands];
    private readonly PunchStream[] _punch = new PunchStream[Bands];
    // 帯ごと・まとめた帯ごとの「静かな側 10% の位置」（明るさに対応させる幅を曲に合わせる）
    private readonly LowTracker[] _low = new LowTracker[Bands];
    // 状態（細かい帯とまとめた帯）
    private readonly WarmEma[] _fRef = new WarmEma[FineBands];
    private CeilingTracker _ceil;
    private CeilingTracker _loudCeil;
    private readonly AsymEnv[] _gDisp = new AsymEnv[GroupCount];
    private readonly PunchStream[] _gPunch = new PunchStream[GroupCount];
    private readonly LowTracker[] _gLow = new LowTracker[GroupCount];
    private AsymEnv _loudDb;
    private AsymEnv _loudVu;
    private AsymEnv _centroid;

    // 設定から求めた割合（設定が変わったときだけ計算し直す）
    private AudioEngineSettings? _coefFor;
    private double _aAtt, _aRel;
    private PunchCoefs _pc;
    private readonly double _aCeil, _ceilFall, _aEq, _aLoudUp, _aLoudDown, _aCentroid;

    /// <param name="sampleRate">サンプリング周波数（Hz）</param>
    /// <param name="nLow">低い帯に使う FFT の大きさ（48 kHz で 2048）</param>
    /// <param name="nHigh">高い帯に使う FFT の大きさ（48 kHz で 1024）</param>
    /// <param name="dt">分析の間隔（秒。512 / サンプリング周波数）</param>
    public BandAnalyzer(int sampleRate, int nLow, int nHigh, double dt)
    {
        _dt = dt;
        // パワーの正規化: P = 2|X|² / (N·Σw²)、Σw² = 3N/8（周期的なハン窓）。
        // こうすると、帯のパワーの合計が「その帯の音の 2 乗平均」になり、FFT の大きさが違っても同じ dB で比べられる
        _normLow = 2.0 / (nLow * (3.0 * nLow / 8));
        _normHigh = 2.0 / (nHigh * (3.0 * nHigh / 8));
        _bands = new Layout(Bands, SplitBand, Edge, Centre, sampleRate, nLow, nHigh);
        _fine = new Layout(FineBands, FineSplitBand, FineEdge, FineCentre, sampleRate, nLow, nHigh);

        _aCeil = TimeConst.Alpha(dt, CeilingRiseSec);
        _ceilFall = CeilingFallDbPerSec * dt;
        _aEq = TimeConst.Alpha(dt, EqSec);
        _aLoudUp = TimeConst.Alpha(dt, LoudUpSec);
        _aLoudDown = TimeConst.Alpha(dt, LoudDownSec);
        _aCentroid = TimeConst.Alpha(dt, CentroidSec);
        _pc.FloorUp = TimeConst.Alpha(dt, FloorUpSec);
        _pc.FloorDown = TimeConst.Alpha(dt, FloorDownSec);
        _pc.Up = TimeConst.Alpha(dt, PunchUpSec);
        _pc.Down = TimeConst.Alpha(dt, PunchDownSec);
        Reset();
    }

    // ===== 結果（Process のたびに上書き）

    /// <summary>見せる帯のパワー（2 乗平均。傾き・自動バランスの前）</summary>
    public readonly double[] Power = new double[Bands];
    /// <summary>見せる帯ごとの「鳴っている大きさ」（0〜1。平滑化した表示用の値）</summary>
    public readonly double[] Display = new double[Bands];
    /// <summary>見せる帯ごとの「立ち上がりを強調した大きさ」（0〜1）</summary>
    public readonly double[] PunchOut = new double[Bands];
    /// <summary>まとめた帯（Sub / Bass / Mid / Treble）の大きさ（細かい帯から求める）</summary>
    public readonly double[] GroupDisplay = new double[GroupCount];
    /// <summary>まとめた帯の立ち上がりを強調した大きさ</summary>
    public readonly double[] GroupPunch = new double[GroupCount];
    /// <summary>自動音量調整の基準（dBFS。細かい帯のいちばん大きい帯を追いかけたもの）</summary>
    public double CeilingDb { get; private set; }
    /// <summary>基準が決まった（一度でも音が鳴った。それまでは −30 dBFS の仮の値）</summary>
    public bool CeilingReady => _ceil.Ready;
    /// <summary>聞こえ方の音量（dBFS。VU メーターのように平滑化）</summary>
    public double LoudDb { get; private set; }
    /// <summary>聞こえ方の音量（0〜1。自分の基準で 0〜1 にしたもの）</summary>
    public double LoudVU { get; private set; }
    /// <summary>音の重心（Hz。1.5 秒で平滑化）</summary>
    public double CentroidHz { get; private set; }

    /// <summary>見せる帯 k に含まれる FFT の番号の数（テスト・調査用）</summary>
    public int BinCount(int k) => _bands.BinCount(k);

    /// <summary>パンチの計算に使う割合（キックの検出でも同じものを使う。Process の後に読む）</summary>
    public PunchCoefs Coefs => _pc;
    /// <summary>自動音量調整の基準が上がるときの割合（1 回あたり）</summary>
    public double CeilingAlpha => _aCeil;
    /// <summary>自動音量調整の基準が 1 回で下がる量の上限（dB）</summary>
    public double CeilingFallDb => _ceilFall;

    public void Reset()
    {
        for (int k = 0; k < Bands; k++)
        {
            _ref[k].Reset();
            _disp[k].V = 0;
            _low[k] = default;
            _punch[k].Reset();
            Display[k] = PunchOut[k] = Power[k] = 0;
        }
        for (int k = 0; k < FineBands; k++) _fRef[k].Reset();
        for (int g = 0; g < GroupCount; g++)
        {
            _gDisp[g].V = 0;
            _gLow[g] = default;
            _gPunch[g].Reset();
            GroupDisplay[g] = GroupPunch[g] = 0;
        }
        _ceil = new CeilingTracker { C = -30 };
        _bandCeil = new CeilingTracker { C = -30 };
        _loudCeil = new CeilingTracker { C = -30 };
        _loudDb.V = -120;
        _loudVu.V = 0;
        _centroid.V = Math.Log2(500);
        CeilingDb = -30;
        LoudDb = -120;
        LoudVU = 0;
        CentroidHz = 500;
        _coefFor = null;
    }

    /// <summary>
    /// 1 回分を分析する。
    /// powLow / powHigh = FFT の各番号の |X|²（0〜N/2 番。正規化の前）
    /// </summary>
    /// <param name="gateOpen">音が鳴っている（基準・自動バランス・重心は、鳴っているときだけ更新する）</param>
    /// <param name="activity">反応の強さ（門 × 「特定のアプリだけ」の割合。0〜1）。平滑化の前に掛けるので、0 になってもなめらかに消える</param>
    public void Process(double[] powLow, double[] powHigh, AudioEngineSettings s, bool gateOpen, double activity)
    {
        if (!ReferenceEquals(s, _coefFor))
        {
            _coefFor = s;
            _aAtt = TimeConst.Alpha(_dt, Math.Max(0, s.ReactionMs) / 1000);
            _aRel = TimeConst.Alpha(_dt, Math.Max(1, s.SmoothnessMs) / 1000);
            _pc.Weight = Math.Clamp(s.Punch, 0, 1);
        }
        double tilt = s.TrebleTilt;
        double beta = Math.Clamp(s.AutoBalance, 0, 1);
        double range = Math.Clamp(s.DynamicRangeDb, 6, 120);
        double gain = s.GainDb;
        double gamma = Math.Clamp(s.Contrast, 0.2, 5);
        double a = Math.Clamp(activity, 0, 1);

        // 1. 細かい帯のパワーと dB（傾きつき）・聞こえ方の音量・音の重心の材料
        double centroidNum = 0, centroidDen = 0, loudPow = 0;
        for (int k = 0; k < FineBands; k++)
        {
            double p = _fine.Sum(k, powLow, powHigh) * (k < FineSplitBand ? _normLow : _normHigh);
            double tiltDb = tilt * _fine.OctFrom1k[k];
            _fLeq[k] = 10 * Math.Log10(p + PowerFloor) + tiltDb;
            loudPow += p * _fine.KWeight[k];
            double pt = p * DbToPower(tiltDb);
            centroidNum += pt * _fine.Log2Centre[k];
            centroidDen += pt;
        }

        // 2. 自動バランス: 帯ごとの 8 秒の平均 R_k と、その中央値との差の β 倍を足す（−6〜+12 dB）
        AutoBalance(_fLeq, _fRef, _fScratch, gateOpen, beta);

        // 3. 全体で 1 つの基準（いちばん大きい帯を追いかける）
        double c = TrackCeiling(ref _ceil, _fLeq, gateOpen);
        CeilingDb = c;

        // 4. 見せる帯: パワー → dB → 自動バランス → 自分の基準で 0〜1 → 平滑化とパンチ。
        //    明るさに対応させる幅は、設定の「音の幅」を上限に、その帯の実際の音の上下（静かな側 10% の位置）に合わせて狭める
        //    （音の大きさがほぼ一定の曲では、固定の幅だといつも 0.8 くらいに張りついて、拍が見えなくなるため）
        for (int k = 0; k < Bands; k++)
        {
            double p = _bands.Sum(k, powLow, powHigh) * (k < SplitBand ? _normLow : _normHigh);
            Power[k] = p;
            _leq[k] = 10 * Math.Log10(p + PowerFloor) + tilt * _bands.OctFrom1k[k];
        }
        AutoBalance(_leq, _ref, _scratch, gateOpen, beta);
        double cb = TrackCeiling(ref _bandCeil, _leq, gateOpen);
        for (int k = 0; k < Bands; k++)
        {
            if (gateOpen) _low[k].Update(_leq[k], LowStepDb);
            double rk = _low[k].Range(cb, AutoRangeMarginDb, AutoRangeMinDb, range);
            double v = Map(_leq[k], gain, cb, rk, gamma) * a;
            double b = _disp[k].StepA(v, _aAtt, _aRel);
            Display[k] = b;
            PunchOut[k] = _punch[k].Step(v, b, _pc);
        }

        // 5. まとめた帯: メンバー（細かい帯）のパワーの平均を dB にする（1 つの帯と同じ目盛りで比べられる）
        for (int g = 0; g < GroupCount; g++)
        {
            double sum = 0;
            int first = GroupFirst[g], last = GroupLast[g];
            for (int k = first; k <= last; k++) sum += DbToPower(_fLeq[k]);
            double lg = 10 * Math.Log10(sum / (last - first + 1) + PowerFloor);
            if (gateOpen) _gLow[g].Update(lg, LowStepDb);
            double rg = _gLow[g].Range(c, AutoRangeMarginDb, AutoRangeMinDb, range);
            double v = Map(lg, gain, c, rg, gamma) * a;
            double b = _gDisp[g].StepA(v, _aAtt, _aRel);
            GroupDisplay[g] = b;
            GroupPunch[g] = _gPunch[g].Step(v, b, _pc);
        }

        // 6. 聞こえ方の音量（K 特性の近似の重み）。自分の基準で 0〜1 にして、VU メーターのように動かす
        double loudInst = 10 * Math.Log10(loudPow + PowerFloor);
        LoudDb = _loudDb.StepA(loudInst, _aLoudUp, _aLoudDown);
        if (gateOpen) _loudCeil.Update(loudInst, _aCeil, _ceilFall, CeilingMinDb);
        double loudRange = Math.Clamp(range * 0.3, 9, 24);
        double vl = Math.Clamp((loudInst + gain - _loudCeil.C) / loudRange * LoudCeilingAt + LoudCeilingAt, 0, 1) * a;
        LoudVU = _loudVu.StepA(vl, _aLoudUp, _aLoudDown);

        // 7. 音の重心（対数の周波数で平均。1.5 秒で平滑化）。音が鳴っているときだけ
        if (gateOpen && centroidDen > 1e-10)
        {
            _centroid.StepA(centroidNum / centroidDen, _aCentroid, _aCentroid);
        }
        CentroidHz = Math.Pow(2, _centroid.V);
    }

    /// <summary>自動バランス: 鳴っているときは帯ごとの 8 秒の平均を更新し、その中央値との差の β 倍を足す（−6〜+12 dB）</summary>
    private void AutoBalance(double[] leq, WarmEma[] refs, double[] scratch, bool gateOpen, double beta)
    {
        int n = leq.Length;
        if (gateOpen)
        {
            for (int k = 0; k < n; k++) refs[k].Step(leq[k], _aEq);
        }
        if (refs[0].N > 0 && beta > 0)
        {
            for (int k = 0; k < n; k++) scratch[k] = refs[k].V;
            double med = SmallStats.MedianInPlace(scratch, n);
            for (int k = 0; k < n; k++) leq[k] += Math.Clamp(beta * (med - refs[k].V), EqMinDb, EqMaxDb);
        }
    }

    /// <summary>いちばん大きい帯を基準として追いかける（鳴っているときだけ）。いまの基準（dBFS）を返す</summary>
    private double TrackCeiling(ref CeilingTracker ceil, double[] leq, bool gateOpen)
    {
        double x = double.NegativeInfinity;
        for (int k = 0; k < leq.Length; k++) if (leq[k] > x) x = leq[k];
        if (gateOpen) ceil.Update(x, _aCeil, _ceilFall, CeilingMinDb);
        return ceil.C;
    }

    /// <summary>dB の値を 0〜1 にする: ((L + 感度) − (基準 − 幅)) / 幅 を 0〜1 に収め、コントラストの指数を掛ける</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Map(double db, double gain, double ceiling, double range, double gamma)
    {
        double v = (db + gain - (ceiling - range)) / range;
        if (v <= 0) return 0;
        if (v >= 1) return 1;
        return gamma == 1 ? v : Math.Pow(v, gamma);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double DbToPower(double db) => Math.Exp(db * (Math.Log(10) / 10));
}

/// <summary>
/// キック（バスドラム）の遅れの少ない検出（FFT を使わず、音の波から直接求める）。
///   1. 150 Hz のローパスフィルターで低音だけにする
///   2. 絶対値を、上がるのは 2 ミリ秒・下がるのは 60 ミリ秒で追いかける（エンベロープ）
///   3. 512 個ごとに最大値を dB にして、自分の基準（基準 − 30 dB 〜 基準）で 0〜1 にする
/// FFT は窓の長さ（43 ミリ秒）の分だけ遅れるが、こちらは 2〜8 ミリ秒しか遅れない
/// </summary>
public sealed class KickPath
{
    private const double CutoffHz = 150;
    private const double Q = 0.7071;
    private const double AttackSec = 0.002;
    private const double ReleaseSec = 0.060;
    /// <summary>0〜1 に対応させる幅（dB）</summary>
    private const double RangeDb = 30;
    /// <summary>基準の最小値（dB。振幅）</summary>
    private const double CeilingMinDb = -50;

    /// <summary>「最近のキックの大きさ」が下がる速さ（dB／秒）</summary>
    private const double KickRefFallDbPerSec = 1.0;
    /// <summary>キックの立ち上がりの時刻を探す範囲（秒。新しい端から）</summary>
    private const double OnsetSearchSec = 0.032;
    /// <summary>
    /// 包絡線の山から、立ち上がりの中点（dB で前の低いところと山の中間）までの、実際の音の始まりからの遅れ（秒）。
    /// 150 Hz のローパスと 2 ミリ秒の追従で少し遅れる分（テスト TestKickEventTiming で合成音から確かめた値）
    /// </summary>
    private const double OnsetLagSec = 0.001;

    private readonly int _sampleRate;
    private Biquad _lp;
    private readonly double _aAttack, _aRelease;
    private double _env;
    private double _hopMax;
    private CeilingTracker _ceil;
    private LowTracker _lowKick;
    /// <summary>キックの包絡線の幅を決める位置（中央値）。ベースが鳴り続ける曲では、静かな側 10% だと合間もほとんど明るいままになるため</summary>
    public const double KickLowQuantile = 0.5;
    /// <summary>キックの包絡線の幅: 中央値からの余白と、幅の下限（dB）。キックの上がり幅はベースの上に数 dB しかないことが多い</summary>
    public const double KickMarginDb = 1.5, KickMinRangeDb = 6;
    private PunchStream _punch;
    // 包絡線の直近の値（音 1 個ずつ。キックの立ち上がりの時刻を求めるのに使う）
    private readonly float[] _envRing;
    private readonly int _envMask;
    private int _envPos;
    private readonly int _search;
    // 最近のキックの山の大きさ（dB。振幅）。キックを認めたときに上げ、1 秒に 1 dB ずつ下げる
    private double _kickRefDb;
    private bool _kickRefReady;
    private readonly double _kickRefFall;

    public KickPath(int sampleRate)
    {
        _sampleRate = sampleRate;
        _aAttack = 1 - Math.Exp(-1.0 / (AttackSec * sampleRate));
        _aRelease = 1 - Math.Exp(-1.0 / (ReleaseSec * sampleRate));
        _search = (int)Math.Round(OnsetSearchSec * sampleRate);
        _kickRefFall = KickRefFallDbPerSec * AudioEngine.Hop / sampleRate;
        int size = 1;
        while (size < 2 * _search + 2) size <<= 1;
        _envRing = new float[size];
        _envMask = size - 1;
        Reset();
    }

    /// <summary>キックの包絡線（0〜1。反応の強さを掛けたもの）</summary>
    public double KickEnv { get; private set; }
    /// <summary>キックの立ち上がりを強調した値（0〜1）</summary>
    public double KickPunch { get; private set; }
    /// <summary>今回の 512 個の中の、包絡線の最大値（dB。振幅）</summary>
    public double HopDb { get; private set; } = -180;
    /// <summary>キックの包絡線の基準（dB。振幅。ふだんのキックの大きさ）</summary>
    public double CeilingDb => _ceil.C;

    public void Reset()
    {
        _lp = Biquad.LowPass(_sampleRate, CutoffHz, Q);
        _env = 0;
        _hopMax = 0;
        _ceil = new CeilingTracker { C = -30 };
        _punch.Reset();
        Array.Clear(_envRing);
        _envPos = 0;
        _kickRefDb = -120;
        _kickRefReady = false;
        _lowKick = default;
        KickEnv = KickPunch = 0;
    }

    /// <summary>音を 1 個通す（取り込みスレッドから、すべての音に対して呼ぶ）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sample(double x)
    {
        double y = Math.Abs(_lp.Process(x));
        _env += (y - _env) * (y > _env ? _aAttack : _aRelease);
        if (_env > _hopMax) _hopMax = _env;
        _envRing[_envPos] = (float)_env;
        _envPos = (_envPos + 1) & _envMask;
    }

    /// <summary>
    /// 直近 32 ミリ秒の中で、キックの包絡線が立ち上がった時刻を探す。
    ///   1. いちばん大きい点（山）を探す
    ///   2. 山より前（さらに 32 ミリ秒まで）でいちばん小さい点（谷）を探す
    ///   3. 谷から山へ上がる途中で、dB でちょうど中間を初めて超えた点を「立ち上がり」とする
    /// 山が谷より minRiseDb 以上大きくなければ false（ノイズの揺れなど、キックらしくない）。
    /// 山が最近のキックの大きさより maxBelowKickDb 以上小さいときも false
    /// （スネアの胴の音（150〜250 Hz）はローパスを少し通り抜けるが、キックよりずっと小さい）。
    /// キックと認めたら、その山を「最近のキックの大きさ」として覚える（1 秒に 1 dB ずつ下げていく）。
    /// samplesAgo = 立ち上がりが、いちばん新しい音から何個前か（実際の音の始まりに合わせて少し戻してある）
    /// </summary>
    public bool TryFindOnset(double minRiseDb, double maxBelowKickDb, out int samplesAgo, out double riseDb)
    {
        samplesAgo = 0;
        riseDb = 0;
        int newest = (_envPos - 1) & _envMask;
        int peakAgo = 0;
        float peak = -1;
        for (int k = 0; k < _search; k++)
        {
            float v = _envRing[(newest - k) & _envMask];
            if (v > peak)
            {
                peak = v;
                peakAgo = k;
            }
        }
        int troughAgo = peakAgo;
        float trough = peak;
        for (int k = peakAgo; k < peakAgo + _search && k <= _envMask; k++)
        {
            float v = _envRing[(newest - k) & _envMask];
            if (v < trough)
            {
                trough = v;
                troughAgo = k;
            }
        }
        if (peak <= 1e-9f) return false;
        riseDb = 20 * Math.Log10(peak / Math.Max(trough, 1e-9f));
        if (riseDb < minRiseDb) return false;
        double peakDb = 20 * Math.Log10(peak);
        if (_kickRefReady && peakDb < _kickRefDb - maxBelowKickDb) return false;
        _kickRefDb = _kickRefReady ? Math.Max(_kickRefDb, peakDb) : peakDb;
        _kickRefReady = true;
        double mid = Math.Sqrt(peak * (double)Math.Max(trough, 1e-9f));
        int crossAgo = peakAgo;
        for (int k = troughAgo; k >= peakAgo; k--)
        {
            if (_envRing[(newest - k) & _envMask] >= mid)
            {
                crossAgo = k;
                break;
            }
        }
        samplesAgo = crossAgo + (int)Math.Round(OnsetLagSec * _sampleRate);
        return true;
    }

    /// <summary>512 個ごとに呼ぶ。0〜1 の値とパンチを求める</summary>
    public void EndHop(bool gateOpen, double activity, double gainDb, double ceilAlpha, double ceilFallDb, in PunchCoefs pc)
    {
        double db = 20 * Math.Log10(_hopMax + 1e-9);
        HopDb = db;
        if (gateOpen) _ceil.Update(db, ceilAlpha, ceilFallDb, CeilingMinDb);
        // 幅は、キックの包絡線の静かな側 10% の位置に合わせて狭める（上限 RangeDb）。キックの合間がちゃんと暗くなるように
        if (gateOpen) _lowKick.Update(db, BandAnalyzer.LowStepDb, KickLowQuantile);
        double range = _lowKick.Range(_ceil.C, KickMarginDb, KickMinRangeDb, RangeDb);
        double v = Math.Clamp((db + gainDb - (_ceil.C - range)) / range, 0, 1) * Math.Clamp(activity, 0, 1);
        KickEnv = v;
        KickPunch = _punch.Step(v, v, pc);
        _hopMax = 0;
        _kickRefDb -= _kickRefFall;
        // 無音が続いたときの非正規化数を防ぐ
        _lp.Flush();
        if (_env < 1e-20) _env = 0;
    }
}

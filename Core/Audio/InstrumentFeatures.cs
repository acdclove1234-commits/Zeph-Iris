// ==================================================
// InstrumentFeatures.cs - 楽器ごとの手がかり (左右の広がり・打楽器と持続音の分け方（過去だけを見る HPSS）・
//                         音のざらつき（スペクトルの平坦さ）・12 音の色（クロマ）・ベースの音の高さ（YIN）と音の出だし・
//                         ボーカル／ギター・鍵盤／パッド／金管らしさ)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Numerics;
using System.Runtime.CompilerServices;

namespace ProFanController;

/// <summary>
/// 楽器ごとの手がかりを、512 個（約 10.7 ミリ秒）ごとに求める。どれも「らしさ」の連続した値で、楽器の名前を決めるものではない
/// （実際の曲では、ボーカルと金管・ひずんだギターなどを取り違えることがある）。
///
/// 流れ（1 回ごと）:
///   1. 打楽器と持続音に分ける（2048 点の FFT。HPSS を過去だけで行う）:
///      ・持続音 H = 番号ごとの、過去 9 回（約 96 ミリ秒。今回は含めない）の強さの中央値（長く鳴る音ほど残る）
///      ・打楽器 P = 今回の、周波数の近い 17 個（約 400 Hz の幅）から 1 個おきに取った 9 個の強さの中央値（広い帯に一度に出る音ほど残る）
///      ・番号ごとの割合 P² / (H² + P²) で、パワーを打楽器の分と持続音の分に分ける
///      ・低音（40〜230 Hz）・中音（230〜2,200 Hz）・高音（2.2〜16 kHz）ごとに合計し、共通の基準で 0〜1 にする
///   2. （キックの打楽器らしさは、ここでは決めない。2048 点の FFT では、キックを見つけた回にキックがまだ窓の端にしか
///      入っていないため。OnsetDetector.Percussiveness が、FFT を使わない包絡線で決める）
///   3. 左右（ミッド M = (L + R) / 2・サイド S = (L − R) / 2。1024 点の FFT）: 帯ごとの広がり（S² の割合）と、真ん中・横の音の大きさ
///   4. 音のざらつき（300 Hz〜6 kHz のスペクトルの平坦さ。ノイズ・ひずんだギターほど 1 に近い）
///   5. 12 音の色（持続音の 400 Hz〜4.2 kHz の山ごとに、頂点の周波数の音名へ足す）と、いちばん強い音名
///   6. ベース: 音を 3 kHz 前後に落として（500 Hz のローパス）、いちばん新しい 45 ミリ秒で YIN（音の高さ 40〜320 Hz）。
///      ベースの音の出だし = キックにしなかった低音の立ち上がり（OnsetDetector.BassCandidate）か、
///      つながった音で高さが変わったとき（EndHop。キックの直後・すぐ消える音は除く）
///   7. らしさ（0〜1。0.15 秒でなめらかに。合成音で合わせた値で、実際の曲では確かめていない）:
///      ・ボーカル: 真ん中の持続音の中音・高音・音の重心が 0.5〜1.3 kHz・ビブラート（音の高さの 4〜8 Hz の揺れ）
///      ・ギター・鍵盤: 持続音の中音・高音が、鳴った後に減っていく（はじく音）・横に広がっているほど大きい
///      ・パッド: 持続音が減らずに続く・音の重心が低い（0.9〜1.4 kHz より下）・ビブラートがない
///      ・金管: 持続音の重心が明るい（1.1〜1.6 kHz より上）・ビブラートがない・はじく音でない
/// 取り込みスレッドからだけ使う。分析の途中ではメモリを新しく確保しない
/// </summary>
public sealed class InstrumentFeatures
{
    /// <summary>持続音の中央値に使う過去の回数（今回は含めない。並べ替えの回路が 9 個用なので変えない）</summary>
    private const int HistFrames = 9;
    /// <summary>見る周波数の上限（Hz）と、打楽器と持続音に分ける上限（Hz。これより上はすべて打楽器の分にする）</summary>
    private const double MaxHz = 16000, SplitMaxHz = 11000;
    /// <summary>まとめる帯の境目（Hz）: 低音・中音・高音（BandAnalyzer のまとめた帯と同じ）</summary>
    private const double LowHz = 40, LowMidHz = 230, MidHighHz = 2200;
    /// <summary>平坦さを見る帯（Hz）</summary>
    private const double FlatLowHz = 300, FlatHighHz = 6000;
    /// <summary>12 音の色を見る帯（Hz）。400 Hz より下は、FFT の 1 個の番号（23 Hz）が半音より広く、近い音の山が重なるので使わない。持続音の山の頂点の周波数を、両隣から細かく求めて音名にする</summary>
    private const double ChromaLowHz = 400, ChromaHighHz = 4200;
    /// <summary>持続音の重心を見る帯（Hz）</summary>
    private const double CentroidLowHz = 200, CentroidHighHz = 8000;
    /// <summary>ビブラートを見る、いちばん強い持続音の山を探す帯（Hz）</summary>
    private const double VibLowHz = 250, VibHighHz = 1500;
    /// <summary>log に 0 を入れないための小さな値（パワー）</summary>
    private const double PowerFloor = 1e-12;
    /// <summary>音量の基準の最小値（dBFS）</summary>
    private const double CeilingMinDb = -55;

    // 平滑化の時定数（秒）
    private const double PercDownSec = 0.080;
    private const double HarmUpSec = 0.030, HarmDownSec = 0.250;
    private const double WidthSec = 0.150;
    private const double ChromaSec = 0.150;
    private const double CentroidSec = 0.150;
    private const double CueSec = 0.300;
    /// <summary>はじく音かを見る、持続音の中音・高音のいちばん大きいところを探す長さ（秒）</summary>
    private const double RecentMaxSec = 0.400;
    /// <summary>はじく音とみなす、いちばん大きいところからの下がり（dB。ここから PluckSpanDb で 0 → 1）。合成音で、鳴り続ける音は 2.7〜3.1 dB・はじく和音は 4.4 dB</summary>
    private const double PluckFromDb = 3.4, PluckSpanDb = 0.8;
    private const double VibFastSec = 0.020, VibSlowSec = 0.150;
    private const double TextureSec = 0.150;
    private const double BassOnsetDecaySec = 0.120;

    // ===== ベースの音の高さ（YIN）
    /// <summary>ベースを見る音の高さの範囲（Hz）</summary>
    public const double BassMinHz = 40, BassMaxHz = 320;
    /// <summary>落とした後のおおよそのサンプリング周波数（Hz）と、その前のローパス（Hz）</summary>
    private const double BassRateHz = 3000, BassLowPassHz = 500;
    /// <summary>YIN の窓（秒。40 Hz の 1.8 周期）</summary>
    private const double YinWindowSec = 0.045;
    /// <summary>YIN の「周期らしさ」のしきい値（正規化した差。小さいほど周期的）</summary>
    private const double YinThreshold = 0.15;
    /// <summary>音の高さを認める確かさの下限（1 − 正規化した差）</summary>
    private const double BassMinConfidence = 0.75;
    /// <summary>これ以上音の高さが変わったら、新しい音の出だし（半音）</summary>
    private const double BassNewNoteSemitones = 0.7;
    /// <summary>ベースの音の出だしの最短の間隔・キックの直後に出さない時間（秒）</summary>
    private const double BassOnsetGapSec = 0.060, BassAfterKickSec = 0.150;
    /// <summary>ベースの大きさを、キックの後この間（秒）は 0 にし、そこから KickTailToSec まででもとに戻す（キックの「ドン」の残りは低い音のサイン波なので）</summary>
    private const double KickTailFromSec = 0.080, KickTailToSec = 0.250;
    /// <summary>音の高さが変わったことでベースの音の出だしにするのに、新しい高さが続く回数（スネアの胴の音のような短い音を除く）</summary>
    private const int StableHops = 3;

    private readonly int _sampleRate;
    private readonly double _dt;
    private readonly double _normLow, _normHigh;
    private readonly int _nb, _nbSplit;            // 分ける番号の数（1〜_nb − 1 番を使う）
    private readonly double _binHz, _binHzHigh;
    private readonly int _nbHigh;
    private readonly int _bLow, _bLowMid, _bMidHigh, _bCentLo, _bCentHi, _bVibLo, _bVibHi;
    private readonly int _hLow, _hLowMid, _hMidHigh, _hFlatLo, _hFlatHi;
    private readonly int _bChromaLo, _bChromaHi;
    /// <summary>過去 HistFrames 回の強さ（回ごとの配列。番号の並びのまま、まとめて中央値を求める）</summary>
    private readonly float[][] _hist;
    private int _histHead;
    /// <summary>SIMD でまとめて求める番号の数（Vector&lt;float&gt;.Count の倍数）と、強さの前後に足す余白の数（打楽器の中央値で範囲の外を読むため）</summary>
    private readonly int _nbVec;
    private const int MagPad = 8;
    /// <summary>今回の強さ（MagPad 個の余白つき。余白は端の値）・持続音 H・打楽器 P</summary>
    private readonly float[] _magP, _harm, _perc;
    private readonly double[] _hpow;
    /// <summary>持続音の中音・高音の大きさ（dB）の直近 0.4 秒</summary>
    private readonly double[] _mhDbHist;
    private int _mhHead, _mhCount;

    // 平滑化の割合
    private readonly double _aPercDown, _aHarmUp, _aHarmDown, _aWidth, _aChroma, _aCentroid, _aCue, _aVibFast, _aVibSlow, _aTexture, _aBassOnset;

    // 状態
    private CeilingTracker _ceil;
    private AsymEnv _pl, _pm, _ph, _hl, _hm, _hh, _centre, _side, _bassLevel, _harmMH;
    private AsymEnv _mBass, _sBass, _mMid, _sMid, _mHigh, _sHigh;
    private AsymEnv _flat, _centroidLog, _vib, _vibFast, _vibSlow, _below;
    private AsymEnv _vocal, _guitar, _pad, _brass;
    private readonly double[] _chroma = new double[12];
    private readonly double[] _chromaNow = new double[12];
    private int _pitchClass = -1;

    // ベース（YIN）
    private readonly int _decim;
    private readonly double _bassRate;
    private Biquad _bassLp1, _bassLp2;
    private int _decPhase;
    private readonly float[] _bassRing;
    private readonly int _bassMask;
    private int _bassPos;
    private readonly int _yinW, _tauMin, _tauMax;
    private readonly double[] _yinX, _yinD;
    private double _lastNoteHz = double.NaN;
    private int _voicedRun;
    private double _lastBassOnsetT = double.NegativeInfinity, _lastKickT = double.NegativeInfinity;
    private double _bassOnset;

    /// <param name="sampleRate">分析するサンプリング周波数（Hz）</param>
    /// <param name="nLow">2048 点の FFT の大きさ（48 kHz で 2048）</param>
    /// <param name="nHigh">1024 点の FFT の大きさ（左右を比べるのに使う）</param>
    /// <param name="dt">分析の間隔（秒）</param>
    public InstrumentFeatures(int sampleRate, int nLow, int nHigh, double dt)
    {
        _sampleRate = sampleRate;
        _dt = dt;
        _normLow = 2.0 / (nLow * (3.0 * nLow / 8));
        _normHigh = 2.0 / (nHigh * (3.0 * nHigh / 8));
        _binHz = (double)sampleRate / nLow;
        _binHzHigh = (double)sampleRate / nHigh;
        _nb = Math.Min(nLow / 2, (int)Math.Round(MaxHz / _binHz)) + 1;
        _nbSplit = Math.Min(_nb, (int)Math.Round(SplitMaxHz / _binHz) + 1);
        _nbHigh = Math.Min(nHigh / 2, (int)Math.Round(MaxHz / _binHzHigh)) + 1;
        int B(double hz) => Math.Clamp((int)Math.Round(hz / _binHz), 1, _nb - 1);
        int BH(double hz) => Math.Clamp((int)Math.Round(hz / _binHzHigh), 1, _nbHigh - 1);
        _bLow = B(LowHz); _bLowMid = B(LowMidHz); _bMidHigh = B(MidHighHz);
        _bCentLo = B(CentroidLowHz); _bCentHi = B(CentroidHighHz);
        _bVibLo = B(VibLowHz); _bVibHi = B(VibHighHz);
        _hLow = BH(LowHz); _hLowMid = BH(LowMidHz); _hMidHigh = BH(MidHighHz);
        _hFlatLo = BH(FlatLowHz); _hFlatHi = BH(FlatHighHz);
        int vc = Vector<float>.Count;
        _nbVec = (_nbSplit + vc - 1) / vc * vc;
        _hist = new float[HistFrames][];
        for (int i = 0; i < HistFrames; i++) _hist[i] = new float[_nbVec];
        _magP = new float[MagPad + _nbVec + MagPad];
        _harm = new float[_nbVec];
        _perc = new float[_nbVec];
        _hpow = new double[_nb];
        _bChromaLo = Math.Max(2, B(ChromaLowHz));
        _bChromaHi = Math.Min(_nb - 2, B(ChromaHighHz));
        _mhDbHist = new double[Math.Max(4, (int)Math.Round(RecentMaxSec / dt))];

        _aPercDown = TimeConst.Alpha(dt, PercDownSec);
        _aHarmUp = TimeConst.Alpha(dt, HarmUpSec);
        _aHarmDown = TimeConst.Alpha(dt, HarmDownSec);
        _aWidth = TimeConst.Alpha(dt, WidthSec);
        _aChroma = TimeConst.Alpha(dt, ChromaSec);
        _aCentroid = TimeConst.Alpha(dt, CentroidSec);
        _aCue = TimeConst.Alpha(dt, CueSec);
        _aVibFast = TimeConst.Alpha(dt, VibFastSec);
        _aVibSlow = TimeConst.Alpha(dt, VibSlowSec);
        _aTexture = TimeConst.Alpha(dt, TextureSec);
        _aBassOnset = TimeConst.Alpha(dt, BassOnsetDecaySec);

        // ベース: 48 kHz → 3 kHz（16 分の 1）。44.1 kHz なら 15 分の 1（2.94 kHz）
        _decim = Math.Max(1, (int)Math.Round(sampleRate / BassRateHz));
        _bassRate = sampleRate / (double)_decim;
        _yinW = (int)Math.Round(YinWindowSec * _bassRate);
        _tauMax = (int)Math.Ceiling(_bassRate / BassMinHz);
        _tauMin = Math.Max(2, (int)Math.Floor(_bassRate / BassMaxHz));
        int need = _yinW + _tauMax + 2;
        int size = 1;
        while (size < need) size <<= 1;
        _bassRing = new float[size];
        _bassMask = size - 1;
        _yinX = new double[_yinW + _tauMax + 1];
        _yinD = new double[_tauMax + 2];
        Reset();
    }

    // ===== 結果（Process のたびに上書き。0〜1 と書いたものは、反応の強さを掛けてある）

    /// <summary>打楽器の分の大きさ（低音・中音・高音。0〜1）</summary>
    public double PercLow, PercMid, PercHigh;
    /// <summary>持続音の分の大きさ（低音・中音・高音。0〜1）</summary>
    public double HarmBass, HarmMid, HarmHigh;
    /// <summary>真ん中の音・横（左右に広がった音）の大きさ（0〜1。モノラルの音では横は 0）</summary>
    public double Centre, Side;
    /// <summary>帯ごとの広がり（0 = 真ん中・モノラル、1 = 左右で別の音）</summary>
    public double WidthBass, WidthMid, WidthHigh;
    /// <summary>音のざらつき（300 Hz〜6 kHz のスペクトルの平坦さ。0〜1）</summary>
    public double Flatness;
    /// <summary>12 音の色（C = 0 … B = 11。いちばん強い音名を 1 にした割合）</summary>
    public readonly double[] Chroma = new double[12];
    /// <summary>いちばん強い音名（0〜11。はっきりしなければ −1）</summary>
    public int PitchClass => _pitchClass;
    /// <summary>いちばん強い音名のはっきりさ（0〜1）</summary>
    public double PitchClarity;
    /// <summary>ベースの音の高さ（Hz。分からなければ 0）</summary>
    public double BassF0Hz;
    /// <summary>ベースの音名（0〜11。分からなければ −1）</summary>
    public int BassPitchClass = -1;
    /// <summary>ベースの音の大きさ（持続音の低音。音の高さが分かるときだけ。キックの直後は小さくする。0〜1）</summary>
    public double BassLevel;
    /// <summary>ベースの音の出だし（出だしで強さの値になり、0.12 秒で消える。0〜1）</summary>
    public double BassOnset;
    /// <summary>最後にベースの音の出だしを見つけた時刻（AudioClock の秒。テスト・調査用）</summary>
    public double LastBassOnsetT => _lastBassOnsetT;
    /// <summary>らしさ（0〜1）: ボーカル・ギターと鍵盤・パッド（シンセの持続音）・金管</summary>
    public double Vocal, GuitarKeys, Pad, Brass;

    /// <summary>調査用: らしさの材料（ビブラートのセント・いちばん大きいところからの下がり dB・続く音らしさ・持続音の重心 Hz・持続音の中音と高音）</summary>
    internal double DiagVib, DiagPluck, DiagSteady, DiagCentroidHz, DiagHarmMH;

    public void Reset()
    {
        foreach (var h in _hist) Array.Clear(h);
        _histHead = 0;
        _mhHead = _mhCount = 0;
        _ceil = new CeilingTracker { C = -30 };
        _pl = _pm = _ph = _hl = _hm = _hh = _centre = _side = _bassLevel = _harmMH = default;
        _mBass = _sBass = _mMid = _sMid = _mHigh = _sHigh = default;
        _flat = _vib = _below = default;
        _vibFast.V = _vibSlow.V = double.NaN;
        _centroidLog.V = Math.Log2(800);
        _vocal = _guitar = _pad = _brass = default;
        Array.Clear(_chroma);
        Array.Clear(Chroma);
        _pitchClass = -1;
        _bassLp1 = Biquad.LowPass(_sampleRate, BassLowPassHz, 0.5412);
        _bassLp2 = Biquad.LowPass(_sampleRate, BassLowPassHz, 1.3065);
        _decPhase = 0;
        Array.Clear(_bassRing);
        _bassPos = 0;
        _lastNoteHz = double.NaN;
        _voicedRun = 0;
        _pendingPitch = false;
        _prevF0 = 0;
        _lastBassOnsetT = _lastKickT = double.NegativeInfinity;
        _bassOnset = 0;
        PercLow = PercMid = PercHigh = HarmBass = HarmMid = HarmHigh = Centre = Side = 0;
        WidthBass = WidthMid = WidthHigh = Flatness = PitchClarity = BassF0Hz = BassLevel = BassOnset = 0;
        Vocal = GuitarKeys = Pad = Brass = 0;
        BassPitchClass = -1;
    }

    /// <summary>ベースの音の高さに使う音を 1 個通す（分析するサンプリング周波数の、すべての音に対して呼ぶ）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sample(float x)
    {
        double y = _bassLp2.Process(_bassLp1.Process(x));
        if (++_decPhase < _decim) return;
        _decPhase = 0;
        _bassRing[_bassPos] = (float)y;
        _bassPos = (_bassPos + 1) & _bassMask;
    }

    /// <summary>
    /// 1 回分の打楽器・持続音・左右・平坦さ・12 音の色・らしさを求める
    /// </summary>
    /// <param name="powLow">2048 点の FFT の |X|²（モノラル = ミッド）</param>
    /// <param name="powHigh">1024 点の FFT の |X|²（モノラル = ミッド）</param>
    /// <param name="powSide">1024 点の FFT のサイドの |X|²（null ならモノラル。横は 0）</param>
    public void Process(double[] powLow, double[] powHigh, double[]? powSide, AudioEngineSettings s, bool gateOpen, double activity)
    {
        double a = Math.Clamp(activity, 0, 1);
        int nb = _nb;

        // 1. 強さ（振幅）。前後の余白は端の値（打楽器の中央値で、範囲の外を端の値として読む）
        int ns = _nbSplit, nv = _nbVec;
        var mg = _magP;
        for (int b = 1; b < ns; b++) mg[MagPad + b] = (float)Math.Sqrt(powLow[b]);
        for (int k = 0; k <= MagPad; k++) mg[k] = mg[MagPad + 1];
        for (int k = MagPad + ns; k < mg.Length; k++) mg[k] = mg[MagPad + ns - 1];

        // 持続音 H（番号ごとの、過去 9 回の中央値。今回は含めない = 新しく鳴った音は打楽器の側に入る）と、
        // 打楽器 P（周波数の近い 17 個の範囲から 1 個おきに取った 9 個の中央値。17 個の中央値とほぼ同じ）。
        // どちらも Vector<float> の数ずつまとめて、並べ替えの回路で求める（11 kHz まで）
        var hs = _hist;
        int vc = Vector<float>.Count;
        for (int b = 0; b < nv; b += vc)
        {
            Med9(new Vector<float>(hs[0], b), new Vector<float>(hs[1], b), new Vector<float>(hs[2], b), new Vector<float>(hs[3], b), new Vector<float>(hs[4], b),
                new Vector<float>(hs[5], b), new Vector<float>(hs[6], b), new Vector<float>(hs[7], b), new Vector<float>(hs[8], b)).CopyTo(_harm, b);
            int o = MagPad + b;
            Med9(new Vector<float>(mg, o - 8), new Vector<float>(mg, o - 6), new Vector<float>(mg, o - 4), new Vector<float>(mg, o - 2), new Vector<float>(mg, o),
                new Vector<float>(mg, o + 2), new Vector<float>(mg, o + 4), new Vector<float>(mg, o + 6), new Vector<float>(mg, o + 8)).CopyTo(_perc, b);
        }
        Array.Copy(mg, MagPad, hs[_histHead], 0, nv);
        _histHead = (_histHead + 1) % HistFrames;

        // 2. 番号ごとに分けて、帯ごとに合計する
        double pL = 0, pM = 0, pH = 0, hL = 0, hM = 0, hH = 0;
        double centNum = 0, centDen = 0;
        Array.Clear(_chromaNow);
        int vibBin = -1;
        double vibMax = 0;
        for (int b = 1; b < nb; b++)
        {
            double pw = powLow[b];
            // 11 kHz より上は分けない（ハイハット・シンバルの響きなど、ほとんどが打楽器の音。分ける計算を 3 割減らす）
            double h = b < ns ? _harm[b] : 0, p = b < ns ? _perc[b] : 1;
            double h2 = h * h, p2 = p * p;
            double wp = p2 / (h2 + p2 + 1e-30);
            double pp = pw * wp, hp = pw - pp;
            if (b >= _bLow && b < _bLowMid) { pL += pp; hL += hp; }
            else if (b >= _bLowMid && b < _bMidHigh) { pM += pp; hM += hp; }
            else if (b >= _bMidHigh) { pH += pp; hH += hp; }
            if (b >= _bCentLo && b <= _bCentHi)
            {
                centNum += hp * b * _binHz;
                centDen += hp;
            }
            _hpow[b] = hp;
            if (b >= _bVibLo && b <= _bVibHi && hp > vibMax && h > 2 * p)
            {
                vibMax = hp;
                vibBin = b;
            }
        }

        // 12 音の色: 持続音の山（両隣より大きい番号）ごとに、頂点の周波数を求めて、その音名に山の強さを足す
        for (int b = _bChromaLo; b <= _bChromaHi; b++)
        {
            double c = _hpow[b];
            if (c <= PowerFloor || c <= _hpow[b - 1] || c < _hpow[b + 1]) continue;
            double off = SmallStats.ParabolicOffset(Math.Log(_hpow[b - 1] + PowerFloor), Math.Log(c + PowerFloor), Math.Log(_hpow[b + 1] + PowerFloor));
            double f = (b + off) * _binHz;
            int pc = (((int)Math.Round(12 * Math.Log2(f / 440)) + 9) % 12 + 12) % 12;
            _chromaNow[pc] += c + _hpow[b - 1] + _hpow[b + 1];
        }

        // 3. 共通の基準で 0〜1 にする
        double nl = _normLow;
        double dPL = Db(pL * nl), dPM = Db(pM * nl), dPH = Db(pH * nl), dHL = Db(hL * nl), dHM = Db(hM * nl), dHH = Db(hH * nl);
        double loudest = Math.Max(Math.Max(Math.Max(dPL, dPM), Math.Max(dPH, dHL)), Math.Max(dHM, dHH));
        if (gateOpen) _ceil.Update(loudest, TimeConst.Alpha(_dt, 0.3), 3.0 * _dt, CeilingMinDb);
        double range = Math.Clamp(s.DynamicRangeDb, 12, 60), gain = s.GainDb;
        double Map(double db) => Math.Clamp((db + gain - (_ceil.C - range)) / range, 0, 1) * a;
        PercLow = _pl.StepA(Map(dPL), 1, _aPercDown);
        PercMid = _pm.StepA(Map(dPM), 1, _aPercDown);
        PercHigh = _ph.StepA(Map(dPH), 1, _aPercDown);
        HarmBass = _hl.StepA(Map(dHL), _aHarmUp, _aHarmDown);
        HarmMid = _hm.StepA(Map(dHM), _aHarmUp, _aHarmDown);
        HarmHigh = _hh.StepA(Map(dHH), _aHarmUp, _aHarmDown);

        // 4. 左右（1024 点の FFT。ミッドとサイドのパワーを帯ごとに 0.15 秒で平滑化してから比べる）
        double mB = 0, sB = 0, mMd = 0, sMd = 0, mHi = 0, sHi = 0;
        double flatLog = 0, flatSum = 0;
        for (int b = _hLow; b < _nbHigh; b++)
        {
            double mp = powHigh[b], sp = powSide is null ? 0 : powSide[b];
            if (b >= _hFlatLo && b <= _hFlatHi)
            {
                flatLog += Math.Log(mp + PowerFloor);
                flatSum += mp + PowerFloor;
            }
            if (b < _hLowMid) { mB += mp; sB += sp; }
            else if (b < _hMidHigh) { mMd += mp; sMd += sp; }
            else { mHi += mp; sHi += sp; }
        }
        WidthBass = Width(ref _mBass, ref _sBass, mB, sB);
        WidthMid = Width(ref _mMid, ref _sMid, mMd, sMd);
        WidthHigh = Width(ref _mHigh, ref _sHigh, mHi, sHi);
        double mAll = (mB + mMd + mHi) * _normHigh, sAll = (sB + sMd + sHi) * _normHigh;
        // 真ん中の音 ≈ M² − S²（左右で同じ成分）、横の音 ≈ 2·S²（左右で違う成分）
        Centre = _centre.StepA(Map(Db(Math.Max(0, mAll - sAll))), _aHarmUp, _aHarmDown);
        Side = _side.StepA(Map(Db(Math.Min(2 * sAll, mAll + sAll))), _aHarmUp, _aHarmDown);

        // 5. 平坦さ（1024 点の FFT の、相乗平均 / 相加平均）
        int nFlat = _hFlatHi - _hFlatLo + 1;
        double flat = nFlat > 0 && flatSum > 0 ? Math.Exp(flatLog / nFlat) / (flatSum / nFlat) : 0;
        Flatness = _flat.StepA(gateOpen ? Math.Clamp(flat, 0, 1) : 0, _aCue, _aCue);

        // 6. 12 音の色
        double cMax = 0, cSum = 0;
        for (int k = 0; k < 12; k++)
        {
            _chroma[k] += ((gateOpen ? _chromaNow[k] : 0) - _chroma[k]) * _aChroma;
            if (_chroma[k] > cMax) cMax = _chroma[k];
            cSum += _chroma[k];
        }
        int best = -1;
        for (int k = 0; k < 12; k++)
        {
            Chroma[k] = cMax > 1e-20 ? _chroma[k] / cMax : 0;
            if (cMax > 1e-20 && _chroma[k] >= cMax) best = k;
        }
        double clarity = cSum > 1e-20 ? (cMax - cSum / 12) / cMax : 0;
        PitchClarity = Math.Clamp(clarity, 0, 1) * a;
        // 前の音名を少しだけひいきする（同じくらいの 2 つの音名で、行ったり来たりしない）
        if (best >= 0 && _pitchClass >= 0 && best != _pitchClass && _chroma[best] < 1.2 * _chroma[_pitchClass]) best = _pitchClass;
        _pitchClass = hM * nl > 1e-9 && clarity > 0.1 ? best : -1;

        // 7. らしさの材料
        double harmMH = Map(Db((hM + hH) * nl));
        double hmh = _harmMH.StepA(harmMH, _aHarmUp, _aHarmDown);
        if (centDen > PowerFloor && gateOpen) _centroidLog.StepA(Math.Log2(centNum / centDen), _aCentroid, _aCentroid);
        double centroidHz = Math.Pow(2, _centroidLog.V);
        // ビブラート: いちばん強い持続音の山の周波数（セント）の、4〜8 Hz くらいのゆっくりした揺れの大きさ。
        // 速い平滑化（20 ミリ秒）と遅い平滑化（0.15 秒）の差 = 1 回ごとの細かいぶれ（和音のうなり・音の重なり）を除いた揺れ。
        // 半音の 0.8 より大きく動いたら、音が変わった（または別の山に移った）だけなので数えない
        double vibHz = double.NaN;
        if (vibBin > 1 && vibBin < nb - 1)
        {
            double l = Math.Log(powLow[vibBin - 1] + PowerFloor), c = Math.Log(powLow[vibBin] + PowerFloor), r = Math.Log(powLow[vibBin + 1] + PowerFloor);
            vibHz = (vibBin + SmallStats.ParabolicOffset(l, c, r)) * _binHz;
        }
        if (double.IsFinite(vibHz) && gateOpen)
        {
            double cents = 1200 * Math.Log2(vibHz);
            if (!double.IsFinite(_vibFast.V) || Math.Abs(cents - _vibFast.V) >= 80)
            {
                _vibFast.V = _vibSlow.V = cents;
            }
            else
            {
                _vibFast.StepA(cents, _aVibFast, _aVibFast);
                _vibSlow.StepA(cents, _aVibSlow, _aVibSlow);
                double dev = _vibFast.V - _vibSlow.V;
                _vib.StepA(dev * dev, _aCue, _aCue);
            }
        }
        double vibCents = Math.Sqrt(_vib.V);
        double vib = Math.Clamp((vibCents - 6) / 10, 0, 1);
        // はじく音と鳴り続ける音: 持続音の中音・高音が、直近 0.4 秒のいちばん大きいところから何 dB 下にいるか（0.3 秒で平滑化）。
        // はじいた音は鳴った後ずっと減っていくので、平均で 4〜5 dB 下（0.4 秒で消える音を 1 秒に 2 回）。
        // 鳴り続ける音（パッド・声・金管）は、うなりやビブラートの揺れだけ（1〜2 dB）
        double dbMH = Db((hM + hH) * nl);
        _mhDbHist[_mhHead] = dbMH;
        _mhHead = (_mhHead + 1) % _mhDbHist.Length;
        if (_mhCount < _mhDbHist.Length) _mhCount++;
        double recentMax = double.NegativeInfinity;
        for (int k = 0; k < _mhCount; k++) recentMax = Math.Max(recentMax, _mhDbHist[k]);
        if (gateOpen && harmMH > 0.05) _below.StepA(Math.Min(recentMax - dbMH, 12), _aCue, _aCue);
        double pluck = Math.Clamp((_below.V - PluckFromDb) / PluckSpanDb, 0, 1);
        double sustain = 1 - pluck;
        double centre = 1 - WidthMid;
        DiagVib = vibCents;
        DiagPluck = _below.V;
        DiagSteady = sustain;
        DiagCentroidHz = centroidHz;
        DiagHarmMH = hmh;

        double lc = Math.Log2(centroidHz);
        double Above(double lo, double hi) => Smooth01((lc - Math.Log2(lo)) / (Math.Log2(hi) - Math.Log2(lo)));
        double bright = Above(1100, 1600);
        double dark = 1 - Above(900, 1400);
        double voiceBand = Above(350, 500) * (1 - Above(1300, 1800));
        // ベースが中心の音（持続音の低音が、中音・高音より大きい）は、ギター・パッドにしない
        double notBass = Math.Clamp((hmh - HarmBass + 0.1) / 0.2, 0, 1);
        double vocal = hmh * centre * voiceBand * (0.15 + 0.85 * vib) * (1 - pluck);
        double brass = hmh * bright * (1 - vib) * (1 - pluck);
        double pad = hmh * sustain * dark * notBass * (1 - vib) * (1 - pluck);
        double guitar = hmh * pluck * notBass * (0.6 + 0.4 * WidthMid) * (1 - vib);
        Vocal = _vocal.StepA(vocal, _aTexture, _aTexture);
        Brass = _brass.StepA(brass, _aTexture, _aTexture);
        Pad = _pad.StepA(pad, _aTexture, _aTexture);
        GuitarKeys = _guitar.StepA(guitar, _aTexture, _aTexture);

        _bassLevelMap = Map(dHL);
        _lowLevelMap = Map(Db((pL + hL) * nl));
        _gateOpen = gateOpen;
    }

    private double _bassLevelMap, _lowLevelMap;
    private bool _gateOpen;

    /// <summary>
    /// ベースの音の高さと出だしを求める（キックを決めた後に呼ぶ。キックの直後は音の出だしにしない）。
    /// 音の出だしは 2 通り:
    ///   ・低音の立ち上がりのうち、打楽器らしくないのでキックにしなかったもの（bassCandidate。キックと同じ速さ = 遅れ約 15 ミリ秒）
    ///   ・音の高さが 2 回続けて分かり、前の音から半音の 0.7 以上変わった（立ち上がりのない、つながった音。窓の長さの分だけ遅い）
    /// ベースの大きさは、キックの直後（BassAfterKickSec）は小さくする（キックの「ドン」の残りを、ベースと数えないように）
    /// </summary>
    /// <param name="tHop">この回のいちばん新しい音の時刻</param>
    /// <param name="kickNow">この回にキックを見つけた</param>
    /// <param name="kickT">そのキックの時刻</param>
    /// <param name="bassCandidate">この回に、打楽器らしくない低音の立ち上がりがあった（OnsetDetector.BassCandidate）</param>
    /// <param name="bassCandidateT">その立ち上がりの時刻</param>
    public void EndHop(double tHop, bool kickNow, double kickT, bool bassCandidate = false, double bassCandidateT = double.NaN)
    {
        // キックの後は、キックの「ドン」の残りの高さを、音の出だしにせずに覚え直す
        if (kickNow)
        {
            _lastKickT = kickT;
            _pendingPitch = true;
        }
        double conf = Yin(out double f0);
        bool voiced = _gateOpen && conf >= BassMinConfidence && f0 >= BassMinHz && f0 <= BassMaxHz;
        // 続けて同じ高さ（前の回から半音の 0.5 以内）だった回数
        bool sameStep = voiced && _prevF0 > 0 && Math.Abs(12 * Math.Log2(f0 / _prevF0)) < 0.5;
        _voicedRun = voiced ? (sameStep ? _voicedRun + 1 : 1) : 0;
        _prevF0 = voiced ? f0 : 0;
        BassF0Hz = voiced ? f0 : 0;
        BassPitchClass = voiced ? (((int)Math.Round(12 * Math.Log2(f0 / 440)) + 9) % 12 + 12) % 12 : -1;
        double sinceKick = tHop - _lastKickT;
        double kickFree = Math.Clamp((sinceKick - KickTailFromSec) / (KickTailToSec - KickTailFromSec), 0, 1);
        BassLevel = _bassLevel.StepA(voiced ? _bassLevelMap * kickFree : 0, _aHarmUp, _aHarmDown);

        _bassOnset += (0 - _bassOnset) * _aBassOnset;
        bool gapOk = tHop - _lastBassOnsetT >= BassOnsetGapSec;
        if (bassCandidate && gapOk && _gateOpen)
        {
            Onset(double.IsFinite(bassCandidateT) ? bassCandidateT : tHop);
            _lastNoteHz = double.NaN; // 高さは、分かったときに覚え直す（同じ音を 2 回出さない）
            _pendingPitch = true;
        }
        else if (_voicedRun >= StableHops)
        {
            // キックの直後は、高さが下がっていく「ドン」の残りなので、覚え直し続ける
            if (_pendingPitch || double.IsNaN(_lastNoteHz) || sinceKick < BassAfterKickSec)
            {
                _lastNoteHz = f0;
                _pendingPitch = false;
            }
            else if (Math.Abs(12 * Math.Log2(f0 / _lastNoteHz)) >= BassNewNoteSemitones)
            {
                _lastNoteHz = f0;
                if (gapOk && sinceKick >= BassAfterKickSec && _lowTrendDb >= MinLowTrendDb) Onset(tHop);
            }
        }
        BassOnset = _bassOnset;

        void Onset(double t)
        {
            _lastBassOnsetT = t;
            _bassOnset = Math.Max(_bassOnset, Math.Clamp(_lowLevelMap, 0.3, 1));
        }
    }

    private bool _pendingPitch;
    private double _prevF0;
    /// <summary>直近 15 ミリ秒の低音の強さが、その前の 15 ミリ秒から何 dB 変わったか</summary>
    private double _lowTrendDb;
    /// <summary>音の高さが変わったことで出だしにするのに必要な、低音の強さの変化の下限（dB。これより速く消えている音は除く）</summary>
    private const double MinLowTrendDb = -2;

    /// <summary>
    /// YIN（落とした音の直近 45 ミリ秒）。戻り値は確かさ（1 − 正規化した差。周期的でなければ 0）、f0 は音の高さ（Hz）
    /// </summary>
    private double Yin(out double f0)
    {
        f0 = 0;
        int n = _yinW + _tauMax;
        int start = _bassPos - n;
        double energy = 0;
        for (int j = 0; j < n; j++)
        {
            double v = _bassRing[(start + j) & _bassMask];
            _yinX[j] = v;
            energy += v * v;
        }
        // 直近 15 ミリ秒と、その前の 15 ミリ秒の強さの比（dB。すぐ消える音（スネアの胴の音）を、ベースの音の出だしにしない）
        int q = Math.Max(4, (int)Math.Round(0.015 * _bassRate));
        double eNew = 0, eOld = 0;
        for (int j = n - q; j < n; j++) eNew += _yinX[j] * _yinX[j];
        for (int j = n - 2 * q; j < n - q; j++) eOld += _yinX[j] * _yinX[j];
        _lowTrendDb = 10 * Math.Log10((eNew + 1e-20) / (eOld + 1e-20));
        // とても小さい音（−66 dBFS 未満）は見ない
        if (energy / n < 2.5e-7) return 0;
        var x = _yinX;
        var d = _yinD;
        double run = 0;
        d[0] = 1;
        int found = -1;
        // 比べる元は、いちばん新しい _yinW 個（新しい音の高さが早く分かるように）。τ 個前の同じ長さと比べる
        int refStart = _tauMax;
        for (int tau = 1; tau <= _tauMax; tau++)
        {
            double sum = 0;
            for (int j = 0; j < _yinW; j++)
            {
                double e = x[refStart + j] - x[refStart + j - tau];
                sum += e * e;
            }
            run += sum;
            d[tau] = run > 1e-20 ? sum * tau / run : 1;
        }
        for (int tau = _tauMin; tau <= _tauMax; tau++)
        {
            if (d[tau] < YinThreshold)
            {
                while (tau + 1 <= _tauMax && d[tau + 1] < d[tau]) tau++;
                found = tau;
                break;
            }
        }
        if (found < 0)
        {
            // しきい値より小さいところがなければ、いちばん小さいところ（確かさは低い）
            double best = double.PositiveInfinity;
            for (int tau = _tauMin; tau <= _tauMax; tau++)
                if (d[tau] < best) { best = d[tau]; found = tau; }
        }
        if (found < 0) return 0;
        double off = found > 1 && found < _tauMax ? SmallStats.ParabolicOffset(d[found - 1], d[found], d[found + 1]) : 0;
        double period = found + off;
        if (period <= 0) return 0;
        f0 = _bassRate / period;
        return Math.Clamp(1 - d[found], 0, 1);
    }

    /// <summary>ミッドとサイドのパワーを平滑化して、広がり（0〜1）を返す</summary>
    private double Width(ref AsymEnv m, ref AsymEnv s, double mp, double sp)
    {
        m.StepA(mp, _aWidth, _aWidth);
        s.StepA(sp, _aWidth, _aWidth);
        double tot = m.V + s.V;
        return tot > 1e-6 ? Math.Clamp(2 * s.V / tot, 0, 1) : 0;
    }

    /// <summary>9 個の中央値（並べ替えの回路。要素ごとに、Vector&lt;float&gt; の数だけまとめて求める。分かれ道がないので速い）</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<float> Med9(Vector<float> p0, Vector<float> p1, Vector<float> p2, Vector<float> p3, Vector<float> p4,
        Vector<float> p5, Vector<float> p6, Vector<float> p7, Vector<float> p8)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void S(ref Vector<float> x, ref Vector<float> y)
        {
            var lo = Vector.Min(x, y);
            y = Vector.Max(x, y);
            x = lo;
        }
        S(ref p1, ref p2); S(ref p4, ref p5); S(ref p7, ref p8);
        S(ref p0, ref p1); S(ref p3, ref p4); S(ref p6, ref p7);
        S(ref p1, ref p2); S(ref p4, ref p5); S(ref p7, ref p8);
        S(ref p0, ref p3); S(ref p5, ref p8); S(ref p4, ref p7);
        S(ref p3, ref p6); S(ref p1, ref p4); S(ref p2, ref p5);
        S(ref p4, ref p7); S(ref p4, ref p2); S(ref p6, ref p4);
        S(ref p4, ref p2);
        return p4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Db(double p) => 10 * Math.Log10(p + PowerFloor);

    /// <summary>0〜1 になめらかにつなぐ（0 以下は 0、1 以上は 1）</summary>
    private static double Smooth01(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }
}

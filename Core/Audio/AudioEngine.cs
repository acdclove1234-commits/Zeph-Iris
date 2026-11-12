// ==================================================
// AudioEngine.cs - 音楽連動の分析エンジン (10 帯のスペクトラム・自動音量調整・キック検出・
//                  立ち上がり検出・テンポとビートの追跡)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>分析の設定（変更しない値。設定が変わったら丸ごと差し替える）</summary>
/// <param name="GainDb">感度（dB。+ にすると小さな音でも明るく光る）</param>
/// <param name="DynamicRangeDb">光の明るさに対応させる音量の幅（dB）</param>
/// <param name="AutoBalance">帯ごとの偏りを自動でならす強さ（0〜1）</param>
/// <param name="TrebleTilt">高音ほど持ち上げる量（dB／オクターブ）</param>
/// <param name="Contrast">明暗の差を強める指数（1 で変えない）</param>
/// <param name="ReactionMs">上がるときの速さ（ミリ秒）</param>
/// <param name="SmoothnessMs">下がるときの滑らかさ（ミリ秒）</param>
/// <param name="Punch">立ち上がりをどれだけ強調するか（0〜1）</param>
/// <param name="BeatSensitivity">ビート・立ち上がりの検出の感度（0.5〜3）</param>
/// <param name="TempoMode">テンポの追跡: Auto / KickOnly / Off</param>
/// <param name="TempoFactor">テンポの倍率（0.5 / 1 / 2。倍・半分に聞こえるとき用）</param>
public sealed record AudioEngineSettings(
    double GainDb = 0,
    double DynamicRangeDb = 40,
    double AutoBalance = 0.35,
    double TrebleTilt = 1.5,
    double Contrast = 1.0,
    double ReactionMs = 10,
    double SmoothnessMs = 250,
    double Punch = 0.5,
    double BeatSensitivity = 1.0,
    string TempoMode = TempoModes.Auto,
    double TempoFactor = 1.0)
{
    public static readonly AudioEngineSettings Default = new();
}

public static class TempoModes
{
    /// <summary>テンポをつかんだらビートを予測して光らせる</summary>
    public const string Auto = "Auto";
    /// <summary>予測せず、キックが鳴ったときだけ</summary>
    public const string KickOnly = "KickOnly";
    /// <summary>テンポもビートも使わない（音の大きさだけ）</summary>
    public const string Off = "Off";
}

/// <summary>
/// 音を取り込むスレッドから Push で音を受け取り、512 個（約 10.7 ミリ秒）ごとに分析して
/// Features（分析結果の記録）・Events（キックなどの出来事）・Beat（テンポとビートの位置）を更新する。
/// LED を光らせるスレッドと画面は、それらを読むだけ（書くのは取り込みスレッドだけ）。
///
/// 512 個ごとの流れ:
///   1. 直近 2048 個と 1024 個にハン窓をかけて FFT（96 kHz では 4096 個と 2048 個。窓の時間の長さをそろえる）
///   2. 無音判定の門（直近 1024 個の音量。−55 dBFS で開き、−62 dBFS 未満が 0.3 秒続いたら閉じる）。
///      反応の強さ = 門（閉じるときは 0.1 秒でなめらかに 0 へ）× 「特定のアプリだけ」の割合（0.08 秒で平滑化）
///   3. BandAnalyzer: 10 帯（1 オクターブごと）の強さ・自動音量調整・平滑化・パンチ・聞こえ方の音量・音の重心
///   4. KickPath: 150 Hz 以下の音の包絡線（音 1 個ずつ計算してあるものを、512 個ごとにまとめる）
///   5. OnsetDetector: 立ち上がり（キック・スネア・ハイハット）を本当の時刻つきで Events に書く
///   6. BeatTracker: テンポとビートの位置を求めて Beat を差し替える
///   7. 1 コマ分の結果を Features に書く（時刻 = そのコマのいちばん新しい音の時刻）
/// 分析の途中ではメモリを新しく確保しない（ガベージコレクションで音が途切れないように）。
/// 例外: テンポとビートの位置（Beat）は変更しない値なので、変わったときだけ作り直す
/// （0.5 ミリ秒以上ずれたとき・確かさが 0.02 以上変わったとき・8 拍ごとなど。ロック中は数拍に 1 回程度）
/// </summary>
public sealed class AudioEngine
{
    public const int Hop = 512;

    /// <summary>門が開く音量（dBFS）</summary>
    private const double GateOpenDb = -55;
    /// <summary>門が閉じる音量（dBFS）</summary>
    private const double GateCloseDb = -62;
    /// <summary>門が閉じるまでに、静かな状態が続く必要がある時間（秒）</summary>
    private const double GateHoldSec = 0.300;
    /// <summary>門が閉じたときに反応の強さを 0 にしていく時定数（秒。急に消えないように）</summary>
    private const double GateReleaseSec = 0.100;
    /// <summary>「特定のアプリだけ」の割合の平滑化の時定数（秒）</summary>
    private const double AppGateSec = 0.080;
    /// <summary>キックの強調した値を低音の強調した値に混ぜるときの重み</summary>
    private const double KickIntoBass = 0.9;
    /// <summary>同期テスト: クリックとみなす大きさ（振幅。フルスケール = 1）</summary>
    private const double ClickLevel = 0.05;
    /// <summary>同期テスト: 「ほぼ無音」とみなす大きさ（振幅）</summary>
    private const double ClickQuietLevel = 0.01;
    /// <summary>同期テスト: クリックの前に必要な、ほぼ無音の時間（秒）</summary>
    private const double ClickQuietSec = 0.300;
    /// <summary>同期テスト: 音が出始めてから、クリックの大きさに届くまでの時間の上限（秒。クリックはなめらかに立ち上がるため）</summary>
    private const double ClickAttackSec = 0.005;

    private readonly Decimator? _dec;
    private readonly double _decDelaySec;
    private readonly int _nLow, _nHigh, _ringMask;
    private readonly float[] _ring;
    private int _pos;
    private int _sinceHop;
    private long _hop = -1;
    private readonly double _dt;
    private readonly double _magScale;
    private readonly Fft _fftLow, _fftHigh;
    private readonly double[] _winLow, _winHigh;
    private readonly double[] _lowRe, _lowIm, _highRe, _highIm;
    private readonly double[] _powLow, _powHigh, _magLow;
    private readonly BandAnalyzer _bands;
    private readonly KickPath _kick;
    private readonly OnsetDetector _onsets;
    private readonly BeatTracker _tracker;
    private readonly DropDetector _drop;
    private readonly AudioFrame _frame = new();
    private BeatExpander _expSub, _expBass, _expMid, _expTreble;
    private HysteresisGate _gate;
    private AsymEnv _gateEnv;
    private AsymEnv _appEnv;
    private readonly double _aGateRelease, _aApp;
    private bool _trackerOn;
    private int _quiet;
    private readonly int _quietNeeded;
    /// <summary>同期テスト: 静けさのあとに音が出始めてから、クリックの大きさに届くまで待つ残りの音の数（0 なら待っていない）</summary>
    private int _attackLeft;
    private readonly int _attackNeeded;
    /// <summary>同期テスト: 音が出始めた時刻（クリックの時刻にする）</summary>
    private double _attackT;

    private volatile bool _resetRequested;
    private volatile bool _resetTempoRequested;
    private AudioEngineSettings _settings = AudioEngineSettings.Default;
    private AudioEngineSettings? _effFor;
    private AudioEngineSettings _eff = AudioEngineSettings.Default;
    private double _lastT = double.NegativeInfinity;
    private BeatState _beat = BeatState.None;

    public AudioEngine(int sampleRate = 48000)
    {
        SampleRate = sampleRate > 0 ? sampleRate : 48000;
        // 88.2〜384 kHz の音は、44.1 / 48 kHz に落としてから分析する。分析の 1 コマ（512 個）と立ち上がりの目盛りは
        // 44.1 / 48 kHz で調整してあり、192 kHz のままだと 1 コマが 4 分の 1 の時間になって、スネアを取りこぼしたり、
        // 倍のテンポをつかんだりするため
        int factor = Decimator.FactorFor(SampleRate);
        if (factor > 1)
        {
            _dec = new Decimator(factor);
            _decDelaySec = _dec.DelaySamples / SampleRate;
        }
        AnalysisRate = SampleRate / factor;
        // 窓の長さ（時間）をサンプリング周波数によらずそろえる: 48 kHz で 2048 / 1024 個、96 kHz で 4096 / 2048 個
        // （落とせない周波数のときだけ。64 kHz など）
        int scale = 1;
        while (scale < 4 && AnalysisRate >= 48000 * scale * 1.5) scale <<= 1;
        _nLow = 2048 * scale;
        _nHigh = 1024 * scale;
        int ring = 4096;
        while (ring < _nLow) ring <<= 1;
        _ring = new float[ring];
        _ringMask = ring - 1;
        _dt = (double)Hop / AnalysisRate;
        // 立ち上がりの検出は「フルスケールのサイン波 ≈ 512」の目盛りで調整してあるので、FFT が大きいときは割る
        _magScale = 1.0 / scale;
        _fftLow = new Fft(_nLow);
        _fftHigh = new Fft(_nHigh);
        _winLow = Fft.PeriodicHann(_nLow);
        _winHigh = Fft.PeriodicHann(_nHigh);
        _lowRe = new double[_nLow];
        _lowIm = new double[_nLow];
        _highRe = new double[_nHigh];
        _highIm = new double[_nHigh];
        _powLow = new double[_nLow / 2 + 1];
        _powHigh = new double[_nHigh / 2 + 1];
        _magLow = new double[_nLow / 2 + 1];
        double frameRate = AnalysisRate / (double)Hop;
        _bands = new BandAnalyzer(AnalysisRate, _nLow, _nHigh, _dt);
        _kick = new KickPath(AnalysisRate);
        _onsets = new OnsetDetector(AnalysisRate, _nLow, frameRate);
        _tracker = new BeatTracker(frameRate);
        _drop = new DropDetector(_dt);
        _aGateRelease = TimeConst.Alpha(_dt, GateReleaseSec);
        _aApp = TimeConst.Alpha(_dt, AppGateSec);
        _quietNeeded = (int)Math.Round(ClickQuietSec * SampleRate);
        _attackNeeded = Math.Max(1, (int)Math.Round(ClickAttackSec * SampleRate));
        ResetInternal();
    }

    /// <summary>Push に渡す音のサンプリング周波数</summary>
    public int SampleRate { get; }
    /// <summary>分析に使うサンプリング周波数（88.2 kHz 以上は 44.1 / 48 kHz に落とす）</summary>
    public int AnalysisRate { get; }
    /// <summary>分析の設定（どのスレッドから変えてもよい。次の分析から反映）</summary>
    public AudioEngineSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set => Volatile.Write(ref _settings, value ?? AudioEngineSettings.Default);
    }
    /// <summary>「特定のアプリだけ」のときの、そのアプリの音の割合（0〜1。取り込みスレッドが設定する）</summary>
    public double AppGate { get; set; } = 1.0;
    /// <summary>同期テスト中だけ true にする（クリック音を Click として記録する）</summary>
    public bool ClickDetect { get; set; }

    public FeatureRing Features { get; } = new();
    public EventRing Events { get; } = new();
    /// <summary>テンポとビートの位置（丸ごと差し替えられる。読むときはいったん変数に入れてから使う）</summary>
    public BeatState Beat
    {
        get => Volatile.Read(ref _beat);
        private set => Volatile.Write(ref _beat, value);
    }

    /// <summary>テンポの追跡（テスト・調査用）</summary>
    internal BeatTracker Tracker => _tracker;

    /// <summary>拍の AI で追跡を確かめて直す（null なら使わない。取り込みのスレッドで動かす）</summary>
    public BeatAssist? Assist { get; set; }
    internal OnsetDetector Onsets => _onsets;

    /// <summary>テンポの追跡の状態（ロック中・予測を続けている・つかんでいない。表示・テスト用）</summary>
    public BeatLock BeatLock => _tracker.Lock;
    /// <summary>今がブレイク（曲の静かな部分）の中か（表示・テスト用）</summary>
    public bool InBreakdown => _drop.InBreakdown;
    /// <summary>分析 1 回あたりの FFT の大きさ（低い帯用。48 kHz で 2048）</summary>
    public int FftSize => _nLow;

    /// <summary>
    /// モノラルの音を追加する（取り込みスレッドから呼ぶ）。
    /// tFirst は mono[0] の音の時刻（AudioClock の秒。SampleClock で求める）。
    /// 無音で何も届かない間は、0 の音（無音）を渡して時間を進めてよい（テンポの記録や減衰が途切れない）。
    /// 512 個たまるごとに分析し、そのコマの時刻は tFirst + (そのコマの最後の音の番号) / サンプリング周波数
    /// </summary>
    public void Push(ReadOnlySpan<float> mono, double tFirst)
    {
        if (_resetRequested) ResetInternal();
        Assist?.Push(mono, tFirst);
        bool click = ClickDetect;
        double invFs = 1.0 / SampleRate;
        var dec = _dec;
        for (int i = 0; i < mono.Length; i++)
        {
            float x = mono[i];
            if (!float.IsFinite(x)) x = 0;
            double t = tFirst + i * invFs;
            // 同期テストのクリックは、届いたままの細かさで時刻を測る
            if (click) DetectClick(x, t);
            if (dec is null) Analyze(x, t);
            // 落とした音の時刻は、フィルターの遅れの分だけ前（線形位相なので、どの高さの音も同じ遅れ）
            else if (dec.Push(x, out float y)) Analyze(y, t - _decDelaySec);
        }
    }

    /// <summary>分析するサンプリング周波数（AnalysisRate）の音 1 個</summary>
    private void Analyze(float x, double t)
    {
        _ring[_pos] = x;
        _pos = (_pos + 1) & _ringMask;
        _kick.Sample(x);
        if (++_sinceHop >= Hop)
        {
            _sinceHop = 0;
            AnalyzeHop(t);
        }
    }

    /// <summary>分析の状態をすべて初期化する（出力先が変わったときなど）</summary>
    public void Reset()
    {
        // 分析の途中の値は取り込みスレッドが次の Push の最初に消す（ほかのスレッドから呼ばれても安全なように）
        _resetRequested = true;
        Features.Clear();
        Events.Clear();
        Beat = BeatState.None;
    }

    /// <summary>テンポの倍率を変えたときなどに、今のテンポの追跡をやり直す</summary>
    public void ResetTempo()
    {
        _resetTempoRequested = true;
        Beat = BeatState.None;
    }

    private void ResetInternal()
    {
        _resetRequested = false;
        Assist?.Reset();
        _resetTempoRequested = false;
        _dec?.Reset();
        Array.Clear(_ring);
        _pos = 0;
        _sinceHop = 0;
        _hop = -1;
        _bands.Reset();
        _kick.Reset();
        _onsets.Reset();
        _tracker.Reset();
        _drop.Reset();
        _gate.Reset();
        _gateEnv.V = 0;
        _appEnv.V = SafeAppGate();
        _trackerOn = false;
        _quiet = 0;
        _attackLeft = 0;
        _lastT = double.NegativeInfinity;
        _frame.Clear();
        _expSub = _expBass = _expMid = _expTreble = default;
    }

    /// <summary>「特定のアプリだけ」の割合（おかしな値なら 1 = 全部の音）</summary>
    private double SafeAppGate()
    {
        double g = AppGate;
        return double.IsFinite(g) ? Math.Clamp(g, 0, 1) : 1;
    }

    /// <summary>
    /// 設定の値を安全な範囲に収める（設定ファイルが壊れていて NaN などが入っていても、分析が止まらないように）。
    /// 設定が差し替えられたときだけ作り直す
    /// </summary>
    private AudioEngineSettings Effective()
    {
        var s = Settings;
        if (ReferenceEquals(s, _effFor)) return _eff;
        static double F(double v, double lo, double hi, double def) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : def;
        var d = AudioEngineSettings.Default;
        _eff = new AudioEngineSettings(
            GainDb: F(s.GainDb, -24, 24, d.GainDb),
            DynamicRangeDb: F(s.DynamicRangeDb, 6, 96, d.DynamicRangeDb),
            AutoBalance: F(s.AutoBalance, 0, 1, d.AutoBalance),
            TrebleTilt: F(s.TrebleTilt, -6, 6, d.TrebleTilt),
            Contrast: F(s.Contrast, 0.2, 5, d.Contrast),
            ReactionMs: F(s.ReactionMs, 0, 1000, d.ReactionMs),
            SmoothnessMs: F(s.SmoothnessMs, 1, 5000, d.SmoothnessMs),
            Punch: F(s.Punch, 0, 1, d.Punch),
            BeatSensitivity: F(s.BeatSensitivity, 0.1, 10, d.BeatSensitivity),
            TempoMode: s.TempoMode ?? TempoModes.Auto,
            TempoFactor: F(s.TempoFactor, 0.25, 4, 1));
        _effFor = s;
        return _eff;
    }

    /// <summary>
    /// 同期テスト: ほぼ無音が 0.3 秒以上続いた後、5 ミリ秒以内にクリックの大きさに届いた音を、出始めた時刻で記録する。
    /// クリックはなめらかに立ち上がるので、出始めの小さな音（無音とクリックの間の大きさ）で数え直さない
    /// </summary>
    private void DetectClick(float x, double t)
    {
        double a = Math.Abs(x);
        if (_attackLeft > 0)
        {
            if (a >= ClickLevel)
            {
                Events.Add(AudioEventKind.Click, _attackT, (float)(a / ClickLevel));
                _attackLeft = 0;
            }
            else _attackLeft--;
            _quiet = 0;
            return;
        }
        if (a >= ClickQuietLevel && _quiet >= _quietNeeded)
        {
            _attackT = t;
            _quiet = 0;
            if (a >= ClickLevel) Events.Add(AudioEventKind.Click, t, (float)(a / ClickLevel));
            else _attackLeft = _attackNeeded;
            return;
        }
        if (a < ClickQuietLevel)
        {
            if (_quiet < _quietNeeded) _quiet++;
        }
        else _quiet = 0;
    }

    /// <param name="tHop">このコマのいちばん新しい音の時刻</param>
    private void AnalyzeHop(double tHop)
    {
        var s = Effective();
        _hop++;
        // 時刻は必ず前のコマより新しくする（時計の取り直しで、まれに少し戻ることがあるため。FeatureRing は時刻順が前提）
        if (!double.IsFinite(tHop)) tHop = double.IsFinite(_lastT) ? _lastT + _dt : 0;
        if (tHop <= _lastT) tHop = _lastT + 1e-6;
        _lastT = tHop;
        if (_resetTempoRequested)
        {
            _resetTempoRequested = false;
            _tracker.Reset();
            _drop.Reset();
        }

        // 1. 窓をかけて FFT（古い順に並べる。窓は直近 N 個を覆う）
        int startLow = _pos - _nLow;
        for (int j = 0; j < _nLow; j++)
        {
            _lowRe[j] = _ring[(startLow + j) & _ringMask] * _winLow[j];
            _lowIm[j] = 0;
        }
        int startHigh = _pos - _nHigh;
        double sumSq = 0;
        for (int j = 0; j < _nHigh; j++)
        {
            double x = _ring[(startHigh + j) & _ringMask];
            sumSq += x * x;
            _highRe[j] = x * _winHigh[j];
            _highIm[j] = 0;
        }
        _fftLow.Forward(_lowRe, _lowIm);
        _fftHigh.Forward(_highRe, _highIm);
        for (int b = 0; b <= _nLow / 2; b++)
        {
            double p = _lowRe[b] * _lowRe[b] + _lowIm[b] * _lowIm[b];
            _powLow[b] = p;
            _magLow[b] = Math.Sqrt(p) * _magScale;
        }
        for (int b = 0; b <= _nHigh / 2; b++) _powHigh[b] = _highRe[b] * _highRe[b] + _highIm[b] * _highIm[b];

        // 2. 無音判定の門と、反応の強さ（門 × アプリの割合）
        double rmsDb = 10 * Math.Log10(sumSq / _nHigh + 1e-12);
        bool open = _gate.Update(rmsDb, _dt, GateOpenDb, GateCloseDb, GateHoldSec);
        double g = _gateEnv.StepA(open ? 1 : 0, 1.0, _aGateRelease);
        double app = _appEnv.StepA(SafeAppGate(), _aApp, _aApp);
        double activity = Math.Clamp(g * app, 0, 1);

        // 3〜4. 帯の強さとキック
        _bands.Process(_powLow, _powHigh, s, open, activity);
        _kick.EndHop(open, activity, s.GainDb, _bands.CeilingAlpha, _bands.CeilingFallDb, _bands.Coefs);

        // 5. 立ち上がり
        _onsets.Process(_magLow, s, activity, open, _hop, tHop, Events, _kick);

        // 6. テンポとビート（Off のときは計算もしない）
        if (s.TempoMode == TempoModes.Off)
        {
            if (_trackerOn)
            {
                _tracker.Reset();
                _trackerOn = false;
            }
            if (!ReferenceEquals(Beat, BeatState.None)) Beat = BeatState.None;
        }
        else
        {
            _trackerOn = true;
            double odf = activity >= OnsetDetector.MinActivity ? _onsets.Odf : 0;
            double low = activity >= OnsetDetector.MinActivity ? _onsets.Low : 0;
            // 小節の頭の判定には、キックの包絡線の大きさ（振幅。強く叩いた拍ほど大きい）を使う
            double accent = Math.Pow(10, _kick.HopDb / 20);
            _tracker.Process(odf, low, accent, _onsets.LowFired, open, tHop, s.TempoFactor);
            Assist?.Tick(tHop, _tracker, open, odf, low);
            var b = _tracker.Published;
            if (!ReferenceEquals(b, Beat)) Beat = b;
        }

        // 盛り上がり（ドロップ）
        if (_drop.Update(_onsets.KickFlux, open && activity >= OnsetDetector.MinActivity, _onsets.KickStrength,
                _onsets.KickTime, _tracker.PeriodSec, out double dropT, out float dropS))
        {
            Events.Add(AudioEventKind.Drop, dropT, dropS);
        }

        // 7. 1 コマ分の結果
        var f = _frame;
        f.T = tHop;
        for (int k = 0; k < AudioFrame.BandCount; k++)
        {
            f.Band[k] = (float)_bands.Display[k];
            f.Punch[k] = (float)_bands.PunchOut[k];
        }
        f.Sub = (float)_bands.GroupDisplay[0];
        f.Bass = (float)_bands.GroupDisplay[1];
        f.Mid = (float)_bands.GroupDisplay[2];
        f.Treble = (float)_bands.GroupDisplay[3];
        // 強調した値は、それぞれの最近の中央値を基準に広げる（音圧の高い曲では強調した値もいつも 0.8 前後になり、拍が見えないため）
        bool track = open && activity >= OnsetDetector.MinActivity;
        f.SubE = _expSub.Apply(_bands.GroupPunch[0], track);
        // 低音の強調した値には、遅れの少ないキックの検出も混ぜる（パルス・ビートで色替えが使う）
        f.BassE = _expBass.Apply(Math.Max(_bands.GroupPunch[1], KickIntoBass * _kick.KickPunch), track);
        f.MidE = _expMid.Apply(_bands.GroupPunch[2], track);
        f.TrebleE = _expTreble.Apply(_bands.GroupPunch[3], track);
        f.KickEnv = (float)_kick.KickEnv;
        f.LoudVU = (float)_bands.LoudVU;
        f.LoudDb = (float)Math.Max(-120, _bands.LoudDb);
        f.RmsDb = (float)Math.Max(-120, rmsDb);
        f.CeilingDb = (float)_bands.CeilingDb;
        f.CentroidHz = (float)_bands.CentroidHz;
        f.Activity = (float)activity;
        f.GateOpen = open;
        f.Extrapolated = false;
        Features.Write(f);
        if (StatsLogging) AddStats(f);
    }

    // ===== 音の統計（記録に残して、実際の曲での光り方の調整に使う）

    /// <summary>音が鳴っている間、1 分ごとに音の統計を記録に書く</summary>
    public bool StatsLogging { get; set; }
    private const double StatsSec = 60;
    private double _stSec, _stBassE, _stMidE, _stTrebleE, _stLoud, _stBassEMax, _stMidEMax, _stTrebleEMax, _stLocked, _stSynced;
    private int _stFrames;
    private readonly long[] _stCount0 = new long[8];

    // 反応しない原因を調べるための値（音が鳴っているかに関係なく、1 分ごと）
    private double _dgSec, _dgOpen, _dgActivity, _dgApp, _dgRms;
    private int _dgFrames;

    private void AddStats(AudioFrame f)
    {
        _dgFrames++;
        _dgSec += _dt;
        if (f.GateOpen) _dgOpen++;
        _dgActivity += f.Activity;
        _dgApp += SafeAppGate();
        _dgRms += f.RmsDb;
        if (_dgSec >= StatsSec)
        {
            double m = _dgFrames;
            // 音がある（平均 −60 dBFS 以上）のに、ほとんど反応していないときだけ書く（無音の間は書かない）
            if (_dgRms / m > -60 && _dgActivity / m < 0.5)
                Logger.Info($"音楽連動の反応が弱い状態（{_dgSec:F0} 秒）: 音量 平均 {_dgRms / m:F0} dBFS・無音判定の門が開いていた割合 {_dgOpen / m * 100:F0}%・"
                    + $"選んだアプリの音の割合 平均 {_dgApp / m:F2}・反応の強さ 平均 {_dgActivity / m:F2}");
            _dgSec = _dgOpen = _dgActivity = _dgApp = _dgRms = 0;
            _dgFrames = 0;
        }

        if (!f.GateOpen || f.Activity < OnsetDetector.MinActivity) return;
        _stFrames++;
        _stSec += _dt;
        _stBassE += f.BassE;
        _stMidE += f.MidE;
        _stTrebleE += f.TrebleE;
        _stLoud += f.LoudVU;
        _stBassEMax = Math.Max(_stBassEMax, f.BassE);
        _stMidEMax = Math.Max(_stMidEMax, f.MidE);
        _stTrebleEMax = Math.Max(_stTrebleEMax, f.TrebleE);
        if (_tracker.Lock == BeatLock.Locked) _stLocked++;
        // 「ロック」と、確かさが少し下がった間（そのまま拍の並びを続ける）を合わせた割合 = LED が拍に合わせて光っている割合
        if (_tracker.Lock != BeatLock.Unlocked) _stSynced++;
        if (_stSec < StatsSec) return;
        var c = Events.CountByKind;
        double Rate(AudioEventKind k) => (c[(int)k] - _stCount0[(int)k]) / _stSec;
        double n = _stFrames;
        var b = Beat;
        Logger.Info($"音の統計（{_stSec:F0} 秒）: キック {Rate(AudioEventKind.Kick):F1}/秒・スネア {Rate(AudioEventKind.Snare):F1}/秒・ハイハット {Rate(AudioEventKind.Hat):F1}/秒・"
            + $"低音 平均 {_stBassE / n:F2} 最大 {_stBassEMax:F2}・中音 平均 {_stMidE / n:F2} 最大 {_stMidEMax:F2}・高音 平均 {_stTrebleE / n:F2} 最大 {_stTrebleEMax:F2}・"
            + $"音量 平均 {_stLoud / n:F2}・テンポ {(b.HasTempo ? b.Bpm : 0):F0} BPM（ロック {_stLocked / n * 100:F0}%・拍に合わせて光る {_stSynced / n * 100:F0}%）");
        Array.Copy(c, _stCount0, c.Length);
        _stSec = _stBassE = _stMidE = _stTrebleE = _stLoud = _stBassEMax = _stMidEMax = _stTrebleEMax = _stLocked = _stSynced = 0;
        _stFrames = 0;
    }
}

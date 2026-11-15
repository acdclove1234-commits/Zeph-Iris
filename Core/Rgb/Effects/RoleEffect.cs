// ==================================================
// RoleEffect.cs - 光り方「パーツごとの役割」 (ゾーンごとに、低音・中音・高音・音全体、または楽器（キック・スネア・
//                 ハイハット・ベース・ボーカル・和音・パッド）のどれかに反応して光る)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// パーツごとの役割: ゾーンの設定「反応する音」（LedSegment.Role）ごとに、決まった音だけに反応する。
///   低音: キックのたびにぱっと光り（τ 0.22 秒で消える）、ベースの強さで明るさが残る
///   中音: スネアのたびに光り（τ 0.18 秒）、ボーカルなどの中音の強さで明るさが残る
///   高音: ハイハットのたびに白っぽく短く光り（τ 0.09 秒）、高音の強さで明るさが残る
///   音全体: 音量で明るさが変わる（点滅しない）
/// 色は役割ごとにパレットの位置をずらす（低音 0・中音 1/3・高音 2/3）。ビートで色が進むときも、役割の違いは保たれる。
/// パレット「選んだ色だけ」なら、すべて同じ色で明るさだけが変わる。
/// 遅いデバイス（slow）は、光の消え方を 1.8 倍ゆっくりにする（一瞬の光を取りこぼさないように）
///
/// 楽器ごとの役割（キックから後。InstrumentFeatures の手がかりで光る。どれも「らしさ」なので、取り違えることがある）:
///   キック: キックのたびにゾーン全体が光る（τ 0.22 秒）。下地は、キックが続いている間だけ明るい（ベースの音では明るくならない）。
///           予測した拍でも光るのは、最後のキックから KickBeatSec 秒まで（ベースやギターでテンポをつかんだときに光らないように）。
///           「激しい点滅を抑える」がオンなら、前の光から 333 ミリ秒たっていないキックの光は 0.35 倍（FlashLimiter と同じ）
///   スネア: 続けて並んだ LED のまとまり（ゾーンの 15%）が光る（τ 0.18 秒）。まとまりの場所はスネアのたびに黄金比ずつ進む。
///           小さいゾーン（6 個未満）はゾーン全体で光り、キックと同じく 1 秒に 3 回までにする。
///           スネア・ハイハットの出来事は、直後の高音に打楽器の分がなければ光らせない（声・金管・ベースの音を取り違えた出来事。DrumConfidence）
///   ハイハット: 離れた LED が 1〜2 個、白っぽく光る（τ 0.07 秒）。小さいゾーンは明るさをほとんど変えず、色を白へ寄せる
///   ベース: 持続音の低音の大きさで光り、音の出だしでふわっとふくらむ。色はベースの音名で少しずれる
///   ボーカル・リード: 声（金管のような明るい持続音も）の大きさで光る。大きいゾーンは、音名の位置に光のかたまりを置く
///   和音: 鍵盤・ギターらしい持続音の大きさで光り、はじいた瞬間（1〜2 kHz の立ち上がりで、前後にキック・スネアがないもの。UpdateStrum）に
///         ふわっとふくらむ。色は和音（12 音の色の 5 度圏の向き）でずれる
///   パッド: 変わらずに続く持続音の大きさで、ゆっくり明るさが変わる（点滅しない）。ゾーンの端から端へ、少し色をずらす
/// 1 つの LED の粒（スネア・ハイハット）は、ParticleGapSec より詰めて置かない（1 つの LED が光るのは 1 秒に 3 回まで）。
/// 更新の遅いデバイス（グラボ・メモリ。1 秒に 4〜10 回）では、楽器ごとの役割は一瞬の光を見せず、
/// 曲の部分ごと（2 秒でならす）にいちばん目立つ楽器の色を SceneColor で混ぜた 1 色にする（明るさは盛り上がりでゆっくり変わる）
/// </summary>
public sealed class RoleEffect : IMusicEffect
{
    public const double BassTauSec = 0.22;
    public const double MidTauSec = 0.18;
    public const double TrebleTauSec = 0.07;
    public const double AllTauSec = 0.30;
    /// <summary>遅いデバイスで消え方に掛ける倍率</summary>
    public const double SlowFactor = 1.8;
    /// <summary>
    /// 高音のパーツのハイハットの光の最短の間隔（秒）。光ってから下がりきる前に次の光が来ると、ちらつきに見える
    /// （LedSteady が光を 75 ミリ秒保ち、その後 4 コマ = 67 ミリ秒以上下がってから次の光にする）
    /// </summary>
    public const double HatMinGapSec = 0.15;
    /// <summary>ハイハットの光で白へ寄せる割合（× 光の強さ）</summary>
    public const float HatWhiteMix = 0.35f;

    /// <summary>音が鳴っている間の下地の明るさと、音の強さで足す幅（どのパーツも真っ暗にならないように）</summary>
    public const float BodyLevel = 0.12f;
    public const float BodyRange = 0.30f;
    /// <summary>役割ごとの強さの山が下がっていく時定数（秒）と、山の下限（ほとんど鳴っていない音を拡大しすぎない）</summary>
    public const double PeakReleaseSec = 4.0;
    public const float PeakFloor = 0.12f;
    /// <summary>出来事の光を満点にする多さ（1 秒あたり）。これより多く鳴る音は √(RefRate / 多さ) 倍に弱める（下限 MinFlashAmp）</summary>
    public const float RefRate = 2f;
    public const float MinFlashAmp = 0.75f;
    /// <summary>低音以外のパーツにも分けるキックの光の割合</summary>
    public const float BeatShare = 0.45f;
    /// <summary>出来事の多さをならす割合（1 回ごと）</summary>
    private const float RateFollow = 0.15f;

    // ===== 楽器ごとの役割
    /// <summary>楽器の持続音の大きさで足す明るさの幅（下地 BodyLevel の上に。打楽器の役割は BodyRange のまま）</summary>
    public const float ToneRange = 0.55f;
    /// <summary>
    /// 手がかりを 0〜1 にする幅（Span01: From で 0、From + Span で 1）。らしさ・ベースの大きさは、その楽器が鳴っていなくても
    /// ほかの楽器で少し出る（合成音で、らしさ 0.0〜0.2・ベースの大きさ 0.4〜0.7）ので、そこまでは 0 にする
    /// </summary>
    public const float TextureFrom = 0.15f, TextureSpan = 0.45f, GuitarFrom = 0.12f, GuitarSpan = 0.3f, BassFrom = 0.45f, BassSpan = 0.5f;
    /// <summary>
    /// 打楽器・和音の下地: 出来事の強さを保つ秒数と、そこへ近づく時定数（秒。上がる・下がる）。
    /// キックの予測した拍の光は、最後のキックから KickBeatSec 秒まで
    /// </summary>
    public const double PresenceHoldSec = 1.0, PresenceUpSec = 0.25, PresenceDownSec = 0.8, KickBeatSec = 2.0;
    private static readonly MusicRole[] PresenceRoles = { MusicRole.Kick, MusicRole.Snare, MusicRole.Hat, MusicRole.Harmony };
    /// <summary>
    /// はじいた瞬間（和音）: 1〜2 kHz の立ち上がりの、ゆっくりした追従（秒）・平均の立ち上がり（秒）・最小の間隔（秒）。
    /// キック・スネアの前後 StrumVetoSec 以内なら出さない（ドラムの音なので）。そのため StrumWaitSec 待ってから光らせる
    /// </summary>
    public const double StrumSlowSec = 0.1, StrumMeanSec = 2.0, StrumGapSec = 0.12, StrumVetoSec = 0.06, StrumWaitSec = 0.02;
    /// <summary>スネアのまとまりの長さ（ゾーンに対する割合。強さで 0.6〜1 倍）と、まとまりの場所を進める量（黄金比）</summary>
    public const float SnareLength = 0.15f;
    private const double SnareStep = 0.618034;
    /// <summary>同じ LED に次の粒を置けるまでの秒数（1 つの LED が光るのは 1 秒に 3 回まで。SparkleEffect と同じ）</summary>
    public const double ParticleGapSec = 0.333;
    /// <summary>ハイハットの粒を白へ寄せる割合</summary>
    public const float HatParticleWhite = 0.4f;
    /// <summary>小さいゾーンで、ハイハットのときに色を白へ寄せる割合と、足す明るさ（点滅に見えないよう小さく）</summary>
    public const float SmallHatWhite = 0.45f, SmallHatLift = 0.08f;
    /// <summary>小さいゾーンのハイハットの色の変化の、最短の間隔（秒）と消え方（秒）。ゾーン全体が変わるので、粒より間をあける</summary>
    public const double SmallHatGapSec = 0.25, SmallHatTauSec = 0.07;
    /// <summary>ベースの音の出だし・和音をはじいた瞬間にふくらむ明るさ（ゾーン全体なので、点滅に見えないよう控えめに）</summary>
    public const float BassSwell = 0.35f, StrumSwell = 0.5f;
    /// <summary>ベースの音の出だしとみなす値（AudioFrame.BassOnset）と、ふくらみの消え方（秒）</summary>
    public const float BassOnsetMin = 0.3f;
    public const double BassSwellTauSec = 0.15;
    /// <summary>音名・和音で色をずらす幅（パレットの位置）</summary>
    public const double PitchHueRange = 0.07;
    /// <summary>音名・和音の向きをならす時定数（秒）: ベース・ボーカル・和音・パッド</summary>
    public const double BassHueSec = 0.12, VocalHueSec = 0.25, ChordHueSec = 0.5, PadHueSec = 1.5;
    /// <summary>ボーカルの光のかたまりの半分の幅（ゾーンに対する割合）と、かたまりの外の明るさの割合</summary>
    public const float VocalBlobHalf = 0.4f, VocalBlobBase = 0.45f;
    /// <summary>パッドの明るさが上がる・下がる時定数（秒）と、ゾーンの端から端でずらす色（パレットの位置）</summary>
    public const double PadUpSec = 0.3, PadDownSec = 0.8, PadSpread = 0.06;
    /// <summary>楽器ごとの役割で、音が鳴っている・止まったの切り替えにかける時間（秒。上がる・下がる）</summary>
    public const double ActiveUpSec = 0.03, ActiveDownSec = 0.3;
    /// <summary>和音の下地に使う、最近はじいた瞬間があったことの割合</summary>
    public const float StrumPresenceShare = 0.7f;
    /// <summary>はじいた瞬間のふくらみの消え方（秒）</summary>
    public const double StrumTauSec = 0.18;
    /// <summary>更新の遅いデバイス: 楽器の目立ち方をならす時定数（秒。曲の部分の長さ）と、明るさが上がる・下がる時定数（秒）</summary>
    public const double SectionSec = 2.0, SceneUpSec = 0.5, SceneDownSec = 1.2;

    /// <summary>楽器ごとの役割の、パレットの位置のずれ（キック 0・スネア 1/3・ハイハット 2/3 は低音・中音・高音と同じ）</summary>
    public static double OffsetOf(MusicRole r) => r switch
    {
        MusicRole.Bass or MusicRole.Kick => 0,
        MusicRole.Mid or MusicRole.Snare => 1 / 3.0,
        MusicRole.Treble or MusicRole.Hat => 2 / 3.0,
        MusicRole.BassLine => 1 / 6.0,
        MusicRole.Vocal => 0.5,
        MusicRole.Harmony => 5 / 6.0,
        MusicRole.Pad => 0.42,
        _ => 0.5,
    };

    private const int Roles = 4;
    private readonly float[] _peak = new float[Roles];
    private readonly float[] _rate = new float[Roles];
    private readonly double[] _lastHitT = new double[Roles];
    private readonly bool _slow;
    private readonly double[] _hitT = new double[Roles];
    private readonly float[] _hitS = new float[Roles];
    private readonly float[] _glow = new float[Roles];
    private readonly float[] _flash = new float[Roles];
    private readonly LinColor[] _color = new LinColor[Roles];

    // 楽器ごとの役割（番号は MusicRole。0〜3 は使わない）
    private static readonly int RoleCount = MusicRoles.Count;
    /// <summary>打楽器・和音の下地: なめらかにした値と、最近の出来事の強さ（PresenceHoldSec の間保つ）・その時刻</summary>
    private readonly float[] _drumPresence = new float[RoleCount], _presHold = new float[RoleCount];
    private readonly double[] _presHoldT = new double[RoleCount];
    /// <summary>最近のキック・スネアの時刻（和音のはじいた瞬間を、ドラムの音と分けるため）</summary>
    private readonly double[] _drumT = new double[8];
    private int _drumHead;
    /// <summary>出来事の直後の分析結果を読むための入れ物</summary>
    private readonly AudioFrame _probe = new();
    private readonly float[] _iglow = new float[RoleCount];
    private readonly LinColor[] _icolor = new LinColor[RoleCount];
    private readonly double[] _ihitT = new double[RoleCount];
    private readonly float[] _ihitS = new float[RoleCount];
    /// <summary>ゾーン全体が光る・ふくらむもの（キック・小さいゾーンのスネア・和音をはじいた瞬間・ベースの出だし）の、1 秒に 3 回まで（「激しい点滅を抑える」のとき）</summary>
    private readonly FlashLimiter _kickLimit = new(), _snareLimit = new(), _strumLimit = new(), _bassLimit = new();
    /// <summary>ベースの音の出だしのふくらみ（始まった時刻・強さ）と、前のコマの出だしの値（立ち上がりを見つける）</summary>
    private double _bassSwellT = double.NegativeInfinity;
    private float _bassSwellS, _prevBassOnset;
    private double _lastHatT = double.NegativeInfinity, _lastSmallHatT = double.NegativeInfinity;
    private float _smallHat;
    private double _snarePos;
    // 粒（LED ごと。スネア・ハイハットのゾーン）
    private double[] _pT0 = Array.Empty<double>();
    private float[] _pTau = Array.Empty<float>();
    private LinColor[] _pCol = Array.Empty<LinColor>();
    // 音名・和音の向き（単位円の上。12 音を 1 周にして、ならしてから角度に戻す）
    private float _bassX = 1, _bassY, _voxX = 1, _voxY, _chordX = 1, _chordY, _padX = 1, _padY;
    private float _activeMix;
    private float _strumSlow, _riseMean, _candS, _strumS, _padLevel;
    private double _candT = double.NaN, _lastCandT = double.NegativeInfinity, _strumT = double.NegativeInfinity;
    private float _vocalPos = 0.5f;
    private LinColor _padEnd;
    // 更新の遅いデバイス: 楽器ごとの目立ち方と、混ぜた色・明るさ
    private readonly float[] _presence = new float[RoleCount];
    private readonly LinColor[] _sceneBuf = new LinColor[RoleCount];
    private LinColor _sceneColor;
    private float _sceneGlow;
    private bool _hasScene;

    public RoleEffect(bool slow = false) => _slow = slow;

    public string Name => MusicStyles.Role;

    /// <summary>役割ごとの今の明るさ（0〜1。テスト用。楽器ごとの役割は、ゾーン全体の明るさ）</summary>
    public float LevelOf(MusicRole r) => MusicRoles.IsInstrument(r) ? _iglow[(int)r] : _glow[(int)r];

    public void Reset(EffectContext c)
    {
        Array.Fill(_hitT, double.NegativeInfinity);
        Array.Clear(_hitS);
        Array.Clear(_glow);
        Array.Clear(_flash);
        Array.Fill(_peak, PeakFloor);
        Array.Fill(_rate, RefRate);
        Array.Fill(_lastHitT, double.NegativeInfinity);

        Array.Clear(_drumPresence);
        Array.Clear(_presHold);
        Array.Fill(_presHoldT, double.NegativeInfinity);
        Array.Fill(_drumT, double.NegativeInfinity);
        Array.Clear(_iglow);
        Array.Fill(_ihitT, double.NegativeInfinity);
        Array.Clear(_ihitS);
        _kickLimit.Reset();
        _snareLimit.Reset();
        _strumLimit.Reset();
        _bassLimit.Reset();
        _bassSwellT = double.NegativeInfinity;
        _bassSwellS = _prevBassOnset = 0;
        _lastHatT = _lastSmallHatT = double.NegativeInfinity;
        _smallHat = 0;
        _snarePos = 0;
        int n = c.LedCount;
        if (_pT0.Length != n)
        {
            _pT0 = new double[n];
            _pTau = new float[n];
            _pCol = new LinColor[n];
        }
        Array.Fill(_pT0, double.NegativeInfinity);
        Array.Fill(_pTau, (float)TrebleTauSec);
        Array.Clear(_pCol);
        _bassX = _voxX = _chordX = _padX = 1;
        _bassY = _voxY = _chordY = _padY = 0;
        _activeMix = 0;
        _strumSlow = _riseMean = _candS = _strumS = _padLevel = 0;
        _candT = double.NaN;
        _lastCandT = _strumT = double.NegativeInfinity;
        _vocalPos = 0.5f;
        Array.Clear(_presence);
        _sceneGlow = 0;
        _hasScene = false;
    }

    private double Tau(MusicRole r)
    {
        double t = r switch { MusicRole.Bass => BassTauSec, MusicRole.Mid => MidTauSec, MusicRole.Treble => TrebleTauSec, _ => AllTauSec };
        return _slow ? t * SlowFactor : t;
    }

    public void Update(EffectContext c)
    {
        // 役割ごとの出来事（キック・スネア・ハイハット）。光った本当の時刻を覚える
        var ev = c.Events;
        for (int i = 0; i < ev.Count; i++)
        {
            var e = ev[i];
            int r = e.Kind switch
            {
                AudioEventKind.Kick => (int)MusicRole.Bass,
                AudioEventKind.Snare => (int)MusicRole.Mid,
                AudioEventKind.Hat => (int)MusicRole.Treble,
                _ => -1,
            };
            if (r < 0) continue;
            float s = Math.Clamp(0.55f + 0.25f * e.Strength, 0, 1);
            if (e.Kind == AudioEventKind.Hat)
            {
                // ハイハットは詰めて光らせない（MusicTuning.HatResponse の間隔）。明るさは下げない
                // （この光り方では、高音のパーツはハイハットで光るのが役割なので、暗くすると低音・中音と釣り合わなくなる）
                // パーツ全体が光るので、強さ「激しい」でも HatMinGapSec より詰めない（16 分音符のハイハットを全部見つけるようになると、
                // 光って 75 ミリ秒保ったあと、下がり始めてすぐに次の光が来て、ちらついて見えた。MusicFlickerTests）
                var (_, minGap) = MusicTuning.HatResponse(c.Music);
                minGap = Math.Max(minGap, HatMinGapSec);
                if (e.T - _hitT[r] < minGap && e.T >= _hitT[r]) continue;
            }
            _hitT[r] = e.T;
            _hitS[r] = s;
            // 出来事の多さ（1 秒あたり）: 間隔から求めて、ならしておく
            double gap = e.T - _lastHitT[r];
            if (gap > 0.02 && gap < 4) _rate[r] += (1 / (float)gap - _rate[r]) * RateFollow;
            _lastHitT[r] = e.T;
        }

        var f = c.Frame;
        bool active = f.Activity > 0.3f;
        // キックの光（どのパーツにも BeatShare の割合で分ける。役割が分かれすぎず、全体が拍に合わせて動くように）
        int bi = (int)MusicRole.Bass;
        float kick = _hitS[bi] * (float)Math.Exp(-Math.Max(0, c.T - _hitT[bi]) / Tau(MusicRole.Bass));
        // ビートの光（テンポをつかんでいれば予測した拍でも光る。キックを検出しそこねた拍でも光るように）。
        // コマの間の最大値から経過時間だけ減らした値も見る（1 秒間のコマ数が少ないデバイスでも拍を取りこぼさない）
        float beatFl = Math.Max(c.Fl, c.FlPeak * (float)Math.Exp(-Math.Max(0, c.T - c.FlPeakT) / Tau(MusicRole.Bass)));
        kick = Math.Max(kick, beatFl);
        for (int r = 0; r < Roles; r++)
        {
            var role = (MusicRole)r;
            double tau = Tau(role);
            // 今の分析結果は、本当の時刻からの経過時間だけ減らす（古い値を何度も拾い直さない）
            float age = (float)Math.Exp(-(c.AgeT - c.FrameTime) / tau);
            float raw = role switch
            {
                MusicRole.Bass => f.BassE,
                MusicRole.Mid => f.MidE,
                MusicRole.Treble => f.TrebleE,
                _ => f.LoudVU,
            } * age;
            // 役割ごとに、最近の強さの山で割ってそろえる（中音はふつう低音より弱いので、そのままだと中音のパーツだけ暗い）
            _peak[r] = Math.Max(raw, Math.Max(PeakFloor, _peak[r] * EffectContext.Decay(c.Dt, PeakReleaseSec)));
            float level = Math.Clamp(raw / _peak[r], 0, 1);
            // 出来事の光: 多く鳴る音（ハイハットは 1 秒に 8 回など）ほど弱くする（どのパーツも同じくらいの目立ち方にする）
            float amp = Math.Clamp(MathF.Sqrt(RefRate / Math.Max(_rate[r], 0.25f)), MinFlashAmp, 1f);
            float own = role == MusicRole.All ? 0f : amp * _hitS[r] * (float)Math.Exp(-Math.Max(0, c.T - _hitT[r]) / tau);
            float hit = role == MusicRole.Bass ? Math.Max(own, kick) : Math.Max(own, BeatShare * kick);
            _flash[r] = hit;
            // 音が鳴っている間は、どのパーツも下地の明るさ（Body）＋音の強さで光り、出来事でさらに光る（音が止まって門が閉じたら下地は消える）
            float body = active ? BodyLevel + BodyRange * level : 0.6f * level;
            // 出来事の光は下地に重ねる（下地より弱い光でも、光ったことが分かるように）
            float x = body + hit * (1 - body);
            _glow[r] = Math.Max(x, _glow[r] * EffectContext.Decay(c.Dt, tau));
            double off = role switch { MusicRole.Bass => 0, MusicRole.Mid => 1 / 3.0, MusicRole.Treble => 2 / 3.0, _ => 0.5 };
            _color[r] = role == MusicRole.Treble && own > 0.05f
                ? OkLab.ToLed(Lab.Lerp(c.ColorLabAt(off), Lab.White, HatWhiteMix * own), c.Gamma)
                : c.ColorAt(off);
        }

        UpdateInstruments(c, beatFl, active);
    }

    // =====================================================================
    // 楽器ごとの役割
    // =====================================================================

    /// <summary>楽器ごとの役割のゾーンがあるか（なければ何もしない。音の高さの役割だけなら 6.15.7 と同じ計算だけ）</summary>
    private static bool HasInstrumentZone(EffectContext c)
    {
        for (int k = 0; k < c.SegmentCount; k++)
            if (MusicRoles.IsInstrument(c.Segments[k].Role)) return true;
        return false;
    }

    private bool SlowInstruments(EffectContext c) => _slow || c.SlowDevice;

    private void UpdateInstruments(EffectContext c, float beatFl, bool active)
    {
        if (!HasInstrumentZone(c)) return;
        var f = c.Frame;
        float dt = c.Dt;
        bool reduce = c.Music.ReduceFlashing;
        _kickLimit.Enabled = _snareLimit.Enabled = _strumLimit.Enabled = _bassLimit.Enabled = reduce;

        // 更新の遅いデバイスは、曲の部分ごとの色だけ
        if (SlowInstruments(c))
        {
            UpdateScene(c, active);
            return;
        }

        // 出来事: キックの光（1 秒に 3 回まで）、スネア・ハイハットの粒
        var ev = c.Events;
        var (hatGain, hatGap) = MusicTuning.HatResponse(c.Music);
        for (int i = 0; i < ev.Count; i++)
        {
            var e = ev[i];
            float s = Math.Clamp(0.55f + 0.25f * e.Strength, 0, 1);
            switch (e.Kind)
            {
                case AudioEventKind.Kick:
                    _ihitT[(int)MusicRole.Kick] = e.T;
                    _ihitS[(int)MusicRole.Kick] = s * _kickLimit.Decide(e.T);
                    Mark(MusicRole.Kick, 1, e.T);
                    RememberDrum(e.T);
                    break;
                case AudioEventKind.Snare:
                {
                    RememberDrum(e.T);
                    // 持続音の音（声・金管・ベース）をスネアと取り違えた出来事では光らせない（高音に打楽器の分がない）
                    float g = DrumConfidence(c, e.T);
                    if (g < 0.05f) break;
                    _ihitT[(int)MusicRole.Snare] = e.T;
                    _ihitS[(int)MusicRole.Snare] = g * s * _snareLimit.Decide(e.T);   // 小さいゾーン（全体で光る）の分
                    Mark(MusicRole.Snare, g, e.T);
                    SpawnSnare(c, e.T, e.Strength, g);
                    break;
                }
                case AudioEventKind.Hat:
                {
                    float g = DrumConfidence(c, e.T);
                    if (g < 0.05f) break;
                    Mark(MusicRole.Hat, g, e.T);
                    // 大きいゾーンは粒（LED ごとに 1 秒 3 回まで）なので「強さ」の間隔のまま。小さいゾーンの色の変化は SmallHatGapSec より詰めない
                    if (!(e.T - _lastHatT < hatGap && e.T >= _lastHatT))
                    {
                        _lastHatT = e.T;
                        SpawnHat(c, e.T, e.Strength, hatGain * g);
                    }
                    if (!(e.T - _lastSmallHatT < Math.Max(hatGap, SmallHatGapSec) && e.T >= _lastSmallHatT))
                    {
                        _lastSmallHatT = e.T;
                        _smallHat = Math.Max(_smallHat, g * hatGain * (0.6f + 0.4f * Math.Clamp((e.Strength - 1f) / 2f, 0f, 1f)));
                    }
                    break;
                }
            }
        }
        _smallHat *= EffectContext.Decay(dt, SmallHatTauSec);

        // 音名・和音の向き（12 音を 1 周にした単位円の上でならす。B と C の間で色が跳ばないように）
        if (f.BassPitchClass >= 0 && f.BassLevel > 0.05f) Turn(ref _bassX, ref _bassY, f.BassPitchClass / 12.0, dt, BassHueSec);
        if (f.PitchClass >= 0 && f.PitchClarity > 0.3f) Turn(ref _voxX, ref _voxY, f.PitchClass / 12.0, dt, VocalHueSec);
        if (ChordAngle(f, out double chord))
        {
            Turn(ref _chordX, ref _chordY, chord, dt, ChordHueSec);
            Turn(ref _padX, ref _padY, chord, dt, PadHueSec);
        }

        UpdateStrum(c);
        // 下地は、出来事が続いている間は一定にする（出来事のたびに上がってゆっくり下がるのこぎりの形だと、ちらつき止めの段で上下に見える）
        foreach (var pr in PresenceRoles)
        {
            int r = (int)pr;
            if (c.T - _presHoldT[r] > PresenceHoldSec) _presHold[r] = 0;
            _drumPresence[r] = EffectContext.Follow(_drumPresence[r], _presHold[r], dt, PresenceUpSec, PresenceDownSec);
        }
        // ベースの音の出だし: 出だしの値が上がり始めたコマで 1 回のふくらみにする（ゾーン全体なので、点滅の制限は和音と同じ）
        if (f.BassOnset > BassOnsetMin && f.BassOnset > _prevBassOnset + 0.05f)
        {
            if (c.T - _bassSwellT > StrumGapSec)
            {
                _bassSwellT = c.FrameTime;
                _bassSwellS = Math.Clamp(f.BassOnset, 0, 1) * _bassLimit.Decide(c.FrameTime);
            }
        }
        _prevBassOnset = f.BassOnset;

        _activeMix = EffectContext.Follow(_activeMix, active ? 1f : 0f, dt, ActiveUpSec, ActiveDownSec);
        float kickTau = (float)BassTauSec;
        // キックの曲でだけ、予測した拍でも光る（ベースやギターでテンポをつかんだときに、キックのパーツが光らないように）
        float kickBeat = c.T - _ihitT[(int)MusicRole.Kick] < KickBeatSec ? beatFl : 0f;
        for (int r = (int)MusicRole.Kick; r < RoleCount; r++)
        {
            var role = (MusicRole)r;
            // 打楽器は、出来事が続いているか（PresenceHoldSec の間保つ）。ほかは手がかりの値を、決まった幅で 0〜1 にする
            // （山で割ると、鳴っていない楽器のわずかな値まで大きくしてしまう）
            float level = role switch
            {
                MusicRole.Kick or MusicRole.Snare or MusicRole.Hat => _drumPresence[r],
                MusicRole.BassLine => Span01(f.BassLevel, BassFrom, BassSpan),
                MusicRole.Vocal => Span01(Math.Max(f.Vocal, f.Brass), TextureFrom, TextureSpan),
                // 和音: ギター・鍵盤らしさか、最近はじいた瞬間があったか（ベースと重なると、らしさは出にくい）
                MusicRole.Harmony => Math.Max(Span01(f.GuitarKeys, GuitarFrom, GuitarSpan), StrumPresenceShare * _drumPresence[r]),
                _ => Span01(f.Pad, TextureFrom, TextureSpan),
            };
            float range = MusicRoles.IsDrum(role) ? BodyRange : ToneRange;
            // 音が鳴っているかの切り替えは、なめらかにする（一瞬だけ門が閉じたときに、下地が段になって下がって戻らないように）
            float body = 0.6f * level + _activeMix * (BodyLevel + range * level - 0.6f * level);
            float hit = role switch
            {
                // キックはビートの光（予測した拍）とも合わせる（キックを検出しそこねた拍でも光る。ビートの光は BeatDriver が 1 秒 3 回までにしている）
                MusicRole.Kick => Math.Max(_ihitS[r] * (float)Math.Exp(-Math.Max(0, c.T - _ihitT[r]) / kickTau), kickBeat),
                // スネアの全体の光は小さいゾーンだけ（Render で使う）。大きいゾーンは粒
                MusicRole.BassLine => BassSwell * _bassSwellS * (0.3f + 0.7f * level) * (float)Math.Exp(-Math.Max(0, c.T - _bassSwellT) / BassSwellTauSec),
                MusicRole.Harmony => StrumSwell * _strumS * (float)Math.Exp(-Math.Max(0, c.T - _strumT) / StrumTauSec),
                _ => 0f,
            };
            float x = body + hit * (1 - body);
            if (role == MusicRole.Pad)
            {
                // パッドは点滅させず、ゆっくり明るさを変える
                _padLevel = EffectContext.Follow(_padLevel, x, dt, PadUpSec, PadDownSec);
                x = _padLevel;
                _iglow[r] = x;
            }
            else
            {
                double tau = role switch { MusicRole.Kick => BassTauSec, MusicRole.Snare => MidTauSec, MusicRole.Hat => TrebleTauSec, _ => AllTauSec };
                _iglow[r] = Math.Max(x, _iglow[r] * EffectContext.Decay(dt, tau));
            }
            double off = OffsetOf(role) + role switch
            {
                MusicRole.BassLine => PitchHueRange * _bassY,
                MusicRole.Vocal => PitchHueRange * _voxY,
                MusicRole.Harmony => PitchHueRange * _chordY,
                MusicRole.Pad => PitchHueRange * _padY,
                _ => 0,
            };
            _icolor[r] = c.ColorAt(off);
            if (role == MusicRole.Pad) _padEnd = c.ColorAt(off + PadSpread);
        }
        // ボーカルの光のかたまりの場所（音名の向きの sin。B と C の間でも跳ばない）
        _vocalPos = 0.5f + 0.35f * _voxY;
    }

    /// <summary>a〜a + span を 0〜1 にする</summary>
    private static float Span01(float x, float from, float span) => Math.Clamp((x - from) / span, 0f, 1f);

    private void Mark(MusicRole r, float g, double t)
    {
        int i = (int)r;
        _presHold[i] = IsNewRun(i, t) ? g : Math.Max(_presHold[i], g);
        _presHoldT[i] = Math.Max(_presHoldT[i], t);
    }

    /// <summary>前の出来事から PresenceHoldSec より空いた（保っていた強さを新しい値にする）</summary>
    private bool IsNewRun(int i, double t) => t - _presHoldT[i] > PresenceHoldSec;

    private void RememberDrum(double t)
    {
        _drumT[_drumHead] = t;
        _drumHead = (_drumHead + 1) % _drumT.Length;
    }

    /// <summary>
    /// スネア・ハイハットの出来事が、本当に打楽器の音か（0〜1）。出来事の直後（12 ミリ秒まで）の分析結果で、
    /// 高音の打楽器の分があり（PercHigh）、高音の持続音の分（HarmHigh）より大きく下回らないこと。
    /// 合成音で、ドラムのスネア・ハイハットは差 −0.05〜+0.2、声・ギター・金管の音を取り違えた出来事は −0.3〜−0.7、
    /// ベースの音を取り違えた出来事は高音の打楽器の分が 0。分析結果の記録がなければ（テストの合成した入力）1
    /// </summary>
    private float DrumConfidence(EffectContext c, double t)
    {
        if (c.Features is not { } ring) return 1f;
        float diff = float.NegativeInfinity, perc = 0;
        for (int k = 0; k < 3; k++)
        {
            if (!ring.Sample(t + 0.002 + 0.005 * k, _probe)) continue;
            diff = Math.Max(diff, _probe.PercHigh - _probe.HarmHigh);
            perc = Math.Max(perc, _probe.PercHigh);
        }
        if (float.IsNegativeInfinity(diff)) return 1f;
        return Span01(diff, -0.35f, 0.3f) * Span01(perc, 0.02f, 0.08f);
    }

    /// <summary>
    /// 和音をはじいた瞬間: 1〜2 kHz の立ち上がりの強さ（Punch・中音の強調）が、直前の谷（上がるときだけゆっくり追う）と最近の平均より大きく上がったら候補にする。
    /// StrumWaitSec 待って、候補の前後 StrumVetoSec にキック・スネアがなければ光らせる（ドラムの音を和音と数えない。ハイハットは
    /// 8 分で和音と同じ時刻に鳴ることが多いので見ない）。持続音の中音・高音がなければ（ドラムだけ）光らせない
    /// </summary>
    private void UpdateStrum(EffectContext c)
    {
        var pk = c.Peak;
        float dt = c.Dt;
        float x = (pk.Punch[5] + pk.Punch[6] + pk.MidE) / 3f;
        float rise = Math.Max(0, x - _strumSlow);
        // 下がるときはすぐ追い、上がるときはゆっくり追う（前の音の残りで下がっている途中の立ち上がりも見つける）
        _strumSlow = x < _strumSlow ? x : _strumSlow + (x - _strumSlow) * (1 - EffectContext.Decay(dt, StrumSlowSec));
        _riseMean += (rise - _riseMean) * (1 - EffectContext.Decay(dt, StrumMeanSec));
        float tonal = Span01(0.5f * (c.Frame.HarmMid + c.Frame.HarmHigh), 0.15f, 0.3f);
        if (double.IsNaN(_candT) && c.T - _lastCandT >= StrumGapSec && rise > Math.Max(0.05f, 2f * _riseMean) && tonal > 0)
        {
            _candT = c.T;
            // 声・金管・パッドらしい音の出だしは、それぞれの役割の音なので弱める
            float other = Span01(Math.Max(Math.Max(c.Frame.Vocal, c.Frame.Brass), c.Frame.Pad), TextureFrom, TextureSpan);
            _candS = Math.Clamp(rise * 8f, 0.5f, 1f) * tonal * (1 - 0.8f * other);
            _lastCandT = c.T;
        }
        if (!double.IsNaN(_candT) && c.T >= _candT + StrumWaitSec)
        {
            bool drum = false;
            foreach (double d in _drumT) drum |= Math.Abs(d - _candT) < StrumVetoSec;
            if (!drum)
            {
                // ゾーン全体がふくらむので、「激しい点滅を抑える」なら 1 秒に 3 回まで（それより詰まったものは弱める）
                _strumT = _candT;
                _strumS = _candS * _strumLimit.Decide(_candT);
                Mark(MusicRole.Harmony, _candS, _candT);
            }
            _candT = double.NaN;
        }
    }

    /// <summary>単位円の上の向き (x, y) を、位置 u（0〜1 で 1 周）へ時定数 tau でならす</summary>
    private static void Turn(ref float x, ref float y, double u, float dt, double tau)
    {
        float k = 1 - EffectContext.Decay(dt, tau);
        double a = 2 * Math.PI * u;
        x += ((float)Math.Cos(a) - x) * k;
        y += ((float)Math.Sin(a) - y) * k;
        float len = MathF.Sqrt(x * x + y * y);
        if (len > 1e-4f) { x /= len; y /= len; }
    }

    /// <summary>和音の向き: 12 音の色を 5 度圏に並べた向き（近い調の和音は近い色になる）。はっきりしなければ false</summary>
    private static bool ChordAngle(AudioFrame f, out double u)
    {
        double sx = 0, sy = 0, sum = 0;
        for (int i = 0; i < 12; i++)
        {
            double w = f.Chroma[i];
            if (!(w > 0)) continue;
            double a = 2 * Math.PI * (i * 7 % 12) / 12.0;
            sx += w * Math.Cos(a);
            sy += w * Math.Sin(a);
            sum += w;
        }
        u = 0;
        if (sum < 1e-3 || Math.Sqrt(sx * sx + sy * sy) < 0.2 * sum) return false;
        u = Math.Atan2(sy, sx) / (2 * Math.PI);
        return true;
    }

    /// <summary>スネア: スネアの役割の大きいゾーンに、続けて並んだ LED のまとまりを置く（場所はスネアのたびに黄金比ずつ進む）</summary>
    private void SpawnSnare(EffectContext c, double t0, float strength, float gain)
    {
        _snarePos = (_snarePos + SnareStep) % 1.0;
        var col = c.ColorAt(OffsetOf(MusicRole.Snare)) * (gain * c.SectionGain(0.6f));
        float scale = Math.Clamp(0.6f + 0.2f * strength, 0.6f, 1f);
        for (int k = 0; k < c.SegmentCount; k++)
        {
            var s = c.Segments[k];
            if (s.Role != MusicRole.Snare || s.Small) continue;
            int len = Math.Clamp((int)MathF.Round(SnareLength * s.Count * scale), 2, Math.Max(2, s.Count / 3));
            int start = (int)(_snarePos * s.Count);
            for (int q = 0; q < len; q++)
            {
                // まとまりの両端を少し暗くして、光の塊に見えるようにする
                float edge = len <= 2 ? 1f : 1f - 0.5f * MathF.Abs(2f * q / (len - 1) - 1f);
                SetParticle(s.Start + (start + q) % s.Count, t0, (float)MidTauSec, col * edge);
            }
        }
    }

    /// <summary>ハイハット: ハイハットの役割の大きいゾーンに、離れた LED を 1〜2 個、白に近い色で</summary>
    private void SpawnHat(EffectContext c, double t0, float strength, float gain)
    {
        float level = gain * c.SectionGain(0.6f) * (0.6f + 0.4f * Math.Clamp((strength - 1f) / 2f, 0f, 1f));
        var col = OkLab.ToLed(Lab.Lerp(c.ColorLabAt(OffsetOf(MusicRole.Hat)), Lab.White, HatParticleWhite), c.Gamma) * level;
        for (int k = 0; k < c.SegmentCount; k++)
        {
            var s = c.Segments[k];
            if (s.Role != MusicRole.Hat || s.Small) continue;
            int n = Math.Clamp((int)MathF.Round(0.02f * s.Count * strength * gain), 1, Math.Max(1, s.Count / 12));
            for (int q = 0; q < n; q++)
            {
                // 少し前に粒が光った LED は避けて、別の LED を選ぶ（見つからなければ、この粒は出さない）
                for (int tryNo = 0; tryNo < 4; tryNo++)
                    if (SetParticle(s.Start + c.Rng.NextInt(s.Count), t0, (float)TrebleTauSec, col)) break;
            }
        }
    }

    /// <summary>LED j に、時刻 t0 に光る粒を置く。同じ LED には ParticleGapSec より詰めて置かない（置かなければ false）</summary>
    private bool SetParticle(int j, double t0, float tau, LinColor col)
    {
        if (j < 0 || j >= _pT0.Length) return false;
        if (t0 - _pT0[j] < ParticleGapSec && t0 >= _pT0[j]) return false;
        _pT0[j] = t0;
        _pTau[j] = tau;
        _pCol[j] = col;
        return true;
    }

    /// <summary>
    /// 更新の遅いデバイス: 楽器ごとの目立ち方を曲の部分の長さ（SectionSec）でならし、目立つ楽器ほど重くして色を混ぜる（SceneColor）。
    /// 分け方「ドラムだけ」なら、キック・スネア・ハイハットだけで混ぜる。一瞬の光はなく、明るさは盛り上がりでゆっくり変わる
    /// </summary>
    private void UpdateScene(EffectContext c, bool active)
    {
        var f = c.Frame;
        float dt = c.Dt;
        bool drumsOnly = MusicRoleSets.Normalize(c.Music.RoleSet) == MusicRoleSets.Drums;
        if (f.GateOpen)
        {
            float k = 1 - EffectContext.Decay(dt, SectionSec);
            for (int r = (int)MusicRole.Kick; r < RoleCount; r++)
            {
                var role = (MusicRole)r;
                float x = drumsOnly && !MusicRoles.IsDrum(role) ? 0f : role switch
                {
                    MusicRole.Kick => f.PercLow,
                    MusicRole.Snare => f.PercMid,
                    MusicRole.Hat => 0.7f * f.PercHigh,
                    MusicRole.BassLine => 0.8f * f.BassLevel,
                    MusicRole.Vocal => 1.6f * Math.Max(f.Vocal, f.Brass),
                    MusicRole.Harmony => 1.6f * f.GuitarKeys,
                    _ => 1.6f * f.Pad,
                };
                _presence[r] += (x - _presence[r]) * k;
            }
        }
        float top = 0;
        for (int r = (int)MusicRole.Kick; r < RoleCount; r++) top = Math.Max(top, _presence[r]);
        var scene = new SceneColor();
        int m = 0;
        if (top > 1e-4f)
        {
            for (int r = (int)MusicRole.Kick; r < RoleCount; r++)
            {
                // いちばん目立つ楽器の色に寄せる（4 乗。ほぼ同じくらいの楽器どうしなら混ざる）
                float w = MathF.Pow(_presence[r] / top, 4);
                if (w < 0.01f) continue;
                var col = c.ColorAt(OffsetOf((MusicRole)r));
                float mc = Math.Max(col.MaxChannel, 1e-4f);
                _sceneBuf[m++] = col * (w / mc);
            }
            scene.Add(_sceneBuf.AsSpan(0, m));
        }
        if (scene.TryGet(out var mixed, out _))
        {
            _sceneColor = mixed;
            _hasScene = true;
        }
        else if (!_hasScene)
        {
            _sceneColor = c.ColorAt(0);
        }
        float target = active ? BodyLevel + 0.25f + 0.35f * c.Section : 0f;
        _sceneGlow = EffectContext.Follow(_sceneGlow, target, dt, SceneUpSec, SceneDownSec);
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        int r = (int)s.Role;
        if (r < Roles)
        {
            var col = _color[r] * c.Ilin(_glow[r]);
            for (int j = s.Start; j < s.Start + s.Count; j++) dst[j] = col;
            return;
        }
        if (SlowInstruments(c))
        {
            var sc = _sceneColor * c.Ilin(_sceneGlow);
            for (int j = s.Start; j < s.Start + s.Count; j++) dst[j] = sc;
            return;
        }
        var role = s.Role;
        float glow = _iglow[r];
        var baseCol = _icolor[r];
        switch (role)
        {
            case MusicRole.Snare when s.Small:
            {
                // 小さいゾーン: スネアでゾーン全体が光る（1 秒に 3 回まで。_snareLimit）
                float hit = _ihitS[r] * (float)Math.Exp(-Math.Max(0, c.T - _ihitT[r]) / MidTauSec);
                var col = baseCol * c.Ilin(glow + hit * (1 - glow));
                for (int j = s.Start; j < s.Start + s.Count; j++) dst[j] = col;
                return;
            }
            case MusicRole.Hat when s.Small:
            {
                // 小さいゾーン: 明るさはほとんど変えず、色を白へ寄せる（ゾーン全体の点滅に見えないように）
                var lab = Lab.Lerp(c.ColorLabAt(OffsetOf(MusicRole.Hat)), Lab.White, SmallHatWhite * Math.Clamp(_smallHat, 0, 1));
                var col = OkLab.ToLed(lab, c.Gamma) * c.Ilin(Math.Min(1f, glow + SmallHatLift * _smallHat));
                for (int j = s.Start; j < s.Start + s.Count; j++) dst[j] = col;
                return;
            }
            case MusicRole.Snare or MusicRole.Hat:
            {
                // 下地の上に粒を重ねる
                var col = baseCol * c.Ilin(glow);
                double t = c.T;
                for (int j = s.Start; j < s.Start + s.Count; j++)
                {
                    double age = t - _pT0[j];
                    var x = col;
                    if (age >= 0 && age < _pTau[j] * 8) x += _pCol[j] * (float)Math.Exp(-age / _pTau[j]);
                    dst[j] = x.Clamp01();
                }
                return;
            }
            case MusicRole.Vocal when !s.Small:
            {
                // 音名の位置に光のかたまり（その外も VocalBlobBase の明るさ）
                for (int j = s.Start; j < s.Start + s.Count; j++)
                {
                    float d = MathF.Abs(c.P[j] - _vocalPos) / VocalBlobHalf;
                    float blob = d >= 1 ? 0f : 0.5f + 0.5f * MathF.Cos(MathF.PI * d);
                    float g = BodyLevel + (glow - BodyLevel) * (VocalBlobBase + (1 - VocalBlobBase) * blob);
                    dst[j] = baseCol * c.Ilin(Math.Max(0, g));
                }
                return;
            }
            case MusicRole.Pad when !s.Small:
            {
                // 端から端へ、少し色をずらす
                for (int j = s.Start; j < s.Start + s.Count; j++)
                    dst[j] = LinColor.Lerp(baseCol, _padEnd, c.P[j]) * c.Ilin(glow);
                return;
            }
            default:
            {
                var col = baseCol * c.Ilin(glow);
                for (int j = s.Start; j < s.Start + s.Count; j++) dst[j] = col;
                return;
            }
        }
    }
}

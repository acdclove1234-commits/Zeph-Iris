// ==================================================
// EnsembleEffect.cs - 光り方「楽器で光る」 (1 つのゾーンの中で、楽器ごとに光り方の形を変える。キックは全体・スネアは片側・
//                     ハイハットは小さな粒・ベースは端のほう・歌は全体の明るさ・ギターや鍵盤は流れる光・パッドはゆっくりした呼吸・
//                     金管は少し白く明るく。単色（パレット「選んだ色だけ」）でも、色ではなく明るさ・場所・動きで楽器が分かる)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 楽器で光る: 1 つのゾーンに、楽器ごとの光の層を重ねる（層の強さ a は 0〜1。重ね方は I = 1 − Π(1 − a)）。
///   下地（全体）: 0.12 + 0.35·Wv·歌 + 0.15·Wbr·パッド·呼吸(t)。歌の大きさで全体がなめらかに明るくなり（上がる 0.25 秒・下がる 0.8 秒）、
///                 パッドは BreathSec の周期でゆっくり呼吸する（拍が分かれば 8 拍を 3〜6 秒に収める。分からなければ 4 秒）
///   キック（全体）: Wk·0.45·exp(−Δt/0.22)。予測した拍でも光るのは、最後のキックから KickBeatSec 秒まで（RoleEffect と同じ）。
///                 キックの光の間は、場所を使う層（ベース・流れる光）を下げて、ゾーン全体を同じ明るさで光らせる（KickDuck）
///   スネア（片側）: Ws·0.55·exp(−Δt/0.18)。光る側はスネアのたびに入れかわる。キックとスネアは同じ FlashLimiter
///                 （ゾーンの広い所が急に明るくなるのはこの 2 つだけなので、合わせて 1 秒に 3 回まで）。
///                 最近 DrumContextSec 秒にキックか打楽器らしいハイハットがあったときだけ（歌・ギターの出だしをスネアと取り違えた出来事では光らせない）
///   ハイハット: 離れた LED が 1〜(N/12) 個だけ光る粒（τ 0.07 秒。1 つの LED は 0.333 秒に 1 回まで）。ゾーン全体は光らせない。
///              単色のときは白を混ぜない（白は金管の印）。キック・スネアと同じ時刻（HatVetoSec 以内）のハイハットは粒にしない
///              （キックの出来事は 1 コマ遅れて届くことがあるので、HatWaitSec 待ってから決める）
///   ベース（端のほう）: p = 0 の側（ケースの下・始まりの側）が、音が続く間だけ明るい（上がる 0.08 秒・下がる 0.35 秒）。出だしで少しふくらむ。
///                     ベースの音の出だしか、キックのない間も続く低音があったときだけ（キックの「ドン」の残りでは光らせない）。
///                     BassTopHz より高い低音（男声・ギターの低い音）は弱める
///   ギター・鍵盤（流れる光）: 光のかたまりが帯に沿って流れ続け（4 拍で 1 周。拍が分からなければ 0.35 周/秒）、
///                           はじいた瞬間（UpdateStrum）ごとに端の外から 1.2 周/秒の波が入ってくる（τ 0.4 秒で消える）。
///                           波が出ている間は、流れ続ける光を弱める（動く光が 1 つに見えるように）。声・金管・パッドらしい音の出だしは波にしない
///   金管: 同じ色のまま少し白く（OKLab で白へ 0.25·Wbra·金管）、全体を 0.12·Wbra·金管 だけ明るく
/// 単色（Palettes.Solid）なら、どの層も選んだ色（c.ColorAt(0)）。色合いは変えず、明るさ・場所・動き（と金管の白さ）だけで分ける。
/// ほかのパレットなら、層ごとに RoleEffect.OffsetOf の位置の色を使う（楽器の AI の場面の色も使える）。
/// 楽器の AI の場面（ResolvedMusic.SceneColors がオンのときの c.Scene）で、層の重み（Pattern）を MusicScene.FadeSec かけて切り替える
/// （歌の部分はなめらか、ソロは動きを大きく）。ドラムの重み（Wk・Ws・Wh）は 1 を超えない（点滅の量は場面で変わらない）。
/// 小さいゾーン（6 個未満）は場所を使う層を使わない。
/// 更新の遅いデバイス（メモリ・直接書き込むグラボ・OpenRGB を通すグラボ。1 秒に 4〜10 回しか送れない）は、一瞬の光も場所の動きも見せられないので、
/// ゾーン全体を 1 色で、次のように光らせる（UpdateSlow）:
///   明るさを変えてよいデバイス: 下地 0.42 + 0.22·Section + 0.12·歌 + 0.08·ギター鍵盤 + 0.06·パッド·呼吸（0.78 まで。上がる 0.6 秒・下がる 1.5 秒）に、
///     拍のふくらみ（SwellAmp。τ SwellTauSec で戻る。SwellGapSec 以上あける = 1 秒に 2.4 回まで）を足す。拍は、テンポをつかんでいれば予測した拍、
///     つかんでいなければキック・スネア（ドラムがなければ強い立ち上がり）。ふくらみのコマは定期便を待たずに送る（c.SendNow）
///   明るさを変えないグラボ（c.FixedBrightness）: 拍で白へ AccentWhite 寄せて AccentWhiteSec 保ち、一度に戻す（戻すコマは差が小さくても送る）
///   色: 単色（選んだ色だけ）は選んだ色のまま（明るさだけ）。ほかは、目立つ楽器（歌・パッド・ギター鍵盤・金管・ベース・ドラム）の色
///     （RoleEffect.OffsetOf の位置。ビートでは進めない）。別の楽器が DomRatio 倍 + DomMargin より大きい状態が DomHoldSec 続いたら替え、
///     DomFadeSec かけて移る（明るさを変えないグラボは、次の拍で一度に替える）。
/// 「AI で楽器を聞き分ける」がオフなら場面はいつもなし（リズムの重み）。毎コマの処理では new しない
/// </summary>
public sealed class EnsembleEffect : IMusicEffect
{
    public const float BodyLevel = 0.12f, VocalBody = 0.35f, BreathBody = 0.15f;
    public const float KickAmp = 0.45f, SnareAmp = 0.55f, HatAmp = 0.5f, BassAmp = 0.5f, FlowAmp = 0.4f;
    public const float BassSwellShare = 0.2f, BrassWhite = 0.25f, BrassLift = 0.12f;
    public const double KickTauSec = 0.22, SnareTauSec = 0.18, HatTauSec = 0.07, BassSwellTauSec = 0.15;
    public const double KickBeatSec = 2.0, ParticleGapSec = 0.333;
    /// <summary>キック・スネアからこの秒数以内のハイハットは粒にしない</summary>
    public const double HatVetoSec = 0.03, HatWaitSec = 0.02;
    /// <summary>スネアを光らせるのは、この秒数以内にキックか打楽器らしいハイハットがあったときだけ</summary>
    public const double DrumContextSec = 2.0;
    /// <summary>
    /// キックの光の間、場所を使う層（ベース・流れる光）を下げる割合（× キックの強さ）。キックの光はゾーン全体で
    /// 同じ明るさに見せる（キックの「ドン」の残りは、ベースの大きさにも少し出るので、そのままだと端だけ明るいキックになる）
    /// </summary>
    public const float KickDuck = 1.0f;
    /// <summary>ベースを光らせ続ける秒数（音の出だし・キックのない間の低音から）・キックのない間とみなす秒数・光らせるのをやめるときの時定数（秒）</summary>
    public const double BassHoldSec = 2.0, BassNoKickSec = 0.6, BassPresDownSec = 0.6;
    /// <summary>ベースとして光らせる低い音の高さ（Hz。BassTopHz から BassTopHz + BassTopSpanHz で 0 にする）</summary>
    public const float BassTopHz = 140, BassTopSpanHz = 60;
    /// <summary>手がかりを 0〜1 にする幅（RoleEffect と同じ）</summary>
    public const float TextureFrom = 0.15f, TextureSpan = 0.45f, GuitarFrom = 0.12f, GuitarSpan = 0.3f, BassFrom = 0.45f, BassSpan = 0.5f;
    /// <summary>層の明るさの追従（秒。上がる・下がる）</summary>
    public const double VocalUpSec = 0.25, VocalDownSec = 0.8, PadUpSec = 0.6, PadDownSec = 1.2, BassUpSec = 0.08, BassDownSec = 0.35,
        HarmUpSec = 0.08, HarmDownSec = 0.3, BrassUpSec = 0.3, BrassDownSec = 0.8, ActiveUpSec = 0.03, ActiveDownSec = 0.6;
    /// <summary>はじいた瞬間（RoleEffect.UpdateStrum と同じ値）と、その波の速さ（周/秒）・消え方（秒）</summary>
    public const double StrumSlowSec = 0.1, StrumMeanSec = 2.0, StrumGapSec = 0.12, StrumVetoSec = 0.06, StrumWaitSec = 0.02;
    public const double WaveSpeed = 1.2, WaveTauSec = 0.4;
    /// <summary>前の波からこの秒数たつまでは、波を出し直さない</summary>
    public const double WaveRestartSec = 0.35;
    /// <summary>波が出ている間、流れ続ける光を弱める速さ（秒。弱める・戻す）</summary>
    public const double FlowSupUpSec = 0.08, FlowSupDownSec = 0.4;
    public const double PresenceHoldSec = 1.0, PresenceUpSec = 0.25, PresenceDownSec = 0.8;
    public const float StrumPresenceShare = 0.7f;
    /// <summary>流れる光の速さ（拍が分かれば 4 拍で 1 周、分からなければ周/秒）と、かたまりの半分の幅（ゾーンに対する割合）</summary>
    public const double FlowBeats = 4, FlowNoTempo = 0.35;
    public const float FlowHalfWidth = 0.18f;
    /// <summary>パッドの呼吸の周期: 拍が分かれば 8 拍（MinBreathSec〜MaxBreathSec に収める）、分からなければ BreathSec。周期の変化をならす秒数</summary>
    public const double BreathBeats = 8, MinBreathSec = 3, MaxBreathSec = 6, BreathSec = 4, BreathFollowSec = 2;
    /// <summary>更新の遅いデバイス: 下地の明るさ（いちばん下・盛り上がり・歌・ギター鍵盤・パッドの呼吸の分）と上限、追従（秒。上がる・下がる）</summary>
    public const float SlowBase = 0.42f, SlowSectionShare = 0.22f, SlowVocalShare = 0.12f, SlowHarmShare = 0.08f, SlowPadShare = 0.06f, SlowBaseMax = 0.78f;
    public const double SlowUpSec = 0.6, SlowDownSec = 1.5;
    /// <summary>更新の遅いデバイス: 拍のふくらみの大きさ・戻る時定数（秒）・次のふくらみまで最低あける秒数</summary>
    public const float SwellAmp = 0.20f;
    public const double SwellTauSec = 0.35, SwellGapSec = 0.42;
    /// <summary>テンポをつかんでいないとき: ドラムが AccentNoDrumSec 秒なければ、強さ AccentOnsetMin 以上の立ち上がりも拍の代わりにする</summary>
    public const float AccentOnsetMin = 1.5f;
    public const double AccentNoDrumSec = 1.5;
    /// <summary>目立つ楽器の色を替える条件（DomRatio 倍 + DomMargin より大きい状態が DomHoldSec 続く）と、移る秒数。金管の色は白へ DomBrassWhite 寄せる</summary>
    public const float DomRatio = 1.25f, DomMargin = 0.02f, DomBrassWhite = 0.2f;
    public const double DomHoldSec = 2.0, DomFadeSec = 1.5;
    /// <summary>目立つ楽器の比べ方: ベース・ドラムは少し割り引く（ほとんどの曲で鳴り続けるので、ほかの楽器が目立つときはそちらの色にする）</summary>
    public const float DomBassShare = 0.8f, DomDrumShare = 0.7f;
    /// <summary>明るさを変えないグラボ: 拍で白へ寄せる量と、保つ秒数</summary>
    public const float AccentWhite = 0.22f;
    public const double AccentWhiteSec = 0.18;

    /// <summary>
    /// 場面ごとの層の重み。Wk・Ws・Wh はドラム（1 を超えない）、Speed は流れる光の速さの倍率、Width はかたまりの幅の倍率、
    /// VocalMul・BrassMul は声と金管の取り違えを場面で減らす倍率（歌の場面では金管らしさを半分、金管の場面では声らしさを半分）
    /// </summary>
    public readonly record struct Pattern(float Wk, float Ws, float Wh, float Wbass, float Wv, float Wf, float Speed, float Wbr, float Wbra,
        float Width = 1f, float VocalMul = 1f, float BrassMul = 1f);

    /// <summary>SceneGroup の番号順（None・Vocal・Brass・Guitar・Keys・Synth・Strings・Speech）</summary>
    public static readonly Pattern[] Patterns =
    {
        new(1.0f, 1.0f, 1.0f, 1.0f, 0.8f, 0.8f, 1.0f, 1.0f, 0.8f),                            // None: リズム
        new(0.75f, 0.7f, 0.6f, 0.8f, 1.2f, 0.6f, 0.7f, 1.0f, 0.5f, BrassMul: 0.5f),           // Vocal: なめらか
        new(0.8f, 0.8f, 0.8f, 0.9f, 0.6f, 1.2f, 1.4f, 0.8f, 1.2f, Width: 1.3f, VocalMul: 0.5f), // Brass: ソロ
        new(0.8f, 0.8f, 0.8f, 0.9f, 0.6f, 1.3f, 1.4f, 0.8f, 0.6f, Width: 1.3f),              // Guitar: ソロ
        new(0.8f, 0.7f, 0.8f, 0.9f, 0.8f, 1.2f, 1.0f, 1.0f, 0.6f),                            // Keys: 流れる
        new(1.0f, 0.9f, 0.9f, 1.0f, 0.8f, 1.0f, 1.2f, 1.3f, 0.6f),                            // Synth: 広がる
        new(0.8f, 0.75f, 0.7f, 0.9f, 0.9f, 0.8f, 0.8f, 1.5f, 0.6f),                           // Strings: 呼吸
        new(0.5f, 0.5f, 0.5f, 0.7f, 1.2f, 0.5f, 0.6f, 1.0f, 0.5f),                            // Speech: 落ち着いた
    };

    public static Pattern PatternOf(SceneGroup g) => (int)g < Patterns.Length ? Patterns[(int)g] : Patterns[0];

    /// <summary>場面 scene の時刻 t の重み（前の場面の重みから、場面の移り変わりと同じ smoothstep で）</summary>
    public static Pattern Blend(MusicScene scene, double t)
    {
        var a = PatternOf(scene.From);
        var b = PatternOf(scene.Group);
        if (a == b) return b;
        float u = scene.Progress(t);
        if (u >= 1) return b;
        if (u <= 0) return a;
        static float M(float x, float y, float u) => x + (y - x) * u;
        return new Pattern(M(a.Wk, b.Wk, u), M(a.Ws, b.Ws, u), M(a.Wh, b.Wh, u), M(a.Wbass, b.Wbass, u), M(a.Wv, b.Wv, u), M(a.Wf, b.Wf, u),
            M(a.Speed, b.Speed, u), M(a.Wbr, b.Wbr, u), M(a.Wbra, b.Wbra, u), M(a.Width, b.Width, u), M(a.VocalMul, b.VocalMul, u), M(a.BrassMul, b.BrassMul, u));
    }

    private readonly bool _slow;
    /// <summary>キックとスネア（ゾーンの広い所が急に明るくなるもの）で共有する 1 秒 3 回まで。はじいた瞬間・ベースの出だしは別</summary>
    private readonly FlashLimiter _area = new(), _strumLimit = new(), _bassLimit = new();
    private readonly AudioFrame _probe = new();
    private readonly double[] _drumT = new double[8];
    private int _drumHead;

    private double _kickT = double.NegativeInfinity, _snareT = double.NegativeInfinity, _lastHatT = double.NegativeInfinity, _confidentHatT = double.NegativeInfinity;
    private float _kickS, _snareS, _beatGain = 1f;
    private bool _snareSide;
    private double _bassSwellT = double.NegativeInfinity;
    private float _bassSwellS, _prevBassOnset, _bassPres;
    private double _bassHoldT = double.NegativeInfinity;
    // はじいた瞬間
    private float _strumSlow, _riseMean, _candS, _strumS, _strumHold, _strumPres;
    private double _candT = double.NaN, _lastCandT = double.NegativeInfinity, _strumT = double.NegativeInfinity, _strumHoldT = double.NegativeInfinity;
    // なめらかにした楽器の大きさ
    private float _active, _vocal, _pad, _bass, _harm, _brass, _slowI, _flowSup;
    private double _flow, _breath, _breathSec = BreathSec;
    // まだ粒にしていないハイハット（HatWaitSec 待つ）
    private readonly double[] _hatT = new double[8], _hatSeen = new double[8];
    private readonly float[] _hatStrength = new float[8];
    private int _hatCount;
    // 粒（LED ごと。ハイハット）
    private double[] _pT0 = Array.Empty<double>();
    private float[] _pAmp = Array.Empty<float>();
    private LinColor[] _pCol = Array.Empty<LinColor>();
    // 更新の遅いデバイス: 拍のふくらみ・目立つ楽器の色
    private double _accT = double.NegativeInfinity, _slowDrumT = double.NegativeInfinity;
    private float _accA, _slowDrums;
    private bool _whiteOn;
    private int _dom = -1, _domCand = -1;
    private double _domCandT, _domFadeT = double.NegativeInfinity;
    private Lab _domFrom, _slowLab;
    private readonly float[] _domLevel = new float[DomCount];
    /// <summary>目立つ楽器の色の位置: 0 歌、1 パッド、2 ギター・鍵盤、3 金管（歌の位置を白へ寄せる）、4 ベース、5 ドラム</summary>
    private static readonly double[] DomOffsets = { RoleEffect.OffsetOf(MusicRole.Vocal), RoleEffect.OffsetOf(MusicRole.Pad), RoleEffect.OffsetOf(MusicRole.Harmony),
        RoleEffect.OffsetOf(MusicRole.Vocal), RoleEffect.OffsetOf(MusicRole.BassLine), RoleEffect.OffsetOf(MusicRole.Kick) };
    private const int DomCount = 6, DomBrass = 3;

    // ---- コマごとに Update が決めて、Render が使う値
    private Pattern _w = Patterns[0];
    private bool _mono;
    private float _body, _kickA, _snareA, _bassA, _flowA, _waveA, _waveP, _lift, _width;
    /// <summary>層の色: 0 = 下地、1 = キック、2 = スネア、3 = ベース、4 = ギター・鍵盤、5 = 金管、6 = ハイハットの粒</summary>
    private readonly LinColor[] _col = new LinColor[7];
    private readonly float[] _colMax = new float[7];
    private const int CBody = 0, CKick = 1, CSnare = 2, CBass = 3, CFlow = 4, CBrass = 5, CHat = 6;

    public EnsembleEffect(bool slow = false) => _slow = slow;

    public string Name => MusicStyles.Ensemble;

    /// <summary>いまの層の重み（テスト用）</summary>
    public Pattern Weights => _w;

    /// <summary>なめらかにした歌・パッド・ベース・ギターと鍵盤・金管の大きさ（テスト用）</summary>
    public (float Vocal, float Pad, float Bass, float Harm, float Brass) Levels => (_vocal, _pad, _bass, _harm, _brass);

    /// <summary>流れる光・波の強さと、波の位置・流れる光の位置（テスト用）</summary>
    public (float Flow, float Wave, float WaveP, double FlowPos) Motion => (_flowA, _waveA, _waveP, _flow);

    /// <summary>キックとスネアで共有する点滅の制限（テスト用）</summary>
    public FlashLimiter AreaLimiter => _area;

    /// <summary>更新の遅いデバイス: 最後の拍のふくらみの時刻・いまの目立つ楽器（0 歌〜5 ドラム。まだなければ −1）・下地の明るさ（テスト用）</summary>
    public (double AccentT, int Dominant, float Base) SlowState => (_accT, _dom, _slowI);

    public void Reset(EffectContext c)
    {
        _area.Reset();
        _strumLimit.Reset();
        _bassLimit.Reset();
        Array.Fill(_drumT, double.NegativeInfinity);
        _drumHead = 0;
        _kickT = _snareT = _lastHatT = _confidentHatT = double.NegativeInfinity;
        _kickS = _snareS = 0;
        _beatGain = 1f;
        _hatCount = 0;
        _snareSide = false;
        _bassSwellT = double.NegativeInfinity;
        _bassSwellS = _prevBassOnset = _bassPres = 0;
        _bassHoldT = double.NegativeInfinity;
        _strumSlow = _riseMean = _candS = _strumS = _strumHold = _strumPres = 0;
        _candT = double.NaN;
        _lastCandT = _strumT = _strumHoldT = double.NegativeInfinity;
        _active = _vocal = _pad = _bass = _harm = _brass = _slowI = _flowSup = 0;
        _flow = _breath = 0;
        _breathSec = BreathSec;
        _accT = _slowDrumT = double.NegativeInfinity;
        _accA = _slowDrums = 0;
        _whiteOn = false;
        _dom = _domCand = -1;
        _domCandT = 0;
        _domFadeT = double.NegativeInfinity;
        int n = c.LedCount;
        if (_pT0.Length != n)
        {
            _pT0 = new double[n];
            _pAmp = new float[n];
            _pCol = new LinColor[n];
        }
        Array.Fill(_pT0, double.NegativeInfinity);
        Array.Clear(_pAmp);
        Array.Clear(_pCol);
        _w = Patterns[0];
    }

    private bool SlowMode(EffectContext c) => _slow || c.SlowDevice;

    /// <summary>a〜a + span を 0〜1 にする</summary>
    private static float Span01(float x, float from, float span) => Math.Clamp((x - from) / span, 0f, 1f);

    public void Update(EffectContext c)
    {
        var f = c.Frame;
        float dt = c.Dt;
        bool reduce = c.Music.ReduceFlashing;
        _area.Enabled = _strumLimit.Enabled = _bassLimit.Enabled = reduce;
        _mono = c.Pal.Name == Palettes.Solid;

        // 場面の重み（「AI で楽器を聞き分ける」がオフなら場面なし = リズム）
        var scene = c.Music.SceneColors ? c.Scene : MusicScene.None;
        _w = Blend(scene, c.T);
        var w = _w;

        // 楽器の大きさ（どの層も、上がるのに 0.08 秒以上・下がるのに 0.3 秒以上かける）
        bool active = f.Activity > 0.3f;
        _active = EffectContext.Follow(_active, active ? 1f : 0f, dt, ActiveUpSec, ActiveDownSec);
        _vocal = EffectContext.Follow(_vocal, w.VocalMul * Span01(f.Vocal, TextureFrom, TextureSpan), dt, VocalUpSec, VocalDownSec);
        _pad = EffectContext.Follow(_pad, Span01(f.Pad, TextureFrom, TextureSpan), dt, PadUpSec, PadDownSec);
        // ベース: 低い持続音は、キックの「ドン」の残りでも少し出るので、ベースの音の出だしがあったか、キックのない間も続く低音があったときだけ
        // （BassHoldSec の間）光らせる（キックだけの曲で、端だけがいつも明るくならないように）
        var ev = c.Events;
        for (int i = 0; i < ev.Count; i++)
            if (ev[i].Kind == AudioEventKind.Kick) { _kickT = Math.Max(_kickT, ev[i].T); }
        bool bassOnset = f.BassOnset > RoleEffect.BassOnsetMin && f.BassOnset > _prevBassOnset + 0.05f;
        if (bassOnset || (f.BassLevel > BassFrom && c.T - _kickT > BassNoKickSec)) _bassHoldT = c.T;
        _bassPres = EffectContext.Follow(_bassPres, c.T - _bassHoldT < BassHoldSec ? 1f : 0f, dt, BassUpSec, BassPresDownSec);
        // 低い音の高さが BassTopHz より上（男声・ギター・鍵盤の低い音）は、ベースとしては弱める
        float bassLow = f.BassF0Hz > 0 ? 1 - Span01(f.BassF0Hz, BassTopHz, BassTopSpanHz) : 1f;
        _bass = EffectContext.Follow(_bass, _bassPres * bassLow * Span01(f.BassLevel, BassFrom, BassSpan), dt, BassUpSec, BassDownSec);
        _brass =EffectContext.Follow(_brass, w.BrassMul * Span01(f.Brass, TextureFrom, TextureSpan), dt, BrassUpSec, BrassDownSec);

        // パッドの呼吸（周期は拍から。周期が変わっても位相は続けて進める）
        double breathTarget = c.HasTempo && c.Period > 0 ? Math.Clamp(BreathBeats * c.Period, MinBreathSec, MaxBreathSec) : BreathSec;
        _breathSec += (breathTarget - _breathSec) * (1 - EffectContext.Decay(dt, BreathFollowSec));
        _breath += dt / _breathSec;
        if (_breath >= 1) _breath -= Math.Floor(_breath);
        float breath = 0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * _breath);

        // 下地: 歌で全体がなめらかに明るくなり、パッドでゆっくり呼吸する
        float body = _active * (BodyLevel + VocalBody * w.Wv * _vocal + BreathBody * w.Wbr * _pad * breath);

        if (SlowMode(c))
        {
            UpdateSlow(c, breath);
            _prevBassOnset = f.BassOnset;
            return;
        }

        // 出来事: キック・スネアは共有の点滅の制限、ハイハットは粒
        var (hatGain, hatGap) = MusicTuning.HatResponse(c.Music);
        for (int i = 0; i < ev.Count; i++)
        {
            var e = ev[i];
            float s = Math.Clamp(0.55f + 0.25f * e.Strength, 0, 1);
            switch (e.Kind)
            {
                case AudioEventKind.Kick:
                    _kickT = e.T;
                    _kickS = s * _area.Decide(e.T);
                    RememberDrum(e.T);
                    break;
                case AudioEventKind.Snare:
                {
                    // ドラムの曲の中のスネアだけ光らせる（最近 DrumContextSec 秒にキックか、打楽器らしいハイハットがあった）。
                    // 歌・ギターだけの部分で、音の出だしをスネアと取り違えた出来事では光らせない（和音をはじいた瞬間としても数えられるように、ドラムとして覚えない）
                    float g = DrumConfidence(c, e.T);
                    if (g < 0.05f || !(e.T - Math.Max(_kickT, _confidentHatT) < DrumContextSec)) break;
                    RememberDrum(e.T);
                    // 光る側はスネアのたびに入れかえる（ほぼ同時の 2 つの出来事は同じ 1 回）
                    if (e.T - _snareT > FlashLimiter.MergeSec || e.T < _snareT) _snareSide = !_snareSide;
                    _snareT = e.T;
                    _snareS = g * s * _area.Decide(e.T);
                    break;
                }
            }
        }
        // ハイハットは、次のコマ以降（HatWaitSec たってから）粒にする。同じ時刻のハイハットは、キックのカチッという音を取り違えたものが多く、
        // キックの出来事はハイハットより 1 コマ遅れて届くことがあるので、待ってから見る（キック・スネアでゾーンの広い所が光るので、粒は出さない）
        for (int i = 0; i < ev.Count; i++)
        {
            var e = ev[i];
            if (e.Kind != AudioEventKind.Hat || _hatCount >= _hatT.Length) continue;
            _hatT[_hatCount] = e.T;
            _hatStrength[_hatCount] = e.Strength;
            _hatSeen[_hatCount] = c.T;
            _hatCount++;
        }
        for (int k = 0; k < _hatCount;)
        {
            // 時刻が戻った・古すぎるものは捨てる
            bool stale = c.T < _hatSeen[k] - 0.25 || c.T - _hatT[k] > 0.25;
            if (!stale && !(c.T > _hatSeen[k] && c.T >= _hatT[k] + HatWaitSec)) { k++; continue; }
            double ht = _hatT[k];
            float hs = _hatStrength[k];
            _hatCount--;
            _hatT[k] = _hatT[_hatCount];
            _hatStrength[k] = _hatStrength[_hatCount];
            _hatSeen[k] = _hatSeen[_hatCount];
            if (stale || NearDrum(ht)) continue;
            float g = DrumConfidence(c, ht);
            if (g < 0.05f) continue;
            if (g >= 0.5f) _confidentHatT = Math.Max(_confidentHatT, ht);
            if (ht - _lastHatT < hatGap && ht >= _lastHatT) continue;
            _lastHatT = ht;
            // 待った分だけ暗くならないように、粒は今のコマから光らせる
            SpawnHat(c, c.T, hs, hatGain * g * w.Wh);
        }
        // 予測した拍の光も、キック・スネアと同じ点滅の制限にかける（キックの曲で、キックを検出しそこねた拍でも光る）
        if (c.Trigger) _beatGain = _area.Decide(c.TriggerT);

        UpdateStrum(c);
        if (c.T - _strumHoldT > PresenceHoldSec) _strumHold = 0;
        // はじいた後に声・金管・パッドらしさが上がってきたら、その出だしは和音ではなかったので、流れる光は続けない
        float otherNow = Span01(Math.Max(Math.Max(f.Vocal, f.Brass), f.Pad), TextureFrom, TextureSpan);
        _strumPres = EffectContext.Follow(_strumPres, _strumHold * (1 - otherNow), dt, PresenceUpSec, PresenceDownSec);
        _harm = EffectContext.Follow(_harm, Math.Max(Span01(f.GuitarKeys, GuitarFrom, GuitarSpan), StrumPresenceShare * _strumPres), dt, HarmUpSec, HarmDownSec);

        // ベースの音の出だし（RoleEffect と同じ見つけ方）
        if (bassOnset && c.T - _bassSwellT > StrumGapSec)
        {
            _bassSwellT = c.FrameTime;
            _bassSwellS = Math.Clamp(f.BassOnset, 0, 1) * _bassLimit.Decide(c.FrameTime);
        }
        _prevBassOnset = f.BassOnset;

        float beatFl = Math.Max(c.Fl, c.FlPeak * (float)Math.Exp(-Math.Max(0, c.T - c.FlPeakT) / KickTauSec));
        float kickBeat = c.T - _kickT < KickBeatSec ? beatFl * _beatGain : 0f;
        float kick = Math.Max(_kickS * (float)Math.Exp(-Math.Max(0, c.T - _kickT) / KickTauSec), kickBeat);
        float snare = _snareS * (float)Math.Exp(-Math.Max(0, c.T - _snareT) / SnareTauSec);
        float swell = _bassSwellS * (float)Math.Exp(-Math.Max(0, c.T - _bassSwellT) / BassSwellTauSec);

        // 流れる光（4 拍で 1 周）と、はじいた瞬間の波
        double rate = c.HasTempo && c.Period > 0 ? 1 / (FlowBeats * c.Period) : FlowNoTempo;
        _flow += dt * rate * w.Speed * c.Speed;
        if (_flow >= 1) _flow -= Math.Floor(_flow);
        double waveAge = Math.Max(0, c.T - _strumT);

        _body = body;
        _kickA = w.Wk * KickAmp * kick;
        _snareA = w.Ws * SnareAmp * snare;
        float duck = 1 - KickDuck * Math.Clamp(kick, 0, 1);
        _bassA = duck * w.Wbass * BassAmp * Math.Min(1f, _bass + BassSwellShare * swell * _bassPres * bassLow);
        // はじいた瞬間の波が出ている間は、流れ続ける光を弱める（動く光が 2 つ重なって、行き先が分からなくならないように）
        float waveEnv = _strumS * (float)Math.Exp(-waveAge / WaveTauSec);
        _flowSup = EffectContext.Follow(_flowSup, Math.Clamp(waveEnv, 0, 1), dt, FlowSupUpSec, FlowSupDownSec);
        _flowA = duck * w.Wf * FlowAmp * _harm * (1 - _flowSup);
        _waveA = duck * w.Wf * FlowAmp * waveEnv;
        _waveP = (float)(WaveSpeed * waveAge);
        _lift = BrassLift * w.Wbra * _brass;
        _width = FlowHalfWidth * w.Width;
        Colors(c, BrassWhite * w.Wbra * _brass, _vocal, _pad);
    }

    /// <summary>
    /// 更新の遅いデバイス（ゾーン全体を 1 色）: 下地の明るさ + 拍のふくらみ（明るさを変えないグラボは白へ寄せる）と、目立つ楽器の色。
    /// 結果は _body（明るさ）と _col[CBody]（色）
    /// </summary>
    private void UpdateSlow(EffectContext c, float breath)
    {
        var f = c.Frame;
        float dt = c.Dt;
        var ev = c.Events;
        _harm = EffectContext.Follow(_harm, Span01(f.GuitarKeys, GuitarFrom, GuitarSpan), dt, HarmUpSec, HarmDownSec);
        _kickA = _snareA = _bassA = _flowA = _waveA = _lift = 0;

        // 拍: テンポをつかんでいれば予測した拍、つかんでいなければキック・スネア（ドラムがなければ強い立ち上がり）
        bool locked = c.Locked;
        double accT = double.NaN;
        float accS = 0;
        if (locked && c.BeatCrossed) { accT = c.BeatCrossT; accS = 1f; }
        for (int i = 0; i < ev.Count; i++)
        {
            var e = ev[i];
            bool kick = e.Kind == AudioEventKind.Kick;
            // スネアは、ドラムの曲の中のものだけ（速いデバイスと同じ。歌・ギターの出だしを取り違えた出来事では光らせない）
            bool snare = e.Kind == AudioEventKind.Snare && e.T - _kickT < DrumContextSec && DrumConfidence(c, e.T) >= 0.05f;
            if (kick || snare) _slowDrumT = Math.Max(_slowDrumT, e.T);
            if (locked) continue;
            bool use = kick || snare || (e.Kind == AudioEventKind.Onset && e.Strength >= AccentOnsetMin && e.T - _slowDrumT > AccentNoDrumSec);
            if (use && (double.IsNaN(accT) || e.T < accT))
            {
                accT = e.T;
                accS = Math.Clamp(0.6f + 0.25f * (e.Strength - 1), 0.6f, 1f) * (snare ? 0.8f : 1f);
            }
        }
        bool accent = false;
        if (!double.IsNaN(accT) && (accT - _accT >= SwellGapSec || accT < _accT - 1) && _active > 0.5f)
        {
            _accT = accT;
            _accA = accS;
            accent = true;
            // ふくらみ（白へ寄せた色）は、次の定期便を待たずに送る。明るさを変えないグラボは、差が小さくても送る
            c.SendNow = true;
            if (c.FixedBrightness) c.SendEvenIfSmall = true;
        }
        _slowDrums = EffectContext.Follow(_slowDrums, c.T - _slowDrumT < AccentNoDrumSec ? 1f : 0f, dt, 0.3, 1.5);
        float env = double.IsFinite(_accT) ? _accA * (float)Math.Exp(-Math.Max(0, c.T - _accT) / SwellTauSec) : 0f;

        // 下地の明るさ（盛り上がり・歌・ギター鍵盤・パッドの呼吸）と、拍のふくらみ
        float target = _active * (SlowBase + SlowSectionShare * c.Section + SlowVocalShare * _vocal + SlowHarmShare * _harm + SlowPadShare * _pad * breath);
        _slowI = EffectContext.Follow(_slowI, Math.Clamp(target, 0, SlowBaseMax), dt, SlowUpSec, SlowDownSec);
        _body = Math.Clamp(_slowI + SwellAmp * env * _active, 0, 1);

        // 色: 単色は選んだ色のまま。ほかは目立つ楽器の色
        Lab lab;
        if (_mono) lab = c.PalLabAt(0);
        else
        {
            _domLevel[0] = _vocal;
            _domLevel[1] = _pad;
            _domLevel[2] = _harm;
            _domLevel[DomBrass] = _brass;
            _domLevel[4] = DomBassShare * _bass;
            _domLevel[5] = DomDrumShare * _slowDrums;
            int best = 0;
            for (int k = 1; k < DomCount; k++) if (_domLevel[k] > _domLevel[best]) best = k;
            if (_dom < 0) { _dom = best; _domFadeT = double.NegativeInfinity; }
            else if (best != _dom && _domLevel[best] > DomRatio * _domLevel[_dom] + DomMargin)
            {
                if (best != _domCand) { _domCand = best; _domCandT = c.T; }
                else if (c.T - _domCandT >= DomHoldSec && (!c.FixedBrightness || accent))
                {
                    _domFrom = _slowLab;
                    _dom = best;
                    _domCand = -1;
                    // 明るさを変えないグラボは、拍のコマで一度に替える（途中の色を何度も書かない）
                    _domFadeT = c.FixedBrightness ? double.NegativeInfinity : c.T;
                }
            }
            else _domCand = -1;
            lab = DomLab(c, _dom);
            if (double.IsFinite(_domFadeT))
            {
                float u = (float)Math.Clamp((c.T - _domFadeT) / DomFadeSec, 0, 1);
                if (u >= 1 || c.T < _domFadeT) _domFadeT = double.NegativeInfinity;
                else lab = Lab.Lerp(_domFrom, lab, EffectContext.Smoothstep(u));
            }
        }
        _slowLab = lab;

        if (c.FixedBrightness)
        {
            // 明るさを変えないグラボ: 拍で白へ寄せ、AccentWhiteSec たったら一度に戻す（戻すコマは、差が小さくても送る）
            bool hold = c.T - _accT < AccentWhiteSec && c.T >= _accT && _active > 0.5f;
            if (hold)
            {
                lab = Lab.Lerp(lab, Lab.White, AccentWhite * _accA);
                _whiteOn = true;
            }
            else if (_whiteOn)
            {
                _whiteOn = false;
                c.SendEvenIfSmall = true;
            }
            _body = 1f;
        }
        var col = OkLab.ToLed(lab, c.Gamma);
        _col[CBody] = col;
        _colMax[CBody] = col.MaxChannel;
    }

    /// <summary>目立つ楽器 k の色（パレットの決まった位置。金管は白へ少し寄せる）</summary>
    private static Lab DomLab(EffectContext c, int k)
    {
        var lab = c.PalLabAt(DomOffsets[Math.Clamp(k, 0, DomCount - 1)]);
        return k == DomBrass ? Lab.Lerp(lab, Lab.White, DomBrassWhite) : lab;
    }

    /// <summary>
    /// 層ごとの色（単色なら全部選んだ色）。金管で白へ寄せる（同じ色合いのまま鮮やかさだけ下がる）。
    /// 下地の色は、キックの色・歌の色・パッドの色を、歌とパッドの大きさ（v・p）で光の量のまま混ぜる
    /// （パレットの位置を動かすと、歌が入るたびに虹の色を順に通ってしまうので、位置は動かさない）
    /// </summary>
    private void Colors(EffectContext c, float white, float v, float p)
    {
        if (_mono)
        {
            var c0 = white > 1e-4f ? OkLab.ToLed(Lab.Lerp(c.ColorLabAt(0), Lab.White, white), c.Gamma) : c.ColorAt(0);
            for (int k = 0; k < _col.Length; k++) { _col[k] = c0; _colMax[k] = c0.MaxChannel; }
            return;
        }
        Set(CKick, RoleEffect.OffsetOf(MusicRole.Kick), white);
        Set(CSnare, RoleEffect.OffsetOf(MusicRole.Snare), white);
        Set(CBass, RoleEffect.OffsetOf(MusicRole.BassLine), white);
        Set(CFlow, RoleEffect.OffsetOf(MusicRole.Harmony), white);
        Set(CBrass, RoleEffect.OffsetOf(MusicRole.Vocal), white);
        Set(CHat, RoleEffect.OffsetOf(MusicRole.Hat), Math.Max(white, RoleEffect.HatParticleWhite));
        Set(CBody, RoleEffect.OffsetOf(MusicRole.Pad), white);
        float wk = 0.2f, sum = wk + v + p;
        var m = _col[CKick] * wk + _col[CBrass] * v + _col[CBody] * p;
        float target = (_colMax[CKick] * wk + _colMax[CBrass] * v + _colMax[CBody] * p) / sum;
        float mc = m.MaxChannel;
        _col[CBody] = mc > 1e-6f ? m * (target / mc) : _col[CKick];
        _colMax[CBody] = _col[CBody].MaxChannel;

        void Set(int k, double off, float wh)
        {
            var lab = c.ColorLabAt(off);
            var col = wh > 1e-4f ? OkLab.ToLed(Lab.Lerp(lab, Lab.White, wh), c.Gamma) : c.ColorAt(off);
            _col[k] = col;
            _colMax[k] = col.MaxChannel;
        }
    }

    /// <summary>キック・スネアの出来事から HatVetoSec 以内か</summary>
    private bool NearDrum(double t)
    {
        foreach (double d in _drumT)
            if (Math.Abs(d - t) < HatVetoSec) return true;
        return false;
    }

    private void RememberDrum(double t)
    {
        _drumT[_drumHead] = t;
        _drumHead = (_drumHead + 1) % _drumT.Length;
    }

    /// <summary>スネア・ハイハットの出来事が本当に打楽器の音か（0〜1。RoleEffect.DrumConfidence と同じ）</summary>
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

    /// <summary>和音をはじいた瞬間（RoleEffect.UpdateStrum と同じ見つけ方。ドラムの前後は数えない）</summary>
    private void UpdateStrum(EffectContext c)
    {
        var pk = c.Peak;
        float dt = c.Dt;
        float x = (pk.Punch[5] + pk.Punch[6] + pk.MidE) / 3f;
        float rise = Math.Max(0, x - _strumSlow);
        _strumSlow = x < _strumSlow ? x : _strumSlow + (x - _strumSlow) * (1 - EffectContext.Decay(dt, StrumSlowSec));
        _riseMean += (rise - _riseMean) * (1 - EffectContext.Decay(dt, StrumMeanSec));
        float tonal = Span01(0.5f * (c.Frame.HarmMid + c.Frame.HarmHigh), 0.15f, 0.3f);
        if (double.IsNaN(_candT) && c.T - _lastCandT >= StrumGapSec && rise > Math.Max(0.05f, 2f * _riseMean) && tonal > 0)
        {
            _candT = c.T;
            float other = Span01(Math.Max(Math.Max(c.Frame.Vocal, c.Frame.Brass), c.Frame.Pad), TextureFrom, TextureSpan);
            // 声・金管・パッドらしい音の出だしは、それぞれの層（歌の明るさ・金管の白さ・パッドの呼吸）で見せるので、波は出さない
            _candS = Math.Clamp(rise * 8f, 0.5f, 1f) * tonal * (1 - other);
            _lastCandT = c.T;
        }
        if (!double.IsNaN(_candT) && c.T >= _candT + StrumWaitSec)
        {
            bool drum = false;
            foreach (double d in _drumT) drum |= Math.Abs(d - _candT) < StrumVetoSec;
            // 声などで弱めて、ほとんど 0 になった候補は数えない（下地を保つ時間も延ばさない）
            if (!drum && _candS >= 0.05f)
            {
                // 前の波がまだ端の近くにあるうちは、波を出し直さない（位置が戻って見えないように）
                double age = _candT - _strumT;
                if (!(age >= 0 && age < WaveRestartSec))
                {
                    _strumT = _candT;
                    _strumS = _candS * _strumLimit.Decide(_candT);
                }
                _strumHold = _candT - _strumHoldT > PresenceHoldSec ? _candS : Math.Max(_strumHold, _candS);
                _strumHoldT = Math.Max(_strumHoldT, _candT);
            }
            _candT = double.NaN;
        }
    }

    /// <summary>ハイハット: 大きいゾーンの離れた LED を 1〜(N/12) 個（小さいゾーンには出さない）</summary>
    private void SpawnHat(EffectContext c, double t0, float strength, float gain)
    {
        float amp = HatAmp * gain * c.SectionGain(0.6f) * (0.6f + 0.4f * Math.Clamp((strength - 1f) / 2f, 0f, 1f));
        if (amp < 1e-3f) return;
        for (int k = 0; k < c.SegmentCount; k++)
        {
            var s = c.Segments[k];
            if (s.Small) continue;
            int n = Math.Clamp((int)MathF.Round(0.02f * s.Count * strength * gain), 1, Math.Max(1, s.Count / 12));
            for (int q = 0; q < n; q++)
                for (int tryNo = 0; tryNo < 4; tryNo++)
                    if (SetParticle(s.Start + c.Rng.NextInt(s.Count), t0, amp)) break;
        }
    }

    private bool SetParticle(int j, double t0, float amp)
    {
        if (j < 0 || j >= _pT0.Length) return false;
        if (t0 - _pT0[j] < ParticleGapSec && t0 >= _pT0[j]) return false;
        _pT0[j] = t0;
        _pAmp[j] = amp;
        return true;
    }

    /// <summary>1 − (1 − a)(1 − b)</summary>
    private static float Screen(float a, float b) => 1 - (1 - a) * (1 - b);

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        int end = s.Start + s.Count;
        if (SlowMode(c))
        {
            var sc = _col[CBody] * c.Ilin(_body);
            for (int j = s.Start; j < end; j++) dst[j] = sc.FitToOne();
            return;
        }
        if (s.Small)
        {
            // 小さいゾーン: 場所を使う層はなし（全体の明るさだけ。スネアは弱め・ハイハットは出さない）
            float i0 = Screen(_body, _kickA);
            i0 = Screen(i0, 0.4f * _snareA);
            i0 = Screen(i0, 0.3f * _w.Wbass * _bass);
            i0 = Screen(i0, 0.25f * _w.Wf * _harm);
            i0 = Screen(i0, _lift);
            var col = Mix(_body, 0.4f * _snareA, 0.3f * _w.Wbass * _bass, 0.25f * _w.Wf * _harm);
            var x = (col * c.Ilin(i0)).FitToOne();
            for (int j = s.Start; j < end; j++) dst[j] = x;
            return;
        }

        float n = s.Placed ? s.Span : s.Count;
        float w = Math.Max(_width, 2f / Math.Max(1f, n));
        // 流れる光の位置（輪なら 1 周、そうでなければ端の外から入って端の外へ出る）
        float pos = s.Ring ? (float)_flow : (float)(-w + _flow * (1 + 2 * w));
        // 波は端の外から入ってくる（端の LED が 1 コマで急に明るくならないように）
        float wave = _waveA > 1e-3f ? _waveP - w : float.NaN;
        double t = c.T;
        float bodyKick = Screen(Screen(_body, _kickA), _lift);
        for (int j = s.Start; j < end; j++)
        {
            float p = c.P[j];
            float half = _snareSide ? EffectContext.Smoothstep((p - 0.4f) / 0.2f) : EffectContext.Smoothstep((0.6f - p) / 0.2f);
            float aSnare = _snareA * half;
            float aBass = _bassA * (1 - EffectContext.Smoothstep((p - 0.2f) / 0.3f));
            float aFlow = _flowA * Blob(p - pos, w, s.Ring);
            if (!float.IsNaN(wave)) aFlow = Math.Max(aFlow, _waveA * Blob(p - wave, w, false));
            float i = Screen(Screen(Screen(bodyKick, aSnare), aBass), aFlow);
            var col = _mono ? _col[CBody] : MixAt(aSnare, aBass, aFlow);
            var x = col * c.Ilin(i);
            double age = t - _pT0[j];
            if (age >= 0 && age < HatTauSec * 8)
            {
                float k = _pAmp[j] * (float)Math.Exp(-age / HatTauSec);
                x += _col[CHat] * k;
            }
            dst[j] = x.FitToOne();
        }
    }

    /// <summary>光のかたまり（中心からの距離 d、半分の幅 w）。輪なら 1 周で折り返して測る</summary>
    private static float Blob(float d, float w, bool ring)
    {
        if (ring)
        {
            d -= MathF.Round(d);
        }
        float a = MathF.Abs(d) / w;
        return a >= 1 ? 0f : 0.5f + 0.5f * MathF.Cos(MathF.PI * a);
    }

    /// <summary>層の色を、層の強さで重みをつけて混ぜる（いちばん強い成分は、混ぜた層の平均にそろえる）</summary>
    private LinColor MixAt(float aSnare, float aBass, float aFlow)
    {
        float wb = _body + _lift, wk = _kickA;
        float sum = wb + wk + aSnare + aBass + aFlow;
        if (sum < 1e-4f) return _col[CBody];
        var m = _col[CBody] * _body + _col[CBrass] * _lift + _col[CKick] * wk + _col[CSnare] * aSnare + _col[CBass] * aBass + _col[CFlow] * aFlow;
        float target = (_colMax[CBody] * _body + _colMax[CBrass] * _lift + _colMax[CKick] * wk + _colMax[CSnare] * aSnare + _colMax[CBass] * aBass + _colMax[CFlow] * aFlow) / sum;
        float mc = m.MaxChannel;
        return mc > 1e-6f ? m * (target / mc) : _col[CBody];
    }

    /// <summary>小さいゾーンの色（全体の層だけ）</summary>
    private LinColor Mix(float body, float snare, float bass, float harm)
    {
        if (_mono) return _col[CBody];
        float sum = body + _lift + _kickA + snare + bass + harm;
        if (sum < 1e-4f) return _col[CBody];
        var m = _col[CBody] * body + _col[CBrass] * _lift + _col[CKick] * _kickA + _col[CSnare] * snare + _col[CBass] * bass + _col[CFlow] * harm;
        float target = (_colMax[CBody] * body + _colMax[CBrass] * _lift + _colMax[CKick] * _kickA + _colMax[CSnare] * snare + _colMax[CBass] * bass + _colMax[CFlow] * harm) / sum;
        float mc = m.MaxChannel;
        return mc > 1e-6f ? m * (target / mc) : _col[CBody];
    }
}

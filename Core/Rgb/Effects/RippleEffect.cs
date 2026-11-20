// ==================================================
// RippleEffect.cs - 光り方 E7「波紋」 (キックのたびに、ゾーンの中央から光の輪が広がる)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 波紋: 本物のキック（バスドラム）のたびに、光の輪を 1 つ出す（最大 6 つ）。拍の数だけでは輪を出さない。
/// テンポは、続けて鳴るキックに先回りする（遅れない）のと、輪の広がる速さ・小さいゾーンの消え方に使う。
/// （6.15.10 までは、テンポをつかむと予測した拍ごとに輪を出していた。キックのない拍・スネアの拍・間奏・話し声でも光り、
/// 裏拍のキックでは光らず、テンポの見当が倍や半分・半拍ずれると、輪がみなずれていた）
/// 輪を出す音（同じキックで 2 つ出さない。SameKickSec 秒以内）:
///   すぐ出す: キックの出来事（強さ KickMinStrength 以上）で、最近 DrumContextSec 秒に確かめたキックが 2 つあり（ドラムが鳴っている）、
///     低音の山が最近のキックより BodyMinDb 以上・立ち上がり RiseMinDb 以上のもの。
///     低音の山は、確かめたキック 8 つの山の中央値が 0 より小さければ、その分だけ直す（銃声などで最近のキックの大きさが上がったまま）。
///     手がかりのない出来事（BodyDb・RiseDb が NaN。テストで作ったキックなど）は、すぐ出す
///   確かめてから出す: それ以外のキックは、ConfirmWinSec 秒先まで分析が届くのを待ち、キックの時刻の ConfirmBackSec 秒前からの
///     超低音（SubE）の山が下限以上で、中音（MidE）の山より SubOverMid 以上大きく、立ち上がりが SlowRiseMinDb 以上（または低音の山が
///     SlowBodyMinDb 以上）なら出す（輪の生まれた時刻 = max(キック, いま − SlowBackSec)。約 0.07 秒遅れる）。
///     ベースの音・スネアの胴・ギター・ピアノ・話し声は、ここで落ちる。確かめたキックは、ドラムが鳴っている印にもする。
///     超低音の下限 = すぐ出したキックの超低音の山の中央値 × SubShare（強さの設定で値が変わるため。まだなければ DefaultSubMin）。
///     ドラムもテンポもないときの最初のキックは FirstSubMargin だけ高く、大きな音のあとは MaskSubRelief だけ低くする
///   大きな出来事（映画・ゲーム）: 静かなところからの急な大きな音（立ち上がり BigRiseDb 以上・音量が 0.15 秒前より BigJumpDb 以上）か、
///     ドラムの中で最近のキックより BigBodyDb 以上大きい音。強さ = 音の大きさ（LoudRel）。テンポをつかんでいなければ、話し声が続くほど弱める（TalkFatigue）。
///     確かめられなかったのに最近のキックより LoudNonKickDb 以上大きい音（銃声など）も、大きな出来事の時刻にする（輪は出さない）
///   先回り: テンポをつかんでいる（確かさ PatternConf 以上・拍の並びが新しい曲のもの）とき、1 小節を 8 分音符で 8 か所に分け、
///     PatternBars 小節続けて確かめたキックがあった場所で、直前の先回りが当たっている（外れたあと、確かめたキックがあった）なら、
///     その場所の時刻に輪を出す（遅いグラボでも遅れない）。強さはその場所のキックの強さ。音が止まっているときは出さない。
///     外れても、大きな出来事の MaskSec 秒あとまでは、つながりを切らない（銃声などにキックが隠れる）。
///     テンポをつかんでいない間は先回りを止め、PatternForgetSec 秒たつか、テンポが PeriodTol より変わったら、覚えた場所を消す
/// 輪の強さ A = clamp(1 + 低音の山の dB / 12, AmpMin, 1) × SectionGain(SectionMin)（弱いキックほど暗い。盛り上がりで明るい）
///   中央からの距離 d = |p − 0.5|（輪になったゾーンなら近いほうの回り方）
///   半径 r = 速さ × (t − t0)、速さ = テンポをつかんでいれば 0.5 / (0.75 拍)、つかんでいなければ 1.2 / 秒
///   太さ w = max(0.05, 1.5 / LED の数)
///   輪の明るさ R = A × exp(−((d − r)/w)²) × exp(−(t − t0)/0.35 秒)
///   LED の色 = 輪の色 × R（輪が重なったら明るいほう）+ 下地
///   下地 = いまの色 × Ilin(max(BaseShare × ゆっくり追った低音, TalkGlow × 話し声の分 × ならした中音))
///     話し声の分 = ドラムもテンポもない間 1（TalkFadeSec 秒で移る）。話し声・ナレーションでも少し灯るが、光りはしない
/// 輪は生まれた時刻から計算するので、1 秒間のコマ数が違っても同じ速さで広がる。新しい輪が見え始めるコマは、定期便を待たずに送る（c.SendNow）。
/// 6 個未満のゾーンは、輪が広がる場所がないので、全体を輪の明るさで光らせる（色 = いちばん新しい輪の色 + 下地）:
///   A × SmallGain × min(1, (経過 + 0.01) / 0.02) × exp(−経過 / τ)（上限 1）
///   τ = テンポをつかんでいれば min(0.35 秒, SmallPeriodShare × 拍の長さ)（遅いデバイスは SlowMinTauSec より短くしない。次の拍の前に暗くなる）、
///   つかんでいなければ 0.35 秒
/// </summary>
public sealed class RippleEffect : IMusicEffect
{
    /// <summary>同時に出せる輪の数</summary>
    public const int MaxRings = 6;
    /// <summary>輪を出すキックの強さの下限</summary>
    public const float KickMinStrength = 1.0f;
    /// <summary>輪が消えていく時定数（秒）</summary>
    public const double FadeTauSec = 0.35;
    /// <summary>テンポがないときの輪の広がる速さ（ゾーンの長さ / 秒）</summary>
    public const double FreeSpeed = 1.2;
    /// <summary>静かな部分での輪の明るさの倍率（盛り上がりで 1。EffectContext.SectionGain）</summary>
    public const float SectionMin = 0.6f;
    /// <summary>下地（いまの色）の明るさ（× 低音）</summary>
    public const float BaseShare = 0.15f;
    /// <summary>
    /// ドラムもテンポもないとき（話し声・ナレーションなど）の下地の明るさ（× ならした中音）。輪は出さないが、音が鳴っているのはわかるくらいに灯す
    /// </summary>
    public const float TalkGlow = 0.3f;
    /// <summary>話し声の下地が中音を追う時定数（秒。上がるとき・下がるとき。音節ごとに弾ませない）と、下地を出し入れする時定数（秒）</summary>
    public const double TalkUpSec = 0.15, TalkDownSec = 0.5, TalkFadeSec = 0.5;
    /// <summary>下地の低音を追う時定数（秒。上がるとき・下がるとき。下地は灯るだけで、キックでない低音（ピアノ・ベース）で光らせない）</summary>
    public const double BaseUpSec = 0.08, BaseDownSec = 0.25;
    /// <summary>輪の中心（ゾーンの中央）</summary>
    public const float Center = 0.5f;
    /// <summary>小さいゾーンで、輪の光が立ち上がる時間（秒）</summary>
    public const double SmallAttackSec = 0.02;
    /// <summary>これより小さい音（dBFS）は無音とみなす</summary>
    public const float SilenceDb = -70f;
    /// <summary>予測した拍の頭で無音だったとき、音が来るのを待つ時間（秒。ビートの光 BeatDriver・BeatFlash が使う）</summary>
    public const double ConfirmSec = 0.1;
    /// <summary>小さいゾーンで、テンポをつかんでいるときの輪が消える時定数の、拍の長さに対する割合（次の拍の前に暗くなる）</summary>
    public const double SmallPeriodShare = 0.4;
    /// <summary>遅いデバイスの小さいゾーンで、輪が消える時定数の下限（秒）</summary>
    public const double SlowMinTauSec = 0.25;

    // ---- 本物のキックの見分け方
    /// <summary>すぐ輪を出すのは、最近これだけの秒数のうちに確かめたキックが 2 つあるとき（ドラムが鳴っている。1 つだけだと、話し声の 1 音で始まる）</summary>
    public const double DrumContextSec = 3.0;
    /// <summary>すぐ輪を出すキックの、低音の山の下限（最近のキックから何 dB。ベースの音・スネアの胴はたいてい −5 dB より小さい）</summary>
    public const float BodyMinDb = -3f;
    /// <summary>すぐ輪を出すキックの、低音の立ち上がりの下限（dB）</summary>
    public const float RiseMinDb = 6f;
    /// <summary>大きな出来事のあと、確かめるときの超低音の山の下限を下げる時間（秒）</summary>
    public const double BigAdjustSec = 3.0;
    /// <summary>
    /// 確かめたキックの低音の山の中央値がこれより小さい（dB。銃声などで最近のキックの大きさが上がったまま）ときも、超低音の山の下限を下げる
    /// </summary>
    public const float RefLowDb = 2f;
    /// <summary>確かめるとき、キックの時刻からこれだけ先までの分析を見る（秒）</summary>
    public const double ConfirmWinSec = 0.06;
    /// <summary>確かめるとき、キックの時刻のこれだけ前からの分析を見る（秒）</summary>
    public const double ConfirmBackSec = 0.01;
    /// <summary>確かめるときの、超低音の山の下限（すぐ出したキックがまだ 3 つないとき）と、中音の山との差の下限</summary>
    public const float SubMin = 0.70f, SubOverMid = 0.05f;
    /// <summary>
    /// 超低音の山の下限は、すぐ出したキック（直近 8 つ）の超低音の山の中央値 × SubShare にする（SubFloor〜SubCeil）。
    /// 超低音の値は強さの設定で変わる（「控えめ」はゆっくり追うので低く、「激しい」は高い）ので、決まった値では
    /// 「控えめ」で本物のキックを落とし、「激しい」でベースの音を通していた
    /// </summary>
    public const float SubShare = 0.8f, SubFloor = 0.55f, SubCeil = 0.8f;
    /// <summary>最近 DrumContextSec 秒に確かめたキックがない（テンポもつかんでいないとき）の、超低音の山の下限への上乗せ</summary>
    public const float FirstSubMargin = 0.1f;
    /// <summary>大きな出来事のあと BigAdjustSec 秒・確かめたキックの山の中央値が −RefLowDb より小さいときの、超低音の山の下限の引き下げ</summary>
    public const float MaskSubRelief = 0.06f;

    /// <summary>すぐ出したキックがまだ 3 つないときの、超低音の山の下限（強さの設定ごと。「ふつう」は SubMin）</summary>
    public static float DefaultSubMin(string intensity) => intensity switch
    {
        Intensities.Subtle => 0.62f,
        Intensities.High => 0.74f,
        Intensities.Intense => 0.78f,
        _ => SubMin,
    };
    /// <summary>
    /// 確かめるときの、低音の立ち上がりの下限（dB）。ただし低音の山が最近のキックから SlowBodyMinDb 以上なら、立ち上がりは見ない
    /// （裏拍のベースの音は、立ち上がり 4.5〜6.5 dB で山は −2.6 dB より小さい。ベースの続く中のキックは、立ち上がりが小さくても山はキックの大きさ）
    /// </summary>
    public const float SlowRiseMinDb = 5.5f, SlowBodyMinDb = -2.5f;
    /// <summary>確かめてから出す輪の生まれた時刻を、いまからどれだけ前にするか（秒。輪が中央から出る）</summary>
    public const double SlowBackSec = 0.03;
    /// <summary>確かめるのを待つ時間の上限（秒）</summary>
    public const double PendMaxSec = 0.5;
    /// <summary>大きな出来事: 立ち上がり（dB）と、0.15 秒前からの音量の増え方（dB）の下限。またはドラムの中で最近のキックより何 dB 大きいか</summary>
    public const float BigRiseDb = 33f, BigJumpDb = 12f, BigBodyDb = 8f;
    /// <summary>
    /// 確かめられなかったのに、低音の山が最近のキックよりこれ以上大きい音（dB。銃声など）は、大きな出来事と同じに扱う
    /// （最近のキックの大きさが上がり、曲のキックが小さく見える・見つからないので）
    /// </summary>
    public const float LoudNonKickDb = 2f;
    /// <summary>この秒数以内のキックは同じキック（輪は 1 つ）</summary>
    public const double SameKickSec = 0.09;
    /// <summary>輪の強さ: 低音の山 1 dB あたりの割合と、下限</summary>
    public const float AmpPerDb = 1f / 12, AmpMin = 0.45f;

    // ---- 先回り
    /// <summary>先回りするテンポの確かさの下限</summary>
    public const float PatternConf = 0.5f;
    /// <summary>8 分音符の場所に、キックがあったとみなすずれ（秒）</summary>
    public const double PatternTolSec = 0.07;
    /// <summary>場所にキックがあったかは、確かめるのに要る時間のあと、さらにこれだけ待って決める（秒）</summary>
    public const double PatternWaitSec = 0.09;
    /// <summary>何小節続けてキックがあった場所で先回りするか</summary>
    public const int PatternBars = 2;
    /// <summary>大きな出来事のあと、先回りが外れてもつながりを切らない時間（秒）</summary>
    public const double MaskSec = 1.5;
    /// <summary>テンポをつかんでいない間、覚えた場所を残す時間（秒）</summary>
    public const double PatternForgetSec = 4.0;
    /// <summary>テンポがこれより変わったら、覚えた場所を消す（割合）</summary>
    public const double PeriodTol = 0.04;

    private const int Slots = 8;

    private struct Ring
    {
        public bool Active;
        public double T0;
        public float A;
        public LinColor Color;
    }

    // 確かめるのを待っているキック
    private struct PendKick
    {
        public double T;
        public float A, BodyDb, BodyAdj, RiseDb;   // BodyAdj = 確かめたキックの山の中央値で直した低音の山
        public bool Shown;   // もう輪を出した（すぐ出したキック・大きな出来事。確かめても、確かめたキックとしては数え直さない）
        public bool Fast;    // すぐ出したキック（超低音の山の目安に使う）
    }

    // 8 分音符の場所で、キックがあったかを決めるのを待っているもの
    private struct PendSlot
    {
        public double T;
        public int Slot;
        public bool Pred;    // 先回りする場所だった
    }

    private readonly Ring[] _rings = new Ring[MaxRings];
    private double _ringColorPos;
    private double _speed = FreeSpeed;
    private LinColor _base;
    private LinColor _smallCol;   // 小さいゾーンの色（輪の光）
    private double _smallTau = FadeTauSec;   // 小さいゾーン: 輪が消える時定数（秒）
    private float _smallGain = 1;
    private int _spawned;
    private readonly AudioFrame _kf = new();
    private readonly TalkFatigue _talk = new();
    private SteadyLevel _steadyMid;   // 下地: 話し声などの中音の小さな上下をならす
    private float _talkMid, _talkShare, _baseBass;  // 下地: ゆっくり追った中音と、話し声の分（0〜1）、ゆっくり追った低音
    private double _lastKickT = double.NegativeInfinity;   // 最後に輪を出したキックの時刻
    private double _sendT = double.NaN;                     // 定期便を待たずに送る、新しい輪の時刻
    private double _drumT = double.NegativeInfinity, _drumT2 = double.NegativeInfinity; // 最後と、その前に確かめたキックの時刻
    private double _bigT = double.NegativeInfinity;         // 最後の大きな出来事の時刻

    private readonly PendKick[] _pendK = new PendKick[16];
    private int _pendKN;
    // 確かめたキックの低音の山（dB。直近 8 つ）・すぐ出したキックの超低音の山（直近 8 つ）と、中央値を求める作業用
    private readonly float[] _bodyHist = new float[8], _subHist = new float[8], _sort = new float[8];
    private int _bodyHead, _bodyN, _subHead, _subN;
    // 確かめたキックの時刻と強さ（直近 32）
    private readonly double[] _confT = new double[32];
    private readonly float[] _confA = new float[32];
    private int _confHead, _confN;
    private float _lastA = 1;

    // 先回り: 場所ごとの、キックがあったかの記録（1 ビットが 1 小節。新しいほど下）と、その場所のキックの強さ
    private readonly int[] _hist = new int[Slots];
    private readonly float[] _slotA = new float[Slots];
    private readonly PendSlot[] _slotPend = new PendSlot[32];
    private int _slotPendN;
    private long _lastSlotN = long.MinValue;
    private double _patPeriod;
    private double _patSeenT = double.NegativeInfinity;
    private bool _chain;

    /// <summary>出した輪の数（テスト用）</summary>
    public int Spawned => _spawned;

    /// <summary>最後に出した輪の生まれた時刻（テスト用。まだなければ NaN）</summary>
    public double LastT0 { get; private set; } = double.NaN;

    /// <summary>輪の出し方ごとの数（調べる用）: すぐ出した・確かめてから出した・先回り・大きな出来事・落としたキック・外れた先回り</summary>
    internal int FastRings, SlowRings, PatternRings, BigRings, Dropped, PatternMiss;

    public void Reset(EffectContext c)
    {
        Array.Clear(_rings);
        _ringColorPos = c.Color.Target;
        _smallCol = LinColor.Black;
        _talk.Reset();
        _steadyMid.Reset();
        _talkMid = _talkShare = _baseBass = 0;
        _lastKickT = _drumT = _drumT2 = _bigT = _patSeenT = double.NegativeInfinity;
        _sendT = double.NaN;
        _pendKN = _bodyN = _bodyHead = _subN = _subHead = _confN = _confHead = 0;
        _lastA = 1;
        ResetPattern();
        _patPeriod = 0;
    }

    /// <summary>輪の生まれた時刻 t0 から、時刻 t の半径（テスト用にも使う）</summary>
    public double RadiusAt(double t0, double t) => _speed * Math.Max(0, t - t0);

    public void Update(EffectContext c)
    {
        _speed = c.Locked ? 0.5 / (0.75 * c.Period) : FreeSpeed;
        bool locked = c.HasTempo && c.Locked;
        _talk.Update(c, locked);
        ReadKicks(c, locked);
        Pattern(c);
        ConfirmPending(c);
        // 雷のように低音が膨らむ音は、キックの合図より音量の立ち上がりで拾う。
        if (!locked && c.Frame.GateOpen && c.Frame.RmsDb >= -20f && c.Frame.SubE > c.Frame.MidE && c.T - _lastKickT > 0.35
            && c.Features is { } frames && frames.Sample(c.T - 0.15, _kf)
            && c.Frame.RmsDb - _kf.RmsDb >= 18f)
        {
            Spawn(c, c.T, c.RelAt(c.Frame.LoudDb));
            _bigT = c.T;
            BigRings++;
        }
        // 新しい輪が見え始めるコマは、定期便を待たずに送る（遅いグラボで輪の出だしを逃さない）
        if (!double.IsNaN(_sendT) && c.T >= _sendT) { c.SendNow = true; _sendT = double.NaN; }

        // 広がりきって暗くなった輪を消す
        for (int k = 0; k < MaxRings; k++)
        {
            ref var r = ref _rings[k];
            if (!r.Active) continue;
            double age = c.T - r.T0;
            double amp = r.A * Math.Exp(-Math.Max(0, age) / FadeTauSec);
            if ((RadiusAt(r.T0, c.T) > 0.6 && amp < 0.02) || age > 4) r.Active = false;
        }
        // 遅いデバイスは、短い光が届かないことがあるので SlowMinTauSec より短くしない
        _smallTau = c.Locked ? Math.Min(FadeTauSec, Math.Max(c.SlowDevice ? SlowMinTauSec : 0, SmallPeriodShare * c.Period)) : FadeTauSec;
        _smallGain = SmallGain(c);

        // 下地: ドラムもテンポもないとき（話し声など）は、ならした中音で少し灯す（輪は出さない）
        float mid = _steadyMid.Step(c, Math.Clamp(c.Frame.MidE, 0f, 1f), SteadyLevel.BandDead);
        _talkMid = EffectContext.Follow(_talkMid, mid, c.Dt, TalkUpSec, TalkDownSec);
        bool talk = !locked && c.T - _drumT > DrumContextSec && SoundNow(c);
        _talkShare = EffectContext.Follow(_talkShare, talk ? 1f : 0f, c.Dt, TalkFadeSec, TalkFadeSec);
        _baseBass = EffectContext.Follow(_baseBass, Math.Clamp(c.Frame.BassE, 0f, 1f), c.Dt, BaseUpSec, BaseDownSec);
        _base = c.ColorAt(0) * c.Ilin(Math.Max(BaseShare * _baseBass, TalkGlow * _talkShare * _talkMid));
        if (c.HasSmallSegment) _smallCol = SmallColor(c);
    }

    /// <summary>このコマのキックの出来事を見て、すぐ輪を出すもの・大きな出来事は出し、ほかは確かめるのを待つ</summary>
    private void ReadKicks(EffectContext c, bool locked)
    {
        var ev = c.Events;
        for (int i = 0; i < ev.Count; i++)
        {
            var e = ev[i];
            if (e.Kind != AudioEventKind.Kick || e.Strength < KickMinStrength) continue;
            float body = e.BodyDb, rise = e.RiseDb;
            bool drums = e.T - _drumT2 <= DrumContextSec;
            // 銃声などで最近のキックの大きさが上がったままなら、確かめたキックの山の中央値（0 より小さいとき）で直す
            float bodyAdj = body;
            if (_bodyN >= 3 && !float.IsNaN(body)) bodyAdj = body - Math.Min(0f, Median(_bodyHist, _bodyN));
            bool fast = drums && (float.IsNaN(bodyAdj) || bodyAdj >= BodyMinDb) && rise >= RiseMinDb;
            // 手がかりのない出来事（テストで作ったキックなど）は、そのまま輪にする
            if (float.IsNaN(body) && float.IsNaN(rise)) fast = true;
            float a = AmpOf(bodyAdj);
            bool big = IsBig(c, e.T, body, rise, drums);
            if (big) _bigT = e.T;
            if (big && !fast)
            {
                if (!SameKick(e.T))
                {
                    // 大きな出来事（爆発・銃声・ジングルの頭など）: 音の大きさで。テンポをつかんでいなければ、話し声が続くほど弱める
                    float ba = c.LoudRel(e.T) * (locked ? 1f : _talk.Scale(e.T, false));
                    if (ba >= 0.05f) { Spawn(c, e.T, ba); BigRings++; }
                }
                AddPending(e.T, a, body, bodyAdj, rise, shown: true, fast: false);
                continue;
            }
            if (fast && !SameKick(e.T)) { Spawn(c, e.T, a); FastRings++; }
            AddPending(e.T, a, body, bodyAdj, rise, shown: fast, fast: fast);
            if (fast) Confirmed(e.T, a, body);
        }
    }

    /// <summary>大きな出来事か（ドラムの中で最近のキックよりずっと大きい音・静かなところからの急な大きな音）</summary>
    private bool IsBig(EffectContext c, double t, float body, float rise, bool drums)
    {
        if (drums && body >= BigBodyDb) return true;
        if (!(rise >= BigRiseDb) || c.Features is not { } fr) return false;
        float now = -120, pre = -120;
        for (double u = t; u <= t + 0.03 + 1e-9; u += 0.005)
            if (fr.Sample(u, _kf)) now = Math.Max(now, _kf.LoudDb);
        if (fr.Sample(t - 0.15, _kf)) pre = _kf.LoudDb;
        return now - pre >= BigJumpDb;
    }

    /// <summary>分析が届いたキックを確かめる（超低音が強く、中音より大きい）。確かめたキックは輪にする（まだ出していなければ）</summary>
    private void ConfirmPending(EffectContext c)
    {
        double latest = c.Features?.LatestT ?? double.NegativeInfinity;
        // ドラムがしばらくない（間奏・次の曲）ときは、超低音の山の目安を忘れる（前の曲の目安でピアノなどを通さない）
        if (c.T - _drumT > PatternForgetSec) _subN = 0;
        float subMin = _subN >= 3 ? Math.Clamp(SubShare * Median(_subHist, _subN), SubFloor, SubCeil) : DefaultSubMin(c.Music.Intensity);
        int w = 0;
        for (int q = 0; q < _pendKN; q++)
        {
            var p = _pendK[q];
            if (latest < p.T + ConfirmWinSec && c.T - p.T < PendMaxSec) { _pendK[w++] = p; continue; }
            float sub = 0, mid = 0;
            if (c.Features is { } fr)
                for (double u = p.T - ConfirmBackSec; u <= p.T + ConfirmWinSec + 1e-9; u += 0.005)
                    if (fr.Sample(u, _kf)) { sub = Math.Max(sub, _kf.SubE); mid = Math.Max(mid, _kf.MidE); }
            if (p.Fast && c.Features is not null)
            {
                _subHist[_subHead] = sub;
                _subHead = (_subHead + 1) % _subHist.Length;
                if (_subN < _subHist.Length) _subN++;
            }
            // しばらくキックがなく、テンポもつかんでいないときの最初のキックは、超低音の山が FirstSubMargin だけ大きいときだけ（話し声の子音などで始めない）
            float need = p.T - _drumT <= DrumContextSec || c.Locked ? subMin : subMin + FirstSubMargin;
            // 大きな音（銃声・爆発など）のあとは、曲のキックの超低音が隠れて小さく見えるので、下限を MaskSubRelief だけ下げる
            if (p.T - _bigT <= BigAdjustSec || (_bodyN >= 3 && Median(_bodyHist, _bodyN) <= -RefLowDb)) need -= MaskSubRelief;
            // 強く立ち上がったキックは、中音が重なっていても超低音の山で拾う。
            double halfPhase = c.Beat.Phase(p.T) * 2;
            bool strongBody = c.HasTempo && c.Locked && !c.GridStale && c.Conf >= PatternConf
                && Math.Abs(halfPhase - Math.Round(halfPhase)) * c.Period * 0.5 <= PatternTolSec
                && p.RiseDb >= RiseMinDb && p.BodyAdj >= 0 && sub >= SubMin && sub >= mid - 0.025f;
            bool ok = sub >= need && (sub - mid >= SubOverMid || strongBody) && !(p.RiseDb < SlowRiseMinDb && p.BodyAdj < SlowBodyMinDb);
            if (ok) { if (p.T > _drumT) { _drumT2 = _drumT; _drumT = p.T; } }
            // 最近のキックより大きいのにキックでなかった音（銃声など）: 最近のキックの大きさが上がったので、大きな出来事と同じに扱う
            else if (p.BodyDb >= LoudNonKickDb) _bigT = Math.Max(_bigT, p.T);
            if (p.Shown) continue;
            if (ok)
            {
                Confirmed(p.T, p.A, p.BodyDb);
                if (!SameKick(p.T))
                {
                    Spawn(c, Math.Max(p.T, c.T - SlowBackSec), p.A);
                    _lastKickT = p.T;
                    SlowRings++;
                }
            }
            else Dropped++;
        }
        _pendKN = w;
    }

    /// <summary>
    /// 先回り: 8 分音符の場所ごとに、確かめたキックがあったかを覚え、続けてキックが来ている場所は、その時刻に輪を出す
    /// </summary>
    private void Pattern(EffectContext c)
    {
        var b = c.Beat;
        bool use = c.HasTempo && c.Locked && c.Conf >= PatternConf && !c.GridStale && b.Period > 0;
        if (!use)
        {
            // 一時的にテンポを見失っても、テンポが同じなら覚えた場所は残す（先回りだけ止める）
            _lastSlotN = long.MinValue;
            _slotPendN = 0;
            if (c.T - _patSeenT > PatternForgetSec) { ResetPattern(); _patPeriod = 0; }
            return;
        }
        _patSeenT = c.T;
        if (Math.Abs(b.Period - _patPeriod) > PeriodTol * b.Period) { ResetPattern(); _patPeriod = b.Period; }
        double h = b.Period / 2;
        long n1 = (long)Math.Floor((c.T - b.LastBeatT) / h + 0.5) + 2 * b.IndexAtLast;
        if (_lastSlotN == long.MinValue) _lastSlotN = n1;
        int mask = (1 << PatternBars) - 1;
        // 音が止まったあとの拍では出さない（無音の中で先回りの輪を出さない）
        bool sound = SoundNow(c) && c.Frame.RmsDb > SilenceDb;
        for (long n = Math.Max(_lastSlotN + 1, n1 - 1); n <= n1; n++)
        {
            double ts = b.LastBeatT + (n - 2 * b.IndexAtLast) * h;
            int slot = (int)(((n - 2L * b.DownbeatOffset) % Slots + Slots) % Slots);
            bool pred = _chain && (_hist[slot] & mask) == mask && sound;
            if (pred && !SameKick(ts) && ts > c.TPrev - 0.05)
            {
                Spawn(c, ts, _slotA[slot] > 0 ? _slotA[slot] : _lastA);
                PatternRings++;
            }
            if (_slotPendN == _slotPend.Length) ResolveOldestSlot();
            _slotPend[_slotPendN++] = new PendSlot { T = ts, Slot = slot, Pred = pred };
        }
        _lastSlotN = Math.Max(_lastSlotN, n1);
        // 場所にキックがあったかを決める（確かめるのに要る時間のあと）
        double latest = c.Features?.LatestT ?? double.NegativeInfinity;
        while (_slotPendN > 0 && (latest >= _slotPend[0].T + PatternWaitSec + ConfirmWinSec || c.T - _slotPend[0].T >= 1.0))
            ResolveOldestSlot();
    }

    /// <summary>いちばん古い待っている場所に、キックがあったかを決めて記録する</summary>
    private void ResolveOldestSlot()
    {
        var p = _slotPend[0];
        Array.Copy(_slotPend, 1, _slotPend, 0, _slotPendN - 1);
        _slotPendN--;
        float hitA = 0;
        bool hit = false;
        for (int k = 0; k < _confN; k++)
        {
            int idx = (_confHead - 1 - k + _confT.Length) % _confT.Length;
            if (Math.Abs(_confT[idx] - p.T) <= PatternTolSec) { hit = true; hitA = _confA[idx]; break; }
        }
        // 先回りが外れても、大きな音（銃声など）に隠れているかもしれない: つながりは切らず、覚えた場所も変えない
        if (!hit && p.Pred && p.T - _bigT >= 0 && p.T - _bigT <= MaskSec) return;
        _hist[p.Slot] = ((_hist[p.Slot] << 1) | (hit ? 1 : 0)) & 0xFF;
        if (hit) _slotA[p.Slot] = hitA;
        if (p.Pred) { _chain = hit; if (!hit) PatternMiss++; }
    }

    private void ResetPattern()
    {
        Array.Clear(_hist);
        Array.Clear(_slotA);
        _lastSlotN = long.MinValue;
        _chain = false;
        _slotPendN = 0;
    }

    private void AddPending(double t, float a, float body, float bodyAdj, float rise, bool shown, bool fast)
    {
        if (_pendKN == _pendK.Length)
        {
            // いっぱいなら、いちばん古いものを捨てる（ふつうは起きない）
            Array.Copy(_pendK, 1, _pendK, 0, _pendKN - 1);
            _pendKN--;
        }
        _pendK[_pendKN++] = new PendKick { T = t, A = a, BodyDb = body, BodyAdj = bodyAdj, RiseDb = rise, Shown = shown, Fast = fast };
    }

    /// <summary>確かめたキックを覚える（先回りのつながり・場所の強さ・低音の山の中央値に使う）</summary>
    private void Confirmed(double t, float a, float body)
    {
        if (!float.IsNaN(body))
        {
            _bodyHist[_bodyHead] = body;
            _bodyHead = (_bodyHead + 1) % _bodyHist.Length;
            if (_bodyN < _bodyHist.Length) _bodyN++;
        }
        _confT[_confHead] = t;
        _confA[_confHead] = a;
        _confHead = (_confHead + 1) % _confT.Length;
        if (_confN < _confT.Length) _confN++;
        _chain = true;
        _lastA = a;
    }

    /// <summary>記録 h の先頭 n 個の中央値（作業用の配列に並べ替える）</summary>
    private float Median(float[] h, int n)
    {
        for (int i = 0; i < n; i++)
        {
            float v = h[i];
            int j = i;
            while (j > 0 && _sort[j - 1] > v) { _sort[j] = _sort[j - 1]; j--; }
            _sort[j] = v;
        }
        return _sort[n / 2];
    }

    private bool SameKick(double t) => Math.Abs(t - _lastKickT) < SameKickSec;

    private static float AmpOf(float bodyDb) => float.IsNaN(bodyDb) ? 1f : Math.Clamp(1 + AmpPerDb * bodyDb, AmpMin, 1f);

    /// <summary>音が鳴っている（音の止まったときの光り方に移るときと同じ見方。IdleMixer）</summary>
    private static bool SoundNow(EffectContext c) => c.Frame.GateOpen && c.Frame.Activity >= IdleMixer.QuietActivity;

    /// <summary>
    /// 小さいゾーンの、輪の明るさの倍率の下限（「控えめ」）。「控えめ」は「激しい」より少し暗く光る
    /// </summary>
    public const float SmallGainMin = 1.0f;

    /// <summary>小さいゾーンの、輪の明るさの倍率（強さの設定で 1.0〜1.5。「ふつう」以上は強いキックで明るさいっぱいまで光る）</summary>
    public static float SmallGain(EffectContext c) =>
        Math.Min(1.5f, Math.Max(SmallGainMin, 0.6f + 1.4f * (float)Math.Clamp(c.Music.FlashAmp, 0, 1)));

    /// <summary>小さいゾーンの色: 輪の明るさのいちばん大きい値 × いちばん新しい輪の色</summary>
    private LinColor SmallColor(EffectContext c)
    {
        double t = c.T;
        float best = 0;
        var col = LinColor.Black;
        double newest = double.NegativeInfinity;
        for (int k = 0; k < MaxRings; k++)
        {
            ref readonly var r = ref _rings[k];
            if (!r.Active) continue;
            double age = t - r.T0;
            if (age < 0) continue;
            double rise = Math.Min(1, (age + SmallAttackSec * 0.5) / SmallAttackSec);
            float a = (float)(r.A * _smallGain * rise * Math.Exp(-age / _smallTau));
            if (a > best) best = a;
            if (r.T0 > newest) { newest = r.T0; col = r.Color; }
        }
        return col * Math.Min(1f, best);
    }

    private void Spawn(EffectContext c, double t0, float amp)
    {
        // 空いている場所、なければいちばん古い輪を置き換える
        int slot = 0;
        double oldest = double.PositiveInfinity;
        for (int k = 0; k < MaxRings; k++)
        {
            if (!_rings[k].Active) { slot = k; break; }
            if (_rings[k].T0 < oldest) { oldest = _rings[k].T0; slot = k; }
        }
        _ringColorPos += c.Pal.Step;   // 輪ごとにパレットの次の色
        _rings[slot] = new Ring { Active = true, T0 = t0, A = amp * c.SectionGain(SectionMin), Color = c.PalAt(_ringColorPos) };
        _spawned++;
        _lastKickT = t0;
        _sendT = t0;
        LastT0 = t0;
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        if (s.Small)
        {
            var sc = (_smallCol + _base).FitToOne();
            for (int j = s.Start; j < s.Start + s.Count; j++) dst[j] = sc;
            return;
        }
        // ケースの中の場所を決めたゾーンは、流れの始まり（位置 0）から輪が広がる（ケース全体で 1 つの輪）
        float w = Math.Max(0.05f, 1.5f / (s.Placed ? s.Ne : s.Count));
        double t = c.T;
        for (int j = s.Start; j < s.Start + s.Count; j++)
        {
            float d = s.Placed ? c.P[j] * 0.5f : Math.Abs(c.P[j] - Center);
            if (s.Ring) d = Math.Min(d, 1 - d);
            var col = LinColor.Black;
            for (int k = 0; k < MaxRings; k++)
            {
                ref readonly var r = ref _rings[k];
                if (!r.Active) continue;
                double age = t - r.T0;
                if (age < 0) continue;
                double x = (d - _speed * age) / w;
                float amp = (float)(r.A * Math.Exp(-x * x) * Math.Exp(-age / FadeTauSec));
                if (amp > 0.002f) col = LinColor.Max(col, r.Color * amp);
            }
            dst[j] = (col + _base).FitToOne();
        }
    }
}

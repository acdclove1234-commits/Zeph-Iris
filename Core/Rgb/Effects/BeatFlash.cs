// ==================================================
// BeatFlash.cs - 「ビートで色替え」の拍の光 (テンポをつかんでいれば予測した拍の頭だけで光り、
//                つかめていなければキックで光る。拍の間のキック・ベースの音では光らない)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 「ビートで色替え」で、光り始めの時刻を決める。
/// 以前は低音の大きさ（BassE）でも光らせていたので、8 分・16 分のベースや拍の間のキックでも光り、
/// 光の半分以上が拍の外で、どの音に合わせているのか分からなかった（録音 17 曲で、拍に乗った光は 4 割）。そこで光るきっかけを決める:
///   テンポをつかんでいて（BeatState.Locked。少し確かさが下がった Coasting を含む）:
///       予測した拍の頭で光る。拍の間のキック・スネア・ベースでは光らない。
///       強さ a は、その拍（± BeatWindowSec）で実際に鳴った音で決める（HitDriven）: キックは 1（キックの包絡線が小さい、
///       スネアの胴の音のようなものは弱く。KickWeight）、スネアだけなら LockedSnareAmp、どちらもなければ LockedNoHitAmp
///       （直近 QuietSec 秒にキック・スネアが 1 つもない静かなところでは LockedQuietAmp）。小節の頭だからとは強くしない。
///       6.15.10 までは、何もない拍も 0.55・キックの拍はほぼいつも 1 で、ドラムのない間奏でも同じ速さで光り続け、
///       「決まった間隔で点滅しているだけ」に見えた。
///       キックは拍の頭より少し遅れて見つかるので、見つかる前は、小節の中のその拍の位置の最近の強さ（_slot）で光り始め、
///       届いたら実際の強さに直す。BeatWindowSec 秒たっても届かなければ、音のない拍の強さに下げる。
///       直前の拍にも音がなかったら覚えた強さは SlotAfterMiss 倍、2 拍続けてなければ使わない（間奏に入ったところで何拍も光らない）。
///       光が消えていく時定数は LockedTauBeats 拍。さらに確かさで弱める: × (ConfFloor + (1 − ConfFloor) × 確かさ)
///   つかめていない:
///       キック（強さ 1.2 以上。Auto なら全体の立ち上がりも）で光る。前の光から GapSec あける
///       （テンポの見当があれば 0.6 拍・0.22〜0.45 秒。半拍のキックで 2 回光らない）。
///       色を進めるキック（BeatDriver の合図）は、間隔によらず必ず光る（色だけが変わって見えないように）。
///       拍の見当（予測した拍の頭）があり、ドラムが鳴っていれば、そこでも UnlockedGridGain 倍の弱い光
///       （キックが重なれば、キックの光の明るさにする）。ドラムのないところ（パッドだけの間奏など）では拍の見当では光らない
///       （鳴っている音がないのに、決まった速さで点滅し続けて見えた）。最近のキック・スネアが拍の見当に乗っていないとき
///       （曲が止まったあとの足音・銃声など。GridFitMin）も光らない（前の曲の拍の速さで、弱く光り続けていた）
///   光の値 = Level × a × exp(−光り始めからの秒数 / τ)、Level = LevelBase + LevelAmp × FlashAmp（強さの設定）、τ = TauBeats 拍
/// つかめていないときのキックの光は、直近の大きな音とくらべた音の大きさで弱める（EffectContext.RelAt。銃声の大小が同じ明るさに見えた）。
/// 前の光より FlashLimiter.RaiseDb 以上大きい音は、GapSec の間でも光る（小さな銃声のすぐあとの爆発が光らなかった）。
/// 話し声が続くときは、つかめていないときのキックの光を弱くしていく（TalkFatigue。前の光よりはっきり大きい音は弱めない）。
/// 「激しい点滅を抑える」がオンなら、光り始めを 1 秒に 3 回まで（FlashLimiter。超えた光は 0.35 倍。前の光よりはっきり大きい音は 1 回だけ通す）。
/// テンポをつかめておらず、キック・スネアも QuietSec 秒ない（ドラムのない曲）ときは Drumless を立てる（光り方が今までどおり低音・メロディで光る）。
/// 出来事と拍の頭は、時刻の順に処理する（BeatDriver と同じ。先に拍の頭で光らせてから前のキックを見ると、そのキックの光が始められなかった）。
/// 時刻はすべて音が鳴った本当の時刻で計算するので、1 秒間のコマ数が違っても同じ光り方になる。時刻が戻ったら（音の遅れの設定を変えたなど）、
/// 覚えている時刻を同じだけずらす。毎コマの処理では new しない
/// </summary>
public sealed class BeatFlash
{
    /// <summary>光り方の値（実際の曲で合わせるため、テストから変えられるようにしてある。アプリの中では変えない）</summary>
    internal static class Tune
    {
        /// <summary>拍の光の強さを決めるときに、その拍の音とみなすキック・スネアのずれ（秒）</summary>
        internal static double BeatWindowSec = 0.08;
        /// <summary>キックのある拍の強さ（強さ 1.2 で KickMinAmp、1 増えるごとに KickSlope 上がる。上限 1）</summary>
        internal static float KickMinAmp = 0.75f, KickSlope = 0.15f;
        /// <summary>スネアだけの拍・音のない拍・静かなところ（直近 QuietSec 秒にキック・スネアがない）の拍の強さ</summary>
        internal static float SnareAmp = 0.7f, NoHitAmp = 0.55f, QuietAmp = 0.3f;
        /// <summary>直近この秒数にキック・スネアがなければ「静かなところ」</summary>
        internal static double QuietSec = 1.5;
        /// <summary>小節の頭の拍に足す強さ</summary>
        internal static float AccentAdd = 0.15f;
        /// <summary>確かさが 0 のときの倍率（1 で確かさを見ない）</summary>
        internal static float ConfFloor = 0.75f;
        /// <summary>光の明るさ = LevelBase + LevelAmp × FlashAmp</summary>
        internal static float LevelBase = 0.55f, LevelAmp = 0.45f;
        /// <summary>光が消えていく時定数 = TauBeats × 1 拍（TauMinSec〜TauMaxSec 秒。テンポが分からなければ TauSec）</summary>
        internal static double TauBeats = 0.3, TauMinSec = 0.10, TauMaxSec = 0.25, TauSec = 0.2;
        /// <summary>つかめていないときの、キックの光の間隔（テンポの見当があれば GapBeats 拍・GapMinSec〜GapMaxSec 秒、なければ GapSec 秒）</summary>
        internal static double GapBeats = 0.6, GapMinSec = 0.22, GapMaxSec = 0.45, GapSec = 0.3;
        /// <summary>つかめていないときの、拍の見当（予測した拍の頭）の光の倍率（0 で、つかめていないときはキックだけ）</summary>
        internal static float UnlockedGridGain = 0.75f;
        /// <summary>つかめていないとき、キックの光のあと、拍の見当の光を出さない間隔（キックの光の間隔 GapSec の何倍か。0 = あけない）</summary>
        internal static double GridAfterKickGap = 0.5;
        /// <summary>拍の並びが前の曲のもの（GridStale）の間は、拍の見当で光らない（false = 光る。調べる用）</summary>
        internal static bool StaleNoGrid = true;
        /// <summary>
        /// つかめていないとき、キック・スネアが拍の見当（± BeatWindowSec）に乗った割合（出来事ごとに GridFitAlpha で追う指数平均）が
        /// GridFitMin 未満なら、拍の見当では光らない
        /// </summary>
        internal static float GridFitMin = 0.5f, GridFitAlpha = 0.35f;
        /// <summary>
        /// テンポをつかんでいるとき、拍の光の強さを、その拍で実際に鳴った音で決める（false = 6.15.10 までの、どの拍もほぼ同じ強さ。調べる用）。
        /// 6.15.10 までは、キックのない拍も 0.55、キックの拍はほぼいつも 1 で、ドラムのない間奏でも同じ速さで光り続け、
        /// 「決まった間隔で点滅しているだけ」に見えた
        /// </summary>
        internal static bool HitDriven = true;
        /// <summary>テンポをつかんでいるときの、スネアだけの拍・音のない拍・静かなところ（直近 QuietSec 秒にキック・スネアがない）の拍の強さ</summary>
        internal static float LockedSnareAmp = 0.6f, LockedNoHitAmp = 0.12f, LockedQuietAmp = 0.1f;
        /// <summary>
        /// キックの拍の強さ = (キックの包絡線 − KickEnvLo) / KickEnvSpan（KickWeightMin〜1）。キックの直後 KickEnvAtSec 秒の値を見る。
        /// キックとして見つかったスネアの胴の音（包絡線が小さい）を弱くする
        /// </summary>
        internal static float KickEnvLo = 0.6f, KickEnvSpan = 0.35f, KickWeightMin = 0.3f;
        internal static double KickEnvAtSec = 0.03;
        /// <summary>
        /// 小節の中の拍の位置ごとに覚えておく、最近の強さ（はじめ SlotInit。実際の音へ SlotAlpha ずつ近づける）。
        /// キックは拍の頭より少し遅れて見つかるので、見つかる前は、この強さで光り始める（届いたら直す）
        /// </summary>
        internal static float SlotInit = 0.6f, SlotAlpha = 0.5f;
        /// <summary>直前の拍に音が届かなかったときの、覚えていた強さの倍率（2 拍続けて届かなければ 0）</summary>
        internal static float SlotAfterMiss = 0.5f;
        /// <summary>
        /// テンポをつかんでいるときの、光が消えていく時定数（拍。TauBeats の代わり）。キックのない拍を弱くした分、拍の間が暗くなりすぎないように少し長くする
        /// （録音 8 曲で、暗いコマの割合が 6.15.10 の 31% から 33% に増えたのが 29% になった）
        /// </summary>
        internal static double LockedTauBeats = 0.33;
    }

    /// <summary>光らせるキックの強さの下限（BeatDriver.FlashMinStrength と同じ）</summary>
    public const float MinStrength = BeatDriver.FlashMinStrength;
    /// <summary>この間隔（秒）以内のキックと立ち上がりは、同じ 1 つの音とみなす</summary>
    public const double SameHitSec = BeatDriver.SameHitSec;

    // 光 2 つ（新しい光 1 と 1 つ前の光 0）: 始まりの時刻・強さ（音で決まる分）・倍率（明るさ × 確かさなど）・時定数
    private double _t1 = double.NegativeInfinity, _t0 = double.NegativeInfinity;
    private float _r1, _r0, _g1, _g0;
    // 新しい光の、本物のキックとしての倍率（明るさ × 点滅の制限。つかめていないときの拍の見当の光に、キックが重なったときに使う）
    private float _k1;
    private bool _beat1;
    private double _tau1 = Tune.TauSec, _tau0 = Tune.TauSec;
    // 直近のキック・スネア（拍の頭より先に届いたものを、次の拍の強さに使う）
    private double _hitT = double.NegativeInfinity, _drumT = double.NegativeInfinity;
    private float _hitA;
    // テンポをつかんでいるときの、直近のキック・スネアの強さ（実際に鳴った音で決める。HitDriven）
    private float _hitH;
    // 小節の中の拍の位置ごとの、最近の強さ（HitDriven）と、いま光っている拍の位置（-1 = なし）・時刻・音が届いたか
    private readonly float[] _slot = { Tune.SlotInit, Tune.SlotInit, Tune.SlotInit, Tune.SlotInit };
    private int _slotI = -1;
    private double _slotT;
    private bool _slotSeen;
    // 続けて音が届かなかった拍の数
    private int _missRun;
    private double _gridKickT = double.NegativeInfinity, _gridSnareT = double.NegativeInfinity;
    private readonly AudioFrame _kickFrame = new();
    // つかめていないときの、キック・スネアが拍の見当に乗った割合（指数平均。つかんでいる間は 1）
    private float _gridFit = 1;
    private readonly FlashLimiter _limiter = new();
    // 話し声が続くとき、テンポをつかめていないキックの光を弱くする
    private readonly TalkFatigue _talk = new();
    // 新しい光の音量（dB。つかんでいるときの拍の光など、分からなければ NaN）
    private float _flashDb = float.NaN;
    // 前のコマの時刻（時刻が戻ったかを見る）
    private double _lastT = double.NegativeInfinity;
    // このコマで、テンポをつかんでいる（拍の頭で光らせる）
    private bool _locked;
    // 無音のまま来た、予測した拍の頭の時刻（NaN = なし）。すぐ後に音が来たら、その拍の時刻で光る
    private double _silentT = double.NaN;

    /// <summary>今のコマの時刻の光（0〜1）</summary>
    public float Value { get; private set; }
    /// <summary>このコマで、拍の頭の光が始まった（テスト用）</summary>
    public bool BeatStarted { get; private set; }
    /// <summary>このコマで、キックの光が始まった（テスト用）</summary>
    public bool KickStarted { get; private set; }
    /// <summary>
    /// テンポをつかめておらず、直近 QuietSec 秒にキック・スネアもない（ドラムのない曲・ところ）。
    /// 光り方は、このときだけ今までどおり低音・メロディで光らせる（拍もキックもないので、拍の光だけでは止まって見える）
    /// </summary>
    public bool Drumless { get; private set; }
    /// <summary>光り始めの点滅の制限（テスト用）</summary>
    internal FlashLimiter Limiter => _limiter;

    public void Reset()
    {
        _t1 = _t0 = _hitT = _drumT = _lastT = double.NegativeInfinity;
        _r1 = _r0 = _g1 = _g0 = _k1 = _hitA = _hitH = 0;
        Array.Fill(_slot, Tune.SlotInit);
        _slotI = -1;
        _slotSeen = false;
        _missRun = 0;
        _gridKickT = _gridSnareT = double.NegativeInfinity;
        _beat1 = false;
        _tau1 = _tau0 = Tune.TauSec;
        _locked = false;
        _gridFit = 1;
        _limiter.Reset();
        _talk.Reset();
        _flashDb = float.NaN;
        _silentT = double.NaN;
        Value = 0;
        BeatStarted = KickStarted = Drumless = false;
    }

    /// <summary>1 コマ分を進める（BeatDriver.Update のあとに呼ぶ。c.BeatCrossed・c.Locked・c.Trigger などを使う）</summary>
    public void Update(EffectContext c)
    {
        var m = c.Music;
        _limiter.Enabled = m.ReduceFlashing;
        // 時刻が戻った（音の遅れ・デバイスの遅れの設定を変えた）: 覚えている時刻を同じだけ戻す（そのままだと光が未来の時刻に残り、追いつくまで光らない）
        if (c.T < _lastT - 1e-6) Shift(c.T - _lastT);
        _lastT = c.T;
        bool auto = m.TempoMode == TempoModes.Auto;
        _locked = c.HasTempo && c.Locked;
        if (_locked) _gridFit = 1;
        _talk.Update(c, _locked);
        BeatStarted = KickStarted = false;
        float level = Tune.LevelBase + Tune.LevelAmp * (float)Math.Clamp(m.FlashAmp, 0, 1);
        double tau = c.HasTempo ? Math.Clamp((_locked && Tune.HitDriven ? Tune.LockedTauBeats : Tune.TauBeats) * c.Period, Tune.TauMinSec, Tune.TauMaxSec) : Tune.TauSec;

        // 出来事（キック・スネア）と予測した拍の頭を、時刻の順に処理する
        bool gridPending = c.HasTempo && c.BeatCrossed;
        var events = c.Events;
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            if (gridPending && ev.T > c.BeatCrossT)
            {
                OnGrid(c, c.BeatCrossT, level, tau);
                gridPending = false;
            }
            OnEvent(c, ev, auto, level, tau);
        }
        if (gridPending) OnGrid(c, c.BeatCrossT, level, tau);
        // 無音で待っていた拍の頭: RippleEffect.ConfirmSec 秒以内に音が来たら、その拍の時刻で光る（少し遅れたキックでも拍で光る）
        if (!double.IsNaN(_silentT))
        {
            if (c.T - _silentT >= RippleEffect.ConfirmSec) _silentT = double.NaN;
            else if (c.Frame.RmsDb > RippleEffect.SilenceDb)
            {
                double ts = _silentT;
                _silentT = double.NaN;
                OnGrid(c, ts, level, tau, heard: true);
            }
        }
        // 拍の頭から BeatWindowSec 秒たってもキック・スネアが来なかった: その拍は音のない拍の強さにする
        if (_slotI >= 0 && c.T - _slotT > Tune.BeatWindowSec) CloseSlot();
        Drumless = !_locked && c.T - _drumT > Tune.QuietSec;

        // 今の時刻の値
        Value = (float)At(c.T);
    }

    /// <summary>予測した拍の頭（時刻 tb）をまたいだ（heard = 無音で待ったあと、音が来た）</summary>
    private void OnGrid(EffectContext c, double tb, float level, double tau, bool heard = false)
    {
        // 拍の頭が無音（デジタルの無音）なら、すぐには光らない。カウントダウンのピッという音の間や、
        // 最後の音のあとの、音のない拍で光って見えた
        if (!heard && c.Frame.RmsDb <= RippleEffect.SilenceDb) { _silentT = tb; return; }
        // 静かなところ（ドラムがない）では、小節の頭も強くしない（拍が弱くゆっくり見えるだけにする）
        bool quiet = tb - _drumT > Tune.QuietSec;
        // つかんでいれば拍の光。つかめていなくても、拍の見当があり、ドラムが鳴っていれば弱い光
        // （ドラムのないところでは光らない。鳴っている音がないのに、決まった速さで点滅し続けて見えた）
        // 曲が変わったと知らされたあと、拍の並びが前の曲のまま（GridStale）なら、拍の見当では光らない（キックで光る）
        bool unlockedGrid = !_locked && Tune.UnlockedGridGain > 0 && !quiet && !(c.GridStale && Tune.StaleNoGrid) && _gridFit >= Tune.GridFitMin;
        if (!(_locked || unlockedGrid)) return;
        float a;
        if (_locked && Tune.HitDriven)
        {
            // テンポをつかんでいる: その拍で実際に鳴った音の強さ（キック > スネア > 音のない拍）。小節の頭だからとは強くしない
            // （小節の頭の見当は外れやすく、ハウスでは 2 拍目がいちばん明るかった）
            CloseSlot();
            bool seen = Math.Abs(tb - _hitT) <= Tune.BeatWindowSec;
            a = quiet ? Tune.LockedQuietAmp : Tune.LockedNoHitAmp;
            if (seen) a = Math.Max(a, _hitH);
            if (!quiet)
            {
                // キックがまだ届いていなければ、この拍の位置の最近の強さで光り始める（届いたら直す。来なければ BeatWindowSec 秒後に弱める）
                _slotI = c.Beat.Bar(tb + 0.25 * c.Period) & 3;
                _slotT = tb;
                _slotSeen = seen;
                if (seen) _slot[_slotI] += (_hitH - _slot[_slotI]) * Tune.SlotAlpha;
                // 直前の拍にも音がなかったら弱く、2 拍続けてなければ使わない（間奏に入ったところで、覚えていた強さで何拍も光らない）
                else a = Math.Max(a, _slot[_slotI] * (_missRun == 0 ? 1f : _missRun == 1 ? Tune.SlotAfterMiss : 0f));
            }
        }
        else
        {
            a = quiet ? Tune.QuietAmp : Tune.NoHitAmp;
            if (Math.Abs(tb - _hitT) <= Tune.BeatWindowSec) a = Math.Max(a, _hitA);
            if (!quiet && c.Beat.Bar(tb + 0.25 * c.Period) == 0) a += Tune.AccentAdd;
        }
        float scale = (Tune.ConfFloor + (1 - Tune.ConfFloor) * c.Conf) * (_locked ? 1 : Tune.UnlockedGridGain);
        if (!_locked && tb >= _t1)
        {
            // つかめていないとき、キックの光の直後の拍の頭: 同じ音として、強さだけ上げる（2 回光らせない）
            if (!_beat1 && tb - _t1 <= Tune.BeatWindowSec) { _r1 = Math.Max(_r1, a); return; }
            // キックの光から間がない拍の頭では光らない（キックの光の直後にもう一度光って見えない）
            if (tb - _t1 < Tune.GridAfterKickGap * Gap(c)) return;
        }
        if (!CanStart(tb)) return;
        // つかんでいるときの拍の光は、音量では通さない（拍の間隔のまま。EventDb は NaN）
        float db = c.EventDb(tb);
        float lim = _limiter.Decide(tb, db);
        if (tb >= _t1) _flashDb = db;
        Start(tb, a, level * scale * lim, level * lim, tau, beat: true);
        BeatStarted = true;
    }

    /// <summary>出来事 1 つ: つかんでいれば拍の光の強さに使うだけ。つかめていなければキックで光る</summary>
    private void OnEvent(EffectContext c, AudioEvent ev, bool auto, float level, double tau)
    {
        bool kickLike = ev.Kind == AudioEventKind.Kick || (auto && ev.Kind == AudioEventKind.Onset);
        bool snare = ev.Kind == AudioEventKind.Snare;
        if (!(kickLike || snare) || ev.Strength < MinStrength) return;
        // 全体の立ち上がり（Onset。どの楽器でもよい）は、ドラムとは数えず、拍の強さにも使わない（つかめていないときのキックの光だけ）
        bool drum = ev.Kind != AudioEventKind.Onset;
        float a = kickLike ? Math.Min(1f, Tune.KickMinAmp + Tune.KickSlope * (ev.Strength - MinStrength)) : Tune.SnareAmp;
        if (drum)
        {
            _drumT = Math.Max(_drumT, ev.T);
            // つかめていないとき: このキック・スネアが拍の見当に乗っているか（乗っていない音が続けば、拍の見当では光らない）
            if (!_locked && c.HasTempo && c.Period > 0)
            {
                double ph = c.Beat.Phase(ev.T);
                bool on = Math.Min(ph, 1 - ph) * c.Period <= Tune.BeatWindowSec;
                _gridFit += ((on ? 1f : 0f) - _gridFit) * Tune.GridFitAlpha;
            }
            float h = DrumWeight(c, ev.T, kickLike);
            if (Math.Abs(ev.T - _hitT) < SameHitSec) { _hitA = Math.Max(_hitA, a); _hitH = Math.Max(_hitH, h); }
            else if (ev.T >= _hitT) { _hitT = ev.T; _hitA = a; _hitH = h; }
            if (_locked && Tune.HitDriven) a = h;
        }
        else if (_locked) return;
        else a = Tune.NoHitAmp;
        bool near = _beat1 && Math.Abs(ev.T - _t1) <= Tune.BeatWindowSec;
        if (_locked)
        {
            if (Tune.HitDriven)
            {
                double phase = c.Beat.Phase(ev.T);
                if (Math.Min(phase, 1 - phase) * c.Period <= Tune.BeatWindowSec)
                {
                    if (kickLike) _gridKickT = ev.T;
                    else _gridSnareT = ev.T;
                }
                // 半分のテンポがスネアだけに合っているときは、拍の中間のキックも見せる。
                else if (kickLike && Math.Abs(phase - 0.5) * c.Period <= Tune.BeatWindowSec
                    && ev.T - _gridSnareT < c.Period && ev.T - _gridKickT > 2 * c.Period
                    && ev.T - _t1 >= 0.35 * c.Period && CanStart(ev.T))
                {
                    float gain = level * _limiter.Decide(ev.T, float.NaN);
                    Start(ev.T, a, gain, gain, tau, beat: false);
                    KickStarted = true;
                }
            }
            // 届くのが遅れた、いまの拍のキック・スネア: その拍の光を強くし直す（小節の頭の分は足したまま）
            if (near) RaiseBeat(c, a);
            if (Tune.HitDriven && _slotI >= 0 && !_slotSeen && Math.Abs(ev.T - _slotT) <= Tune.BeatWindowSec)
            {
                // この拍で最初に届いた音: 拍の位置の強さを覚え直し、覚えていた強さで光り始めていたなら、実際の音の強さに直す
                _slotSeen = true;
                _slot[_slotI] += (a - _slot[_slotI]) * Tune.SlotAlpha;
                if (near && a < _r1) _r1 = a;
            }
            return;
        }
        if (!kickLike) return;
        if (near)
        {
            // つかめていないときの拍の見当の光と同じ音: 本物のキックとして、キックの光の明るさにする
            RaiseBeat(c, a);
            _g1 = Math.Max(_g1, _k1);
            return;
        }
        // 強さは、直近の大きな音とくらべた音の大きさでも弱める（小さな銃声は弱く、大きな爆発は強く）
        float db = c.EventDb(ev.T);
        float ka = Math.Clamp(0.5f + 0.25f * (ev.Strength - 1), 0.5f, 1f) * c.RelAt(db);
        // 話し声が続くときは弱くする（TalkFatigue。キックが拍の見当にそろっていれば弱めない）
        float fat = _talk.Scale(ev.T, c.HasTempo && _gridFit >= Tune.GridFitMin);
        if (!_beat1 && Math.Abs(ev.T - _t1) < SameHitSec) { _r1 = Math.Max(_r1, ka * fat); return; }
        bool trigger = c.Trigger && Math.Abs(c.TriggerT - ev.T) < 1e-9;
        // 前の光よりはっきり大きい音は、間隔が短くても光る（話し声の中でも弱めない）。
        // 前の光が音から来たなら、その音が上がりきった音量とくらべる（鳴り始めに読んだ音量は低すぎて、ピッという音の終わりでもう一度光った）
        float refDb = _flashDb;
        if (!_beat1 && !float.IsNaN(refDb))
        {
            float settled = c.EventDbSpan(_t1, Math.Min(_t1 + FlashLimiter.SettleSec, ev.T - 0.01));
            if (settled > refDb) refDb = settled;
        }
        bool louder = ev.T - _t1 > SameHitSec && db >= refDb + FlashLimiter.RaiseDb;
        if (!louder) ka *= fat;
        if (!trigger && !louder && ev.T - _t1 < Gap(c)) return;
        // 色を進めるキックが、今の光より前の時刻に届いた（検出の遅れ）ときは、1 つ前の光として入れる（色だけが変わって見えないように）
        if (!CanStart(ev.T)) return;
        float lim = _limiter.Decide(ev.T, db, c);
        if (ev.T >= _t1) _flashDb = db;
        Start(ev.T, ka, level * lim, level * lim, tau, beat: false);
        KickStarted = true;
    }

    /// <summary>時刻 t の光（0〜1）</summary>
    public double At(double t) => Math.Min(1, Math.Max(Pulse(t, _t1, _r1 * _g1, _tau1), Pulse(t, _t0, _r0 * _g0, _tau0)));

    /// <summary>つかめていないときの、キックの光の間隔（秒）</summary>
    private static double Gap(EffectContext c) =>
        c.HasTempo ? Math.Clamp(Tune.GapBeats * c.Period, Tune.GapMinSec, Tune.GapMaxSec) : Tune.GapSec;

    private void RaiseBeat(EffectContext c, float a)
    {
        float accent = !(_locked && Tune.HitDriven) && c.HasTempo && c.Beat.Bar(_t1 + 0.25 * c.Period) == 0 ? Tune.AccentAdd : 0;
        _r1 = Math.Max(_r1, a + accent);
    }

    /// <summary>キックは低音の包絡線で、スネアは一定の割合で光らせ、どちらも音量に合わせる。</summary>
    private float DrumWeight(EffectContext c, double t, bool kick)
    {
        if (c.Features is not { } ring || !ring.Sample(t + Tune.KickEnvAtSec, _kickFrame)) return kick ? 1f : Tune.LockedSnareAmp;
        float envelope = kick ? Math.Clamp((_kickFrame.KickEnv - Tune.KickEnvLo) / Tune.KickEnvSpan, Tune.KickWeightMin, 1f) : Tune.LockedSnareAmp;
        // 自動調整で同じ高さになったキックにも、元の音量の差を残す。
        float loudness = Math.Clamp(MathF.Pow(10f, (_kickFrame.LoudDb + 10f) / 20f), 0.6f, 1f);
        return envelope * loudness;
    }

    /// <summary>いま光っている拍を閉じる: 音が届かなかったら、その拍の位置の強さを下げ、光も音のない拍の強さにする</summary>
    private void CloseSlot()
    {
        if (_slotI < 0) return;
        if (!_slotSeen)
        {
            _slot[_slotI] += (Tune.LockedNoHitAmp - _slot[_slotI]) * Tune.SlotAlpha;
            if (_beat1 && Math.Abs(_t1 - _slotT) < 1e-9) _r1 = Math.Min(_r1, Tune.LockedNoHitAmp);
            _missRun++;
        }
        else _missRun = 0;
        _slotI = -1;
    }

    /// <summary>時刻 t に光を始められるか（いまの光より後か、いまの光と 1 つ前の光の間なら 1 つ前の光として入れられる）</summary>
    private bool CanStart(double t) => t >= _t1 || t > _t0;

    /// <param name="g">倍率（明るさ × 確かさ × 点滅の制限）</param>
    /// <param name="k">本物のキックとしての倍率（明るさ × 点滅の制限）</param>
    private void Start(double t, float a, float g, float k, double tau, bool beat)
    {
        if (t < _t1)
        {
            // いまの光より前の時刻（届くのが遅れたキック・拍の並びが前へずれた）: 1 つ前の光として入れる（いまの光はそのまま）
            _t0 = t; _r0 = a; _g0 = g; _tau0 = tau;
            return;
        }
        _t0 = _t1; _r0 = _r1; _g0 = _g1; _tau0 = _tau1;
        _t1 = t; _r1 = a; _g1 = g; _k1 = k; _tau1 = tau; _beat1 = beat;
    }

    /// <summary>時刻が d 秒（負）だけ戻った: 覚えている時刻をそろえてずらす（点滅の制限は数え直す）</summary>
    private void Shift(double d)
    {
        _t1 += d;
        _t0 += d;
        _hitT += d;
        _drumT += d;
        _gridKickT += d;
        _gridSnareT += d;
        _slotT += d;
        _silentT = double.NaN;
        _limiter.Reset();
    }

    private static double Pulse(double t, double tStart, double a, double tau)
    {
        if (a <= 0 || double.IsInfinity(tStart) || t < tStart) return 0;
        return a * Math.Exp(-(t - tStart) / tau);
    }
}

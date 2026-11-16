// ==================================================
// BeatFlash.cs - 「ビートで色替え」「パルス」の拍の光 (テンポをつかんでいれば予測した拍の頭だけで光り、
//                つかめていなければキックで光る。拍の間のキック・ベースの音では光らない)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 「ビートで色替え」「パルス」で、光り始めの時刻を決める（どちらも同じ時刻に光るので、見た目のリズムがそろう）。
/// 以前は低音の大きさ（BassE）でも光らせていたので、8 分・16 分のベースや拍の間のキックでも光り、
/// 光の半分以上が拍の外で、どの音に合わせているのか分からなかった（録音 17 曲で、拍に乗った光は 4 割）。そこで光るきっかけを決める:
///   テンポをつかんでいて（BeatState.Locked。少し確かさが下がった Coasting を含む）:
///       予測した拍の頭で光る（色の切り替えと同じ拍）。拍の間のキック・スネア・ベースでは光らない。
///       強さ a = その拍のキック（± BeatWindowSec）の強さで KickMinAmp〜1、スネアだけなら SnareAmp、どちらもなければ NoHitAmp
///       （直近 QuietSec 秒にキック・スネアが 1 つもない静かなところでは QuietAmp）。小節の頭は AccentAdd だけ強く（静かなところは除く）。
///       キックは拍の頭より少し遅れて届くことがあるので、届いたときに、その拍の光の強さを上げ直す。
///       さらに確かさで弱める: × (ConfFloor + (1 − ConfFloor) × 確かさ)
///   つかめていない:
///       キック（強さ 1.2 以上。Auto なら全体の立ち上がりも）で光る。前の光から GapSec あける
///       （テンポの見当があれば 0.6 拍・0.22〜0.45 秒。半拍のキックで 2 回光らない）。
///       色を進めるキック（BeatDriver の合図）は、間隔によらず必ず光る（色だけが変わって見えないように）。
///       拍の見当（予測した拍の頭）があり、ドラムが鳴っていれば、そこでも UnlockedGridGain 倍の弱い光
///       （キックが重なれば、キックの光の明るさにする）。ドラムのないところ（パッドだけの間奏など）では拍の見当では光らない
///       （鳴っている音がないのに、決まった速さで点滅し続けて見えた）
///   光の値 = Level × a × exp(−光り始めからの秒数 / τ)、Level = LevelBase + LevelAmp × FlashAmp（強さの設定）、τ = TauBeats 拍
/// 「激しい点滅を抑える」がオンなら、光り始めを 1 秒に 3 回まで（FlashLimiter。超えた光は 0.35 倍）。
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
    // このコマで、新しい光が始まった・今の光が強くなった（検出が遅れて、前のコマより前の時刻のこともある）。_fresh0 は 1 つ前の光
    private bool _fresh, _fresh0;
    private double _tau1 = Tune.TauSec, _tau0 = Tune.TauSec;
    // 直近のキック・スネア（拍の頭より先に届いたものを、次の拍の強さに使う）
    private double _hitT = double.NegativeInfinity, _drumT = double.NegativeInfinity;
    private float _hitA;
    private readonly FlashLimiter _limiter = new();
    // 前のコマの時刻（時刻が戻ったかを見る）
    private double _lastT = double.NegativeInfinity;
    // このコマで、テンポをつかんでいる（拍の頭で光らせる）
    private bool _locked;

    /// <summary>今のコマの時刻の光（0〜1）</summary>
    public float Value { get; private set; }
    /// <summary>このコマで光り始めた光があれば、その始まりの値と時刻（なければ Value と今の時刻）</summary>
    public float Peak { get; private set; }
    public double PeakT { get; private set; }
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
        _r1 = _r0 = _g1 = _g0 = _k1 = _hitA = 0;
        _beat1 = _fresh = _fresh0 = false;
        _tau1 = _tau0 = Tune.TauSec;
        _locked = false;
        _limiter.Reset();
        Value = Peak = 0;
        PeakT = 0;
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
        BeatStarted = KickStarted = _fresh = _fresh0 = false;
        float level = Tune.LevelBase + Tune.LevelAmp * (float)Math.Clamp(m.FlashAmp, 0, 1);
        double tau = c.HasTempo ? Math.Clamp(Tune.TauBeats * c.Period, Tune.TauMinSec, Tune.TauMaxSec) : Tune.TauSec;

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
        Drumless = !_locked && c.T - _drumT > Tune.QuietSec;

        // 今の時刻の値と、このコマで光り始めた光の始まりの値
        double t1 = c.T;
        Value = (float)At(t1);
        Peak = Value;
        PeakT = t1;
        // （届くのが遅れた光も、光り始めの時刻からの値にする。1 秒間のコマ数で、届いたあと最初のコマの時刻が違っても同じ残光になる）
        if ((_fresh || _t1 > c.TPrev) && _t1 <= t1) ConsiderPeak(_t1);
        if ((_fresh0 || _t0 > c.TPrev) && _t0 <= t1) ConsiderPeak(_t0);
    }

    private void ConsiderPeak(double t)
    {
        float v = (float)At(t + 1e-6);
        if (v > Peak) { Peak = v; PeakT = t; }
    }

    /// <summary>予測した拍の頭（時刻 tb）をまたいだ</summary>
    private void OnGrid(EffectContext c, double tb, float level, double tau)
    {
        // 静かなところ（ドラムがない）では、小節の頭も強くしない（拍が弱くゆっくり見えるだけにする）
        bool quiet = tb - _drumT > Tune.QuietSec;
        // つかんでいれば拍の光。つかめていなくても、拍の見当があり、ドラムが鳴っていれば弱い光
        // （ドラムのないところでは光らない。鳴っている音がないのに、決まった速さで点滅し続けて見えた）
        // 曲が変わったと知らされたあと、拍の並びが前の曲のまま（GridStale）なら、拍の見当では光らない（キックで光る）
        bool unlockedGrid = !_locked && Tune.UnlockedGridGain > 0 && !quiet && !(c.GridStale && Tune.StaleNoGrid);
        if (!(_locked || unlockedGrid)) return;
        float a = quiet ? Tune.QuietAmp : Tune.NoHitAmp;
        if (Math.Abs(tb - _hitT) <= Tune.BeatWindowSec) a = Math.Max(a, _hitA);
        if (!quiet && c.Beat.Bar(tb + 0.25 * c.Period) == 0) a += Tune.AccentAdd;
        float scale = (Tune.ConfFloor + (1 - Tune.ConfFloor) * c.Conf) * (_locked ? 1 : Tune.UnlockedGridGain);
        if (!_locked && tb >= _t1)
        {
            // つかめていないとき、キックの光の直後の拍の頭: 同じ音として、強さだけ上げる（2 回光らせない）
            if (!_beat1 && tb - _t1 <= Tune.BeatWindowSec) { _r1 = Math.Max(_r1, a); _fresh = true; return; }
            // キックの光から間がない拍の頭では光らない（キックの光の直後にもう一度光って見えない）
            if (tb - _t1 < Tune.GridAfterKickGap * Gap(c)) return;
        }
        if (!CanStart(tb)) return;
        float lim = _limiter.Decide(tb);
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
            if (Math.Abs(ev.T - _hitT) < SameHitSec) _hitA = Math.Max(_hitA, a);
            else if (ev.T >= _hitT) { _hitT = ev.T; _hitA = a; }
        }
        else if (_locked) return;
        else a = Tune.NoHitAmp;
        bool near = _beat1 && Math.Abs(ev.T - _t1) <= Tune.BeatWindowSec;
        if (_locked)
        {
            // 届くのが遅れた、いまの拍のキック・スネア: その拍の光を強くし直す（小節の頭の分は足したまま）
            if (near) RaiseBeat(c, a);
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
        float ka = Math.Clamp(0.5f + 0.25f * (ev.Strength - 1), 0.5f, 1f);
        if (!_beat1 && Math.Abs(ev.T - _t1) < SameHitSec) { _r1 = Math.Max(_r1, ka); _fresh = true; return; }
        bool trigger = c.Trigger && Math.Abs(c.TriggerT - ev.T) < 1e-9;
        if (!trigger && ev.T - _t1 < Gap(c)) return;
        // 色を進めるキックが、今の光より前の時刻に届いた（検出の遅れ）ときは、1 つ前の光として入れる（色だけが変わって見えないように）
        if (!CanStart(ev.T)) return;
        float lim = _limiter.Decide(ev.T);
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
        float accent = c.HasTempo && c.Beat.Bar(_t1 + 0.25 * c.Period) == 0 ? Tune.AccentAdd : 0;
        _r1 = Math.Max(_r1, a + accent);
        _fresh = true;
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
            _fresh0 = true;
            return;
        }
        _t0 = _t1; _r0 = _r1; _g0 = _g1; _tau0 = _tau1;
        _t1 = t; _r1 = a; _g1 = g; _k1 = k; _tau1 = tau; _beat1 = beat;
        _fresh = true;
    }

    /// <summary>時刻が d 秒（負）だけ戻った: 覚えている時刻をそろえてずらす（点滅の制限は数え直す）</summary>
    private void Shift(double d)
    {
        _t1 += d;
        _t0 += d;
        _hitT += d;
        _drumT += d;
        _limiter.Reset();
    }

    private static double Pulse(double t, double tStart, double a, double tau)
    {
        if (a <= 0 || double.IsInfinity(tStart) || t < tStart) return 0;
        return a * Math.Exp(-(t - tStart) / tau);
    }
}

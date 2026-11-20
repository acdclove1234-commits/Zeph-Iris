// ==================================================
// BeatDriver.cs - ビートの合図とビートの光の計算 (予測したビート・キックなどの出来事から、
//                 色を進めるきっかけと「ぱっと光って消える」光の強さを、デバイスごとに求める)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// デバイスごとに 1 つ。毎コマ、前のコマの時刻 t0 から今の時刻 t1 まで（t0 は含まず t1 は含む）を見て、
///   合図（Trigger）: テンポをつかんでいれば予測したビートの頭、つかんでいなければ強いキック（強さ 1.5 以上、0.25 秒以上あける）
///   ビートの光（Flash）= 確かさ × 予測の光 + (1 − 確かさ) × 出来事の光（テンポをつかんでいないときは出来事の光だけ。遅いデバイス用の光り方は除く。
///       予測した拍で光らせても色はキックで進むので、光ったあとで色だけが変わって、色替えの途中で光って見えていた）
///       予測の光 = exp(−拍の頭からの秒数 / τf)、τf = 0.35 × 1 拍（0.12〜0.28 秒）
///       出来事の光 = a × exp(−出来事からの秒数 / 0.25 秒)、a = 0.5 + 0.25 × (強さ − 1)（0.5〜1）
///   色の切り替え: 合図 ColorStepBeats 回ごとに、パレットを 1 つ進める（テンポをつかんでいれば小節の頭にそろえる）。
///       テンポをつかんでいるときは、次の拍の前に切り替え始めて拍の頭で切り替え終わる（ビートの光が新しい色で光る。
///       切り替えにかける時間は CrossfadeSec と 1 拍の PreFadeMaxBeats 倍の短いほう）。
///       つかんでいないときはキックで進めるので前もって切り替えられない: 速いデバイスはキックの光と同時に一度に切り替える
///       （クロスフェードすると、キックの光のいちばん明るいところが途中の色になり、そのあと色だけが変わって見える）
///   全体の光り方が「ビートで色替え」（テンポ Auto）なら、テンポをつかんでいても拍の数では進めず、拍に乗ったキックで進める（HitSteps）:
///       ふだんは前の切り替えから ColorStepBeats × HitStepMul 拍たったキックの拍で、和音・ベースの音が変わる拍の位置（たいてい小節の頭）がわかればそこで。
///       キックが来そうな拍（1 小節前の同じ拍と直前の拍にキック）なら、ほかの光り方と同じく拍の前から切り替え始める。
///       しばらくキックがなかったあとの最初のキック・直近より大きいキックは区切りとして早めに進める。キックのない間奏では進めない
///       （キックが HitNoKickSec 秒なければ、ゆっくり拍で進める）。遅いデバイスの拍の光の強さ FlHit も、その拍で鳴った音で決める
/// 時刻はすべて「音が鳴った本当の時刻」で計算するので、1 秒間のコマ数が違っても同じ光り方になる
/// </summary>
public sealed class BeatDriver
{
    /// <summary>合図にするキックの強さの下限（しきい値の何倍か）</summary>
    public const float TriggerMinStrength = 1.5f;
    /// <summary>合図の最小間隔（秒）</summary>
    public const double TriggerMinGapSec = 0.25;
    /// <summary>出来事の光にするキックの強さの下限</summary>
    public const float FlashMinStrength = 1.2f;
    /// <summary>この間隔（秒）以内のキックと立ち上がりは、同じ 1 つの音とみなす（光を 2 回にしない）</summary>
    public const double SameHitSec = 0.06;
    /// <summary>出来事の光が消えていく時定数（秒）</summary>
    public const double OnsetFlashTauSec = 0.25;
    /// <summary>更新の遅いデバイスでの、ビートの光が消えていく時定数の下限（秒）。速く消えると見逃すため</summary>
    public const double SlowFlashTauMinSec = 0.30;
    /// <summary>更新の遅いデバイスでの、色の切り替えにかける時間の下限（秒）</summary>
    public const double SlowCrossfadeMinSec = 0.15;
    /// <summary>拍の前に色を切り替え始めるときの、切り替えにかける時間の上限（1 拍の何倍か。前の拍の光の最中に混ぜ始めない）</summary>
    public const double PreFadeMaxBeats = 0.6;

    /// <summary>
    /// テンポをつかんでいないときも予測した拍で光らせる（6.15.8 までの動き。調べる用に、テストから前と比べるために変えられる。アプリの中では変えない）
    /// </summary>
    internal static bool UnlockedPrediction { get; set; }
    /// <summary>テンポをつかんでいるとき、拍の前に色を切り替え始める（調べる用。アプリの中では変えない）</summary>
    internal static bool PreFade { get; set; } = true;

    /// <summary>
    /// 全体の光り方が「ビートで色替え」（テンポ Auto）なら、テンポをつかんでいても、色は拍の数ではなく、拍に乗ったキックで進める（false = 6.15.10 までの、
    /// 決まった拍の数ごと。調べる用。アプリの中では変えない）。6.15.10 までは決まった間隔で色が変わるだけで、ドラムのない間奏でも変わり続け、
    /// 小節の頭の見当がずれると 2・4 拍目で変わっていた
    /// </summary>
    internal static bool HitSteps = true;
    /// <summary>キックで色を進めるとき: ふだんは前の切り替えから ColorStepBeats × HitStepMul 拍たってから（Subtle 8・Moderate 4・High 2 拍）</summary>
    internal static double HitStepMul = 2;
    /// <summary>
    /// 区切りのキック（前のキックから HitGapBeats 拍以上あいた（2 拍以上キックのなかった）キック・直近 4 つのキックより HitLouderDb 以上大きいキック）は、
    /// 前の切り替えから max(1, ColorStepBeats / 2) 拍で進めてよい（ドロップ・サビの頭で色が変わる）
    /// </summary>
    internal static double HitGapBeats = 2.75;
    internal static float HitLouderDb = 1.5f;
    internal static double HitLouderBeats = 8;
    /// <summary>拍に乗ったキックとみなす、拍の頭とのずれ（秒）</summary>
    internal static double HitOnBeatSec = 0.08;
    /// <summary>
    /// キックが HitNoKickSec 秒ないとき（ドラムのない曲・ところ）は、音が鳴っていれば ColorStepBeats × HitStepMul × HitDrumlessMul 拍ごとに拍で進める
    /// （色が止まったままにならないように）
    /// </summary>
    internal static double HitNoKickSec = 2.0, HitDrumlessMul = 4;
    /// <summary>キックはあるのに HitNoKickSec 秒どれも拍に乗っていないときは、ColorStepBeats × HitStepMul × HitOffGridMul 拍ごとに拍で進める</summary>
    internal static double HitOffGridMul = 2;
    /// <summary>
    /// 次の拍で色を進める時期で、その拍にキックが来そう（1 小節前の同じ拍と、直前か 2 拍前の拍にキックがあった）なら、拍の前から切り替え始める
    /// （キックが見つかるのは拍の頭より少し後なので、見つかってから切り替えると、光のいちばん明るいところが前の色になる）
    /// </summary>
    internal static bool HitPredict = true;
    /// <summary>
    /// ふだんの色の切り替えを、和音・ベースの音が変わる拍の位置（たいてい小節の頭）にそろえる（HarmonyPhase。false = そろえない。調べる用）。
    /// 拍の位置ごとの変わり方を HarmDecay で古くしながらため、いちばん大きい位置が HarmMinScore 以上・2 番目の HarmClear 倍以上ならそろえる
    /// </summary>
    internal static bool HitHarmony = true;
    internal static double HarmDecay = 0.75, HarmMinScore = 1.0, HarmClear = 1.5, HarmChromaShare = 0.5;

    private double _lastTriggerT = double.NegativeInfinity;
    // 予測したビートの頭をまたいだか（予測が書き換わっても同じビートを 2 回合図にしない）
    private readonly BeatCrossing _crossing = new();
    // 出来事の光は、直近 2 つを覚えておき明るいほうを使う（新しい光を弱めたとき、前の光が急に消えないように）
    private double _onT1 = double.NegativeInfinity, _onT0 = double.NegativeInfinity;
    private float _onA1, _onA0, _onG1 = 1, _onG0 = 1;
    private float _predGain = 1;
    // 無音のまま来た、予測した拍の頭の時刻（NaN = なし）。すぐ後に音が来たら、その拍の光を出す
    private double _silentT = double.NaN;
    // 拍の前に色を切り替え始めた拍の時刻（その拍の頭では、もう進めない）
    private double _preT = double.NegativeInfinity;
    // このコマで新しく分かった光（検出の遅れで、前のコマの範囲より前の時刻のこともある）
    private bool _fresh1, _fresh0;
    // キックで色を進めるとき（HitSteps）: 前に色を進めた時刻・直近のキックの時刻、直近のキックの時刻と音量（dB。拍に乗ったものだけ）
    private double _hitStepT = double.NegativeInfinity, _hitKickT = double.NegativeInfinity, _onBeatKickT = double.NegativeInfinity;
    private readonly double[] _kickTs = Enumerable.Repeat(double.NegativeInfinity, 4).ToArray();
    private int _kickTHead;
    private readonly double[] _kickDbT = Enumerable.Repeat(double.NegativeInfinity, 8).ToArray();
    private readonly float[] _kickDbs = new float[8];
    private int _kickDbHead;
    // 拍に乗ったキックがあった拍の番号（直近 8 拍。次の拍にキックが来そうかを見る）
    private readonly long[] _kickBeats = Enumerable.Repeat(long.MinValue, 8).ToArray();
    private int _kickBeatHead;
    // 遅いデバイスの拍の光の強さ（FlHit）: いまの拍の時刻と強さ、直近のキック・スネアの時刻と強さ
    private double _hitBeatT = double.NegativeInfinity, _hitT = double.NegativeInfinity;
    private float _hitBeatA = 1, _hitA;
    // 拍の位置（拍の番号を 8 で割った余り）ごとの、和音・ベースの音の変わり方と、まだ測っていない拍の時刻（NaN = なし）
    private readonly double[] _harm = new double[8];
    private readonly double[] _harmT = { double.NaN, double.NaN, double.NaN };
    private int _harmHead;
    private readonly AudioFrame _hf = new();

    /// <summary>激しい点滅の制限</summary>
    public FlashLimiter Limiter { get; } = new();

    /// <summary>状態を最初に戻す（デバイスを作り直したときなど）</summary>
    public void Reset()
    {
        _lastTriggerT = _onT1 = _onT0 = _preT = double.NegativeInfinity;
        _onA1 = _onA0 = 0;
        _onG1 = _onG0 = _predGain = 1;
        _silentT = double.NaN;
        _hitStepT = _hitKickT = _onBeatKickT = _hitBeatT = _hitT = double.NegativeInfinity;
        Array.Fill(_kickTs, double.NegativeInfinity);
        Array.Fill(_kickDbT, double.NegativeInfinity);
        Array.Clear(_harm);
        Array.Fill(_harmT, double.NaN);
        _hitBeatA = 1;
        _hitA = 0;
        Array.Fill(_kickBeats, long.MinValue);
        _crossing.Reset();
        Limiter.Reset();
    }

    /// <summary>
    /// 1 コマ分を計算して c（Trigger・Fl・Locked など）に書く。t0 = 前のコマの音の時刻、t1 = 今のコマの音の時刻。
    /// c.Beat・c.Events・c.Music・c.Pal・c.Slow は先に入れておくこと
    /// </summary>
    public void Update(EffectContext c, double t0, double t1)
    {
        var beat = c.Beat;
        var m = c.Music;
        bool auto = m.TempoMode == TempoModes.Auto;
        bool off = m.TempoMode == TempoModes.Off;
        Limiter.Enabled = m.ReduceFlashing;

        // ---- テンポ（Auto のときだけ予測を使う） ----
        c.HasTempo = auto && beat.HasTempo && beat.Period > 0;
        c.Conf = c.HasTempo ? (float)Math.Clamp(beat.Confidence, 0, 1) : 0f;
        c.Locked = c.HasTempo && beat.Locked;
        c.GridStale = c.HasTempo && beat.Stale;
        c.Period = c.HasTempo ? beat.Period : 0.5;
        c.Phase = c.HasTempo ? beat.Phase(t1) : 0;
        c.BeatIndex = c.HasTempo ? beat.Index(t1) : 0;
        c.Downbeat = c.Locked && beat.Bar(t1) == 0;
        // またいだビートの時刻 = t1 を含む拍の頭（1 コマで 2 拍以上またぐのは極端に遅いときだけなので、新しいほうを使う）
        c.BeatCrossed = _crossing.Step(beat, c.HasTempo, t0, t1, out c.BeatCrossT);
        c.Trigger = false;
        c.TriggerT = t1;
        c.TriggerStrength = 0;
        _fresh1 = _fresh0 = false;

        // ---- 出来事と予測ビートを、時刻の順に処理する ----
        bool beatPending = c.BeatCrossed && !off;
        var events = c.Events;
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            if (beatPending && ev.T > c.BeatCrossT)
            {
                OnPredictedBeat(c);
                beatPending = false;
            }
            if (off) continue;
            if (ev.Kind is AudioEventKind.Kick or AudioEventKind.Snare && ev.Strength >= FlashMinStrength) NoteHit(ev);
            bool kickLike = ev.Kind == AudioEventKind.Kick || (auto && ev.Kind == AudioEventKind.Onset);
            if (!kickLike) continue;
            OnOnset(c, ev);
        }
        if (beatPending) OnPredictedBeat(c);
        // 無音で待っていた拍の頭: RippleEffect.ConfirmSec 秒以内に音が来たら、その拍の光を出す（来なければ光らない）
        bool late = false;
        if (!double.IsNaN(_silentT))
        {
            if (t1 - _silentT >= RippleEffect.ConfirmSec) _silentT = double.NaN;
            else if (c.Frame.RmsDb > RippleEffect.SilenceDb) { _predGain = Limiter.Decide(_silentT); late = true; }
        }
        if (c.Locked && !off && UsesHitSteps(c)) UpdateHarmony(c);
        if (c.Locked && !off) PreStep(c, t1);

        // ---- ビートの光（今の時刻の値と、このコマの間の最大値） ----
        c.Fl = c.FlPeak = c.FlHit = 0;
        c.FlPeakT = t1;
        if (off) return;
        c.FlHit = (float)Math.Clamp(m.FlashAmp * FlashAt(c, t1, hitScaled: true), 0, 1);
        double now = FlashAt(c, t1);
        double peak = now, peakT = t1;
        // 光が最大になるのは光り始めの瞬間（予測したビートの頭・キックの時刻）。このコマの間にあったか、
        // このコマで初めて届いた（検出が遅れた）光なら、そこも見る
        if (c.BeatCrossed && c.BeatCrossT > t0) Consider(c, c.BeatCrossT, ref peak, ref peakT);
        if (late)
        {
            Consider(c, _silentT, ref peak, ref peakT);
            _silentT = double.NaN;
        }
        if (_onT1 <= t1 && (_fresh1 || _onT1 > t0)) Consider(c, _onT1, ref peak, ref peakT);
        if (_onT0 <= t1 && (_fresh0 || _onT0 > t0)) Consider(c, _onT0, ref peak, ref peakT);
        c.Fl = (float)Math.Clamp(m.FlashAmp * now, 0, 1);
        c.FlPeak = (float)Math.Clamp(m.FlashAmp * peak, 0, 1);
        c.FlPeakT = peakT;
    }

    private void Consider(EffectContext c, double t, ref double peak, ref double peakT)
    {
        double v = FlashAt(c, t + 1e-6);
        if (v > peak) { peak = v; peakT = t; }
    }

    /// <summary>
    /// 時刻 t のビートの光（FlashAmp を掛ける前）= 確かさ × 予測の光 + (1 − 確かさ) × 出来事の光。
    /// hitScaled なら、予測の光をその拍で実際に鳴った音の強さ倍にする（キック 1・スネア・音のない拍。FlHit）
    /// </summary>
    public double FlashAt(EffectContext c, double t, bool hitScaled = false)
    {
        if (c.Music.TempoMode == TempoModes.Off) return 0;
        var beat = c.Beat;
        bool slow = c.Slow;
        double fpred = 0;
        double conf = PredictFlash(c) ? c.Conf : 0;
        if (conf > 0)
        {
            double tauF = Math.Clamp(0.35 * beat.Period, 0.12, 0.28);
            if (slow) tauF = Math.Max(tauF, SlowFlashTauMinSec);
            fpred = Math.Exp(-beat.Phase(t) * beat.Period / tauF) * _predGain * (hitScaled ? _hitBeatA : 1);
        }
        double tauO = slow ? Math.Max(OnsetFlashTauSec, SlowFlashTauMinSec) : OnsetFlashTauSec;
        double fons = Math.Max(Pulse(t, _onT1, _onA1 * _onG1, tauO), Pulse(t, _onT0, _onA0 * _onG0, tauO));
        return conf * fpred + (1 - conf) * fons;
    }

    /// <summary>
    /// 予測した拍で光らせるか（テンポをつかんでいるときだけ。遅いデバイス用の光り方（c.Slow）は、つかんでいなくても光らせる:
    /// 光も色の切り替えもゆっくりなので、キックの光と色の切り替えが重なると、送った 1 回だけ新しい色で明るく光って見えるため）。
    /// ただし、つかめていない拍の並びが前の曲のもの（c.GridStale。曲が変わったと知らされた直後）なら光らせない（前の曲の拍で光る）
    /// </summary>
    private static bool PredictFlash(EffectContext c) => c.HasTempo && c.Conf > 0 && (c.Locked || ((UnlockedPrediction || c.Slow) && !c.GridStale));

    /// <summary>時刻 tStart に強さ a で始まり、時定数 tau で消えていく光の、時刻 t の値</summary>
    private static double Pulse(double t, double tStart, double a, double tau)
    {
        if (a <= 0 || double.IsInfinity(tStart) || t < tStart) return 0;
        return a * Math.Exp(-(t - tStart) / tau);
    }

    /// <summary>予測したビートの頭をまたいだ</summary>
    private void OnPredictedBeat(EffectContext c)
    {
        double tb = c.BeatCrossT;
        if (PredictFlash(c))
        {
            // 拍の頭が無音（デジタルの無音）なら、まだ光らない（音のない拍で光って見えた。色の切り替えはそのまま）
            if (c.Frame.RmsDb <= RippleEffect.SilenceDb) { _predGain = 0; _silentT = tb; }
            else { _predGain = Limiter.Decide(tb); _silentT = double.NaN; }
        }
        // 遅いデバイスの拍の光の強さ（FlHit）: 拍の頭までに届いたキック・スネアの強さ、なければ音のない拍の強さ（あとから届いたら上げる）
        _hitBeatT = tb;
        _hitBeatA = Math.Abs(tb - _hitT) <= HitOnBeatSec ? _hitA : BeatFlash.Tune.LockedNoHitAmp;
        if (!c.Locked) return;
        bool step;
        if (UsesHitSteps(c))
        {
            // この拍の和音・ベースの変わり方を、拍の終わりの近くで測る（UpdateHarmony）
            _harmT[_harmHead] = tb;
            _harmHead = (_harmHead + 1) % _harmT.Length;
            // キックで色を進める: 拍では進めない。ただしキックが HitNoKickSec 秒ない（ドラムのないところ）なら、ゆっくり拍で進める。
            // キックはあるのに拍に乗っていない（拍の並びがキックとずれている）ときは、それより早めに拍で進める（色が止まったままにならない）
            double b = StepEvery(c) * HitStepMul;
            double every = tb - _hitKickT > HitNoKickSec ? b * HitDrumlessMul : tb - _onBeatKickT > HitNoKickSec ? b * HitOffGridMul : double.PositiveInfinity;
            step = (tb - LastHitStep(c)) / c.Period >= every - 0.25 && c.Frame.RmsDb > RippleEffect.SilenceDb;
            if (step) _hitStepT = tb;
            // 一度に切り替えるデバイス（拍の前から切り替えない）は、キックが来そうな拍なら、拍の頭で進める
            else if (!(Math.Min(CrossfadeSec(c), PreFadeMaxBeats * c.Period) > 0) && PreFade) step = HitStepDue(c, tb);
        }
        // テンポをつかんでいる: 予測したビートが合図。色は小節の頭にそろえて ColorStepBeats 拍ごとに進める
        // （拍の前に切り替え始めた拍では、もう進めない）
        else step = IsStepBeat(c, c.BeatIndex) && !(Math.Abs(tb - _preT) < SameBeatSec(c));
        FireTrigger(c, tb, 1f, step, sameSec: SameBeatSec(c));
    }

    /// <summary>キックで色を進めるか（全体の光り方が「ビートで色替え」・テンポ Auto。色はほかのデバイスと共有するので、全体の光り方で決める）</summary>
    internal static bool UsesHitSteps(EffectContext c) =>
        HitSteps && c.Music.Effect == MusicStyles.BeatHue && c.Music.TempoMode == TempoModes.Auto;

    /// <summary>キック・スネアが来た（遅いデバイスの拍の光の強さ FlHit）</summary>
    private void NoteHit(AudioEvent ev)
    {
        float a = ev.Kind == AudioEventKind.Kick ? 1f : BeatFlash.Tune.LockedSnareAmp;
        if (Math.Abs(ev.T - _hitT) < SameHitSec) _hitA = Math.Max(_hitA, a);
        else if (ev.T >= _hitT) { _hitT = ev.T; _hitA = a; }
        if (Math.Abs(ev.T - _hitBeatT) <= HitOnBeatSec) _hitBeatA = Math.Max(_hitBeatA, a);
    }

    /// <summary>
    /// キックで色を進めるとき、時刻 tn の拍で、キックを待たずに拍の頭で進めるか: 進める時期（前の切り替えから ColorStepBeats × HitStepMul 拍）で、
    /// その拍にキックが来そう（1 小節前の同じ拍と、直前の拍か 2 拍前にキックがあった）。進めるなら、進めた時刻を覚える
    /// </summary>
    private bool HitStepDue(EffectContext c, double tn)
    {
        if (!HitPredict) return false;
        long n = c.Beat.Index(tn + 0.25 * c.Period);
        if (!OnStepBeat(c, n, (tn - LastHitStep(c)) / c.Period)) return false;
        if (!(KickOnBeat(n - 4) && (KickOnBeat(n - 1) || KickOnBeat(n - 2)))) return false;
        _hitStepT = tn;
        return true;
    }

    /// <summary>
    /// キックで色を進めるとき、ふだんの切り替えの時期か: 前の切り替えから b = ColorStepBeats × HitStepMul 拍たっていて、
    /// 和音・ベースの音が変わる拍の位置（HarmonyPhase）が分かっていれば、その位置の拍（その位置にキックがなければ 1.5 × b 拍で、どのキックでも）
    /// </summary>
    private bool OnStepBeat(EffectContext c, long n, double since)
    {
        double b = StepEvery(c) * HitStepMul;
        if (since < b - 0.25) return false;
        var (m, r) = HarmonyPhase(b);
        return r < 0 || (int)(((n % m) + m) % m) == r || since >= 1.5 * b - 0.25;
    }

    /// <summary>HarmonyPhase で調べる、拍の位置の数（大きいほうから）</summary>
    private static readonly int[] HarmMods = { 8, 4, 2 };

    /// <summary>
    /// 和音・ベースの音が変わりやすい、拍の位置（拍の番号を m で割った余り r。m は b を超えない 8・4・2。分からなければ r = −1）。
    /// 曲は小節の頭で和音が変わることが多いので、色の切り替えをそこにそろえる（キックだけでは、4 つ打ちの曲で小節の何拍目か分からない）
    /// </summary>
    private (int M, int R) HarmonyPhase(double b)
    {
        if (!HitHarmony) return (1, -1);
        foreach (int m in HarmMods)
        {
            if (m > b + 1e-6) continue;
            double best = 0, second = 0;
            int r = -1;
            for (int k = 0; k < m; k++)
            {
                double s = 0;
                for (int j = k; j < _harm.Length; j += m) s += _harm[j];
                if (s > best) { second = best; best = s; r = k; }
                else if (s > second) second = s;
            }
            if (best >= HarmMinScore && best >= HarmClear * second) return (m, r);
        }
        return (1, -1);
    }

    /// <summary>
    /// 拍 tb の前後で、和音・ベースの音がどれだけ変わったか（拍の終わりの近くまで待って測り、拍の位置ごとにためる。HarmonyPhase）。
    /// ベースの音名が変わったら 1、12 音の色の違い（1 − cos）を HarmChromaShare 倍して足す
    /// </summary>
    private void UpdateHarmony(EffectContext c)
    {
        for (int q = 0; q < _harmT.Length; q++) UpdateHarmony(c, q);
    }

    private void UpdateHarmony(EffectContext c, int q)
    {
        if (double.IsNaN(_harmT[q]) || c.Features is not { } ring || !(c.Period > 0)) return;
        double tb = _harmT[q], p = c.Period, tAfter = tb + 0.8 * p;
        if (c.T - tb > 3 * p) { _harmT[q] = double.NaN; return; }
        if (c.T < tAfter || !ring.Sample(tAfter, _hf) || _hf.Extrapolated) return;
        _harmT[q] = double.NaN;
        Span<float> before = stackalloc float[12], after = stackalloc float[12];
        int pcB = Chroma(ring, tb - 0.25 * p, tb - 0.1 * p, before);
        int pcA = Chroma(ring, tb + 0.65 * p, tAfter, after);
        double dot = 0, na = 0, nb = 0;
        for (int k = 0; k < 12; k++) { dot += before[k] * after[k]; na += before[k] * before[k]; nb += after[k] * after[k]; }
        double chroma = na > 1e-9 && nb > 1e-9 ? 1 - dot / Math.Sqrt(na * nb) : 0;
        double score = (pcB >= 0 && pcA >= 0 && pcB != pcA ? 1 : 0) + HarmChromaShare * chroma;
        int i = (int)(((c.Beat.Index(tb + 0.25 * p) % _harm.Length) + _harm.Length) % _harm.Length);
        _harm[i] = HarmDecay * _harm[i] + score;
    }

    /// <summary>時刻 a と b の 12 音の色を足して dst に入れ、b のベースの音名を返す（分からなければ −1）</summary>
    private int Chroma(FeatureRing ring, double a, double b, Span<float> dst)
    {
        dst.Clear();
        ring.Sample(a, _hf);
        for (int k = 0; k < 12; k++) dst[k] += _hf.Chroma[k];
        ring.Sample(b, _hf);
        for (int k = 0; k < 12; k++) dst[k] += _hf.Chroma[k];
        return _hf.BassPitchClass;
    }

    /// <summary>キックで色を進めるときの、前に色を進めた時刻（ほかのデバイスが共有の色を進めた拍も含める。デバイスごとに数えがずれない）</summary>
    private double LastHitStep(EffectContext c) => Math.Max(_hitStepT, c.Shared?.LastBeatT ?? double.NegativeInfinity);

    /// <summary>拍の番号 n の拍に、拍に乗ったキックがあったか（直近 8 拍）</summary>
    private bool KickOnBeat(long n)
    {
        foreach (long k in _kickBeats) if (k == n) return true;
        return false;
    }

    /// <summary>
    /// キックで色を進める（UsesHitSteps）: 拍に乗ったキックで、前の切り替えから ColorStepBeats × HitStepMul 拍たっていれば進める。
    /// 区切りのキック（しばらくキックがなかったあとの最初・直近より大きい）は、その半分で進めてよい。拍に乗っていないキックでは進めない。
    /// 細かく続くキック（1 拍に 3 つ以上。スネアの連打がキックとして見つかったものが多い）では、直近より大きくなければ進めない
    /// </summary>
    private void HitStep(EffectContext c, AudioEvent ev)
    {
        double gapBeats = c.Period > 0 ? (ev.T - _hitKickT) / c.Period : double.PositiveInfinity;
        if (ev.T > _hitKickT) _hitKickT = ev.T;
        int dense = 1;
        foreach (double k in _kickTs) if (ev.T - k < c.Period - 1e-6 && ev.T - k > 1e-6) dense++;
        _kickTs[_kickTHead] = ev.T; _kickTHead = (_kickTHead + 1) % _kickTs.Length;
        if (!c.Locked) return;
        double ph = c.Beat.Phase(ev.T);
        if (Math.Min(ph, 1 - ph) * c.Period > HitOnBeatSec) return;
        if (ev.T > _onBeatKickT) _onBeatKickT = ev.T;
        long n = c.Beat.Index(ev.T + 0.25 * c.Period);
        if (dense < 3 && !KickOnBeat(n)) { _kickBeats[_kickBeatHead] = n; _kickBeatHead = (_kickBeatHead + 1) % _kickBeats.Length; }
        // 直近 2 小節（HitLouderBeats 拍）の拍に乗ったキックのどれよりも HitLouderDb 以上大きい（スネアと重なる拍だけ少し大きい、では区切りにしない）
        float db = c.LoudDbAt(ev.T), loudest = float.NegativeInfinity;
        for (int i = 0; i < _kickDbs.Length; i++) if (ev.T - _kickDbT[i] <= HitLouderBeats * c.Period) loudest = Math.Max(loudest, _kickDbs[i]);
        bool louder = !float.IsNaN(db) && !float.IsNegativeInfinity(loudest) && db >= loudest + HitLouderDb;
        if (!float.IsNaN(db)) { _kickDbT[_kickDbHead] = ev.T; _kickDbs[_kickDbHead] = db; _kickDbHead = (_kickDbHead + 1) % _kickDbs.Length; }
        // この拍で、キックが来そうだったので拍の頭で切り替えたときは、since が 0 に近いので、もう進めない
        int every = StepEvery(c);
        double since = (ev.T - LastHitStep(c)) / c.Period;
        if (dense >= 3 && !louder) return;
        bool accent = gapBeats > HitGapBeats || louder;
        if (!(OnStepBeat(c, n, since) || (accent && since >= Math.Max(1, every / 2.0) - 0.25))) return;
        _hitStepT = ev.T;
        // 光と同時に一度に切り替える（遅いデバイスは、いつもどおり CrossfadeSec かけて）
        if (c.Shared is { } shared) shared.Step(ev.T, c.Pal.Step, ev.T, SameBeatSec(c));
        else c.Color.Step(ev.T, c.Pal.Step, c.Slow ? CrossfadeSec(c) : 0);
    }

    private static bool IsStepBeat(EffectContext c, long index)
    {
        int every = StepEvery(c);
        long rel = index - c.Beat.DownbeatOffset;
        return ((rel % every) + every) % every == 0;
    }

    /// <summary>
    /// テンポをつかんでいるとき: 次の拍で色を進めるなら、拍の頭で切り替え終わるように、その少し前から切り替え始める
    /// （拍の頭から切り替え始めると、ビートの光のいちばん明るいところが前の色・途中の色になる）
    /// </summary>
    private void PreStep(EffectContext c, double t1)
    {
        if (!PreFade) return;
        bool hit = UsesHitSteps(c);
        if (hit && !HitPredict) return;
        double lead = Math.Min(CrossfadeSec(c), PreFadeMaxBeats * c.Period);
        if (!(lead > 0)) return;   // 一度に切り替える: 拍の頭で切り替えるので、ビートの光はもう新しい色
        var beat = c.Beat;
        double tn = beat.NextBeatAfter(t1);
        // 同じ拍かどうかは半拍で比べる（切り替え始めたあとでテンポの予測が 0.15 秒以上動いても、同じ拍でもう一度切り替えない）
        if (tn - lead > t1 || Math.Abs(tn - _preT) < SameBeatSec(c)) return;
        _preT = tn;
        // 次の拍の番号（拍の頭のすぐ後ろで数える。小数の誤差で前の拍にならないように）
        if (hit ? !HitStepDue(c, tn) : !IsStepBeat(c, beat.Index(tn + 0.25 * beat.Period))) return;
        double start = Math.Max(c.TPrev, tn - lead);
        if (c.Shared is { } shared) shared.Step(start, c.Pal.Step, tn, SameBeatSec(c));
        else c.Color.Step(start, c.Pal.Step, tn - start);
    }

    /// <summary>テンポをつかんでいるとき、同じ拍とみなす時間（半拍。BeatCrossing と同じ。SameStepSec より短くはしない）</summary>
    private static double SameBeatSec(EffectContext c) => HalfBeatSame ? Math.Max(SharedColor.SameStepSec, 0.5 * c.Period) : SharedColor.SameStepSec;

    /// <summary>同じ拍かを半拍で比べる（調べる用。false で 6.15.8 と同じ 0.15 秒。アプリの中では変えない）</summary>
    internal static bool HalfBeatSame { get; set; } = true;

    /// <summary>キック（Auto なら立ち上がりも）が来た</summary>
    private void OnOnset(EffectContext c, AudioEvent ev)
    {
        // 出来事の光（ほぼ同時のキックと立ち上がりは 1 つの音として、強いほうを使う）
        if (ev.Strength >= FlashMinStrength)
        {
            // テンポをつかんでいないときは、直近の大きな音とくらべた音の大きさでも弱める（小さな銃声は弱く。EffectContext.LoudRel）
            float db = c.EventDb(ev.T);
            float a = Math.Clamp(0.5f + 0.25f * (ev.Strength - 1), 0.5f, 1f) * c.RelAt(db);
            if (Math.Abs(ev.T - _onT1) < SameHitSec)
            {
                _onA1 = Math.Max(_onA1, a);
                _fresh1 = true;
            }
            else
            {
                _onT0 = _onT1; _onA0 = _onA1; _onG0 = _onG1; _fresh0 = _fresh1;
                _onT1 = ev.T; _onA1 = a; _onG1 = Limiter.Decide(ev.T, db, c); _fresh1 = true;
            }
        }
        // キックで色を進める（全体の光り方が「ビートで色替え」。テンポをつかんでいるときだけ進める。つかんでいないときは下の合図で）
        if (ev.Kind == AudioEventKind.Kick && ev.Strength >= FlashMinStrength && UsesHitSteps(c)) HitStep(c, ev);
        // テンポをつかんでいなければ、強いキックが合図
        if (!c.Locked && ev.Strength >= TriggerMinStrength && ev.T - _lastTriggerT >= TriggerMinGapSec)
        {
            int every = StepEvery(c);
            bool step = (c.TriggerCount + 1) % every == 0;
            FireTrigger(c, ev.T, ev.Strength, step, onset: true);
        }
    }

    private void FireTrigger(EffectContext c, double t, float strength, bool step, bool onset = false, double sameSec = SharedColor.SameStepSec)
    {
        _lastTriggerT = Math.Max(_lastTriggerT, t);
        c.Trigger = true;
        c.TriggerT = t;
        c.TriggerStrength = strength;
        c.TriggerCount++;
        if (!step) return;
        // キックで進めるとき: 光と同時に一度に切り替える（共有の色は、切り替え終わる時刻 = キックの時刻をほかのデバイスにも渡す。
        // 遅いデバイスは、いつもどおり CrossfadeSec かけて切り替える）
        if (c.Shared is { } shared) shared.Step(t, c.Pal.Step, onset ? t : double.NaN, sameSec);
        else c.Color.Step(t, c.Pal.Step, onset && !c.Slow ? 0 : CrossfadeSec(c));
    }

    /// <summary>色の切り替えにかける時間（秒）</summary>
    public static double CrossfadeSec(EffectContext c)
    {
        if (c.NoFade) return 0;
        double dur = c.Music.CrossfadeMs / 1000;
        return c.Slow ? Math.Max(dur, SlowCrossfadeMinSec) : dur;
    }

    /// <summary>共有の色が進んでいたら、このデバイスもその色へ切り替える（毎コマ、合図の計算のあとに呼ぶ）</summary>
    public static void SyncSharedColor(EffectContext c)
    {
        if (c.Shared is not { } shared) return;
        // 切り替えの始まりは、共有の色が進んだビートの時刻（ただし今のコマより先にはしない）
        double t = Math.Min(Math.Max(shared.LastStepT, c.TPrev), c.T);
        double dur = CrossfadeSec(c);
        double end = shared.FadeEndT;
        if (!double.IsNaN(end))
        {
            // 切り替え終わる時刻が決まっている（拍の前に切り替え始めた・キックで進めた）: その時刻で切り替え終わる。
            // 一度に切り替えるデバイスは、その時刻まで待つ
            if (dur <= 0)
            {
                if (c.T < end) return;
                t = Math.Min(Math.Max(end, c.TPrev), c.T);
            }
            // 遅いデバイスは、いつもの時間（CrossfadeSec）より短くしない（光が長いので、急に変えると目立つ）
            else dur = Math.Max(end - t, c.Slow ? dur : 0);
        }
        c.Color.SyncTo(shared, t, dur);
    }

    /// <summary>色を何回の合図ごとに進めるか（全デバイスで同じにする。違うとデバイスごとに色がずれる）</summary>
    private static int StepEvery(EffectContext c) => Math.Max(1, c.Music.ColorStepBeats);
}

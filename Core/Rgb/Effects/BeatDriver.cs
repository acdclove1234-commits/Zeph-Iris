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

    private double _lastTriggerT = double.NegativeInfinity;
    // 予測したビートの頭をまたいだか（予測が書き換わっても同じビートを 2 回合図にしない）
    private readonly BeatCrossing _crossing = new();
    // 出来事の光は、直近 2 つを覚えておき明るいほうを使う（新しい光を弱めたとき、前の光が急に消えないように）
    private double _onT1 = double.NegativeInfinity, _onT0 = double.NegativeInfinity;
    private float _onA1, _onA0, _onG1 = 1, _onG0 = 1;
    private float _predGain = 1;
    // 拍の前に色を切り替え始めた拍の時刻（その拍の頭では、もう進めない）
    private double _preT = double.NegativeInfinity;
    // このコマで新しく分かった光（検出の遅れで、前のコマの範囲より前の時刻のこともある）
    private bool _fresh1, _fresh0;

    /// <summary>激しい点滅の制限</summary>
    public FlashLimiter Limiter { get; } = new();

    /// <summary>状態を最初に戻す（デバイスを作り直したときなど）</summary>
    public void Reset()
    {
        _lastTriggerT = _onT1 = _onT0 = _preT = double.NegativeInfinity;
        _onA1 = _onA0 = 0;
        _onG1 = _onG0 = _predGain = 1;
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
            bool kickLike = ev.Kind == AudioEventKind.Kick || (auto && ev.Kind == AudioEventKind.Onset);
            if (!kickLike) continue;
            OnOnset(c, ev);
        }
        if (beatPending) OnPredictedBeat(c);
        if (c.Locked && !off) PreStep(c, t1);

        // ---- ビートの光（今の時刻の値と、このコマの間の最大値） ----
        c.Fl = c.FlPeak = 0;
        c.FlPeakT = t1;
        if (off) return;
        double now = FlashAt(c, t1);
        double peak = now, peakT = t1;
        // 光が最大になるのは光り始めの瞬間（予測したビートの頭・キックの時刻）。このコマの間にあったか、
        // このコマで初めて届いた（検出が遅れた）光なら、そこも見る
        if (c.BeatCrossed && c.BeatCrossT > t0) Consider(c, c.BeatCrossT, ref peak, ref peakT);
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

    /// <summary>時刻 t のビートの光（FlashAmp を掛ける前）= 確かさ × 予測の光 + (1 − 確かさ) × 出来事の光</summary>
    public double FlashAt(EffectContext c, double t)
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
            fpred = Math.Exp(-beat.Phase(t) * beat.Period / tauF) * _predGain;
        }
        double tauO = slow ? Math.Max(OnsetFlashTauSec, SlowFlashTauMinSec) : OnsetFlashTauSec;
        double fons = Math.Max(Pulse(t, _onT1, _onA1 * _onG1, tauO), Pulse(t, _onT0, _onA0 * _onG0, tauO));
        return conf * fpred + (1 - conf) * fons;
    }

    /// <summary>
    /// 予測した拍で光らせるか（テンポをつかんでいるときだけ。遅いデバイス用の光り方（c.Slow）は、つかんでいなくても光らせる:
    /// 光も色の切り替えもゆっくりなので、キックの光と色の切り替えが重なると、送った 1 回だけ新しい色で明るく光って見えるため）
    /// </summary>
    private static bool PredictFlash(EffectContext c) => c.HasTempo && c.Conf > 0 && (c.Locked || UnlockedPrediction || c.Slow);

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
        if (PredictFlash(c)) _predGain = Limiter.Decide(tb);
        if (!c.Locked) return;
        // テンポをつかんでいる: 予測したビートが合図。色は小節の頭にそろえて ColorStepBeats 拍ごとに進める
        // （拍の前に切り替え始めた拍では、もう進めない）
        bool step = IsStepBeat(c, c.BeatIndex) && !(Math.Abs(tb - _preT) < SameBeatSec(c));
        FireTrigger(c, tb, 1f, step, sameSec: SameBeatSec(c));
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
        double lead = Math.Min(CrossfadeSec(c), PreFadeMaxBeats * c.Period);
        if (!(lead > 0)) return;   // 一度に切り替える: 拍の頭で切り替えるので、ビートの光はもう新しい色
        var beat = c.Beat;
        double tn = beat.NextBeatAfter(t1);
        // 同じ拍かどうかは半拍で比べる（切り替え始めたあとでテンポの予測が 0.15 秒以上動いても、同じ拍でもう一度切り替えない）
        if (tn - lead > t1 || Math.Abs(tn - _preT) < SameBeatSec(c)) return;
        _preT = tn;
        // 次の拍の番号（拍の頭のすぐ後ろで数える。小数の誤差で前の拍にならないように）
        if (!IsStepBeat(c, beat.Index(tn + 0.25 * beat.Period))) return;
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
            float a = Math.Clamp(0.5f + 0.25f * (ev.Strength - 1), 0.5f, 1f);
            if (Math.Abs(ev.T - _onT1) < SameHitSec)
            {
                _onA1 = Math.Max(_onA1, a);
                _fresh1 = true;
            }
            else
            {
                _onT0 = _onT1; _onA0 = _onA1; _onG0 = _onG1; _fresh0 = _fresh1;
                _onT1 = ev.T; _onA1 = a; _onG1 = Limiter.Decide(ev.T); _fresh1 = true;
            }
        }
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

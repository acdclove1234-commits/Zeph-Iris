// ==================================================
// BeatHueEffect.cs - 光り方 E2「ビートで色替え」 (ビートのたびにパレットの次の色へ切り替わり、ぱっと光って消えていく)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// ビートで色替え: ビートの合図でパレットの次の色へ（OKLab でクロスフェード）。明るさはビートの光・低音・メロディ。
///   I = max(Fl, 0.7 × BassE, メロディの光 × (1 − 0.8 × テンポの確かさ) × (1 − 0.85 × ドラムの鳴っている度合い))
///   メロディの光 = 高音・中音が直近の平均からどれだけ上がったか（× 3）を、すぐ明るく・ゆっくり（0.35 秒）戻る光にしたもの。
///   明るくなるのは音の出だし（キック・スネア・ハイハット・立ち上がり）のすぐあとと、無音から鳴り始めたときだけ（タイヤのきしみ・サイレンのような続く音では明るくならない）。
///   拍をつかんでいる曲・ドラムが鳴っている曲では拍とキックの光が主役、ドラムのない静かな曲（ベルやアルペジオ）ではメロディで光る
///   LED の色 = P(いまの色 + 0.08 × (p − 0.5)) × Ilin(I)   … ゾーンの中で色を少しずらし、のっぺりさせない
/// パレットに「選んだ色（Mono）」を使えば、選んだ色とその両隣の色で光る。
/// 遅いデバイス用（slow）では、ビートの光の消え方・色の切り替えを遅くする（BeatDriver が c.Slow を見て行う）。
/// 話し声が続くときは、テンポをつかめていないキックの光を弱くしていく（TalkFatigue）
/// 遅いデバイス（全体の光り方が「ビートで色替え」・テンポの設定が Off 以外）は、テンポをつかんでいるとき
///   I = 拍の間 + (1 − 拍の間) × 拍の光（FlHit: その拍で鳴った音の強さ倍。キックのない拍は弱い）、拍の間は下と同じ（SlowBeatOnBed）
/// 光り方「ビートで色替え」として使うとき（followBeat。速いデバイス・テンポの設定が Off 以外）は、光るきっかけを拍だけにする:
///   I = 拍の間 + (1 − 拍の間) × 拍の光（BeatFlash: 拍の頭。テンポをつかめないときはキック）
///   拍の間 = max(BodyShare × 低音をゆっくり（0.5 秒）追った値, メロディの光（つかめていないときだけ）, 0.7 × BassE（テンポもドラムもないときだけ）)
///   低音・メロディの立ち上がりでは光り始めない（以前は光の半分以上が 8 分・16 分のベースなど拍の外だった）。
///   拍の間を暗くしすぎると、ほとんど真っ暗で短く点滅するだけに見えるので、BodyShare は拍の光が目立つ範囲で大きくする（0.35）
/// </summary>
public sealed class BeatHueEffect : IMusicEffect
{
    /// <summary>ゾーンの端から端での、パレットの位置のずれ</summary>
    public const float GradientWidth = 0.08f;

    /// <summary>明るさの決め方の値（実際の光り方で合わせるため、テストから変えられるようにしてある。アプリの中では変えない）</summary>
    internal static class Tune
    {
        /// <summary>低音を明るさに使う割合</summary>
        internal static float BassShare = 0.7f;
        /// <summary>テンポをつかんでいるとき、拍の間の低音の光を減らす割合（EffectContext.OffBeatDuck）</summary>
        internal static float OffBeatDuck = 0.25f;
        /// <summary>メロディ（高音・中音の立ち上がり）の光を明るさに使う割合（0 = 使わない）・中音の重み・立ち上がりを何倍にするか</summary>
        internal static float MelodyShare = 1f, MelodyMidWeight = 0.7f, MelodyGain = 3f;
        /// <summary>立ち上がりを測る基準（直近の平均）の時定数（秒）</summary>
        internal static float MelodyRefSec = 1.5f;
        /// <summary>メロディの光が明るくなる・戻る速さ（秒。すぐ明るくなって、ゆっくり戻る = 1 音ずつちかちかしない）</summary>
        internal static float MelodyAttackSec = 0.03f, MelodyReleaseSec = 0.35f;
        /// <summary>
        /// メロディの光が明るくなってよいのは、音の出だし（キック・スネア・ハイハット・立ち上がり）の MelodyHitLeadSec 秒前から MelodyHitSec 秒後まで。
        /// タイヤのきしみ・サイレンのような、出だしのない続く音では明るくならない（戻り方は今までどおり）
        /// </summary>
        internal static double MelodyHitLeadSec = 0.05, MelodyHitSec = 0.15;
        /// <summary>
        /// 無音（MelodyQuietDb より小さい音）から MelodyStartDb より大きい音が鳴り始めたときも、音の出だしとみなす（dBFS。曲の最初の音では、出だしの合図がまだ出ないため。
        /// 部屋の小さな音では鳴り始めとみなさない）
        /// </summary>
        internal static float MelodyQuietDb = -60f, MelodyStartDb = -40f;
        /// <summary>拍がはっきりしているほど（テンポの確かさが高いほど）メロディの光を控えめにする割合（拍の光を主役にする）</summary>
        internal static float MelodyDuck = 0.8f;
        /// <summary>
        /// ドラム（キック・スネア）が鳴っているほどメロディの光を控えめにする割合と、「鳴っている」とみなすキック・スネアの数（直近 DrumWindowSec 秒の 1 秒あたり）。
        /// テンポをつかめていない区間でも、ギター・スネア・リードの立ち上がりで光りっぱなしにならないように（0 = しない）
        /// </summary>
        internal static float DrumDuck = 0.85f, DrumFullRate = 1.5f;
        internal static double DrumWindowSec = 2.0;
        /// <summary>
        /// 拍に乗せる光り方（followBeat）の、拍の間の明るさ（低音をゆっくり追う。立ち上がり BodyUpSec・戻り BodyDownSec 秒）の割合。
        /// ゆっくり追うので、8 分・16 分のベースで光り始めたようには見えない
        /// </summary>
        internal static float BodyShare = 0.35f;
        internal static double BodyUpSec = 0.5, BodyDownSec = 0.5;
        /// <summary>ドラムのないところ（BeatFlash.Drumless）で、今までどおり低音でも光らせるようになるまでの時定数（秒）</summary>
        internal static double DrumlessSec = 0.5;
        /// <summary>
        /// 遅いデバイス（メモリなど）も、拍の間は低音をゆっくり追う明るさにして、拍の光をその上に足す（全体の光り方が「ビートで色替え」のとき。
        /// false = 6.15.10 まで: 低音の明るさが拍の光より明るく、メモリでは拍がほとんど見えなかった。調べる用）
        /// </summary>
        internal static bool SlowBeatOnBed = true;
        /// <summary>遅いデバイスで、テンポをつかんだ・外したときに、光り方を混ぜて切り替える時定数（秒）</summary>
        internal static double SlowBlendSec = 0.5;
    }

    /// <summary>メロディの光を進める刻み（秒。分析の 1 回 ≈ 10.7 ミリ秒に合わせる）</summary>
    private const double MelodyStepSec = 0.010;
    /// <summary>1 コマで進める刻みの数の上限（長く止まっていたとき）</summary>
    private const int MelodyMaxSteps = 50;

    private float _i, _mel, _trRef, _midRef, _body, _drumless;
    private double _hitT = double.NegativeInfinity;   // 直近の音の出だしの時刻（メロディの光が明るくなってよいか）
    private float _toneBalance, _toneOffset;
    private float _slowW;   // 遅いデバイス: テンポをつかんでいるときの光り方の割合（0〜1。SlowBeatOnBed）
    private bool _wasQuiet = true;                     // 無音だった（次に大きな音が鳴り始めたら、音の出だしとみなす）
    private SteadyLevel _bedSteady;   // 低音で光る明るさを、ずっと続く音（エンジン・風）の小さな上下で揺らさない
    // 拍に乗せる光（followBeat のときだけ）
    private readonly BeatFlash? _beat;
    private readonly AudioFrame _sample = new();
    // 直近のキック・スネアの時刻（ドラムが鳴っているか）
    private readonly double[] _drumT = new double[32];
    private int _drumHead, _drumCount;
    // 話し声が続くときの、キックの光の弱め方（拍に乗せないとき）と、ひとつ前のキックの音量・はっきり大きい音の時刻
    private readonly TalkFatigue _talk = new();
    private float _kickDb = float.NaN;
    private double _louderT = double.NegativeInfinity;

    /// <param name="slow">遅いデバイス用（ビートの光の消え方・色の切り替えを遅くする）</param>
    /// <param name="followBeat">
    /// 光り方「ビートで色替え」として使う: 光るのは拍の頭だけ（テンポをつかめないときはキック。BeatFlash）。
    /// 低音の大きさでは光らせない（拍の間は、低音をゆっくり追う明るさだけ）。テンポを使わない設定（Off）では今までどおり。
    /// 遅いデバイス用は false（今までどおり）
    /// </param>
    public BeatHueEffect(bool slow = false, bool followBeat = false)
    {
        IsSlow = slow;
        if (followBeat && !slow) _beat = new BeatFlash();
    }

    /// <summary>拍に乗せる光り方か（テスト用）</summary>
    public bool FollowsBeat => _beat is not null;

    /// <summary>拍に乗せる光（テスト用。拍に乗せないときは null）</summary>
    internal BeatFlash? Flash => _beat;

    public bool IsSlow { get; }
    private double _slowKickT = double.NegativeInfinity;

    public void Reset(EffectContext c)
    {
        _i = _mel = _trRef = _midRef = _body = _drumless = 0;
        _hitT = double.NegativeInfinity;
        _toneBalance = _toneOffset = 0;
        _slowW = 0;
        _slowKickT = double.NegativeInfinity;
        _wasQuiet = true;
        _bedSteady.Reset();
        _drumHead = _drumCount = 0;
        _talk.Reset();
        _kickDb = float.NaN;
        _louderT = double.NegativeInfinity;
        _beat?.Reset();
    }

    public void Update(EffectContext c)
    {
        var f = c.Frame;
        UpdateMelody(c);
        // 音域のバランスで基調色を動かす。ドラムが休む間もフレーズの変化が色に残る。
        if ((_beat is not null || c.Music.Effect == MusicStyles.BeatHue) && c.Music.TempoMode == TempoModes.Auto)
        {
            float balance = Math.Clamp(Math.Max(f.MidE, f.TrebleE) - f.BassE, -1f, 1f);
            _toneBalance = EffectContext.Follow(_toneBalance, balance, c.Dt, 0.22, 0.35);
            // 色を一度に送る機器では、音色の途中経過を毎コマ書き込まない。
            if (!c.NoFade || c.Trigger) _toneOffset = _toneBalance;
        }
        else _toneBalance = _toneOffset = 0;
        // 拍をつかんでいる曲では拍の光が主役、つかめていない静かな曲（ベル・アルペジオ）ではメロディの光が主役。
        // ドラムが鳴っている曲では、テンポをつかめていなくても、キック・スネアの光を主役にする
        float melShare = Tune.MelodyShare * (1 - Tune.MelodyDuck * c.Conf) * (1 - Tune.DrumDuck * DrumPresence(c));
        // テンポをつかんでいないときは、直近の大きな音とくらべた音の大きさでも弱める（小さな銃声の光が明るく残り、すぐあとの爆発が目立たなかった）
        melShare *= c.RelAt(c.HasTempo && c.Locked ? float.NaN : f.LoudDb);
        if (_beat is { } bf && c.Music.TempoMode != TempoModes.Off)
        {
            // 拍に乗せる: 光るのは拍の頭（つかめないときはキック）だけ。拍の間は低音をゆっくり追う明るさ。
            // メロディの光は、テンポをつかめていないとき（ドラムのない静かな曲）だけ
            bf.Update(c);
            _body = EffectContext.Follow(_body, f.BassE, c.Dt, Tune.BodyUpSec, Tune.BodyDownSec);
            float mel = c.HasTempo && c.Locked ? 0 : melShare * _mel;
            // 拍の光は、拍の間の明るさの上に足す（拍の間が明るいところでも、拍で必ず明るくなる）
            // テンポもドラムもないところ（ドラムのない曲など）では、今までどおり低音でも光る（拍もキックもないので）
            _drumless = EffectContext.Follow(_drumless, bf.Drumless ? 1 : 0, c.Dt, Tune.DrumlessSec, Tune.DrumlessSec);
            float bed = Math.Max(Math.Max(Tune.BodyShare * _body, mel), _drumless * Tune.BassShare * _bedSteady.Step(c, f.BassE, SteadyLevel.BandDead));
            _i = bed + (1 - bed) * bf.Value;
            return;
        }
        // 拍の間の低音（8 分・16 分のベース・キック）は控えめにして、拍の光を目立たせる
        // ずっと続く音の小さな上下をならすのは速いデバイスだけ（遅いデバイスの版は、もともと光がゆっくりで揺れが見えない）
        float bass = IsSlow ? f.BassE : _bedSteady.Step(c, f.BassE, SteadyLevel.BandDead);
        // 話し声が続くときは、テンポをつかめていないキックの光を弱くする（TalkFatigue。はっきり大きい音のすぐあとは弱めない）
        float talk = TalkScale(c);
        _i = Math.Max(Math.Max(c.Fl * talk, Tune.BassShare * c.OffBeatDuck(Tune.OffBeatDuck) * bass), melShare * _mel);
        if (IsSlow && Tune.SlowBeatOnBed && c.Music.TempoMode != TempoModes.Off && c.Music.Effect == MusicStyles.BeatHue)
        {
            // 遅いデバイスでテンポをつかんでいる: 拍の間は低音をゆっくり追う明るさ、拍の光（その拍で鳴った音の強さ倍。FlHit）をその上に足す
            // （以前は 0.7 × 低音が拍の光と同じくらい明るく、メモリでは拍が見えなかった）。
            // つかめていないとき（映画・ゲームの音など）は今までどおり。切り替わるときは SlowBlendSec 秒かけて混ぜる（明るさが跳ねない）
            bool locked = c.HasTempo && c.Locked;
            if (!locked || _slowKickT > c.T) _slowKickT = double.NegativeInfinity;
            foreach (var ev in c.Events)
                if (locked && ev.Kind == AudioEventKind.Kick && ev.Strength >= BeatFlash.MinStrength && ev.T <= c.T)
                    _slowKickT = Math.Max(_slowKickT, ev.T);
            _slowW = EffectContext.Follow(_slowW, locked ? 1 : 0, c.Dt, Tune.SlowBlendSec, Tune.SlowBlendSec);
            _body = EffectContext.Follow(_body, f.BassE, c.Dt, Tune.BodyUpSec, Tune.BodyDownSec);
            if (_slowW <= 1e-3) return;
            float amp = (float)Math.Max(1e-3, c.Music.FlashAmp);
            float level = BeatFlash.Tune.LevelBase + BeatFlash.Tune.LevelAmp * Math.Min(1f, amp);
            float flash = Math.Min(1f, level * c.FlHit / amp) * talk;
            // 揺れる拍でも、実際に届いたキックは遅いコマまで残す。
            flash = Math.Max(flash, level * EffectContext.Decay((float)(c.T - _slowKickT), 0.12));
            float bed = Tune.BodyShare * _body;
            float pulse = bed + (1 - bed) * flash;
            _i = _slowW * pulse + (1 - _slowW) * _i;
        }
    }

    /// <summary>
    /// 拍に乗せないとき（遅いデバイスなど）の、ビートの光の倍率: 話し声が続くときは弱くする（TalkFatigue）。
    /// ひとつ前のキックより FlashLimiter.RaiseDb 以上大きい音のあと LouderHoldSec 秒は弱めない
    /// </summary>
    private float TalkScale(EffectContext c)
    {
        bool locked = c.HasTempo && c.Locked;
        _talk.Update(c, locked);
        bool auto = c.Music.TempoMode == TempoModes.Auto;
        foreach (var ev in c.Events)
        {
            bool kickLike = ev.Kind == AudioEventKind.Kick || (auto && ev.Kind == AudioEventKind.Onset);
            if (!kickLike || ev.Strength < BeatDriver.FlashMinStrength) continue;
            float db = c.EventDb(ev.T);
            if (float.IsNaN(db)) continue;
            if (float.IsNaN(_kickDb) || db >= _kickDb + FlashLimiter.RaiseDb) _louderT = Math.Max(_louderT, ev.T);
            _kickDb = db;
        }
        if (c.T - _louderT < LouderHoldSec) return 1f;
        return _talk.Scale(c.T, locked);
    }

    /// <summary>はっきり大きい音のあと、話し声の疲れで弱めない秒数</summary>
    public const double LouderHoldSec = 0.5;

    /// <summary>ドラムが鳴っている度合い（0〜1）: 直近 DrumWindowSec 秒のキック・スネアの数（1 秒あたり DrumFullRate で 1）</summary>
    private float DrumPresence(EffectContext c)
    {
        foreach (var ev in c.Events)
        {
            if (ev.Kind is not (AudioEventKind.Kick or AudioEventKind.Snare)) continue;
            _drumT[_drumHead] = ev.T;
            _drumHead = (_drumHead + 1) % _drumT.Length;
            if (_drumCount < _drumT.Length) _drumCount++;
        }
        if (Tune.DrumDuck <= 0 || _drumCount == 0) return 0;
        double from = c.T - Tune.DrumWindowSec;
        int n = 0;
        for (int i = 0; i < _drumCount; i++) if (_drumT[i] >= from && _drumT[i] <= c.T + 0.5) n++;
        return Math.Clamp((float)(n / Tune.DrumWindowSec / Tune.DrumFullRate), 0, 1);
    }

    /// <summary>
    /// メロディの光: 高音・中音が「直近の平均」からどれだけ上がったか。ハイハットが鳴り続ける曲では平均も高いので
    /// 明るさの底上げにならず、静かなところから鳴るベルやアルペジオの音だけがふくらむように光る。
    /// すぐ明るく・ゆっくり戻る光なので、コマの時刻の値だけで進めると、コマ数の少ないデバイスで音の山を取りこぼす。
    /// 前のコマから今のコマまでを約 10 ミリ秒ずつ、分析結果の記録を読んで進める（記録がなければ今のコマの値で 1 回）
    /// </summary>
    private void UpdateMelody(EffectContext c)
    {
        double span = Math.Max(0, c.T - c.TPrev);
        var ring = c.Features;
        int n = ring is null || span <= 0 ? 1 : Math.Clamp((int)Math.Ceiling(span / MelodyStepSec), 1, MelodyMaxSteps);
        float dt = n == 1 ? Math.Max(0, c.Dt) : (float)(span / n);
        foreach (var ev in c.Events)
            if (ev.Kind is AudioEventKind.Kick or AudioEventKind.Snare or AudioEventKind.Hat or AudioEventKind.Onset) _hitT = Math.Max(_hitT, ev.T);
        // 無音から鳴り始めた音も、音の出だし（曲の最初の音）
        float ld = c.Frame.RmsDb;
        if (ld < Tune.MelodyQuietDb) _wasQuiet = true;
        else if (_wasQuiet && ld > Tune.MelodyStartDb) { _wasQuiet = false; _hitT = Math.Max(_hitT, c.T); }
        for (int k = 1; k <= n; k++)
        {
            double tk = n == 1 ? c.T : c.TPrev + span * k / n;
            var f = n == 1 || !ring!.Sample(tk, _sample) ? c.Frame : _sample;
            float kRef = 1 - MathF.Exp(-dt / Tune.MelodyRefSec);
            _trRef += (f.TrebleE - _trRef) * kRef;
            _midRef += (f.MidE - _midRef) * kRef;
            float rise = Math.Max(f.TrebleE - _trRef, Tune.MelodyMidWeight * (f.MidE - _midRef));
            float target = Math.Clamp(Tune.MelodyGain * rise, 0, 1);
            // 明るくなるのは音の出だしのすぐあとだけ（きしみ・サイレン・高さの変わる続く音では明るくならない）
            if (target > _mel && (tk < _hitT - Tune.MelodyHitLeadSec || tk > _hitT + Tune.MelodyHitSec)) target = _mel;
            float tau = target > _mel ? Tune.MelodyAttackSec : Tune.MelodyReleaseSec;
            float a = tau > 0 ? 1 - MathF.Exp(-dt / tau) : 1;
            _mel += (target - _mel) * a;
        }
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        float k = c.Ilin(_i);
        double offset = 0;
        offset = _toneOffset * c.Pal.Step * (c.Music.ReduceFlashing ? 0.45 : 0.75);
        for (int j = s.Start; j < s.Start + s.Count; j++)
            dst[j] = c.ColorAt(offset + GradientWidth * (c.P[j] - 0.5f)) * k;
    }
}

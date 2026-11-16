// ==================================================
// PulseEffect.cs - 光り方 E1「パルス」 (色はゆっくり移り変わり、低音に合わせてふくらむように脈打つ)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// パルス: デバイス全体が同じ色で、低音とビートの光に合わせて明るくなり、残光を残して暗くなる。
///   I = max(低音 BassE, ビートの光 Fl)
///   G = max(I, 前のコマの G × exp(−dt/τg))   … 残光（τg = 0.25 秒。遅いデバイスは 0.45 秒）
///   色 = いまの色（ビートで進む）× Ilin(G)
/// 光り方「パルス」として使うとき（steadyColor）は、「ビートで色替え」と見分けがつくように:
///   色はビートで切り替えず、時刻でゆっくり移り変わる（パレットの 1 色ぶんに ColorHoldSec 秒。全デバイスで同じ色）
///   残光を長くする（τg = SwellGlowSec 秒。遅いデバイスは SlowSwellGlowSec 秒）。光り始めは拍に遅れないよう、すぐ明るくする
///   速いデバイス（テンポの設定が Off 以外）では、光り始めを「ビートで色替え」と同じ拍の頭だけにする（BeatFlash。つかめないときはキック）:
///     G = 拍の間 + (1 − 拍の間) × min(1, FlashGain × 拍の光の残光)（残光 τg。テンポをつかんでいれば GlowMaxBeats 拍まで）
///     拍の間 = max(BodyShare × 低音をゆっくり（0.5 秒）追った値, SwellShare × 低音のふくらみ, 今までの低音の光（テンポもドラムもないときだけ）)
///     低音のふくらみ = 低音に SwellUpSec 秒かけて明るくなり、τg で戻る（8 分のベースでも、ぱっと光り始めたようには見えない、息をするような明るさ）。
///     拍の間を暗くしすぎると、拍の間がほとんど真っ暗で、短く点滅するだけに見えた（以前の半分以下の明るさ）
/// 「激しい」では、小節の頭のビートで白へ 0.4 × Fl だけ寄せる（OKLab で混ぜる）。
/// 音量メーター・流れる波・波紋の「小さいゾーン」用の代わりとしても使う（音量で光る・色を外から決める。色はビートで進む）
/// </summary>
public sealed class PulseEffect : IMusicEffect
{
    /// <summary>光り方「パルス」で、パレットの 1 色ぶん進むのにかける秒数</summary>
    public const double ColorHoldSec = 8.0;
    /// <summary>光り方「パルス」の残光の時定数（秒）</summary>
    public const double SwellGlowSec = 0.40;
    public const double SlowSwellGlowSec = 0.55;
    /// <summary>何に合わせて明るくするか</summary>
    public enum Source
    {
        /// <summary>低音（キックを含む）</summary>
        Bass,
        /// <summary>音量（聞こえ方に合わせた VU）</summary>
        Loudness,
    }

    /// <summary>残光の時定数（秒）</summary>
    public const double GlowTauSec = 0.25;
    /// <summary>更新の遅いデバイスでの残光の時定数（秒）</summary>
    public const double SlowGlowTauSec = 0.45;
    /// <summary>更新の遅いデバイスで、明るくなるのを遅らせる時定数（秒）。一瞬だけの光は GPU では一部しか書き換わらない</summary>
    public const double SlowAttackSec = 0.04;
    /// <summary>更新の遅いデバイスで音量に合わせるとき、暗くなるのをゆっくりにする時定数（秒）</summary>
    public const double SlowLoudReleaseSec = 0.40;
    /// <summary>小節の頭で白へ寄せる割合（× Fl）</summary>
    public const float DownbeatWhiteMix = 0.4f;

    private readonly bool _slow;
    private readonly bool _steady;
    private readonly Source _source;
    private float _g;        // 残光
    private float _att;      // 立ち上がりを遅らせた強さ（遅いデバイス）
    private LinColor _color; // このコマの色
    private float _body;     // 拍の間の明るさ（拍に乗せるとき。低音をゆっくり追う）
    private float _gf;       // 拍の光の残光（拍に乗せるとき）
    private float _gs, _drumless; // ドラムのないところの低音の光（残光つき）と、その混ぜ具合（拍に乗せるとき）
    private float _sw;            // 低音のふくらみ（拍に乗せるとき。ゆっくり明るくなる）
    private readonly AudioFrame _sample = new();
    /// <summary>拍の間の明るさを進める刻み（秒。分析の 1 回 ≈ 10.7 ミリ秒に合わせる）と、1 コマで進める刻みの数の上限</summary>
    private const double BedStepSec = 0.010;
    private const int BedMaxSteps = 50;
    // 拍に乗せる光（光り方「パルス」の速いデバイス用だけ）
    private readonly BeatFlash? _beat;

    /// <summary>拍に乗せるときの明るさの値（実際の曲で合わせるため、テストから変えられるようにしてある。アプリの中では変えない）</summary>
    internal static class Tune
    {
        /// <summary>拍の間の明るさ（低音をゆっくり追う）の割合と、追う速さ（秒）</summary>
        internal static float PulseBodyShare = 0.4f;
        internal static double PulseBodyUpSec = 0.5, PulseBodyDownSec = 0.5;
        /// <summary>低音のふくらみの割合（0 = 使わない）と、明るくなるのにかける時定数（秒。戻りは残光の τg）</summary>
        internal static float PulseSwellShare = 0.55f;
        internal static double PulseSwellUpSec = 0.12;
        /// <summary>拍の光の残光に掛ける倍率（上限 1）</summary>
        internal static float PulseFlashGain = 1.4f;
        /// <summary>テンポをつかんでいれば、残光の時定数を 1 拍のこの倍までにする（次の拍の前に暗くなって、拍ごとの脈が見える）</summary>
        internal static double PulseGlowMaxBeats = 1.2;
        /// <summary>ドラムのないところ（BeatFlash.Drumless）で、今までどおり低音で光らせるようになるまでの時定数（秒）</summary>
        internal static double PulseDrumlessSec = 0.5;
    }

    /// <param name="steadyColor">
    /// 光り方「パルス」として使う（色を時刻でゆっくり変え、ふくらむように光る）。小さいゾーン用の代わりは false。
    /// 遅いデバイス用でなければ、光り始めは「ビートで色替え」と同じ拍の頭だけ（テンポをつかめないときはキック。BeatFlash）にして、
    /// 低音の大きさでは光らせない（テンポを使わない設定 Off では今までどおり低音で光る）
    /// </param>
    public PulseEffect(bool slow = false, Source source = Source.Bass, bool steadyColor = false)
    {
        _slow = slow;
        _source = source;
        _steady = steadyColor;
        if (steadyColor && !slow && source == Source.Bass) _beat = new BeatFlash();
    }

    /// <summary>拍に乗せる光り方か（テスト用）</summary>
    public bool FollowsBeat => _beat is not null;

    /// <summary>拍に乗せる光（テスト用。拍に乗せないときは null）</summary>
    internal BeatFlash? Flash => _beat;

    /// <summary>光り方「パルス」として使っている（色がビートで切り替わらない）</summary>
    public bool SteadyColor => _steady;

    public string Name => MusicStyles.Pulse;

    /// <summary>色を外から決める（流れる波の小さいゾーン用。null ならいまの色）。毎コマ Update の前に入れる</summary>
    public LinColor? ColorOverride { get; set; }

    /// <summary>このコマの明るさ（残光を含む 0〜1。テスト用）</summary>
    public float Level => _g;

    public void Reset(EffectContext c)
    {
        _g = 0;
        _att = 0;
        _body = _gf = _gs = _drumless = _sw = 0;
        _beat?.Reset();
    }

    public void Update(EffectContext c)
    {
        var f = c.Frame;
        double tau = _steady ? (_slow ? SlowSwellGlowSec : SwellGlowSec) : _slow ? SlowGlowTauSec : GlowTauSec;
        double swellTau = tau;
        float flash = c.Fl;
        if (_beat is { } bf && c.Music.TempoMode != TempoModes.Off)
        {
            // 拍に乗せる: 光り始めは拍の頭（つかめないときはキック）だけ。拍の間は低音をゆっくり追う明るさ
            bf.Update(c);
            if (c.HasTempo && c.Locked) tau = Math.Min(tau, Tune.PulseGlowMaxBeats * c.Period);
            float bPeak = bf.Peak * (float)Math.Exp(-Math.Max(0, c.T - bf.PeakT) / tau);
            // 拍の光は、拍の間の明るさの上に足す（拍の間が明るいところでも、拍で必ず明るくなる）
            // 残光は拍の光の分だけにつける（拍の間の明るさまで減らすと、1 秒間のコマ数で明るさが変わる）
            _gf = Math.Max(Math.Max(bf.Value, bPeak), _gf * EffectContext.Decay(c.Dt, tau));
            // テンポもドラムもないところ（ドラムのない曲など）では、今までどおり低音でふくらむ（拍もキックもないので）
            _drumless = EffectContext.Follow(_drumless, bf.Drumless ? 1 : 0, c.Dt, Tune.PulseDrumlessSec, Tune.PulseDrumlessSec);
            float bass = BassLevel(c, f, swellTau);
            _gs = Math.Max(bass, _gs * EffectContext.Decay(c.Dt, swellTau));
            UpdateBed(c, swellTau);
            float bed = Math.Max(Math.Max(Tune.PulseBodyShare * _body, Tune.PulseSwellShare * _sw), _drumless * _gs);
            _g = bed + (1 - bed) * Math.Min(1f, Tune.PulseFlashGain * _gf);
            // 小節の頭で白へ寄せる分も残光に合わせて減らす（拍の光だけで減らすと、明るさが残ったまま色だけが先に戻り、一瞬白く光って見える）
            flash = _gf;
        }
        else Swell(c, f, tau);

        if (ColorOverride is LinColor over) { _color = over; return; }
        bool accent = c.Music.DownbeatAccent && c.Downbeat && flash > 0;
        if (_steady)
        {
            // 色はビートで切り替えず、時刻でゆっくり移り変わる（どのデバイスも同じ時刻なので同じ色）
            double pos = c.T / ColorHoldSec * c.Pal.Step;
            _color = accent ? OkLab.ToLed(Lab.Lerp(c.PalLabAt(pos), Lab.White, DownbeatWhiteMix * flash), c.Gamma) : c.PalAt(pos);
        }
        else if (accent) _color = OkLab.ToLed(Lab.Lerp(c.ColorLabAt(0), Lab.White, DownbeatWhiteMix * c.Fl), c.Gamma);
        else _color = c.ColorAt(0);
    }

    /// <summary>
    /// 拍の間の明るさ（拍に乗せるとき）: 低音をゆっくり追う値と、低音のふくらみ（ゆっくり明るくなり、残光と同じ速さ tauDown で戻る。
    /// 拍の間も暗くなりすぎず、息をするように明るさが動く）。コマの時刻の値だけで進めると、コマ数の少ないデバイスでは
    /// 低音の山を 1 コマの間ずっと追い続けて明るくなりすぎるので、前のコマから今のコマまでを約 10 ミリ秒ずつ、分析結果の記録を読んで進める
    /// （記録がなければ今のコマの値で 1 回）
    /// </summary>
    private void UpdateBed(EffectContext c, double tauDown)
    {
        double span = Math.Max(0, c.T - c.TPrev);
        var ring = c.Features;
        int n = ring is null || span <= 0 ? 1 : Math.Clamp((int)Math.Ceiling(span / BedStepSec), 1, BedMaxSteps);
        float dt = n == 1 ? Math.Max(0, c.Dt) : (float)(span / n);
        for (int k = 1; k <= n; k++)
        {
            var f = n == 1 || !ring!.Sample(c.TPrev + span * k / n, _sample) ? c.Frame : _sample;
            _body = EffectContext.Follow(_body, f.BassE, dt, Tune.PulseBodyUpSec, Tune.PulseBodyDownSec);
            _sw = EffectContext.Follow(_sw, f.BassE, dt, Tune.PulseSwellUpSec, tauDown);
        }
    }

    /// <summary>今までの明るさ: 低音（または音量）とビートの光の大きいほうに、残光をつける</summary>
    private void Swell(EffectContext c, AudioFrame f, double tau)
    {
        float x = BassLevel(c, f, tau);
        // ビートの光も、コマの間の最大値（ビートの瞬間）から経過時間だけ減らした値を使う
        float flPeak = c.FlPeak * (float)Math.Exp(-Math.Max(0, c.T - c.FlPeakT) / tau);
        float i = Math.Max(x, Math.Max(c.Fl, flPeak));
        _g = Math.Max(i, _g * EffectContext.Decay(c.Dt, tau));
    }

    /// <summary>低音（または音量）の今の値と、コマの間のピークの大きいほう（遅いデバイスは立ち上がりを遅らせる）</summary>
    private float BassLevel(EffectContext c, AudioFrame f, double tau)
    {
        float now, peak;
        // 今の値は、本当の時刻（分析結果の時刻）からの経過時間だけ減らしておく（古い値を何度も拾い直さない）
        float age = (float)Math.Exp(-(c.AgeT - c.FrameTime) / tau);
        if (_source == Source.Bass)
        {
            now = f.BassE * age;
            // コマの間にあったピークを、ピークからの経過時間だけ減らして使う（1 秒に 8 回でもキックを取りこぼさない）
            peak = c.Peak.BassE * (float)Math.Exp(-Math.Max(0, c.AgeT - c.BassPeakT) / tau);
        }
        else
        {
            now = f.LoudVU * age;
            peak = c.Peak.LoudVU * (float)Math.Exp(-Math.Max(0, c.AgeT - c.LoudPeakT) / tau);
        }
        float x = Math.Max(now, peak);
        if (_slow)
        {
            _att = EffectContext.Follow(_att, x, c.Dt, SlowAttackSec, _source == Source.Loudness ? SlowLoudReleaseSec : 0);
            x = _att;
        }
        return x;
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        var col = _color * c.Ilin(_g);
        for (int j = s.Start; j < s.Start + s.Count; j++) dst[j] = col;
    }
}

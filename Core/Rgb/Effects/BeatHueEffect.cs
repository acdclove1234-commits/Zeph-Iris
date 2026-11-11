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
///   拍をつかんでいる曲・ドラムが鳴っている曲では拍とキックの光が主役、ドラムのない静かな曲（ベルやアルペジオ）ではメロディで光る
///   LED の色 = P(いまの色 + 0.08 × (p − 0.5)) × Ilin(I)   … ゾーンの中で色を少しずらし、のっぺりさせない
/// パレットに「選んだ色（Mono）」を使えば、選んだ色とその両隣の色で光る。
/// 遅いデバイス用（slow）では、ビートの光の消え方・色の切り替えを遅くする（BeatDriver が c.Slow を見て行う）
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
        /// <summary>拍がはっきりしているほど（テンポの確かさが高いほど）メロディの光を控えめにする割合（拍の光を主役にする）</summary>
        internal static float MelodyDuck = 0.8f;
        /// <summary>
        /// ドラム（キック・スネア）が鳴っているほどメロディの光を控えめにする割合と、「鳴っている」とみなすキック・スネアの数（直近 DrumWindowSec 秒の 1 秒あたり）。
        /// テンポをつかめていない区間でも、ギター・スネア・リードの立ち上がりで光りっぱなしにならないように（0 = しない）
        /// </summary>
        internal static float DrumDuck = 0.85f, DrumFullRate = 1.5f;
        internal static double DrumWindowSec = 2.0;
    }

    /// <summary>メロディの光を進める刻み（秒。分析の 1 回 ≈ 10.7 ミリ秒に合わせる）</summary>
    private const double MelodyStepSec = 0.010;
    /// <summary>1 コマで進める刻みの数の上限（長く止まっていたとき）</summary>
    private const int MelodyMaxSteps = 50;

    private float _i, _mel, _trRef, _midRef;
    private readonly AudioFrame _sample = new();
    // 直近のキック・スネアの時刻（ドラムが鳴っているか）
    private readonly double[] _drumT = new double[32];
    private int _drumHead, _drumCount;

    public BeatHueEffect(bool slow = false) => IsSlow = slow;

    public bool IsSlow { get; }
    public string Name => MusicStyles.BeatHue;

    public void Reset(EffectContext c)
    {
        _i = _mel = _trRef = _midRef = 0;
        _drumHead = _drumCount = 0;
    }

    public void Update(EffectContext c)
    {
        var f = c.Frame;
        UpdateMelody(c);
        // 拍をつかんでいる曲では拍の光が主役、つかめていない静かな曲（ベル・アルペジオ）ではメロディの光が主役。
        // ドラムが鳴っている曲では、テンポをつかめていなくても、キック・スネアの光を主役にする
        float melShare = Tune.MelodyShare * (1 - Tune.MelodyDuck * c.Conf) * (1 - Tune.DrumDuck * DrumPresence(c));
        // 拍の間の低音（8 分・16 分のベース・キック）は控えめにして、拍の光を目立たせる
        _i = Math.Max(Math.Max(c.Fl, Tune.BassShare * c.OffBeatDuck(Tune.OffBeatDuck) * f.BassE), melShare * _mel);
    }

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
        for (int k = 1; k <= n; k++)
        {
            var f = n == 1 || !ring!.Sample(c.TPrev + span * k / n, _sample) ? c.Frame : _sample;
            float kRef = 1 - MathF.Exp(-dt / Tune.MelodyRefSec);
            _trRef += (f.TrebleE - _trRef) * kRef;
            _midRef += (f.MidE - _midRef) * kRef;
            float rise = Math.Max(f.TrebleE - _trRef, Tune.MelodyMidWeight * (f.MidE - _midRef));
            float target = Math.Clamp(Tune.MelodyGain * rise, 0, 1);
            float tau = target > _mel ? Tune.MelodyAttackSec : Tune.MelodyReleaseSec;
            float a = tau > 0 ? 1 - MathF.Exp(-dt / tau) : 1;
            _mel += (target - _mel) * a;
        }
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        float k = c.Ilin(_i);
        for (int j = s.Start; j < s.Start + s.Count; j++)
            dst[j] = c.ColorAt(GradientWidth * (c.P[j] - 0.5f)) * k;
    }
}

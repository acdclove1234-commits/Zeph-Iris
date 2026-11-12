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

    /// <param name="steadyColor">光り方「パルス」として使う（色を時刻でゆっくり変え、ふくらむように光る）。小さいゾーン用の代わりは false</param>
    public PulseEffect(bool slow = false, Source source = Source.Bass, bool steadyColor = false)
    {
        _slow = slow;
        _source = source;
        _steady = steadyColor;
    }

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
    }

    public void Update(EffectContext c)
    {
        var f = c.Frame;
        double tau = _steady ? (_slow ? SlowSwellGlowSec : SwellGlowSec) : _slow ? SlowGlowTauSec : GlowTauSec;
        float now, peak;
        // 今の値は、本当の時刻（分析結果の時刻）からの経過時間だけ減らしておく（古い値を何度も拾い直さない）
        float age = (float)Math.Exp(-(c.T - c.FrameTime) / tau);
        if (_source == Source.Bass)
        {
            now = f.BassE * age;
            // コマの間にあったピークを、ピークからの経過時間だけ減らして使う（1 秒に 8 回でもキックを取りこぼさない）
            peak = c.Peak.BassE * (float)Math.Exp(-Math.Max(0, c.T - c.BassPeakT) / tau);
        }
        else
        {
            now = f.LoudVU * age;
            peak = c.Peak.LoudVU * (float)Math.Exp(-Math.Max(0, c.T - c.LoudPeakT) / tau);
        }
        float x = Math.Max(now, peak);
        if (_slow)
        {
            _att = EffectContext.Follow(_att, x, c.Dt, SlowAttackSec, _source == Source.Loudness ? SlowLoudReleaseSec : 0);
            x = _att;
        }
        // ビートの光も、コマの間の最大値（ビートの瞬間）から経過時間だけ減らした値を使う
        float flPeak = c.FlPeak * (float)Math.Exp(-Math.Max(0, c.T - c.FlPeakT) / tau);
        float i = Math.Max(x, Math.Max(c.Fl, flPeak));
        _g = Math.Max(i, _g * EffectContext.Decay(c.Dt, tau));

        if (ColorOverride is LinColor over) { _color = over; return; }
        bool accent = c.Music.DownbeatAccent && c.Downbeat && c.Fl > 0;
        if (_steady)
        {
            // 色はビートで切り替えず、時刻でゆっくり移り変わる（どのデバイスも同じ時刻なので同じ色）
            double pos = c.T / ColorHoldSec * c.Pal.Step;
            _color = accent ? OkLab.ToLed(Lab.Lerp(c.Pal.LabAt(pos), Lab.White, DownbeatWhiteMix * c.Fl), c.Gamma) : c.PalAt(pos);
        }
        else if (accent) _color = OkLab.ToLed(Lab.Lerp(c.ColorLabAt(0), Lab.White, DownbeatWhiteMix * c.Fl), c.Gamma);
        else _color = c.ColorAt(0);
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        var col = _color * c.Ilin(_g);
        for (int j = s.Start; j < s.Start + s.Count; j++) dst[j] = col;
    }
}

// ==================================================
// PulseEffect.cs - パルス (デバイス全体が 1 色で、低音または音量に合わせてふくらむように光り、残光を残して暗くなる。
//                  音量メーターの小さいゾーン用と、遅いデバイス用の音量メーターの代わり)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// パルス: デバイス全体が同じ色で、低音（または音量）とビートの光に合わせて明るくなり、残光を残して暗くなる。
///   I = max(低音 BassE, ビートの光 Fl)
///   G = max(I, 前のコマの G × exp(−dt/τg))   … 残光（τg = 0.25 秒。遅いデバイスは 0.45 秒）
///   色 = いまの色（ビートで進む）× Ilin(G)
/// 「激しい」では、小節の頭のビートで白へ 0.4 × Fl だけ寄せる（OKLab で混ぜる）。
/// 選べる光り方ではなく、音量メーターの「小さいゾーン」用の代わりと、
/// 更新の遅いデバイスでの音量メーターの代わり（音量で光る）として使う（色はビートで進む）
/// </summary>
public sealed class PulseEffect : IMusicEffect
{
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
    private readonly Source _source;
    private float _g;        // 残光
    private float _att;      // 立ち上がりを遅らせた強さ（遅いデバイス）
    private LinColor _color; // このコマの色
    private SteadyLevel _steady; // ずっと続く音の小さな上下をならす

    /// <param name="slow">更新の遅いデバイス用（立ち上がりを遅らせ、暗くなるのをゆっくりにする）。音量に合わせるとき（PulseLoudSlow）だけ使う</param>
    public PulseEffect(bool slow = false, Source source = Source.Bass)
    {
        if (slow && source != Source.Loudness) throw new ArgumentException("the slow pulse follows the loudness only (PulseLoudSlow)", nameof(slow));
        _slow = slow;
        _source = source;
    }

    /// <summary>このコマの明るさ（残光を含む 0〜1。テスト用）</summary>
    public float Level => _g;

    public void Reset(EffectContext c)
    {
        _g = 0;
        _att = 0;
        _steady.Reset();
    }

    public void Update(EffectContext c)
    {
        Swell(c, c.Frame, _slow ? SlowGlowTauSec : GlowTauSec);

        bool accent = c.Music.DownbeatAccent && c.Downbeat && c.Fl > 0;
        _color = accent ? OkLab.ToLed(Lab.Lerp(c.ColorLabAt(0), Lab.White, DownbeatWhiteMix * c.Fl), c.Gamma) : c.ColorAt(0);
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
        // エンジン・風・雨のようなずっと続く音では、小さな上下で光が揺れないようにする（打つ音がしばらくないときだけ。
        // 遅いデバイスの版は、もともと立ち上がり・暗くなるのがゆっくりなので使わない）
        float x = _slow ? Math.Max(now, peak) : _steady.Step(c, Math.Max(now, peak), _source == Source.Loudness ? SteadyLevel.LoudDead : SteadyLevel.BandDead);
        if (_slow)
        {
            _att = EffectContext.Follow(_att, x, c.Dt, SlowAttackSec, SlowLoudReleaseSec);   // 遅い版は音量に合わせるときだけ（コンストラクター）
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

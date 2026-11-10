// ==================================================
// RoleEffect.cs - 光り方「パーツごとの役割」 (ゾーンごとに、低音・中音・高音・音全体のどれかに反応して光る)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// パーツごとの役割: ゾーンの設定「反応する音」（LedSegment.Role）ごとに、決まった音だけに反応する。
///   低音: キックのたびにぱっと光り（τ 0.22 秒で消える）、ベースの強さで明るさが残る
///   中音: スネアのたびに光り（τ 0.18 秒）、ボーカルなどの中音の強さで明るさが残る
///   高音: ハイハットのたびに白っぽく短く光り（τ 0.09 秒）、高音の強さで明るさが残る
///   音全体: 音量で明るさが変わる（点滅しない）
/// 色は役割ごとにパレットの位置をずらす（低音 0・中音 1/3・高音 2/3）。ビートで色が進むときも、役割の違いは保たれる。
/// パレット「選んだ色だけ」なら、すべて同じ色で明るさだけが変わる。
/// 遅いデバイス（slow）は、光の消え方を 1.8 倍ゆっくりにする（一瞬の光を取りこぼさないように）
/// </summary>
public sealed class RoleEffect : IMusicEffect
{
    public const double BassTauSec = 0.22;
    public const double MidTauSec = 0.18;
    public const double TrebleTauSec = 0.07;
    public const double AllTauSec = 0.30;
    /// <summary>遅いデバイスで消え方に掛ける倍率</summary>
    public const double SlowFactor = 1.8;
    /// <summary>ハイハットの光で白へ寄せる割合（× 光の強さ）</summary>
    public const float HatWhiteMix = 0.35f;

    /// <summary>音が鳴っている間の下地の明るさと、音の強さで足す幅（どのパーツも真っ暗にならないように）</summary>
    public const float BodyLevel = 0.12f;
    public const float BodyRange = 0.30f;
    /// <summary>役割ごとの強さの山が下がっていく時定数（秒）と、山の下限（ほとんど鳴っていない音を拡大しすぎない）</summary>
    public const double PeakReleaseSec = 4.0;
    public const float PeakFloor = 0.12f;
    /// <summary>出来事の光を満点にする多さ（1 秒あたり）。これより多く鳴る音は √(RefRate / 多さ) 倍に弱める（下限 MinFlashAmp）</summary>
    public const float RefRate = 2f;
    public const float MinFlashAmp = 0.75f;
    /// <summary>低音以外のパーツにも分けるキックの光の割合</summary>
    public const float BeatShare = 0.45f;
    /// <summary>出来事の多さをならす割合（1 回ごと）</summary>
    private const float RateFollow = 0.15f;

    private const int Roles = 4;
    private readonly float[] _peak = new float[Roles];
    private readonly float[] _rate = new float[Roles];
    private readonly double[] _lastHitT = new double[Roles];
    private readonly bool _slow;
    private readonly double[] _hitT = new double[Roles];
    private readonly float[] _hitS = new float[Roles];
    private readonly float[] _glow = new float[Roles];
    private readonly float[] _flash = new float[Roles];
    private readonly LinColor[] _color = new LinColor[Roles];

    public RoleEffect(bool slow = false) => _slow = slow;

    public string Name => MusicStyles.Role;

    /// <summary>役割ごとの今の明るさ（0〜1。テスト用）</summary>
    public float LevelOf(MusicRole r) => _glow[(int)r];

    public void Reset(EffectContext c)
    {
        Array.Fill(_hitT, double.NegativeInfinity);
        Array.Clear(_hitS);
        Array.Clear(_glow);
        Array.Clear(_flash);
        Array.Fill(_peak, PeakFloor);
        Array.Fill(_rate, RefRate);
        Array.Fill(_lastHitT, double.NegativeInfinity);
    }

    private double Tau(MusicRole r)
    {
        double t = r switch { MusicRole.Bass => BassTauSec, MusicRole.Mid => MidTauSec, MusicRole.Treble => TrebleTauSec, _ => AllTauSec };
        return _slow ? t * SlowFactor : t;
    }

    public void Update(EffectContext c)
    {
        // 役割ごとの出来事（キック・スネア・ハイハット）。光った本当の時刻を覚える
        var ev = c.Events;
        for (int i = 0; i < ev.Count; i++)
        {
            var e = ev[i];
            int r = e.Kind switch
            {
                AudioEventKind.Kick => (int)MusicRole.Bass,
                AudioEventKind.Snare => (int)MusicRole.Mid,
                AudioEventKind.Hat => (int)MusicRole.Treble,
                _ => -1,
            };
            if (r < 0) continue;
            float s = Math.Clamp(0.55f + 0.25f * e.Strength, 0, 1);
            if (e.Kind == AudioEventKind.Hat)
            {
                // ハイハットは詰めて光らせない（MusicTuning.HatResponse の間隔）。明るさは下げない
                // （この光り方では、高音のパーツはハイハットで光るのが役割なので、暗くすると低音・中音と釣り合わなくなる）
                var (_, minGap) = MusicTuning.HatResponse(c.Music);
                if (e.T - _hitT[r] < minGap && e.T >= _hitT[r]) continue;
            }
            _hitT[r] = e.T;
            _hitS[r] = s;
            // 出来事の多さ（1 秒あたり）: 間隔から求めて、ならしておく
            double gap = e.T - _lastHitT[r];
            if (gap > 0.02 && gap < 4) _rate[r] += (1 / (float)gap - _rate[r]) * RateFollow;
            _lastHitT[r] = e.T;
        }

        var f = c.Frame;
        bool active = f.Activity > 0.3f;
        // キックの光（どのパーツにも BeatShare の割合で分ける。役割が分かれすぎず、全体が拍に合わせて動くように）
        int bi = (int)MusicRole.Bass;
        float kick = _hitS[bi] * (float)Math.Exp(-Math.Max(0, c.T - _hitT[bi]) / Tau(MusicRole.Bass));
        // ビートの光（テンポをつかんでいれば予測した拍でも光る。キックを検出しそこねた拍でも光るように）。
        // コマの間の最大値から経過時間だけ減らした値も見る（1 秒間のコマ数が少ないデバイスでも拍を取りこぼさない）
        float beatFl = Math.Max(c.Fl, c.FlPeak * (float)Math.Exp(-Math.Max(0, c.T - c.FlPeakT) / Tau(MusicRole.Bass)));
        kick = Math.Max(kick, beatFl);
        for (int r = 0; r < Roles; r++)
        {
            var role = (MusicRole)r;
            double tau = Tau(role);
            // 今の分析結果は、本当の時刻からの経過時間だけ減らす（古い値を何度も拾い直さない）
            float age = (float)Math.Exp(-(c.T - c.FrameTime) / tau);
            float raw = role switch
            {
                MusicRole.Bass => f.BassE,
                MusicRole.Mid => f.MidE,
                MusicRole.Treble => f.TrebleE,
                _ => f.LoudVU,
            } * age;
            // 役割ごとに、最近の強さの山で割ってそろえる（中音はふつう低音より弱いので、そのままだと中音のパーツだけ暗い）
            _peak[r] = Math.Max(raw, Math.Max(PeakFloor, _peak[r] * EffectContext.Decay(c.Dt, PeakReleaseSec)));
            float level = Math.Clamp(raw / _peak[r], 0, 1);
            // 出来事の光: 多く鳴る音（ハイハットは 1 秒に 8 回など）ほど弱くする（どのパーツも同じくらいの目立ち方にする）
            float amp = Math.Clamp(MathF.Sqrt(RefRate / Math.Max(_rate[r], 0.25f)), MinFlashAmp, 1f);
            float own = role == MusicRole.All ? 0f : amp * _hitS[r] * (float)Math.Exp(-Math.Max(0, c.T - _hitT[r]) / tau);
            float hit = role == MusicRole.Bass ? Math.Max(own, kick) : Math.Max(own, BeatShare * kick);
            _flash[r] = hit;
            // 音が鳴っている間は、どのパーツも下地の明るさ（Body）＋音の強さで光り、出来事でさらに光る（音が止まって門が閉じたら下地は消える）
            float body = active ? BodyLevel + BodyRange * level : 0.6f * level;
            // 出来事の光は下地に重ねる（下地より弱い光でも、光ったことが分かるように）
            float x = body + hit * (1 - body);
            _glow[r] = Math.Max(x, _glow[r] * EffectContext.Decay(c.Dt, tau));
            double off = role switch { MusicRole.Bass => 0, MusicRole.Mid => 1 / 3.0, MusicRole.Treble => 2 / 3.0, _ => 0.5 };
            _color[r] = role == MusicRole.Treble && own > 0.05f
                ? OkLab.ToLed(Lab.Lerp(c.ColorLabAt(off), Lab.White, HatWhiteMix * own), c.Gamma)
                : c.ColorAt(off);
        }
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        int r = (int)s.Role;
        var col = _color[r] * c.Ilin(_glow[r]);
        for (int j = s.Start; j < s.Start + s.Count; j++) dst[j] = col;
    }
}

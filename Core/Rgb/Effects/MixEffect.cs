// ==================================================
// MixEffect.cs - ミックス (低音・中音・高音の強さで色が混ざる全体で 1 色。スペクトラムの小さいゾーン用と、遅いデバイス用の代わり)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// ミックス: デバイス全体が 1 色。低音 = パレットの最初の色、中音 = 真ん中の色、高音 = 最後の色として、
/// 強い帯の色に寄せる（重み w = e⁴ / Σe⁴ で、いちばん強い帯がはっきり勝つ）。
///   重みは、上がるとき WeightRiseSec・下がるとき WeightFallSec の時定数でならす（1〜2 コマだけ別の帯が勝って、
///   色が一瞬だけ飛んで戻る＝色替えの途中でちかっと光るのを防ぐ）
///   色 = OKLab の (L, a, b) を重みで平均（色相の角度の平均だと、離れた 2 色の間で、どちらでもない鮮やかな色を通って
///   一瞬だけ違う色が光っていた）。鮮やかさだけは、重みで平均した鮮やかさ × √(まとまり具合) に戻す（灰色にしすぎない）
///   I = max(e_低, 弾み, (1 − SideDim × L) × max(e_中, e_高), Fl)（L = テンポをつかんでいる度合い 0〜1）
///   弾み = キックの弾み（KickBounce。τ KickTauSec）× 盛り上がり（SectionGain(SectionMin)）
///   スペクトラムの小さいゾーン用（zone）で、更新の速いデバイスでは I = max(e_低, 弾み, (1 − SideDim × L) × max(高音の分, 中音の分), Fl)。
///   中音の分 = max(3 つの帯の平均, L × e_中, 打つ音がしばらくない度合い × ゆっくり追った e_中)
///   高音の分 = max((TrebleShare + (1 − TrebleShare) × L) × e_高, ゆっくり追った e_高)
///   （話し声の中音や「パ・タ」の短い高音では強く光りすぎない。ストリングス・ライザー・雨のような続く音は、ゆっくり明るくなる）
///   遅いデバイス用（slow）では I = max((1 − SlowDim × L) × max(e_低, e_中, e_高), 弾み（τ SlowKickTauSec）, Fl)
///   （帯だけだと、ハイハット・クラップ・裏拍のベースで明るさが決まり、キックがいちばん明るい瞬間にならず、光が遅れて見えた。
///   キックの検出は 2〜8 ミリ秒で届く。映画・ゲームの効果音ではテンポをつかまないので、帯の明るさは今までどおり）
/// 直近の大きな音よりずっと小さい音（GateFromDb より下）では、帯の小さな上下で光らせない（部屋の音・雑音のゆらぎ）。
///   ただし部屋の音（いちばん小さい音をゆっくり追う）より FloorFromDb 以上大きい音は、今までどおり光る
///   （大きな音のあとの静かな曲・続く効果音。部屋の音だけのときは静かなまま）。
///   速いデバイスでは、ずっと続く音のならし（SteadyLevel）の幅を CalmDead まで広げる。遅いデバイスでは、帯を CalmSec でならす。
///   明るさを一定にするグラボでは、色もほとんど動かさない。テンポをつかんでいる曲では今までどおり
/// 音がまったくない（Σe⁴ ≈ 0）ときは、前の色のままにする（虹色に飛ばない）。
/// 遅いデバイス用（slow）では、各帯が暗くなるのを 0.3 秒かけてゆっくりにする。
/// 選べる光り方ではなく、スペクトラムの「小さいゾーン」用の代わりと、更新の遅いデバイスでのスペクトラムの代わりとして使う
/// </summary>
public sealed class MixEffect : IMusicEffect
{
    /// <summary>遅いデバイスで、暗くなるのをゆっくりにする時定数（秒）</summary>
    public const double SlowReleaseSec = 0.30;
    /// <summary>帯の重みをならす時定数（秒。上がるとき・下がるとき）</summary>
    public const double WeightRiseSec = 0.06, WeightFallSec = 0.15;
    /// <summary>小さいゾーン用: テンポをつかんだ・はなした度合いを追う時定数（秒。つかんだとき・はなしたとき）</summary>
    public const double LockRiseSec = 0.3, LockFallSec = 1.0;
    /// <summary>小さいゾーン用: 続く音を見るために、中音・高音をゆっくり追う時定数（秒）</summary>
    public const double SteadySec = 0.3;
    /// <summary>小さいゾーン用: テンポをつかんでいないとき、短い高音で光らせる割合</summary>
    public const float TrebleShare = 0.85f;
    /// <summary>直近の大きな音（ProgramPeakDb）より GateFromDb 小さいと静かにし始め、さらに GateSpanDb 小さいと完全に静かにする（dB）</summary>
    public const float GateFromDb = 18f, GateSpanDb = 8f;
    /// <summary>静かにするとき、速いデバイスで広げるならす幅</summary>
    public const float CalmDead = 0.6f;
    /// <summary>静かにするとき、遅いデバイスで帯をならす時定数（秒）</summary>
    public const double CalmSec = 0.4;
    /// <summary>部屋の音の大きさ（いちばん小さい音をゆっくり追う）より FloorFromDb 大きいと静かにするのをやめ始め、さらに FloorSpanDb 大きいと完全にやめる（dB）</summary>
    public const float FloorFromDb = 10f, FloorSpanDb = 8f;
    /// <summary>部屋の音の大きさが下がるときの時定数（秒）と、上がる速さ（dB / 秒）</summary>
    public const double FloorFallSec = 0.1;
    public const float FloorRiseDbPerSec = 2f;
    /// <summary>キックの弾みが戻る時定数（秒。速いデバイス・遅いデバイス）</summary>
    public const double KickTauSec = 0.12, SlowKickTauSec = 0.30;
    /// <summary>キックの弾みにかける盛り上がりの倍率の下限</summary>
    public const float SectionMin = 0.5f;
    /// <summary>テンポをつかんでいる曲で、中音・高音を弱める割合（速いデバイス）と、帯をまとめて弱める割合（遅いデバイス）</summary>
    public const float SideDim = 0.2f, SlowDim = 0.4f;

    private readonly bool _slow;
    private readonly KickBounce _kick = new();
    private readonly bool _zone;
    private float _eB, _eM, _eT;
    private float _wB, _wM, _wT;       // ならした帯の重み
    private bool _hasW;
    private float _l, _c, _h;          // いまの色（OKLCh）
    private bool _hasColor;
    private ColorPalette? _bandPal;    // 帯の色を求めたパレット
    private ColorPalette? _bandFrom, _bandTo;   // 帯の色を求めたときの場面の色（PaletteView）
    private float _bandU;
    private readonly Lab[] _band = new Lab[3];
    private LinColor _color;
    private float _i;
    private SteadyLevel _sB, _sM, _sT;   // ずっと続く音の小さな上下をならす（速いデバイスだけ）
    private float _cB, _cM, _cT;         // 静かにするときの、ならした帯（遅いデバイスだけ）
    private bool _hasCalm;
    private float _lock;                 // テンポをつかんでいる度合い（0〜1。小さいゾーン用）
    private float _midSlow, _trebleSlow; // ゆっくり追った中音・高音（小さいゾーン用）
    private float _floor;                // 部屋の音の大きさ（dBFS。いちばん小さい音をゆっくり追う）
    private bool _hasFloor;

    /// <param name="slow">遅いデバイス用（帯が暗くなるのをゆっくりにする）</param>
    /// <param name="zone">スペクトラムの小さいゾーン用（中音だけは、話し声で光りすぎないようにする）</param>
    public MixEffect(bool slow = false, bool zone = false)
    {
        _slow = slow;
        _zone = zone;
    }

    /// <summary>重みをならし、OKLab で混ぜる（調べる用に、テストから前と比べるために変えられる。アプリの中では変えない。false なら 6.15.8 までの色相の平均）</summary>
    internal static bool Smooth { get; set; } = true;

    public void Reset(EffectContext c)
    {
        _eB = _eM = _eT = 0;
        _wB = _wM = _wT = 0;
        _hasW = false;
        _sB.Reset();
        _sM.Reset();
        _sT.Reset();
        _cB = _cM = _cT = 0;
        _hasCalm = false;
        _lock = _midSlow = _trebleSlow = 0;
        _kick.Reset();
        _floor = 0;
        _hasFloor = false;
        _hasColor = false;
        _bandPal = _bandFrom = _bandTo = null;
    }

    public void Update(EffectContext c)
    {
        var f = c.Frame;
        // 大きな音のすぐあとの、ずっと小さい音（部屋の音・雑音）では、帯の小さな上下で光らせない（1 = 今までどおり、0 = 静かにする）。
        // ただし部屋の音よりはっきり大きい音（大きな音のあとの静かな曲・次の効果音）では、静かにしない
        float g = Math.Max(Gate(c), FloorGate(c));
        if (_slow)
        {
            float xB = Calm(ref _cB, f.BassE, g, c.Dt), xM = Calm(ref _cM, f.MidE, g, c.Dt), xT = Calm(ref _cT, f.TrebleE, g, c.Dt);
            _hasCalm = true;
            _eB = EffectContext.Follow(_eB, xB, c.Dt, 0, SlowReleaseSec);
            _eM = EffectContext.Follow(_eM, xM, c.Dt, 0, SlowReleaseSec);
            _eT = EffectContext.Follow(_eT, xT, c.Dt, 0, SlowReleaseSec);
        }
        else
        {
            // エンジン・風・雨のようなずっと続く音では、小さな上下で光が揺れないようにする（打つ音がしばらくないとき。
            // 静かにするときは、打つ音があっても幅を広げる）
            float calm = CalmDead * (1 - g);
            _eB = _sB.Step(c, f.BassE, SteadyLevel.BandDead, calm);
            _eM = _sM.Step(c, f.MidE, SteadyLevel.BandDead, calm);
            _eT = _sT.Step(c, f.TrebleE, SteadyLevel.BandDead, calm);
        }

        var view = c.View;
        if (!ReferenceEquals(_bandPal, c.Pal) || !ReferenceEquals(_bandFrom, view.From) || !ReferenceEquals(_bandTo, view.To) || _bandU != view.U)
        {
            // 帯の色（パレットの最初・真ん中・最後）は、パレット・楽器の AI の場面の色（移り変わりの途中なら毎コマ）が変わったときだけ求め直す
            _bandPal = c.Pal;
            _bandFrom = view.From;
            _bandTo = view.To;
            _bandU = view.U;
            double span = c.Pal.Span;
            _band[0] = view.LabAt(0);
            _band[1] = view.LabAt(span * 0.5);
            _band[2] = view.LabAt(span);
            if (!_hasColor) { SetColor(_band[0]); }
        }

        float wB = Pow4(_eB), wM = Pow4(_eM), wT = Pow4(_eT);
        float sum = wB + wM + wT;
        if (sum > 1e-8f)
        {
            wB /= sum; wM /= sum; wT /= sum;
            if (!Smooth)
            {
                float sin = wB * MathF.Sin(_band[0].Hue) + wM * MathF.Sin(_band[1].Hue) + wT * MathF.Sin(_band[2].Hue);
                float cos = wB * MathF.Cos(_band[0].Hue) + wM * MathF.Cos(_band[1].Hue) + wT * MathF.Cos(_band[2].Hue);
                _h = MathF.Atan2(sin, cos);
                _l = wB * _band[0].L + wM * _band[1].L + wT * _band[2].L;
                _c = wB * _band[0].Chroma + wM * _band[1].Chroma + wT * _band[2].Chroma;
                _hasColor = true;
                _hasW = false;
            }
            else if (!_hasW) { _wB = wB; _wM = wM; _wT = wT; _hasW = true; }
            else
            {
                // 映画の静かな場面（音の手ごたえ c.Presence が小さい）では、色をほとんど動かさない（小さな音で色が変わり続けない。
                // 明るさを一定にするグラボでは、色の変化だけが見える）
                // そのグラボでは、静かにするとき（Gate）も色を止める（部屋の音のゆらぎで色が変わらない）
                float wdt = c.Dt * Math.Clamp(c.Presence, 0f, 1f) * (c.FixedBrightness ? g : 1f);
                _wB = EffectContext.Follow(_wB, wB, wdt, WeightRiseSec, WeightFallSec);
                _wM = EffectContext.Follow(_wM, wM, wdt, WeightRiseSec, WeightFallSec);
                _wT = EffectContext.Follow(_wT, wT, wdt, WeightRiseSec, WeightFallSec);
            }
        }
        float ws = _wB + _wM + _wT;
        if (_hasW && ws > 1e-6f)
        {
            float iw = 1f / ws;
            float b0 = _wB * iw, b1 = _wM * iw, b2 = _wT * iw;
            float l = b0 * _band[0].L + b1 * _band[1].L + b2 * _band[2].L;
            float a = b0 * _band[0].A + b1 * _band[1].A + b2 * _band[2].A;
            float bb = b0 * _band[0].B + b1 * _band[1].B + b2 * _band[2].B;
            float meanC = b0 * _band[0].Chroma + b1 * _band[1].Chroma + b2 * _band[2].Chroma;
            float res = MathF.Sqrt(a * a + bb * bb);
            _l = l;
            if (res > 1e-5f)
            {
                _h = MathF.Atan2(bb, a);
                _c = meanC * MathF.Sqrt(Math.Clamp(res / Math.Max(meanC, 1e-6f), 0f, 1f));
            }
            else _c = 0;
            _hasColor = true;
        }
        _color = OkLab.ToLed(OkLab.FitGamut(Lab.FromLch(_l, _c, _h)), c.Gamma);

        // キックの弾み（届いたキックの本当の時刻から。キックのたびに、すぐ明るくなる）
        _kick.Take(c);
        float bounce = _kick.At(c.T, _slow ? SlowKickTauSec : KickTauSec) * c.SectionGain(SectionMin);
        _lock = EffectContext.Follow(_lock, c.Locked ? 1f : 0f, c.Dt, LockRiseSec, LockFallSec);
        if (_slow)
        {
            // 遅いデバイス: テンポをつかんでいる曲では帯をまとめて弱め、キックで明るくする（中音・高音でいつも明るく、キックが見えなかった）
            float dim = 1 - SlowDim * _lock;
            _i = Math.Max(Math.Max(Math.Max(dim * _eB, bounce), dim * Math.Max(_eM, _eT)), c.Fl);
        }
        else if (_zone && !c.SlowDevice)
        {
            // 小さいゾーン用（更新の速いデバイスだけ）: 低音はそのまま光る。中音（話し声の帯）は 3 つの帯の平均までにする。
            // ただし、テンポをつかんでいる曲では中音もそのまま（6.15.10 と同じ）。打つ音のない続く音では、ゆっくり追った中音まで光る
            _midSlow = EffectContext.Follow(_midSlow, _eM, c.Dt, SteadySec, SteadySec);
            _trebleSlow = EffectContext.Follow(_trebleSlow, _eT, c.Dt, SteadySec, SteadySec);
            float mid = Math.Max((_eB + _eM + _eT) / 3f, Math.Max(_lock * _eM, _sM.Quiet * Math.Min(_midSlow, _eM)));
            // 高音: 短い音（話し声の「パ・タ」）は TrebleShare まで。続く高音（雨・ライザー）と、テンポをつかんでいる曲ではそのまま
            float treble = Math.Max((TrebleShare + (1 - TrebleShare) * _lock) * _eT, Math.Min(_trebleSlow, _eT));
            // テンポをつかんでいる曲では、中音・高音を少し弱める（キックがいちばん明るい瞬間になる）
            float side = (1 - SideDim * _lock) * Math.Max(mid, treble);
            _i = Math.Max(Math.Max(Math.Max(_eB, bounce), side), c.Fl);
        }
        else
        {
            float side = (1 - SideDim * _lock) * Math.Max(_eM, _eT);
            _i = Math.Max(Math.Max(Math.Max(_eB, bounce), side), c.Fl);
        }
    }

    /// <summary>
    /// 静かにする度合い（1 = 今までどおり、0 = 静かにする）。直近の大きな音（ProgramPeakDb）といまの音（RmsDb。新しい音の
    /// 最初のコマですぐ上がる）の差が GateFromDb より大きいと下がり、さらに GateSpanDb で 0。テンポをつかんでいる曲では 1
    /// </summary>
    private static float Gate(EffectContext c)
    {
        if (c.HasTempo && c.Locked) return 1f;
        var f = c.Frame;
        float pk = f.ProgramPeakDb;
        if (!(pk > -100)) return 1f;
        return Math.Clamp(1f - (pk - f.RmsDb - GateFromDb) / GateSpanDb, 0f, 1f);
    }

    /// <summary>
    /// 部屋の音とくらべた、静かにしない度合い（1 = 静かにしない）。部屋の音の大きさ = RmsDb のいちばん小さいところ
    /// （下がるときは FloorFallSec ですぐ、上がるときは FloorRiseDbPerSec でゆっくり追う）。いまの音がそれより FloorFromDb 大きいと上がり、
    /// さらに FloorSpanDb で 1（大きな音のあとでも、部屋の音よりはっきり大きい音なら、帯の動きで光る）
    /// </summary>
    private float FloorGate(EffectContext c)
    {
        float r = c.Frame.RmsDb;
        if (!(r > -200)) return 0f;
        if (!_hasFloor) { _floor = r; _hasFloor = true; }
        else if (r < _floor) _floor = r + (_floor - r) * EffectContext.Decay(c.Dt, FloorFallSec);
        else _floor = Math.Min(r, _floor + FloorRiseDbPerSec * (float)c.Dt);
        return Math.Clamp((r - _floor - FloorFromDb) / FloorSpanDb, 0f, 1f);
    }

    /// <summary>遅いデバイスで静かにするとき: 帯を CalmSec でならした値へ、度合い g で寄せる（g = 1 ならそのまま）</summary>
    private float Calm(ref float body, float x, float g, float dt)
    {
        body = _hasCalm ? EffectContext.Follow(body, x, dt, CalmSec, CalmSec) : x;
        return body + (x - body) * g;
    }

    private void SetColor(Lab lab)
    {
        _l = lab.L;
        _c = lab.Chroma;
        _h = lab.Hue;
        _hasColor = true;
    }

    private static float Pow4(float e)
    {
        float x = Math.Clamp(e, 0f, 1f);
        x *= x;
        return x * x;
    }

    public void Render(EffectContext c, in LedSegment s, LinColor[] dst)
    {
        var col = _color * c.Ilin(_i);
        for (int j = s.Start; j < s.Start + s.Count; j++) dst[j] = col;
    }
}

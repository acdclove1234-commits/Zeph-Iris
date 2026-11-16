// ==================================================
// LedSteady.cs - 音楽連動の LED ごとのちらつき止め (明るくなる・暗くなるの向きが、短い間に何度も戻らないようにする)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 音楽連動で、LED ごとの明るさ（いちばん強い成分）の「向き」（明るくなっている・暗くなっている）を見て、
/// 短い間に向きが何度も戻る動き（上がって下がってまた上がる = ちらつき）を止める。直すのは明るさだけ（保つ分は下の Hold で足す）。
///   1. 向きを変えるのは、前の値から HystCode 段 + 今の明るさの HystRel 倍より大きく動いたときだけ（小さな上下は無視して今の値を保つ）
///   2. 向きを変えてから MinFlipSec の間は、また向きを変えない（光った直後にすぐ暗くし始めない）。
///      ただし 1 コマで大きく明るくなったとき（新しいキック・ハイハット。BigRise）は待たずに明るくする（光り始めが遅れないように）。
///      待たせた小さな上がりは、待ち終わってから SoftRiseTauSec で少しずつ明るくする（保っていた分を 1 コマで跳ばない）
///   3. 暗くなる速さに上限をつける（時定数 ReleaseTauSec = 80 ミリ秒。1 から 0.1 まで 180 ミリ秒以上かける）。
///      1 コマで消える光は、続けて光る光と区別できず、点滅に見えるため。2 で保ったあとに 30 ミリ秒で一気に暗くすると
///      （6.15.8 まで）、光が四角く切れて、光った直後にもう一度ちかっと光ったように見えていた。
///      光が LED の上を動く光り方と、色が大きく変わったとき（前の色を早く消す）は MovingReleaseTauSec = 30 ミリ秒
/// 同じ向きに動くときはそのまま通すので、ゆっくり明るくなる・暗くなる光は段にならない。
/// 明るさを保つ（今の値より明るく出す）ときは、今の色を明るくするのではなく、足りない分を前に出した光の色で足す（Hold）
///   出す光 = 今の光 + 前に出した光の色 × (保つ明るさ − 今の明るさ)。色が大きく変わったときは、輝度が前に出した光と今の光の明るいほうを超えない
/// 色が同じなら「今の色を保つ明るさで出す」のと同じ。色が変わったとき（明るい青 → 暗い緑の下地など）に、
/// 新しい色が保った明るさで一瞬光る（色替えの途中のちかっとした光）ことがない。
/// 色が大きく変わったとき（いちばん強い成分を 1 にそろえた色の差が ColorJump 以上）は、新しい音とみなして
/// 向きを変えるのを待たない（暗くなる速さの上限だけで、前の色から新しい色へ混ぜる）。
/// ストロボ（白く短く光らせるのが光り方そのもの）には使わない
/// </summary>
public sealed class LedSteady
{
    /// <summary>向きを変えてから、次に向きを変えられるまでの秒数（60 コマ／秒の 4.5 コマ分）</summary>
    public const double MinFlipSec = 0.075;
    /// <summary>暗くなる速さの上限（時定数・秒）</summary>
    public const double ReleaseTauSec = 0.08;
    /// <summary>光が LED の上を動く光り方（Moving）と、色が大きく変わったときの、暗くなる速さの上限（時定数・秒）</summary>
    public const double MovingReleaseTauSec = 0.03;
    /// <summary>向きを変えるのに必要な差（0〜255 の段と、今の明るさに対する割合）</summary>
    public const float HystCode = 2.5f, HystRel = 0.06f;
    /// <summary>MinFlipSec の間でも通す、1 コマで大きく明るくなる変化（0〜255 の段と、前のコマの明るさに対する割合）</summary>
    public const float BigRiseCode = 3f, BigRiseRel = 0.1f;
    /// <summary>待たせた小さな上がりを追いかける時定数（秒）</summary>
    public const double SoftRiseTauSec = 0.04;
    /// <summary>色が変わったとみなす差（いちばん強い成分を 1 にそろえた色の、成分ごとの差の最大）</summary>
    public const float ColorJump = 0.25f;

    /// <summary>LED ごとの直し方</summary>
    public enum Kind : byte
    {
        /// <summary>1〜3 をすべて</summary>
        Full,
        /// <summary>
        /// 光が LED の上を動く光り方（波紋・流れる波）: 2 の「明るくなってから暗くし始めない」を使わない
        /// （通り過ぎた光が 75 ミリ秒残って、輪や波が尾を引くため）。小さな上下の無視・暗くなる速さの上限は使う
        /// </summary>
        Moving,
        /// <summary>直さない</summary>
        Skip,
    }

    /// <summary>
    /// 光り方ごとの直し方。ストロボ（短い点滅が光り方そのもの）と、ムード（点滅せず、曲の雰囲気でゆっくり変わる。
    /// 向きを変えるところで待つと、音の大きさへの付き方が鈍る）は直さない
    /// </summary>
    public static Kind KindOf(EffectVariant v) => !Enabled || v is EffectVariant.Strobe or EffectVariant.Mood ? Kind.Skip
        : v is EffectVariant.Ripple or EffectVariant.Scroll ? Kind.Moving : Kind.Full;

    /// <summary>使う光り方（ストロボ以外）</summary>
    public static bool Applies(EffectVariant v) => KindOf(v) != Kind.Skip;

    /// <summary>使うか（調べる用に、テストから前と比べるために変えられる。アプリの中では変えない）</summary>
    internal static bool Enabled { get; set; } = true;

    private float[] _y = Array.Empty<float>();
    private sbyte[] _dir = Array.Empty<sbyte>();
    private float[] _age = Array.Empty<float>();
    private bool[] _soft = Array.Empty<bool>();
    private float[] _x = Array.Empty<float>();
    private LinColor[] _out = Array.Empty<LinColor>();
    private bool _fresh = true;

    /// <summary>状態を捨てる（次のコマはそのまま通す）</summary>
    public void Reset() => _fresh = true;

    /// <summary>
    /// 光の量（明るさを掛けたあと）を、その場で直す。kinds[i] は LED ごとの直し方（null なら全部 kind）。
    /// Skip の LED は直さず、状態も今の値にそろえる（ゾーンだけストロボにしたときなど）
    /// </summary>
    public void Apply(LinColor[] px, int n, float dt, Kind[]? kinds = null, Kind kind = Kind.Full)
    {
        if (_y.Length != n)
        {
            _y = new float[n];
            _dir = new sbyte[n];
            _age = new float[n];
            _soft = new bool[n];
            _x = new float[n];
            _out = new LinColor[n];
            _fresh = true;
        }
        float hAbs = HystCode / 255f, bigAbs = BigRiseCode / 255f;
        float releaseSlow = EffectContext.Decay(dt, ReleaseTauSec), releaseFast = EffectContext.Decay(dt, MovingReleaseTauSec);
        float softRise = 1 - EffectContext.Decay(dt, SoftRiseTauSec);
        for (int i = 0; i < n; i++)
        {
            var c = px[i];
            float x = Math.Max(c.R, Math.Max(c.G, c.B));
            if (!(x >= 0)) x = 0;
            var k = kinds is not null && i < kinds.Length ? kinds[i] : kind;
            if (_fresh || k == Kind.Skip)
            {
                _y[i] = _x[i] = x;
                _dir[i] = 0;
                _age[i] = float.PositiveInfinity;
                _soft[i] = false;
                _out[i] = c;
                continue;
            }
            float y = _y[i];
            int dir = _dir[i];
            float age = _age[i] + dt;
            float h = hAbs + HystRel * y;
            bool soft = _soft[i];
            float xPrev = _x[i];
            _x[i] = x;
            if (x > y)
            {
                if (x - xPrev > bigAbs + BigRiseRel * xPrev)
                {
                    // 1 コマで大きく明るくなった（新しい音）: すぐ明るくする
                    if (dir <= 0) age = 0;
                    y = x; dir = 1; soft = false;
                }
                else if (dir >= 0)
                {
                    if (dir == 0) age = 0;
                    y = soft ? y + (x - y) * softRise : x;
                    dir = 1;
                }
                else if (x - y > h && age >= MinFlipSec)
                {
                    // 暗くなっている途中の小さな上がり: 少しずつ明るくする（保っていた分を 1 コマで跳ばない）
                    y += (x - y) * softRise; dir = 1; age = 0; soft = true;
                }
                // それ以外（暗くなり始めた直後の小さな上がり）は今の明るさを保つ
                if (soft && x - y < 0.5f / 255f) soft = false;
            }
            else if (x < y)
            {
                // 色が大きく変わった（色替え・別の楽器の色）: 新しい音とみなし、向きを変えるのを待たない
                bool jump = ColorChanged(_out[i], c, x);
                float release = jump || k == Kind.Moving ? releaseFast : releaseSlow;
                if (dir <= 0) { if (dir == 0) age = 0; y = Math.Max(x, y * release); dir = -1; }
                else if (y - x > h && (age >= MinFlipSec || k == Kind.Moving || jump)) { y = Math.Max(x, y * release); dir = -1; age = 0; soft = false; }
                // それ以外（明るくなった直後の下がり・小さな下がり）は今の明るさを保つ
            }
            _y[i] = y;
            _dir[i] = (sbyte)dir;
            _age[i] = age;
            _soft[i] = soft;
            if (y == x) { _out[i] = c; continue; }
            // 明るくなるのを待たせる: 今の色を暗めに。明るさを保つ: 足りない分を前に出した光の色で足す
            var o = y < x ? c * (y / x) : Hold(c, x, _out[i], y);
            // 保つ明るさは、実際に出した明るさまで（色が変わって足す光を減らしたとき、次のコマに新しい色で明るさを戻さない）
            if (y > x) _y[i] = Math.Max(x, Math.Max(o.R, Math.Max(o.G, o.B)));
            px[i] = _out[i] = o;
        }
        _fresh = false;
    }

    /// <summary>
    /// 前に出した光 prev と今の光 c（いちばん強い成分 x）の色が大きく違うか（いちばん強い成分を 1 にそろえて比べる。
    /// どちらかがほとんど消えていれば、色はないので違わないとみなす）
    /// </summary>
    internal static bool ColorChanged(in LinColor prev, in LinColor c, float x)
    {
        float m = Math.Max(prev.R, Math.Max(prev.G, prev.B));
        if (m < 1e-3f || x < 1e-3f) return false;
        float ip = 1f / m, ic = 1f / x;
        float d = Math.Max(Math.Abs(prev.R * ip - c.R * ic), Math.Max(Math.Abs(prev.G * ip - c.G * ic), Math.Abs(prev.B * ip - c.B * ic)));
        return d > ColorJump;
    }

    private static float Luma(in LinColor c) => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;

    /// <summary>
    /// 今の光 c（いちばん強い成分 x）を、明るさ y（&gt; x）で出す光: c + 前に出した光 prev の色 × (y − x)。
    /// 色が大きく変わったとき（ColorChanged）は、足す光の輝度を、出す光の輝度が prev と c の明るいほうを超えない分までにする
    /// （混ぜた色が前後より明るく光らない。色がほとんど同じときは上限を付けない: 付けると、色相のわずかな揺れで明るさが行き来してちらつく）。
    /// prev と c が同じ色なら c × (y / x) と同じ。prev が消えていれば c × (y / x)
    /// </summary>
    internal static LinColor Hold(in LinColor c, float x, in LinColor prev, float y)
    {
        float m = Math.Max(prev.R, Math.Max(prev.G, prev.B));
        if (!(m > 1e-6f)) return x > 1e-6f ? c * (y / x) : c;
        var r = prev * ((y - x) / m);
        float lr = Luma(r);
        if (lr > 1e-9f)
        {
            float lc = Luma(c);
            float room = Math.Max(0f, Luma(prev) - lc) * 1.0001f + 1e-7f;
            if (lr > room && ColorChanged(prev, c, x)) r *= room / lr;
        }
        return c + r;
    }
}

// ==================================================
// OutputStage.cs - LED に送る直前の仕上げ (ガンマ補正の表・いちばん暗いときの明るさ・明るさの掛け算・
//                  暗い色を滑らかにするディザリング・0〜255 の整数への変換)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 光り方の計算はすべて「光の量」（LinColor、0〜1 の小数）で行い、最後にここで 0〜255 の整数にする。
///   1. 画面の色 → 光の量: Lin(c) = ガンマ補正オンなら c^2.2、オフなら c（1024 等分の表を引く）
///   2. 音の強さ I（0〜1）→ 明るさ: Ilin(I) = F0 + (1 − F0)·I^γ（F0 = いちばん暗いときの明るさ^γ）
///      ガンマの「後」に下限を足すので、下限 20% が 2.9% につぶれることがない
///   3. 全体の明るさ（Brightness）を掛ける
///   4. 0〜255 にする。速いデバイスは、暗い色（64 未満）だけ「ディザリング」する:
///      端数をコマをまたいで持ち越し、1.3 なら 1,1,2,1,1,2… のように出して平均を 1.3 にする（暗いところの段差が消える）
/// RGB のまま前のコマとの間をならす処理（EMA）は、遅れと色のくすみの原因になるので使わない
/// </summary>
public sealed class OutputStage
{
    /// <summary>ガンマ補正の指数（LED は暗い成分もそのまま光るので、2.2 乗して画面の色に近づける）</summary>
    public const float GammaExponent = 2.2f;
    /// <summary>ガンマ補正の表の細かさ（0〜1 を 1024 等分。間は直線で補う）</summary>
    private const int GammaSteps = 1024;
    /// <summary>この値（0〜255 の単位）より暗い成分だけディザリングする（明るいところでは段差が目立たないため）</summary>
    public const float DitherBelowCode = 64f;

    private static readonly float[] GammaLut = BuildGamma();

    private static float[] BuildGamma()
    {
        var lut = new float[GammaSteps + 1];
        for (int i = 0; i <= GammaSteps; i++) lut[i] = (float)Math.Pow(i / (double)GammaSteps, GammaExponent);
        return lut;
    }

    /// <summary>x^2.2（表を引く速い版）。x は 0〜1 に収めてから計算する</summary>
    public static float Pow22(float x)
    {
        if (!(x > 0)) return 0;   // NaN も 0 にする
        if (x >= 1) return 1;
        float u = x * GammaSteps;
        int i = (int)u;
        float f = u - i;
        return GammaLut[i] + (GammaLut[i + 1] - GammaLut[i]) * f;
    }

    /// <summary>画面の色の値（0〜1）→ LED の光の量。ガンマ補正オフなら、そのまま（0〜1 に収めるだけ）</summary>
    public static float Lin(float c, bool gamma) => gamma ? Pow22(c) : Math.Clamp(c, 0f, 1f);

    /// <summary>画面の色（Rgb）→ LED の光の量</summary>
    public static LinColor Lin(Rgb c, bool gamma) =>
        new(Lin(c.R / 255f, gamma), Lin(c.G / 255f, gamma), Lin(c.B / 255f, gamma));

    /// <summary>
    /// いちばん暗いときの明るさ F0（光の量）。minBrightness は 0〜1（設定画面の % ÷ 100）。
    /// ガンマ補正オンなら 2.2 乗する（20% → 0.029。見た目では 20% くらいの明るさに見える）
    /// </summary>
    public static float Floor(double minBrightness, bool gamma)
    {
        float m = (float)Math.Clamp(minBrightness, 0, 1);
        return gamma ? Pow22(m) : m;
    }

    /// <summary>音の強さ I（0〜1）→ 明るさ（光の量）。I = 0 でも F0 は残る</summary>
    public static float Ilin(float i, float f0, bool gamma) => f0 + (1 - f0) * Lin(i, gamma);

    /// <summary>
    /// ディザリングで持ち越している端数（LED ごと・成分ごと。−0.5〜+0.5）。
    /// デバイスごとに 1 つ持ち、LED の数が変わったら作り直す
    /// </summary>
    private float[] _err = Array.Empty<float>();

    /// <summary>持ち越している端数を捨てる</summary>
    public void Reset() => Array.Clear(_err);

    /// <summary>
    /// 光の量 src に明るさ brightness（0〜1）を掛けて、0〜255 の色にして dst へ入れる。
    /// dither = true なら、暗い成分（64 未満）は端数をコマをまたいで持ち越す（1 秒に何十回も送る速いデバイス用）
    /// </summary>
    /// <summary>
    /// この値（0〜255 の段）より暗い色はディザリングしない（四捨五入）。0 と 1 を行き来すると、消えたり点いたりして
    /// ちらついて見えるため。雷・波などの光り方で 1.5 にする（音楽連動は 0 のまま: 暗くなって消えるまでを滑らかにする）
    /// </summary>
    public float DitherFloorCode { get; set; }

    public void Write(ReadOnlySpan<LinColor> src, Span<Rgb> dst, float brightness, bool dither)
    {
        int n = Math.Min(src.Length, dst.Length);
        if (!dither)
        {
            Quantize(src[..n], dst[..n], brightness);
            return;
        }
        if (_err.Length != n * 3) _err = new float[n * 3];   // LED の数が変わったときだけ作り直す
        for (int i = 0; i < n; i++)
        {
            var c = src[i];
            byte r = Dither(c.R * brightness, ref _err[i * 3], DitherFloorCode);
            byte g = Dither(c.G * brightness, ref _err[i * 3 + 1], DitherFloorCode);
            byte b = Dither(c.B * brightness, ref _err[i * 3 + 2], DitherFloorCode);
            dst[i] = new Rgb(r, g, b);
        }
    }

    /// <summary>
    /// 1 成分のディザリング。a = 値×255 + 持ち越し、出す値 o = a を四捨五入、持ち越し = a − o（−0.5〜+0.5）。
    /// 真っ黒（0）はチラつかないよう、持ち越しを捨てて 0 のままにする
    /// </summary>
    private static byte Dither(float v, ref float err, float floorCode = 0)
    {
        if (!(v > 0)) { err = 0; return 0; }
        float code = v * 255;
        if (code >= DitherBelowCode || code < floorCode)
        {
            err = 0;
            return (byte)Math.Round(Math.Min(code, 255f));
        }
        float a = code + err;
        float o = Math.Clamp(MathF.Floor(a + 0.5f), 0f, 255f);
        err = Math.Clamp(a - o, -0.5f, 0.5f);
        return (byte)o;
    }

    /// <summary>WriteHold で、前に送った値を保つ幅（0〜255 の段）。これを超えて変わったときだけ値を変える</summary>
    public const float HoldBand = 0.75f;

    /// <summary>
    /// 前に送った値を保ちながら 0〜255 にする（ヒステリシス）。目標が前の値から HoldBand 段より離れたときだけ、四捨五入した値に変える。
    /// ゆっくり変わる暗い色が、段の境目のあたりで 2・3・2・3 と行き来しない（ディザリングと違い、行き来そのものが起きない）。
    /// 雷・波などの光り方用。ゆっくり明るくなる・暗くなるときは、1 段ずつ順に変わる
    /// </summary>
    public void WriteHold(ReadOnlySpan<LinColor> src, Span<Rgb> dst, float brightness)
    {
        int n = Math.Min(src.Length, dst.Length);
        if (_held.Length != n * 3)
        {
            _held = new float[n * 3];
            Array.Fill(_held, -1f);
        }
        for (int i = 0; i < n; i++)
        {
            var c = src[i];
            byte r = Hold(c.R * brightness, ref _held[i * 3]);
            byte g = Hold(c.G * brightness, ref _held[i * 3 + 1]);
            byte b = Hold(c.B * brightness, ref _held[i * 3 + 2]);
            dst[i] = new Rgb(r, g, b);
        }
    }

    private float[] _held = Array.Empty<float>();

    private static byte Hold(float v, ref float held)
    {
        float code = float.IsFinite(v) ? Math.Clamp(v * 255, 0f, 255f) : 0f;
        if (held < 0 || Math.Abs(code - held) > HoldBand || (code < 0.5f && held > 0 && held <= 1)) held = MathF.Round(code);
        return (byte)held;
    }

    /// <summary>ディザリングなしで 0〜255 にする（四捨五入）。更新の遅いデバイス・止まっている光り方用</summary>
    public static void Quantize(ReadOnlySpan<LinColor> src, Span<Rgb> dst, float brightness)
    {
        int n = Math.Min(src.Length, dst.Length);
        for (int i = 0; i < n; i++)
        {
            var c = src[i];
            dst[i] = new Rgb(ToByte(c.R * brightness), ToByte(c.G * brightness), ToByte(c.B * brightness));
        }
    }

    private static byte ToByte(float v) => !(v > 0) ? (byte)0 : v >= 1 ? (byte)255 : (byte)Math.Round(v * 255.0);
}

// ==================================================
// AttackHold.cs - 音楽連動の光の「立ち上がりの段つき」をなくす (明るくなった直後の短い間は、暗くしない)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 音楽連動で、LED が明るくなってから HoldSec の間は、前より暗くしない（色はそのまま、明るさだけ保つ）。
/// 低音の強さは 10.7 ミリ秒ごとに読むので、ベースの波（50〜60 Hz）と読むタイミングがずれて、値が少しずつ上下する。
/// そのため 1 回のキックの光が「明るく → 少し暗く → もっと明るく」と段をつけて立ち上がり、ちかちかして見えていた。
/// 明るくなった直後の小さな下がりだけを止めるので、光り始めの時刻は変わらない。
/// 半分より下へ一気に暗くなるとき（消灯・光り方の切り替えなど）は、待たずに暗くする。
/// 保つ間は、今の色を山の明るさにするのではなく、足りない分を前に出した光の色で足す（LedSteady.Hold。色が変わったときに、
/// 新しい色が山の明るさで一瞬光らないように）。保ち終わったら、一気に落とさず時定数 ReleaseTauSec で暗くする。
/// 色が大きく変わったとき（LedSteady.ColorChanged）は保たない（新しい色の音として、混ぜながら暗くするだけ）
/// </summary>
public sealed class AttackHold
{
    /// <summary>明るくなってから、暗くしない時間（秒。60 コマ／秒の 2 コマ分）</summary>
    public const double DefaultHoldSec = 0.035;
    /// <summary>この割合より下へ一気に暗くなるときは、保たずに暗くする</summary>
    public const float CutRatio = 0.5f;
    /// <summary>保ち終わってから今の明るさへ下げる時定数（秒）</summary>
    public const double ReleaseTauSec = 0.03;

    /// <summary>保つ時間（秒。0 なら何もしない。調べる用に変えられる）</summary>
    public static double HoldSec { get; set; } = DefaultHoldSec;

    /// <summary>
    /// 使う光り方: ビートで色替え・パーツごとの役割・楽器で光る・スペクトラムと、きらめき
    /// （きらめきの下地も、拍の光と低音で明るさが決まるので、立ち上がりが段つきになる）。
    /// 波紋は使わない: 細い輪が 1 コマに 1〜2 個ずつ進むので、通り過ぎた LED を 2 コマ保つと、いちばん明るい所が輪の
    /// 3 個後ろになって輪が遅れて見える（波紋のちらつきは LedSteady の「光が動く光り方」の直し方で止める）
    /// </summary>
    public static bool Applies(EffectVariant v) => v is EffectVariant.BeatHue or EffectVariant.Role
        or EffectVariant.Ensemble or EffectVariant.Spectrum or EffectVariant.Sparkle;

    private float[] _peak = Array.Empty<float>();
    private float[] _age = Array.Empty<float>();
    private LinColor[] _out = Array.Empty<LinColor>();

    public void Reset()
    {
        Array.Clear(_peak);
        Array.Clear(_out);
        Array.Fill(_age, float.PositiveInfinity);
    }

    /// <summary>光の量（明るさを掛けたあと）を、その場で直す</summary>
    public void Apply(LinColor[] px, int n, float dt)
    {
        double hold = HoldSec;
        if (hold <= 0) return;
        if (_peak.Length < n)
        {
            _peak = new float[n];
            _age = new float[n];
            _out = new LinColor[n];
            Array.Fill(_age, float.PositiveInfinity);
        }
        float release = EffectContext.Decay(dt, ReleaseTauSec);
        for (int i = 0; i < n; i++)
        {
            var c = px[i];
            float x = Math.Max(c.R, Math.Max(c.G, c.B));
            float p = _peak[i];
            if (x >= p || x < p * CutRatio || !(x > 1e-6f) || LedSteady.ColorChanged(_out[i], c, x))
            {
                if (x > p + 1e-4f) _age[i] = 0;   // 明るくなった: ここから数える
                else if (x < p) _age[i] = float.PositiveInfinity;
                _peak[i] = x;
                _out[i] = c;
                continue;
            }
            _age[i] += dt;
            // 保つ間は山の明るさのまま、保ち終わったら時定数 ReleaseTauSec で下げる
            float y = _age[i] <= hold ? p : Math.Max(x, p * release);
            // 足りない分を前に出した光の色で足す（色が同じなら「今の色を y の明るさで」と同じ）
            var o = LedSteady.Hold(c, x, _out[i], y);
            px[i] = _out[i] = o;
            _peak[i] = Math.Max(x, Math.Min(y, o.MaxChannel));
            if (y <= x) _age[i] = float.PositiveInfinity;
        }
    }
}

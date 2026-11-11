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
/// 半分より下へ一気に暗くなるとき（ストロボ・消灯・光り方の切り替え）は、待たずに暗くする
/// </summary>
public sealed class AttackHold
{
    /// <summary>明るくなってから、暗くしない時間（秒。60 コマ／秒の 2 コマ分）</summary>
    public const double DefaultHoldSec = 0.035;
    /// <summary>この割合より下へ一気に暗くなるときは、保たずに暗くする</summary>
    public const float CutRatio = 0.5f;

    /// <summary>保つ時間（秒。0 なら何もしない。調べる用に変えられる）</summary>
    public static double HoldSec { get; set; } = DefaultHoldSec;

    /// <summary>
    /// 使う光り方: LED の受け持ちが決まっていて、光が移動しないもの（パルス・ビートで色替え・ミックス・パーツごとの役割・
    /// スペクトラム（LED ごとに受け持つ音の高さが決まっている））。
    /// 光が動く光り方（波紋・テンポ追従・流れる波など）では、通り過ぎた光が残って尾を引くので使わない
    /// </summary>
    public static bool Applies(EffectVariant v) => v is EffectVariant.Pulse or EffectVariant.BeatHue or EffectVariant.Mix or EffectVariant.Role
        or EffectVariant.Spectrum;

    private float[] _peak = Array.Empty<float>();
    private float[] _age = Array.Empty<float>();

    public void Reset()
    {
        Array.Clear(_peak);
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
            Array.Fill(_age, float.PositiveInfinity);
        }
        for (int i = 0; i < n; i++)
        {
            var c = px[i];
            float x = Math.Max(c.R, Math.Max(c.G, c.B));
            float p = _peak[i];
            if (x >= p)
            {
                if (x > p + 1e-4f) _age[i] = 0;   // 明るくなった: ここから数える
                _peak[i] = x;
                continue;
            }
            _age[i] += dt;
            if (_age[i] <= hold && x >= p * CutRatio && x > 1e-6f)
            {
                px[i] = c * (p / x);   // 色は今のまま、明るさだけ直前の山に保つ
                continue;
            }
            _peak[i] = x;
            _age[i] = float.PositiveInfinity;
        }
    }
}

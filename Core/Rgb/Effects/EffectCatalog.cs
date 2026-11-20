// ==================================================
// EffectCatalog.cs - 光り方の一覧と選び方 (デバイスごとの上書き「Auto」・更新の遅いデバイス用の置き換え)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>実際に動かす光り方（遅いデバイス用の版を含む）</summary>
public enum EffectVariant
{
    /// <summary>遅いデバイス用の音量メーターの代わり（音量で光るパルス。暗くなるのに 0.4 秒）</summary>
    PulseLoudSlow,
    BeatHue,
    /// <summary>遅いデバイス用のビートで色替え（光の消え方 0.3 秒以上・色の切り替え 0.15 秒以上）</summary>
    BeatHueSlow,
    Spectrum,
    Meter,
    Ripple,
    Sparkle,
    /// <summary>遅いデバイス用のスペクトラムの代わり（低音・中音・高音の強さで色が混ざる全体で 1 色。暗くなるのに 0.3 秒以上）</summary>
    MixSlow,
    /// <summary>パーツごとの役割</summary>
    Role,
    /// <summary>遅いデバイス用のパーツごとの役割（光の消え方をゆっくり）</summary>
    RoleSlow,
    /// <summary>楽器で光る</summary>
    Ensemble,
    /// <summary>遅いデバイス用の楽器で光る（一瞬の光も動きもなく、明るさをゆっくり変える）</summary>
    EnsembleSlow,
}

/// <summary>
/// どの光り方を使うかを決める。
///   デバイスの設定が "Auto"（または空）なら全体の光り方（MusicStyle）。更新の遅いデバイス（GPU・メモリなど）は次のように置き換える:
///     ビートで色替え → 遅い版、スペクトラム → 遅い版のミックス（低音・中音・高音の色が混ざる全体で 1 色）、
///     音量メーター → 音量で光る遅い版のパルス、波紋・きらめき → 遅い版のビートで色替え、
///     パーツごとの役割 → 遅い版のパーツごとの役割、楽器で光る → 遅い版の楽器で光る（一瞬の光も動きもなく、明るさをゆっくり変える）
///   デバイスの設定で光り方を指定したら、遅いデバイスでもそのまま使う。
///   なくした光り方の名前（6.15.10 までのパルス・ストロボ・ミックス・エナジー・テンポ追従・流れる波・ムード）は、置き換え先の光り方にする
///   （MusicStyles.NormalizeOverride。デバイスの設定のミックス・流れる波・ムードは "Auto" と同じ）
/// </summary>
public static class EffectCatalog
{
    public const string Auto = "Auto";

    public static readonly int VariantCount = Enum.GetValues<EffectVariant>().Length;

    /// <summary>実際に使う光り方を決める</summary>
    /// <param name="globalEffect">全体の光り方（ResolvedMusic.Effect）</param>
    /// <param name="deviceEffect">デバイスの設定（RgbDeviceSettings.Effect。"Auto" なら全体の光り方）</param>
    public static EffectVariant Resolve(string? globalEffect, string? deviceEffect, bool isSlow)
    {
        string over = MusicStyles.NormalizeOverride(deviceEffect);
        string? chosen = over == Auto ? null : over;
        bool auto = chosen is null;
        string name = chosen ?? MusicStyles.Normalize(globalEffect);
        if (isSlow && auto)
        {
            return name switch
            {
                MusicStyles.Spectrum => EffectVariant.MixSlow,
                MusicStyles.Meter => EffectVariant.PulseLoudSlow,
                MusicStyles.Role => EffectVariant.RoleSlow,
                MusicStyles.Ensemble => EffectVariant.EnsembleSlow,
                _ => EffectVariant.BeatHueSlow,   // ビートで色替え・波紋・きらめき
            };
        }
        return name switch
        {
            MusicStyles.Meter => EffectVariant.Meter,
            MusicStyles.Spectrum => EffectVariant.Spectrum,
            MusicStyles.Ripple => EffectVariant.Ripple,
            MusicStyles.Sparkle => EffectVariant.Sparkle,
            MusicStyles.Role => EffectVariant.Role,
            MusicStyles.Ensemble => EffectVariant.Ensemble,
            _ => EffectVariant.BeatHue,
        };
    }

    /// <summary>
    /// 光り方の表示名（MusicStyles の値）。遅い版のうち、1 つの光り方だけの代わり（スペクトラムの MixSlow・音量メーターの PulseLoudSlow・
    /// パーツごとの役割・楽器で光る）は、その光り方の名前。波紋・きらめきの代わりにも使う BeatHueSlow は、
    /// 実際の見た目の「ビートで色替え」（置き換えたことが分かる）
    /// </summary>
    public static string NameOf(EffectVariant v) => v switch
    {
        EffectVariant.BeatHue or EffectVariant.BeatHueSlow => MusicStyles.BeatHue,
        EffectVariant.Spectrum or EffectVariant.MixSlow => MusicStyles.Spectrum,
        EffectVariant.Meter or EffectVariant.PulseLoudSlow => MusicStyles.Meter,
        EffectVariant.Ripple => MusicStyles.Ripple,
        EffectVariant.Sparkle => MusicStyles.Sparkle,
        EffectVariant.Role or EffectVariant.RoleSlow => MusicStyles.Role,
        EffectVariant.Ensemble or EffectVariant.EnsembleSlow => MusicStyles.Ensemble,
        _ => MusicStyles.BeatHue,
    };

    /// <summary>遅いデバイス用の版か（ビートの光の消え方・色の切り替えを遅くする）</summary>
    public static bool IsSlowVariant(EffectVariant v) =>
        v is EffectVariant.PulseLoudSlow or EffectVariant.BeatHueSlow or EffectVariant.MixSlow or EffectVariant.RoleSlow
        or EffectVariant.EnsembleSlow;

    /// <summary>光り方を作る（デバイスごとに 1 回だけ。毎コマは作らない）</summary>
    public static IMusicEffect Create(EffectVariant v) => v switch
    {
        EffectVariant.PulseLoudSlow => new PulseEffect(slow: true, source: PulseEffect.Source.Loudness),
        EffectVariant.BeatHue => new BeatHueEffect(followBeat: true),
        EffectVariant.BeatHueSlow => new BeatHueEffect(slow: true),
        EffectVariant.Spectrum => new SpectrumEffect(),
        EffectVariant.Meter => new MeterEffect(),
        EffectVariant.Ripple => new RippleEffect(),
        EffectVariant.Sparkle => new SparkleEffect(),
        EffectVariant.MixSlow => new MixEffect(slow: true),
        EffectVariant.Role => new RoleEffect(),
        EffectVariant.RoleSlow => new RoleEffect(slow: true),
        EffectVariant.Ensemble => new EnsembleEffect(),
        EffectVariant.EnsembleSlow => new EnsembleEffect(slow: true),
        _ => new BeatHueEffect(),
    };
}

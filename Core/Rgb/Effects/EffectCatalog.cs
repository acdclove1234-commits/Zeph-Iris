// ==================================================
// EffectCatalog.cs - 光り方の一覧と選び方 (デバイスごとの上書き「Auto」・更新の遅いデバイス用の置き換え・
//                    ストロボを使ってよいか)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>実際に動かす光り方（遅いデバイス用の版を含む）</summary>
public enum EffectVariant
{
    Pulse,
    /// <summary>遅いデバイス用のパルス（立ち上がり 40 ミリ秒・残光 0.45 秒）</summary>
    PulseSlow,
    /// <summary>遅いデバイス用の音量メーターの代わり（音量で光るパルス。暗くなるのに 0.4 秒）</summary>
    PulseLoudSlow,
    BeatHue,
    /// <summary>遅いデバイス用のビートで色替え（光の消え方 0.3 秒以上・色の切り替え 0.15 秒以上）</summary>
    BeatHueSlow,
    Spectrum,
    Energy,
    Meter,
    Scroll,
    Ripple,
    Sparkle,
    Chase,
    Mood,
    Strobe,
    Mix,
    /// <summary>遅いデバイス用のミックス（暗くなるのに 0.3 秒以上）</summary>
    MixSlow,
    /// <summary>パーツごとの役割</summary>
    Role,
    /// <summary>遅いデバイス用のパーツごとの役割（光の消え方をゆっくり）</summary>
    RoleSlow,
}

/// <summary>
/// どの光り方を使うかを決める。
///   デバイスの設定が "Auto"（または空）なら全体の光り方（MusicStyle）。更新の遅いデバイス（GPU・メモリなど）は次のように置き換える:
///     パルス → 遅い版のパルス、ビートで色替え → 遅い版、スペクトラム・エナジー・ミックス → 遅い版のミックス（全体で 1 色）、
///     音量メーター → 音量で光る遅い版のパルス、流れる波・波紋・きらめき・テンポ追従・ストロボ → 遅い版のビートで色替え、ムード → ムード
///   デバイスの設定で光り方を指定したら、遅いデバイスでもそのまま使う（ただしストロボは使わない）。
///   ストロボは「使ってよい」（強さ「激しい」かつ点滅の制限がオフ）ときだけ。だめなら「ビートで色替え」
/// </summary>
public static class EffectCatalog
{
    public const string Auto = "Auto";

    public static readonly int VariantCount = Enum.GetValues<EffectVariant>().Length;

    /// <summary>実際に使う光り方を決める</summary>
    /// <param name="globalEffect">全体の光り方（ResolvedMusic.Effect）</param>
    /// <param name="deviceEffect">デバイスの設定（RgbDeviceSettings.Effect。"Auto" なら全体の光り方）</param>
    public static EffectVariant Resolve(string? globalEffect, string? deviceEffect, bool isSlow, bool strobeAllowed)
    {
        bool auto = string.IsNullOrEmpty(deviceEffect) || deviceEffect == Auto || !IsKnown(deviceEffect);
        string name = auto ? (IsKnown(globalEffect) ? globalEffect! : MusicStyles.BeatHue) : deviceEffect!;
        if (name == MusicStyles.Strobe && (!strobeAllowed || isSlow)) name = MusicStyles.BeatHue;
        if (isSlow && auto)
        {
            return name switch
            {
                MusicStyles.Pulse => EffectVariant.PulseSlow,
                MusicStyles.Spectrum or MusicStyles.Energy or MusicStyles.Mix => EffectVariant.MixSlow,
                MusicStyles.Meter => EffectVariant.PulseLoudSlow,
                MusicStyles.Mood => EffectVariant.Mood,
                MusicStyles.Role => EffectVariant.RoleSlow,
                _ => EffectVariant.BeatHueSlow,   // ビートで色替え・流れる波・波紋・きらめき・テンポ追従
            };
        }
        return name switch
        {
            MusicStyles.Pulse => EffectVariant.Pulse,
            MusicStyles.Mix => EffectVariant.Mix,
            MusicStyles.Meter => EffectVariant.Meter,
            MusicStyles.Spectrum => EffectVariant.Spectrum,
            MusicStyles.Energy => EffectVariant.Energy,
            MusicStyles.Scroll => EffectVariant.Scroll,
            MusicStyles.Ripple => EffectVariant.Ripple,
            MusicStyles.Sparkle => EffectVariant.Sparkle,
            MusicStyles.Chase => EffectVariant.Chase,
            MusicStyles.Mood => EffectVariant.Mood,
            MusicStyles.Strobe => EffectVariant.Strobe,
            MusicStyles.Role => EffectVariant.Role,
            _ => EffectVariant.BeatHue,
        };
    }

    /// <summary>光り方の表示名（MusicStyles の値。遅い版は元の光り方の名前）</summary>
    public static string NameOf(EffectVariant v) => v switch
    {
        EffectVariant.Pulse or EffectVariant.PulseSlow or EffectVariant.PulseLoudSlow => MusicStyles.Pulse,
        EffectVariant.BeatHue or EffectVariant.BeatHueSlow => MusicStyles.BeatHue,
        EffectVariant.Mix or EffectVariant.MixSlow => MusicStyles.Mix,
        EffectVariant.Spectrum => MusicStyles.Spectrum,
        EffectVariant.Energy => MusicStyles.Energy,
        EffectVariant.Meter => MusicStyles.Meter,
        EffectVariant.Scroll => MusicStyles.Scroll,
        EffectVariant.Ripple => MusicStyles.Ripple,
        EffectVariant.Sparkle => MusicStyles.Sparkle,
        EffectVariant.Chase => MusicStyles.Chase,
        EffectVariant.Mood => MusicStyles.Mood,
        EffectVariant.Strobe => MusicStyles.Strobe,
        EffectVariant.Role or EffectVariant.RoleSlow => MusicStyles.Role,
        _ => MusicStyles.BeatHue,
    };

    /// <summary>遅いデバイス用の版か（ビートの光の消え方・色の切り替えを遅くする）</summary>
    public static bool IsSlowVariant(EffectVariant v) =>
        v is EffectVariant.PulseSlow or EffectVariant.PulseLoudSlow or EffectVariant.BeatHueSlow or EffectVariant.MixSlow or EffectVariant.RoleSlow;

    /// <summary>光り方を作る（デバイスごとに 1 回だけ。毎コマは作らない）</summary>
    public static IMusicEffect Create(EffectVariant v) => v switch
    {
        EffectVariant.Pulse => new PulseEffect(steadyColor: true),
        EffectVariant.PulseSlow => new PulseEffect(slow: true, steadyColor: true),
        EffectVariant.PulseLoudSlow => new PulseEffect(slow: true, source: PulseEffect.Source.Loudness),
        EffectVariant.BeatHueSlow => new BeatHueEffect(slow: true),
        EffectVariant.Spectrum => new SpectrumEffect(),
        EffectVariant.Energy => new EnergyEffect(),
        EffectVariant.Meter => new MeterEffect(),
        EffectVariant.Scroll => new ScrollEffect(),
        EffectVariant.Ripple => new RippleEffect(),
        EffectVariant.Sparkle => new SparkleEffect(),
        EffectVariant.Chase => new ChaseEffect(),
        EffectVariant.Mood => new MoodEffect(),
        EffectVariant.Strobe => new StrobeEffect(),
        EffectVariant.Mix => new MixEffect(),
        EffectVariant.MixSlow => new MixEffect(slow: true),
        EffectVariant.Role => new RoleEffect(),
        EffectVariant.RoleSlow => new RoleEffect(slow: true),
        _ => new BeatHueEffect(),
    };

    private static bool IsKnown(string? name)
    {
        if (name is null) return false;
        foreach (var s in MusicStyles.All)
            if (s == name) return true;
        return false;
    }
}

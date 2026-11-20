// ==================================================
// MusicTuning.cs - 音楽連動の「プリセット」「強さ」と、設定から実際の値を決める計算
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

public static class Intensities
{
    public const string Subtle = "Subtle";
    public const string Moderate = "Moderate";
    public const string High = "High";
    public const string Intense = "Intense";
    public static readonly string[] All = { Subtle, Moderate, High, Intense };

    public static string Label(string id) => id switch
    {
        Subtle => L("控えめ"),
        Moderate => L("ふつう"),
        High => L("強め"),
        Intense => L("激しい"),
        _ => id,
    };
}

/// <summary>色の組み合わせ（パレット）の名前。中身の色は Palette.cs</summary>
public static class Palettes
{
    public const string Rainbow = "Rainbow";
    public const string Neon = "Neon";
    public const string Sunset = "Sunset";
    public const string Ocean = "Ocean";
    public const string Fire = "Fire";
    public const string Vaporwave = "Vaporwave";
    public const string Forest = "Forest";
    public const string Ice = "Ice";
    /// <summary>選んだ色（StaticColor）と、その両隣の色</summary>
    public const string Mono = "Mono";
    /// <summary>選んだ色（StaticColor）だけ。色は変わらず、明るさだけで音に合わせる</summary>
    public const string Solid = "Solid";
    public static readonly string[] All = { Rainbow, Neon, Sunset, Ocean, Fire, Vaporwave, Forest, Ice, Mono, Solid };

    /// <summary>選んだ色（StaticColor）を使うパレットか</summary>
    public static bool UsesStaticColor(string? id) => id is Mono or Solid;

    /// <summary>単色（選んだ色だけ）なのに、楽器を色以外で見せる「楽器で光る」を使っていない（画面に案内を出す）</summary>
    public static bool SuggestEnsemble(RgbConfig cfg) =>
        cfg.Mode == RgbModes.Music && (cfg.Music ?? new MusicConfig()).Palette == Solid && cfg.MusicStyle != MusicStyles.Ensemble;

    public static string Label(string id) => id switch
    {
        Rainbow => L("レインボー"),
        Neon => L("ネオン"),
        Sunset => L("サンセット"),
        Ocean => L("オーシャン"),
        Fire => L("ファイア"),
        Vaporwave => L("ヴェイパーウェイヴ"),
        Forest => L("フォレスト"),
        Ice => L("アイス"),
        Mono => L("選んだ色と近い色"),
        Solid => L("選んだ色だけ（単色）"),
        _ => id,
    };
}

public static class MusicPresets
{
    public const string Chill = "Chill";
    public const string Groove = "Groove";
    public const string Party = "Party";
    public const string Club = "Club";
    public const string Visualizer = "Visualizer";
    public const string Custom = "Custom";
    public static readonly string[] All = { Chill, Groove, Party, Club, Visualizer, Custom };

    public static string Label(string id) => id switch
    {
        Chill => L("チル（ゆったり）"),
        Groove => L("グルーヴ（標準）"),
        Party => L("パーティー"),
        Club => L("クラブ（激しい）"),
        Visualizer => L("ビジュアライザー"),
        Custom => L("カスタム"),
        _ => id,
    };
}

public static class IdleEffects
{
    public const string Mood = "Mood";
    public const string Breathing = "Breathing";
    public const string Static = "Static";
    public const string Off = "Off";
    public static readonly string[] All = { Mood, Breathing, Static, Off };

    public static string Label(string id) => id switch
    {
        Mood => L("ゆっくり色が変わる"),
        Breathing => L("呼吸"),
        Static => L("固定色"),
        Off => L("消灯"),
        _ => id,
    };
}

/// <summary>
/// 「強さ」ごとの値。
/// ColorStepBeats = 色を次へ進める間隔（拍）。CrossfadeMs = 色が切り替わるのにかける時間
/// </summary>
public sealed record IntensityProfile(
    string Name, double ReactionMs, double SmoothnessMs, double MinBrightness, double FlashAmp, double Punch,
    int ColorStepBeats, double CrossfadeMs, bool DownbeatAccent);

/// <summary>LED の描画で使う、音楽連動の設定を決めたもの（1 コマごとに作り直さない。設定が変わったときだけ作る）</summary>
/// <param name="Effect">光り方（MusicStyles）</param>
/// <param name="MinBrightness">いちばん暗いときの明るさ（0〜0.4。ガンマ補正の前の値）</param>
/// <param name="FlashAmp">ビートの光の強さ（0〜1）</param>
/// <param name="ColorStepBeats">色を次へ進める間隔（拍。1 / 2 / 4）</param>
/// <param name="CrossfadeMs">色の切り替えにかける時間（ミリ秒）</param>
/// <param name="AudioDelayMs">音の遅れの補正（ミリ秒。+ で LED を遅らせる）</param>
/// <param name="Speed">流れる速さの倍率（RgbConfig.Speed）</param>
/// <param name="RoleSet">「パーツに合わせる」のゾーンの分け方（MusicRoleSets。音の高さ・楽器・ドラムだけ）</param>
/// <param name="SceneColors">楽器の AI の場面ごとに色を変える（RgbConfig.MusicScenes。ScenePalettes）</param>
public sealed record ResolvedMusic(
    string Effect, string Intensity, string Palette, double MinBrightness, double FlashAmp, int ColorStepBeats,
    double CrossfadeMs, bool DownbeatAccent, bool ReduceFlashing, double AudioDelayMs,
    string IdleEffect, double IdleDelaySec, string TempoMode, double Speed, string RoleSet = MusicRoleSets.Bands,
    bool SceneColors = false)
{
    public static readonly ResolvedMusic Default = MusicTuning.Resolve(new RgbConfig());
}

public static class MusicTuning
{
    private static readonly IntensityProfile[] Profiles =
    {
        new(Intensities.Subtle, 40, 500, 28, 0.3, 32, 4, 500, false),
        new(Intensities.Moderate, 8, 220, 17, 0.55, 55, 2, 200, false),
        new(Intensities.High, 3, 150, 8, 0.85, 68, 1, 80, false),
        new(Intensities.Intense, 0, 90, 3, 1.0, 85, 1, 0, true),
    };

    public static IntensityProfile Profile(string intensity) =>
        Profiles.FirstOrDefault(p => p.Name == intensity) ?? Profiles[1];

    /// <summary>
    /// ハイハットへの反応（「強さ」と「激しい点滅を抑える」で決める）。ハイハットは 1 秒に何回も鳴るので、
    /// キック・スネアと同じ明るさで光らせると、ちかちかして落ち着かない（「ハイハットに過敏」）。
    ///   Gain = 光の明るさ・粒の数に掛ける倍率、MinGapSec = 同じまとまりで次に光らせるまでの最小の間隔（秒）
    /// </summary>
    public static (float Gain, double MinGapSec) HatResponse(ResolvedMusic m)
    {
        var (gain, gap) = m.Intensity switch
        {
            Intensities.Subtle => (0.45f, 0.20),
            Intensities.High => (0.85f, 0.08),
            Intensities.Intense => (1.0f, 0.05),
            _ => (0.7f, 0.12),
        };
        if (m.ReduceFlashing)
        {
            gain *= 0.85f;
            gap = Math.Max(gap, 0.15);
        }
        return (gain, gap);
    }

    /// <summary>「強さ」を選んだとき: 反応の速さ・滑らかさ・メリハリ・いちばん暗いときの明るさを、その強さの値にする</summary>
    public static void ApplyIntensity(MusicConfig m, string intensity)
    {
        var p = Profile(intensity);
        m.Intensity = p.Name;
        m.ReactionMs = p.ReactionMs;
        m.SmoothnessMs = p.SmoothnessMs;
        m.Punch = p.Punch;
        m.MinBrightness = p.MinBrightness;
    }

    /// <summary>プリセットを選んだとき: 光り方・強さ・パレットなどをまとめて書き換える</summary>
    public static void ApplyPreset(RgbConfig cfg, string preset)
    {
        var m = cfg.Music;
        (string effect, string intensity, string palette) = preset switch
        {
            MusicPresets.Chill => (MusicStyles.Ensemble, Intensities.Subtle, Palettes.Ocean),
            MusicPresets.Groove => (MusicStyles.BeatHue, Intensities.Moderate, Palettes.Neon),
            MusicPresets.Party => (MusicStyles.Ripple, Intensities.High, Palettes.Vaporwave),
            MusicPresets.Club => (MusicStyles.BeatHue, Intensities.Intense, Palettes.Neon),
            MusicPresets.Visualizer => (MusicStyles.Spectrum, Intensities.High, Palettes.Rainbow),
            _ => (cfg.MusicStyle, m.Intensity, m.Palette),
        };
        cfg.MusicStyle = effect;
        ApplyIntensity(m, intensity);
        m.Palette = palette;
        m.Preset = MusicPresets.All.Contains(preset) ? preset : MusicPresets.Custom;
    }

    /// <summary>「反応のしかた」の 1 つの値: 画面の名前・今の値・初期値・表示の書式</summary>
    public sealed record TuningValue(string Label, double Current, double Default, string Format)
    {
        /// <summary>初期値から変えているか</summary>
        public bool Changed => Math.Abs(Current - Default) > 1e-6;
        public string Show(double v) => string.Format(System.Globalization.CultureInfo.InvariantCulture, Format, v);
    }

    /// <summary>
    /// 「反応のしかた」の値と、その初期値。「強さ」に結びついた 4 つ（反応の速さ・滑らかさ・メリハリ・いちばん暗いとき）は
    /// 今の強さの値、それ以外はアプリの初期値。
    /// 音の遅れの補正は、イヤホン・スピーカーに合わせた値なので含めない
    /// </summary>
    public static IReadOnlyList<TuningValue> TuningValues(RgbConfig cfg)
    {
        var m = cfg.Music;
        var p = Profile(m.Intensity);
        var d = new MusicConfig();
        var dr = new RgbConfig();
        return new[]
        {
            new TuningValue(L("感度"), m.GainDb, d.GainDb, "{0:+0;-0;0} dB"),
            new TuningValue(L("反応の感度"), cfg.Sensitivity, dr.Sensitivity, "{0:F1}"),
            new TuningValue(L("滑らかさ"), m.SmoothnessMs, p.SmoothnessMs, "{0:F0} ms"),
            new TuningValue(L("メリハリ"), m.Punch, p.Punch, "{0:F0}%"),
            new TuningValue(L("いちばん暗いとき"), m.MinBrightness, p.MinBrightness, "{0:F0}%"),
            new TuningValue(L("反応の速さ"), m.ReactionMs, p.ReactionMs, "{0:F0} ms"),
            new TuningValue(L("明るさに使う音量の幅"), m.DynamicRangeDb, d.DynamicRangeDb, "{0:F0} dB"),
            new TuningValue(L("音の高さのバランス"), m.AutoBalance, d.AutoBalance, "{0:F0}%"),
            new TuningValue(L("高音の持ち上げ"), m.TrebleTilt, d.TrebleTilt, "{0:F1} dB/oct"),
            new TuningValue(L("コントラスト"), m.Contrast, d.Contrast, "{0:F2}"),
        };
    }

    /// <summary>初期値から変えている値の説明（例「反応の感度 2.0（初期 1.0）」。変えていなければ空）</summary>
    public static string ChangedTuningSummary(RgbConfig cfg) =>
        string.Join(L("、"), TuningValues(cfg).Where(v => v.Changed).Select(v => L($"{v.Label} {v.Show(v.Current)}（初期 {v.Show(v.Default)}）")));

    /// <summary>
    /// 「初期値に戻す」: 反応のしかたの値を初期値にする（光り方・強さ・色の組み合わせ・音の遅れの補正はそのまま）。
    /// 戻した結果が、どれかのプリセットとまったく同じなら、そのプリセットを選んだ状態にする
    /// </summary>
    public static void ResetTuning(RgbConfig cfg)
    {
        var m = cfg.Music;
        var d = new MusicConfig();
        ApplyIntensity(m, m.Intensity);
        m.GainDb = d.GainDb;
        m.DynamicRangeDb = d.DynamicRangeDb;
        m.AutoBalance = d.AutoBalance;
        m.TrebleTilt = d.TrebleTilt;
        m.Contrast = d.Contrast;
        cfg.Sensitivity = new RgbConfig().Sensitivity;
        m.Preset = MatchingPreset(cfg) ?? MusicPresets.Custom;
    }

    /// <summary>今の光り方・強さ・色の組み合わせ・強さの値が、まったく同じになるプリセット（なければ null）</summary>
    public static string? MatchingPreset(RgbConfig cfg)
    {
        var m = cfg.Music;
        foreach (var preset in MusicPresets.All)
        {
            if (preset == MusicPresets.Custom) continue;
            var sample = new RgbConfig { MusicStyle = cfg.MusicStyle };
            ApplyPreset(sample, preset);
            var s = sample.Music;
            if (sample.MusicStyle == cfg.MusicStyle && s.Intensity == m.Intensity && s.Palette == m.Palette
                && s.ReactionMs == m.ReactionMs && s.SmoothnessMs == m.SmoothnessMs && s.Punch == m.Punch && s.MinBrightness == m.MinBrightness)
                return preset;
        }
        return null;
    }

    /// <summary>分析エンジンの設定</summary>
    public static AudioEngineSettings EngineSettings(RgbConfig cfg)
    {
        var m = cfg.Music ?? new MusicConfig();
        return new AudioEngineSettings(
            GainDb: Math.Clamp(m.GainDb, -12, 12),
            DynamicRangeDb: Math.Clamp(m.DynamicRangeDb, 24, 60),
            AutoBalance: Math.Clamp(m.AutoBalance, 0, 100) / 100,
            TrebleTilt: Math.Clamp(m.TrebleTilt, 0, 4.5),
            Contrast: Math.Clamp(m.Contrast, 0.7, 2.0),
            ReactionMs: Math.Clamp(m.ReactionMs, 0, 40),
            SmoothnessMs: Math.Clamp(m.SmoothnessMs, 60, 600),
            Punch: Math.Clamp(m.Punch, 0, 100) / 100,
            BeatSensitivity: Math.Clamp(cfg.Sensitivity, 0.5, 3),
            TempoMode: m.TempoMode,
            TempoFactor: m.TempoFactor);
    }

    /// <summary>LED の描画で使う値</summary>
    public static ResolvedMusic Resolve(RgbConfig cfg)
    {
        var m = cfg.Music ?? new MusicConfig();
        var p = Profile(m.Intensity);
        return new ResolvedMusic(
            Effect: MusicStyles.Normalize(cfg.MusicStyle),
            Intensity: p.Name,
            Palette: Palettes.All.Contains(m.Palette) ? m.Palette : Palettes.Rainbow,
            MinBrightness: Math.Clamp(m.MinBrightness, 0, 40) / 100,
            FlashAmp: p.FlashAmp,
            ColorStepBeats: p.ColorStepBeats,
            CrossfadeMs: p.CrossfadeMs,
            DownbeatAccent: p.DownbeatAccent,
            ReduceFlashing: m.ReduceFlashing,
            AudioDelayMs: Math.Clamp(m.AudioDelayMs, -100, 400),
            IdleEffect: IdleEffects.All.Contains(m.IdleEffect) ? m.IdleEffect : IdleEffects.Mood,
            IdleDelaySec: Math.Clamp(m.IdleDelaySec, 1, 10),
            TempoMode: m.TempoMode,
            Speed: Math.Clamp(cfg.Speed, 0.3, 10),
            RoleSet: MusicRoleSets.Normalize(m.RoleSet),
            SceneColors: cfg.MusicScenes);
    }

    /// <summary>
    /// 光り方の表示名と説明（設定画面・タスクトレイ用）。説明の 1 行目は、その光り方がどの音に反応するか（Follows）
    /// </summary>
    public static (string Label, string Description) Describe(string effect)
    {
        var (label, description) = DescribeLook(effect);
        string follows = Follows(effect);
        return (label, follows.Length == 0 ? description : description.Length == 0 ? follows : follows + "\n" + description);
    }

    /// <summary>
    /// その光り方がどの音に反応するかを 1 文で（光り方を選ぶ画面のカードの 1 行目・タスクトレイの説明の 1 行目。
    /// どの音で光っているのか分からない、という声があったため）
    /// </summary>
    public static string Follows(string effect) => effect switch
    {
        MusicStyles.BeatHue => L("反応する音: キックで光る（スネアは弱く、ドラムのない拍は暗いまま。テンポを使わない設定では低音）。色は小節の頭のキックと曲の区切りで変わる"),
        MusicStyles.Meter => L("反応する音: 音量で長さが変わり、キックとスネアで弾む"),
        MusicStyles.Spectrum => L("反応する音: 低音の端はキックで光り、その先は高い音まで、高さごとの音の強さで光る（色は音の高さごとに決まっている）"),
        MusicStyles.Ripple => L("反応する音: キック（バスドラム）で光の輪が出る（爆発など大きな音でも）。テンポをつかむと、続けて鳴るキックに先回りして光る"),
        MusicStyles.Sparkle => L("反応する音: キック・スネア・ハイハットと、ギターなどの音の出だし（テンポをつかんでいるとき）が、それぞれ違う光り方で光る"),
        MusicStyles.Role => L("反応する音: パーツ（ゾーン）ごとに決めた音（低音・中音・高音・楽器）だけ"),
        MusicStyles.Ensemble => L("反応する音: 楽器ごと（グラボ・メモリでは、拍でふくらみ、目立つ楽器の色になる）"),
        _ => "",
    };

    /// <summary>光り方の表示名と、光り方の見た目の説明（Describe の 2 行目）</summary>
    private static (string Label, string Description) DescribeLook(string effect) => effect switch
    {
        MusicStyles.BeatHue => (L("ビートで色替え"), L("キックのたびにぱっと光り、小節の頭や曲の区切りで次の色へ切り替わる")),
        MusicStyles.Meter => (L("音量メーター"), L("音量の分だけ端から点き、ピークの点がゆっくり落ちてくる")),
        MusicStyles.Spectrum => (L("スペクトラム"), L("LED の並びに低音〜高音を並べ、それぞれの強さで光る")),
        MusicStyles.Ripple => (L("波紋"), L("キックのたびに光の輪が広がる")),
        MusicStyles.Sparkle => (L("きらめき"), L("キックで全体がふわっと、スネアでまとまった光、ハイハットで白い粒、ギターの音の出だしで色の帯が走る")),
        MusicStyles.Role => (L("パーツごとの役割"), L("ファンは低音、グラボは中音、メモリは高音のように、パーツごとに決まった音で光る。低音・中音・高音は色で見分けられる。ファンの端子がいくつもあれば、低音・中音・高音に分ける（ゾーンの設定「反応する音」で変えられる）")),
        MusicStyles.Ensemble => (L("楽器で光る"), L("キックは全体、スネアは片側、ハイハットは小さな粒、ベースは端のほう、歌は全体の明るさ、ギターや鍵盤は流れる光で見せる。単色（選んだ色だけ）でも楽器が分かる。小さいゾーンや更新の遅い機器では、一部の動きを省きます")),
        _ => (effect, ""),
    };
}

//// <summary>
/// ゾーンが音楽のどの音に反応するか（光り方「パーツごとの役割」で使う）。
/// 低音・中音・高音・音全体は音の高さで分ける（6.15.7 までと同じ）。キックから後は楽器ごと（InstrumentFeatures の手がかりで光る）。
/// 設定には名前（MusicSounds）で保存する。番号の並びは変えない（新しい役割は後ろに足す）
/// </summary>
public enum MusicRole : byte
{
    /// <summary>低音（キック・ベース）</summary>
    Bass,
    /// <summary>中音（スネア・ボーカル）</summary>
    Mid,
    /// <summary>高音（ハイハット・シンバル）</summary>
    Treble,
    /// <summary>音全体（音量）</summary>
    All,
    /// <summary>キック（バスドラム）だけ</summary>
    Kick,
    /// <summary>スネア・クラップだけ</summary>
    Snare,
    /// <summary>ハイハット・シンバルだけ</summary>
    Hat,
    /// <summary>ベース（楽器。キックを除いた持続音の低音と、その音の高さ）</summary>
    BassLine,
    /// <summary>ボーカル・リード（真ん中の持続音・金管のような明るい持続音）</summary>
    Vocal,
    /// <summary>和音（鍵盤・ギターのはじく音）</summary>
    Harmony,
    /// <summary>パッド・シンセ（変わらずに続く持続音）</summary>
    Pad,
}

/// <summary>役割の分類</summary>
public static class MusicRoles
{
    /// <summary>役割の数（RoleEffect の配列の長さ）</summary>
    public static readonly int Count = Enum.GetValues<MusicRole>().Length;

    /// <summary>楽器ごとの役割（キックから後）か。低音・中音・高音・音全体は false</summary>
    public static bool IsInstrument(MusicRole r) => r >= MusicRole.Kick;

    /// <summary>ドラムの役割（キック・スネア・ハイハット）か</summary>
    public static bool IsDrum(MusicRole r) => r is MusicRole.Kick or MusicRole.Snare or MusicRole.Hat;
}

/// <summary>
/// 「パーツに合わせる」のゾーンをどう分けるか（MusicConfig.RoleSet）。
///   Bands … 音の高さ（低音・中音・高音・音全体）。6.15.7 までと同じ（設定に値がなければこれ）
///   Band  … 楽器ごと（キック・スネア・ハイハット・ベース・ボーカル・和音・パッド）
///   Drums … ドラムだけ（キック・スネア・ハイハット。ほかの楽器では光らない）
/// </summary>
public static class MusicRoleSets
{
    public const string Bands = "Bands";
    public const string Band = "Band";
    public const string Drums = "Drums";
    public static readonly string[] All = { Bands, Band, Drums };

    public static string Normalize(string? id) => id is Band or Drums ? id : Bands;

    public static string Label(string id) => id switch
    {
        Band => L("楽器ごと"),
        Drums => L("ドラムだけ"),
        _ => L("音の高さ（低音・中音・高音）"),
    };

    public static string Tip(string id) => id switch
    {
        Band => L("ファンはキック、メモリやキーボードはハイハット、グラボはボーカルのように、パーツごとに楽器を分けて光ります。ファンの端子がいくつもあれば、キック・スネア・ハイハット・ベースの順に分けます"),
        Drums => L("キック・スネア・ハイハットだけで光ります。ギターや歌では光りません"),
        _ => L("ファンは低音、グラボは中音、メモリは高音のように、音の高さで分けて光ります（これまでと同じ）"),
    };

    /// <summary>光り方「パーツごとの役割」を、全体・デバイス・ゾーンのどこかで使っているか（設定画面で「楽器の分け方」を出す）</summary>
    public static bool InUse(RgbConfig cfg) =>
        cfg.MusicStyle == MusicStyles.Role
        || cfg.DeviceSettings.Any(d => d.Effect == MusicStyles.Role || d.Zones.Any(z => z.Effect == MusicStyles.Role));

    /// <summary>「パーツに合わせる」で決めた音の高さの役割を、分け方に合わせた役割にする</summary>
    public static MusicRole Map(MusicRole natural, string? roleSet) => Normalize(roleSet) switch
    {
        Band => natural switch
        {
            MusicRole.Bass => MusicRole.Kick,
            MusicRole.Mid => MusicRole.Vocal,
            MusicRole.Treble => MusicRole.Hat,
            _ => MusicRole.Harmony,
        },
        Drums => natural switch
        {
            MusicRole.Mid => MusicRole.Snare,
            MusicRole.Treble => MusicRole.Hat,
            _ => MusicRole.Kick,
        },
        _ => natural,
    };

    /// <summary>同じ役割になったゾーンを振り分ける順（MusicSounds.Spread）</summary>
    public static MusicRole[] SpreadOrder(string? roleSet) => Normalize(roleSet) switch
    {
        Band => BandOrder,
        Drums => DrumOrder,
        _ => BandsOrder,
    };

    private static readonly MusicRole[] BandsOrder = { MusicRole.Bass, MusicRole.Mid, MusicRole.Treble };
    private static readonly MusicRole[] BandOrder =
        { MusicRole.Kick, MusicRole.Snare, MusicRole.Hat, MusicRole.BassLine, MusicRole.Vocal, MusicRole.Harmony, MusicRole.Pad };
    private static readonly MusicRole[] DrumOrder = { MusicRole.Kick, MusicRole.Snare, MusicRole.Hat };
}

/// <summary>ゾーンの設定「反応する音」の値と、パーツに合わせた初期値</summary>
public static class MusicSounds
{
    /// <summary>パーツに合わせて決める（ファンは低音、グラボは中音、メモリ・キーボードは高音 など。同じデバイスで重なれば Spread で振り分ける）</summary>
    public const string Auto = "Auto";
    public const string Bass = "Bass";
    public const string Mid = "Mid";
    public const string Treble = "Treble";
    public const string All = "All";
    public const string Kick = "Kick";
    public const string Snare = "Snare";
    public const string Hat = "Hat";
    public const string BassLine = "BassLine";
    public const string Vocal = "Vocal";
    public const string Harmony = "Harmony";
    public const string Pad = "Pad";
    public static readonly string[] Ids = { Auto, Bass, Mid, Treble, All, Kick, Snare, Hat, BassLine, Vocal, Harmony, Pad };

    public static string Label(string id) => id switch
    {
        Auto => L("パーツに合わせる"),
        Bass => L("低音（キック・ベース）"),
        Mid => L("中音（スネア・ボーカル）"),
        Treble => L("高音（ハイハット）"),
        All => L("音全体（音量）"),
        Kick => L("キック"),
        Snare => L("スネア"),
        Hat => L("ハイハット・シンバル"),
        BassLine => L("ベース（楽器）"),
        Vocal => L("ボーカル・リード"),
        Harmony => L("和音（鍵盤・ギター）"),
        Pad => L("パッド・シンセ"),
        _ => id,
    };

    public static string Normalize(string? id) => id is not null && Ids.Contains(id) ? id : Auto;

    /// <summary>
    /// 実際に反応する音。Auto なら、ケースの中の場所（決めてあれば）、なければデバイスの種類から決める:
    ///   ファン・CPU クーラー・マザーボードの端子 → 低音、グラボ・マザーボード本体の飾り → 中音、
    ///   メモリ・キーボード・マウス → 高音、テープ・そのほか → 音全体。
    /// 分け方（roleSet）が楽器ごと・ドラムだけなら、それを楽器の役割にする（MusicRoleSets.Map）
    /// </summary>
    public static MusicRole Resolve(string? id, RgbDevice device, string? zoneName, string? place, string? roleSet = MusicRoleSets.Bands)
    {
        switch (Normalize(id))
        {
            case Bass: return MusicRole.Bass;
            case Mid: return MusicRole.Mid;
            case Treble: return MusicRole.Treble;
            case All: return MusicRole.All;
            case Kick: return MusicRole.Kick;
            case Snare: return MusicRole.Snare;
            case Hat: return MusicRole.Hat;
            case BassLine: return MusicRole.BassLine;
            case Vocal: return MusicRole.Vocal;
            case Harmony: return MusicRole.Harmony;
            case Pad: return MusicRole.Pad;
        }
        return MusicRoleSets.Map(Natural(device, zoneName, place), roleSet);
    }

    /// <summary>パーツの種類・ケースの中の場所から決める、音の高さの役割</summary>
    private static MusicRole Natural(RgbDevice device, string? zoneName, string? place)
    {
        if (CaseLayout.Find(place) is { } p)
        {
            if (p.Group == L("ファン") || p.Id == "cpu") return MusicRole.Bass; // Group は表示の名前（L を通した値）
            if (p.Id.StartsWith("gpu", StringComparison.Ordinal) || p.Id.StartsWith("board", StringComparison.Ordinal)) return MusicRole.Mid;
            if (p.Id.StartsWith("ram", StringComparison.Ordinal)) return MusicRole.Treble;
            return MusicRole.All;
        }
        return device.Type switch
        {
            RgbDevice.TypeDram => MusicRole.Treble,
            RgbDevice.TypeGpu => MusicRole.Mid,
            RgbDevice.TypeKeyboard or RgbDevice.TypeMouse or RgbDevice.TypeMousemat => MusicRole.Treble,
            RgbDevice.TypeCooler => MusicRole.Bass,
            RgbDevice.TypeMotherboard => IsHeader(zoneName) ? MusicRole.Bass : MusicRole.Mid,
            _ => MusicRole.All,
        };
    }

    /// <summary>
    /// マザーボードのゾーンの名前が、ファンやテープをつなぐ端子か（OpenRGB での名前。ASRock「Addressable Header 1」・
    /// ASUS「Aura Addressable 1」・MSI「JRAINBOW1」・Gigabyte「D_LED1」など）
    /// </summary>
    private static bool IsHeader(string? zoneName) =>
        zoneName is not null && HeaderWords.Any(w => zoneName.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] HeaderWords = { "Header", "Addressable", "ARGB", "JRAINBOW", "JRGB", "D_LED" };

    /// <summary>
    /// 同じデバイスの中で「パーツに合わせる」のゾーンが 2 つ以上同じ音になったら、低音・中音・高音を順に割り当て直す
    /// （ファンの端子が 3 つあるマザーボードで、全部が低音になって同じ色・同じ光り方にならないように）。
    /// 順番は、もとの音から始めて、ゾーンの並び順（音全体は低音から）。LED が 1 個のゾーン（12V の RGB 端子など、
    /// 何もつないでいないことが多い）は後に回す。「反応する音」を自分で選んだゾーンは変えない。
    /// 分け方が楽器ごとなら キック・スネア・ハイハット・ベース・ボーカル・和音・パッド、ドラムだけなら キック・スネア・ハイハット の順
    /// </summary>
    /// <param name="natural">ゾーンごとの、Resolve で決めた音</param>
    /// <param name="auto">ゾーンの「反応する音」が「パーツに合わせる」か</param>
    /// <param name="leds">ゾーンごとの LED の数</param>
    /// <param name="roleSet">分け方（MusicRoleSets）</param>
    public static MusicRole[] Spread(IReadOnlyList<MusicRole> natural, IReadOnlyList<bool> auto, IReadOnlyList<int> leds, string? roleSet = MusicRoleSets.Bands)
    {
        var order = MusicRoleSets.SpreadOrder(roleSet);
        var roles = natural.ToArray();
        foreach (var role in Enum.GetValues<MusicRole>())
        {
            var group = Enumerable.Range(0, roles.Length).Where(k => auto[k] && natural[k] == role)
                .OrderBy(k => leds[k] <= 1 ? 1 : 0).ToList(); // OrderBy は並び順を保つ
            if (group.Count < 2) continue;
            int first = Math.Max(0, Array.IndexOf(order, role));
            for (int i = 0; i < group.Count; i++) roles[group[i]] = order[(first + i) % order.Length];
        }
        return roles;
    }
}

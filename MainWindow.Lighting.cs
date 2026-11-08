// ==================================================
// MainWindow.Lighting.cs - 「ライティング」画面 (OpenRGB の接続・光り方・音楽連動の設定・
//   いまの音のスペクトラムとビート・テンポの表示・同期テスト・デバイスごとの設定と LED のプレビュー)
// Dependencies: WPF / NAudio.Wasapi (MIT License), .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ProFanController.UI;
using ProFanController.UI.Controls;

namespace ProFanController;

/// <summary>ライティング画面の選択肢の種類（押されたときに、設定のどこを変えるか）</summary>
public enum LightingChoiceKind
{
    /// <summary>プリセット（MusicConfig.Preset）</summary>
    Preset,
    /// <summary>音楽の表現（RgbConfig.MusicStyle）</summary>
    Effect,
    /// <summary>強さ（MusicConfig.Intensity）</summary>
    Intensity,
    /// <summary>色の組み合わせ（MusicConfig.Palette）</summary>
    Palette,
    /// <summary>テンポの追跡（MusicConfig.TempoMode）</summary>
    Tempo,
    /// <summary>音が止まったときの光り方（MusicConfig.IdleEffect）</summary>
    Idle,
    /// <summary>色の候補（RgbConfig.StaticColor）</summary>
    Color,
    /// <summary>画面のアクセント色（AppearanceConfig.Accent）</summary>
    Accent,
    /// <summary>画面の背景色（AppearanceConfig.Background）</summary>
    Background,
    /// <summary>画面の文字色（AppearanceConfig.TextColor）</summary>
    TextColor,
}

/// <summary>コンボボックスの選択肢（Id = 設定に保存する値、Label = 画面に出す言葉）</summary>
public sealed record ChoiceOption(string Id, string Label);

/// <summary>
/// ライティング画面のチップ・カード 1 つ分（プリセット・音楽の表現・強さ・色の組み合わせ・テンポ・音が止まったとき・色の候補）。
/// IsSelected が true のものは、画面で色が付いて「選ばれている」と分かるようになる
/// </summary>
public sealed class LightingChoiceVm : ObservableObject
{
    private bool _selected;
    private bool _enabled = true;
    private Brush? _swatch;

    public required LightingChoiceKind Kind { get; init; }
    /// <summary>設定に保存する値（"Groove"・"Pulse"・"#6A00FF" など）</summary>
    public required string Id { get; init; }
    /// <summary>画面に出す名前</summary>
    public required string Label { get; init; }
    /// <summary>カードの下に出す説明（音楽の表現だけ）</summary>
    public string Description { get; init; } = "";
    /// <summary>マウスを乗せたときの説明</summary>
    public string? Tip { get; init; }
    /// <summary>注意書き（ストロボなど）</summary>
    public string Warning { get; init; } = "";
    public bool HasWarning => Warning.Length > 0;
    /// <summary>色の見本（パレットのグラデーション・色の候補の丸）</summary>
    public Brush? Swatch { get => _swatch; set => Set(ref _swatch, value); }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public bool IsEnabled { get => _enabled; set => Set(ref _enabled, value); }
}

/// <summary>
/// デバイスの中のゾーン 1 つ分（マザーボードの端子ごとなど）。光り方・左右対称・向き・色合わせを、
/// デバイスの設定とは別に選べる（「デバイスと同じ」ならデバイスの設定のまま）
/// </summary>
public sealed class RgbZoneVm : ObservableObject
{
    public const string SameId = "device";
    public static IReadOnlyList<ChoiceOption> MirrorChoices => _mirrorChoices.Value;
    private static readonly PerLanguage<IReadOnlyList<ChoiceOption>> _mirrorChoices = new(() => new ChoiceOption[]
    {
        new(SameId, L("デバイスと同じ")), new("on", L("オン")), new("off", L("オフ")),
    });
    public static IReadOnlyList<ChoiceOption> ReverseChoices => _reverseChoices.Value;
    private static readonly PerLanguage<IReadOnlyList<ChoiceOption>> _reverseChoices = new(() => new ChoiceOption[]
    {
        new(SameId, L("デバイスと同じ")), new("on", L("逆向き")), new("off", L("そのまま")),
    });

    public static IReadOnlyList<ChoiceOption> SoundChoices => _soundChoices.Value;
    private static readonly PerLanguage<IReadOnlyList<ChoiceOption>> _soundChoices =
        new(() => MusicSounds.Ids.Select(id => new ChoiceOption(id, MusicSounds.Label(id))).ToList());

    private string _effectId = RgbZoneSettings.SameAsDevice, _mirrorId = SameId, _reverseId = SameId, _soundId = MusicSounds.Auto;
    private int _gainR = 100, _gainG = 100, _gainB = 100, _level = 100;
    private bool _showEffect, _loading;

    /// <summary>OpenRGB のゾーン名（設定の保存にも使う）</summary>
    public required string Name { get; init; }
    public required int Leds { get; init; }
    public string LedText => L($"LED {Leds} 個");
    /// <summary>光り方の選択肢（先頭が「デバイスと同じ」）</summary>
    public required IReadOnlyList<ChoiceOption> EffectOptions { get; init; }
    public IReadOnlyList<ChoiceOption> MirrorOptions => MirrorChoices;
    public IReadOnlyList<ChoiceOption> ReverseOptions => ReverseChoices;
    public IReadOnlyList<ChoiceOption> SoundOptions => SoundChoices;
    /// <summary>光り方を表示する（音楽連動のときだけ）</summary>
    public bool ShowEffect { get => _showEffect; set => Set(ref _showEffect, value); }

    public string EffectId { get => _effectId; set { if (!string.IsNullOrEmpty(value) && Set(ref _effectId, value)) NotifyEdited(); } }
    public string MirrorId { get => _mirrorId; set { if (!string.IsNullOrEmpty(value) && Set(ref _mirrorId, value)) NotifyEdited(); } }
    public string ReverseId { get => _reverseId; set { if (!string.IsNullOrEmpty(value) && Set(ref _reverseId, value)) NotifyEdited(); } }
    /// <summary>反応する音（光り方「パーツごとの役割」で使う）</summary>
    public string SoundId { get => _soundId; set { if (!string.IsNullOrEmpty(value) && Set(ref _soundId, value)) NotifyEdited(); } }
    public int GainR { get => _gainR; set { if (Set(ref _gainR, Math.Clamp(value, 0, 100))) NotifyEdited(); } }
    public int GainG { get => _gainG; set { if (Set(ref _gainG, Math.Clamp(value, 0, 100))) NotifyEdited(); } }
    public int GainB { get => _gainB; set { if (Set(ref _gainB, Math.Clamp(value, 0, 100))) NotifyEdited(); } }
    public int Level { get => _level; set { if (Set(ref _level, Math.Clamp(value, 0, 100))) NotifyEdited(); } }

    /// <summary>利用者が値を変えたときに呼ぶ</summary>
    internal Action? Edited { get; set; }

    /// <summary>ケースの中の場所（「ケースの中の配置」の欄で選ぶ。ここでは保存のときに消さないよう持っておくだけ）</summary>
    internal string Place { get; set; } = "";

    /// <summary>設定に保存する形</summary>
    public RgbZoneSettings ToSettings() => new()
    {
        Name = Name,
        Effect = _effectId,
        Mirror = _mirrorId switch { "on" => true, "off" => false, _ => null },
        Reverse = _reverseId switch { "on" => true, "off" => false, _ => null },
        GainR = _gainR, GainG = _gainG, GainB = _gainB, Level = _level,
        Sound = _soundId,
        Place = Place,
    };

    /// <summary>設定から読んだ値を入れる（このときは Edited を呼ばない）</summary>
    internal void Load(RgbZoneSettings? z)
    {
        _loading = true;
        try
        {
            EffectId = z?.Effect is { Length: > 0 } e ? e : RgbZoneSettings.SameAsDevice;
            MirrorId = z?.Mirror switch { true => "on", false => "off", null => SameId };
            ReverseId = z?.Reverse switch { true => "on", false => "off", null => SameId };
            GainR = z?.GainR ?? 100;
            GainG = z?.GainG ?? 100;
            GainB = z?.GainB ?? 100;
            Level = z?.Level ?? 100;
            Place = z?.Place ?? "";
            SoundId = MusicSounds.Normalize(z?.Sound);
        }
        finally
        {
            _loading = false;
        }
    }

    private void NotifyEdited()
    {
        if (!_loading) Edited?.Invoke();
    }
}

/// <summary>
/// 「ケースの中の配置」の欄の 1 行（デバイスのゾーン 1 つ分。ゾーンが 1 つしかないデバイスも 1 行）。
/// 場所を選ぶと、そのゾーンの LED はケース全体での位置で光る
/// </summary>
public sealed class CasePlaceVm : ObservableObject
{
    public const string NoPlace = "";
    public static IReadOnlyList<ChoiceOption> PlaceChoices => _placeChoices.Value;
    private static readonly PerLanguage<IReadOnlyList<ChoiceOption>> _placeChoices = new(CreatePlaceChoices);

    private static List<ChoiceOption> CreatePlaceChoices()
    {
        var list = new List<ChoiceOption> { new(NoPlace, L("決めない（デバイスの中の並びで光る）")) };
        foreach (var p in CaseLayout.Places) list.Add(new ChoiceOption(p.Id, $"{p.Group}: {p.Label}"));
        return list;
    }

    private string _placeId = NoPlace;
    private bool _loading;

    public required string DeviceName { get; init; }
    public required string ZoneName { get; init; }
    /// <summary>デバイスの中での、このゾーンの最初の LED の番号</summary>
    public required int Start { get; init; }
    public required int Leds { get; init; }
    /// <summary>ゾーンが 1 つしかないデバイス（ゾーン名は出さない）</summary>
    public bool SingleZone { get; init; }
    public string Title => SingleZone ? DeviceName : L($"{DeviceName} ・ {ZoneName}");
    public string LedText => L($"LED {Leds} 個");
    public IReadOnlyList<ChoiceOption> PlaceOptions => PlaceChoices;

    public string PlaceId
    {
        get => _placeId;
        set { if (Set(ref _placeId, value ?? NoPlace) && !_loading) Edited?.Invoke(this); }
    }

    public CasePlace? Place => CaseLayout.Find(_placeId);

    internal Action<CasePlaceVm>? Edited { get; set; }

    internal void Load(string? place)
    {
        _loading = true;
        try { PlaceId = CaseLayout.Find(place) is null ? NoPlace : place!; }
        finally { _loading = false; }
    }
}

/// <summary>
/// ライティング画面のデバイス 1 台分（色を変えるかどうか・光り方の上書き・左右対称・逆向き・赤と緑の入れ替え・
/// 色合わせ・ゾーンごとの設定・プレビュー・状態）。利用者が変えた値は Edited で画面に知らせ、画面が設定（RgbConfig）に書き込む
/// </summary>
public sealed class RgbDeviceVm : ObservableObject
{
    /// <summary>「左右対称」の選択肢（設定では null = 自動、true = オン、false = オフ）</summary>
    public static IReadOnlyList<ChoiceOption> MirrorChoices => _mirrorChoices.Value;
    private static readonly PerLanguage<IReadOnlyList<ChoiceOption>> _mirrorChoices = new(() => new ChoiceOption[]
    {
        new("auto", L("自動（30 個以上で対称）")), new("on", L("オン")), new("off", L("オフ")),
    });
    /// <summary>「1 秒に送る回数」の選択肢（設定では 0 = 自動）</summary>
    public static IReadOnlyList<ChoiceOption> FpsChoices => _fpsChoices.Value;
    private static readonly PerLanguage<IReadOnlyList<ChoiceOption>> _fpsChoices = new(() => new ChoiceOption[]
    {
        new("0", L("自動")), new("4", L("4 回")), new("8", L("8 回")), new("12", L("12 回")), new("15", L("15 回")),
        new("20", L("20 回")), new("30", L("30 回")), new("60", L("60 回")),
    });
    /// <summary>グラボの「1 秒に送る回数」の選択肢（グラボは自動の回数より上げられない。下げるのは可）</summary>
    public static IReadOnlyList<ChoiceOption> GpuFpsChoices => _gpuFpsChoices.Value;
    private static readonly PerLanguage<IReadOnlyList<ChoiceOption>> _gpuFpsChoices = new(() => new ChoiceOption[]
    {
        new("0", L("自動")), new("1", L("1 回")), new("2", L("2 回")), new("3", L("3 回")), new("4", L("4 回")), new("5", L("5 回")),
    });
    /// <summary>「早めに光らせる」（デバイスの遅れ）の選択肢（ミリ秒。設定では -1 = 自動）</summary>
    public static IReadOnlyList<ChoiceOption> LeadChoices => _leadChoices.Value;
    private static readonly PerLanguage<IReadOnlyList<ChoiceOption>> _leadChoices = new(() => new ChoiceOption[]
    {
        new("-1", L("自動")), new("0", L("0 ミリ秒")), new("20", L("20 ミリ秒")), new("40", L("40 ミリ秒")), new("60", L("60 ミリ秒")),
        new("80", L("80 ミリ秒")), new("100", L("100 ミリ秒")), new("150", L("150 ミリ秒")), new("200", L("200 ミリ秒")),
    });
    private static IReadOnlyList<ChoiceOption> FastEffects => _fastEffects.Value;
    private static readonly PerLanguage<IReadOnlyList<ChoiceOption>> _fastEffects = new(() => CreateEffectOptions(slow: false));
    // 更新が遅いデバイスでは、ストロボは使わない（一部しか書き換わらずチカチカするため）
    private static IReadOnlyList<ChoiceOption> SlowEffects => _slowEffects.Value;
    private static readonly PerLanguage<IReadOnlyList<ChoiceOption>> _slowEffects = new(() => CreateEffectOptions(slow: true));

    private bool _selected, _canSelect, _showEffect, _swap, _reverse, _animateOnGpu, _showDetails;
    private int _gainR = 100, _gainG = 100, _gainB = 100, _level = 100;
    private string _effect = "Auto", _mirrorId = "auto", _fpsId = "0", _leadId = "-1", _statusText = "", _errorText = "";
    private bool _loading;

    /// <summary>OpenRGB のデバイス名（設定の保存にも使う）</summary>
    public required string Name { get; init; }
    public required int Leds { get; init; }
    /// <summary>更新が遅いデバイス（GPU・メモリなど I2C のもの）</summary>
    public bool IsSlow { get; init; }
    /// <summary>色を送るたびにちかちかしやすいデバイス（ASRock の Radeon グラボ）</summary>
    public bool IsFlickerProne { get; init; }
    public string LedText => L($"LED {Leds} 個");
    /// <summary>更新が遅いデバイスの目印の文字</summary>
    public string SlowBadgeText => IsFlickerProne ? L("ちかちか対策中（色が変わったときだけ送る）")
        : IsGpu ? L($"グラボ: 書き込みを最小限に（1 秒に最大 {(MaxFps > 0 ? Math.Min(MaxFps, AutoFps) : AutoFps)} 回）")
        : L($"更新が遅いデバイス（1 秒に {(MaxFps > 0 ? MaxFps : AutoFps)} 回{(MaxFps > 0 ? "" : L("・自動"))}）");
    /// <summary>「自動」のときの 1 秒に送る回数（グラボ 5・メモリなど 8）</summary>
    public int AutoFps { get; init; } = 8;
    /// <summary>グラボ（書き込みに時間がかかる・ちかちかしやすい）</summary>
    public bool IsGpu { get; init; }
    /// <summary>「1 秒に送る回数」を選べる（更新が遅いデバイスだけ）</summary>
    public bool ShowFps => IsSlow;
    /// <summary>グラボは下げる方向だけ選べる（自動の回数まで。ちかちかしやすいグラボは 4 回まで）</summary>
    public IReadOnlyList<ChoiceOption> FpsOptions => !IsGpu ? FpsChoices
        : _gpuFps ??= GpuFpsChoices.Where(c => int.Parse(c.Id, System.Globalization.CultureInfo.InvariantCulture) <= AutoFps).ToList();
    private IReadOnlyList<ChoiceOption>? _gpuFps;
    /// <summary>「虹・呼吸も動かす」を選べる（ちかちかしやすい ASRock の Radeon 以外のグラボ）</summary>
    public bool ShowAnimateOnGpu => IsGpu && !IsFlickerProne;

    /// <summary>グラボでもレインボー（流れる）・呼吸を動かす（既定はオフ: 止まった虹・固定色）</summary>
    public bool AnimateOnGpu
    {
        get => _animateOnGpu;
        set { if (Set(ref _animateOnGpu, value)) NotifyEdited(); }
    }

    /// <summary>1 秒に送る回数の上限: "0"（自動）/ "4" / "8" …</summary>
    public string FpsId
    {
        get => _fpsId;
        set
        {
            if (string.IsNullOrEmpty(value) || !Set(ref _fpsId, value)) return;
            Raise(nameof(SlowBadgeText));
            NotifyEdited();
        }
    }

    public IReadOnlyList<ChoiceOption> LeadOptions => LeadChoices;

    /// <summary>早めに光らせる量（デバイスの遅れ。ミリ秒）: "-1"（自動）/ "0" / "20" …</summary>
    public string LeadId
    {
        get => _leadId;
        set { if (!string.IsNullOrEmpty(value) && Set(ref _leadId, value)) NotifyEdited(); }
    }

    /// <summary>設定に保存する形の「デバイスの遅れ」（-1 = 自動）</summary>
    public int LatencyMs => int.TryParse(_leadId, out int v) ? v : -1;

    /// <summary>設定に保存する形の「1 秒に送る回数」（0 = 自動）</summary>
    public int MaxFps => int.TryParse(_fpsId, out int v) ? v : 0;
    public string SlowBadgeTip => IsFlickerProne
        ? L("このグラボは、色を書き込むたびに一瞬ちかちかすることがあります（OpenRGB 1.1 の書き込み方が原因と考えられますが、この機種では確認中です）。書き込みを最小限にするため、明るさは変えず、色がはっきり変わったときだけ（0.25 秒以上あけて）送り、同じ色の送り直しもしません。レインボー（流れる）は止まった虹に、呼吸は固定色になります（光る場所ごとに 1 色しか出せないため）。音楽連動で音が止まったときは、色を 20 秒ごとに切り替えます。気になる場合は光り方を「固定色」にしてください")
        : IsGpu
            ? L("グラボの LED は、OpenRGB が 1 個ずつ書き込むため、1 回の色替えに 20〜200 ミリ秒ほどかかります。書き込みが続くと、ちかちかしたり遅れたりするので、書き込みを最小限にしています: ")
              + L("明るさは一定（色の切り替えでビートを見せる）、色がはっきり変わったときだけ送る（1 秒に最大 5 回）、温度モードは温度の段が変わったときだけ切り替える、")
              + L("レインボー（流れる）・呼吸は止まった虹・固定色（「虹・呼吸も動かす」で変えられます）、同じ色の送り直しは 60 秒ごと。")
              + L("ほかのデバイスとタイミングがずれるときは「早めに光らせる」で合わせてください")
            : L("メモリなど、基板の内側の配線（I2C）でつながる LED は、1 回送るのに時間がかかるため、自動では 1 秒に 8 回までにしています（右の「1 秒に送る回数」で変えられます）。音楽連動の色の切り替えは定期便を待たずにすぐ送ります。同じ色の送り直しは 60 秒ごとです");
    public IReadOnlyList<ChoiceOption> EffectOptions => IsSlow ? SlowEffects : FastEffects;
    public IReadOnlyList<ChoiceOption> MirrorOptions => MirrorChoices;

    /// <summary>色を変える対象（「すべてのデバイス」のときは常に true）</summary>
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    /// <summary>チェックを変えられる（「すべてのデバイス」がオフのときだけ）</summary>
    public bool CanSelect { get => _canSelect; set => Set(ref _canSelect, value); }
    /// <summary>光り方の上書きを表示する（音楽連動のときだけ）</summary>
    public bool ShowEffect
    {
        get => _showEffect;
        set
        {
            if (!Set(ref _showEffect, value)) return;
            foreach (var z in Zones) z.ShowEffect = value;
        }
    }

    /// <summary>「ゾーンと色合わせ」を開いている</summary>
    public bool ShowDetails { get => _showDetails; set => Set(ref _showDetails, value); }

    /// <summary>色合わせ: 赤・緑・青の強さと、このデバイスだけの明るさ（0〜100%）</summary>
    public int GainR { get => _gainR; set { if (Set(ref _gainR, Math.Clamp(value, 0, 100))) NotifyEdited(); } }
    public int GainG { get => _gainG; set { if (Set(ref _gainG, Math.Clamp(value, 0, 100))) NotifyEdited(); } }
    public int GainB { get => _gainB; set { if (Set(ref _gainB, Math.Clamp(value, 0, 100))) NotifyEdited(); } }
    public int Level { get => _level; set { if (Set(ref _level, Math.Clamp(value, 0, 100))) NotifyEdited(); } }

    /// <summary>ゾーン（LED のあるゾーンが 2 つ以上のときだけ入れる）</summary>
    public ObservableCollection<RgbZoneVm> Zones { get; } = new();
    public bool HasZones => Zones.Count > 0;

    /// <summary>ゾーンの行を作る（設定から値を読み込む）</summary>
    internal void LoadZones(RgbDevice device, RgbDeviceSettings settings)
    {
        Zones.Clear();
        var zones = device.Zones;
        if (zones is not null && zones.Count(z => z > 0) >= 2)
        {
            var options = new List<ChoiceOption> { new(RgbZoneSettings.SameAsDevice, L("デバイスと同じ")) };
            options.AddRange(EffectOptions);
            for (int k = 0; k < zones.Count; k++)
            {
                if (zones[k] <= 0) continue;
                string name = device.ZoneName(k);
                var z = new RgbZoneVm { Name = name, Leds = zones[k], EffectOptions = options, ShowEffect = _showEffect };
                z.Load(settings.ZoneFor(name));
                z.Edited = () => NotifyEdited(nameof(Zones));
                Zones.Add(z);
            }
        }
        Raise(nameof(HasZones));
    }

    /// <summary>色合わせを設定から読み込む（このときは Edited を呼ばない）</summary>
    internal void LoadCalibration(RgbDeviceSettings s)
    {
        _loading = true;
        try
        {
            GainR = s.GainR;
            GainG = s.GainG;
            GainB = s.GainB;
            Level = s.Level;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>色合わせとゾーンの設定を、設定の項目に書き込む</summary>
    internal void WriteCalibration(RgbDeviceSettings s)
    {
        s.GainR = _gainR;
        s.GainG = _gainG;
        s.GainB = _gainB;
        s.Level = _level;
        // 画面に出ていないゾーン（いま見えていない）の設定は残す
        var shown = Zones.Select(z => z.Name).ToHashSet();
        var keep = (s.Zones ?? new()).Where(z => !shown.Contains(z.Name)).ToList();
        keep.AddRange(Zones.Select(z => z.ToSettings()).Where(z => !z.IsDefault));
        s.Zones = keep;
    }

    public bool SwapRedGreen
    {
        get => _swap;
        set { if (Set(ref _swap, value)) NotifyEdited(); }
    }

    /// <summary>音楽連動の光り方の上書き（"Auto" なら全体の設定）</summary>
    public string Effect
    {
        get => _effect;
        // 一覧から外れるときに ComboBox が null を書き戻すことがあるので、空の値は無視する
        set { if (!string.IsNullOrEmpty(value) && Set(ref _effect, value)) NotifyEdited(); }
    }

    /// <summary>左右対称: "auto" / "on" / "off"</summary>
    public string MirrorId
    {
        get => _mirrorId;
        set { if (!string.IsNullOrEmpty(value) && Set(ref _mirrorId, value)) NotifyEdited(); }
    }

    /// <summary>設定に保存する形の「左右対称」（null = 自動）</summary>
    public bool? Mirror => _mirrorId switch { "on" => true, "off" => false, _ => null };

    public bool Reverse
    {
        get => _reverse;
        set { if (Set(ref _reverse, value)) NotifyEdited(); }
    }

    /// <summary>送っている回数といまの光り方（例: "60 fps ・ パルス"）</summary>
    public string StatusText { get => _statusText; set => Set(ref _statusText, value ?? ""); }

    /// <summary>このデバイスへの送信で起きた最後のエラー（なければ空）</summary>
    public string ErrorText
    {
        get => _errorText;
        set { if (Set(ref _errorText, value ?? "")) Raise(nameof(HasError)); }
    }

    public bool HasError => _errorText.Length > 0;

    /// <summary>画面に出ている LED のプレビュー（LedPreview の Loaded で設定し、Unloaded で外す）</summary>
    internal LedPreview? Preview { get; set; }

    /// <summary>利用者が値を変えたときに呼ぶ（引数は変わったプロパティの名前）</summary>
    internal Action<RgbDeviceVm, string>? Edited { get; set; }

    /// <summary>設定から読んだ値を入れる（このときは Edited を呼ばない）</summary>
    internal void Load(bool swapRedGreen, string? effect, bool? mirror, bool reverse, int maxFps = 0, int latencyMs = -1, bool animateOnGpu = false)
    {
        _loading = true;
        try
        {
            AnimateOnGpu = animateOnGpu;
            LeadId = latencyMs >= 0 ? latencyMs.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-1";
            SwapRedGreen = swapRedGreen;
            // グラボで自動の回数より多い値（以前の版で選べた 8〜60 回）は、自動と同じなので「自動」と表示する
            // （一覧にない値を入れると、選択欄が空になるため）
            if (IsGpu && maxFps > AutoFps) maxFps = 0;
            FpsId = maxFps > 0 ? maxFps.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
            Effect = string.IsNullOrEmpty(effect) ? "Auto" : effect;
            MirrorId = mirror switch { true => "on", false => "off", null => "auto" };
            Reverse = reverse;
        }
        finally
        {
            _loading = false;
        }
    }

    private void NotifyEdited([CallerMemberName] string? name = null)
    {
        if (!_loading) Edited?.Invoke(this, name ?? "");
    }

    private static IReadOnlyList<ChoiceOption> CreateEffectOptions(bool slow)
    {
        var list = new List<ChoiceOption> { new("Auto", L("自動（全体の設定）")) };
        foreach (var style in MusicStyles.All)
        {
            if (slow && style == MusicStyles.Strobe) continue;
            string label = MusicTuning.Describe(style).Label;
            list.Add(new(style, style == MusicStyles.Strobe ? label + L("（激しい点滅）") : label));
        }
        return list;
    }
}

/// <summary>
/// ライティング画面の決まりごと（画面の部品を使わない計算だけを集めたもの）。
/// 色の入力の確認・デバイスの設定の書き込み・どの設定を表示するか、など
/// </summary>
internal static class LightingRules
{
    // 末尾は \z（.NET の $ は最後の改行の前でも一致してしまうため）
    private static readonly Regex HexPattern = new(@"^#[0-9A-Fa-f]{6}\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// 色の組み合わせ（パレット）ごとの色（画面の見本用。LED 側の Palette.cs と同じ色）。
    /// レインボーは輪になっているので、最初の色を最後にもう一度入れて 1 周を表す
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> PaletteStops = new Dictionary<string, string[]>
    {
        [Palettes.Rainbow] = new[] { "#FF0000", "#FFFF00", "#00FF00", "#00FFFF", "#0000FF", "#FF00FF", "#FF0000" },
        [Palettes.Neon] = new[] { "#FF0080", "#7C3AED", "#00E5FF", "#00FF85" },
        [Palettes.Sunset] = new[] { "#FF3D00", "#FF8A00", "#FFC400", "#FF006E" },
        [Palettes.Ocean] = new[] { "#0033FF", "#0091FF", "#00E5D1", "#7CFFCB" },
        [Palettes.Fire] = new[] { "#FF0000", "#FF4500", "#FF9F00", "#FFD000" },
        [Palettes.Vaporwave] = new[] { "#FF71CE", "#01CDFE", "#05FFA1", "#B967FF" },
        [Palettes.Forest] = new[] { "#00FF41", "#7CFC00", "#00B36B", "#2EFFC7" },
        [Palettes.Ice] = new[] { "#A0E9FF", "#00B4FF", "#5E60FF" },
    };

    /// <summary>"#RRGGBB" の形なら true</summary>
    public static bool IsValidHex(string? text) => text is not null && HexPattern.IsMatch(text);

    /// <summary>
    /// 入力された色を "#RRGGBB"（大文字）にそろえる。先頭の # がなくても、6 桁の 16 進数なら受け付ける。
    /// それ以外（桁が足りない・0〜9 と A〜F 以外の文字がある）は null
    /// </summary>
    public static string? NormalizeHex(string? text)
    {
        string s = (text ?? "").Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        return IsValidHex(s) ? s.ToUpperInvariant() : null;
    }

    /// <summary>
    /// 名前の一覧に、いま見えているデバイスの選択を反映する。
    /// いま接続していないデバイスの名前は消さずに残す（つなぎ直したときに設定が元どおりになるように）
    /// </summary>
    /// <param name="current">いまの設定の一覧</param>
    /// <param name="present">いま見えているデバイスの名前</param>
    /// <param name="chosen">見えているデバイスのうち、選ばれているものの名前</param>
    public static List<string> MergeNames(IEnumerable<string> current, IReadOnlyCollection<string> present, IEnumerable<string> chosen)
    {
        var result = new List<string>();
        foreach (var n in current)
        {
            if (!string.IsNullOrEmpty(n) && !present.Contains(n) && !result.Contains(n)) result.Add(n);
        }
        foreach (var n in chosen)
        {
            if (!string.IsNullOrEmpty(n) && !result.Contains(n)) result.Add(n);
        }
        return result;
    }

    /// <summary>既定のままのデバイス設定か（それなら一覧に入れておく必要はない）</summary>
    public static bool IsDefault(RgbDeviceSettings s)
        => (string.IsNullOrEmpty(s.Effect) || s.Effect == "Auto") && s.Mirror is null && !s.Reverse && s.MaxFps <= 0 && s.LatencyMs < 0
           && !s.WhiteOnly && !s.AnimateOnGpu && s.GainR == 100 && s.GainG == 100 && s.GainB == 100 && s.Level == 100
           && (s.Zones is null || s.Zones.All(z => z.IsDefault));

    /// <summary>
    /// デバイスの設定を一覧に書き込む。既定と違うときだけ項目を作り、すべて既定に戻ったら項目を消す。
    /// この画面では変えない値（遅れ）は、そのまま残す。maxFps が負なら、1 秒に送る回数の上限もそのまま
    /// </summary>
    /// <summary>デバイスのゾーンの、ケースの中の場所を設定する（空なら外す。何も設定が残らなければ項目ごと消す）</summary>
    public static void SetZonePlace(List<RgbDeviceSettings> list, string device, string zone, string? place)
    {
        place ??= "";
        var s = list.FirstOrDefault(d => d.Name == device);
        if (s is null)
        {
            if (place.Length == 0) return;
            s = new RgbDeviceSettings { Name = device };
            list.Add(s);
        }
        s.Zones ??= new();
        var z = s.Zones.FirstOrDefault(x => x.Name == zone);
        if (z is null)
        {
            if (place.Length == 0) return;
            z = new RgbZoneSettings { Name = zone };
            s.Zones.Add(z);
        }
        z.Place = place;
        if (z.IsDefault) s.Zones.Remove(z);
        if (IsDefault(s)) list.Remove(s);
    }

    public static void UpsertDeviceSettings(List<RgbDeviceSettings> list, string name, string effect, bool? mirror, bool reverse, int maxFps = -1,
        int? latencyMs = null, bool? animateOnGpu = null, Action<RgbDeviceSettings>? apply = null)
    {
        RgbDeviceSettings? entry = null;
        foreach (var d in list)
        {
            if (d.Name == name) { entry = d; break; }
        }
        var s = entry ?? new RgbDeviceSettings { Name = name };
        s.Effect = string.IsNullOrEmpty(effect) ? "Auto" : effect;
        s.Mirror = mirror;
        s.Reverse = reverse;
        if (maxFps >= 0) s.MaxFps = maxFps;
        if (latencyMs is int lat) s.LatencyMs = lat < 0 ? -1 : lat;
        if (animateOnGpu is bool anim) s.AnimateOnGpu = anim;
        apply?.Invoke(s);
        if (IsDefault(s))
        {
            if (entry is not null) list.Remove(entry);
        }
        else if (entry is null)
        {
            list.Add(s);
        }
    }

    /// <summary>出力先の名前から、Bluetooth など音が遅れて届く機器らしいかを判断する</summary>
    public static bool LooksLikeBluetooth(string? endpointName)
    {
        if (string.IsNullOrEmpty(endpointName)) return false;
        foreach (var word in new[] { "Bluetooth", "Hands-Free", "AirPods", "Buds" })
        {
            if (endpointName.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// プリセットが書き換える値か。これを手で変えたら、プリセットは「カスタム」になる。
    /// 感度・音の遅れの補正など「使う環境に合わせる値」は、変えてもプリセットのまま
    /// </summary>
    public static bool IsPresetProperty(object? sender, string? property) => sender switch
    {
        RgbConfig => property == nameof(RgbConfig.MusicStyle),
        MusicConfig => property is nameof(MusicConfig.Intensity) or nameof(MusicConfig.Palette) or nameof(MusicConfig.ReactionMs)
            or nameof(MusicConfig.SmoothnessMs) or nameof(MusicConfig.Punch) or nameof(MusicConfig.MinBrightness),
        _ => false,
    };

    /// <summary>テンポの倍率を 1 段上げる（0.5 → 1 → 2）</summary>
    public static double TempoUp(double factor) => factor < 0.75 ? 1.0 : 2.0;

    /// <summary>テンポの倍率を 1 段下げる（2 → 1 → 0.5）</summary>
    public static double TempoDown(double factor) => factor > 1.5 ? 1.0 : 0.5;

    /// <summary>その光り方で「メリハリ」が効くか（ムード・音量メーターは音量だけを使うので効かない）</summary>
    public static bool UsesPunch(string effect) => effect is not (MusicStyles.Mood or MusicStyles.Meter);

    /// <summary>その光り方でテンポを使うか（音量メーターは使わない）</summary>
    public static bool UsesTempo(string effect) => effect != MusicStyles.Meter;

    /// <summary>その光り方で「流れる速さ」を使うか</summary>
    public static bool UsesSpeed(string effect) => effect == MusicStyles.Scroll;

    /// <summary>「色」（StaticColor）を使う設定か（固定色・呼吸・パレット「選んだ色」・止まったとき／止めるときの固定色）</summary>
    public static bool UsesStaticColor(RgbConfig cfg)
    {
        var m = cfg.Music ?? new MusicConfig();
        return cfg.Mode is RgbModes.Static or RgbModes.Breathing
            || (cfg.Mode == RgbModes.Music && (Palettes.UsesStaticColor(m.Palette) || m.IdleEffect is IdleEffects.Static or IdleEffects.Breathing))
            || cfg.OnStop == RgbStopBehaviors.Static;
    }

    /// <summary>
    /// パレット「選んだ色」の見本の 3 色（選んだ色と、色相を ±25° ずらした色）。
    /// LED 側は OKLCh で作るが、見本は HSV で近い色を作れば十分
    /// </summary>
    public static string[] MonoStops(string? staticColor)
    {
        var c = Rgb.Parse(staticColor);
        var (h, s, v) = ToHsv(c);
        return new[]
        {
            Rgb.FromHsv(h - 25, s, v * 0.82).Hex,
            c.Hex,
            Rgb.FromHsv(h + 25, s * 0.8, v).Hex,
        };
    }

    /// <summary>色を色相（0〜360）・彩度（0〜1）・明度（0〜1）に分ける</summary>
    public static (double H, double S, double V) ToHsv(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * (((g - b) / d) % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }
}

public partial class MainWindow
{
    /// <summary>色の候補（固定色・呼吸・パレット「選んだ色」の色）と、その名前</summary>
    private static (string Hex, string Name)[] SwatchColors => _swatchColors.Value;
    private static readonly PerLanguage<(string Hex, string Name)[]> _swatchColors = new(() => new[]
    {
        ("#6A00FF", L("紫")), ("#0040FF", L("青")), ("#00B4FF", L("水色")), ("#00FF50", L("緑")),
        ("#FFB000", L("オレンジ")), ("#FF0000", L("赤")), ("#FF0080", L("ピンク")), ("#FFFFFF", L("白")),
    });

    /// <summary>「止めるときの LED」の選択肢</summary>
    private static IReadOnlyList<ChoiceOption> StopOptions => _stopOptions.Value;
    private static readonly PerLanguage<IReadOnlyList<ChoiceOption>> _stopOptions = new(() => new ChoiceOption[]
    {
        new(RgbStopBehaviors.Off, L("消灯（LED を消す）")),
        new(RgbStopBehaviors.Static, L("固定色（「色」で選んだ色にする）")),
        new(RgbStopBehaviors.Keep, L("そのまま（最後の色で止まる）")),
    });

    /// <summary>画面の動きの更新は最大 1 秒に 60 回（秒）</summary>
    private const double LiveFrameSec = 1.0 / 60;
    /// <summary>LED のプレビューの更新は 1 秒に 30 回（秒）</summary>
    private const double LivePreviewSec = 1.0 / 30;
    /// <summary>文字の表示（テンポ・音量・接続の状態）の更新は 1 秒に 4 回（秒）</summary>
    private const double LiveTextSec = 0.25;
    /// <summary>ビートのランプが消えていく速さ（時定数、秒）</summary>
    private const double LampTauSec = 0.15;
    /// <summary>「接続テスト」などの結果を、状態の表示より優先して出しておく時間（秒）</summary>
    private const double RgbMessageSec = 12;
    /// <summary>Bluetooth の案内を出すのは、音の遅れの補正がこれより小さいとき（ミリ秒）</summary>
    private const double BluetoothHintBelowMs = 150;

    private readonly List<LightingChoiceVm> _presetChoices = new();
    private readonly List<LightingChoiceVm> _effectChoices = new();
    private readonly List<LightingChoiceVm> _intensityChoices = new();
    private readonly List<LightingChoiceVm> _paletteChoices = new();
    private readonly List<LightingChoiceVm> _tempoChoices = new();
    private readonly List<LightingChoiceVm> _idleChoices = new();
    private readonly List<LightingChoiceVm> _colorChoices = new();
    private readonly ObservableCollection<RgbDeviceVm> _rgbDeviceRows = new();

    /// <summary>いま分かっている OpenRGB のデバイス（接続中の一覧か、「接続テスト」の結果）</summary>
    private IReadOnlyList<RgbDevice> _rgbKnownDevices = Array.Empty<RgbDevice>();
    /// <summary>最後に見た RgbSync.Devices（入れ替わったら一覧を作り直す）</summary>
    private IReadOnlyList<RgbDevice>? _rgbSyncDevicesSeen;
    private RgbConfig? _litRgb;
    private MusicConfig? _litMusic;
    /// <summary>プリセットを当てている間（このときの変更では「カスタム」にしない）</summary>
    private bool _applyingPreset;
    /// <summary>ライティングの元に戻す・やり直す（ファンの設定の履歴とは別。InitLighting で作る）</summary>
    private LightingHistory? _lightHistory;
    /// <summary>色の入力欄の文字をコードから書き換えている間（TextChanged を無視する）</summary>
    private bool _hexSyncing;
    private string _monoSwatchFor = "";
    private string _colorDotFor = "";
    private (byte R, byte G, byte B, bool On) _rgbSwatchState;
    private string? _rgbMessage;
    private double _rgbMessageUntil;

    // ----- いまの音・プレビュー（画面が見えている間だけ動かす） -----
    private bool _lightingLive;
    private double _liveLastT, _livePreviewT, _liveTextT;
    private readonly AudioFrame _liveFrame = new();
    private bool _liveHasFrame;
    private readonly List<AudioEvent> _liveEvents = new();
    private AudioEngine? _liveEngine;
    private long _liveLastEventId;
    private double _kickGlow, _clickGlow;
    private double _liveErrorLoggedAt = double.NegativeInfinity;
    /// <summary>同期テスト中</summary>
    private bool _syncTestOn;

    /// <summary>いまの LED の設定（タスクトレイのメニューで、選ばれている光り方にチェックを付けるため）</summary>
    public RgbConfig LightingConfig => Config.Rgb;

    /// <summary>ライティング画面の準備（コンストラクターから 1 回だけ呼ぶ）</summary>
    private void InitLighting()
    {
        LightingPage.DataContext = Config;
        BuildLightingChoices();
        PresetChoices.ItemsSource = _presetChoices;
        EffectChoices.ItemsSource = _effectChoices;
        IntensityChoices.ItemsSource = _intensityChoices;
        PaletteChoices.ItemsSource = _paletteChoices;
        TempoChoices.ItemsSource = _tempoChoices;
        IdleChoices.ItemsSource = _idleChoices;
        ColorSwatches.ItemsSource = _colorChoices;
        OnStopCombo.ItemsSource = StopOptions;
        RgbDevices.ItemsSource = _rgbDeviceRows;
        CasePlaceList.ItemsSource = _casePlaces;
        CaseDirectionCombo.ItemsSource = CaseDirections.All.Select(d => new ChoiceOption(d.Id, d.Label)).ToList();

        WatchLightingConfig();
        _lightHistory = new LightingHistory(Config.Rgb);
        _rgbKnownDevices = CurrentApp.Rgb.Devices;
        RebuildRgbDeviceRows();
        RefreshLightingUi();

        // 見えている間だけ動かす（ほかの画面へ移ったとき・タスクトレイに隠したときは止める）
        LightingPage.IsVisibleChanged += OnLightingPageVisibleChanged;
        // 元に戻す・バックアップから戻すなどで設定が丸ごと入れ替わったとき
        _controller.ConfigReplaced += OnLightingConfigReplaced;
    }

    /// <summary>チップ・カードの選択肢を作る（説明文もここで作る）</summary>
    private void BuildLightingChoices()
    {
        foreach (var id in MusicPresets.All)
            _presetChoices.Add(new LightingChoiceVm { Kind = LightingChoiceKind.Preset, Id = id, Label = MusicPresets.Label(id), Tip = PresetTip(id) });

        foreach (var id in MusicStyles.All)
        {
            var (label, description) = MusicTuning.Describe(id);
            bool strobe = id == MusicStyles.Strobe;
            _effectChoices.Add(new LightingChoiceVm
            {
                Kind = LightingChoiceKind.Effect, Id = id, Label = label, Description = description,
                Tip = strobe
                    ? description + L("\n光に敏感な方は使わないでください。「激しい点滅を抑える」がオンのあいだは選べません")
                    : description,
                Warning = strobe ? L("⚠ 激しく点滅します") : "",
            });
        }

        foreach (var id in Intensities.All)
            _intensityChoices.Add(new LightingChoiceVm { Kind = LightingChoiceKind.Intensity, Id = id, Label = Intensities.Label(id), Tip = IntensityTip(id) });

        foreach (var id in Palettes.All)
        {
            _paletteChoices.Add(new LightingChoiceVm
            {
                Kind = LightingChoiceKind.Palette, Id = id, Label = Palettes.Label(id),
                Tip = id == Palettes.Solid
                    ? L("「色と明るさ」で選んだ色だけで光ります。色は変わらず、音に合わせて明るさだけが変わります")
                    : id == Palettes.Mono
                    ? L("「色と明るさ」で選んだ色と、その両隣の色で光ります")
                    : L($"{Palettes.Label(id)}の色で光ります。ビートのたびに、この中の次の色へ切り替わります"),
                Swatch = LightingRules.PaletteStops.TryGetValue(id, out var stops) ? GradientBrush(stops) : null,
            });
        }

        foreach (var (id, label, tip) in new[]
        {
            (TempoModes.Auto, L("自動（先読み）"), L("曲のテンポをつかんだら、次のビートを先読みして遅れなく光らせます（おすすめ）")),
            (TempoModes.KickOnly, L("キックだけ"), L("先読みはせず、バスドラムが鳴ったときだけ光ります。テンポが変わる曲向け")),
            (TempoModes.Off, L("使わない"), L("テンポもビートも使わず、音の大きさだけで光ります")),
        })
        {
            _tempoChoices.Add(new LightingChoiceVm { Kind = LightingChoiceKind.Tempo, Id = id, Label = label, Tip = tip });
        }

        foreach (var id in IdleEffects.All)
        {
            string tip = id switch
            {
                IdleEffects.Mood => L("パレットの色で、ゆっくり色が移り変わります"),
                IdleEffects.Breathing => L("「色」で選んだ色が、ゆっくり明るくなったり暗くなったりします"),
                IdleEffects.Static => L("「色」で選んだ色で光り続けます"),
                IdleEffects.Off => L("音が止まったら LED を消します"),
                _ => IdleEffects.Label(id),
            };
            _idleChoices.Add(new LightingChoiceVm { Kind = LightingChoiceKind.Idle, Id = id, Label = IdleEffects.Label(id), Tip = tip });
        }

        foreach (var (hex, name) in SwatchColors)
            _colorChoices.Add(new LightingChoiceVm { Kind = LightingChoiceKind.Color, Id = hex, Label = name, Tip = L($"{name}（{hex}）"), Swatch = SolidBrush(hex) });
    }

    /// <summary>プリセットの説明（中身は実際に当てて調べるので、MusicTuning の表とずれない）</summary>
    private static string PresetTip(string id)
    {
        if (id == MusicPresets.Custom) return L("下の設定を自分で調整した状態です。ほかのプリセットを選ぶと、その組み合わせに戻ります");
        var sample = new RgbConfig();
        MusicTuning.ApplyPreset(sample, id);
        string scene = id switch
        {
            MusicPresets.Chill => L("作業中や夜に。ゆったりと色が移り変わります"),
            MusicPresets.Groove => L("迷ったらこれ。低音に合わせて気持ちよく脈打ちます"),
            MusicPresets.Party => L("ドラムのたびに光の輪が広がる、にぎやかな光り方です"),
            MusicPresets.Club => L("ビートごとに強く色が切り替わる、いちばん激しい光り方です"),
            MusicPresets.Visualizer => L("LED の並びに低音〜高音を並べて、音を目で見る光り方です"),
            MusicPresets.Cinema => L("映画やゲームの邪魔をしない、控えめで暖かい光り方です"),
            _ => "",
        };
        string tip = L($"{scene}\n光り方: {MusicTuning.Describe(sample.MusicStyle).Label} ・ 強さ: {Intensities.Label(sample.Music.Intensity)} ・ 色: {Palettes.Label(sample.Music.Palette)}");
        if (id == MusicPresets.Club) tip += L("\n（「激しい点滅を抑える」をオフにすると、さらに激しく点滅します）");
        return tip;
    }

    /// <summary>「強さ」の説明（選ぶと変わる値も書く）</summary>
    private static string IntensityTip(string id)
    {
        var p = MusicTuning.Profile(id);
        string feel = id switch
        {
            Intensities.Subtle => L("ゆったり穏やか。色は小節ごとにゆっくり変わります"),
            Intensities.Moderate => L("ほどよい反応。色は 2 拍ごとに変わります"),
            Intensities.High => L("キビキビ反応。色は 1 拍ごとに変わります"),
            Intensities.Intense => L("最大の反応。色は 1 拍ごとに一瞬で変わり、小節の頭を強調します"),
            _ => "",
        };
        return L($"{feel}\n選ぶと: 反応の速さ {p.ReactionMs:F0} ms ・ 滑らかさ {p.SmoothnessMs:F0} ms ・ メリハリ {p.Punch:F0}% ・ いちばん暗いとき {p.MinBrightness:F0}%");
    }

    // =====================================================================
    // 設定の変化に合わせて画面を更新する
    // =====================================================================

    /// <summary>LED の設定の変更を見張る（設定が丸ごと入れ替わったら、新しい設定を見張り直す）</summary>
    private void WatchLightingConfig()
    {
        if (_litRgb is not null) _litRgb.PropertyChanged -= OnLightingConfigChanged;
        if (_litMusic is not null) _litMusic.PropertyChanged -= OnLightingConfigChanged;
        _litRgb = Config.Rgb;
        _litRgb.Music ??= new MusicConfig();
        _litMusic = _litRgb.Music;
        _litRgb.PropertyChanged += OnLightingConfigChanged;
        _litMusic.PropertyChanged += OnLightingConfigChanged;
    }

    private void OnLightingConfigReplaced()
    {
        // 温度センサーの候補（RgbSensorCombo）はここでは作り直さない。
        // 候補を入れ替えると、ComboBox が一致しない値を空にして書き戻し、選んだセンサーが消えることがあるため
        LightingPage.DataContext = Config;
        WatchLightingConfig();
        RebuildRgbDeviceRows();
        RefreshLightingUi();
        // バックアップから戻すなどで LED の設定も変わったら、それも 1 段として元に戻せるようにする
        CheckpointLighting();
    }

    // =====================================================================
    // ライティングの元に戻す・やり直す
    // =====================================================================

    /// <summary>変更が落ち着いた LED の設定を、元に戻す用の 1 段として覚える（保存のたびに FlushCommit から呼ぶ）</summary>
    private void CheckpointLighting()
    {
        if (_lightHistory is null) return;
        _lightHistory.Checkpoint(Config.Rgb);
        UpdateLightingUndoButtons();
    }

    private void UpdateLightingUndoButtons()
    {
        if (_lightHistory is null) return;
        LightingUndoButton.IsEnabled = _lightHistory.CanUndo;
        LightingRedoButton.IsEnabled = _lightHistory.CanRedo;
    }

    private void OnLightingUndoClick(object sender, RoutedEventArgs e) => LightingUndo();
    private void OnLightingRedoClick(object sender, RoutedEventArgs e) => LightingRedo();

    private void LightingUndo() => LightingStep(undo: true);
    private void LightingRedo() => LightingStep(undo: false);

    private void LightingStep(bool undo)
    {
        if (_lightHistory is null) return;
        FlushCommit(); // 保存の予約中の変更も、先に 1 段にする
        bool ok, lists;
        _applyingPreset = true; // 戻している途中の値の変化で、プリセットを「カスタム」にしない
        try { ok = undo ? _lightHistory.Undo(Config.Rgb, out lists) : _lightHistory.Redo(Config.Rgb, out lists); }
        finally { _applyingPreset = false; }
        if (!ok) return;
        // デバイスの一覧は変更の知らせが出ないので、行を作り直して LED へ反映する（ほかの値は知らせで反映される）
        if (lists)
        {
            RebuildRgbDeviceRows();
            LightingListsChanged();
        }
        RefreshLightingUi();
        UpdateLightingUndoButtons();
        Logger.Info(undo ? "ライティングの設定を元に戻しました" : "ライティングの設定をやり直しました");
    }

    /// <summary>
    /// LED の設定が変わったとき（画面のスライダー・トレイのメニュー・元に戻すなど、どこから変えても来る）。
    /// 保存と LED への反映は MainWindow.xaml.cs の OnConfigPropertyChanged が行う。ここでは決まりごとと表示だけ
    /// </summary>
    private void OnLightingConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        var rgb = Config.Rgb;
        var m = rgb.Music;
        // 個別の値を手で変えたら、プリセットは「カスタム」（プリセットを当てている間は除く）
        if (!_applyingPreset && m.Preset != MusicPresets.Custom && LightingRules.IsPresetProperty(sender, e.PropertyName))
            m.Preset = MusicPresets.Custom;
        // 「激しい点滅を抑える」をオンに戻したら、ストロボは使えないので「ビートで色替え」にする
        if (sender is MusicConfig && e.PropertyName == nameof(MusicConfig.ReduceFlashing) && m.ReduceFlashing && rgb.MusicStyle == MusicStyles.Strobe)
            rgb.MusicStyle = MusicStyles.BeatHue;
        // 音楽連動でなくなったら、同期テストは続けられない
        if (sender is RgbConfig && (e.PropertyName is nameof(RgbConfig.Mode) or nameof(RgbConfig.Enabled)) && !(rgb.Enabled && rgb.Mode == RgbModes.Music))
            StopSyncTest();
        RefreshLightingUi();
    }

    /// <summary>選ばれているチップ・表示する行・色の入力欄などを、いまの設定に合わせる</summary>
    private void RefreshLightingUi()
    {
        var rgb = Config.Rgb;
        var m = rgb.Music;
        foreach (var c in _presetChoices) c.IsSelected = c.Id == m.Preset;
        foreach (var c in _effectChoices)
        {
            c.IsSelected = c.Id == rgb.MusicStyle;
            // ストロボは「激しい点滅を抑える」をオフにしたときだけ選べる
            c.IsEnabled = c.Id != MusicStyles.Strobe || !m.ReduceFlashing;
        }
        foreach (var c in _intensityChoices) c.IsSelected = c.Id == m.Intensity;
        foreach (var c in _paletteChoices) c.IsSelected = c.Id == m.Palette;
        foreach (var c in _tempoChoices) c.IsSelected = c.Id == m.TempoMode;
        foreach (var c in _idleChoices) c.IsSelected = c.Id == m.IdleEffect;
        foreach (var c in _colorChoices) c.IsSelected = string.Equals(c.Id, rgb.StaticColor, StringComparison.OrdinalIgnoreCase);

        // パレット「選んだ色」の見本と、いまの色の丸は、色が変わったときだけ作り直す
        if (_monoSwatchFor != rgb.StaticColor)
        {
            _monoSwatchFor = rgb.StaticColor;
            foreach (var c in _paletteChoices)
            {
                if (c.Id == Palettes.Mono) c.Swatch = GradientBrush(LightingRules.MonoStops(rgb.StaticColor));
                else if (c.Id == Palettes.Solid) c.Swatch = GradientBrush(new[] { rgb.StaticColor, rgb.StaticColor });
            }
        }
        if (_colorDotFor != rgb.StaticColor)
        {
            _colorDotFor = rgb.StaticColor;
            CurrentColorDot.Fill = SolidBrush(rgb.StaticColor);
        }
        if (!HexColorBox.IsKeyboardFocusWithin && HexColorBox.Text != rgb.StaticColor)
        {
            _hexSyncing = true;
            HexColorBox.Text = rgb.StaticColor;
            _hexSyncing = false;
            HexColorError.Visibility = Visibility.Collapsed;
        }

        // 選んだ光り方で使う設定だけを表示する
        bool music = rgb.Mode == RgbModes.Music;
        SpeedRow.Visibility = VisibleIf(rgb.Mode is RgbModes.Rainbow or RgbModes.Breathing || (music && LightingRules.UsesSpeed(rgb.MusicStyle)));
        SpeedLabel.Text = music ? L("流れる速さ") : L("速さ");
        PunchRow.Visibility = VisibleIf(LightingRules.UsesPunch(rgb.MusicStyle));
        RefreshTuningDefaults();
        TempoRow.Visibility = VisibleIf(LightingRules.UsesTempo(rgb.MusicStyle));
        ColorRow.Visibility = VisibleIf(LightingRules.UsesStaticColor(rgb));
        StrobeNote.Visibility = VisibleIf(m.ReduceFlashing);
        FlashWarning.Visibility = VisibleIf(!m.ReduceFlashing);

        // テンポの倍率（×2・÷2）
        TempoFactorText.Text = m.TempoFactor < 0.75 ? L("いまは半分のテンポ") : m.TempoFactor > 1.5 ? L("いまは 2 倍のテンポ") : "";
        TempoHalfButton.IsEnabled = m.TempoFactor > 0.75;
        TempoDoubleButton.IsEnabled = m.TempoFactor < 1.5;

        RefreshRgbDeviceSelection();
    }

    private static Visibility VisibleIf(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    // =====================================================================
    // チップ・カードを押したとき
    // =====================================================================

    private void OnLightingChoiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LightingChoiceVm c } || !c.IsEnabled) return;
        if (c.Kind is not (LightingChoiceKind.Accent or LightingChoiceKind.Background or LightingChoiceKind.TextColor))
            FlushCommit(); // 直前のスライダーの変更とは別の 1 段にする
        var rgb = Config.Rgb;
        var m = rgb.Music;
        switch (c.Kind)
        {
            case LightingChoiceKind.Preset:
                // 「カスタム」を押しても値は変えない（今の調整をそのまま残す）
                if (c.Id == MusicPresets.Custom)
                {
                    m.Preset = MusicPresets.Custom;
                    break;
                }
                _applyingPreset = true;
                try { MusicTuning.ApplyPreset(rgb, c.Id); }
                finally { _applyingPreset = false; }
                break;
            case LightingChoiceKind.Effect:
                rgb.MusicStyle = c.Id;
                break;
            case LightingChoiceKind.Intensity:
                // 反応の速さ・滑らかさ・メリハリ・いちばん暗いときも、その強さの値にする
                MusicTuning.ApplyIntensity(m, c.Id);
                break;
            case LightingChoiceKind.Palette:
                m.Palette = c.Id;
                break;
            case LightingChoiceKind.Tempo:
                m.TempoMode = c.Id;
                break;
            case LightingChoiceKind.Idle:
                m.IdleEffect = c.Id;
                break;
            case LightingChoiceKind.Color:
                rgb.StaticColor = c.Id;
                break;
            case LightingChoiceKind.Accent:
                Config.Appearance.Accent = c.Id;
                RefreshAppearanceUi();
                return;
            case LightingChoiceKind.Background:
                Config.Appearance.Background = c.Id;
                RefreshAppearanceUi();
                return;
            case LightingChoiceKind.TextColor:
                Config.Appearance.TextColor = c.Id;
                RefreshAppearanceUi();
                return;
        }
        RefreshLightingUi();
        FlushCommit(); // 選んだものを 1 段として覚え、すぐに「元に戻す」を押せるようにする
    }

    private void OnTempoHalfClick(object sender, RoutedEventArgs e)
        => Config.Rgb.Music.TempoFactor = LightingRules.TempoDown(Config.Rgb.Music.TempoFactor);

    private void OnTempoDoubleClick(object sender, RoutedEventArgs e)
        => Config.Rgb.Music.TempoFactor = LightingRules.TempoUp(Config.Rgb.Music.TempoFactor);

    /// <summary>「−」「+」: 音の遅れの補正を 10 ミリ秒ずつ動かす（同期テストで合わせるとき用）</summary>
    private void OnAudioDelayNudgeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int step)) return;
        var m = Config.Rgb.Music;
        m.AudioDelayMs = Math.Clamp(Math.Round(m.AudioDelayMs + step), -100, 400);
    }

    /// <summary>出力先の目安（Bluetooth +180・HDMI +50・仮想の出力先 +30）に、音の遅れの補正を合わせる</summary>
    private void OnBluetoothDelayClick(object sender, RoutedEventArgs e) =>
        Config.Rgb.Music.AudioDelayMs = (sender as FrameworkElement)?.Tag is int ms && ms > 0 ? ms : OutputPath.BluetoothDelayMs;

    /// <summary>「反応のしかた」の値のうち、初期値から変えているものを表示する（カスタムしたときに、元の値が分かるように）</summary>
    private void RefreshTuningDefaults()
    {
        var rgb = Config.Rgb;
        string changed = MusicTuning.ChangedTuningSummary(rgb);
        TuningChangedText.Text = changed.Length == 0
            ? L($"すべて初期値です（強さ「{Intensities.Label(rgb.Music.Intensity)}」の値）")
            : L("初期値から変えている値: ") + changed;
        ResetTuningButton.IsEnabled = changed.Length > 0;
    }

    /// <summary>「初期値に戻す」: 反応のしかたの値を初期値にする（光り方・強さ・色の組み合わせ・音の遅れの補正はそのまま）</summary>
    private void OnResetTuningClick(object sender, RoutedEventArgs e)
    {
        string changed = MusicTuning.ChangedTuningSummary(Config.Rgb);
        if (changed.Length == 0) return;
        FlushCommit(); // 直前のスライダーの変更とは別の 1 段にする
        _applyingPreset = true;
        try { MusicTuning.ResetTuning(Config.Rgb); }
        finally { _applyingPreset = false; }
        FlushCommit(); // すぐに「元に戻す」を押せるようにする
        Logger.Info($"音楽連動の反応のしかたを初期値に戻しました（{changed}）");
        ShowAlert(L("反応のしかたの値を初期値に戻しました。右上の「元に戻す」で、戻す前の値にできます。"), AlertLevel.Info);
    }

    /// <summary>
    /// 「激しい点滅を抑える」: オフにするときは、光に敏感な方への注意を出して確認する。
    /// チェックボックスは設定と片方向にだけ結び付けてあり、確認が済むまで設定は変えない
    /// </summary>
    private void OnReduceFlashingClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box) return;
        var m = Config.Rgb.Music;
        if (box.IsChecked == true)
        {
            m.ReduceFlashing = true;
            return;
        }
        var answer = MessageBox.Show(this,
            L("激しい点滅を許可しますか？\n\n強い光の点滅は、光に敏感な方の体調（光過敏性発作など）に影響することがあります。") +
            L("気分が悪くなったら、すぐに「激しい点滅を抑える」をオンに戻してください。"),
            "ProFanController", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) m.ReduceFlashing = false;
        else box.SetCurrentValue(ToggleButton.IsCheckedProperty, true); // 結び付けを残したまま元に戻す
    }

    // =====================================================================
    // 色の入力（#RRGGBB）
    // =====================================================================

    /// <summary>入力中: 正しい形になったらすぐに色を変える（途中の文字では注意を出さない）</summary>
    private void OnHexColorChanged(object sender, TextChangedEventArgs e)
    {
        if (_hexSyncing) return;
        string? hex = LightingRules.NormalizeHex(HexColorBox.Text);
        if (hex is null) return;
        HexColorError.Visibility = Visibility.Collapsed;
        if (!string.Equals(hex, Config.Rgb.StaticColor, StringComparison.OrdinalIgnoreCase)) Config.Rgb.StaticColor = hex;
    }

    private void OnHexColorLostFocus(object sender, RoutedEventArgs e) => CommitHexColor();

    private void OnHexColorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitHexColor();
        e.Handled = true;
    }

    /// <summary>入力を確定する: 正しくなければ注意を出し、設定は変えない（おかしな文字を保存しない）</summary>
    private void CommitHexColor()
    {
        string? hex = LightingRules.NormalizeHex(HexColorBox.Text);
        if (hex is null)
        {
            HexColorError.Visibility = Visibility.Visible;
            return;
        }
        HexColorError.Visibility = Visibility.Collapsed;
        if (!string.Equals(hex, Config.Rgb.StaticColor, StringComparison.OrdinalIgnoreCase)) Config.Rgb.StaticColor = hex;
        if (HexColorBox.Text != hex)
        {
            _hexSyncing = true;
            HexColorBox.Text = hex;
            _hexSyncing = false;
        }
    }

    // =====================================================================
    // 接続
    // =====================================================================

    private async void OnTestRgbClick(object sender, RoutedEventArgs e)
    {
        FlushCommit();
        ShowRgbMessage(L("OpenRGB に接続しています…"));
        try
        {
            var devices = await RgbSync.TestAsync(Config.Rgb.Host, Config.Rgb.Port);
            SetKnownRgbDevices(devices);
            int count = devices.Count(d => d.LedCount > 0);
            ShowRgbMessage(count == 0
                ? L("接続できましたが、色を変えられるデバイスが見つかりません。OpenRGB の画面にデバイスが表示されているか確認してください")
                : L($"接続できました。{count} 個のデバイスが見つかりました。") + (Config.Rgb.Enabled ? "" : L("「OpenRGB と連携する」をオンにすると始まります")));
        }
        catch (Exception ex)
        {
            var inner = ex is AggregateException { InnerException: { } x } ? x : ex;
            ShowRgbMessage(L($"接続できません: {inner.Message}\nOpenRGB を起動して「SDK Server」タブの「Start Server」を押してから、もう一度試してください。"));
        }
    }

    /// <summary>「今すぐ再接続」: 待たずに OpenRGB へ繋ぎ直す</summary>
    private void OnRgbReconnectClick(object sender, RoutedEventArgs e)
    {
        FlushCommit();
        if (!Config.Rgb.Enabled)
        {
            ShowRgbMessage(L("「OpenRGB と連携する」がオフです。オンにすると接続します"));
            return;
        }
        CurrentApp.Rgb.Kick();
        ShowRgbMessage(L("OpenRGB に繋ぎ直しています…"));
    }

    /// <summary>設定の「通知・連携」からの案内</summary>
    private void OnOpenLightingClick(object sender, RoutedEventArgs e) => NavLighting.IsChecked = true;

    /// <summary>ボタンの結果を、しばらく接続の状態の代わりに表示する</summary>
    private void ShowRgbMessage(string message)
    {
        _rgbMessage = message;
        _rgbMessageUntil = AudioClock.Now + RgbMessageSec;
        RgbStatus.Text = message;
    }

    /// <summary>接続の状態と、いま送っている色の丸を最新にする</summary>
    private void UpdateRgbStatus()
    {
        var sync = CurrentApp.Rgb;
        string text = _rgbMessage is not null && AudioClock.Now < _rgbMessageUntil ? _rgbMessage
            : Config.Rgb.Enabled ? sync.Status
            : L("オフ（「OpenRGB と連携する」をオンにすると、LED の色を変え始めます）");
        SetText(RgbStatus, text);

        var (r, g, b) = sync.CurrentColor;
        bool on = Config.Rgb.Enabled && (r | g | b) != 0;
        var state = (r, g, b, on);
        if (state == _rgbSwatchState) return; // 色が変わったときだけブラシを作る
        _rgbSwatchState = state;
        RgbSwatch.Background = on ? new SolidColorBrush(Color.FromRgb(r, g, b)) : Palette.Muted;
    }

    // =====================================================================
    // デバイス
    // =====================================================================

    /// <summary>デバイスの一覧を受け取る（中身が同じなら作り直さない）</summary>
    private void SetKnownRgbDevices(IReadOnlyList<RgbDevice> devices)
    {
        bool same = devices.Count == _rgbKnownDevices.Count;
        for (int i = 0; same && i < devices.Count; i++)
            same = devices[i].Name == _rgbKnownDevices[i].Name && devices[i].LedCount == _rgbKnownDevices[i].LedCount;
        _rgbKnownDevices = devices;
        if (!same) RebuildRgbDeviceRows();
    }

    /// <summary>接続中の一覧が入れ替わったら取り込む（「接続テスト」を押さなくても一覧が出るように）</summary>
    private void RefreshRgbDevicesFromSync()
    {
        var devices = CurrentApp.Rgb.Devices;
        if (ReferenceEquals(devices, _rgbSyncDevicesSeen)) return;
        _rgbSyncDevicesSeen = devices;
        if (devices.Count > 0) SetKnownRgbDevices(devices);
    }

    /// <summary>デバイスの行を作り直す（設定から、選択・光り方の上書き・向きを読み込む）</summary>
    private void RebuildRgbDeviceRows()
    {
        var rgb = Config.Rgb;
        rgb.Devices ??= new();
        rgb.SwapRedGreen ??= new();
        rgb.DeviceSettings ??= new();
        foreach (var vm in _rgbDeviceRows) vm.Edited = null; // 消える行から設定を書き換えないように
        _rgbDeviceRows.Clear();
        var seen = new HashSet<string>();
        foreach (var d in _rgbKnownDevices)
        {
            if (d.LedCount <= 0 || !seen.Add(d.Name)) continue;
            var s = rgb.SettingsFor(d.Name);
            var vm = new RgbDeviceVm
            {
                Name = d.Name, Leds = d.LedCount, IsSlow = d.IsSlow, IsFlickerProne = d.IsFlickerProne, IsGpu = d.IsLowWrite,
                AutoFps = (int)Math.Round(RgbDeviceChannel.MaxFpsFor(d, new RgbDeviceSettings { Name = d.Name })),
            };
            vm.Load(rgb.SwapRedGreen.Contains(d.Name), s.Effect, s.Mirror, s.Reverse, s.MaxFps, s.LatencyMs, s.AnimateOnGpu);
            vm.LoadCalibration(s);
            vm.LoadZones(d, s);
            vm.Edited = OnRgbDeviceEdited;
            _rgbDeviceRows.Add(vm);
        }
        RgbDevicesEmpty.Visibility = VisibleIf(_rgbDeviceRows.Count == 0);
        RefreshRgbDeviceSelection();
        UpdateMissingDevicesText();
        RebuildCasePlaces();
    }

    // ----- ケースの中の配置 -----

    private readonly ObservableCollection<CasePlaceVm> _casePlaces = new();

    /// <summary>
    /// 「ケースの中の配置」の行を作る（デバイスのゾーンごと。ゾーンの情報がないデバイスは 1 行）。
    /// 並びは DeviceRenderer のゾーンの分け方（RgbEffects.SegmentZones）と同じにする
    /// </summary>
    private void RebuildCasePlaces()
    {
        foreach (var p in _casePlaces) p.Edited = null;
        _casePlaces.Clear();
        var rgb = Config.Rgb;
        var seen = new HashSet<string>();
        foreach (var d in _rgbKnownDevices)
        {
            if (d.LedCount <= 0 || !seen.Add(d.Name)) continue;
            var s = rgb.SettingsFor(d.Name);
            var segments = new List<(int Start, int Length, int Zone)>();
            RgbEffects.SegmentZones(d.LedCount, d.Zones, segments);
            foreach (var (start, length, zone) in segments)
            {
                if (zone < 0 && segments.Count > 1) continue;
                string zoneName = d.ZoneName(Math.Max(0, zone));
                var vm = new CasePlaceVm { DeviceName = d.Name, ZoneName = zoneName, Start = start, Leds = length, SingleZone = segments.Count == 1 };
                vm.Load(s.ZoneFor(zoneName)?.Place);
                vm.Edited = OnCasePlaceEdited;
                _casePlaces.Add(vm);
            }
        }
        CasePlacesEmpty.Visibility = VisibleIf(_casePlaces.Count == 0);
    }

    /// <summary>ゾーンの場所を選んだとき: 設定に書き込み、ゾーンの行（色合わせ・ゾーンごとの設定）にも入れておく</summary>
    private void OnCasePlaceEdited(CasePlaceVm vm)
    {
        var rgb = Config.Rgb;
        var row = _rgbDeviceRows.FirstOrDefault(r => r.Name == vm.DeviceName);
        var zoneRow = row?.Zones.FirstOrDefault(z => z.Name == vm.ZoneName);
        if (zoneRow is not null) zoneRow.Place = vm.PlaceId;
        LightingRules.SetZonePlace(rgb.DeviceSettings, vm.DeviceName, vm.ZoneName, vm.PlaceId);
        LightingListsChanged();
    }

    private void OnCasePreviewLoaded(object sender, RoutedEventArgs e) => UpdateCasePreview();

    /// <summary>ケースの絵に、場所を決めた LED の、最後に送った色を描く</summary>
    private void UpdateCasePreview()
    {
        if (!Config.Rgb.CaseLayout || CasePreviewView is null || !CasePreviewView.IsVisible) return;
        var sync = CurrentApp.Rgb;
        bool enabled = Config.Rgb.Enabled;
        var items = new List<CasePreview.Item>();
        foreach (var p in _casePlaces)
        {
            if (p.Place is not { } place) continue;
            items.Add(new CasePreview.Item(place, p.Start, p.Leds, enabled ? sync.GetLastFrame(p.DeviceName) : null));
        }
        CasePreviewView.SetItems(items, Config.Rgb.Gamma);
    }

    /// <summary>「すべてのデバイス」と各行のチェックを、いまの設定に合わせる</summary>
    private void RefreshRgbDeviceSelection()
    {
        var rgb = Config.Rgb;
        rgb.Devices ??= new();
        // 設定の Devices が空 = すべてのデバイス
        bool all = rgb.Devices.Count == 0;
        bool music = rgb.Mode == RgbModes.Music;
        AllDevicesSwitch.IsChecked = all;
        foreach (var vm in _rgbDeviceRows)
        {
            vm.CanSelect = !all;
            vm.Selected = all || rgb.Devices.Contains(vm.Name);
            vm.ShowEffect = music;
        }
    }

    /// <summary>いま見えているデバイスの名前</summary>
    private HashSet<string> PresentRgbNames() => new(_rgbDeviceRows.Select(r => r.Name));

    /// <summary>「すべてのデバイスの色を変える」のオン・オフ</summary>
    private void OnAllRgbDevicesClick(object sender, RoutedEventArgs e)
    {
        var rgb = Config.Rgb;
        RgbDeviceHint.Visibility = Visibility.Collapsed;
        if (AllDevicesSwitch.IsChecked == true)
        {
            rgb.Devices = new List<string>();
        }
        else
        {
            if (_rgbDeviceRows.Count == 0)
            {
                AllDevicesSwitch.IsChecked = true;
                ShowDeviceHint(L("デバイスの一覧を読み込んでから選べます。上の「接続テスト」を押してください"));
                return;
            }
            // まずは全部を選んだ状態にする（光り方は変わらない）。ここから、色を変えたくないデバイスのチェックを外す
            rgb.Devices = _rgbDeviceRows.Select(r => r.Name).ToList();
        }
        RefreshRgbDeviceSelection();
        LightingListsChanged();
    }

    /// <summary>デバイスのチェックを変えたとき</summary>
    private void OnRgbDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RgbDeviceVm vm }) return;
        var rgb = Config.Rgb;
        RgbDeviceHint.Visibility = Visibility.Collapsed;
        if (rgb.Devices.Count == 0)
        {
            // 「すべて」のときはチェックを変えられない（念のため元に戻す）
            vm.Selected = true;
            return;
        }
        var chosen = _rgbDeviceRows.Where(r => r.Selected).Select(r => r.Name).ToList();
        if (chosen.Count == 0)
        {
            // 「何も選ばない」は「すべて」と同じ意味になってしまうので、最後の 1 つは外せないようにする
            vm.Selected = true;
            ShowDeviceHint(L("少なくとも 1 つは選んでください（LED を消したいときは、上の「OpenRGB と連携する」をオフにします）"));
            return;
        }
        rgb.Devices = LightingRules.MergeNames(rgb.Devices, PresentRgbNames(), chosen);
        LightingListsChanged();
    }

    /// <summary>行の中の設定（光り方の上書き・左右対称・逆向き・赤と緑）を変えたとき</summary>
    private void OnRgbDeviceEdited(RgbDeviceVm vm, string property)
    {
        var rgb = Config.Rgb;
        if (property == nameof(RgbDeviceVm.SwapRedGreen))
            rgb.SwapRedGreen = LightingRules.MergeNames(rgb.SwapRedGreen, PresentRgbNames(), _rgbDeviceRows.Where(r => r.SwapRedGreen).Select(r => r.Name));
        else
            LightingRules.UpsertDeviceSettings(rgb.DeviceSettings, vm.Name, vm.Effect, vm.Mirror, vm.Reverse, vm.ShowFps ? vm.MaxFps : -1, vm.LatencyMs,
                vm.ShowAnimateOnGpu ? vm.AnimateOnGpu : null, vm.WriteCalibration);
        LightingListsChanged();
    }

    /// <summary>
    /// デバイスの一覧（Devices・SwapRedGreen・DeviceSettings）は変更の知らせが出ないので、
    /// LED へすぐ反映し、保存も予約する
    /// </summary>
    private void LightingListsChanged()
    {
        CurrentApp.ApplyLightingNow();
        ScheduleCommit();
        UpdateMissingDevicesText();
    }

    private void ShowDeviceHint(string text)
    {
        RgbDeviceHint.Text = text;
        RgbDeviceHint.Visibility = Visibility.Visible;
    }

    /// <summary>設定にあるのに、いま見つからないデバイスの名前を表示する（設定は消さずに残している）</summary>
    private void UpdateMissingDevicesText()
    {
        var rgb = Config.Rgb;
        if (_rgbDeviceRows.Count == 0)
        {
            MissingDevicesText.Visibility = Visibility.Collapsed;
            return;
        }
        var present = PresentRgbNames();
        var missing = rgb.Devices.Concat(rgb.SwapRedGreen).Concat(rgb.DeviceSettings.Select(d => d.Name))
            .Where(n => !string.IsNullOrEmpty(n) && !present.Contains(n)).Distinct().ToList();
        MissingDevicesText.Text = L($"いま見つからないデバイスの設定も残してあります（つなぎ直すと元どおりに使えます）: {string.Join(L("、"), missing)}");
        MissingDevicesText.Visibility = VisibleIf(missing.Count > 0);
    }

    private void OnLedPreviewLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is LedPreview { DataContext: RgbDeviceVm vm } preview) vm.Preview = preview;
    }

    private void OnLedPreviewUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is LedPreview { DataContext: RgbDeviceVm vm } preview && vm.Preview == preview) vm.Preview = null;
    }

    // =====================================================================
    // 反応する音（アプリを選ぶ）
    // =====================================================================

    private void OnAllSoundsClick(object sender, RoutedEventArgs e) => Config.Rgb.MusicSource = "";

    /// <summary>
    /// 音を出している（出せる）アプリの一覧から選ぶ。音が出ているアプリを上に並べる。
    /// アプリの一覧は別のスレッドで調べ（数百ミリ秒かかることがある）、その間も画面は止めない。メニューはすぐ開いて「探しています」と出す
    /// </summary>
    private async void OnPickMusicAppClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom, MaxHeight = 420 };
        menu.Items.Add(new MenuItem { Header = L("音を出しているアプリを探しています…"), IsEnabled = false });
        menu.IsOpen = true;

        List<(string Name, float Peak, bool Active)> apps;
        List<string> windows;
        try
        {
            (apps, windows) = await Task.Run(() =>
            {
                var found = ListAudioApps();
                // 音のセッションが取れないときは、ウィンドウのあるアプリから選べるようにする（以前と同じ）
                return (found, found.Count == 0 ? ProfileSwitcher.RunningApps() : new List<string>());
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"音を出しているアプリの一覧を取得できません: {ex.Message}");
            apps = new();
            windows = new();
        }

        // ここからは画面のスレッド（await のあとは元のスレッドに戻る）。調べている間にメニューを閉じていたら、開き直さない
        if (!menu.IsOpen) return;
        menu.Items.Clear();
        foreach (var (name, peak, active) in apps)
        {
            string state = peak > 0.001f ? L("　（音が出ています）") : active ? L("　（再生中）") : "";
            var item = new MenuItem { Header = name + state };
            item.Click += (_, _) => Config.Rgb.MusicSource = name;
            menu.Items.Add(item);
        }
        foreach (var app in windows)
        {
            var item = new MenuItem { Header = app };
            item.Click += (_, _) => Config.Rgb.MusicSource = app;
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = L("アプリが見つかりません。曲を再生してから、もう一度押してください"), IsEnabled = false });
    }

    /// <summary>
    /// いつもの出力先で音のセッションを持っているアプリ（プロセス名・いまの音の大きさ・再生中か）。
    /// 音が出ているもの → 再生中のもの → 名前の順に並べる。音の取り込み（AudioCapture）と同じく、プロセス名で選ぶ。
    /// 時間がかかることがあるので、画面のスレッドでは呼ばない（OnPickMusicAppClick が別のスレッドで呼ぶ）
    /// </summary>
    private static List<(string Name, float Peak, bool Active)> ListAudioApps()
    {
        var apps = new Dictionary<string, (float Peak, bool Active)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                NAudio.CoreAudioApi.AudioSessionControl? s = null;
                try
                {
                    s = sessions[i];
                    if (s.IsSystemSoundsSession) continue;
                    uint pid = s.GetProcessID;
                    if (pid == 0 || pid == (uint)Environment.ProcessId) continue; // 自分（同期テストの音）は除く
                    string name;
                    using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName;
                    float peak = s.AudioMeterInformation.MasterPeakValue;
                    bool active = s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive;
                    apps[name] = apps.TryGetValue(name, out var old) ? (Math.Max(old.Peak, peak), old.Active || active) : (peak, active);
                }
                catch
                {
                    // 調べている途中で終了したアプリなど。ほかのアプリは続けて調べる
                }
                finally
                {
                    s?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"音を出しているアプリの一覧を取得できません: {ex.Message}");
        }
        return apps.Select(kv => (Name: kv.Key, kv.Value.Peak, kv.Value.Active))
            .OrderByDescending(a => a.Peak > 0.001f).ThenByDescending(a => a.Active).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // =====================================================================
    // 同期テスト（クリック音と光のずれを合わせる）
    // =====================================================================

    private void OnSyncTestClick(object sender, RoutedEventArgs e)
    {
        if (SyncTestToggle.IsChecked == true) StartSyncTest();
        else StopSyncTest();
    }

    private void StartSyncTest()
    {
        var app = CurrentApp;
        var rgb = Config.Rgb;
        if (!rgb.Enabled || rgb.Mode != RgbModes.Music || !app.Audio.Running)
        {
            SyncTestToggle.IsChecked = false;
            ShowSyncHelp(L("同期テストは、「OpenRGB と連携する」がオンで、光り方が「音楽連動」のときに使えます。"), warn: true);
            return;
        }
        app.SyncTestPlayer.Start(app.Audio.Engine);
        if (!app.SyncTestPlayer.Running)
        {
            SyncTestToggle.IsChecked = false;
            ShowSyncHelp(app.SyncTestPlayer.Error.Length > 0 ? app.SyncTestPlayer.Error : L("クリック音を鳴らせませんでした"), warn: true);
            return;
        }
        app.Rgb.SyncTest = true;
        _syncTestOn = true;
        SyncTestToggle.IsChecked = true;
        ShowSyncHelp(L("0.6 秒ごとに「カチッ」と鳴り、LED が白く光ります。光が音より早ければ「+」、遅ければ「−」で、音と光がそろうところを探してください。") +
                     L("もう一度押すか、この画面を離れると止まります。"), warn: false);
    }

    /// <summary>同期テストを止める（動いていなければ何もしない）</summary>
    private void StopSyncTest()
    {
        if (_syncTestOn)
        {
            _syncTestOn = false;
            try
            {
                CurrentApp.SyncTestPlayer.Stop(CurrentApp.Audio.Engine);
            }
            catch (Exception ex)
            {
                Logger.Warn($"同期テストを止められません: {ex.Message}");
            }
            CurrentApp.Rgb.SyncTest = false;
            SyncTestHelp.Visibility = Visibility.Collapsed;
        }
        if (SyncTestToggle.IsChecked == true) SyncTestToggle.IsChecked = false;
    }

    private void ShowSyncHelp(string text, bool warn)
    {
        SyncTestHelp.Text = text;
        SyncTestHelp.Foreground = warn ? Palette.Warn : Palette.Accent2;
        SyncTestHelp.Visibility = Visibility.Visible;
    }

    // =====================================================================
    // いまの音・LED のプレビュー（画面が見えている間だけ、画面の描き直しに合わせて動かす）
    // =====================================================================

    private void OnLightingPageVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (LightingPage.IsVisible)
        {
            StartLightingLive();
            CheckOpenRgbSmbus();
        }
        else
        {
            StopLightingLive();
            StopSyncTest(); // 画面を離れたら、クリック音を鳴らし続けない
            StopWhiteTest();
        }
    }

    /// <summary>「白く光らせて見比べる」のオン・オフ（色合わせ用。画面を離れたら止める）</summary>
    private void OnWhiteTestClick(object sender, RoutedEventArgs e) => CurrentApp.Rgb.WhiteTest = WhiteTestSwitch.IsChecked == true;

    private void StopWhiteTest()
    {
        if (WhiteTestSwitch.IsChecked == true) WhiteTestSwitch.IsChecked = false;
        CurrentApp.Rgb.WhiteTest = false;
    }

    /// <summary>
    /// ほかの RGB ソフトが動いていないか・OpenRGB の SMBus の共有がオフでないかを調べて、ライティング画面に知らせる。
    /// 動いているアプリの一覧と設定ファイルを読むので、画面が止まらないよう別スレッドで調べ、結果だけを画面に入れる
    /// </summary>
    private async void CheckOpenRgbSmbus()
    {
        int gen = ++_smbusCheckGen;
        List<string> notes;
        try
        {
            notes = await Task.Run(() =>
            {
                var list = new List<string>();
                var conflicts = InstallCheck.FindRunningConflicts();
                if (conflicts.Count > 0)
                    list.Add(L($"{string.Join(Localization.ListSeparator, conflicts)} が動いています。同じ LED を操作するので、色が勝手に戻る・ちらつく・ほかのデバイスとそろわない原因になります。")
                        + L("そのソフトの LED の制御（ライティング）を止めるか、終了してください（Lian Li の L-Connect は、ファンの回転だけ使う設定にできます）。"));
                string? path = OpenRgbSettings.FindUnsharedSmbus();
                if (path is not null)
                    list.Add(L("OpenRGB の設定で「Shared SMBus Access」（SMBus をほかのソフトと共有する）がオフになっています。")
                        + L("このアプリはメモリの温度を同じ配線（SMBus）で読むので、ぶつかって温度が読めなくなることがあります。")
                        + L("OpenRGB の Settings で「Shared SMBus Access」をオンにして、OpenRGB を起動し直してください（または、設定 →「メモリの温度を読む」をオフに）。")
                        + L($"\n設定ファイル: {path}"));
                return list;
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"ほかの RGB ソフト・OpenRGB の設定を確かめられません: {ex.Message}");
            return;
        }
        if (gen != _smbusCheckGen) return; // 調べている間に、もう一度調べ始めた
        SmbusWarning.Visibility = VisibleIf(notes.Count > 0);
        SmbusWarning.Text = string.Join("\n\n", notes);
    }

    /// <summary>CheckOpenRgbSmbus の何回目か（古い結果で上書きしないため）</summary>
    private int _smbusCheckGen;

    private void StartLightingLive()
    {
        if (_lightingLive) return;
        _lightingLive = true;
        _liveLastT = _livePreviewT = _liveTextT = 0;
        CompositionTarget.Rendering += OnLightingFrame;
        UpdateLightingTexts();
    }

    private void StopLightingLive()
    {
        if (!_lightingLive) return;
        _lightingLive = false;
        CompositionTarget.Rendering -= OnLightingFrame;
    }

    /// <summary>
    /// 画面の描き直しのたびに呼ばれる（最大 1 秒に 60 回に間引く）。
    /// スペクトラムとランプは毎回、LED のプレビューは 1 秒に 30 回、文字は 1 秒に 4 回だけ更新する。
    /// 配列・リストは使い回すので、ここでは新しいオブジェクトをほとんど作らない
    /// </summary>
    private void OnLightingFrame(object? sender, EventArgs e)
    {
        double now = AudioClock.Now;
        double dt = now - _liveLastT;
        if (dt < LiveFrameSec * 0.9) return;
        _liveLastT = now;
        if (WindowState == WindowState.Minimized) return;
        dt = Math.Min(dt, 0.1);
        try
        {
            UpdateLiveAudio(now, dt);
            if (now - _livePreviewT >= LivePreviewSec * 0.9)
            {
                _livePreviewT = now;
                UpdateLedPreviews();
            }
            if (now - _liveTextT >= LiveTextSec)
            {
                _liveTextT = now;
                UpdateLightingTexts();
            }
        }
        catch (Exception ex)
        {
            // 表示だけの失敗で画面全体を止めない（ログは 1 分に 1 回まで）
            if (now - _liveErrorLoggedAt > 60)
            {
                _liveErrorLoggedAt = now;
                Logger.Warn($"ライティング画面の表示の更新に失敗: {ex.Message}");
            }
        }
    }

    /// <summary>スペクトラムとビートのランプ</summary>
    private void UpdateLiveAudio(double now, double dt)
    {
        var audio = CurrentApp.Audio;
        var rgb = Config.Rgb;
        var engine = audio.Running && rgb.Enabled && rgb.Mode == RgbModes.Music ? audio.Engine : null;
        if (!ReferenceEquals(engine, _liveEngine))
        {
            // 取り込みが始まった・止まった・作り直された: 古い出来事は読まない
            _liveEngine = engine;
            _liveLastEventId = engine?.Events.LastId ?? 0;
            _liveHasFrame = false;
            LiveSpectrum.Clear();
        }
        if (engine is not null)
        {
            // 取り込みのやり直しでエンジンが作り直されても、同期テストのクリック音は検出し続ける
            if (_syncTestOn && !engine.ClickDetect) engine.ClickDetect = true;
            _liveHasFrame = engine.Features.Latest(_liveFrame);
            if (_liveHasFrame) LiveSpectrum.SetFrame(_liveFrame.Band, _liveFrame.Punch, _liveFrame.GateOpen, now);

            // 前回から増えた出来事だけを見る（出来事の時刻は検出より少し前なので、時刻ではなく通し番号で区切る）
            _liveEvents.Clear();
            engine.Events.Between(now - 2, now + 1, _liveEvents);
            for (int i = 0; i < _liveEvents.Count; i++)
            {
                var ev = _liveEvents[i];
                if (ev.Id <= _liveLastEventId) continue;
                _liveLastEventId = ev.Id;
                if (ev.Kind == AudioEventKind.Kick) _kickGlow = 1;
                else if (ev.Kind == AudioEventKind.Click) _clickGlow = 1;
            }
        }
        double fade = Math.Exp(-dt / LampTauSec);
        _kickGlow *= fade;
        _clickGlow *= fade;
        BeatLampView.SetGlow(_kickGlow, _clickGlow);
    }

    /// <summary>デバイスごとの LED のプレビュー（最後に送った色）</summary>
    private void UpdateLedPreviews()
    {
        var sync = CurrentApp.Rgb;
        bool enabled = Config.Rgb.Enabled;
        bool gamma = Config.Rgb.Gamma;
        for (int i = 0; i < _rgbDeviceRows.Count; i++)
        {
            var vm = _rgbDeviceRows[i];
            vm.Preview?.SetColors(enabled ? sync.GetLastFrame(vm.Name) : null, gamma);
        }
        UpdateCasePreview();
    }

    /// <summary>文字の表示（接続の状態・デバイスごとの状態・テンポ・音量・取り込みの状態）。1 秒に 4 回</summary>
    private void UpdateLightingTexts()
    {
        UpdateRgbStatus();
        RefreshRgbDevicesFromSync();
        UpdateRgbDeviceStatuses();
        UpdateAudioTexts();
        // App が同期テストを止めた（音楽連動をやめた・取り込みが止まった）ときは、ボタンも戻す
        // クリック音が止まった、または音の出力先が切り替わった（クリックが取り込まれない）ときはテストを終える
        var player = CurrentApp.SyncTestPlayer;
        if (_syncTestOn && (!player.Running || (CurrentApp.Audio.EndpointId.Length > 0 && player.DeviceId != CurrentApp.Audio.EndpointId))) StopSyncTest();
    }

    /// <summary>デバイスごとの「1 秒に送っている回数・いまの光り方・最後のエラー」</summary>
    private void UpdateRgbDeviceStatuses()
    {
        var statuses = CurrentApp.Rgb.DeviceStatus;
        bool enabled = Config.Rgb.Enabled;
        for (int i = 0; i < _rgbDeviceRows.Count; i++)
        {
            var vm = _rgbDeviceRows[i];
            RgbDeviceStatus? st = null;
            for (int k = 0; enabled && k < statuses.Count; k++)
            {
                if (statuses[k].Name == vm.Name) { st = statuses[k]; break; }
            }
            if (st is null)
            {
                vm.StatusText = "";
                vm.ErrorText = "";
                continue;
            }
            string effect = st.ActiveEffect.Length > 0 ? L(" ・ ") + MusicTuning.Describe(st.ActiveEffect).Label : "";
            vm.StatusText = $"{st.Fps:F0} fps{effect}";
            vm.ErrorText = st.LastError;
        }
    }

    /// <summary>取り込みの状態・Bluetooth の案内・テンポ・音量</summary>
    private void UpdateAudioTexts()
    {
        var audio = CurrentApp.Audio;
        var rgb = Config.Rgb;
        var m = rgb.Music;
        string status = !rgb.Enabled ? L("「OpenRGB と連携する」をオンにすると、音の取り込みが始まります")
            : rgb.Mode != RgbModes.Music ? ""
            : rgb.MusicSource.Length > 0 ? L($"{audio.Status}（{rgb.MusicSource} の音だけに反応）")
            : audio.Status;
        var output = audio.Output;
        if (audio.Running && audio.EndpointName.Length > 0)
        {
            string kind = OutputPath.KindLabel(output.Kind);
            status += L($"\n聞いている出力先: {audio.EndpointName}") + (kind.Length > 0 ? L($"（{kind}）") : "");
        }
        SetText(AudioStatus, status);
        // 出力先に合わせた音の遅れの補正の案内（Bluetooth・HDMI・仮想の出力先）。種類が分からなければ、名前で Bluetooth を見分ける
        bool suggest = audio.Running && (OutputPath.ShouldSuggest(output, m.AudioDelayMs)
            || (output.Kind == OutputKind.Unknown && LightingRules.LooksLikeBluetooth(audio.EndpointName) && m.AudioDelayMs < BluetoothHintBelowMs));
        if (suggest)
        {
            int ms = output.SuggestedDelayMs > 0 ? output.SuggestedDelayMs : OutputPath.BluetoothDelayMs;
            SetText(OutputHintText, output.Kind == OutputKind.Unknown ? OutputPath.Hint(output with { Kind = OutputKind.Bluetooth }) : OutputPath.Hint(output));
            string label = L($"+{ms} ms にする");
            if (OutputHintButton.Content as string != label) OutputHintButton.Content = label;
            OutputHintButton.Tag = ms;
        }
        BluetoothHint.Visibility = VisibleIf(suggest);
        string note = audio.Running ? OutputPath.SoftwareNote(output) : "";
        SetText(OutputSoftwareNote, note);
        OutputSoftwareNote.Visibility = VisibleIf(note.Length > 0);

        var engine = _liveEngine;
        if (engine is null)
        {
            SetText(BpmText, "—");
            SetText(BeatInfoText, rgb.Enabled && rgb.Mode == RgbModes.Music ? L("音の取り込みを準備しています…") : L("音楽連動で OpenRGB と連携しているときに表示します"));
            SetText(LevelText, "");
            SetText(GateText, "");
            return;
        }
        var beat = engine.Beat;
        if (m.TempoMode == TempoModes.Off)
        {
            SetText(BpmText, L("テンポ: 使わない"));
            SetText(BeatInfoText, L("音の大きさだけで光っています"));
        }
        else if (beat.Locked)
        {
            SetText(BpmText, $"{beat.Bpm:F0} BPM");
            SetText(BeatInfoText, m.TempoMode == TempoModes.KickOnly
                ? L("キックに合わせて光っています")
                : L($"テンポをつかんでいます（確かさ {beat.Confidence * 100:F0}%）"));
        }
        else
        {
            SetText(BpmText, L("テンポ検出中…"));
            SetText(BeatInfoText, beat.HasTempo && beat.Confidence > 0.05
                ? L($"候補 {beat.Bpm:F0} BPM（確かさ {beat.Confidence * 100:F0}%）")
                : L("ビートがはっきりした曲なら、数秒でテンポをつかみます"));
        }
        if (!_liveHasFrame)
        {
            SetText(LevelText, "");
            SetText(GateText, "");
            return;
        }
        string rms = _liveFrame.RmsDb <= -100 ? "—" : $"{_liveFrame.RmsDb:F0} dBFS";
        SetText(LevelText, L($"音量 {rms} ・ 基準 {_liveFrame.CeilingDb:F0} dBFS"));
        SetText(GateText, _liveFrame.GateOpen ? L("● 音を検出しています") : L("○ 無音（音が鳴ると反応します）"));
    }

    /// <summary>文字が変わったときだけ書き換える（同じ文字を入れ直して描き直しを起こさない）</summary>
    private static void SetText(TextBlock block, string text)
    {
        if (block.Text != text) block.Text = text;
    }

    // =====================================================================
    // 色のブラシ
    // =====================================================================

    private static SolidColorBrush SolidBrush(string hex)
    {
        var c = Rgb.Parse(hex);
        var brush = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>左から右へ、色を等間隔に並べたグラデーション（パレットの見本）</summary>
    private static Brush GradientBrush(IReadOnlyList<string> stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        for (int i = 0; i < stops.Count; i++)
        {
            var c = Rgb.Parse(stops[i]);
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(c.R, c.G, c.B), stops.Count == 1 ? 0 : i / (double)(stops.Count - 1)));
        }
        brush.Freeze();
        return brush;
    }
}

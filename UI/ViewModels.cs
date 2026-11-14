// ==================================================
// ViewModels.cs - 画面表示用のデータ (温度カード・ファンカード・センサー一覧・グラフ系列・ウィザード)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.Windows.Media;

namespace ProFanController.UI;

public static class Palette
{
    public static readonly SolidColorBrush Ok = Make(0x34, 0xD3, 0x99);
    public static readonly SolidColorBrush Warn = Make(0xFB, 0xBF, 0x24);
    public static readonly SolidColorBrush Danger = Make(0xF8, 0x71, 0x71);
    public static readonly SolidColorBrush Accent = Make(0x7C, 0x5C, 0xFF);
    public static readonly SolidColorBrush Accent2 = Make(0x22, 0xC7, 0xFF);
    public static readonly SolidColorBrush Muted = Make(0x5B, 0x61, 0x78);

    /// <summary>グラフの系列用（見分けやすい順）</summary>
    public static readonly SolidColorBrush[] Series =
    {
        Make(0x7C, 0x5C, 0xFF), Make(0x22, 0xC7, 0xFF), Make(0xF4, 0x72, 0xB6), Make(0x34, 0xD3, 0x99),
        Make(0xFB, 0xBF, 0x24), Make(0xF8, 0x71, 0x71), Make(0xA3, 0xE6, 0x35), Make(0xFB, 0x92, 0x3C),
        Make(0x60, 0xA5, 0xFA), Make(0xE8, 0x79, 0xF9),
    };

    private static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>カーブの連動先として選べる温度（実センサー＋仮想センサー）</summary>
public sealed record SensorOption(string Id, string Name, string Group, SensorKind Kind)
{
    public string Display => $"{Name}  ·  {Group}";
}

/// <summary>直近 N 件の値を保持する固定長バッファ（スパークライン用）</summary>
public sealed class History
{
    private readonly int _capacity;
    private readonly Queue<double> _values = new();

    public History(int capacity = 90) => _capacity = capacity;

    public double[] Push(double value)
    {
        _values.Enqueue(value);
        while (_values.Count > _capacity) _values.Dequeue();
        return _values.ToArray();
    }
}

/// <summary>ダッシュボード上部の温度カード</summary>
public sealed class TempCardVm : ObservableObject
{
    private readonly History _history = new();
    private string _valueText = "--";
    private SolidColorBrush _levelBrush = Palette.Muted;
    private double[]? _values;
    private bool _editing;
    private string? _suspect;

    public required string Title { get; init; }
    public required string SensorId { get; init; }
    public required string Subtitle { get; init; }
    /// <summary>カードの 2 行目。センサーの値が固まっているときは、その警告に置き換える</summary>
    public string DisplaySubtitle => _suspect is null ? Subtitle : L("⚠ 値が固まっています（異常として扱い中）");
    public string? SuspectReason => _suspect;
    /// <summary>この温度以上で黄色、Danger 以上で赤</summary>
    public double Warn { get; init; } = 70;
    public double Danger { get; init; } = 85;

    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }
    public SolidColorBrush LevelBrush { get => _levelBrush; private set => Set(ref _levelBrush, value); }
    public double[]? Values { get => _values; private set => Set(ref _values, value); }
    /// <summary>ダッシュボードの編集モード中は並べ替え・削除ボタンを出す</summary>
    public bool Editing { get => _editing; set => Set(ref _editing, value); }

    /// <summary>異常値フィルターが「固着」と判定したら、その理由を渡す（解消したら null）</summary>
    public void SetSuspect(string? reason)
    {
        if (_suspect == reason) return;
        _suspect = reason;
        Raise(nameof(DisplaySubtitle));
        Raise(nameof(SuspectReason));
    }

    public void Update(float? value)
    {
        if (value is null)
        {
            ValueText = "--";
            LevelBrush = Palette.Muted;
            return;
        }
        ValueText = $"{value.Value:F0}";
        LevelBrush = value >= Danger ? Palette.Danger : value >= Warn ? Palette.Warn : Palette.Ok;
        Values = _history.Push(value.Value);
    }
}

/// <summary>ダッシュボードのファン／ポンプカード</summary>
public sealed class FanCardVm : ObservableObject
{
    private readonly History _history = new();
    private string _name = "";
    private string _rpmText = "--";
    private double _duty;
    private string _dutyText = "--";
    private string _stateText = "";
    private SolidColorBrush _stateBrush = Palette.Muted;
    private string _detailText = "";
    private double[]? _values;
    private string _tempText = "--";
    private string _sourceText = "";
    private string _profileText = "";

    public required string ControlId { get; init; }
    /// <summary>端子があるデバイス名（例: ASRock B860 Steel Legend WiFi）</summary>
    public string DeviceText { get; init; } = "";
    public required bool IsPump { get; init; }
    public string RoleText => IsPump ? "PUMP" : "FAN";
    public SolidColorBrush RoleBrush => IsPump ? Palette.Accent2 : Palette.Accent;

    public string Name { get => _name; set => Set(ref _name, value); }
    public string RpmText { get => _rpmText; private set => Set(ref _rpmText, value); }
    public double Duty { get => _duty; private set => Set(ref _duty, value); }
    public string DutyText { get => _dutyText; private set => Set(ref _dutyText, value); }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public SolidColorBrush StateBrush { get => _stateBrush; private set => Set(ref _stateBrush, value); }
    public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }
    public double[]? Values { get => _values; private set => Set(ref _values, value); }
    /// <summary>円形ゲージの中央に出す温度（いちばん高い回転を要求しているカーブの温度）</summary>
    public string TempText { get => _tempText; private set => Set(ref _tempText, value); }
    /// <summary>そのカーブ名（例: CPU-Pkg）。固定回転なら「固定」</summary>
    public string SourceText { get => _sourceText; private set => Set(ref _sourceText, value); }
    public string ProfileText { get => _profileText; private set => Set(ref _profileText, value); }

    public void Update(FanStatus s, string profile, bool fixedDuty)
    {
        ProfileText = profile;
        var lead = s.Curves.Where(c => !c.Error).OrderByDescending(c => c.Request).FirstOrDefault();
        TempText = lead?.Temp is double t ? $"{t:F0}°" : "--";
        SourceText = fixedDuty ? L("固定回転") : lead?.Name ?? "";

        Name = s.Name;
        RpmText = s.Rpm.HasValue ? $"{s.Rpm.Value:N0}" : "--";
        Duty = s.Duty ?? 0;
        DutyText = s.Duty.HasValue ? $"{s.Duty.Value:F0}%" : "--";
        if (s.Rpm.HasValue) Values = _history.Push(s.Rpm.Value);

        (StateText, StateBrush) = s.State switch
        {
            FanState.Active => (L("制御中"), Palette.Ok),
            FanState.DryRun => (L($"DRY RUN（計算値 {s.Target:F0}%）"), Palette.Warn),
            FanState.Bios => (s.Role == FanRoles.Pump ? L("BIOS 制御・監視中") : L("BIOS 制御"), Palette.Muted),
            FanState.Paused => (L("一時停止（BIOS）"), Palette.Warn),
            FanState.Identify => (L("テスト中（100%）"), Palette.Accent2),
            FanState.Calibrating => (L("キャリブレーション中"), Palette.Accent2),
            FanState.Boost => (L("ブースト中（100%）"), Palette.Accent2),
            FanState.Emergency => (L("緊急全開"), Palette.Danger),
            FanState.SensorError => (L("センサー異常 → 全開"), Palette.Danger),
            FanState.Stall => (L("停止を検知"), Palette.Danger),
            FanState.PumpAlert => (L("ポンプ異常 → 保護運転"), Palette.Danger),
            FanState.NotFound => (L("端子が見つかりません"), Palette.Danger),
            FanState.SensorMissing => (L($"センサーなし → 安全な回転数 {s.Target:F0}%"), Palette.Warn),
            FanState.Manual => (L("手動テスト中"), Palette.Accent2),
            FanState.GpuGuard => (L("GPU 保護 → 100%"), Palette.Danger),
            _ => ("", Palette.Muted),
        };

        DetailText = s.Curves.Count == 0
            ? ""
            : string.Join("   ", s.Curves.Select(c => c.Missing ? L($"{c.Name}: センサーなし")
                                                      : c.Error ? $"{c.Name}: --"
                                                      : $"{c.Name}: {c.Temp:F0}°C → {c.Request:F0}%"));
    }
}

/// <summary>ダッシュボードの「システム」タイル（使用率・電力・メモリなど）とウィジェットの行</summary>
public sealed class MetricTileVm : ObservableObject
{
    private readonly History _history = new();
    private string _valueText = "--";
    private string _subText = "";
    private double _percent;
    private double[]? _values;

    public required string Title { get; init; }
    /// <summary>ウィジェット用の短い名前（CPU / GPU / VRAM / RAM）</summary>
    public required string ShortTitle { get; init; }
    /// <summary>履歴に記録する代表のセンサー（null なら記録しない）</summary>
    public string? HistoryKey { get; init; }
    public string HistoryUnit { get; init; } = "%";
    public SolidColorBrush Brush { get; init; } = Palette.Accent;
    /// <summary>値の読み取り方（センサー ID の組み合わせはタイルごとに違う）</summary>
    public required Func<IReadOnlyDictionary<string, float?>, (double? Value, string Text, string Sub, double? Percent)> Read { get; init; }

    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }
    public string SubText { get => _subText; private set => Set(ref _subText, value); }
    /// <summary>0〜100（バー表示用）</summary>
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public double[]? Values { get => _values; private set => Set(ref _values, value); }

    public void Update(IReadOnlyDictionary<string, float?> values)
    {
        var (v, text, sub, percent) = Read(values);
        ValueText = text;
        SubText = sub;
        Percent = percent ?? 0;
        if (v.HasValue) Values = _history.Push(v.Value);
    }
}

/// <summary>診断結果 1 件分</summary>
public sealed class FindingVm : ObservableObject
{
    private bool _applied;
    private string _resultText = "";

    public required DiagnosticFinding Finding { get; init; }
    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public string? ActionLabel => Finding.ActionLabel;
    public bool CanApply => Finding.Apply is not null && !_applied;
    public SolidColorBrush LevelBrush => Finding.Level switch
    {
        FindingLevel.Danger => Palette.Danger,
        FindingLevel.Warning => Palette.Warn,
        FindingLevel.Good => Palette.Ok,
        _ => Palette.Accent2,
    };
    public string LevelText => Finding.Level switch
    {
        FindingLevel.Danger => L("要対応"),
        FindingLevel.Warning => L("注意"),
        FindingLevel.Good => L("良好"),
        _ => L("ヒント"),
    };
    public string ResultText { get => _resultText; private set => Set(ref _resultText, value); }

    public void MarkApplied(string result)
    {
        _applied = true;
        ResultText = "✔ " + result;
        Raise(nameof(CanApply));
    }
}

/// <summary>デバイス画面のカード 1 枚分</summary>
public sealed class DeviceCardVm : ObservableObject
{
    private System.Windows.Media.ImageSource? _image;
    private string _badgeText = "";

    public required DeviceInfo Info { get; init; }
    public string Name => Info.Name;
    public string Kind => Info.Kind;
    public string KindText => Info.Kind switch
    {
        "Motherboard" => L("マザーボード"),
        "Cpu" => "CPU",
        "Gpu" => L("グラフィックス"),
        _ => L("コントローラー"),
    };
    public string DetailText => Info.ChipName is null
        ? L($"温度センサー {Info.TempCount} ・ ファン {Info.FanCount}")
        : L($"{Info.ChipName} ・ 温度センサー {Info.TempCount} ・ ファン端子 {Info.FanCount}");
    public bool HasBadge => Info.FanCount > 0;

    /// <summary>自分で設定した画像（null ならイラスト）</summary>
    public System.Windows.Media.ImageSource? Image { get => _image; set => Set(ref _image, value); }
    /// <summary>ネットの写真なら作者とライセンス（空なら出さない）</summary>
    public string Credit { get => _credit; set { if (Set(ref _credit, value ?? "")) Raise(nameof(HasCredit)); } }
    public bool HasCredit => _credit.Length > 0;
    private string _credit = "";
    /// <summary>回っているファンの数（SignalRGB の右下の数字と同じ考え方）</summary>
    public string BadgeText { get => _badgeText; private set => Set(ref _badgeText, value); }

    public void Update(IReadOnlyDictionary<string, float?> values)
    {
        if (!HasBadge) return;
        int spinning = Info.FanSensorIds.Count(id => values.TryGetValue(id, out var v) && v > 100);
        BadgeText = spinning.ToString();
    }
}

public sealed class SensorRowVm : ObservableObject
{
    private string _valueText = "--";

    public required SensorInfo Info { get; init; }
    public string Name => Info.Name;
    public string Id => Info.Id;
    public string TypeText => Info.SensorType switch
    {
        "Temperature" => L("温度"),
        "Fan" => L("回転数"),
        "Control" => L("制御"),
        "Virtual" => L("仮想"),
        "Load" => L("使用率"),
        "Clock" => L("クロック"),
        "Power" => L("電力"),
        "Data" or "SmallData" => L("データ"),
        "Level" => L("レベル"),
        _ => Info.SensorType,
    };
    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }
    private string _minText = "--", _maxText = "--", _avgText = "--";
    /// <summary>起動・リセットしてからの最小・最大・平均</summary>
    public string MinText { get => _minText; private set => Set(ref _minText, value); }
    public string MaxText { get => _maxText; private set => Set(ref _maxText, value); }
    public string AvgText { get => _avgText; private set => Set(ref _avgText, value); }

    public void Update(float? v, SensorStats.Entry? stats = null)
    {
        ValueText = Format(v);
        MinText = Format(stats?.Min);
        MaxText = Format(stats?.Max);
        AvgText = Format(stats?.Average);
    }

    private string Format(float? v) => v is not float f || float.IsNaN(f)
        ? "--"
        : Info.SensorType switch
        {
            "Temperature" or "Virtual" => $"{f:F1} °C",
            "Fan" => $"{f:N0} rpm",
            "Clock" => $"{f:N0} MHz",
            "Power" => $"{f:F1} W",
            "Data" => $"{f:F1} GB",
            "SmallData" => $"{f:N0} MB",
            _ => $"{f:F0} %",
        };
}

public sealed class SensorGroupVm
{
    public required string Name { get; init; }
    public required string HardwareType { get; init; }
    public ObservableCollection<SensorRowVm> Rows { get; } = new();
}

/// <summary>履歴グラフの凡例（チェックで表示切替）</summary>
public sealed class SeriesToggleVm : ObservableObject
{
    private bool _visible;

    public required Controls.ChartSeries Series { get; init; }
    public string Name => Series.Name;
    public SolidColorBrush Brush => Series.Brush;
    public string Unit => Series.IsRpm ? "rpm" : "°C";

    public bool Visible
    {
        get => _visible;
        set
        {
            if (Set(ref _visible, value)) Series.Visible = value;
        }
    }
}

/// <summary>仮想センサー作成フォームの「元にするセンサー」の選択肢</summary>
public sealed class SourcePickVm : ObservableObject
{
    private bool _selected;

    public required SensorOption Option { get; init; }
    public string Display => Option.Display;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>セットアップウィザードの端子 1 行分</summary>
public sealed class WizardChannelVm : ObservableObject
{
    public const string RoleUnused = "Unused";
    public const string SourceCpu = "CPU";
    public const string SourceGpu = "GPU";
    public const string SourceBoth = "Both";

    private string _name = "";
    private string _role = FanRoles.Fan;
    private string _source = SourceCpu;
    private string _rpmText = "--";
    private double _rpm;

    public required ControlInfo Control { get; init; }
    public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>"Fan" / "Pump" / "Unused"</summary>
    public string Role
    {
        get => _role;
        set
        {
            if (!Set(ref _role, value)) return;
            GuessedUnused = false;   // 選び直した（またはアプリが決め直した）
            Raise(nameof(PumpHint));
        }
    }
    /// <summary>"CPU" / "GPU" / "Both"</summary>
    public string Source { get => _source; set => Set(ref _source, value); }
    public string RpmText { get => _rpmText; private set => Set(ref _rpmText, value); }
    public double Rpm => _rpm;
    /// <summary>最初に回転数を見たときの値（ポンプかもしれないファンの下限を決めるのに使う）</summary>
    public double FirstRpm { get; set; }
    /// <summary>0 rpm だったので、アプリが「使わない」にした（あとで回ったら「ファン」に戻す）</summary>
    public bool GuessedUnused { get; set; }

    /// <summary>「ファン」なのに速く回っている（簡易水冷のポンプかもしれない）ときの注意</summary>
    public string PumpHint => !Testing && SetupGuess.MaybePump(_role, _rpm) ? L("速く回っています。簡易水冷のポンプなら「ポンプ」に") : "";

    /// <summary>「テスト」で全開にしている間（この間の回転数は、ふだんの速さではない）</summary>
    public DateTime TestUntilUtc { get; set; }
    public bool Testing => DateTime.UtcNow < TestUntilUtc;

    public void UpdateRpm(float? rpm)
    {
        _rpm = rpm ?? 0;
        RpmText = rpm.HasValue ? $"{rpm.Value:N0} rpm" : "--";
        Raise(nameof(PumpHint));
    }
}

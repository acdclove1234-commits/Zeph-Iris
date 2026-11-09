// ==================================================
// AppConfig.cs - 設定データモデル (プロファイル・仮想センサー・キャリブレーション・画面設定・
//                安全機能・通知・RGB・電気代) と保存処理（世代バックアップ付き）
// Dependencies: .NET 8 / System.Text.Json (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProFanController;

public sealed class AppConfig : ObservableObject
{
    /// <summary>設定ファイルの形式の版（形式を変えたときに、古い設定を移し替える目印）</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>この設定ファイルを書いたときの形式の版</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// この版が知らない項目（新しい版で追加された設定など）。読み込んだまま保存し直すので、
    /// 古い版に戻して使っても、新しい版の設定が消えない
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    private bool _dryRun;
    private int _pollingIntervalMs = 1000;
    private double _emergencyTemperature = 100.0;
    private double _emergencyHotspotTemperature = 108.0;
    private bool _startMinimized;
    private string _activeProfile = "標準";
    private bool _setupCompleted;

    /// <summary>true の間は計算結果を表示するだけで、ファンには書き込まない</summary>
    public bool DryRun { get => _dryRun; set => Set(ref _dryRun, value); }
    public int PollingIntervalMs { get => _pollingIntervalMs; set => Set(ref _pollingIntervalMs, value); }
    /// <summary>カーブで参照している実センサーのどれかがこの温度以上なら、制御中の全ファンを 100%</summary>
    public double EmergencyTemperature { get => _emergencyTemperature; set => Set(ref _emergencyTemperature, value); }
    /// <summary>GPU Hot Spot 用の緊急温度。Hot Spot は Edge より 10〜20°C 高く出て、Radeon は 110°C で自動的に性能を落とすため別にする</summary>
    public double EmergencyHotspotTemperature { get => _emergencyHotspotTemperature; set => Set(ref _emergencyHotspotTemperature, value); }
    /// <summary>手動起動時もウィンドウを出さずにタスクトレイで開始する（自動起動時は常にトレイ）</summary>
    public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }
    /// <summary>画面の言語（"ja"・"en"・"zh-Hans" など。空なら Windows の表示言語。変えたら、アプリは止めずに画面を作り直して当てる）</summary>
    public string Language { get => _language; set => Set(ref _language, value ?? ""); }
    private string _language = "";
    /// <summary>手動で選んだプロファイル名（自動切り替えのルールに一致しないときに使う）</summary>
    public string ActiveProfile { get => _activeProfile; set => Set(ref _activeProfile, value); }
    public bool SetupCompleted { get => _setupCompleted; set => Set(ref _setupCompleted, value); }

    public ObservableCollection<ProfileConfig> Profiles { get; set; } = new();
    public ObservableCollection<AutoSwitchRule> AutoSwitchRules { get; set; } = new();
    /// <summary>時間帯でプロファイルを切り替えるルール（アプリの自動切り替えより優先度は低い）</summary>
    public ObservableCollection<ScheduleRule> ScheduleRules { get; set; } = new();
    public ObservableCollection<VirtualSensorConfig> VirtualSensors { get; set; } = new();
    public ObservableCollection<CalibrationResult> Calibrations { get; set; } = new();
    /// <summary>ダッシュボード上部の温度カード。空のときは CPU/GPU などを自動で選ぶ</summary>
    public ObservableCollection<DashboardCard> DashboardCards { get; set; } = new();
    public WidgetConfig Widget { get; set; } = new();
    /// <summary>見た目（テーマ・アクセント色・フォント・文字の大きさ）</summary>
    public AppearanceConfig Appearance { get; set; } = new();
    public QuickConfig Quick { get; set; } = new();
    public AdvisorConfig Advisor { get; set; } = new();
    public WebConfig Web { get; set; } = new();
    /// <summary>終了時に、起動したときの電源プランへ戻す</summary>
    public bool RestorePowerPlanOnExit { get; set; } = true;
    /// <summary>デバイス画面で使う自分で用意した画像（exe と同じフォルダの images\ にコピーして保存）</summary>
    public ObservableCollection<DeviceImageConfig> DeviceImages { get; set; } = new();
    public SafetyConfig Safety { get; set; } = new();
    public NotifyConfig Notify { get; set; } = new();
    public EnergyConfig Energy { get; set; } = new();
    public RgbConfig Rgb { get; set; } = new();
    public MaintenanceConfig Maintenance { get; set; } = new();
    /// <summary>アプリの中からの更新（新しい版の確認）</summary>
    public UpdateConfig Update { get; set; } = new();
    /// <summary>前回起動時に見つかったハードウェア（部品の交換を検知するため）</summary>
    public List<KnownDevice> KnownDevices { get; set; } = new();
    /// <summary>設定で使っているセンサーの名前（部品交換・設定の読み込みで、新しいセンサーに対応付けるため）</summary>
    public Dictionary<string, SensorLabel> SensorLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>v1 形式（プロファイル導入前）の読み込み専用。保存はしない</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FanConfig>? Fans { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語をそのまま読める形で保存
    };

    public static AppConfig FromJson(string json)
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
                  ?? throw new InvalidDataException(L("config.json が空です。"));
        cfg.Normalize();
        return cfg;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>エンジンへ渡すための完全なコピー（画面側の編集がエンジンのスレッドに影響しないように）</summary>
    public AppConfig Clone() => FromJson(ToJson());

    public ProfileConfig? FindProfile(string? name)
        => Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    public CalibrationResult? FindCalibration(string controlId)
        => Calibrations.FirstOrDefault(c => string.Equals(c.ControlId, controlId, StringComparison.OrdinalIgnoreCase));

    /// <summary>範囲外の値を安全な範囲に丸め、古い形式を移行する（例外は投げず、壊れた設定でも起動できるようにする）</summary>
    public void Normalize()
    {
        // 手で編集した設定ファイルで null になっていても落ちないように、空の既定値で埋める
        Profiles ??= new();
        AutoSwitchRules ??= new();
        ScheduleRules ??= new();
        VirtualSensors ??= new();
        Calibrations ??= new();
        DashboardCards ??= new();
        DeviceImages ??= new();
        Widget ??= new();
        Appearance ??= new();
        Appearance.Normalize();
        Quick ??= new();
        Advisor ??= new();
        Web ??= new();
        Safety ??= new();
        Notify ??= new();
        Energy ??= new();
        Rgb ??= new();
        Maintenance ??= new();
        Update ??= new();
        KnownDevices ??= new();
        SensorLabels ??= new(StringComparer.OrdinalIgnoreCase);
        foreach (var r in AutoSwitchRules.Where(r => r is null).ToList()) AutoSwitchRules.Remove(r);
        foreach (var r in ScheduleRules.Where(r => r is null).ToList()) ScheduleRules.Remove(r);
        foreach (var p in Profiles.Where(p => p is null).ToList()) Profiles.Remove(p);

        SchemaVersion = Math.Max(SchemaVersion, CurrentSchemaVersion);
        PollingIntervalMs = Math.Clamp(PollingIntervalMs, 250, 5000);
        EmergencyTemperature = Math.Clamp(EmergencyTemperature, 60, 110);
        EmergencyHotspotTemperature = Math.Clamp(EmergencyHotspotTemperature, 90, 115);

        // v1 → v2: ファン一覧をそのまま「標準」にし、静音／パフォーマンスを自動生成する
        if (Profiles.Count == 0 && Fans is { Count: > 0 })
        {
            var standard = new ProfileConfig { Name = L("標準"), Role = ProfileRoles.Balanced, Fans = new ObservableCollection<FanConfig>(Fans) };
            Profiles.Add(CurvePresets.DeriveProfile(standard, L("静音"), CurvePresets.Silent));
            Profiles.Add(standard);
            Profiles.Add(CurvePresets.DeriveProfile(standard, L("パフォーマンス"), CurvePresets.Performance));
            ActiveProfile = L("標準");
            SetupCompleted = true;
        }
        Fans = null;

        if (Profiles.Count == 0) Profiles.Add(new ProfileConfig { Name = L("標準"), Role = ProfileRoles.Balanced });
        // 役割がまだないプロファイル（前の版の設定）は、名前から決める
        foreach (var p in Profiles) p.Role = string.IsNullOrEmpty(p.Role) ? ProfileRoles.FromName(p.Name) : ProfileRoles.Normalize(p.Role);

        // プロファイル名の前後の空白を取り、重複には番号を付けて分ける（ルールの参照先が分からなくならないように）。
        // 名前を変えたら、それを指していた設定（実行中・ルール・静音）も新しい名前に付け替える（最初に出てきたものを優先）
        var names = new HashSet<string>(StringComparer.Ordinal);
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var profile in Profiles)
        {
            string raw = profile.Name ?? "";
            string name = string.IsNullOrWhiteSpace(raw) ? L("プロファイル") : raw.Trim();
            string unique = name;
            for (int i = 2; !names.Add(unique); i++) unique = $"{name} ({i})";
            renamed.TryAdd(raw, unique);
            renamed.TryAdd(raw.Trim(), unique);
            if (profile.Name != unique) profile.Name = unique;
        }
        string Map(string? n) => n is not null && renamed.TryGetValue(n, out var m) ? m
            : n is not null && renamed.TryGetValue(n.Trim(), out var m2) ? m2 : n ?? "";
        ActiveProfile = Map(ActiveProfile);
        foreach (var r in AutoSwitchRules) r.ProfileName = Map(r.ProfileName);
        foreach (var r in ScheduleRules) r.ProfileName = Map(r.ProfileName);
        Quick.QuietProfile = Map(Quick.QuietProfile);

        if (FindProfile(ActiveProfile) is null) ActiveProfile = Profiles[0].Name;

        foreach (var profile in Profiles)
        {
            profile.Fans ??= new();
            // 同じ端子が 2 回出てくると、エンジンの準備（端子ごとの表）で失敗してファンを制御できなくなる。
            // 最初の 1 つだけ残す
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fan in profile.Fans.ToList())
            {
                if (fan is null) { profile.Fans.Remove(fan!); continue; }
                if (!string.IsNullOrEmpty(fan.ControlId) && !seen.Add(fan.ControlId))
                {
                    Logger.Warn($"設定: プロファイル「{profile.Name}」に同じ端子（{fan.ControlId}）が重複していたため、2 つ目以降を外しました");
                    profile.Fans.Remove(fan);
                }
            }
        }

        foreach (var fan in Profiles.SelectMany(p => p.Fans))
        {
            fan.Normalize();
        }
        foreach (var vs in VirtualSensors)
        {
            if (string.IsNullOrEmpty(vs.Id)) vs.Id = VirtualSensorConfig.NewId();
        }
        Widget.Opacity = Math.Clamp(Widget.Opacity, 0.3, 1.0);
        Web.Port = Math.Clamp(Web.Port, 1024, 65535);
        Web ??= new();
        // 鍵が空・短すぎるときは作り直す（空の鍵では誰でも入れてしまうため）
        if (string.IsNullOrWhiteSpace(Web.Token) || Web.Token.Trim().Length < WebServer.MinTokenLength) Web.Token = WebConfig.NewToken();
        if (string.IsNullOrWhiteSpace(Advisor.OllamaUrl)) Advisor.OllamaUrl = "http://localhost:11434";
        Safety.Normalize();
        Energy.PricePerKwh = Math.Clamp(Energy.PricePerKwh, 0, 200);
        Energy.BaseWatts = Math.Clamp(Energy.BaseWatts, 0, 1000);
        Rgb.Port = Math.Clamp(Rgb.Port, 1, 65535);
        if (Rgb.HighTemp - Rgb.LowTemp < 5) Rgb.HighTemp = Rgb.LowTemp + 5;
        Rgb.Speed = Math.Clamp(Rgb.Speed, 0.3, 10);
        Rgb.Brightness = Math.Clamp(Rgb.Brightness, 10, 100);
        Rgb.Sensitivity = Math.Clamp(Rgb.Sensitivity, 0.5, 3);
        Rgb.Music ??= new();
        Rgb.Music.Normalize();
        Rgb.DeviceSettings ??= new();
        Rgb.DeviceSettings.RemoveAll(d => d is null || string.IsNullOrEmpty(d.Name));
        foreach (var d in Rgb.DeviceSettings) d.Normalize();
        Rgb.DeviceSettings = Rgb.DeviceSettings.GroupBy(d => d.Name).Select(g => g.Last()).ToList();
        Rgb.Devices ??= new();
        Rgb.SwapRedGreen ??= new();
        if (!MusicStyles.All.Contains(Rgb.MusicStyle)) Rgb.MusicStyle = MusicStyles.BeatHue;
        if (Rgb.OnStop is not (RgbStopBehaviors.Off or RgbStopBehaviors.Static or RgbStopBehaviors.Keep)) Rgb.OnStop = RgbStopBehaviors.Off;
        if (string.IsNullOrWhiteSpace(Notify.NtfyServer)) Notify.NtfyServer = "https://ntfy.sh";
        if (string.IsNullOrWhiteSpace(Notify.NtfyTopic)) Notify.NtfyTopic = NotifyConfig.NewTopic();
        SensorLabels = new Dictionary<string, SensorLabel>(SensorLabels ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>設定のどこかで参照しているセンサー（温度・回転数）と制御端子の ID</summary>
    public IEnumerable<string> ReferencedSensorIds()
    {
        foreach (var f in Profiles.SelectMany(p => p.Fans))
        {
            foreach (var c in f.Curves) yield return c.SensorId;
            if (f.Mode == FanModes.Target && f.TargetSensorId.Length > 0) yield return f.TargetSensorId;
            if (f.RpmSensorId is { Length: > 0 } rpm) yield return rpm;
        }
        foreach (var v in VirtualSensors)
        {
            foreach (var s in v.Sources) yield return s;
        }
        foreach (var c in DashboardCards) yield return c.SensorId;
        if (Rgb.SensorId.Length > 0) yield return Rgb.SensorId;
    }

    public IEnumerable<string> ReferencedControlIds()
        => Profiles.SelectMany(p => p.Fans).Select(f => f.ControlId).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>ID を一括で置き換える（部品交換・設定の読み込みでの対応付け）。仮想センサーの ID はそのまま</summary>
    public void RemapIds(IReadOnlyDictionary<string, string> map)
    {
        string M(string id) => id is not null && map.TryGetValue(id, out var to) ? to : id!;
        foreach (var f in Profiles.SelectMany(p => p.Fans))
        {
            if (map.ContainsKey(f.ControlId))
            {
                var cal = FindCalibration(f.ControlId);
                f.ControlId = M(f.ControlId);
                if (cal is not null && FindCalibration(f.ControlId) is null) cal.ControlId = f.ControlId;
            }
            if (f.RpmSensorId is not null) f.RpmSensorId = M(f.RpmSensorId);
            if (f.TargetSensorId.Length > 0) f.TargetSensorId = M(f.TargetSensorId);
            if (f.FollowControlId.Length > 0) f.FollowControlId = M(f.FollowControlId);
            foreach (var c in f.Curves) c.SensorId = M(c.SensorId);
        }
        foreach (var v in VirtualSensors)
        {
            for (int i = 0; i < v.Sources.Count; i++) v.Sources[i] = M(v.Sources[i]);
        }
        foreach (var c in DashboardCards) c.SensorId = M(c.SensorId);
        if (Rgb.SensorId.Length > 0) Rgb.SensorId = M(Rgb.SensorId);
        foreach (var (from, to) in map)
        {
            if (SensorLabels.Remove(from, out var label) && !SensorLabels.ContainsKey(to)) SensorLabels[to] = label;
        }
    }
}

public sealed class ProfileConfig : ObservableObject
{
    private string _name = "標準";

    private string _powerPlanGuid = "";
    private string _role = "";

    public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>このプロファイルになったときに切り替える Windows の電源プラン（空なら変更しない）</summary>
    public string PowerPlanGuid { get => _powerPlanGuid; set { if (value is not null) Set(ref _powerPlanGuid, value); } }
    /// <summary>役割（ProfileRoles。空なら読み込むときに名前から決める）</summary>
    public string Role { get => _role; set => Set(ref _role, value ?? ""); }
    public ObservableCollection<FanConfig> Fans { get; set; } = new();
}

/// <summary>曜日と時刻でプロファイルを切り替える（例: 毎日 22:00〜7:00 は静音）</summary>
public sealed class ScheduleRule : ObservableObject
{
    public const string Everyday = "Everyday";
    public const string Weekdays = "Weekdays";
    public const string Weekends = "Weekends";

    private string _days = Everyday;
    private string _start = "22:00";
    private string _end = "07:00";
    private string _profileName = "静音";

    public string Days { get => _days; set { if (!string.IsNullOrEmpty(value)) Set(ref _days, value); } }
    public string Start { get => _start; set => Set(ref _start, value); }
    public string End { get => _end; set => Set(ref _end, value); }
    public string ProfileName { get => _profileName; set { if (!string.IsNullOrEmpty(value)) Set(ref _profileName, value); } }

    /// <summary>日付をまたぐ時間帯（22:00〜7:00）は、開始した日の曜日で判定する</summary>
    public bool Matches(DateTime now)
    {
        if (!TimeSpan.TryParse(Start, out var start) || !TimeSpan.TryParse(End, out var end)) return false;
        var t = now.TimeOfDay;
        DateTime day;
        if (start <= end)
        {
            if (t < start || t >= end) return false;
            day = now.Date;
        }
        else
        {
            if (t >= start) day = now.Date;
            else if (t < end) day = now.Date.AddDays(-1);
            else return false;
        }
        bool weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        return Days switch
        {
            Weekdays => !weekend,
            Weekends => weekend,
            _ => true,
        };
    }
}

/// <summary>指定したアプリ（プロセス）が起動している間、プロファイルを自動で切り替える</summary>
public sealed class AutoSwitchRule : ObservableObject
{
    private string _processName = "";
    private string _profileName = "パフォーマンス";

    /// <summary>拡張子なしのプロセス名（例: "eldenring"）。大文字小文字は区別しない</summary>
    public string ProcessName { get => _processName; set => Set(ref _processName, value); }
    public string ProfileName { get => _profileName; set { if (!string.IsNullOrEmpty(value)) Set(ref _profileName, value); } }
}

public static class FanModes
{
    public const string Curve = "Curve";
    /// <summary>目標温度を保つよう自動で回転数を調整する（PI 制御）</summary>
    public const string Target = "Target";
    public const string Fixed = "Fixed";
    /// <summary>別のファン（リーダー）と同じ回転にする＝ファングループ</summary>
    public const string Follow = "Follow";

    public static readonly string[] All = { Curve, Target, Fixed, Follow };
}

public static class FanRoles
{
    public const string Fan = "Fan";
    public const string Pump = "Pump";
}

public sealed class FanConfig : ObservableObject
{
    private string _name = "Fan";
    private string _controlId = "";
    private string? _rpmSensorId;
    private string _role = FanRoles.Fan;
    private bool _enabled = true;
    private string _mode = FanModes.Curve;
    private string _targetSensorId = "";
    private double _targetTemp = 75;
    private double _targetAggressiveness = 5;
    private string _followControlId = "";
    private double _followOffset;
    private bool _followMatchRpm = true;
    private double _fixedDuty = 60.0;
    private double _minDuty = 25.0;
    private double _maxStepUpPerTick = 8.0;
    private double _maxStepDownPerTick = 2.0;
    private double _pumpAlertRpm = 1000.0;

    public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>LHM の Control の Identifier（例: /lpc/nct6686d/0/control/2）</summary>
    public string ControlId { get => _controlId; set => Set(ref _controlId, value); }
    public string? RpmSensorId { get => _rpmSensorId; set => Set(ref _rpmSensorId, value); }
    /// <summary>"Fan" または "Pump"。Pump は下限 Duty の強制と回転数監視が有効になる</summary>
    public string Role { get => _role; set { if (Set(ref _role, value)) Raise(nameof(IsPump)); } }
    /// <summary>false のときはこのツールで制御せず BIOS に任せる（ポンプは監視だけ続ける）</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>"Curve" / "Target" / "Fixed" / "Follow"</summary>
    public string Mode
    {
        get => _mode;
        set
        {
            if (string.IsNullOrEmpty(value) || !Set(ref _mode, value)) return;
            Raise(nameof(UseFixedDuty));
        }
    }

    /// <summary>旧形式との互換用（固定回転モードかどうか）。新しい設定では Mode を使う</summary>
    public bool UseFixedDuty
    {
        get => Mode == FanModes.Fixed;
        set
        {
            if (value) Mode = FanModes.Fixed;
            else if (Mode == FanModes.Fixed) Mode = FanModes.Curve;
        }
    }

    /// <summary>目標温度モードで見る温度センサー</summary>
    public string TargetSensorId { get => _targetSensorId; set { if (!string.IsNullOrEmpty(value)) Set(ref _targetSensorId, value); } }
    public double TargetTemp { get => _targetTemp; set => Set(ref _targetTemp, value); }
    /// <summary>1（ゆったり）〜10（きびきび）。PI 制御のゲインの倍率</summary>
    public double TargetAggressiveness { get => _targetAggressiveness; set => Set(ref _targetAggressiveness, value); }
    /// <summary>連動モードで合わせる相手（リーダー）の ControlId</summary>
    public string FollowControlId { get => _followControlId; set { if (value is not null) Set(ref _followControlId, value); } }
    /// <summary>リーダーに対して上乗せする Duty（-30〜+30%）</summary>
    public double FollowOffset { get => _followOffset; set => Set(ref _followOffset, value); }
    /// <summary>両方キャリブレーション済みなら、Duty ではなく回転数（rpm）を揃える</summary>
    public bool FollowMatchRpm { get => _followMatchRpm; set => Set(ref _followMatchRpm, value); }
    public double FixedDuty { get => _fixedDuty; set => Set(ref _fixedDuty, value); }
    public double MinDuty { get => _minDuty; set => Set(ref _minDuty, value); }
    public double MaxStepUpPerTick { get => _maxStepUpPerTick; set => Set(ref _maxStepUpPerTick, value); }
    public double MaxStepDownPerTick { get => _maxStepDownPerTick; set => Set(ref _maxStepDownPerTick, value); }
    /// <summary>ポンプの回転数がこの値を下回り続けたら警告し、100% で保護する</summary>
    public double PumpAlertRpm { get => _pumpAlertRpm; set => Set(ref _pumpAlertRpm, value); }
    public ObservableCollection<CurveConfig> Curves { get; set; } = new();
    /// <summary>共振（ケースの「ブーン」という共鳴）を避ける Duty の帯。この帯の中には留まらず、素早く通り過ぎる</summary>
    public ObservableCollection<DutyBand> AvoidBands { get; set; } = new();

    [JsonIgnore]
    public bool IsPump => Role == FanRoles.Pump;

    /// <summary>GPU のファン（マザーボードの端子ではない）</summary>
    [JsonIgnore]
    public bool IsGpuFan => ControlId.StartsWith("/gpu", StringComparison.OrdinalIgnoreCase);

    /// <summary>プロファイルをまたいで共通にすべき「端子そのものの情報」</summary>
    public static readonly string[] HardwareProperties = { nameof(Name), nameof(Role), nameof(RpmSensorId), nameof(PumpAlertRpm) };

    public void Normalize()
    {
        MinDuty = Math.Clamp(MinDuty, 0, 100);
        FixedDuty = Math.Clamp(FixedDuty, 0, 100);
        MaxStepUpPerTick = Math.Clamp(MaxStepUpPerTick, 0.5, 100);
        MaxStepDownPerTick = Math.Clamp(MaxStepDownPerTick, 0.5, 100);
        if (Role != FanRoles.Pump) Role = FanRoles.Fan;
        if (!FanModes.All.Contains(Mode)) Mode = FanModes.Curve;
        if (string.IsNullOrEmpty(TargetSensorId) && Curves.FirstOrDefault()?.SensorId is { Length: > 0 } sid) TargetSensorId = sid;
        TargetTemp = Math.Clamp(TargetTemp, 30, 100);
        TargetAggressiveness = Math.Clamp(TargetAggressiveness, 1, 10);
        FollowOffset = Math.Clamp(FollowOffset, -30, 30);
        AvoidBands ??= new();
        foreach (var b in AvoidBands.ToList())
        {
            double lo = Math.Clamp(Math.Min(b.From, b.To), 0, 100), hi = Math.Clamp(Math.Max(b.From, b.To), 0, 100);
            if (hi - lo < 1) { AvoidBands.Remove(b); continue; }
            b.From = lo;
            b.To = hi;
        }

        foreach (var curve in Curves)
        {
            curve.EmaAlpha = Math.Clamp(curve.EmaAlpha, 0.05, 1.0);
            curve.HysteresisCelsius = Math.Clamp(curve.HysteresisCelsius, 0, 15);
            foreach (var p in curve.Points)
            {
                p.Temp = Math.Clamp(p.Temp, -50, 150);
                p.Duty = Math.Clamp(p.Duty, 0, 100);
            }
            if (curve.Points.Count < 2) curve.Points = CurveConfig.DefaultPoints();
            curve.SortPoints();
        }
    }

    public FanConfig DeepCopy() => JsonSerializer.Deserialize<FanConfig>(JsonSerializer.Serialize(this))!;
}

public sealed class CurveConfig : ObservableObject
{
    private string _name = "カーブ";
    private string _sensorId = "";
    private double _hysteresisCelsius = 4.0;
    private double _emaAlpha = 0.2;

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>
    /// 温度センサーの Identifier（仮想センサーは /virtual/...）。
    /// 画面の ComboBox は一覧に無い値を null で書き戻すことがあるため、null/空は無視して設定を壊さない。
    /// </summary>
    public string SensorId
    {
        get => _sensorId;
        set { if (!string.IsNullOrEmpty(value)) Set(ref _sensorId, value); }
    }

    public double HysteresisCelsius { get => _hysteresisCelsius; set => Set(ref _hysteresisCelsius, value); }
    public double EmaAlpha { get => _emaAlpha; set => Set(ref _emaAlpha, value); }
    public List<CurvePoint> Points { get; set; } = DefaultPoints();

    public void SortPoints() => Points.Sort((a, b) => a.Temp.CompareTo(b.Temp));

    public CurveConfig DeepCopy() => JsonSerializer.Deserialize<CurveConfig>(JsonSerializer.Serialize(this))!;

    public static List<CurvePoint> DefaultPoints() => new()
    {
        new() { Temp = 40, Duty = 30 },
        new() { Temp = 60, Duty = 45 },
        new() { Temp = 75, Duty = 70 },
        new() { Temp = 90, Duty = 100 },
    };
}

public sealed class CurvePoint : ObservableObject
{
    private double _temp;
    private double _duty;

    public double Temp { get => _temp; set => Set(ref _temp, value); }
    public double Duty { get => _duty; set => Set(ref _duty, value); }
}

/// <summary>避ける Duty の範囲（From〜To %）</summary>
public sealed class DutyBand : ObservableObject
{
    private double _from = 40;
    private double _to = 50;

    public double From { get => _from; set => Set(ref _from, value); }
    public double To { get => _to; set => Set(ref _to, value); }

    /// <summary>帯の内側（端は含まない）か</summary>
    public bool Contains(double duty) => duty > From && duty < To;
}

public static class VirtualSensorKinds
{
    public const string Max = "Max";
    public const string Min = "Min";
    public const string Average = "Average";
    /// <summary>Sources[0] − Sources[1]（例: 水温 − 室温）</summary>
    public const string Delta = "Delta";
}

/// <summary>複数のセンサーをまとめた計算上のセンサー。カーブの連動先として使える</summary>
public sealed class VirtualSensorConfig : ObservableObject
{
    private string _id = "";
    private string _name = "仮想センサー";
    private string _kind = VirtualSensorKinds.Max;

    public const string IdPrefix = "/virtual/";

    public string Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Kind { get => _kind; set => Set(ref _kind, value); }
    public ObservableCollection<string> Sources { get; set; } = new();

    public static string NewId() => IdPrefix + Guid.NewGuid().ToString("N")[..8];

    [JsonIgnore]
    public string KindText => Kind switch
    {
        VirtualSensorKinds.Max => L("最大値"),
        VirtualSensorKinds.Min => L("最小値"),
        VirtualSensorKinds.Average => L("平均値"),
        VirtualSensorKinds.Delta => L("差分"),
        _ => Kind,
    };
}

/// <summary>自動キャリブレーションの結果（端子ごと・プロファイル共通）</summary>
public sealed class CalibrationResult
{
    public string ControlId { get; set; } = "";
    public double MaxRpm { get; set; }
    /// <summary>止まった状態から回り始める Duty</summary>
    public double StartDuty { get; set; }
    /// <summary>回っている状態から下げていき、止まる直前の Duty（これ以上なら回り続ける）</summary>
    public double StopDuty { get; set; }
    public List<CalibrationPoint> Points { get; set; } = new();
    public DateTime MeasuredAt { get; set; }

    /// <summary>Duty から回転数を推定する（測定点の線形補間）</summary>
    public double? EstimateRpm(double duty)
    {
        var pts = Points.OrderBy(p => p.Duty).ToList();
        if (pts.Count < 2) return null;
        if (duty <= pts[0].Duty) return pts[0].Rpm;
        if (duty >= pts[^1].Duty) return pts[^1].Rpm;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            if (duty <= pts[i + 1].Duty)
            {
                double range = pts[i + 1].Duty - pts[i].Duty;
                return range <= 0 ? pts[i + 1].Rpm : pts[i].Rpm + (duty - pts[i].Duty) / range * (pts[i + 1].Rpm - pts[i].Rpm);
            }
        }
        return pts[^1].Rpm;
    }
}

public static class CalibrationExtensions
{
    /// <summary>回転数から Duty を逆算する（回っている測定点だけを使う）</summary>
    public static double? EstimateDuty(this CalibrationResult cal, double rpm)
    {
        var pts = cal.Points.Where(p => p.Rpm > 50).OrderBy(p => p.Rpm).ToList();
        if (pts.Count < 2) return null;
        if (rpm <= pts[0].Rpm) return pts[0].Duty;
        if (rpm >= pts[^1].Rpm) return pts[^1].Duty;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            if (rpm <= pts[i + 1].Rpm)
            {
                double range = pts[i + 1].Rpm - pts[i].Rpm;
                return range <= 0 ? pts[i + 1].Duty : pts[i].Duty + (rpm - pts[i].Rpm) / range * (pts[i + 1].Duty - pts[i].Duty);
            }
        }
        return pts[^1].Duty;
    }
}

public sealed class CalibrationPoint
{
    public double Duty { get; set; }
    public double Rpm { get; set; }
}

public sealed class DashboardCard
{
    public string SensorId { get; set; } = "";
    public string Title { get; set; } = "";
    public double Warn { get; set; } = 75;
    public double Danger { get; set; } = 90;
}

public sealed class DeviceImageConfig
{
    public string DeviceKey { get; set; } = "";
    public string FileName { get; set; } = "";
}

public sealed class QuickConfig
{
    /// <summary>「静音」ワンタッチ操作で一時的に使うプロファイル</summary>
    public string QuietProfile { get; set; } = "静音";
    /// <summary>Ctrl+Alt+B / Q / N のグローバルホットキー</summary>
    public bool HotkeysEnabled { get; set; } = true;
}

public sealed class AdvisorConfig
{
    /// <summary>AI 冷却アドバイザーを使うか（実験的な機能。既定はオフで、ツールの画面に出さず、Ollama にも問い合わせない）</summary>
    public bool Enabled { get; set; }
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "";
}

public sealed class WebConfig
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8090;
    /// <summary>スマホからのアクセスに必要な鍵（URL に含める）</summary>
    public string Token { get; set; } = "";

    /// <summary>新しい鍵（16 バイトの乱数 = 32 文字）</summary>
    public static string NewToken() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

/// <summary>安全機能（見張りプロセス・異常値の除外・部品の交換検知・GPU ファン）</summary>
public sealed class SafetyConfig : ObservableObject
{
    private bool _watchdogEnabled = true;
    private bool _watchdogRestart = true;
    private double _watchdogSafeDuty = 70;
    private bool _sensorFilterEnabled = true;
    private double _missingSensorDuty = 60;
    private bool _hardwareCheckEnabled = true;
    private bool _allowGpuFanControl = false;
    private bool _flightRecorderEnabled = true;
    private bool _readMemoryTemperature = true;

    /// <summary>本体が強制終了・フリーズしたら、ファンを安全な回転数にする見張りプロセスを使う</summary>
    public bool WatchdogEnabled { get => _watchdogEnabled; set => Set(ref _watchdogEnabled, value); }
    /// <summary>本体が落ちたら自動で起動し直す</summary>
    public bool WatchdogRestart { get => _watchdogRestart; set => Set(ref _watchdogRestart, value); }
    /// <summary>見張りプロセスが本体の代わりに設定する回転数（ポンプは 100%）</summary>
    public double WatchdogSafeDuty { get => _watchdogSafeDuty; set => Set(ref _watchdogSafeDuty, value); }
    /// <summary>一瞬だけ跳ねた値や、負荷が変わっても動かない（固まった）温度を制御に使わない</summary>
    public bool SensorFilterEnabled { get => _sensorFilterEnabled; set => Set(ref _sensorFilterEnabled, value); }
    /// <summary>センサーそのものが見つからない（部品を外した・交換した）ときに、そのカーブが要求する回転数</summary>
    public double MissingSensorDuty { get => _missingSensorDuty; set => Set(ref _missingSensorDuty, value); }
    /// <summary>起動時に部品の交換を確認する</summary>
    public bool HardwareCheckEnabled { get => _hardwareCheckEnabled; set => Set(ref _hardwareCheckEnabled, value); }
    /// <summary>GPU のファンも制御対象にする（試験的）</summary>
    public bool AllowGpuFanControl { get => _allowGpuFanControl; set => Set(ref _allowGpuFanControl, value); }
    /// <summary>危険な状態になったとき、前後 10 分の記録を自動で保存する</summary>
    public bool FlightRecorderEnabled { get => _flightRecorderEnabled; set => Set(ref _flightRecorderEnabled, value); }
    /// <summary>
    /// メモリ（DIMM）の温度を読む。読むたびにメモリの配線（SMBus）を使うので、OpenRGB などが同じ配線を
    /// 順番待ちせずに使っていると、ぶつかってメモリの温度が読めなくなることがある。オフにすると読まない
    /// （メモリの温度を使うカーブは「センサーが見つからない」扱いになり、安全な回転数で回る）
    /// </summary>
    public bool ReadMemoryTemperature { get => _readMemoryTemperature; set => Set(ref _readMemoryTemperature, value); }

    public void Normalize()
    {
        WatchdogSafeDuty = Math.Clamp(WatchdogSafeDuty, 40, 100);
        MissingSensorDuty = Math.Clamp(MissingSensorDuty, 30, 100);
    }
}

public static class NotifyServices
{
    public const string Ntfy = "Ntfy";
    public const string Discord = "Discord";
}

/// <summary>スマホへの通知（ntfy または Discord）</summary>
public sealed class NotifyConfig : ObservableObject
{
    private bool _enabled = false;
    private string _service = NotifyServices.Ntfy;
    private string _ntfyServer = "https://ntfy.sh";
    private string _ntfyTopic = "";
    private string _discordWebhook = "";
    private bool _includeWarnings = true;
    private bool _includeRecovery = true;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>"Ntfy" / "Discord"</summary>
    public string Service { get => _service; set => Set(ref _service, value); }
    public string NtfyServer { get => _ntfyServer; set => Set(ref _ntfyServer, value); }
    /// <summary>ntfy のトピック名。名前を知っている人は誰でも読めるので、推測されにくいランダムな名前にする</summary>
    public string NtfyTopic { get => _ntfyTopic; set => Set(ref _ntfyTopic, value); }
    public string DiscordWebhook { get => _discordWebhook; set => Set(ref _discordWebhook, value); }
    /// <summary>true なら「注意」も通知する（false なら「警告」だけ）</summary>
    public bool IncludeWarnings { get => _includeWarnings; set => Set(ref _includeWarnings, value); }
    /// <summary>温度が下がった・ポンプが回復した、などの「回復」も通知する</summary>
    public bool IncludeRecovery { get => _includeRecovery; set => Set(ref _includeRecovery, value); }

    public static string NewTopic() => "fanlume-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
}

/// <summary>消費電力量と電気代の計算</summary>
public sealed class EnergyConfig : ObservableObject
{
    private double _pricePerKwh = 31;
    private double _baseWatts = 50;

    /// <summary>1 kWh あたりの電気代（円）。全国家庭電気製品公正取引協議会の目安単価は 31 円</summary>
    public double PricePerKwh { get => _pricePerKwh; set => Set(ref _pricePerKwh, value); }
    /// <summary>CPU と GPU 以外（マザーボード・メモリ・SSD・ファン・電源の損失など）の目安（W）</summary>
    public double BaseWatts { get => _baseWatts; set => Set(ref _baseWatts, value); }
}

/// <summary>OpenRGB と連携して、温度に合わせて LED の色を変える</summary>
public sealed class RgbConfig : ObservableObject
{
    /// <summary>この版が知らない LED の設定（新しい版の設定を消さないため）</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    private bool _enabled = false;
    private string _host = "127.0.0.1";
    private int _port = 6742;
    private string _sensorId = "";
    private double _lowTemp = 40;
    private double _highTemp = 85;
    private bool _blinkOnEmergency = true;
    private bool _directGpuWrite = true;
    private bool _directGpuUnverified;
    private bool _matchSlowDevices = true;
    private string _mode = RgbModes.Temperature;
    private double _speed = 1.0;
    private double _brightness = 100;
    private string _staticColor = "#6A00FF";
    private string _musicStyle = MusicStyles.BeatHue;
    private string _musicSource = "";
    private double _sensitivity = 1.0;
    private bool _gamma = true;
    private bool _lightsOff;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>消灯: LED の制御は続けたまま、すべて黒にする（連携をオフにすると、機器が自分の光り方に戻ってしまうため）</summary>
    public bool LightsOff { get => _lightsOff; set => Set(ref _lightsOff, value); }
    public string Host { get => _host; set => Set(ref _host, value); }
    public int Port { get => _port; set => Set(ref _port, value); }
    /// <summary>色を決める温度センサー（空なら CPU と GPU の高いほう）</summary>
    public string SensorId { get => _sensorId; set => Set(ref _sensorId, value ?? ""); }
    public double LowTemp { get => _lowTemp; set => Set(ref _lowTemp, value); }
    public double HighTemp { get => _highTemp; set => Set(ref _highTemp, value); }
    /// <summary>色を変えるデバイス名（空なら全部）</summary>
    public List<string> Devices { get; set; } = new();
    /// <summary>赤と緑を入れ替えて送るデバイス名（色の並び順が逆のマザーボードなど）</summary>
    public List<string> SwapRedGreen { get; set; } = new();
    /// <summary>緊急全開のときに赤く点滅させる</summary>
    public bool BlinkOnEmergency { get => _blinkOnEmergency; set => Set(ref _blinkOnEmergency, value); }
    /// <summary>対応しているグラボ（PNY ARGB Epic-X）は OpenRGB を通さず直接書き込む（オフなら OpenRGB を通す）</summary>
    public bool DirectGpuWrite { get => _directGpuWrite; set => Set(ref _directGpuWrite, value); }
    /// <summary>実機で確かめていない型番のグラボ（書き方は同じと分かっているもの）にも、直接書き込む（初期値はオフ）</summary>
    public bool DirectGpuUnverified { get => _directGpuUnverified; set => Set(ref _directGpuUnverified, value); }
    /// <summary>
    /// 音楽連動で、グラボ・メモリなど更新の遅いデバイスは、ほかの（速い）デバイスがいま光っている色に合わせる
    /// （オフなら、遅いデバイス用の光り方で光る。デバイスごとに光り方を選んだデバイスは、選んだ光り方のまま）
    /// </summary>
    public bool MatchSlowDevices { get => _matchSlowDevices; set => Set(ref _matchSlowDevices, value); }
    /// <summary>
    /// ケースの中の位置に合わせて光を流す（ゾーンごとに「場所」を選んだ LED は、ケース全体で 1 本の帯として光る。
    /// 場所を選んでいない LED は、これまでどおりデバイスの中の並びで光る）
    /// </summary>
    public bool CaseLayout { get => _caseLayout; set => Set(ref _caseLayout, value); }
    /// <summary>ケースの中で光が流れる向き（CaseDirections の値）</summary>
    public string CaseDirection { get => _caseDirection; set { if (!string.IsNullOrEmpty(value)) Set(ref _caseDirection, value); } }
    private bool _caseLayout;
    private string _caseDirection = CaseDirections.Up;
    /// <summary>光り方: Temperature / Rainbow / RainbowStatic / Breathing / Static / Music</summary>
    public string Mode { get => _mode; set { if (!string.IsNullOrEmpty(value)) Set(ref _mode, value); } }
    /// <summary>レインボー・呼吸の速さ（0.3〜10 倍）</summary>
    public double Speed { get => _speed; set => Set(ref _speed, value); }
    /// <summary>明るさ（10〜100%）</summary>
    public double Brightness { get => _brightness; set => Set(ref _brightness, value); }
    /// <summary>固定色・呼吸・音楽（ビート）の色 "#RRGGBB"</summary>
    public string StaticColor { get => _staticColor; set { if (!string.IsNullOrEmpty(value)) Set(ref _staticColor, value); } }
    /// <summary>音楽連動の光り方: BeatHue / Mix / Pulse / Meter</summary>
    public string MusicStyle { get => _musicStyle; set { if (!string.IsNullOrEmpty(value)) Set(ref _musicStyle, value); } }
    /// <summary>反応する音（空ならすべての音。アプリ名ならそのアプリの音が鳴っているときだけ）</summary>
    public string MusicSource { get => _musicSource; set => Set(ref _musicSource, value ?? ""); }
    /// <summary>音楽連動の感度（0.5〜3）</summary>
    public double Sensitivity { get => _sensitivity; set => Set(ref _sensitivity, value); }
    /// <summary>ガンマ補正（LED の色を画面の色に近い濃い色にする）</summary>
    public bool Gamma { get => _gamma; set => Set(ref _gamma, value); }
    /// <summary>連携をやめる・アプリを終了するときの LED: Off（消す）/ Static（固定色にする）/ Keep（そのまま）</summary>
    public string OnStop { get => _onStop; set { if (!string.IsNullOrEmpty(value)) Set(ref _onStop, value); } }
    /// <summary>音楽連動の詳しい設定</summary>
    public MusicConfig Music { get; set; } = new();
    /// <summary>デバイスごとの設定（光り方の上書き・向きなど）。ないデバイスは既定のまま</summary>
    public List<RgbDeviceSettings> DeviceSettings { get; set; } = new();

    private string _onStop = RgbStopBehaviors.Off;

    /// <summary>デバイスの設定（なければ既定値。一覧には追加しない）</summary>
    public RgbDeviceSettings SettingsFor(string deviceName) =>
        DeviceSettings.FirstOrDefault(d => d.Name == deviceName) ?? new RgbDeviceSettings { Name = deviceName };
}

public static class RgbStopBehaviors
{
    public const string Off = "Off";
    public const string Static = "Static";
    public const string Keep = "Keep";
}

/// <summary>
/// 音楽連動の詳しい設定。「プリセット」「強さ」を選ぶと下の値がまとめて書き換わる（MusicTuning.ApplyPreset / ApplyIntensity）。
/// 光り方そのものは RgbConfig.MusicStyle、反応の感度（ビートの検出）は RgbConfig.Sensitivity
/// </summary>
public sealed class MusicConfig : ObservableObject
{
    private string _preset = MusicPresets.Custom;
    private string _intensity = Intensities.Moderate;
    private string _palette = Palettes.Rainbow;
    private double _gainDb = 0;
    private double _reactionMs = 8;
    private double _smoothnessMs = 220;
    private double _punch = 55;
    private double _minBrightness = 17;
    private double _dynamicRangeDb = 40;
    private double _autoBalance = 35;
    private double _trebleTilt = 1.5;
    private double _contrast = 1.0;
    private double _audioDelayMs = 0;
    private bool _reduceFlashing = true;
    private string _tempoMode = TempoModes.Auto;
    private double _tempoFactor = 1.0;
    private string _idleEffect = IdleEffects.Mood;
    private double _idleDelaySec = 2;

    /// <summary>プリセット: Chill / Groove / Party / Club / Visualizer / Cinema / Custom（表示用。値を変えたら Custom）</summary>
    public string Preset { get => _preset; set { if (!string.IsNullOrEmpty(value)) Set(ref _preset, value); } }
    /// <summary>強さ: Subtle / Moderate / High / Intense（ビートの光の強さ・色の切り替えの間隔に使う）</summary>
    public string Intensity { get => _intensity; set { if (!string.IsNullOrEmpty(value)) Set(ref _intensity, value); } }
    /// <summary>色の組み合わせ（Palettes の名前）</summary>
    public string Palette { get => _palette; set { if (!string.IsNullOrEmpty(value)) Set(ref _palette, value); } }
    /// <summary>感度（dB、−12〜+12）。+ にすると小さな音でも明るく光る</summary>
    public double GainDb { get => _gainDb; set => Set(ref _gainDb, value); }
    /// <summary>反応の速さ（ミリ秒、0〜40）。小さいほど音の立ち上がりにすぐ反応する</summary>
    public double ReactionMs { get => _reactionMs; set => Set(ref _reactionMs, value); }
    /// <summary>滑らかさ（ミリ秒、60〜600）。大きいほどゆっくり暗くなる</summary>
    public double SmoothnessMs { get => _smoothnessMs; set => Set(ref _smoothnessMs, value); }
    /// <summary>メリハリ（%、0〜100）。大きいほどドラムの瞬間だけ強く光る</summary>
    public double Punch { get => _punch; set => Set(ref _punch, value); }
    /// <summary>いちばん暗いときの明るさ（%、0〜40）</summary>
    public double MinBrightness { get => _minBrightness; set => Set(ref _minBrightness, value); }
    /// <summary>光の明るさに対応させる音量の幅（dB、24〜60）</summary>
    public double DynamicRangeDb { get => _dynamicRangeDb; set => Set(ref _dynamicRangeDb, value); }
    /// <summary>帯ごとの偏りを自動でならす強さ（%、0〜100）</summary>
    public double AutoBalance { get => _autoBalance; set => Set(ref _autoBalance, value); }
    /// <summary>高音の持ち上げ（dB／オクターブ、0〜4.5）</summary>
    public double TrebleTilt { get => _trebleTilt; set => Set(ref _trebleTilt, value); }
    /// <summary>コントラスト（0.7〜2.0）</summary>
    public double Contrast { get => _contrast; set => Set(ref _contrast, value); }
    /// <summary>音の遅れの補正（ミリ秒、−100〜+400）。Bluetooth のイヤホンなど、音が遅れて聞こえるときに + にする</summary>
    public double AudioDelayMs { get => _audioDelayMs; set => Set(ref _audioDelayMs, value); }
    /// <summary>激しい点滅を抑える（1 秒に 3 回まで。ストロボは使わない）</summary>
    public bool ReduceFlashing { get => _reduceFlashing; set => Set(ref _reduceFlashing, value); }
    /// <summary>テンポの追跡: Auto / KickOnly / Off</summary>
    public string TempoMode { get => _tempoMode; set { if (!string.IsNullOrEmpty(value)) Set(ref _tempoMode, value); } }
    /// <summary>テンポの倍率（0.5 / 1 / 2）</summary>
    public double TempoFactor { get => _tempoFactor; set => Set(ref _tempoFactor, value); }
    /// <summary>音が止まっているときの光り方: Mood / Breathing / Static / Off</summary>
    public string IdleEffect { get => _idleEffect; set { if (!string.IsNullOrEmpty(value)) Set(ref _idleEffect, value); } }
    /// <summary>音が止まってから、止まっているときの光り方に切り替えるまでの秒数（1〜10）</summary>
    public double IdleDelaySec { get => _idleDelaySec; set => Set(ref _idleDelaySec, value); }

    public void Normalize()
    {
        if (!MusicPresets.All.Contains(Preset)) Preset = MusicPresets.Custom;
        if (!Intensities.All.Contains(Intensity)) Intensity = Intensities.Moderate;
        if (!Palettes.All.Contains(Palette)) Palette = Palettes.Rainbow;
        GainDb = Math.Clamp(GainDb, -12, 12);
        ReactionMs = Math.Clamp(ReactionMs, 0, 40);
        SmoothnessMs = Math.Clamp(SmoothnessMs, 60, 600);
        Punch = Math.Clamp(Punch, 0, 100);
        MinBrightness = Math.Clamp(MinBrightness, 0, 40);
        DynamicRangeDb = Math.Clamp(DynamicRangeDb, 24, 60);
        AutoBalance = Math.Clamp(AutoBalance, 0, 100);
        TrebleTilt = Math.Clamp(TrebleTilt, 0, 4.5);
        Contrast = Math.Clamp(Contrast, 0.7, 2.0);
        AudioDelayMs = Math.Clamp(AudioDelayMs, -100, 400);
        if (TempoMode is not (TempoModes.Auto or TempoModes.KickOnly or TempoModes.Off)) TempoMode = TempoModes.Auto;
        TempoFactor = TempoFactor <= 0.75 ? 0.5 : TempoFactor >= 1.5 ? 2.0 : 1.0;
        if (!IdleEffects.All.Contains(IdleEffect)) IdleEffect = IdleEffects.Mood;
        IdleDelaySec = Math.Clamp(IdleDelaySec, 1, 10);
        UpgradeOldIntensityDefaults();
    }

    /// <summary>
    /// 6.5.1 までの「強さ」の値（反応の速さ・滑らかさ・メリハリ・いちばん暗い明るさ）のまま変えていなければ、
    /// 今の値（音楽に合っていると分かりやすい、明暗の差の大きい値）にする。1 つでも自分で変えた値があれば、そのまま
    /// </summary>
    private void UpgradeOldIntensityDefaults()
    {
        // これまでの版の値（6.5.1 まで、6.6.0 の開発中のいちど目）のどれかと全部同じなら、まだ自分で変えていないとみなす
        (double R, double S, double P, double M)[] olds = Intensity switch
        {
            Intensities.Subtle => new[] { (40.0, 600.0, 30.0, 35.0), (40.0, 500.0, 35.0, 25.0) },
            Intensities.Moderate => new[] { (10.0, 250.0, 50.0, 20.0), (6.0, 200.0, 62.0, 10.0), (6.0, 200.0, 58.0, 14.0) },
            Intensities.High => new[] { (4.0, 160.0, 65.0, 12.0), (3.0, 150.0, 72.0, 6.0) },
            Intensities.Intense => new[] { (0.0, 90.0, 80.0, 4.0) },
            _ => Array.Empty<(double, double, double, double)>(),
        };
        if (!olds.Any(o => ReactionMs == o.R && SmoothnessMs == o.S && Punch == o.P && MinBrightness == o.M)) return;
        var p = MusicTuning.Profile(Intensity);
        ReactionMs = p.ReactionMs;
        SmoothnessMs = p.SmoothnessMs;
        Punch = p.Punch;
        MinBrightness = p.MinBrightness;
    }
}

/// <summary>デバイスごとの設定</summary>
public sealed class RgbDeviceSettings
{
    /// <summary>OpenRGB のデバイス名</summary>
    public string Name { get; set; } = "";
    /// <summary>音楽連動の光り方の上書き（"Auto" なら全体の設定。更新の遅いデバイスは自動で滑らかな光り方に置き換える）</summary>
    public string Effect { get; set; } = "Auto";
    /// <summary>中央から左右対称にする（長い LED テープ向け）。null なら自動（LED が 30 個以上のゾーンでオン）</summary>
    public bool? Mirror { get; set; }
    /// <summary>LED の並びを逆向きにする</summary>
    public bool Reverse { get; set; }
    /// <summary>1 秒に送る回数の上限（0 なら自動: USB 60・I2C 8）</summary>
    public int MaxFps { get; set; }
    /// <summary>LED に反映されるまでの遅れ（ミリ秒。−1 なら自動: USB 8・I2C 20）</summary>
    public int LatencyMs { get; set; } = -1;
    /// <summary>白い LED だけのデバイス（Logitech G610 など）。R・G・B の最大を白として送る</summary>
    public bool WhiteOnly { get; set; }
    /// <summary>
    /// グラボでも「レインボー（流れる）」「呼吸」を動かす（既定はオフ: グラボは止まった虹・固定色にする。
    /// グラボは LED を 1 個ずつ書くため、動かし続けるとちかちかすることがある）
    /// </summary>
    public bool AnimateOnGpu { get; set; }
    /// <summary>色合わせ: 赤・緑・青の強さ（0〜100%。LED ごとの色味のずれを、白を見比べて合わせる）</summary>
    public int GainR { get; set; } = 100;
    public int GainG { get; set; } = 100;
    public int GainB { get; set; } = 100;
    /// <summary>このデバイスだけの明るさ（0〜100%。全体の明るさに掛ける）</summary>
    public int Level { get; set; } = 100;
    /// <summary>ゾーン（マザーボードの端子ごとなど）の設定。書かれていないゾーンはデバイスの設定のまま</summary>
    public List<RgbZoneSettings> Zones { get; set; } = new();

    /// <summary>ゾーンの設定（名前で探し、なければ null）</summary>
    public RgbZoneSettings? ZoneFor(string zoneName)
    {
        if (Zones is null) return null;
        foreach (var z in Zones)
            if (z is not null && z.Name == zoneName) return z;
        return null;
    }

    public void Normalize()
    {
        Name ??= "";
        if (string.IsNullOrEmpty(Effect)) Effect = "Auto";
        MaxFps = MaxFps <= 0 ? 0 : Math.Clamp(MaxFps, 1, 60);
        LatencyMs = LatencyMs < 0 ? -1 : Math.Clamp(LatencyMs, 0, 200);
        GainR = Math.Clamp(GainR, 0, 100);
        GainG = Math.Clamp(GainG, 0, 100);
        GainB = Math.Clamp(GainB, 0, 100);
        Level = Math.Clamp(Level, 0, 100);
        Zones ??= new();
        Zones.RemoveAll(z => z is null || string.IsNullOrEmpty(z.Name));
        foreach (var z in Zones) z.Normalize();
        Zones = Zones.GroupBy(z => z.Name).Select(g => g.Last()).Where(z => !z.IsDefault).ToList();
    }
}

/// <summary>
/// ゾーン 1 つの設定（ASRock の「Addressable Header 2」など、マザーボードの端子ごと）。
/// 同じデバイスでも、つないだ物（ファン・電源・水冷のヘッド）ごとに光り方・向き・色味を変えられる
/// </summary>
public sealed class RgbZoneSettings
{
    /// <summary>この値の光り方は「デバイスの設定のまま」</summary>
    public const string SameAsDevice = "Device";

    /// <summary>OpenRGB のゾーン名</summary>
    public string Name { get; set; } = "";
    /// <summary>音楽連動の光り方（SameAsDevice ならデバイスの設定。"Auto" なら全体の設定）</summary>
    public string Effect { get; set; } = SameAsDevice;
    /// <summary>左右対称（null ならデバイスの設定）</summary>
    public bool? Mirror { get; set; }
    /// <summary>逆向き（null ならデバイスの設定）</summary>
    public bool? Reverse { get; set; }
    /// <summary>色合わせ（0〜100%。デバイスの色合わせに掛ける）</summary>
    public int GainR { get; set; } = 100;
    public int GainG { get; set; } = 100;
    public int GainB { get; set; } = 100;
    /// <summary>このゾーンだけの明るさ（0〜100%）</summary>
    public int Level { get; set; } = 100;
    /// <summary>反応する音（MusicSounds。光り方「パーツごとの役割」で使う。Auto ならパーツに合わせる）</summary>
    public string Sound { get; set; } = MusicSounds.Auto;
    /// <summary>ケースの中の場所（CaseLayout の場所の Id。空なら場所を決めていない）</summary>
    public string Place { get; set; } = "";

    [JsonIgnore]
    public bool IsDefault => Effect == SameAsDevice && Mirror is null && Reverse is null
        && GainR == 100 && GainG == 100 && GainB == 100 && Level == 100 && string.IsNullOrEmpty(Place) && Sound == MusicSounds.Auto;

    public void Normalize()
    {
        Name ??= "";
        Place ??= "";
        Sound = MusicSounds.Normalize(Sound);
        if (string.IsNullOrEmpty(Effect)) Effect = SameAsDevice;
        GainR = Math.Clamp(GainR, 0, 100);
        GainG = Math.Clamp(GainG, 0, 100);
        GainB = Math.Clamp(GainB, 0, 100);
        Level = Math.Clamp(Level, 0, 100);
    }
}

/// <summary>ファンの劣化・ホコリの検知</summary>
public sealed class MaintenanceConfig
{
    /// <summary>「掃除した」を押した日時。これ以降のデータを新しい基準にする</summary>
    public DateTime? LastCleaned { get; set; }
    /// <summary>最後にメンテナンスの通知を出した日時（同じ通知を毎日出さない）</summary>
    public DateTime? LastNotified { get; set; }
    /// <summary>
    /// 冷え方の判定で「室温」として使うセンサー。空 = 自動（マザーボードの System・Motherboard）、
    /// "none" = 使わない（休んでいるときとの差だけで比べる）、それ以外 = そのセンサーの ID
    /// </summary>
    public string? AmbientSensorId { get; set; }

    /// <summary>室温のセンサーを使わないときの AmbientSensorId</summary>
    public const string NoAmbientSensor = "none";
}

/// <summary>アプリの中からの更新</summary>
public sealed class UpdateConfig : ObservableObject
{
    private bool _autoCheck = true;

    /// <summary>起動したとき（と 6 時間ごと）に、1 日 1 回まで GitHub に新しい版を問い合わせる</summary>
    public bool AutoCheck { get => _autoCheck; set => Set(ref _autoCheck, value); }
    /// <summary>最後に新しい版を確かめた日時</summary>
    public DateTime? LastChecked { get; set; }
    /// <summary>お知らせを出した新しい版（同じ版のお知らせは 1 回だけ）</summary>
    public string? NotifiedVersion { get; set; }
    /// <summary>前回起動したときの版（変わっていたら「更新しました」と知らせる）</summary>
    public string? LastRunVersion { get; set; }
    /// <summary>いまの版の前に使っていた版（新しい版で困ったときに戻すため）</summary>
    public string? PreviousVersion { get; set; }
    /// <summary>いまの版に変わった日時</summary>
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>前回起動時にあったハードウェア</summary>
public sealed class KnownDevice
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
}

/// <summary>センサーの名前（ID が変わったときに、同じ意味のセンサーを探すために使う）</summary>
public sealed class SensorLabel
{
    public string Name { get; set; } = "";
    public string Hardware { get; set; } = "";
    /// <summary>LHM の HardwareType（Cpu / GpuAmd / GpuNvidia / SuperIO など）</summary>
    public string HardwareType { get; set; } = "";
    public string SensorType { get; set; } = "";
}

/// <summary>テーマの選び方</summary>
public static class AppThemes
{
    /// <summary>Windows の「アプリ モード」（ダーク／ライト）に合わせる</summary>
    public const string System = "System";
    public const string Dark = "Dark";
    public const string Light = "Light";
    public static readonly string[] All = { System, Dark, Light };
}

/// <summary>見た目の設定</summary>
public sealed class AppearanceConfig : ObservableObject
{
    /// <summary>アクセント色: この値なら Windows のアクセント色に合わせる</summary>
    public const string SystemAccent = "System";
    /// <summary>アクセント色: この値ならアプリの既定（紫）</summary>
    public const string DefaultAccent = "Default";
    public const int MinTextScale = 85, MaxTextScale = 130;
    /// <summary>背景色: この値ならテーマの既定の背景</summary>
    public const string DefaultBackground = "Default";
    /// <summary>背景色: 候補の番号の前につける文字（"bg0"〜"bg63"）</summary>
    public const string BackgroundPrefix = "bg";
    /// <summary>文字色: この値ならテーマの既定の文字色</summary>
    public const string DefaultTextColor = "Default";
    /// <summary>文字色: 候補の番号の前につける文字（"tx0"〜"tx63"）</summary>
    public const string TextColorPrefix = "tx";

    private string _theme = AppThemes.Dark;
    private string _accent = DefaultAccent;
    private string _background = DefaultBackground;
    private string _textColor = DefaultTextColor;
    private string _fontFamily = "";
    private int _textScale = 100;

    /// <summary>"System" / "Dark" / "Light"</summary>
    public string Theme { get => _theme; set { if (!string.IsNullOrEmpty(value)) Set(ref _theme, value); } }
    /// <summary>"Default" / "System" / "#RRGGBB"</summary>
    public string Accent { get => _accent; set { if (!string.IsNullOrEmpty(value)) Set(ref _accent, value); } }
    /// <summary>"Default" / "bg0"〜"bg63"（候補の番号。同じ番号でもダークでは暗い色・ライトでは明るい色）</summary>
    public string Background { get => _background; set { if (!string.IsNullOrEmpty(value)) Set(ref _background, value); } }
    /// <summary>"Default" / "tx0"〜"tx63"（候補の番号。同じ番号でもダークでは明るい色・ライトでは暗い色）</summary>
    public string TextColor { get => _textColor; set { if (!string.IsNullOrEmpty(value)) Set(ref _textColor, value); } }
    /// <summary>画面の文字のフォント（空ならアプリの既定: 英数字 Montserrat・日本語 ProFan Maru）</summary>
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value ?? ""); }
    /// <summary>文字と画面の大きさ（%）</summary>
    public int TextScale { get => _textScale; set => Set(ref _textScale, Math.Clamp(value, MinTextScale, MaxTextScale)); }

    public void Normalize()
    {
        if (!AppThemes.All.Contains(Theme)) Theme = AppThemes.Dark;
        if (Accent != SystemAccent && Accent != DefaultAccent && !System.Text.RegularExpressions.Regex.IsMatch(Accent, "^#[0-9A-Fa-f]{6}$"))
            Accent = DefaultAccent;
        if (Background != DefaultBackground && !System.Text.RegularExpressions.Regex.IsMatch(Background, "^bg([0-9]|[1-5][0-9]|6[0-3])$"))
            Background = DefaultBackground;
        if (TextColor != DefaultTextColor && !System.Text.RegularExpressions.Regex.IsMatch(TextColor, "^tx([0-9]|[1-5][0-9]|6[0-3])$"))
            TextColor = DefaultTextColor;
        FontFamily ??= "";
        TextScale = Math.Clamp(TextScale, MinTextScale, MaxTextScale);
    }
}

public sealed class WidgetConfig
{
    public bool Visible { get; set; }
    /// <summary>null は未設定（初回は画面右上に出す）。NaN は JSON に書けないため null で表す</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Opacity { get; set; } = 0.9;
}

/// <summary>config.json の保存先と、壊れにくい保存処理（世代バックアップ・壊れたときの自動復旧）</summary>
public static class ConfigStore
{
    /// <summary>残しておくバックアップの数</summary>
    private const int MaxBackups = 30;
    /// <summary>続けて保存したときは、この間隔より短ければバックアップを作らない（スライダー操作のたびに増えないように）</summary>
    private static readonly TimeSpan BackupInterval = TimeSpan.FromMinutes(10);
    private static DateTime _lastBackup = DateTime.MinValue;

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "config.json");
    public static string BackupDirectory => Path.Combine(AppContext.BaseDirectory, "backups");

    /// <summary>
    /// 設定の「表示言語」だけを、設定全体を読む前に取り出す（二重起動の案内・見張りプロセスの表示も、その言語で出すため）。
    /// 読めなければ空（Windows の表示言語）
    /// </summary>
    public static string ReadLanguage(string? path = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path ?? FilePath));
            return doc.RootElement.TryGetProperty(nameof(AppConfig.Language), out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
        }
        catch { return ""; }
    }

    /// <summary>復旧に使ったバックアップ（なければ null）。起動時の案内用</summary>
    public static string? RecoveredFrom { get; private set; }

    /// <summary>
    /// config.json を読む。壊れていたら壊れたファイルを退避し、新しいバックアップから順に読めるものを探す。
    /// どれも読めなければ例外を投げる（呼び出し側で空の設定にする）
    /// </summary>
    public static AppConfig Load()
    {
        if (!File.Exists(FilePath))
        {
            // 初回起動: 既定値も必ず整える（鍵などが空のままにならないように）
            var fresh = new AppConfig();
            fresh.Normalize();
            return fresh;
        }
        try
        {
            return AppConfig.FromJson(ReadWithRetry(FilePath));
        }
        catch (IOException ex) when (ex is not FileNotFoundException)
        {
            // ほかのプログラム（ウイルス対策ソフトなど）が開いたままにしている。壊れているわけではないので退避はしない。
            // 今回は最新の読めるバックアップで起動する（書き戻さないので、次に保存したときに config.json が新しくなる）
            Logger.Error($"config.json を読めません（使用中）: {ex.Message}");
            foreach (var backup in ListBackups())
            {
                try
                {
                    var cfg = AppConfig.FromJson(File.ReadAllText(backup.Path));
                    RecoveredFrom = backup.Path;
                    Logger.Warn($"config.json が使用中のため、バックアップで起動しました: {backup.Path}");
                    return cfg;
                }
                catch
                {
                    // 次のバックアップへ
                }
            }
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"config.json の読み込みに失敗: {ex.Message}");
            string broken = Path.Combine(AppContext.BaseDirectory, $"config.broken_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            try { File.Copy(FilePath, broken, overwrite: true); } catch { /* 退避できなくても復旧は試す */ }

            foreach (var backup in ListBackups())
            {
                try
                {
                    var cfg = AppConfig.FromJson(File.ReadAllText(backup.Path));
                    RecoveredFrom = backup.Path;
                    Logger.Warn($"バックアップから復旧しました: {backup.Path}");
                    SaveJson(cfg.ToJson(), backup: false);
                    return cfg;
                }
                catch
                {
                    // このバックアップも壊れている → 次へ
                }
            }
            throw;
        }
    }

    /// <summary>ほかのプログラムが一時的に開いているときのために、少し待って 3 回まで読み直す</summary>
    private static string ReadWithRetry(string path)
    {
        for (int i = 0; ; i++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (i < 9 && File.Exists(path)) { Thread.Sleep(200 + i * 100); } // 合計 5 秒ほど待つ
        }
    }

    /// <summary>一時ファイルに書いてから置き換える（保存中に落ちても config.json が壊れない）</summary>
    public static void SaveJson(string json, bool backup = true)
    {
        if (backup && DateTime.Now - _lastBackup >= BackupInterval) Backup("auto");
        string tmp = FilePath + ".tmp";
        // ディスクに書き終わったことを確かめてから置き換える（書いた直後に電源が切れても、空のファイルにならないように）
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(json);
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>今の config.json をバックアップする（同じ内容の最新バックアップがあれば作らない）</summary>
    public static string? Backup(string reason)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            string text = File.ReadAllText(FilePath);
            Directory.CreateDirectory(BackupDirectory);
            var latest = ListBackups().FirstOrDefault();
            _lastBackup = DateTime.Now;
            if (latest is not null && File.ReadAllText(latest.Path) == text) return latest.Path;

            string path = Path.Combine(BackupDirectory, $"config_{DateTime.Now:yyyyMMdd_HHmmss}_{reason}.json");
            File.WriteAllText(path, text);
            foreach (var old in ListBackups().Skip(MaxBackups)) File.Delete(old.Path);
            return path;
        }
        catch (Exception ex)
        {
            Logger.Warn($"設定のバックアップに失敗: {ex.Message}");
            return null;
        }
    }

    /// <summary>新しい順のバックアップ一覧</summary>
    public static List<ConfigBackupInfo> ListBackups()
    {
        if (!Directory.Exists(BackupDirectory)) return new();
        return Directory.GetFiles(BackupDirectory, "config_*.json")
            .Select(p => new ConfigBackupInfo(p, File.GetLastWriteTime(p), ReasonOf(p)))
            .OrderByDescending(b => b.Time)
            .ToList();
    }

    private static string ReasonOf(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string reason = name.Split('_').Length >= 4 ? name.Split('_')[3] : "";
        return reason switch
        {
            "startup" => L("起動時"),
            "auto" => L("自動"),
            "manual" => L("手動"),
            "import" => L("読み込み前"),
            "remap" => L("部品交換の対応付け前"),
            "restore" => L("復元前"),
            _ => reason,
        };
    }
}

public sealed record ConfigBackupInfo(string Path, DateTime Time, string Reason)
{
    public string Display => L($"{Time:yyyy/MM/dd HH:mm}（{Reason}）");
}

// ==================================================
// HardwareHub.cs - LibreHardwareMonitor のラッパー (センサー検索・カタログ作成・部品ごとの読み取りエラーの切り分け)
// Dependencies: LibreHardwareMonitorLib (MPL-2.0), .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace ProFanController;

public sealed record SensorInfo(string Id, string Name, string HardwareName, string HardwareType, string SensorType)
{
    public string Display => $"{Name}  ·  {HardwareName}";
}

public sealed record ControlInfo(string Id, string Name, string HardwareName, string? RpmSensorId)
{
    public string Display => IsGpu ? L($"{Name}  ·  {HardwareName}（GPU・試験的）") : $"{Name}  ·  {HardwareName}";
    public bool IsGpu => Id.StartsWith("/gpu", StringComparison.OrdinalIgnoreCase);
}

public sealed record HardwareGroup(string Name, string HardwareType, IReadOnlyList<SensorInfo> Sensors);

/// <summary>デバイス画面のカード 1 枚分（マザーボード・CPU・GPU など最上位のハードウェア）</summary>
public sealed record DeviceInfo(
    string Key, string Name, string Kind, string? ChipName,
    int TempCount, int FanCount, int ControlCount, IReadOnlyList<string> FanSensorIds);

/// <summary>起動時に一度だけ作る、このPCで使えるセンサーと制御チャネルの一覧</summary>
public sealed record HardwareCatalog(
    IReadOnlyList<DeviceInfo> Devices,
    IReadOnlyList<HardwareGroup> Groups,
    IReadOnlyList<SensorInfo> Temperatures,
    IReadOnlyList<ControlInfo> Controls,
    bool PawnIoInstalled)
{
    /// <summary>すべてのセンサー（温度以外の使用率・クロック・電力なども含む）</summary>
    public IEnumerable<SensorInfo> AllSensors => Groups.SelectMany(g => g.Sensors);

    public SensorInfo? FindSensor(string? id)
        => id is null ? null : AllSensors.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>設定に保存する、センサーの名前情報</summary>
    public SensorLabel? LabelOf(string? id)
    {
        if (FindSensor(id) is { } s) return new SensorLabel { Name = s.Name, Hardware = s.HardwareName, HardwareType = s.HardwareType, SensorType = s.SensorType };
        if (Controls.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) is { } c)
            return new SensorLabel { Name = c.Name, Hardware = c.HardwareName, HardwareType = c.IsGpu ? "Gpu" : "SuperIO", SensorType = "Control" };
        return null;
    }
}

/// <summary>
/// FanEngine から見た「ハードウェア」。普段は LHM を使う HardwareHub、ロジックテストでは偽物に差し替える。
/// どのメソッドも FanEngine のスレッドからだけ呼ぶ。
/// </summary>
internal interface IFanHardware : IDisposable
{
    /// <summary>全センサーの値を読み直す。読み取りに失敗した部品（ハードウェア）の数を返す</summary>
    int Update();
    /// <summary>今回 Update した部品の数（全部失敗したかどうかの判定用）</summary>
    int HardwareCount { get; }
    /// <summary>そのセンサーが存在するか（今回の読み取りに失敗していても、存在すれば true）</summary>
    bool Exists(string? id);
    /// <summary>今回の値。存在しない・今回の読み取りに失敗した部品・NaN は null</summary>
    float? Value(string? id);
    /// <summary>センサーの表示名（見つからなければ null）</summary>
    string? NameOf(string? id);
    /// <summary>制御端子（見つからなければ null）。同じ端子には毎回同じオブジェクトを返す</summary>
    IFanControl? FindControl(string? id);
    /// <summary>センサーと同じ部品の使用率（CPU 温度なら CPU 使用率）。固着の判定に使う。CPU・GPU 以外（SSD など）は null</summary>
    float? LoadFor(string? sensorId);
    /// <summary>GPU ファンの端子と同じ GPU の温度（Core と Hot Spot。読めないものは null）</summary>
    void GpuTemps(string controlId, out float? core, out float? hotSpot);
    /// <summary>画面表示用に、全センサーの現在値を Identifier → 値 で返す</summary>
    Dictionary<string, float?> SnapshotValues();
    HardwareCatalog BuildCatalog(bool includeGpuFans);
}

/// <summary>ファンの制御端子 1 つ（LHM の IControl を包んだもの）</summary>
internal interface IFanControl
{
    string Id { get; }
    /// <summary>Duty（0〜100%）を書き込む</summary>
    void SetSoftware(float duty);
    /// <summary>BIOS（マザーボード）の自動制御に戻す</summary>
    void SetDefault();
}

/// <summary>
/// LHM の Computer を保持する。LHM はスレッドセーフではないため、
/// このクラスは FanEngine のスレッドからだけ使うこと。
/// </summary>
public sealed class HardwareHub : IFanHardware
{
    private readonly Computer _computer;
    private readonly Dictionary<string, ISensor> _sensors = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>ハードウェアの Identifier → そのハードウェアの代表の使用率（CPU Total / GPU Core）</summary>
    private readonly Dictionary<string, ISensor> _loads = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>今回の読み取りに失敗した部品のセンサー（値が古いままなので使わない）</summary>
    private readonly HashSet<string> _stale = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>部品ごとの、続けて読み取りに失敗した回数（ログを出しすぎないため）</summary>
    private readonly Dictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>制御端子の包み（同じ端子には同じオブジェクトを返す。FanEngine が「触った端子」を覚えるため）</summary>
    private readonly Dictionary<string, LhmControl> _controls = new(StringComparer.OrdinalIgnoreCase);
    private int _hardwareCount;
    private int _failedCount;

    public HardwareHub()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true, // ASRock の Super I/O (Nuvoton) はこの配下
            IsControllerEnabled = true,
            IsMemoryEnabled = true,      // メモリ使用量（監視の拡張）
            IsStorageEnabled = true,     // SSD の温度と寿命（監視の拡張）
        };
        try
        {
            _computer.Open();
            Update();
        }
        catch
        {
            // 開きかけのまま例外になると、呼び出し側には Dispose する相手が渡らない（ドライバのハンドルなどが残る）。ここで閉じてから知らせる
            try { _computer.Close(); } catch { /* 閉じられなくても元の例外を優先する */ }
            throw;
        }
    }

    /// <summary>PawnIO ドライバがインストール済みか（LHM 0.9.6 以降はこれが無いと Super I/O を触れない）</summary>
    public static bool IsPawnIoInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
        return key is not null;
    }

    public int HardwareCount => _hardwareCount;

    /// <summary>メモリ（DIMM）の温度を読まない（SMBus を使わない）。読まない間は、そのセンサーは見つからない扱い</summary>
    public bool SkipDimm { get; set; }

    /// <summary>メモリ（DIMM）の温度の部品か（LHM の識別子が /memory/dimm/… のもの。読むと SMBus を使う）</summary>
    public static bool IsDimm(string hardwareId) => hardwareId.StartsWith("/memory/dimm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 全ハードウェアのセンサー値を更新し、Identifier → Sensor の辞書を作り直す。
    /// 1 つの部品の読み取りに失敗しても、ほかの部品は読み続ける（失敗した部品の値は今回は使わない）。
    /// 読み取りに失敗した部品の数を返す
    /// </summary>
    public int Update()
    {
        _sensors.Clear();
        _loads.Clear();
        _stale.Clear();
        _hardwareCount = 0;
        _failedCount = 0;
        foreach (var hw in _computer.Hardware)
        {
            UpdateRecursive(hw);
        }
        return _failedCount;
    }

    private void UpdateRecursive(IHardware hw)
    {
        string hwId = hw.Identifier.ToString();
        if (SkipDimm && IsDimm(hwId)) return;
        _hardwareCount++;
        bool failed = false;
        try
        {
            hw.Update();
            if (_failures.Remove(hwId)) Logger.Info($"部品の読み取りが回復: {hw.Name}");
        }
        catch (Exception ex)
        {
            // ドライバの一時的なエラーなど。この部品の値は古いままなので、今回は「読めない」として扱う
            failed = true;
            _failedCount++;
            _failures.TryGetValue(hwId, out int n);
            _failures[hwId] = ++n;
            if (n == 1 || n % 60 == 0) Logger.Warn($"部品の読み取りに失敗（{n} 回目）: {hw.Name}: {ex.Message}");
        }
        foreach (var s in hw.Sensors)
        {
            string id = s.Identifier.ToString();
            _sensors[id] = s;
            if (failed) _stale.Add(id);
        }
        var load = LoadMovesTemperature(hw.HardwareType)
            ? SensorNames.Pick(hw.Sensors.Where(s => s.SensorType == SensorType.Load).ToList(), s => s.Name, SensorNames.CpuLoad.Concat(SensorNames.GpuLoad).ToArray())
              ?? hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load)
            : null;
        if (load is not null && !failed) _loads[hwId] = load;
        foreach (var sub in hw.SubHardware)
        {
            UpdateRecursive(sub);
        }
    }

    public ISensor? FindSensor(string? identifier)
        => identifier is not null && _sensors.TryGetValue(identifier, out var s) ? s : null;

    public bool Exists(string? id) => FindSensor(id) is not null;

    public float? Value(string? id)
    {
        if (id is null || !_sensors.TryGetValue(id, out var s) || _stale.Contains(id)) return null;
        float? v = s.Value;
        return v is float f && !float.IsNaN(f) && !float.IsInfinity(f) ? f : null;
    }

    public string? NameOf(string? id) => FindSensor(id)?.Name;

    IFanControl? IFanHardware.FindControl(string? identifier)
    {
        if (FindSensor(identifier)?.Control is not { } c) return null;
        if (_controls.TryGetValue(identifier!, out var w) && ReferenceEquals(w.Inner, c)) return w;
        w = new LhmControl(identifier!, c);
        _controls[identifier!] = w;
        return w;
    }

    /// <summary>
    /// その部品の使用率が、数分のうちに温度をはっきり動かすか（固着の判定に使ってよいか）。CPU と GPU だけ。
    /// SSD の「使用率」は読み書きの量で、短い読み書きでは温度がほとんど変わらず、温度も 1°C 刻みなので、
    /// 何分も同じ値なのは普通（固着と判定すると、音楽を流しているだけでファンが全開になってしまう）
    /// </summary>
    internal static bool LoadMovesTemperature(HardwareType type)
        => type is HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    public float? LoadFor(string? sensorId)
        => FindSensor(sensorId)?.Hardware is { } hw && _loads.TryGetValue(hw.Identifier.ToString(), out var l) ? Value(l.Identifier.ToString()) : null;

    public void GpuTemps(string controlId, out float? core, out float? hotSpot)
    {
        core = null;
        hotSpot = null;
        if (FindSensor(controlId)?.Hardware is not { } hw) return;
        foreach (var x in hw.Sensors)
        {
            if (x.SensorType != SensorType.Temperature) continue;
            float? v = Value(x.Identifier.ToString());
            // 127 / 128°C などの「読めない」を表す値は使わない
            if (!FanSafety.IsPlausibleTemp(v)) continue;
            if (x.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase))
                hotSpot = hotSpot is float h ? Math.Max(h, v!.Value) : v;
            else if (x.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                core = core is float c ? Math.Max(c, v!.Value) : v;
        }
    }

    /// <summary>画面表示用に、全センサーの現在値を Identifier → 値 で返す（読み取りに失敗した部品は null）</summary>
    public Dictionary<string, float?> SnapshotValues()
    {
        var d = new Dictionary<string, float?>(_sensors.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var id in _sensors.Keys) d[id] = Value(id);
        return d;
    }

    /// <summary>LHM の IControl を包む（FanEngine はこの包みだけを使う）</summary>
    private sealed class LhmControl : IFanControl
    {
        public LhmControl(string id, IControl inner)
        {
            Id = id;
            Inner = inner;
        }

        public string Id { get; }
        public IControl Inner { get; }
        public void SetSoftware(float duty) => Inner.SetSoftware(duty);
        public void SetDefault() => Inner.SetDefault();
    }

    /// <param name="includeGpuFans">GPU のファンも制御対象の一覧に入れる（試験的）</param>
    public HardwareCatalog BuildCatalog(bool includeGpuFans = false)
    {
        var groups = new List<HardwareGroup>();
        foreach (var hw in _computer.Hardware)
        {
            CollectGroups(hw, groups);
        }

        var temps = groups.SelectMany(g => g.Sensors).Where(s => s.SensorType == nameof(SensorType.Temperature)).ToList();
        var all = groups.SelectMany(g => g.Sensors).ToList();
        var controls = all
            // 標準はマザーボード (Super I/O) の端子だけ。GPU のファンはドライバの自動制御と競合しやすいので、設定で許可したときだけ
            .Where(s => s.SensorType == nameof(SensorType.Control) && FindSensor(s.Id)?.Control is not null
                        && (s.Id.StartsWith("/lpc/") || (includeGpuFans && s.Id.StartsWith("/gpu"))))
            .Select(s =>
            {
                // Super I/O では control/N と fan/N が同じ端子に対応している。GPU は番号がずれることがあるので名前でも探す
                string rpmId = s.Id.Replace("/control/", "/fan/");
                if (FindSensor(rpmId) is null)
                {
                    rpmId = all.FirstOrDefault(f => f.SensorType == nameof(SensorType.Fan) && f.HardwareName == s.HardwareName && f.Name == s.Name)?.Id
                            ?? all.FirstOrDefault(f => f.SensorType == nameof(SensorType.Fan) && f.HardwareName == s.HardwareName && s.Id.StartsWith("/gpu"))?.Id
                            ?? "";
                }
                return new ControlInfo(s.Id, s.Name, s.HardwareName, rpmId.Length == 0 || FindSensor(rpmId) is null ? null : rpmId);
            })
            .ToList();

        var devices = _computer.Hardware.Select(BuildDevice).ToList();
        return new HardwareCatalog(devices, groups, temps, controls, IsPawnIoInstalled());
    }

    private static DeviceInfo BuildDevice(IHardware hw)
    {
        // マザーボードのセンサーは Super I/O（サブハードウェア）側にあるので、まとめて数える
        var all = hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors)).ToList();
        string kind = hw.HardwareType switch
        {
            HardwareType.Motherboard => "Motherboard",
            HardwareType.Cpu => "Cpu",
            HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel => "Gpu",
            _ => "Other",
        };
        return new DeviceInfo(
            hw.Identifier.ToString(),
            hw.Name,
            kind,
            hw.SubHardware.FirstOrDefault()?.Name,
            all.Count(s => s.SensorType == SensorType.Temperature),
            all.Count(s => s.SensorType == SensorType.Fan),
            all.Count(s => s.SensorType == SensorType.Control),
            all.Where(s => s.SensorType == SensorType.Fan).Select(s => s.Identifier.ToString()).ToList());
    }

    private static void CollectGroups(IHardware hw, List<HardwareGroup> groups)
    {
        var sensors = hw.Sensors
            .Where(s => s.SensorType is SensorType.Temperature or SensorType.Fan or SensorType.Control
                or SensorType.Load or SensorType.Clock or SensorType.Power or SensorType.Data or SensorType.SmallData or SensorType.Level)
            .OrderBy(s => s.SensorType).ThenBy(s => s.Index)
            .Select(s => new SensorInfo(s.Identifier.ToString(), s.Name, hw.Name, hw.HardwareType.ToString(), s.SensorType.ToString()))
            .ToList();
        if (sensors.Count > 0)
        {
            groups.Add(new HardwareGroup(hw.Name, hw.HardwareType.ToString(), sensors));
        }
        foreach (var sub in hw.SubHardware)
        {
            CollectGroups(sub, groups);
        }
    }

    public void Dispose() => _computer.Close();
}

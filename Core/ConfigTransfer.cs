// ==================================================
// ConfigTransfer.cs - 設定の書き出し・読み込み (プロファイルをファイルで共有。端子・センサーは名前で対応付ける)
// Dependencies: .NET 8 / System.Text.Json (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ProFanController;

/// <summary>書き出しファイル（*.zeph-iris.json）の中身</summary>
public sealed class SettingsPackage
{
    public const string FormatName = "Zeph-Iris.Settings";

    /// <summary>この印のファイルを読み込める（いまの名前と、前の名前〈Fanlume の *.fanlume.json・ProFanController の *.profan.json〉）</summary>
    public static bool IsKnownFormat(string? format)
        => format == FormatName || AppIdentity.LegacyApps.Any(a => a.SettingsFormat == format);

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = 1;
    public DateTime ExportedAt { get; set; } = DateTime.Now;
    public string Machine { get; set; } = Environment.MachineName;
    /// <summary>書き出した PC のハードウェア（読み込み時の案内用）</summary>
    public List<KnownDevice> Devices { get; set; } = new();
    public List<ProfileConfig> Profiles { get; set; } = new();
    public List<VirtualSensorConfig> VirtualSensors { get; set; } = new();
    public List<CalibrationResult> Calibrations { get; set; } = new();
    public List<AutoSwitchRule> AutoSwitchRules { get; set; } = new();
    public List<ScheduleRule> ScheduleRules { get; set; } = new();
    /// <summary>使っているセンサー・端子の名前（別の PC で同じ役割のものを探すため）</summary>
    public Dictionary<string, SensorLabel> Labels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string DevicesText => Devices.Count == 0 ? "" : string.Join(L(" ／ "), Devices.Where(d => d.Kind is "Motherboard" or "Cpu" or "Gpu").Select(d => d.Name));
}

public static class ConfigTransfer
{
    public const string Extension = ".zeph-iris.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>選んだプロファイル（と関係する仮想センサー・ルール）をファイルにする</summary>
    public static SettingsPackage Export(AppConfig cfg, HardwareCatalog? catalog, IEnumerable<string> profileNames)
    {
        var names = profileNames.ToHashSet();
        var copy = cfg.Clone();
        var pkg = new SettingsPackage
        {
            Devices = catalog?.Devices.Select(d => new KnownDevice { Key = d.Key, Name = d.Name, Kind = d.Kind }).ToList() ?? cfg.KnownDevices,
            Profiles = copy.Profiles.Where(p => names.Contains(p.Name)).ToList(),
        };
        var used = pkg.Profiles.SelectMany(p => p.Fans).SelectMany(f => f.Curves.Select(c => c.SensorId).Append(f.TargetSensorId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        pkg.VirtualSensors = copy.VirtualSensors.Where(v => used.Contains(v.Id)).ToList();
        var controls = pkg.Profiles.SelectMany(p => p.Fans).Select(f => f.ControlId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        pkg.Calibrations = copy.Calibrations.Where(c => controls.Contains(c.ControlId)).ToList();
        pkg.AutoSwitchRules = copy.AutoSwitchRules.Where(r => names.Contains(r.ProfileName)).ToList();
        pkg.ScheduleRules = copy.ScheduleRules.Where(r => names.Contains(r.ProfileName)).ToList();

        var ids = used.Concat(controls)
            .Concat(pkg.VirtualSensors.SelectMany(v => v.Sources))
            .Concat(pkg.Profiles.SelectMany(p => p.Fans).Select(f => f.RpmSensorId ?? ""))
            .Where(id => id.Length > 0);
        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var label = catalog?.LabelOf(id) ?? (cfg.SensorLabels.TryGetValue(id, out var l) ? l : null);
            if (label is not null) pkg.Labels[id] = label;
        }
        return pkg;
    }

    public static void Save(SettingsPackage pkg, string path) => File.WriteAllText(path, JsonSerializer.Serialize(pkg, Options));

    public static SettingsPackage Load(string path)
    {
        var pkg = JsonSerializer.Deserialize<SettingsPackage>(File.ReadAllText(path), Options)
                  ?? throw new InvalidDataException(L("ファイルが空です。"));
        if (!SettingsPackage.IsKnownFormat(pkg.Format))
            throw new InvalidDataException(L("Zeph-Iris の設定ファイルではありません。"));
        if (pkg.Profiles.Count == 0) throw new InvalidDataException(L("プロファイルが入っていません。"));
        foreach (var f in pkg.Profiles.SelectMany(p => p.Fans)) f.Normalize();
        return pkg;
    }

    /// <summary>このファイルの端子・センサーを、この PC のどれに対応付けるかの案</summary>
    public static List<RemapItem> ProposeMapping(SettingsPackage pkg, HardwareCatalog catalog)
    {
        var items = new List<RemapItem>();
        foreach (var fan in pkg.Profiles.SelectMany(p => p.Fans).GroupBy(f => f.ControlId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            pkg.Labels.TryGetValue(fan.ControlId, out var label);
            var match = SensorRemapper.FindControl(fan.ControlId, label?.Name, label?.Hardware, catalog);
            items.Add(new RemapItem
            {
                IsControl = true,
                OldId = fan.ControlId,
                OldName = fan.Name,
                OldHardware = label?.Hardware ?? "",
                Usage = fan.IsPump ? L("ポンプ") : L("ファン"),
                NewId = match?.Id,
                Note = match is null ? L("対応する端子が見つかりません（読み込まない）") : match.Id == fan.ControlId ? L("同じ端子") : L("同じ名前の端子"),
            });
        }

        var sensorIds = pkg.Profiles.SelectMany(p => p.Fans)
            .SelectMany(f => f.Curves.Select(c => c.SensorId).Append(f.Mode == FanModes.Target ? f.TargetSensorId : ""))
            .Concat(pkg.VirtualSensors.SelectMany(v => v.Sources))
            .Where(id => id.Length > 0 && !id.StartsWith(VirtualSensorConfig.IdPrefix))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var id in sensorIds)
        {
            pkg.Labels.TryGetValue(id, out var label);
            var here = catalog.FindSensor(id);
            SensorInfo? match;
            string note;
            if (here is not null && (label is null || here.Name == label.Name))
            {
                match = here;
                note = L("同じセンサー");
            }
            else
            {
                (match, note) = SensorRemapper.FindTemperature(label, id, catalog);
            }
            items.Add(new RemapItem
            {
                IsControl = false,
                OldId = id,
                OldName = label?.Name ?? id,
                OldHardware = label?.Hardware ?? "",
                NewId = match?.Id,
                Note = note,
            });
        }
        return items;
    }

    /// <summary>
    /// 読み込んだプロファイルを設定に加える（元の設定はコピーして返す。元に戻せるよう呼び出し側で ReplaceConfig する）
    /// </summary>
    /// <param name="replaceSameName">同じ名前のプロファイルがあれば置き換える（false なら「（読み込み）」を付けて追加）</param>
    public static AppConfig Merge(AppConfig current, SettingsPackage pkg, IEnumerable<string> profileNames, IEnumerable<RemapItem> mapping,
        HardwareCatalog catalog, bool replaceSameName, out string summary)
    {
        var cfg = current.Clone();
        var names = profileNames.ToHashSet();
        var map = mapping.ToList();

        // 読み込むプロファイルだけを入れた一時的な設定で ID を置き換えてから、本体に移す（読み込んだデータ自体は変えない）
        static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
        var temp = new AppConfig
        {
            Profiles = new ObservableCollection<ProfileConfig>(Copy(pkg.Profiles.Where(p => names.Contains(p.Name)).ToList())),
            VirtualSensors = new ObservableCollection<VirtualSensorConfig>(Copy(pkg.VirtualSensors)),
            SensorLabels = new Dictionary<string, SensorLabel>(pkg.Labels, StringComparer.OrdinalIgnoreCase),
        };
        HardwareChange.Apply(temp, map, catalog);
        // 別の PC の設定は、端子に何がつながっているか（ポンプの位置など）が違うかもしれないので、
        // 読み込んだファンはすべて BIOS 制御のまま追加する（カーブを確認してからオンにしてもらう）
        bool otherPc = !string.Equals(pkg.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        if (otherPc) foreach (var f in temp.Profiles.SelectMany(p => p.Fans)) f.Enabled = false;

        int added = 0, replaced = 0;
        foreach (var p in temp.Profiles)
        {
            var existing = cfg.FindProfile(p.Name);
            if (existing is not null && replaceSameName)
            {
                cfg.Profiles[cfg.Profiles.IndexOf(existing)] = p;
                replaced++;
            }
            else
            {
                if (existing is not null)
                {
                    string name = L($"{p.Name}（読み込み）");
                    for (int i = 2; cfg.FindProfile(name) is not null; i++) name = L($"{p.Name}（読み込み {i}）");
                    p.Name = name;
                }
                cfg.Profiles.Add(p);
                added++;
            }
        }

        // 仮想センサー（同じ ID がなければ追加）
        foreach (var v in temp.VirtualSensors.Where(v => cfg.VirtualSensors.All(x => x.Id != v.Id))) cfg.VirtualSensors.Add(v);

        // キャリブレーションは、同じ端子にそのまま対応したときだけ（別の PC のファンの測定値は使えない）
        foreach (var cal in pkg.Calibrations)
        {
            var item = map.FirstOrDefault(m => m.IsControl && string.Equals(m.OldId, cal.ControlId, StringComparison.OrdinalIgnoreCase));
            if (item?.NewId is null || !string.Equals(item.NewId, cal.ControlId, StringComparison.OrdinalIgnoreCase)) continue;
            if (pkg.Machine == Environment.MachineName && cfg.FindCalibration(cal.ControlId) is null) cfg.Calibrations.Add(cal);
        }

        // ルール（読み込んだプロファイル名に合わせる）
        foreach (var r in pkg.AutoSwitchRules.Where(r => names.Contains(r.ProfileName)))
        {
            if (cfg.AutoSwitchRules.Any(x => x.ProcessName == r.ProcessName)) continue;
            cfg.AutoSwitchRules.Add(r);
        }

        foreach (var (id, label) in pkg.Labels)
        {
            if (catalog.LabelOf(id) is { } here) cfg.SensorLabels[id] = here;
            else if (!cfg.SensorLabels.ContainsKey(id)) cfg.SensorLabels[id] = label;
        }
        cfg.Normalize();

        int dropped = map.Count(m => m.IsControl && m.NewId is null);
        summary = L($"{added + replaced} 個のプロファイルを読み込みました（追加 {added}・置き換え {replaced}）")
                  + (dropped > 0 ? L($"。対応する端子がない {dropped} 個のファンは読み込んでいません") : "")
                  + (otherPc ? L("。安全のため、読み込んだファンは「このツールで制御する」をオフにしてあります") : "");
        return cfg;
    }
}

// ==================================================
// HardwareChange.cs - 部品の交換を検知し、消えたセンサー・端子を新しいものへ対応付ける案を作る
//   (GPU 交換・設定ファイルの読み込みで共通に使う。起動直後で部品がまだ見えていないときは判断を保留する)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>対応付けの 1 行（古い ID → 新しい ID の案）</summary>
public sealed class RemapItem : ObservableObject
{
    private string? _newId;

    /// <summary>true なら制御端子（ファン）、false なら温度センサー</summary>
    public required bool IsControl { get; init; }
    public required string OldId { get; init; }
    public required string OldName { get; init; }
    public string OldHardware { get; init; } = "";
    /// <summary>この ID を使っている場所（例: CaseFan の「GPU」カーブ）</summary>
    public string Usage { get; init; } = "";
    /// <summary>対応付ける先（null は「使わない」）</summary>
    public string? NewId { get => _newId; set => Set(ref _newId, value); }
    /// <summary>案を作った理由（「同じ名前」「Hot Spot がないため GPU Core」など）</summary>
    public string Note { get; init; } = "";

    public string OldDisplay => OldHardware.Length > 0 ? $"{OldName}  ·  {OldHardware}" : OldName;
}

/// <summary>部品交換の検知結果</summary>
public sealed class HardwareChangeReport
{
    /// <summary>「GPU: Radeon RX 7900 XTX → GeForce RTX 5070 Ti」のような要約</summary>
    public List<string> DeviceChanges { get; } = new();
    public List<RemapItem> Items { get; } = new();
    public bool HasChanges => Items.Count > 0;
    /// <summary>
    /// 部品がまだ見えていない（起動直後でドライバの準備が遅れている・マザーボードの端子が 1 つも見えないなど）。
    /// このときは判断しない（Items は空）。呼び出し側は 10〜30 秒後にもう一度確かめること
    /// </summary>
    public bool NotReady { get; set; }
    /// <summary>NotReady の理由（ログ・画面表示用）</summary>
    public string NotReadyReason { get; set; } = "";
}

public static class SensorRemapper
{
    /// <summary>LHM の HardwareType を、交換しても同じ役割になる分類にまとめる</summary>
    public static string Category(string hardwareType) => hardwareType switch
    {
        _ when hardwareType.StartsWith("Cpu", StringComparison.OrdinalIgnoreCase) => "Cpu",
        _ when hardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase) => "Gpu",
        "SuperIO" or "Motherboard" => "Board",
        _ => hardwareType,
    };

    /// <summary>ID の形から分類を推測する（古い設定で名前が保存されていないとき用）</summary>
    public static string CategoryFromId(string id) => id switch
    {
        _ when id.StartsWith("/gpu", StringComparison.OrdinalIgnoreCase) => "Gpu",
        _ when id.Contains("cpu", StringComparison.OrdinalIgnoreCase) => "Cpu",
        _ when id.StartsWith("/lpc", StringComparison.OrdinalIgnoreCase) => "Board",
        _ => "",
    };

    private static readonly string[] CpuMain = { "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Core Max", "Core Average" };

    /// <summary>
    /// 古いセンサーと同じ役割の、新しいセンサーを探す。
    /// 1. 同じ分類（CPU / GPU / マザーボード）で同じ名前  2. GPU・CPU の代表的な温度に読み替える
    /// </summary>
    public static (SensorInfo? Sensor, string Note) FindTemperature(SensorLabel? old, string oldId, HardwareCatalog catalog)
    {
        var temps = catalog.Temperatures;
        string cat = old is not null ? Category(old.HardwareType) : CategoryFromId(oldId);
        var same = temps.Where(t => Category(t.HardwareType) == cat).ToList();

        if (old is not null)
        {
            // 同じ部品（名前まで同じ）にあれば最優先
            var exact = same.FirstOrDefault(t => t.Name == old.Name && t.HardwareName == old.Hardware) ?? same.FirstOrDefault(t => t.Name == old.Name);
            if (exact is not null) return (exact, L("同じ名前のセンサー"));
        }

        string name = old?.Name ?? "";
        if (cat == "Gpu")
        {
            SensorInfo? core = SensorNames.Pick(same, t => t.Name, SensorNames.GpuCoreTemp) ?? same.FirstOrDefault();
            if (name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) || name.Contains("Junction", StringComparison.OrdinalIgnoreCase))
            {
                var hs = same.FirstOrDefault(t => t.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase));
                if (hs is not null) return (hs, L("同じ役割のセンサー"));
                return (core, L("新しい GPU には Hot Spot がないため、GPU 温度（Core）を使います"));
            }
            if (name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
            {
                var mem = same.FirstOrDefault(t => t.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase));
                if (mem is not null) return (mem, L("同じ役割のセンサー"));
            }
            return (core, old is null ? L("GPU の温度（Core）を使います") : L("GPU の温度（Core）に読み替えます"));
        }
        if (cat == "Cpu")
        {
            var main = CpuMain.Select(n => same.FirstOrDefault(t => t.Name == n)).FirstOrDefault(t => t is not null) ?? same.FirstOrDefault();
            return (main, L("CPU の代表的な温度に読み替えます"));
        }
        return (null, L("対応するセンサーが見つかりません"));
    }

    /// <summary>古い制御端子と同じ端子を探す（同じチップの同じ名前 → 同じ名前）</summary>
    public static ControlInfo? FindControl(string oldId, string? oldName, string? oldHardware, HardwareCatalog catalog)
    {
        var byId = catalog.Controls.FirstOrDefault(c => string.Equals(c.Id, oldId, StringComparison.OrdinalIgnoreCase));
        if (byId is not null && (oldHardware is null || oldHardware.Length == 0 || byId.HardwareName == oldHardware)) return byId;
        if (oldName is { Length: > 0 })
        {
            var match = catalog.Controls.FirstOrDefault(c => c.Name == oldName && c.HardwareName == oldHardware)
                        ?? catalog.Controls.FirstOrDefault(c => c.Name == oldName && c.IsGpu == oldId.StartsWith("/gpu"));
            if (match is not null) return match;
        }
        if (byId is not null) return byId;
        // マザーボードのチップが違っても、端子の番号（control/N）が同じなら同じ位置の端子であることが多い
        if (oldId.StartsWith("/lpc/", StringComparison.OrdinalIgnoreCase) && oldId.LastIndexOf('/') is int slash and > 0)
        {
            string suffix = oldId[slash..];
            return catalog.Controls.FirstOrDefault(c => !c.IsGpu && c.Id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && c.Id.Contains("/control/"));
        }
        return null;
    }
}

public static class HardwareChange
{
    /// <summary>
    /// 前回の起動時と比べて、設定で使っているセンサー・端子が無くなっていないか調べる。
    /// 無くなっていたら、新しいハードウェアで同じ役割のものを探して対応付けの案を作る。
    /// 部品の種類ごと見えていないとき（起動直後の準備中など）は、NotReady にして判断しない
    /// </summary>
    /// <param name="force">
    /// true なら「まだ見えていない」の判断をせずに案を作る（何度か待っても見えないとき＝本当に外したときに使う）
    /// </param>
    public static HardwareChangeReport Detect(AppConfig cfg, HardwareCatalog catalog, bool force = false)
    {
        var report = new HardwareChangeReport();
        if (catalog.Temperatures.Count == 0)
        {
            // ドライバの問題で何も見えていないときは判断しない
            report.NotReady = true;
            report.NotReadyReason = L("温度センサーがまだ 1 つも見えていません");
            return report;
        }
        if (!force && NotReadyReason(cfg, catalog) is { } why)
        {
            report.NotReady = true;
            report.NotReadyReason = why;
            return report;
        }

        // --- 部品の入れ替わり（表示用）
        foreach (var kind in new[] { "Gpu", "Cpu", "Motherboard" })
        {
            var before = cfg.KnownDevices.Where(d => d.Kind == kind).Select(d => d.Name).ToList();
            var now = catalog.Devices.Where(d => d.Kind == kind).Select(d => d.Name).ToList();
            var gone = before.Except(now).ToList();
            var added = now.Except(before).ToList();
            if (before.Count == 0 || (gone.Count == 0 && added.Count == 0)) continue;
            string label = kind switch { "Gpu" => L("グラフィックス"), "Cpu" => "CPU", _ => L("マザーボード") };
            report.DeviceChanges.Add($"{label}: {(gone.Count > 0 ? string.Join(" / ", gone) : L("なし"))} → {(added.Count > 0 ? string.Join(" / ", added) : L("なし"))}");
        }

        // --- 消えた温度センサー
        var known = catalog.AllSensors.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usage = UsageMap(cfg);
        foreach (var id in usage.Keys.Where(id => !id.StartsWith(VirtualSensorConfig.IdPrefix) && !id.StartsWith("rpm:") && !known.Contains(id)))
        {
            cfg.SensorLabels.TryGetValue(id, out var label);
            var (match, note) = SensorRemapper.FindTemperature(label, id, catalog);
            report.Items.Add(new RemapItem
            {
                IsControl = false,
                OldId = id,
                OldName = label?.Name ?? id,
                OldHardware = label?.Hardware ?? "",
                Usage = string.Join(L("、"), usage[id].Distinct().Take(4)),
                NewId = match?.Id,
                Note = note,
            });
        }

        // --- 消えた制御端子
        var controls = catalog.Controls.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var fan in cfg.Profiles.SelectMany(p => p.Fans).GroupBy(f => f.ControlId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            if (controls.Contains(fan.ControlId))
            {
                // 端子はあるのに、設定した回転数センサーだけが見つからない（ポンプの監視が止まってしまう）。
                // 同じ端子の回転数センサーが見つかれば、付け替える案を出す
                if (fan.RpmSensorId is { Length: > 0 } rpmId && !known.Contains(rpmId)
                    && catalog.Controls.FirstOrDefault(c => string.Equals(c.Id, fan.ControlId, StringComparison.OrdinalIgnoreCase)) is { RpmSensorId: { } newRpm }
                    && !string.Equals(newRpm, rpmId, StringComparison.OrdinalIgnoreCase))
                {
                    cfg.SensorLabels.TryGetValue(fan.ControlId, out var l);
                    report.Items.Add(new RemapItem
                    {
                        IsControl = true,
                        OldId = fan.ControlId,
                        OldName = fan.Name,
                        OldHardware = l?.Hardware ?? "",
                        Usage = fan.IsPump ? L("ポンプ（回転数の監視）") : L("ファン（回転数）"),
                        NewId = fan.ControlId,
                        Note = L("回転数センサーが見つからないため、同じ端子の回転数センサーに付け替えます"),
                    });
                }
                continue;
            }
            // GPU のファンの制御をオフにしているときは、一覧に出ないのが正常
            if (fan.IsGpuFan && !cfg.Safety.AllowGpuFanControl) continue;
            cfg.SensorLabels.TryGetValue(fan.ControlId, out var label);
            var match = SensorRemapper.FindControl(fan.ControlId, label?.Name, label?.Hardware, catalog);
            report.Items.Add(new RemapItem
            {
                IsControl = true,
                OldId = fan.ControlId,
                OldName = fan.Name,
                OldHardware = label?.Hardware ?? "",
                Usage = fan.IsPump ? L("ポンプ") : L("ファン"),
                NewId = match?.Id,
                Note = match is not null ? L("同じ名前の端子")
                    : !ControlCategoryPresent(catalog, fan.ControlId) ? L("この種類の端子がまだ見えていません（消さずに BIOS 制御にして残します）")
                    : fan.IsGpuFan ? L("GPU のファンは交換した GPU では使えません（削除します）")
                    : L("対応する端子が見つかりません"),
            });
        }
        return report;
    }

    /// <summary>
    /// 部品がまだ見えていない（判断を保留すべき）理由。準備ができていれば null。
    ///   ・設定で使っているマザーボードの端子が見つからず、しかもマザーボードの端子が 1 つも見えない
    ///   ・設定で使っている CPU / GPU / マザーボードのセンサーが見つからず、しかもその種類のセンサーが 1 つも見えない
    /// （本当に部品を外した場合もこうなるので、呼び出し側は何度か待ってから force: true で確かめる）
    /// </summary>
    public static string? NotReadyReason(AppConfig cfg, HardwareCatalog catalog)
    {
        if (catalog.Temperatures.Count == 0) return L("温度センサーがまだ 1 つも見えていません");
        var controls = catalog.Controls.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var fan in cfg.Profiles.SelectMany(p => p.Fans))
        {
            if (string.IsNullOrEmpty(fan.ControlId) || controls.Contains(fan.ControlId)) continue;
            if (fan.IsGpuFan && !cfg.Safety.AllowGpuFanControl) continue;
            if (!ControlCategoryPresent(catalog, fan.ControlId))
                return fan.IsGpuFan ? L("GPU がまだ見えていません") : L("マザーボードのファン端子（Super I/O）がまだ 1 つも見えていません");
        }
        var known = catalog.AllSensors.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in UsageMap(cfg).Keys)
        {
            if (id.StartsWith(VirtualSensorConfig.IdPrefix) || id.StartsWith("rpm:") || known.Contains(id)) continue;
            cfg.SensorLabels.TryGetValue(id, out var label);
            string cat = label is not null ? SensorRemapper.Category(label.HardwareType) : SensorRemapper.CategoryFromId(id);
            // ファン制御に関わる種類だけ確かめる（SSD などは外すこともあるので、見えなくても待たない）
            if (cat is not ("Cpu" or "Gpu" or "Board")) continue;
            if (!catalog.Temperatures.Any(t => SensorRemapper.Category(t.HardwareType) == cat))
                return NoTemperatureSensorReason(cat);
        }
        return null;
    }

    /// <summary>
    /// その種類の温度センサーが見えないときの理由の文。
    /// 「CPU の」＋「温度センサーが…」のようにつなぐと語順の違う言語に訳せないので、文ごと分ける
    /// </summary>
    private static string NoTemperatureSensorReason(string cat) => cat switch
    {
        "Cpu" => L("CPU の温度センサーがまだ 1 つも見えていません"),
        "Gpu" => L("GPU の温度センサーがまだ 1 つも見えていません"),
        "Board" => L("マザーボードの温度センサーがまだ 1 つも見えていません"),
        _ => L("温度センサーがまだ 1 つも見えていません"),
    };

    /// <summary>
    /// その制御端子の種類（マザーボード / GPU）の部品が、今のハードウェアに見えているか。
    /// 見えていなければ「まだ準備中かもしれない」ので、その端子のファンを消してはいけない
    /// </summary>
    public static bool ControlCategoryPresent(HardwareCatalog catalog, string controlId)
    {
        if (controlId.StartsWith("/lpc/", StringComparison.OrdinalIgnoreCase))
            return catalog.Controls.Any(c => c.Id.StartsWith("/lpc/", StringComparison.OrdinalIgnoreCase));
        if (controlId.StartsWith("/gpu", StringComparison.OrdinalIgnoreCase))
            return catalog.Temperatures.Any(t => SensorRemapper.Category(t.HardwareType) == "Gpu")
                   || catalog.Devices.Any(d => d.Kind == "Gpu");
        return true;
    }

    /// <summary>
    /// 置き換え先の端子が重複してしまう対応付け（あとから来たほう）。
    /// 1 つの端子を 2 つのファンが取り合うと、どちらも正しく制御できないので、これらは置き換えない。
    /// 対応付けの画面で「適用」の前に確かめるのにも使える
    /// </summary>
    public static List<RemapItem> ConflictingControlItems(AppConfig cfg, IEnumerable<RemapItem> items)
    {
        var candidates = items.Where(i => i.IsControl && i.NewId is not null && !string.Equals(i.NewId, i.OldId, StringComparison.OrdinalIgnoreCase)).ToList();
        var rejected = new List<RemapItem>();
        // 受け付けない対応付けが決まると「動かないファン」が増えるので、変わらなくなるまで繰り返す
        for (int round = 0; round <= candidates.Count; round++)
        {
            var moving = candidates.Where(i => !rejected.Contains(i)).Select(i => i.OldId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var accepted = new List<RemapItem>();
            var next = new List<RemapItem>();
            foreach (var i in candidates)
            {
                bool conflict = false;
                foreach (var p in cfg.Profiles)
                {
                    if (!p.Fans.Any(f => string.Equals(f.ControlId, i.OldId, StringComparison.OrdinalIgnoreCase))) continue;
                    // そのプロファイルで、置き換え先の端子を動かないファンが使っている
                    if (p.Fans.Any(f => string.Equals(f.ControlId, i.NewId, StringComparison.OrdinalIgnoreCase) && !moving.Contains(f.ControlId))) conflict = true;
                    // 先に受け付けた対応付けが、同じ端子へ動かしている
                    if (accepted.Any(a => string.Equals(a.NewId, i.NewId, StringComparison.OrdinalIgnoreCase)
                                          && p.Fans.Any(f => string.Equals(f.ControlId, a.OldId, StringComparison.OrdinalIgnoreCase)))) conflict = true;
                }
                // 同じ古い端子が 2 回出てきたら、2 回目は使わない
                if (accepted.Any(a => string.Equals(a.OldId, i.OldId, StringComparison.OrdinalIgnoreCase))) conflict = true;
                if (conflict) next.Add(i);
                else accepted.Add(i);
            }
            bool same = next.Count == rejected.Count && next.All(rejected.Contains);
            rejected = next;
            if (same) break;
        }
        return rejected;
    }

    /// <summary>センサー ID → 使っている場所の説明</summary>
    private static Dictionary<string, List<string>> UsageMap(AppConfig cfg)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string id, string where)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (!map.TryGetValue(id, out var list)) map[id] = list = new();
            list.Add(where);
        }
        foreach (var f in cfg.Profiles.SelectMany(p => p.Fans))
        {
            foreach (var c in f.Curves) Add(c.SensorId, L($"{f.Name}「{c.Name}」"));
            if (f.Mode == FanModes.Target) Add(f.TargetSensorId, L($"{f.Name}（目標温度）"));
        }
        foreach (var v in cfg.VirtualSensors)
        {
            foreach (var s in v.Sources) Add(s, L($"仮想センサー「{v.Name}」"));
        }
        foreach (var c in cfg.DashboardCards) Add(c.SensorId, L($"ダッシュボード「{c.Title}」"));
        if (cfg.Rgb.SensorId.Length > 0) Add(cfg.Rgb.SensorId, L("LED の色"));
        return map;
    }

    /// <summary>
    /// 今のハードウェアを「前回あったもの」として覚え、使っているセンサーの名前を保存する。
    /// 部品がまだ見えていないとき（NotReadyReason）は、部品の一覧は書き換えない（見えていない部品を忘れないように）
    /// </summary>
    /// <param name="force">true なら、まだ見えていない部品があっても今の一覧を覚える（ユーザーが対応付けを確定したあと）</param>
    public static bool Remember(AppConfig cfg, HardwareCatalog catalog, bool force = false)
    {
        bool changed = false;
        // 温度を読めたことがあるかは、同じ部品なら前の記録を引き継ぐ（一時的に読めなくても忘れない）
        bool HadBefore(DeviceInfo d) => cfg.KnownDevices.Any(k => k.Key == d.Key && k.Name == d.Name && k.HadTemps);
        var devices = catalog.Devices.Select(d => new KnownDevice { Key = d.Key, Name = d.Name, Kind = d.Kind, HadTemps = d.TempCount > 0 || HadBefore(d) }).ToList();
        bool ready = force || NotReadyReason(cfg, catalog) is null;
        if (ready && !devices.Select(d => d.Key + d.Name).SequenceEqual(cfg.KnownDevices.Select(d => d.Key + d.Name)))
        {
            cfg.KnownDevices = devices;
            changed = true;
        }
        foreach (var d in catalog.Devices.Where(d => d.TempCount > 0))
        {
            if (cfg.KnownDevices.FirstOrDefault(k => k.Key == d.Key && k.Name == d.Name) is { HadTemps: false } k)
            {
                k.HadTemps = true;
                changed = true;
            }
        }
        foreach (var id in cfg.ReferencedSensorIds().Concat(cfg.ReferencedControlIds()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (catalog.LabelOf(id) is not { } label) continue;
            if (cfg.SensorLabels.TryGetValue(id, out var old) && old.Name == label.Name && old.Hardware == label.Hardware) continue;
            cfg.SensorLabels[id] = label;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// 対応付けを設定に反映する。
    ///   ・対応先のない端子のファンは削除する。ただし、その種類の部品（マザーボード / GPU）がまだ見えていないときは
    ///     消さずに BIOS 制御（Enabled = false）にして残す（起動直後の準備中に、ファンの設定を失わないように）
    ///   ・置き換え先の端子が重複する対応付けは受け付けず、そのファンは元の端子のまま BIOS 制御にする
    /// </summary>
    public static void Apply(AppConfig cfg, IEnumerable<RemapItem> items, HardwareCatalog catalog)
    {
        var list = items.ToList();
        var rejected = ConflictingControlItems(cfg, list);
        foreach (var r in rejected)
            Logger.Warn($"端子の対応付けを受け付けませんでした（置き換え先 {r.NewId} を別のファンが使っています）: {r.OldDisplay}");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in list)
        {
            if (i.NewId is null || string.Equals(i.NewId, i.OldId, StringComparison.OrdinalIgnoreCase) || rejected.Contains(i)) continue;
            map.TryAdd(i.OldId, i.NewId);
        }

        // 端子が変わったら、回転数センサーも新しい端子のものにする
        foreach (var item in list.Where(i => i.IsControl && i.NewId is not null && !rejected.Contains(i)))
        {
            var ctl = catalog.Controls.FirstOrDefault(c => string.Equals(c.Id, item.NewId, StringComparison.OrdinalIgnoreCase));
            foreach (var f in cfg.Profiles.SelectMany(p => p.Fans).Where(f => string.Equals(f.ControlId, item.OldId, StringComparison.OrdinalIgnoreCase)))
            {
                if (ctl?.RpmSensorId is { } rpm && f.RpmSensorId is { } oldRpm) map.TryAdd(oldRpm, rpm);
                else if (ctl is not null) f.RpmSensorId = ctl.RpmSensorId;
            }
        }
        // 端子が変わったファンは、つながっている物（ポンプかファンか）が同じとは限らないので、
        // いったん BIOS 制御（このツールで制御しない）にして、確認してからオンにしてもらう
        var movedControls = list.Where(i => i.IsControl && i.NewId is not null && !string.Equals(i.NewId, i.OldId, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.OldId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in cfg.Profiles.SelectMany(p => p.Fans).Where(f => movedControls.Contains(f.ControlId))) f.Enabled = false;

        cfg.RemapIds(map);

        foreach (var item in list.Where(i => i.IsControl && i.NewId is null))
        {
            // 部品の種類ごと見えていないなら、準備中なだけかもしれない: 消さずに BIOS 制御にして残す
            bool present = ControlCategoryPresent(catalog, item.OldId);
            foreach (var p in cfg.Profiles)
            {
                foreach (var f in p.Fans.Where(f => string.Equals(f.ControlId, item.OldId, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    if (present) p.Fans.Remove(f);
                    else f.Enabled = false;
                }
            }
        }
        // 念のため: 置き換えの結果、同じプロファイルに同じ端子が重複していたら、最初の 1 つだけ残す（エンジンが端子ごとの表を作れなくなるため）
        foreach (var p in cfg.Profiles)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in p.Fans.ToList())
            {
                if (!string.IsNullOrEmpty(f.ControlId) && !seen.Add(f.ControlId)) p.Fans.Remove(f);
            }
        }
        // 対応先のない温度センサーを使っているダッシュボードのカードは消す（カーブはセンサーなし＝安全な回転数のまま残る）
        foreach (var item in list.Where(i => !i.IsControl && i.NewId is null))
        {
            foreach (var c in cfg.DashboardCards.Where(c => string.Equals(c.SensorId, item.OldId, StringComparison.OrdinalIgnoreCase)).ToList()) cfg.DashboardCards.Remove(c);
        }
    }
}

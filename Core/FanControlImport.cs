// ==================================================
// FanControlImport.cs - FanControl（Rem0o）の設定ファイル（Configurations\*.json）を、Zeph-Iris の設定の読み込みに変換する
// Dependencies: .NET 8 / System.Text.Json (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Text.Json;

namespace ProFanController;

/// <summary>
/// FanControl の設定を、Zeph-Iris の「設定を読み込む」と同じ形（SettingsPackage）にする。
/// あとは同じ画面で、端子・センサーをこの PC のどれにするかを確かめて読み込む（元に戻すもできる）。
/// FanControl の形式は公開された仕様がないので、名前で探して、分からないものは読み飛ばして知らせる:
///   Controls[]: Identifier・Name・NickName・Enable・SelectedFanCurve（名前だけ、またはカーブそのもの）・MinimumPercent
///   FanCurves[]: Name・SelectedTempSource.Identifier と、種類ごとの値
///     グラフ: Points ["温度,%" …] ／ Auto・線形: 低い温度と速さ・高い温度と速さ ／ 固定: 値 1 つ ／ Mix: SelectedFanCurves（いちばん高いものを使う）
/// センサーの ID は、FanControl も LibreHardwareMonitor の形（/lpc/…/control/0 など）なので、そのまま、
/// または古い形（/lpc/it8686e/control/0 のように /0 がない）を今の形に直して対応させる
/// </summary>
public static class FanControlImport
{
    /// <summary>読み込み元の印（SettingsPackage.Machine に入れる。この PC の設定ではないので、読み込んだファンは制御オフで入る）</summary>
    public const string SourceName = "FanControl";

    public sealed class Result
    {
        public required SettingsPackage Package { get; init; }
        /// <summary>読み込めなかったもの・近い形に直したもの（画面に出す）</summary>
        public List<string> Notes { get; } = new();
        public int FanCount { get; set; }
    }

    /// <summary>FanControl の設定が入っていそうなフォルダー（ファイルを選ぶ画面の最初の場所）</summary>
    public static string? FindConfigFolder()
    {
        var dirs = new List<string>();
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("FanControl"))
            {
                try { if (p.MainModule?.FileName is { } exe) dirs.Add(Path.Combine(Path.GetDirectoryName(exe)!, "Configurations")); }
                catch { /* 管理者でも読めないことがある */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* 一覧が取れなくても、決まった場所を探す */ }
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData })
        {
            string r = Environment.GetFolderPath(root);
            if (r.Length > 0) dirs.Add(Path.Combine(r, "FanControl", "Configurations"));
        }
        return dirs.FirstOrDefault(Directory.Exists);
    }

    /// <summary>FanControl の設定ファイルらしいか（Zeph-Iris の設定ファイルと見分ける）</summary>
    public static bool LooksLikeFanControl(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return Find(doc.RootElement, "FanCurves") is not null && Find(doc.RootElement, "Controls") is not null;
        }
        catch (JsonException) { return false; }
    }

    public static Result Convert(string json, string fileName, HardwareCatalog catalog)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var controlsEl = Find(root, "Controls") ?? throw new InvalidDataException(L("FanControl の設定ファイルではありません（Controls がありません）。"));
        var curvesEl = Find(root, "FanCurves");

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string profileName = string.IsNullOrEmpty(stem) || stem.Equals("userConfig", StringComparison.OrdinalIgnoreCase) ? "FanControl" : L($"FanControl（{stem}）");
        var profile = new ProfileConfig { Name = profileName };
        var pkg = new SettingsPackage { Machine = SourceName, Profiles = { profile } };
        var result = new Result { Package = pkg };

        var curves = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (curvesEl is { ValueKind: JsonValueKind.Array } arr)
            foreach (var c in arr.EnumerateArray())
                if (Str(c, "Name") is { Length: > 0 } n) curves.TryAdd(n, c);

        foreach (var ctl in controlsEl.EnumerateArray())
        {
            string? id = Str(ctl, "Identifier");
            if (string.IsNullOrEmpty(id)) continue;
            string name = Str(ctl, "NickName") is { Length: > 0 } nick ? nick : Str(ctl, "Name") ?? id;
            if (!ctl.TryGetProperty("SelectedFanCurve", out var sel) || sel.ValueKind != JsonValueKind.Object) continue;   // カーブなし＝ FanControl でも制御していない

            // カーブ: 名前だけなら FanCurves から探す。中身ごと入っていればそれを使う
            JsonElement curve = sel;
            if (!HasCurveData(sel) && Str(sel, "Name") is { } refName && curves.TryGetValue(refName, out var found)) curve = found;

            var fan = new FanConfig { Name = name, ControlId = MapControl(id, catalog), Enabled = false, Mode = FanModes.Curve };
            if (LooksLikePump(name) || LooksLikePump(Str(ctl, "Name"))) fan.Role = FanRoles.Pump;
            // 速さの変わり方（FanControl のステップアップ・ステップダウンは %/秒。Zeph-Iris も 1 秒ごと）
            if (Num(ctl, "SelectedCommandStepUp") is double up && up > 0) fan.MaxStepUpPerTick = Math.Clamp(up, 0.5, 100);
            if (Num(ctl, "SelectedCommandStepDown") is double down && down > 0) fan.MaxStepDownPerTick = Math.Clamp(down, 0.5, 100);

            if (!AddCurve(fan, curve, curves, catalog, result.Notes, depth: 0))
            {
                result.Notes.Add(L($"{name}: カーブ「{Str(curve, "Name") ?? "?"}」の種類が分からないため、読み込んでいません"));
                continue;
            }
            // 最低回転数: FanControl の最小 % があればそれ。なければ、Zeph-Iris の初期値（25%）がカーブの低いところを持ち上げないよう、
            // カーブのいちばん低い速さまで下げる（FanControl で回していたのと同じ速さにする）
            if (Num(ctl, "MinimumPercent") is double min && min > 0) fan.MinDuty = Math.Clamp(min, 0, 100);
            else if (fan.Mode == FanModes.Curve && fan.Curves.SelectMany(c => c.Points).Select(pt => pt.Duty).DefaultIfEmpty(fan.MinDuty).Min() is double low && low < fan.MinDuty)
                fan.MinDuty = Math.Clamp(low, 0, 100);
            if (Num(ctl, "SelectedOffset") is double off && Math.Abs(off) > 0.5)
                result.Notes.Add(L($"{name}: FanControl のオフセット（{off:+0;-0}%）は、Zeph-Iris にないので読み込んでいません（カーブの点で調整してください）"));
            fan.Normalize();
            profile.Fans.Add(fan);
            result.FanCount++;
        }
        if (result.FanCount == 0)
            // カーブが 1 つもない: FanControl で作っても、保存するまではファイルに入らない（閉じても保存されないことがある）
            throw new InvalidDataException(curves.Count == 0
                ? L("このファイルにはカーブがまだ保存されていません。FanControl でカーブを作ってファンに割り当てたら、右上の「⋮」→「保存」を押してから、もう一度読み込んでください。")
                : L("カーブを使っているファンが見つかりませんでした。"));

        // 名前の情報（この PC の部品が分かるものだけ。分からない ID は対応付けの画面で選ぶ）
        foreach (var f in profile.Fans)
        {
            if (catalog.LabelOf(f.ControlId) is { } cl) pkg.Labels[f.ControlId] = cl;
            foreach (var c in f.Curves)
                if (catalog.LabelOf(c.SensorId) is { } sl) pkg.Labels[c.SensorId] = sl;
                else if (c.SensorId.StartsWith("NVApiWrapper", StringComparison.OrdinalIgnoreCase))
                    pkg.Labels[c.SensorId] = new SensorLabel { Name = "GPU Core", HardwareType = "GpuNvidia", SensorType = "Temperature" };
        }
        return result;
    }

    // ---------------------------------------------------------------------
    // カーブ
    // ---------------------------------------------------------------------

    private static bool HasCurveData(JsonElement c)
        => c.TryGetProperty("Points", out _) || c.TryGetProperty("SelectedTempSource", out _) || c.TryGetProperty("SelectedFanCurves", out _)
           || c.TryGetProperty("IdleTemperature", out _);

    /// <summary>カーブ 1 つを、ファンのカーブ（いくつか）にして加える。読めなければ false</summary>
    private static bool AddCurve(FanConfig fan, JsonElement c, Dictionary<string, JsonElement> all, HardwareCatalog catalog, List<string> notes, int depth)
    {
        if (depth > 4) return false;   // Mix の中の Mix の…（輪になっていても止める）
        string curveName = Str(c, "Name") ?? "FanControl";

        // Mix: 中のカーブをすべて加える（Zeph-Iris は、ファンのカーブのうちいちばん高い速さを使う）
        if (c.TryGetProperty("SelectedFanCurves", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            bool any = false;
            foreach (var p in parts.EnumerateArray())
            {
                var part = p;
                if (p.ValueKind == JsonValueKind.String && all.TryGetValue(p.GetString()!, out var byName)) part = byName;
                else if (!HasCurveData(p) && Str(p, "Name") is { } n && all.TryGetValue(n, out var byRef)) part = byRef;
                any |= AddCurve(fan, part, all, catalog, notes, depth + 1);
            }
            if (any && Num(c, "SelectedMixFunction") is double fn && fn != 0)
                notes.Add(L($"{fan.Name}: Mix「{curveName}」は、Zeph-Iris では「いちばん高いものを使う」で読み込みました"));
            return any;
        }

        string? sensor = c.TryGetProperty("SelectedTempSource", out var src) && src.ValueKind == JsonValueKind.Object ? Str(src, "Identifier") : null;
        var points = ReadPoints(c, out string? flat);
        if (points is null) return false;
        if (flat is not null)
        {
            // 固定の値: 温度によらず同じ速さ
            fan.Mode = FanModes.Fixed;
            fan.FixedDuty = points[0].Duty;
            return true;
        }
        if (string.IsNullOrEmpty(sensor))
        {
            notes.Add(L($"{fan.Name}: カーブ「{curveName}」に温度センサーがないため、読み込んでいません"));
            return false;
        }
        var curve = new CurveConfig
        {
            Name = curveName,
            SensorId = MapSensor(sensor, catalog),
            Points = points,
            HysteresisCelsius = Math.Clamp(Num(c, "Deadband") ?? Num(c, "SelectedHysteresis") ?? Hysteresis(c) ?? 2, 0, 10),
        };
        curve.SortPoints();
        fan.Curves.Add(curve);
        return true;
    }

    private static readonly string[] LowTemp = { "IdleTemperature", "MinTemperature", "MinimumTemperature", "MinTemp", "TemperatureMin", "StartTemperature" };
    private static readonly string[] HighTemp = { "LoadTemperature", "MaxTemperature", "MaximumTemperature", "MaxTemp", "TemperatureMax", "EndTemperature" };
    private static readonly string[] LowSpeed = { "MinFanSpeed", "IdleFanSpeed", "MinSpeed", "MinimumSpeed", "MinimumFanSpeed", "IdleSpeed" };
    private static readonly string[] HighSpeed = { "MaxFanSpeed", "LoadFanSpeed", "MaxSpeed", "MaximumSpeed", "MaximumFanSpeed", "LoadSpeed" };
    private static readonly string[] FlatValue = { "FlatValue", "Value", "FixedValue", "Speed" };

    /// <summary>カーブの点を読む（グラフ・Auto・線形・固定）。固定なら flat に種類の名前を入れる</summary>
    public static List<CurvePoint>? ReadPoints(JsonElement c, out string? flat)
    {
        flat = null;
        if (c.TryGetProperty("Points", out var pts) && pts.ValueKind == JsonValueKind.Array)
        {
            var list = new List<CurvePoint>();
            foreach (var p in pts.EnumerateArray())
                if (ParsePoint(p) is { } cp) list.Add(cp);
            return list.Count >= 1 ? list : null;
        }
        if (First(c, LowTemp) is double t0 && First(c, HighTemp) is double t1 && First(c, LowSpeed) is double s0 && First(c, HighSpeed) is double s1)
        {
            if (t1 <= t0) t1 = t0 + 1;
            return new List<CurvePoint> { new() { Temp = t0, Duty = Clamp(s0) }, new() { Temp = t1, Duty = Clamp(s1) } };
        }
        if (First(c, FlatValue) is double v && !c.TryGetProperty("SelectedTempSource", out _))
        {
            flat = "flat";
            return new List<CurvePoint> { new() { Temp = 0, Duty = Clamp(v) } };
        }
        return null;
    }

    /// <summary>"50,15"（温度,%）か {X:50, Y:15} のような点</summary>
    private static CurvePoint? ParsePoint(JsonElement p)
    {
        if (p.ValueKind == JsonValueKind.String)
        {
            var parts = p.GetString()!.Split(',', StringSplitOptions.TrimEntries);
            // 小数点が「,」の地域で "50,5,15,5" のように書かれていても読む
            (string a, string b) = parts.Length switch
            {
                2 => (parts[0], parts[1]),
                4 => ($"{parts[0]}.{parts[1]}", $"{parts[2]}.{parts[3]}"),
                _ => ("", ""),
            };
            if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double t) &&
                double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return new CurvePoint { Temp = t, Duty = Clamp(d) };
            return null;
        }
        if (p.ValueKind == JsonValueKind.Object &&
            (Num(p, "X") ?? Num(p, "Temperature") ?? Num(p, "Temp")) is double x &&
            (Num(p, "Y") ?? Num(p, "Speed") ?? Num(p, "Value")) is double y)
            return new CurvePoint { Temp = x, Duty = Clamp(y) };
        return null;
    }

    private static double Clamp(double duty) => Math.Clamp(duty, 0, 100);

    /// <summary>新しい FanControl のヒステリシス（HysteresisConfig の上げ・下げ。Zeph-Iris は 1 つなので大きいほう）</summary>
    private static double? Hysteresis(JsonElement c)
    {
        if (!c.TryGetProperty("HysteresisConfig", out var h) || h.ValueKind != JsonValueKind.Object) return null;
        double? up = Num(h, "HysteresisValueUp"), down = Num(h, "HysteresisValueDown");
        return up is null ? down : down is null ? up : Math.Max(up.Value, down.Value);
    }

    private static bool LooksLikePump(string? name)
        => name is not null && (name.Contains("pump", StringComparison.OrdinalIgnoreCase) || name.Contains("ポンプ") || name.Contains("AIO", StringComparison.Ordinal));

    // ---------------------------------------------------------------------
    // ID の対応付け
    // ---------------------------------------------------------------------

    /// <summary>
    /// ID の比べる形: 小文字にして、数字だけの区切りを除く（最後の番号は残す）。
    /// 例: /lpc/it8686e/0/control/2 と /lpc/it8686e/control/2 は同じ形になる
    /// </summary>
    public static string IdKey(string id)
    {
        var seg = id.ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
        var keep = seg.Where((s, i) => i == seg.Length - 1 || !s.All(char.IsDigit));
        return string.Join('/', keep);
    }

    /// <summary>端子の ID を、この PC の端子の ID にする（見つからなければそのまま。対応付けの画面で選べる）</summary>
    public static string MapControl(string id, HardwareCatalog catalog)
    {
        if (catalog.Controls.Any(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))) return id;
        var same = catalog.Controls.Where(c => IdKey(c.Id) == IdKey(id)).ToList();
        if (same.Count == 1) return same[0].Id;
        // NvAPI 経由の GPU のファン（NVApiWrapper/<GPU>/control/N）→ この PC の N 番目の GPU のファン
        if (id.StartsWith("NVApiWrapper", StringComparison.OrdinalIgnoreCase) && LastNumber(id) is int n)
        {
            var gpu = catalog.Controls.Where(c => c.IsGpu).ToList();
            if (n < gpu.Count) return gpu[n].Id;
        }
        return id;
    }

    /// <summary>温度センサーの ID を、この PC のセンサーの ID にする（見つからなければそのまま）</summary>
    public static string MapSensor(string id, HardwareCatalog catalog)
    {
        if (catalog.FindSensor(id) is not null) return id;
        var same = catalog.Temperatures.Where(t => IdKey(t.Id) == IdKey(id)).ToList();
        return same.Count == 1 ? same[0].Id : id;
    }

    private static int? LastNumber(string id)
        => int.TryParse(id[(id.LastIndexOf('/') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;

    // ---------------------------------------------------------------------
    // JSON
    // ---------------------------------------------------------------------

    /// <summary>名前の配列を、入れ子のどこからでも探す（FanControl の版によって置き場所が違う）</summary>
    private static JsonElement? Find(JsonElement e, string name, int depth = 0)
    {
        if (depth > 6) return null;
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
                if (p.NameEquals(name) && p.Value.ValueKind == JsonValueKind.Array) return p.Value;
            foreach (var p in e.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Object && Find(p.Value, name, depth + 1) is { } hit) return hit;
        }
        return null;
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
        return null;
    }

    private static double? First(JsonElement e, string[] names) => names.Select(n => Num(e, n)).FirstOrDefault(v => v is not null);
}

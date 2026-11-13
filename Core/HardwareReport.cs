// ==================================================
// HardwareReport.cs - 「動作報告」: この PC で動いたことを、GitHub の動作報告のページに入れた状態で開く
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Text;

namespace ProFanController;

/// <summary>アプリが確かめられた、この PC での動き（数だけ。名前や番号は入れない）</summary>
public sealed record HardwareReportFacts(int FansControlled, bool SetupCompleted, bool LedsOn, bool MusicUsed);

/// <summary>
/// GitHub の動作報告（.github/ISSUE_TEMPLATE/hardware_report.yml）を、版・部品の型番と、
/// アプリが確かめたこと（数だけ）を入れた状態で開く URL を作る。
/// アプリからは何も送らない（ページを開くだけで、送るかどうかは使う人が GitHub で決める）。
/// 入れるのは型番と数だけ（PC の名前・ユーザー名・シリアル番号・センサーの値・設定は入れない）
/// </summary>
public static class HardwareReport
{
    public const string Template = "hardware_report.yml";
    /// <summary>1 つの欄に入れる最長の文字数（URL が長くなりすぎないように）</summary>
    public const int MaxField = 200;

    public static string Url(SupportInfo info, HardwareReportFacts facts)
    {
        var sb = new StringBuilder($"https://github.com/{AppUpdater.Repository}/issues/new?template={Template}");
        void Add(string field, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string v = value.Trim();
            sb.Append('&').Append(field).Append('=').Append(Uri.EscapeDataString(v.Length > MaxField ? v[..MaxField] : v));
        }
        Add("title", $"[Hardware] {Motherboard(info) ?? "PC"}");
        Add("version", info.AppVersion);
        Add("motherboard", Motherboard(info));
        Add("cpu", First(info, "Cpu"));
        Add("gpu", First(info, "Gpu"));
        sb.Append("&notes=").Append(Uri.EscapeDataString(Notes(info, facts)));
        return sb.ToString();
    }

    /// <summary>マザーボードの型番（Super I/O のチップが分かれば括弧で足す）</summary>
    public static string? Motherboard(SupportInfo info)
    {
        var mb = info.Devices.FirstOrDefault(d => d.Kind == "Motherboard");
        if (mb is null) return null;
        string? chip = mb.ChipName;
        return string.IsNullOrEmpty(chip) || mb.Name.Contains(chip, StringComparison.OrdinalIgnoreCase) ? mb.Name : $"{mb.Name} ({chip})";
    }

    private static string? First(SupportInfo info, string kind) => info.Devices.FirstOrDefault(d => d.Kind == kind)?.Name;

    /// <summary>「気づいたこと」の欄に入れる、アプリが確かめたこと（英語。報告は誰でも読むので）</summary>
    public static string Notes(SupportInfo info, HardwareReportFacts facts)
    {
        int temps = info.Sensors.Count(s => s.SensorType == "Temperature" && info.Values.TryGetValue(s.Id, out var v) && v is not null);
        int headers = info.Devices.Where(d => d.Kind is not "Gpu").Sum(d => d.ControlCount);
        int gpuFans = info.Devices.Where(d => d.Kind == "Gpu").Sum(d => d.ControlCount);
        int leds = info.RgbDevices.Count(d => d.LedCount > 0);
        string Yes(bool b) => b ? "yes" : "no";
        var lines = new List<string>
        {
            "Checked by the app (counts only):",
            $"- Windows: {info.Windows}",
            $"- PawnIO: {(info.PawnIoInstalled ? "installed" : "not installed")}",
            $"- Temperature sensors read: {temps}",
            $"- Fan headers found: {headers} (controlled: {facts.FansControlled})" + (gpuFans > 0 ? $", GPU fans: {gpuFans}" : ""),
            $"- Auto setup finished: {Yes(facts.SetupCompleted)}",
            $"- OpenRGB devices with LEDs: {leds}" + (facts.LedsOn ? " (lighting on)" : ""),
            $"- Music sync used: {Yes(facts.MusicUsed)}",
            "",
            "What worked / what did not (please write here):",
            "",
        };
        return string.Join("\n", lines);
    }
}

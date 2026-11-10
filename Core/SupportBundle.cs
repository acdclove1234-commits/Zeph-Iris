// ==================================================
// SupportBundle.cs - 不具合の報告用のまとめ (ログ・設定・ハードウェアの一覧を 1 つの zip に。個人的な情報は伏せる)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ProFanController;

/// <summary>報告に入れる、このPCの様子（画面から集めて渡す）</summary>
public sealed record SupportInfo(
    string AppVersion,
    string Windows,
    string Language,
    bool PawnIoInstalled,
    IReadOnlyList<DeviceInfo> Devices,
    IReadOnlyList<RgbDevice> RgbDevices,
    IReadOnlyList<SensorInfo> Sensors,
    IReadOnlyDictionary<string, float?> Values);

/// <summary>
/// 不具合の報告用の zip を作る。中身:
///   summary.txt（版・Windows・部品・LED のデバイス）、sensors.txt（センサーの一覧と今の値）、
///   config.json（設定。合言葉・通知の宛先などは消す）、logs\（直近 3 日のログ）。
/// どのファイルも、ユーザー名・PC の名前・ユーザーのフォルダー・メールアドレスなどを伏せてから入れる（SupportSanitizer）。
/// 送るのは利用者自身（GitHub の報告ページに zip を添付する）。アプリからはどこにも送らない
/// </summary>
public static class SupportBundle
{
    /// <summary>ログを入れる日数</summary>
    public const int LogDays = 3;
    /// <summary>1 つのログの大きさの上限（バイト。長すぎるログは最後の部分だけ入れる）</summary>
    public const int MaxLogBytes = 4 * 1024 * 1024;

    /// <summary>summary.txt の中身（画面にも見本として出す）</summary>
    public static string Summary(SupportInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{AppIdentity.Name} {info.AppVersion}");
        sb.AppendLine($"Windows: {info.Windows}");
        sb.AppendLine($"Language: {info.Language}");
        sb.AppendLine($"PawnIO: {(info.PawnIoInstalled ? "installed" : "not installed")}");
        sb.AppendLine();
        sb.AppendLine("Hardware:");
        foreach (var d in info.Devices)
            sb.AppendLine($"  [{d.Kind}] {d.Name}{(d.ChipName is { Length: > 0 } chip ? $" ({chip})" : "")}: {d.TempCount} temperature sensors, {d.FanCount} fans, {d.ControlCount} fan controls");
        sb.AppendLine();
        sb.AppendLine("OpenRGB devices:");
        if (info.RgbDevices.Count == 0) sb.AppendLine("  (none / not connected)");
        foreach (var r in info.RgbDevices)
            sb.AppendLine($"  {r.Name}: {r.LedCount} LEDs, type {r.Type}, zones [{(r.Zones is null ? "-" : string.Join(", ", r.Zones.Select((z, k) => $"{r.ZoneName(k)}={z}")))}], {ShortLocation(r.Location)}");
        return sb.ToString();
    }

    /// <summary>sensors.txt の中身（センサーの ID・名前・部品・種類・今の値）</summary>
    public static string Sensors(SupportInfo info)
    {
        var sb = new StringBuilder();
        foreach (var s in info.Sensors.OrderBy(s => s.HardwareName, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            string v = info.Values.TryGetValue(s.Id, out var x) && x is float f ? f.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "-";
            sb.AppendLine($"{s.Id}\t{s.Name}\t{s.HardwareName}\t{s.HardwareType}\t{s.SensorType}\t{v}");
        }
        return sb.ToString();
    }

    /// <summary>設定から、他人に見せてはいけないもの（スマホから見る合言葉・通知の宛先など）を消した JSON</summary>
    public static string SafeConfig(AppConfig cfg)
    {
        var c = cfg.Clone();
        c.Web.Token = "";
        c.Notify.NtfyTopic = c.Notify.NtfyTopic.Length > 0 ? "(hidden)" : "";
        c.Notify.DiscordWebhook = c.Notify.DiscordWebhook.Length > 0 ? "(hidden)" : "";
        if (!IsLocal(c.Notify.NtfyServer)) c.Notify.NtfyServer = c.Notify.NtfyServer.Length > 0 ? "(hidden)" : "";
        if (!IsLocal(c.Advisor.OllamaUrl)) c.Advisor.OllamaUrl = "(hidden)";
        return c.ToJson();
    }

    /// <summary>zip を作る（同じ名前のファイルがあれば上書き）。作ったファイルのパスを返す</summary>
    public static string Create(string zipPath, SupportInfo info, AppConfig cfg, string logDirectory, SupportSanitizer sanitizer, DateTime now)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        string tmp = zipPath + ".tmp";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Add(string name, string text)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(sanitizer.Clean(text));
            }
            Add("summary.txt", Summary(info));
            Add("sensors.txt", Sensors(info));
            Add("config.json", SafeConfig(cfg));
            foreach (var log in RecentLogs(logDirectory, now)) Add("logs/" + Path.GetFileName(log), ReadTail(log));
        }
        File.Move(tmp, zipPath, overwrite: true);
        return zipPath;
    }

    /// <summary>直近 LogDays 日のログ（アプリと見張りプロセスの両方）</summary>
    public static IEnumerable<string> RecentLogs(string logDirectory, DateTime now)
    {
        if (!Directory.Exists(logDirectory)) yield break;
        var days = Enumerable.Range(0, LogDays).Select(d => now.Date.AddDays(-d).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        foreach (var f in Directory.GetFiles(logDirectory, "*.log").OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            if (name.Length >= 8 && days.Contains(name[..8])) yield return f;
        }
    }

    /// <summary>ログを読む（書き込み中でも読めるように共有で開く。長すぎれば最後の MaxLogBytes だけ）</summary>
    private static string ReadTail(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxLogBytes) fs.Seek(-MaxLogBytes, SeekOrigin.End);
            using var r = new StreamReader(fs, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch (Exception ex)
        {
            return $"(could not read: {ex.Message})";
        }
    }

    /// <summary>GitHub の報告ページ（版・部品の欄を埋めた状態で開く。設定やログは入れない）</summary>
    public static string IssueUrl(SupportInfo info)
    {
        string Q(string s) => Uri.EscapeDataString(s.Length > 200 ? s[..200] : s);
        string? First(string kind) => info.Devices.FirstOrDefault(d => d.Kind == kind)?.Name;
        var sb = new StringBuilder($"https://github.com/{AppUpdater.Repository}/issues/new?template=bug_report.yml");
        sb.Append("&version=").Append(Q(info.AppVersion));
        if (First("Motherboard") is { } mb) sb.Append("&motherboard=").Append(Q(mb));
        if (First("Cpu") is { } cpu) sb.Append("&cpu=").Append(Q(cpu));
        if (First("Gpu") is { } gpu) sb.Append("&gpu=").Append(Q(gpu));
        sb.Append("&windows=").Append(Q(info.Windows));
        return sb.ToString();
    }

    /// <summary>報告用の zip の名前（日時入り）</summary>
    public static string FileName(DateTime now) => $"{AppIdentity.Name}-report-{now:yyyyMMdd-HHmm}.zip";

    /// <summary>Windows の名前と版（例: "Windows 11 Pro 24H2 (26100)"）</summary>
    public static string WindowsVersion()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = key?.GetValue("ProductName") as string ?? "Windows";
            string display = key?.GetValue("DisplayVersion") as string ?? "";
            string build = key?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Windows 11 でも ProductName が "Windows 10 …" のままのことがある（ビルド 22000 以降は 11）
            if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
            return $"{product} {display} ({build})".Replace("  ", " ", StringComparison.Ordinal).Trim();
        }
        catch
        {
            return Environment.OSVersion.VersionString;
        }
    }

    /// <summary>つながっている場所の種類だけ（"HID: \\?\HID#VID_…" → "HID"。機器ごとの番号は入れない）</summary>
    private static string ShortLocation(string location)
    {
        int colon = location.IndexOf(':');
        return colon > 0 ? location[..colon] : location;
    }

    private static bool IsLocal(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 報告に入れる文章から、個人的な情報を伏せる:
///   ユーザー名・PC の名前・ユーザーのフォルダー（C:\Users\名前）・メールアドレス・IP アドレス（PC の中だけの 127.0.0.1 は残す）・
///   MAC アドレス・機器ごとの番号（HID のパスの末尾）・指定した文字列（合言葉など）
/// </summary>
public sealed class SupportSanitizer
{
    private readonly List<(string Find, string Replace)> _words = new();
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant);
    private static readonly Regex UsersPath = new(@"([A-Za-z]:[\\/]+Users[\\/]+)[^\\/\s""'<>|]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Ipv4 = new(@"(?<![\d.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?![\d.])", RegexOptions.CultureInvariant);
    private static readonly Regex Mac = new(@"\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.CultureInvariant);
    private static readonly Regex HidInstance = new(@"(#[0-9A-Fa-f]&)[0-9A-Fa-f]{6,}&[0-9A-Fa-f]&[0-9A-Fa-f]{4}", RegexOptions.CultureInvariant);

    /// <param name="userName">Windows のユーザー名</param>
    /// <param name="machineName">PC の名前</param>
    /// <param name="secrets">ほかに伏せる文字列（合言葉・通知の宛先など。空の文字列は無視）</param>
    public SupportSanitizer(string userName, string machineName, IEnumerable<string>? secrets = null)
    {
        foreach (var s in secrets ?? Array.Empty<string>())
            if (s is { Length: >= 4 }) _words.Add((s, "(hidden)"));
        if (machineName.Length >= 2) _words.Add((machineName, "(pc)"));
        if (userName.Length >= 2) _words.Add((userName, "(user)"));
    }

    /// <summary>この PC で使う伏せ方（今のユーザー名・PC の名前と、設定の合言葉・通知の宛先）</summary>
    public static SupportSanitizer ForThisPc(AppConfig cfg)
        => new(Environment.UserName, Environment.MachineName, new[] { cfg.Web.Token, cfg.Notify.NtfyTopic, cfg.Notify.DiscordWebhook });

    public string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string s = UsersPath.Replace(text, "$1(user)");
        foreach (var (find, replace) in _words) s = s.Replace(find, replace, StringComparison.OrdinalIgnoreCase);
        s = Email.Replace(s, "(email)");
        s = Mac.Replace(s, "(mac)");
        s = HidInstance.Replace(s, "$1(id)");
        s = Ipv4.Replace(s, m => IsVersionLike(m) || m.Value == "127.0.0.1" || m.Value == "0.0.0.0" ? m.Value : "(ip)");
        return s;
    }

    /// <summary>版の番号（"6.10.1.0" など）を IP アドレスとまちがえて伏せない: どれかの数が 255 を超える、または全部が小さい版らしい数</summary>
    private static bool IsVersionLike(Match m)
    {
        var parts = Enumerable.Range(1, 4).Select(i => int.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (parts.Any(p => p > 255)) return true;
        return parts[0] < 10 && parts.Skip(1).All(p => p < 100) && parts[3] == 0;
    }
}

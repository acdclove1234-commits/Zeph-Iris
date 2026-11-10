// ==================================================
// SupportBundleTests.cs - 不具合の報告用の zip（個人的な情報を伏せる・中身・報告ページの URL）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.IO.Compression;
using ProFanController;

namespace LogicTests;

public static class SupportBundleTests
{
    private static SupportInfo Sample() => new(
        "6.10.1+abc", "Windows 11 Pro 24H2 (26100)", "ja", true,
        new List<DeviceInfo>
        {
            new("/lpc/nct6686d/0", "ASRock B860 Steel Legend WiFi", "Motherboard", "Nuvoton NCT6686D", 9, 6, 6, new List<string>()),
            new("/intelcpu/0", "Intel Core Ultra 5 245KF", "Cpu", null, 30, 0, 0, new List<string>()),
            new("/gpu-nvidia/0", "NVIDIA GeForce RTX 5070 Ti", "Gpu", null, 3, 3, 1, new List<string>()),
        },
        new List<RgbDevice> { new(0, "Logitech G610", 117, 5, null, @"HID: \\?\HID#VID_046D&PID_C338&MI_01&Col03#7&170650e5&0&0002#{4d1e55b2}") },
        new List<SensorInfo> { new("/intelcpu/0/temperature/16", "CPU Package", "Intel Core Ultra 5 245KF", "Cpu", "Temperature") },
        new Dictionary<string, float?> { ["/intelcpu/0/temperature/16"] = 48.5f });

    /// <summary>ユーザー名・PC の名前・ユーザーのフォルダー・メール・IP・MAC・機器ごとの番号・合言葉を伏せる。版の番号・127.0.0.1 は残す</summary>
    public static void TestSanitizer()
    {
        var s = new SupportSanitizer("taro", "TARO-DESKTOP", new[] { "secret-token-123", "" });
        string text = @"C:\Users\taro\AppData\Local\Temp\x.wav and c:/users/Taro/Desktop; PC TARO-DESKTOP; mail taro.y@example.com;
ip 192.168.1.23 local 127.0.0.1 version 6.10.1.0; mac 00:1A:2B:3C:4D:5E; token secret-token-123;
HID#VID_046D&PID_C338&MI_01&Col03#7&170650e5&0&0002#{4d1e55b2}";
        string c = s.Clean(text);
        T.Check(!c.Contains("taro", StringComparison.OrdinalIgnoreCase), $"user name hidden: {c}");
        T.Check(!c.Contains("TARO-DESKTOP", StringComparison.OrdinalIgnoreCase), "PC name hidden");
        T.Check(c.Contains(@"C:\Users\(user)\AppData", StringComparison.Ordinal), "user folder hidden, rest of the path kept");
        T.Check(!c.Contains("example.com", StringComparison.Ordinal) && c.Contains("(email)", StringComparison.Ordinal), "email hidden");
        T.Check(!c.Contains("192.168.1.23", StringComparison.Ordinal) && c.Contains("127.0.0.1", StringComparison.Ordinal), "LAN IP hidden, loopback kept");
        T.Check(c.Contains("6.10.1.0", StringComparison.Ordinal), "version numbers are not mistaken for IPs");
        T.Check(!c.Contains("00:1A:2B", StringComparison.Ordinal), "MAC hidden");
        T.Check(!c.Contains("secret-token-123", StringComparison.Ordinal), "secrets hidden");
        T.Check(!c.Contains("170650e5", StringComparison.Ordinal) && c.Contains("VID_046D&PID_C338", StringComparison.Ordinal), "device instance id hidden, model ids kept");
    }

    /// <summary>設定から、合言葉・通知の宛先・外のサーバーを消す（PC の中の Ollama は残す）</summary>
    public static void TestSafeConfig()
    {
        var cfg = new AppConfig();
        cfg.Normalize();
        cfg.Web.Token = "abcdefghijklmnop";
        cfg.Notify.NtfyTopic = "zephiris-1234";
        cfg.Notify.NtfyServer = "https://ntfy.example.org";
        cfg.Notify.DiscordWebhook = "https://discord.com/api/webhooks/1/abc";
        string json = SupportBundle.SafeConfig(cfg);
        T.Check(!json.Contains("abcdefghijklmnop", StringComparison.Ordinal), "web token removed");
        T.Check(!json.Contains("zephiris-1234", StringComparison.Ordinal), "ntfy topic removed");
        T.Check(!json.Contains("ntfy.example.org", StringComparison.Ordinal), "external ntfy server removed");
        T.Check(!json.Contains("discord.com", StringComparison.Ordinal), "discord webhook removed");
        T.Check(json.Contains("localhost:11434", StringComparison.Ordinal), "local Ollama URL kept");
        T.Check(cfg.Web.Token == "abcdefghijklmnop", "the real settings are not changed");
    }

    /// <summary>zip の中身: まとめ・センサー・設定・直近 3 日のログ（古いログは入れない）。どれも伏せたあと</summary>
    public static void TestCreateZip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zephiris-support-test-" + Guid.NewGuid().ToString("N"));
        string logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        try
        {
            var now = new DateTime(2026, 11, 11, 10, 0, 0);
            File.WriteAllText(Path.Combine(logs, "20261111.log"), @"started from C:\Users\taro\Downloads by TARO-PC");
            File.WriteAllText(Path.Combine(logs, "20261110_watchdog.log"), "watchdog ok");
            File.WriteAllText(Path.Combine(logs, "20261101.log"), "old");
            var cfg = new AppConfig();
            cfg.Normalize();
            string zip = SupportBundle.Create(Path.Combine(dir, SupportBundle.FileName(now)), Sample(), cfg, logs, new SupportSanitizer("taro", "TARO-PC"), now);
            using var a = ZipFile.OpenRead(zip);
            var names = a.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            T.Check(names.SequenceEqual(new[] { "config.json", "logs/20261110_watchdog.log", "logs/20261111.log", "sensors.txt", "summary.txt" }),
                $"zip entries ({string.Join(", ", names)})");
            string all = string.Join("\n", a.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
            T.Check(!all.Contains("taro", StringComparison.OrdinalIgnoreCase) && !all.Contains("TARO-PC", StringComparison.Ordinal), "nothing personal inside");
            T.Check(all.Contains("ASRock B860 Steel Legend WiFi", StringComparison.Ordinal) && all.Contains("48.5", StringComparison.Ordinal), "hardware and sensor values inside");
            T.Check(!all.Contains("170650e5", StringComparison.Ordinal), "device instance ids not in the summary");
            T.Check(Path.GetFileName(zip) == "Zeph-Iris-report-20261111-1000.zip", Path.GetFileName(zip));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>報告ページの URL: 報告の書式を選び、版・部品・Windows の欄を埋める（設定・ログは入れない）</summary>
    public static void TestIssueUrl()
    {
        string url = SupportBundle.IssueUrl(Sample());
        T.Check(url.StartsWith($"https://github.com/{AppUpdater.Repository}/issues/new?template=bug_report.yml", StringComparison.Ordinal), url);
        T.Check(url.Contains("&version=6.10.1%2Babc", StringComparison.Ordinal), "version filled");
        T.Check(url.Contains("&motherboard=ASRock%20B860", StringComparison.Ordinal) && url.Contains("&cpu=Intel%20Core", StringComparison.Ordinal)
                && url.Contains("&gpu=NVIDIA%20GeForce", StringComparison.Ordinal) && url.Contains("&windows=Windows%2011", StringComparison.Ordinal), "hardware fields filled");
        T.Check(url.Length < 2000, $"short enough for a browser ({url.Length})");
    }

    /// <summary>調査（SUPPORT_LIVE=フォルダー）: この PC の本物のログで zip を作り、中身に残った気になる言葉を数える（どこにも送らない）</summary>
    public static void TestLiveBundle()
    {
        string? outDir = Environment.GetEnvironmentVariable("SUPPORT_LIVE");
        string? logDir = Environment.GetEnvironmentVariable("SUPPORT_LOGS");
        if (string.IsNullOrEmpty(outDir) || string.IsNullOrEmpty(logDir)) return;
        var cfg = new AppConfig();
        cfg.Normalize();
        string zip = SupportBundle.Create(Path.Combine(outDir, "live-report.zip"), Sample(), cfg, logDir, SupportSanitizer.ForThisPc(cfg), DateTime.Now);
        using var a = ZipFile.OpenRead(zip);
        string all = string.Join("\n", a.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
        T.Info($"live zip: {a.Entries.Count} files, {all.Length} chars; user name present: {all.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase)}, " +
               $"PC name present: {all.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase)}, 'Users\\' paths: {System.Text.RegularExpressions.Regex.Matches(all, @"Users[\\/][^(]").Count}");
    }
}

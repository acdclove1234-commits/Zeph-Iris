// 「動作報告…」で開く GitHub のページ（HardwareReport）。部品は作り物
using ProFanController;

namespace LogicTests;

public static class HardwareReportTests
{
    private static SupportInfo Info(string? chip = "Nuvoton NCT6798D", bool withMb = true)
    {
        var devices = new List<DeviceInfo>();
        if (withMb) devices.Add(new DeviceInfo("/mb", "MSI MAG B650 TOMAHAWK WIFI (MS-7D75)", "Motherboard", chip, 6, 7, 7, new List<string>()));
        devices.Add(new DeviceInfo("/cpu", "AMD Ryzen 7 7800X3D", "Cpu", null, 3, 0, 0, new List<string>()));
        devices.Add(new DeviceInfo("/gpu", "AMD Radeon RX 7800 XT", "Gpu", null, 3, 1, 1, new List<string>()));
        var sensors = new List<SensorInfo>
        {
            new("/cpu/t/0", "Tctl", "AMD Ryzen 7 7800X3D", "Cpu", "Temperature"),
            new("/gpu/t/0", "GPU Core", "AMD Radeon RX 7800 XT", "GpuAmd", "Temperature"),
            new("/mb/t/9", "Unused", "Nuvoton NCT6798D", "SuperIO", "Temperature"),
            new("/mb/fan/0", "Fan #1", "Nuvoton NCT6798D", "SuperIO", "Fan"),
        };
        var values = new Dictionary<string, float?> { ["/cpu/t/0"] = 45, ["/gpu/t/0"] = 40, ["/mb/t/9"] = null, ["/mb/fan/0"] = 900 };
        var rgb = new List<RgbDevice> { new(0, "Motherboard RGB", 8), new(1, "Empty", 0) };
        return new SupportInfo("6.15.0", "Windows 11 Pro 24H2 (26100)", "ja", true, devices, rgb, sensors, values);
    }

    private static readonly HardwareReportFacts Facts = new(FansControlled: 5, SetupCompleted: true, LedsOn: true, MusicUsed: false);

    private static Dictionary<string, string> Query(string url)
    {
        var q = url[(url.IndexOf('?') + 1)..];
        return q.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
    }

    public static void TestUrlFields()
    {
        string url = HardwareReport.Url(Info(), Facts);
        T.Check(url.StartsWith("https://github.com/" + AppUpdater.Repository + "/issues/new?"), "GitHub の新しい報告のページ");
        var q = Query(url);
        T.Check(q["template"] == HardwareReport.Template, "動作報告のひな形");
        T.Check(q["version"] == "6.15.0", "版");
        T.Check(q["motherboard"] == "MSI MAG B650 TOMAHAWK WIFI (MS-7D75) (Nuvoton NCT6798D)", "マザーボードと Super I/O");
        T.Check(q["cpu"] == "AMD Ryzen 7 7800X3D" && q["gpu"] == "AMD Radeon RX 7800 XT", "CPU とグラボ");
        T.Check(q["title"].StartsWith("[Hardware] MSI MAG B650"), "題名にマザーボード");
        T.Check(!url.Contains(' ') && !url.Contains('\n'), "URL に空白・改行がそのまま入らない");
    }

    public static void TestNotesAreCountsOnly()
    {
        string n = HardwareReport.Notes(Info(), Facts);
        T.Check(n.Contains("Temperature sensors read: 2"), "読めた温度センサーの数（読めないものは数えない）");
        T.Check(n.Contains("Fan headers found: 7 (controlled: 5), GPU fans: 1"), "ファンの端子・制御している数・グラボのファン");
        T.Check(n.Contains("Auto setup finished: yes") && n.Contains("Music sync used: no"), "自動セットアップ・音楽連動");
        T.Check(n.Contains("OpenRGB devices with LEDs: 1 (lighting on)"), "LED のあるデバイスだけ数える");
        T.Check(n.Contains("PawnIO: installed"), "PawnIO");
        T.Check(!n.Contains("45") && !n.Contains("900"), "センサーの値は入れない");
        T.Check(!n.Contains("Motherboard RGB") && !n.Contains("Tctl"), "LED・センサーの名前は入れない");
        T.Check(!n.Contains(Environment.MachineName) && !n.Contains(Environment.UserName), "PC の名前・ユーザー名は入れない");
    }

    public static void TestMissingParts()
    {
        T.Check(HardwareReport.Motherboard(Info(chip: null)) == "MSI MAG B650 TOMAHAWK WIFI (MS-7D75)", "チップが分からなければ型番だけ");
        var noMb = Info(withMb: false);
        T.Check(HardwareReport.Motherboard(noMb) is null, "マザーボードが見つからなければ空");
        var q = Query(HardwareReport.Url(noMb, Facts));
        T.Check(!q.ContainsKey("motherboard") && q["title"] == "[Hardware] PC", "空の欄は入れない");

        var longName = new SupportInfo(new string('X', 500), "W", "en", false, new List<DeviceInfo>(), new List<RgbDevice>(),
            new List<SensorInfo>(), new Dictionary<string, float?>());
        T.Check(Query(HardwareReport.Url(longName, Facts))["version"].Length == HardwareReport.MaxField, "長すぎる欄は切る");
    }
}

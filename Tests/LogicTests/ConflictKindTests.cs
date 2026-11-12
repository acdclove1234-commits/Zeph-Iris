// ファン・LED を取り合うソフトの見分け（何を取り合うか・いまの設定で困るか・こつがあるか）
using ProFanController;

namespace LogicTests;

public static class ConflictKindTests
{
    public static void TestWhatEachAppFights()
    {
        var found = InstallCheck.FindConflictApps(new[] { "FanControl", "LightingService", "L-Connect 3", "MSIAfterburner", "explorer" });
        ConflictingApp Get(string label) => found.First(a => a.Label.Contains(label));
        T.Check(found.Count == 4, $"four conflicting apps found ({found.Count})");
        T.Check(Get("FanControl").Kind == ConflictKind.Fan && Get("FanControl").FightsFans, "FanControl fights the fans");
        T.Check(Get("Aura").Kind == ConflictKind.Led && !Get("Aura").FightsFans, "Aura only fights the LEDs");
        T.Check(Get("L-Connect").Kind == ConflictKind.Both, "L-Connect fights both");
        T.Check(Get("Afterburner").Kind == ConflictKind.GpuFan, "Afterburner only fights the GPU fans");
        T.Check(InstallCheck.KnownConflicts.All(a => !string.IsNullOrWhiteSpace(a.Tip)), "every known app has a tip on how to coexist");
    }

    public static void TestOnlyWarnWhenItMatters()
    {
        var aura = InstallCheck.FindConflictApps(new[] { "LightingService" }).Single();
        T.Check(!aura.Matters(rgbEnabled: false, gpuFans: false), "LED-only app with lighting off: no warning");
        T.Check(aura.Matters(rgbEnabled: true, gpuFans: false), "LED-only app with lighting on: warning");
        var ab = InstallCheck.FindConflictApps(new[] { "MSIAfterburner" }).Single();
        T.Check(!ab.Matters(true, gpuFans: false) && ab.Matters(false, gpuFans: true), "Afterburner matters only when this app controls GPU fans");
        var fc = InstallCheck.FindConflictApps(new[] { "FanControl" }).Single();
        T.Check(fc.Matters(false, false), "fan control app always matters");
        T.Check(InstallCheck.FindConflicts(new[] { "FanControl", "iCUE" }).Count == 2, "label list still works for the Lighting page");
    }
}

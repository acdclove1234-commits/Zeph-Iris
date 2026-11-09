// ==================================================
// RenameInstallerTests.cs - 名前の変更（ProFanController → Fanlume）とインストーラーのロジックテスト
//   (前の版の設定の引き継ぎ・前の名前の設定ファイル・アンインストーラーとのやり取り・インストーラーのスクリプトとの名前の一致)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class RenameInstallerTests
{
    private static string NewTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"fanlume_rename_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public static void TestAppNames()
    {
        T.Check(AppIdentity.Name == "Fanlume" && AppIdentity.ExeName == "Fanlume.exe", "new name and exe");
        T.Check(AppIdentity.LegacyExeName == "ProFanController.exe", "previous exe name");
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        T.Check(InstallCheck.RecommendedFolder.Equals(Path.Combine(pf, "Fanlume"), StringComparison.OrdinalIgnoreCase),
            $"install folder is Program Files\\Fanlume ({InstallCheck.RecommendedFolder})");
        T.Check(InstallCheck.UnstableFolderName(InstallCheck.RecommendedFolder) is null, "the install folder is a stable place");
    }

    public static void TestLegacySettingsCarriedOver()
    {
        string root = NewTempRoot();
        try
        {
            string legacy = Path.Combine(root, "Tools", "ProFanController");
            string here = Path.Combine(root, "Program Files", "Fanlume");
            Directory.CreateDirectory(here);
            Write(Path.Combine(legacy, "config.json"), "{\"old\":1}");
            Write(Path.Combine(legacy, "data", "minutes_20261109.csv"), "rows");
            Write(Path.Combine(legacy, "backups", "config_1.json"), "{}");
            Write(Path.Combine(legacy, "logs", "20261109.log"), "log");
            Write(Path.Combine(legacy, "ProFanController.exe"), "old exe");

            string? from = LegacyMigration.CopySettingsIfNeeded(here, legacy);
            T.Check(from is not null && Path.GetFullPath(from) == Path.GetFullPath(legacy), "settings copied from the old folder");
            T.Check(File.ReadAllText(Path.Combine(here, "config.json")) == "{\"old\":1}", "config.json carried over");
            T.Check(File.Exists(Path.Combine(here, "data", "minutes_20261109.csv")) && File.Exists(Path.Combine(here, "backups", "config_1.json")),
                "records and backups carried over");
            T.Check(!File.Exists(Path.Combine(here, "ProFanController.exe")) && !Directory.Exists(Path.Combine(here, "logs")),
                "the old exe and logs are not copied");
            T.Check(File.Exists(Path.Combine(legacy, "config.json")), "the old folder is left as it was");

            // 2 回目（ここに設定ができた）・新しい設定を変えたあと: 前の版の設定で上書きしない
            File.WriteAllText(Path.Combine(here, "config.json"), "{\"new\":2}");
            T.Check(LegacyMigration.CopySettingsIfNeeded(here, legacy) is null, "settings already here → nothing copied again");
            T.Check(File.ReadAllText(Path.Combine(here, "config.json")) == "{\"new\":2}", "the new settings are kept");

            string empty = Path.Combine(root, "Empty");
            Directory.CreateDirectory(empty);
            T.Check(LegacyMigration.CopySettingsIfNeeded(empty, Path.Combine(root, "Nowhere")) is null, "no old settings → nothing to do");
            T.Check(LegacyMigration.CopySettingsIfNeeded(legacy, legacy) is null, "running from the old folder itself → nothing to do");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 一時フォルダー */ }
        }
    }

    public static void TestLegacySettingsFileStillReadable()
    {
        string root = NewTempRoot();
        try
        {
            var cfg = new AppConfig();
            cfg.Profiles.Add(new ProfileConfig { Name = "標準" });
            var pkg = ConfigTransfer.Export(cfg, null, new[] { "標準" });
            T.Check(pkg.Format == SettingsPackage.FormatName && SettingsPackage.FormatName == "Fanlume.Settings", "new files use the new name");
            T.Check(ConfigTransfer.Extension == ".fanlume.json", "new extension");

            pkg.Format = SettingsPackage.LegacyFormatName;
            string old = Path.Combine(root, "old.profan.json");
            ConfigTransfer.Save(pkg, old);
            string why = "";
            bool loaded;
            try { loaded = ConfigTransfer.Load(old).Profiles.Count == pkg.Profiles.Count; }
            catch (Exception ex) { loaded = false; why = ex.Message; }
            T.Check(loaded, $"a file written by ProFanController (old name) can be read (profiles {pkg.Profiles.Count}{(why.Length > 0 ? ", " + why : "")})");

            pkg.Format = "SomethingElse.Settings";
            string other = Path.Combine(root, "other.json");
            ConfigTransfer.Save(pkg, other);
            bool rejected = false;
            try { ConfigTransfer.Load(other); }
            catch (InvalidDataException) { rejected = true; }
            T.Check(rejected, "a file from another app is still rejected");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 一時フォルダー */ }
        }
    }

    public static void TestUninstallerHandOff()
    {
        T.Check(InstallerCleanup.UninstallerArguments(true).Contains("/FANLUMEDATA=delete")
                && InstallerCleanup.UninstallerArguments(false).Contains("/FANLUMEDATA=keep"), "the data choice is passed on");
        T.Check(InstallerCleanup.UninstallerArguments(true).Contains("/SILENT") && InstallerCleanup.UninstallerArguments(true).Contains("/SUPPRESSMSGBOXES"),
            "the uninstaller does not ask again (the app already asked)");
        T.Check(InstallerCleanup.CrashDumpKey("Fanlume.exe").EndsWith(@"LocalDumps\Fanlume.exe"), "crash dump key per exe");

        string root = NewTempRoot();
        try
        {
            T.Check(InstallerCleanup.UninstallerPath(root) is null, "not installed with the installer → no uninstaller");
            Write(Path.Combine(root, InstallerCleanup.UninstallerName), "");
            T.Check(InstallerCleanup.UninstallerPath(root) == Path.Combine(root, "unins000.exe"), "installed with the installer → uninstaller found");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 一時フォルダー */ }
        }

        var facts = new CleanupFacts(AutoStartRegistered: true, FirewallRuleExists: true, CrashDumpSettingExists: true, PawnIoInstalled: false);
        foreach (bool deleteData in new[] { false, true })
        {
            var plan = CleanUninstall.Plan(new CleanupOptions(DeleteData: deleteData), facts);
            var results = plan.Where(s => s is not (CleanupStep.ExitApp or CleanupStep.DeleteData))
                              .Select(s => new StepResult(s, StepStatus.Done, "ok")).ToList();
            string byInstaller = CleanUninstall.Summary(results, plan, @"C:\Program Files\Fanlume", byUninstaller: true);
            string manual = CleanUninstall.Summary(results, plan, @"C:\Program Files\Fanlume");
            T.Check(byInstaller.Contains("アンインストーラー") && !byInstaller.Contains("削除してください"),
                $"installed: the uninstaller removes the app, nothing to delete by hand (data {deleteData})");
            T.Check(manual.Contains("削除してください") && manual.Contains(@"C:\Program Files\Fanlume"), $"not installed: the folder is deleted by hand (data {deleteData})");
        }
    }

    /// <summary>インストーラーのスクリプトが、アプリと同じ名前（印・合図・引数・前の版の場所）を使っているか</summary>
    public static void TestInstallerScriptMatchesApp()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ProFanController.csproj"))) d = d.Parent;
        T.Check(d is not null, "repository found");
        if (d is null) return;
        string iss = File.ReadAllText(Path.Combine(d.FullName, "Installer", "Fanlume.iss"));
        foreach (var name in new[]
                 {
                     AppIdentity.SingleInstance, AppIdentity.LegacySingleInstance, AppIdentity.ExitSignal, AppIdentity.LegacyExitSignal,
                     InstallerCleanup.Argument, InstallerCleanup.DeleteDataArgument, "/FANLUMEDATA=", AppIdentity.LegacyFolder + @"\config.json",
                     $"\"{AppIdentity.Name}\"", $"\"{AppIdentity.ExeName}\"",
                 })
            T.Check(iss.Contains(name, StringComparison.Ordinal), $"installer script uses {name}");
        string dist = File.ReadAllText(Path.Combine(d.FullName, "Distribution", "make-dist.ps1"));
        T.Check(dist.Contains(AppIdentity.ExeName) && dist.Contains("Fanlume-$version-setup.exe"), "the build script makes the setup from Fanlume.exe");
    }
}

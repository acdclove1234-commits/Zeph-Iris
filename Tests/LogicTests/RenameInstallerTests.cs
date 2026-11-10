// ==================================================
// RenameInstallerTests.cs - 名前の変更（ProFanController → Zeph-Iris）とインストーラーのロジックテスト
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
        string root = Path.Combine(Path.GetTempPath(), $"zephiris_rename_{Guid.NewGuid():N}");
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
        T.Check(AppIdentity.Name == "Zeph-Iris" && AppIdentity.ExeName == "Zeph-Iris.exe", "new name and exe");
        T.Check(AppIdentity.LegacyApps.Select(a => a.ExeName).SequenceEqual(new[] { "Fanlume.exe", "ProFanController.exe" }),
            "previous names, newest first");
        T.Check(AppIdentity.Fanlume.InstalledWithInstaller && !AppIdentity.ProFanController.InstalledWithInstaller
                && AppIdentity.Fanlume.Folder.EndsWith(@"\Fanlume") && AppIdentity.ProFanController.Folder == @"C:\Tools\ProFanController",
            "where each previous version lived");
        T.Check(AppIdentity.Fanlume.SingleInstance == @"Global\Fanlume.SingleInstance" && AppIdentity.Fanlume.ExitSignal == @"Global\Fanlume.Exit"
                && AppIdentity.Fanlume.FirewallRule == "Fanlume Web" && AppIdentity.ProFanController.TaskName == "ProFanController",
            "previous versions' Windows names");
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        T.Check(InstallCheck.RecommendedFolder.Equals(Path.Combine(pf, "Zeph-Iris"), StringComparison.OrdinalIgnoreCase),
            $"install folder is Program Files\\Zeph-Iris ({InstallCheck.RecommendedFolder})");
        T.Check(InstallCheck.UnstableFolderName(InstallCheck.RecommendedFolder) is null, "the install folder is a stable place");
    }

    public static void TestLegacySettingsCarriedOver()
    {
        string root = NewTempRoot();
        try
        {
            string legacy = Path.Combine(root, "Tools", "ProFanController");
            string here = Path.Combine(root, "Program Files", "Zeph-Iris");
            Directory.CreateDirectory(here);
            Write(Path.Combine(legacy, "config.json"), "{\"old\":1}");
            Write(Path.Combine(legacy, "data", "minutes_20261109.csv"), "rows");
            Write(Path.Combine(legacy, "backups", "config_1.json"), "{}");
            Write(Path.Combine(legacy, "logs", "20261109.log"), "log");
            Write(Path.Combine(legacy, "ProFanController.exe"), "old exe");

            string? from = LegacyMigration.CopySettingsIfNeeded(here, new[] { legacy });
            T.Check(from is not null && Path.GetFullPath(from) == Path.GetFullPath(legacy), "settings copied from the old folder");
            T.Check(File.ReadAllText(Path.Combine(here, "config.json")) == "{\"old\":1}", "config.json carried over");
            T.Check(File.Exists(Path.Combine(here, "data", "minutes_20261109.csv")) && File.Exists(Path.Combine(here, "backups", "config_1.json")),
                "records and backups carried over");
            T.Check(!File.Exists(Path.Combine(here, "ProFanController.exe")) && !Directory.Exists(Path.Combine(here, "logs")),
                "the old exe and logs are not copied");
            T.Check(File.Exists(Path.Combine(legacy, "config.json")), "the old folder is left as it was");

            // 2 回目（ここに設定ができた）・新しい設定を変えたあと: 前の版の設定で上書きしない
            File.WriteAllText(Path.Combine(here, "config.json"), "{\"new\":2}");
            T.Check(LegacyMigration.CopySettingsIfNeeded(here, new[] { legacy }) is null, "settings already here → nothing copied again");
            T.Check(File.ReadAllText(Path.Combine(here, "config.json")) == "{\"new\":2}", "the new settings are kept");

            string empty = Path.Combine(root, "Empty");
            Directory.CreateDirectory(empty);
            T.Check(LegacyMigration.CopySettingsIfNeeded(empty, new[] { Path.Combine(root, "Nowhere") }) is null, "no old settings → nothing to do");
            T.Check(LegacyMigration.CopySettingsIfNeeded(legacy, new[] { legacy }) is null, "running from the old folder itself → nothing to do");

            // Fanlume（新しいほう）と ProFanController の両方に設定がある: 新しいほうから引き継ぐ
            string fanlume = Path.Combine(root, "Program Files", "Fanlume");
            Write(Path.Combine(fanlume, "config.json"), "{\"fanlume\":1}");
            string fresh = Path.Combine(root, "Program Files", "Zeph-Iris-2");
            Directory.CreateDirectory(fresh);
            string? picked = LegacyMigration.CopySettingsIfNeeded(fresh, new[] { fanlume, legacy });
            T.Check(picked is not null && Path.GetFullPath(picked) == Path.GetFullPath(fanlume)
                    && File.ReadAllText(Path.Combine(fresh, "config.json")) == "{\"fanlume\":1}", "both old versions present → the newer one (Fanlume) is used");
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
            T.Check(pkg.Format == SettingsPackage.FormatName && SettingsPackage.FormatName == "Zeph-Iris.Settings", "new files use the new name");
            T.Check(ConfigTransfer.Extension == ".zeph-iris.json", "new extension");

            foreach (var legacyApp in AppIdentity.LegacyApps)
            {
                pkg.Format = legacyApp.SettingsFormat;
                string old = Path.Combine(root, $"old-{legacyApp.Name}.json");
                ConfigTransfer.Save(pkg, old);
                string why = "";
                bool loaded;
                try { loaded = ConfigTransfer.Load(old).Profiles.Count == pkg.Profiles.Count; }
                catch (Exception ex) { loaded = false; why = ex.Message; }
                T.Check(loaded, $"a file written by {legacyApp.Name} (old name) can be read{(why.Length > 0 ? " (" + why + ")" : "")}");
            }

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
        T.Check(InstallerCleanup.UninstallerArguments(true).Contains("/ZEPHIRISDATA=delete")
                && InstallerCleanup.UninstallerArguments(false).Contains("/ZEPHIRISDATA=keep"), "the data choice is passed on");
        T.Check(InstallerCleanup.UninstallerArguments(true).Contains("/SILENT") && InstallerCleanup.UninstallerArguments(true).Contains("/SUPPRESSMSGBOXES"),
            "the uninstaller does not ask again (the app already asked)");
        T.Check(InstallerCleanup.CrashDumpKey("Zeph-Iris.exe").EndsWith(@"LocalDumps\Zeph-Iris.exe"), "crash dump key per exe");

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
            string byInstaller = CleanUninstall.Summary(results, plan, @"C:\Program Files\Zeph-Iris", byUninstaller: true);
            string manual = CleanUninstall.Summary(results, plan, @"C:\Program Files\Zeph-Iris");
            T.Check(byInstaller.Contains("アンインストーラー") && !byInstaller.Contains("削除してください"),
                $"installed: the uninstaller removes the app, nothing to delete by hand (data {deleteData})");
            T.Check(manual.Contains("削除してください") && manual.Contains(@"C:\Program Files\Zeph-Iris"), $"not installed: the folder is deleted by hand (data {deleteData})");
        }
    }

    /// <summary>インストーラーのスクリプトが、アプリと同じ名前（印・合図・引数・前の版の場所）を使っているか</summary>
    public static void TestInstallerScriptMatchesApp()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ProFanController.csproj"))) d = d.Parent;
        T.Check(d is not null, "repository found");
        if (d is null) return;
        string iss = File.ReadAllText(Path.Combine(d.FullName, "Installer", "Zeph-Iris.iss"));
        foreach (var name in new[]
                 {
                     AppIdentity.SingleInstance, AppIdentity.ExitSignal,
                     InstallerCleanup.Argument, InstallerCleanup.DeleteDataArgument, "/ZEPHIRISDATA=", AppIdentity.ProFanController.Folder + @"\config.json",
                     @"\Fanlume\config.json", $"\"{AppIdentity.Name}\"", $"\"{AppIdentity.ExeName}\"",
                 }.Concat(AppIdentity.LegacyApps.SelectMany(a => new[] { a.SingleInstance, a.ExitSignal })))
            T.Check(iss.Contains(name, StringComparison.Ordinal), $"installer script uses {name}");
        T.Check(!iss.Contains("6D16AD73-005A-4A6E-95CA-46D885E2EF1E", StringComparison.OrdinalIgnoreCase),
            "a new installer AppId (so it does not install into the old Fanlume folder)");
        T.Check(AppIdentity.FanlumeUninstallKeepData.Contains("/FANLUMEDATA=keep") && AppIdentity.FanlumeUninstallKeepData.Contains("/SILENT"),
            "the old Fanlume uninstaller is told to keep settings and records");
        string dist = File.ReadAllText(Path.Combine(d.FullName, "Distribution", "make-dist.ps1"));
        T.Check(dist.Contains(AppIdentity.ExeName) && dist.Contains("Zeph-Iris-$version-setup.exe"), "the build script makes the setup from Zeph-Iris.exe");
    }

    /// <summary>
    /// PawnIO の同梱: 作るスクリプトは公式のリリースから決まった版を取り、SHA-256 を確かめて ISCC に渡す。
    /// インストーラーは、入っていないときだけ一時フォルダーに出して -install -silent で実行し、だめなら winget に戻る。
    /// ライセンスの案内（GPL の全文と、ソースコードの場所）も入っている
    /// </summary>
    public static void TestPawnIoBundled()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ProFanController.csproj"))) d = d.Parent;
        if (d is null) { T.Check(false, "repository found"); return; }
        string iss = File.ReadAllText(Path.Combine(d.FullName, "Installer", "Zeph-Iris.iss"));
        string dist = File.ReadAllText(Path.Combine(d.FullName, "Distribution", "make-dist.ps1"));
        string notices = File.ReadAllText(Path.Combine(d.FullName, "Distribution", "licenses", "THIRD-PARTY-NOTICES.txt"));
        var version = System.Text.RegularExpressions.Regex.Match(dist, @"\$pawnVersion = '([0-9.]+)'").Groups[1].Value;
        var sha = System.Text.RegularExpressions.Regex.Match(dist, @"\$pawnSha256 = '([0-9A-F]{64})'").Groups[1].Value;
        T.Check(version.Length > 0 && sha.Length == 64, $"build script pins the PawnIO version and SHA-256 ({version})");
        T.Check(dist.Contains("https://github.com/namazso/PawnIO.Setup/releases/download/$pawnVersion/PawnIO_setup.exe", StringComparison.Ordinal),
            "downloaded only from the official PawnIO release");
        T.Check(dist.Contains("Get-FileHash", StringComparison.Ordinal) && dist.Contains("/DPawnIoSetup=$pawnSetup", StringComparison.Ordinal), "hash checked and passed to ISCC");
        T.Check(iss.Contains("Source: \"{#PawnIoSetup}\"; Flags: dontcopy", StringComparison.Ordinal), "bundled, not copied to the app folder");
        T.Check(iss.Contains("ExtractTemporaryFile('PawnIO_setup.exe')", StringComparison.Ordinal) && iss.Contains("'-install -silent'", StringComparison.Ordinal),
            "run silently from the temp folder");
        int bundled = iss.IndexOf("ExtractTemporaryFile('PawnIO_setup.exe')", StringComparison.Ordinal);
        int winget = iss.IndexOf("Exec(WingetPath(), PawnIoWingetArguments", StringComparison.Ordinal);
        T.Check(bundled > 0 && winget > bundled, "winget only as the fallback after the bundled setup");
        T.Check(iss.Contains("function NeedRestart(): Boolean;", StringComparison.Ordinal) && iss.Contains("ResultCode = 3010", StringComparison.Ordinal),
            "asks for a restart when PawnIO needs one (3010)");
        T.Check(notices.Contains($"PawnIO {version}", StringComparison.Ordinal) && notices.Contains("https://github.com/namazso/PawnIO", StringComparison.Ordinal),
            "notice names the bundled version and the source code");
        T.Check(File.Exists(Path.Combine(d.FullName, "Distribution", "licenses", "PawnIO-COPYING.txt")), "the GPL text is shipped");
        T.Check(!iss.Contains("winget の公式の取得元からインストールします", StringComparison.Ordinal), "setup no longer says the internet is needed");
    }
}

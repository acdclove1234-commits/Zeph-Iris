// ==================================================
// SetupUninstallTests.cs - PawnIO のアプリ内インストールと、アンインストール（お片付け）のテスト
//   (winget の終了コードの読み取り・出力の表示・結果の案内・手順の順番・中止の判断・設定と記録の消し方)
//   winget・schtasks などは実行しない（この PC には何もしない）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class SetupUninstallTests
{
    private static WingetRun Run(int code) => new(code, false, false, Array.Empty<string>());

    public static void TestWingetCodesClassified()
    {
        T.Check(Winget.Classify(0) == WingetOutcome.Success, "0 = success");
        T.Check(Winget.Classify(unchecked((int)0x8A15002B)) == WingetOutcome.AlreadyInstalled, "0x8A15002B (no applicable update) = already installed");
        T.Check(Winget.Classify(unchecked((int)0x8A150061)) == WingetOutcome.AlreadyInstalled, "0x8A150061 (package already installed) = already installed");
        T.Check(Winget.Classify(unchecked((int)0x8A15010D)) == WingetOutcome.AlreadyInstalled, "0x8A15010D (install already installed) = already installed");
        T.Check(Winget.Classify(3010) == WingetOutcome.SuccessRebootRequired, "3010 = success, reboot required");
        T.Check(Winget.Classify(unchecked((int)0x8A150109)) == WingetOutcome.SuccessRebootRequired, "0x8A150109 = reboot to finish");
        T.Check(Winget.Classify(unchecked((int)0x8A15010A)) == WingetOutcome.RebootThenRetry, "0x8A15010A = reboot then retry (not success)");
        T.Check(Winget.Classify(unchecked((int)0x8A150014)) == WingetOutcome.PackageNotFound, "0x8A150014 = package not found (install)");
        T.Check(Winget.Classify(unchecked((int)0x8A150008)) == WingetOutcome.NetworkError, "0x8A150008 = download failed");
        T.Check(Winget.Classify(unchecked((int)0x8A150107)) == WingetOutcome.NetworkError, "0x8A150107 = no network");
        T.Check(Winget.Classify(unchecked((int)0x8A150045)) == WingetOutcome.SourceError, "0x8A150045 = source open failed");
        T.Check(Winget.Classify(unchecked((int)0x8A15003A)) == WingetOutcome.BlockedByPolicy, "0x8A15003A = blocked by policy");
        T.Check(Winget.Classify(unchecked((int)0x8A15010F)) == WingetOutcome.BlockedByPolicy, "0x8A15010F = install blocked by policy");
        T.Check(Winget.Classify(unchecked((int)0x8A150102)) == WingetOutcome.AnotherInstallRunning, "0x8A150102 = another install running");
        T.Check(Winget.Classify(unchecked((int)0x8A150101)) == WingetOutcome.InUse, "0x8A150101 = in use");
        T.Check(Winget.Classify(unchecked((int)0x8A15010C)) == WingetOutcome.Cancelled, "0x8A15010C = cancelled");
        T.Check(Winget.Classify(unchecked((int)0x8A150002)) == WingetOutcome.WingetTooOld, "0x8A150002 = invalid arguments (old winget)");
        T.Check(Winget.Classify(unchecked((int)0x8A150019)) == WingetOutcome.RequiresAdmin, "0x8A150019 = requires admin");
        T.Check(Winget.Classify(1) == WingetOutcome.Failed && Winget.Classify(unchecked((int)0x8A1500FF)) == WingetOutcome.Failed, "unknown codes = failed");
        // アンインストールでは「見つからない」は「入っていない」
        T.Check(Winget.ClassifyUninstall(unchecked((int)0x8A150014)) == WingetOutcome.NotInstalled, "uninstall: 0x8A150014 = not installed");
        T.Check(Winget.ClassifyUninstall(unchecked((int)0x8A150030)) == WingetOutcome.Failed, "uninstall: 0x8A150030 (uninstall command failed) = failed");
        T.Check(Winget.ClassifyUninstall(0) == WingetOutcome.Success, "uninstall: 0 = success");
        // 表記
        T.Check(Winget.CodeText(unchecked((int)0x8A150014)) == "0x8A150014", $"hex code text ({Winget.CodeText(unchecked((int)0x8A150014))})");
        T.Check(Winget.CodeText(3010) == "3010", "installer code shown as decimal");
        // 起動できない・時間切れは、終了コードより優先
        T.Check(WingetRun.Missing().Outcome == WingetOutcome.WingetMissing, "missing winget outcome");
        T.Check(new WingetRun(null, false, true, Array.Empty<string>()).Outcome == WingetOutcome.TimedOut, "timed out outcome");
    }

    public static void TestWingetLocationIsFullPathNeverBareName()
    {
        var list = Winget.Candidates(@"C:\Users\someone\AppData\Local").ToList();
        T.Check(list.Count == 1 && list[0] == @"C:\Users\someone\AppData\Local\Microsoft\WindowsApps\winget.exe", $"alias path ({string.Join(";", list)})");
        T.Check(list.All(Path.IsPathRooted), "every candidate is a full path (no search of the app folder)");
        T.Check(!Winget.Candidates("").Any() && !Winget.Candidates(null).Any(), "no LOCALAPPDATA -> no candidate (treated as winget missing)");
    }

    public static void TestArgumentsAreNonInteractive()
    {
        string a = PawnIoSetup.InstallArguments;
        T.Check(a.StartsWith("install ") && a.Contains("--id namazso.PawnIO") && a.Contains(" -e "), "install: exact package id");
        T.Check(a.Contains("--accept-source-agreements") && a.Contains("--accept-package-agreements"), "install: agreements accepted (no prompt)");
        T.Check(a.Contains("--source winget") && a.Contains("--silent"), "install: winget source only, silent installer");
        string u = PawnIoSetup.UninstallArguments;
        T.Check(u.StartsWith("uninstall ") && u.Contains("--id namazso.PawnIO") && u.Contains("--silent") && u.Contains("--accept-source-agreements"), "uninstall arguments");
        T.Check(!u.Contains("--accept-package-agreements"), "uninstall has no package-agreements flag (winget uninstall does not accept it)");
        T.Check(PawnIoSetup.OfficialSite.StartsWith("https://"), "official site is https");
    }

    public static void TestProgressLinesCleaned()
    {
        T.Check(Winget.CleanLine("  -  ") == "" && Winget.CleanLine(@"\") == "" && Winget.CleanLine("|") == "", "spinner-only lines dropped");
        T.Check(Winget.CleanLine("\x1b[32mSuccessfully installed\x1b[0m") == "Successfully installed", "ANSI colors removed");
        string bar = Winget.CleanLine("  ██████████████▒▒▒▒▒▒▒▒  1.20 MB / 2.40 MB");
        T.Check(bar == "1.20 MB / 2.40 MB", $"progress bar removed ({bar})");
        T.Check(Winget.Describe(bar) == "ダウンロードしています… 1.20 MB / 2.40 MB", $"download size shown ({Winget.Describe(bar)})");
        T.Check(Winget.Describe("Successfully installed")!.StartsWith("インストールが終わりました"), "english success described");
        T.Check(Winget.Describe("インストールが完了しました")!.StartsWith("インストールが終わりました"), "japanese success described");
        T.Check(Winget.Describe("Starting package uninstall...")!.StartsWith("アンインストールしています"), "uninstall start described");
        T.Check(Winget.Describe("Successfully verified installer hash") == "ダウンロードしたファイルを確かめています…", "hash check described");
        T.Check(Winget.Describe("Found PawnIO [namazso.PawnIO] Version 2.2.0") == "winget: Found PawnIO [namazso.PawnIO] Version 2.2.0", "other lines shown as-is");
        T.Check(Winget.Describe("") is null && Winget.Describe("12.3") is null, "lines without words are not shown");
        T.Check(Winget.Describe("��� abc") is null, "garbled lines (wrong encoding) are not shown");
        T.Check(Winget.Describe(new string('a', 200))!.Length <= 130, "long lines shortened");
    }

    public static void TestInstallResults()
    {
        // 成功して見つかった → このアプリを起動し直す（起動時に PawnIO がなかったので）
        var ok = PawnIoSetup.InterpretInstall(Run(0), detectedAfter: true, loadedAtStartup: false);
        T.Check(ok.Ok && ok.NeedsAppRestart && !ok.NeedsPcRestart && !ok.OfferOfficialSite, "success + detected -> restart app");
        // すでに入っていて、起動時にも使えていた → そのまま使える
        var already = PawnIoSetup.InterpretInstall(Run(unchecked((int)0x8A15002B)), detectedAfter: true, loadedAtStartup: true);
        T.Check(already.Ok && !already.NeedsAppRestart && !already.NeedsPcRestart, "already installed and loaded -> nothing to do");
        // 再起動が要る
        var reboot = PawnIoSetup.InterpretInstall(Run(3010), detectedAfter: true, loadedAtStartup: false);
        T.Check(reboot.Ok && reboot.NeedsPcRestart, "3010 -> PC restart");
        // 成功と返したのに見つからない → 失敗扱い・PC の再起動と公式サイトを案内
        var ghost = PawnIoSetup.InterpretInstall(Run(0), detectedAfter: false, loadedAtStartup: false);
        T.Check(!ghost.Ok && ghost.NeedsPcRestart && ghost.OfferOfficialSite, "success but not detected -> PC restart + official site");
        // winget がない → 公式サイト
        var missing = PawnIoSetup.InterpretInstall(WingetRun.Missing(), detectedAfter: false, loadedAtStartup: false);
        T.Check(!missing.Ok && missing.OfferOfficialSite && missing.Message.Contains("アプリ インストーラー"), "winget missing -> official site");
        // ネットワーク → 公式サイトも案内
        var net = PawnIoSetup.InterpretInstall(Run(unchecked((int)0x8A150008)), detectedAfter: false, loadedAtStartup: false);
        T.Check(!net.Ok && net.OfferOfficialSite && net.Message.Contains("0x8A150008"), "download failure shows code and offers official site");
        // ポリシーで止められている → 公式サイトは勧めない（同じく止められているはず）
        var policy = PawnIoSetup.InterpretInstall(Run(unchecked((int)0x8A15003A)), detectedAfter: false, loadedAtStartup: false);
        T.Check(!policy.Ok && !policy.OfferOfficialSite, "blocked by policy -> no official site push");
        // エラーでも、入っていれば使える方向で案内する
        var partial = PawnIoSetup.InterpretInstall(Run(unchecked((int)0x8A150003)), detectedAfter: true, loadedAtStartup: false);
        T.Check(partial.Ok && partial.NeedsAppRestart, "error code but detected -> restart app");
        // 時間切れ
        var slow = PawnIoSetup.InterpretInstall(new WingetRun(null, false, true, Array.Empty<string>()), detectedAfter: false, loadedAtStartup: false);
        T.Check(!slow.Ok && slow.OfferOfficialSite, "timeout -> official site");
        // 使用中 → PC の再起動
        var inUse = PawnIoSetup.InterpretInstall(Run(unchecked((int)0x8A150101)), detectedAfter: false, loadedAtStartup: false);
        T.Check(!inUse.Ok && inUse.NeedsPcRestart, "in use -> PC restart");
    }

    public static void TestUninstallResults()
    {
        var ok = PawnIoSetup.InterpretUninstall(Run(0), stillInstalled: false);
        T.Check(ok.Ok && !ok.NeedsPcRestart, "uninstalled");
        var none = PawnIoSetup.InterpretUninstall(Run(unchecked((int)0x8A150014)), stillInstalled: false);
        T.Check(none.Ok && none.Message.Contains("インストールされていません"), "not installed -> ok");
        var stuck = PawnIoSetup.InterpretUninstall(Run(unchecked((int)0x8A150014)), stillInstalled: true);
        T.Check(!stuck.Ok && stuck.Message.Contains("設定"), "winget cannot find it but registry says installed -> manual hint");
        var reboot = PawnIoSetup.InterpretUninstall(Run(3010), stillInstalled: true);
        T.Check(reboot.Ok && reboot.NeedsPcRestart, "3010 -> PC restart");
        var leftover = PawnIoSetup.InterpretUninstall(Run(0), stillInstalled: true);
        T.Check(!leftover.Ok && leftover.NeedsPcRestart, "success but still installed -> restart and check");
        var inUse = PawnIoSetup.InterpretUninstall(Run(unchecked((int)0x8A150101)), stillInstalled: true);
        T.Check(!inUse.Ok && inUse.NeedsPcRestart && inUse.Message.Contains("FanControl"), "in use by other software");
        var missing = PawnIoSetup.InterpretUninstall(WingetRun.Missing(), stillInstalled: true);
        T.Check(!missing.Ok && missing.Message.Contains(PawnIoSetup.ManualUninstallHint), "winget missing -> manual hint");
    }

    private static readonly CleanupFacts AllFacts = new(AutoStartRegistered: true, FirewallRuleExists: true, CrashDumpSettingExists: true, PawnIoInstalled: true);

    public static void TestPlanOrder()
    {
        var plan = CleanUninstall.Plan(new CleanupOptions(DeleteData: true, UninstallPawnIo: true), AllFacts).ToList();
        T.Check(plan[0] == CleanupStep.RestoreFans, "fans go back to BIOS first");
        T.Check(plan.IndexOf(CleanupStep.StopFanControl) == 1, "then fan control stops (hardware closed)");
        T.Check(plan.IndexOf(CleanupStep.UninstallPawnIo) > plan.IndexOf(CleanupStep.StopFanControl), "PawnIO removed only after the driver is closed");
        T.Check(plan.IndexOf(CleanupStep.StopLighting) < plan.IndexOf(CleanupStep.ExitApp), "lighting released before exit");
        T.Check(plan.IndexOf(CleanupStep.RemoveAutoStart) < plan.IndexOf(CleanupStep.ExitApp), "auto-start removed before exit");
        T.Check(plan[^2] == CleanupStep.ExitApp && plan[^1] == CleanupStep.DeleteData, "data deleted after the app has exited (nothing writes it anymore)");
        T.Check(plan.Distinct().Count() == plan.Count, "no step twice");
        T.Check(plan.All(s => !string.IsNullOrWhiteSpace(CleanUninstall.Describe(s)) && CleanUninstall.Describe(s) != s.ToString()), "every step has a description");
    }

    public static void TestPlanDefaultsAndFacts()
    {
        // 既定（どちらもオフ）: 設定・記録は消さない・PawnIO は残す
        var plan = CleanUninstall.Plan(new CleanupOptions(), AllFacts);
        T.Check(!plan.Contains(CleanupStep.DeleteData) && !plan.Contains(CleanupStep.UninstallPawnIo), "defaults keep data and PawnIO");
        T.Check(plan.Contains(CleanupStep.RestoreFans) && plan.Contains(CleanupStep.StopLighting) && plan.Contains(CleanupStep.RemoveAutoStart) && plan.Contains(CleanupStep.ExitApp),
            "mandatory steps always present");
        // 何も登録されていない PC: 行わないことは並べない（確認の画面は「実際に行うこと」だけ）
        var bare = CleanUninstall.Plan(new CleanupOptions(UninstallPawnIo: true), new CleanupFacts(false, false, false, PawnIoInstalled: false));
        T.Check(!bare.Contains(CleanupStep.RemoveAutoStart) && !bare.Contains(CleanupStep.RemoveFirewallRule) && !bare.Contains(CleanupStep.RemoveCrashDumpSetting),
            "nothing registered -> those steps not listed");
        T.Check(!bare.Contains(CleanupStep.UninstallPawnIo), "PawnIO not installed -> no uninstall step even if chosen");
        T.Check(bare.SequenceEqual(new[] { CleanupStep.RestoreFans, CleanupStep.StopFanControl, CleanupStep.StopLighting, CleanupStep.ExitApp }), "minimal plan");
    }

    public static void TestAbortWhenFansNotRestored()
    {
        var failed = new List<StepResult> { new(CleanupStep.RestoreFans, StepStatus.Failed, "戻せませんでした") };
        T.Check(CleanUninstall.Aborted(failed), "fan restore failure aborts");
        T.Check(CleanUninstall.SkipReason(CleanupStep.RemoveAutoStart, failed) is not null, "nothing else runs after the abort");
        T.Check(CleanUninstall.SkipReason(CleanupStep.ExitApp, failed) is not null, "the app does not exit after the abort (it keeps controlling the fans)");
        string summary = CleanUninstall.Summary(failed, CleanUninstall.Plan(new CleanupOptions(), AllFacts), @"C:\Tools\Fanlume");
        T.Check(summary.Contains("中止") && summary.Contains("何も変えていません"), "abort summary says nothing changed");
        T.Check(CleanUninstall.AbortsOnFailure(CleanupStep.RestoreFans) && !CleanUninstall.AbortsOnFailure(CleanupStep.RemoveAutoStart)
                && !CleanUninstall.AbortsOnFailure(CleanupStep.StopLighting), "only the fan restore aborts");
    }

    public static void TestPawnIoKeptWhenDriverStillOpen()
    {
        var results = new List<StepResult>
        {
            new(CleanupStep.RestoreFans, StepStatus.Done, ""),
            new(CleanupStep.StopFanControl, StepStatus.Failed, "止めきれませんでした"),
        };
        T.Check(CleanUninstall.SkipReason(CleanupStep.UninstallPawnIo, results) is not null, "PawnIO not uninstalled while the driver may be open");
        T.Check(CleanUninstall.SkipReason(CleanupStep.RemoveAutoStart, results) is null, "other steps continue");
        var good = new List<StepResult> { new(CleanupStep.RestoreFans, StepStatus.Done, ""), new(CleanupStep.StopFanControl, StepStatus.Done, "") };
        T.Check(CleanUninstall.SkipReason(CleanupStep.UninstallPawnIo, good) is null, "PawnIO uninstall allowed after a clean stop");
    }

    public static void TestSummaryMentionsProblemsAndFolder()
    {
        var plan = CleanUninstall.Plan(new CleanupOptions(DeleteData: true), AllFacts);
        var results = new List<StepResult>
        {
            new(CleanupStep.RestoreFans, StepStatus.Done, "BIOS 制御に戻しました"),
            new(CleanupStep.StopFanControl, StepStatus.Done, ""),
            new(CleanupStep.StopLighting, StepStatus.Done, ""),
            new(CleanupStep.RemoveAutoStart, StepStatus.Failed, "自動起動を消せませんでした"),
        };
        string s = CleanUninstall.Summary(results, plan, @"C:\Tools\Fanlume");
        T.Check(s.Contains("自動起動を消せませんでした"), "failed step listed");
        T.Check(s.Contains(@"C:\Tools\Fanlume"), "folder to delete named");
        T.Check(s.Contains("消します"), "data deletion after exit announced");
        var clean = CleanUninstall.Summary(results.Take(3).ToList(), CleanUninstall.Plan(new CleanupOptions(), AllFacts), @"D:\PFC");
        T.Check(clean.Contains("BIOS") && clean.Contains("残します"), "clean summary: BIOS + data kept");
    }

    public static void TestDataDeletionOnlyTouchesAppData()
    {
        string root = Path.Combine(Path.GetTempPath(), "pfc_cleanup_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            void Touch(string rel)
            {
                string p = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, "x");
            }
            // アプリ本体（残す）
            Touch("Fanlume.exe");
            Touch("Fanlume.dll");
            Touch("LibreHardwareMonitorLib.dll");
            Touch("はじめにお読みください.txt");
            Touch("my-notes.txt");                 // 利用者が置いたもの（残す）
            Touch(@"licenses\LICENSE.txt");        // 配布物（残す）
            // 設定・ログ・記録（消す）
            Touch("config.json");
            Touch("config.json.tmp");
            Touch("config.broken_20260101_120000.json");
            Touch(@"logs\20261107.log");
            Touch(@"logs\dumps\Fanlume.exe.1234.dmp");
            Touch(@"data\minutes_20261107.csv");
            Touch(@"data\energy.json");
            Touch(@"backups\config_20261107_120000_startup.json");
            Touch(@"records\20261107_120000.csv");
            Touch(@"reports\AI_20261107_1200.txt");
            Touch(@"images\motherboard.png");
            File.SetAttributes(Path.Combine(root, @"data\energy.json"), FileAttributes.ReadOnly);

            var targets = CleanUninstall.DataTargets(root);
            T.Check(targets.Count == 9, $"9 targets found ({targets.Count}: {string.Join(", ", targets.Select(Path.GetFileName))})");
            T.Check(!targets.Any(t => t.EndsWith(".exe") || t.EndsWith(".dll")), "exe and dll never targeted");

            var failed = CleanUninstall.DeleteData(root);
            T.Check(failed.Count == 0, $"all deleted ({string.Join(", ", failed)})");
            T.Check(File.Exists(Path.Combine(root, "Fanlume.exe")) && File.Exists(Path.Combine(root, "Fanlume.dll"))
                    && File.Exists(Path.Combine(root, "LibreHardwareMonitorLib.dll")), "app binaries kept");
            T.Check(File.Exists(Path.Combine(root, "my-notes.txt")) && File.Exists(Path.Combine(root, "はじめにお読みください.txt"))
                    && File.Exists(Path.Combine(root, @"licenses\LICENSE.txt")), "unrelated files kept");
            T.Check(!File.Exists(Path.Combine(root, "config.json")) && !Directory.Exists(Path.Combine(root, "logs")) && !Directory.Exists(Path.Combine(root, "data"))
                    && !Directory.Exists(Path.Combine(root, "images")) && !File.Exists(Path.Combine(root, "config.broken_20260101_120000.json")),
                "settings, logs, records removed (read-only file too)");
            T.Check(CleanUninstall.DataTargets(root).Count == 0, "nothing left to delete");
            T.Check(CleanUninstall.DeleteData(root).Count == 0, "second run is harmless");
            T.Check(CleanUninstall.DataTargets(Path.Combine(root, "missing")).Count == 0, "missing folder -> no targets");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* テスト用の一時フォルダー */ }
        }
    }

    public static void TestPatternMatching()
    {
        T.Check(CleanUninstall.MatchesPattern("config.broken_20260101_120000.json", "config.broken_*.json"), "broken backup matches");
        T.Check(!CleanUninstall.MatchesPattern("config.json.bak", "config.json"), "exact names stay exact");
        T.Check(CleanUninstall.MatchesPattern("CONFIG.JSON", "config.json"), "case-insensitive");
        T.Check(!CleanUninstall.MatchesPattern("myconfig.broken_1.json", "config.broken_*.json"), "anchored at the start");
    }
}

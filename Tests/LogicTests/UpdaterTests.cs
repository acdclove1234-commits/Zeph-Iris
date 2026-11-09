// ==================================================
// UpdaterTests.cs - アプリの中からの更新のロジックテスト
//   (GitHub の返事の読み方・版の比べ方・ダウンロードしたファイルの確かめ方・開いたまま実行できること・インストーラーとの引数)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using System.Security.Cryptography;
using ProFanController;

namespace LogicTests;

public static class UpdaterTests
{
    private const string Sha = "76ef203faec4e6d309c2ccadc9fe8f1a71fe03336682a7bb1861e5537a623995";

    private static string Release(string tag = "v6.7.1", string asset = "Zeph-Iris-6.7.1-setup.exe", bool draft = false, bool prerelease = false,
        string? digest = "sha256:" + Sha, string url = AppUpdater.DownloadPrefix + "v6.7.1/Zeph-Iris-6.7.1-setup.exe", long size = 73791128)
    {
        string digestPart = digest is null ? "" : $"\"digest\": \"{digest}\",";
        return $$"""
            { "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
              "published_at": "2026-11-10T03:00:00Z",
              "assets": [
                { "name": "notes.txt", "state": "uploaded", "size": 10, "browser_download_url": "{{AppUpdater.DownloadPrefix}}x/notes.txt" },
                { "name": "{{asset}}", "state": "uploaded", "size": {{size}}, {{digestPart}} "browser_download_url": "{{url}}" }
              ] }
            """;
    }

    public static void TestVersionsCompared()
    {
        T.Check(AppUpdater.ParseTag("v6.7.1") == new Version(6, 7, 1) && AppUpdater.ParseTag("6.10.0") == new Version(6, 10, 0), "tags read with or without v");
        T.Check(AppUpdater.ParseTag("latest") is null && AppUpdater.ParseTag("v6.7") is null && AppUpdater.ParseTag("") is null, "unreadable tags ignored");
        T.Check(AppUpdater.IsNewer(new Version(6, 7, 1), new Version(6, 7, 0)), "6.7.1 is newer than 6.7.0");
        T.Check(AppUpdater.IsNewer(new Version(6, 10, 0), new Version(6, 9, 9)), "6.10.0 is newer than 6.9.9 (numbers, not text)");
        T.Check(!AppUpdater.IsNewer(new Version(6, 7, 0), new Version(6, 7, 0, 5)) && !AppUpdater.IsNewer(new Version(6, 6, 1), new Version(6, 7, 0)),
            "same or older is not offered");
        var now = AppUpdater.CurrentVersion;
        // テストは Core のソースを自分の中に取り込んで動くので、版はテストのもの（アプリでは csproj の <Version>）
        T.Check(now.Build >= 0 && now.Revision == -1, $"current version has three parts ({now})");
    }

    public static void TestReleaseRead()
    {
        var info = AppUpdater.ParseRelease(Release());
        T.Check(info is not null && info.Version == new Version(6, 7, 1) && info.AssetName == "Zeph-Iris-6.7.1-setup.exe"
                && info.Size == 73791128 && info.Sha256 == Sha && info.PublishedAt is not null, "setup asset, size, SHA-256 and date read");
        T.Check(AppUpdater.ParseRelease(Release(draft: true)) is null, "drafts are not offered");
        T.Check(AppUpdater.ParseRelease(Release(prerelease: true)) is null, "pre-releases are not offered");
        T.Check(AppUpdater.ParseRelease(Release(asset: "ProFanController-6.7.1-windows-x64.zip")) is null, "no setup.exe → nothing offered");
        T.Check(AppUpdater.ParseRelease(Release(asset: "Zeph-Iris-6.7.0-setup.exe")) is null, "setup of another version than the tag → nothing offered");
        T.Check(AppUpdater.ParseRelease(Release(digest: null)) is null, "no SHA-256 from GitHub → not offered (cannot be checked)");
        T.Check(AppUpdater.ParseRelease(Release(digest: "sha256:1234")) is null, "broken SHA-256 → not offered");
        T.Check(AppUpdater.ParseRelease(Release(url: "https://example.com/Zeph-Iris-6.7.1-setup.exe")) is null, "download outside this repository → refused");
        T.Check(AppUpdater.ParseRelease(Release(size: 0)) is null, "no size → not offered");
    }

    public static void TestDownloadChecked()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"zephiris_upd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = new byte[200_000];
            new Random(5).NextBytes(data);
            string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            string path = Path.Combine(dir, "Zeph-Iris-6.7.1-setup.exe");
            File.WriteAllBytes(path, data);
            var good = new UpdateInfo(new Version(6, 7, 1), "v6.7.1", "Zeph-Iris-6.7.1-setup.exe", AppUpdater.DownloadPrefix + "x", data.Length, sha, null);

            using (var fs = File.OpenRead(path)) AppUpdater.Verify(fs, good);
            T.Check(true, "same size and SHA-256 → accepted");

            bool badHash = false;
            using (var fs = File.OpenRead(path))
                try { AppUpdater.Verify(fs, good with { Sha256 = Sha }); } catch (InvalidDataException) { badHash = true; }
            T.Check(badHash, "different SHA-256 → refused");

            bool badSize = false;
            using (var fs = File.OpenRead(path))
                try { AppUpdater.Verify(fs, good with { Size = data.Length + 1 }); } catch (InvalidDataException) { badSize = true; }
            T.Check(badSize, "different size → refused");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 一時フォルダー */ }
        }
    }

    /// <summary>確かめたファイルを書き換え・削除できないように開いたままでも、そのファイルを実行できる（すり替えを防ぐやり方が使える）</summary>
    public static void TestHeldFileStillRuns()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"zephiris_upd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string exe = Path.Combine(dir, "probe.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), exe);
            using var held = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
            bool writeBlocked = false, deleteBlocked = false;
            try { using var w = new FileStream(exe, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); } catch (IOException) { writeBlocked = true; }
            try { File.Delete(exe); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { deleteBlocked = true; }
            T.Check(writeBlocked && deleteBlocked && File.Exists(exe), "while held: cannot be rewritten or deleted");

            using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
            p.StandardOutput.ReadToEnd();
            bool exited = p.WaitForExit(10_000);
            T.Check(exited && p.ExitCode == 0, $"while held: it still runs (exit code {(exited ? p.ExitCode : -1)})");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 一時フォルダー */ }
        }
    }

    public static void TestInstallerHandOff()
    {
        T.Check(AppUpdater.InstallerArguments(false) == "/SILENT /SUPPRESSMSGBOXES /NORESTART", "silent install, no screens");
        T.Check(AppUpdater.InstallerArguments(true).EndsWith(" " + AppUpdater.ShowAppArgument), "window was open → asks to show the app again");
        T.Check(AppUpdater.ReleasesPage.EndsWith("/" + AppIdentity.Name + "/releases") && AppUpdater.DownloadPrefix.StartsWith("https://github.com/"),
            "points at this app's GitHub releases");

        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ProFanController.csproj"))) d = d.Parent;
        T.Check(d is not null, "repository found");
        if (d is null) return;
        string iss = File.ReadAllText(Path.Combine(d.FullName, "Installer", "Zeph-Iris.iss"));
        T.Check(iss.Contains(AppUpdater.ShowAppArgument, StringComparison.Ordinal) && iss.Contains("{code:RestartArguments}", StringComparison.Ordinal),
            "the installer reads the show-app switch and restarts the app");
        // Zeph-Iris.exe は管理者権限が必要。postinstall の項目は既定でダブルクリックした元の（管理者でない）ユーザーで
        // CreateProcess されてエラー 740 になる（2026-11 に実際に起きた）。起動する行は、どれもセットアップと同じ権限で
        var runLines = iss.Split('\n').SkipWhile(l => !l.StartsWith("[Run]")).Skip(1).TakeWhile(l => !l.StartsWith("["))
            .Where(l => l.StartsWith("Filename:")).ToList();
        T.Check(runLines.Count == 2 && runLines.All(l => l.Contains("runascurrentuser")),
            $"every app launch after install runs with the installer's admin rights ({runLines.Count} lines)");

        var cfg = new AppConfig();
        T.Check(cfg.Update.AutoCheck, "automatic check is on by default");
        cfg.Update.LastRunVersion = "6.7.0";
        cfg.Update.LastChecked = new DateTime(2026, 11, 9, 20, 0, 0);
        var back = AppConfig.FromJson(cfg.ToJson());
        T.Check(back.Update.LastRunVersion == "6.7.0" && back.Update.LastChecked == cfg.Update.LastChecked, "update state saved in config.json");
        var old = AppConfig.FromJson("{ }");
        T.Check(old.Update is not null && old.Update.AutoCheck, "settings from before this feature → defaults");

        // インストーラーが PawnIO を入れるときの引数は、アプリが入れるときと同じ（取得元・画面なし・同意）
        T.Check(iss.Contains(PawnIoSetup.InstallArguments, StringComparison.Ordinal), "installer installs PawnIO with the app's own winget arguments");
        T.Check(iss.Contains("ssPostInstall") && iss.Contains("InstallPawnIo()"), "PawnIO is installed after the files are in place");
    }

    public static void TestRollBackOffered()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0);
        var cur = new Version(6, 7, 3);
        var cfg = new UpdateConfig { PreviousVersion = "6.7.2", UpdatedAt = now.AddDays(-1) };
        T.Check(AppUpdater.CanRollBack(cfg, cur, now), "updated yesterday from 6.7.2 → can go back");
        T.Check(!AppUpdater.CanRollBack(with_updated(now.AddDays(-15)), cur, now), "more than 14 days ago → not offered");
        T.Check(!AppUpdater.CanRollBack(new UpdateConfig { PreviousVersion = null, UpdatedAt = now }, cur, now), "previous version unknown → not offered");
        T.Check(!AppUpdater.CanRollBack(new UpdateConfig { PreviousVersion = "6.7.4", UpdatedAt = now }, cur, now), "previous is newer (already went back) → not offered");
        T.Check(AppUpdater.ReleaseByTagApi(new Version(6, 7, 2)).EndsWith("/releases/tags/v6.7.2"), "the previous version's release is looked up by its tag");

        var app = new AppConfig();
        T.Check(Watchdog.RollbackHint(app, now, cur) == "", "watchdog notice: no hint when not just updated");
        app.Update.PreviousVersion = "6.7.2";
        app.Update.UpdatedAt = now.AddHours(-2);
        string hint = Watchdog.RollbackHint(app, now, cur);
        T.Check(hint.Contains("6.7.2") && hint.Contains(AppUpdater.ReleasesPage), $"watchdog notice: says how to go back when just updated");

        static UpdateConfig with_updated(DateTime at) => new() { PreviousVersion = "6.7.2", UpdatedAt = at };
    }

    public static void TestOldDownloadsCleanedUp()
    {
        string root = Path.Combine(Path.GetTempPath(), $"zephiris_clean_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string a = Path.Combine(root, AppUpdater.DownloadFolderPrefix + "aaa"), b = Path.Combine(root, AppUpdater.DownloadFolderPrefix + "bbb");
            string other = Path.Combine(root, "SomethingElse");
            foreach (var d in new[] { a, b, other }) Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(a, "Zeph-Iris-6.7.2-setup.exe"), "x");
            string held = Path.Combine(b, "Zeph-Iris-6.7.3-setup.exe");
            int removed;
            using (new FileStream(held, FileMode.Create, FileAccess.Write, FileShare.None))
                removed = AppUpdater.CleanUpDownloads(root);
            T.Check(removed == 1 && !Directory.Exists(a) && Directory.Exists(other), "old downloads removed, other folders kept");
            T.Check(Directory.Exists(b), "a download still in use is left for the next start");
            T.Check(AppUpdater.CleanUpDownloads(root) == 1 && !Directory.Exists(b), "removed on the next try once free");
            // 前の名前の版（Fanlume）が残したもの
            string old = Path.Combine(root, "Fanlume-update-ccc");
            Directory.CreateDirectory(old);
            T.Check(AppUpdater.CleanUpDownloads(root) == 1 && !Directory.Exists(old), "downloads left by the old name (Fanlume) are removed too");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 一時フォルダー */ }
        }
    }
}

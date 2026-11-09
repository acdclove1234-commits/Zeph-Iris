// ==================================================
// InstallCheck.cs - 起動したときの確認 (ほかのメーカーの LED・ファンのソフトが動いていないか・
//                   置き場所が変わりやすいフォルダーでないか・自動起動がこの場所を指しているか)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;

namespace ProFanController;

/// <summary>LED やファンを取り合うソフト（プロセス名の一部 → 表示名）</summary>
public sealed record ConflictingApp(string Process, string Label, bool Exact = false);

public static class InstallCheck
{
    /// <summary>
    /// 同じ LED・ファンを操作するメーカーのソフト。動いていると、色や回転数をこのアプリと取り合って、
    /// 色が勝手に戻る・ちらつく・回転数が反映されない、などが起きる（どちらか最後に書いたほうが勝つため）
    /// </summary>
    public static IReadOnlyList<ConflictingApp> KnownConflicts => _knownConflicts.Value;
    private static readonly PerLanguage<IReadOnlyList<ConflictingApp>> _knownConflicts = new(() => new ConflictingApp[]
    {
        new("LightingService", L("ASUS Aura（Armoury Crate の LED）")),
        new("ArmouryCrate", "ASUS Armoury Crate"),
        new("LEDKeeper2", "MSI Mystic Light"),
        new("MSI.CentralServer", "MSI Center"),
        new("MysticLight", "MSI Mystic Light"),
        new("RGBFusion", "GIGABYTE RGB Fusion"),
        new("Polychrome", "ASRock Polychrome"),
        new("iCUE", "Corsair iCUE"),
        new("L-Connect", "Lian Li L-Connect"),
        new("NZXT CAM", "NZXT CAM"),
        new("SignalRgb", "SignalRGB"),
        new("FanControl", "FanControl", Exact: true),
        new("SpeedFan", "SpeedFan", Exact: true),
        new("ArgusMonitor", "Argus Monitor"),
        // 6.6.1 までの名前のこのアプリ。新しい版と一緒に動くと、同じファンを 2 つのアプリが制御してしまう
        new(AppIdentity.LegacyName, L("ProFanController（前の版）"), Exact: true),
    });

    /// <summary>プロセス名の一覧から、取り合いになるソフトを探す（同じソフトは 1 回だけ。このアプリ自身は除く）</summary>
    public static List<string> FindConflicts(IEnumerable<string> processNames)
    {
        var found = new List<string>();
        foreach (var name in processNames)
        {
            if (string.IsNullOrEmpty(name) || name.Contains(AppIdentity.Name, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var app in KnownConflicts)
            {
                bool hit = app.Exact
                    ? name.Equals(app.Process, StringComparison.OrdinalIgnoreCase)
                    : name.Contains(app.Process, StringComparison.OrdinalIgnoreCase);
                if (hit && !found.Contains(app.Label)) found.Add(app.Label);
            }
        }
        return found;
    }

    /// <summary>いま動いている、取り合いになるソフト</summary>
    public static List<string> FindRunningConflicts()
    {
        var names = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try { names.Add(p.ProcessName); }
            catch (InvalidOperationException) { /* 終わったプロセス */ }
            finally { p.Dispose(); }
        }
        return FindConflicts(names);
    }

    /// <summary>
    /// 置き場所が変わりやすいフォルダー（ダウンロード・デスクトップ・一時フォルダー）か。
    /// ここに置いたままだと、整理したときに消える・別の版が増える・自動起動が古い場所を指したままになる
    /// </summary>
    public static string? UnstableFolderName(string exeDirectory)
    {
        string dir = Path.GetFullPath(exeDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var places = new (string Path, string Name)[]
        {
            (Path.Combine(user, "Downloads"), L("ダウンロード")),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), L("デスクトップ")),
            (Path.GetTempPath(), L("一時フォルダー")),
        };
        foreach (var (path, name) in places)
        {
            if (string.IsNullOrEmpty(path)) continue;
            string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (dir.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return name;
        }
        return null;
    }

    /// <summary>
    /// 移すときのおすすめの場所（ダウンロードなどから起動したときは、ここへ自動で入れる）。
    /// インストーラーが入れる場所（C:\Program Files\Fanlume）と同じにして、置き場所が 1 つにまとまるようにする
    /// </summary>
    public static string RecommendedFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { Length: > 0 } pf ? pf : @"C:\Program Files", AppIdentity.Name);

    /// <summary>
    /// 写すアプリのファイル（exe と、インストーラーが入れる説明書・バッチ・ライセンス）。
    /// これ以外は写さない（exe をダウンロードフォルダーにそのまま置いたとき、ダウンロードの中身を丸ごと写さないため）
    /// </summary>
    private static readonly string[] AppFiles =
        { AppIdentity.ExeName, "README_en.txt", "はじめにお読みください.txt", "1_PawnIO をインストール.bat" }; // no-loc
    private static readonly string[] AppFolders = { "licenses" };
    /// <summary>設定と記録（設定を引き継ぐときだけ写す。ログは書き込み中で写せないことがあり、写した先で新しく作られる）</summary>
    private static readonly string[] DataFolders = { "data", "backups", "records", "reports", "images" };
    private const string ConfigFile = "config.json";

    /// <summary>
    /// いまのフォルダーのアプリ（exe・配布物）と設定・記録を、移す先へ写す（設定画面の「移す」ボタン）。
    /// 移す先に設定（config.json）があれば、上書きする前に移す先の backups に控えを残す。使用中で写せないファイルは飛ばす（ログに残す）。
    /// 移す先がいまのフォルダーと同じなら何もしない。写した先の exe の場所を返す
    /// </summary>
    public static string CopyInstall(string fromDirectory, string toDirectory)
    {
        string from = Path.GetFullPath(fromDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string to = Path.GetFullPath(toDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string exe = Path.Combine(to, AppIdentity.ExeName);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return exe;
        Directory.CreateDirectory(to);
        BackUpConfig(to, "before-move");
        CopyApp(from, to);
        CopyData(from, to);
        if (!File.Exists(exe)) throw new IOException(L("Fanlume.exe を写せませんでした"));
        return exe;
    }

    /// <summary>自動で入れた結果。CopiedApp = exe を写した（入れる先のほうが新しい版なら写さない）、MovedSettings = 設定と記録も写した</summary>
    public sealed record InstallResult(string Exe, bool CopiedApp, bool MovedSettings);

    /// <summary>
    /// ダウンロード・デスクトップ・一時フォルダー（zip の中から開いたとき）で起動したとき: アプリを toDirectory に入れる。
    ///   exe と配布物: 入れる先の exe のほうが新しい版なら写さない（古い版で上書きしない）
    ///   設定と記録: いまのフォルダーに設定があり、入れる先に設定がない（または、いまのほうが新しい）ときだけ写す
    ///   （入れる先の設定は backups に控える）。入れる先の exe の場所を返す
    /// </summary>
    public static InstallResult InstallFrom(string fromDirectory, string toDirectory, Version? runningVersion)
    {
        string from = Path.GetFullPath(fromDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string to = Path.GetFullPath(toDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string exe = Path.Combine(to, AppIdentity.ExeName);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return new InstallResult(exe, false, false);
        Directory.CreateDirectory(to);

        bool copyApp = !(File.Exists(exe) && runningVersion is not null && FileVersion(exe) is { } installed && installed > runningVersion);
        if (copyApp) CopyApp(from, to);

        string fromConfig = Path.Combine(from, ConfigFile), toConfig = Path.Combine(to, ConfigFile);
        bool moveSettings = File.Exists(fromConfig)
                            && (!File.Exists(toConfig) || File.GetLastWriteTimeUtc(fromConfig) > File.GetLastWriteTimeUtc(toConfig));
        if (moveSettings)
        {
            BackUpConfig(to, "before-install");
            CopyData(from, to);
        }
        if (!File.Exists(exe)) throw new IOException(L("Fanlume.exe を写せませんでした"));
        return new InstallResult(exe, copyApp, moveSettings);
    }

    /// <summary>設定と記録を、別のフォルダーへ写す（写す先の設定は、上書きする前に backups に控える。前の版から引き継ぐとき）</summary>
    public static void CopySettings(string fromDirectory, string toDirectory)
    {
        string to = Path.GetFullPath(toDirectory).TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(to);
        BackUpConfig(to, "before-migration");
        CopyData(Path.GetFullPath(fromDirectory).TrimEnd(Path.DirectorySeparatorChar), to);
    }

    /// <summary>exe の版（読めなければ null）</summary>
    public static Version? FileVersion(string exe)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, Math.Max(0, info.FilePrivatePart));
        }
        catch (Exception ex)
        {
            Logger.Warn($"exe の版を読めません: {exe}: {ex.Message}");
            return null;
        }
    }

    private static void BackUpConfig(string to, string why)
    {
        string targetConfig = Path.Combine(to, ConfigFile);
        if (!File.Exists(targetConfig)) return;
        string backups = Path.Combine(to, "backups");
        Directory.CreateDirectory(backups);
        File.Copy(targetConfig, Path.Combine(backups, $"config_{DateTime.Now:yyyyMMdd_HHmmss}_{why}.json"), overwrite: true);
    }

    private static void CopyApp(string from, string to)
    {
        foreach (var name in AppFiles) CopyFile(Path.Combine(from, name), Path.Combine(to, name));
        foreach (var dir in AppFolders) CopyTree(Path.Combine(from, dir), Path.Combine(to, dir));
    }

    private static void CopyData(string from, string to)
    {
        CopyFile(Path.Combine(from, ConfigFile), Path.Combine(to, ConfigFile));
        foreach (var dir in DataFolders) CopyTree(Path.Combine(from, dir), Path.Combine(to, dir));
    }

    private static void CopyFile(string from, string to)
    {
        if (!File.Exists(from)) return;
        try { File.Copy(from, to, overwrite: true); }
        catch (IOException ex) { Logger.Warn($"移すときに写せなかったファイル: {from}: {ex.Message}"); }
    }

    private static void CopyTree(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) CopyFile(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    /// <summary>自動起動のタスクの XML から、起動する exe の場所を取り出す（なければ null）</summary>
    public static string? CommandFromTaskXml(string xml)
    {
        int start = xml.IndexOf("<Command>", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += "<Command>".Length;
        int end = xml.IndexOf("</Command>", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;
        string cmd = System.Net.WebUtility.HtmlDecode(xml[start..end]).Trim().Trim('"');
        return cmd.Length > 0 ? cmd : null;
    }

    /// <summary>自動起動が、いま動いている exe とは別の場所を指しているか</summary>
    public static bool PointsElsewhere(string? registered, string current) =>
        registered is not null
        && !string.Equals(Path.GetFullPath(registered), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase);
}

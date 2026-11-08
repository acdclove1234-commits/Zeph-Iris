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
    });

    /// <summary>プロセス名の一覧から、取り合いになるソフトを探す（同じソフトは 1 回だけ。このアプリ自身は除く）</summary>
    public static List<string> FindConflicts(IEnumerable<string> processNames)
    {
        var found = new List<string>();
        foreach (var name in processNames)
        {
            if (string.IsNullOrEmpty(name) || name.Contains("ProFanController", StringComparison.OrdinalIgnoreCase)) continue;
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

    /// <summary>移すときのおすすめの場所</summary>
    public const string RecommendedFolder = @"C:\Tools\ProFanController";

    /// <summary>写さないフォルダー（ログは書き込み中で写せないことがあり、移した先で新しく作られる）</summary>
    private static readonly string[] SkipFolders = { "logs" };

    /// <summary>
    /// いまのフォルダーの中身（exe・設定・記録）を、移す先へ写す。移す先に設定（config.json）があれば、
    /// 上書きする前に移す先の backups に控えを残す。使用中で写せないファイルは飛ばす（ログに残す）。
    /// 移す先がいまのフォルダーと同じなら何もしない。写した先の exe の場所を返す
    /// </summary>
    public static string CopyInstall(string fromDirectory, string toDirectory)
    {
        string from = Path.GetFullPath(fromDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string to = Path.GetFullPath(toDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string exe = Path.Combine(to, "ProFanController.exe");
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return exe;
        Directory.CreateDirectory(to);
        string targetConfig = Path.Combine(to, "config.json");
        if (File.Exists(targetConfig))
        {
            string backups = Path.Combine(to, "backups");
            Directory.CreateDirectory(backups);
            File.Copy(targetConfig, Path.Combine(backups, $"config_{DateTime.Now:yyyyMMdd_HHmmss}_before-move.json"), overwrite: true);
        }
        CopyTree(from, to, top: true);
        if (!File.Exists(exe)) throw new IOException(L("ProFanController.exe を写せませんでした"));
        return exe;
    }

    private static void CopyTree(string from, string to, bool top)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            try { File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true); }
            catch (IOException ex) { Logger.Warn($"移すときに写せなかったファイル: {file}: {ex.Message}"); }
        }
        foreach (var dir in Directory.GetDirectories(from))
        {
            if (top && SkipFolders.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)) continue;
            CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)), top: false);
        }
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

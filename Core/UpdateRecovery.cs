// ==================================================
// UpdateRecovery.cs - 更新した版が起動しなかったときに、アプリの外から前の版へ戻す
//   1. 更新の前に、いまの exe と設定を「previous」フォルダーへ写す（exe は ZephIrisRecovery.exe という名前で）
//   2. その ZephIrisRecovery.exe を「見守り」として起動してから、インストーラーを動かす
//      （名前が違うので、インストーラーが待つ Zeph-Iris.exe のプロセスには数えられない）
//   3. 新しい版は、ファンの制御を始めたら previous\ok.txt に自分の版を書く
//   4. 見守りは、決まった時間のうちに ok.txt が来なければ、新しい版を終わらせて、前の exe と設定に戻し、起動し直す
//   ZephIrisRecovery.exe をダブルクリックすると、確かめてから手で前の版に戻せる（アプリが起動しないとき用）
// Dependencies: .NET 8 / System.Text.Json (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ProFanController;

/// <summary>更新の途中の記録（previous\update.json）</summary>
public sealed record UpdatePending(string From, string To, DateTime Started);

/// <summary>見守りが前の版に戻した記録（previous\rolledback.json。戻した版が起動したときに知らせて消す）</summary>
public sealed record UpdateRolledBack(string Failed, string Restored, DateTime At, string Reason);

public enum GuardStep
{
    /// <summary>まだ待つ</summary>
    Wait,
    /// <summary>新しい版が制御を始めた（見守りは終わる）</summary>
    Done,
    /// <summary>新しい版が入ったのに、時間内に制御を始めなかった → 前の版に戻す</summary>
    Restore,
    /// <summary>入れ替わらなかった（インストールの失敗・取り消し）のに、アプリが動いていない → 前の版をそのまま起動する</summary>
    StartOld,
    /// <summary>入れ替わらず、前の版が動いている（見守りは何もしないで終わる）</summary>
    Nothing,
}

public static class UpdateRecovery
{
    public const string Folder = "previous";
    public const string RecoveryExe = "ZephIrisRecovery.exe";
    public const string GuardArgument = "--update-guard";
    public const string RestoreArgument = "--restore-previous";
    public const string PendingFile = "update.json";
    public const string OkFile = "ok.txt";
    public const string RolledBackFile = "rolledback.json";
    public const string ConfigFile = "config.json";

    /// <summary>新しい版がファンの制御を始めるのを待つ時間（インストール・起動・ハードウェアの読み込みを含む）</summary>
    public static readonly TimeSpan GuardTimeout = TimeSpan.FromMinutes(8);
    /// <summary>入れ替わらなかったと決めるまでの時間（インストーラーがアプリを閉じて入れ終わるまで）</summary>
    public static readonly TimeSpan NoInstallTimeout = TimeSpan.FromMinutes(4);
    /// <summary>インストーラーが動いているあいだ待つ、いちばん長い時間</summary>
    public static readonly TimeSpan InstallerMaxWait = TimeSpan.FromMinutes(20);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string BackupDir(string appDir) => Path.Combine(appDir, Folder);
    public static string RecoveryPath(string appDir) => Path.Combine(BackupDir(appDir), RecoveryExe);
    public static string AppExe(string appDir) => Path.Combine(appDir, AppIdentity.ExeName);

    /// <summary>
    /// 更新の前: いまの exe と設定を previous フォルダーに写し、更新の途中の記録を書く（前の ok.txt・戻した記録は消す）。
    /// 写した見守り用の exe の場所を返す
    /// </summary>
    public static string Prepare(string appDir, string currentExe, Version from, Version to, DateTime now)
    {
        string dir = BackupDir(appDir);
        Directory.CreateDirectory(dir);
        string recovery = Path.Combine(dir, RecoveryExe);
        File.Copy(currentExe, recovery, overwrite: true);
        string config = Path.Combine(appDir, ConfigFile);
        if (File.Exists(config)) File.Copy(config, Path.Combine(dir, ConfigFile), overwrite: true);
        TryDelete(Path.Combine(dir, OkFile));
        TryDelete(Path.Combine(dir, RolledBackFile));
        Write(Path.Combine(dir, PendingFile), new UpdatePending(from.ToString(3), to.ToString(3), now));
        return recovery;
    }

    public static UpdatePending? ReadPending(string appDir) => Read<UpdatePending>(Path.Combine(BackupDir(appDir), PendingFile));

    /// <summary>新しい版が制御を始めたとき: 待っている更新の版がいまの版なら、ok.txt を書く（何度呼んでもよい）</summary>
    public static bool MarkStarted(string appDir, Version current)
    {
        try
        {
            if (ReadPending(appDir) is not { } p || p.To != current.ToString(3)) return false;
            string ok = Path.Combine(BackupDir(appDir), OkFile);
            if (File.Exists(ok) && File.ReadAllText(ok).Trim() == p.To) return false;
            File.WriteAllText(ok, p.To, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsMarkedOk(string appDir, UpdatePending p)
    {
        string ok = Path.Combine(BackupDir(appDir), OkFile);
        try { return File.Exists(ok) && File.ReadAllText(ok).Trim() == p.To; }
        catch (IOException) { return false; }
    }

    /// <summary>見守りが次にすること</summary>
    /// <param name="installed">いまアプリのフォルダーにある exe の版（読めなければ null）</param>
    /// <param name="installerRunning">インストーラーがまだ動いている（動いているあいだは、入れ終わるまで待つ。最長 InstallerMaxWait）</param>
    public static GuardStep Decide(UpdatePending p, Version? installed, bool okMarked, bool appRunning, TimeSpan elapsed, bool installerRunning = false)
    {
        if (okMarked) return GuardStep.Done;
        if (installerRunning && elapsed < InstallerMaxWait) return GuardStep.Wait;
        string? v = installed?.ToString(3);
        if (v == p.To) return elapsed >= GuardTimeout ? GuardStep.Restore : GuardStep.Wait;
        if (v == p.From && elapsed >= NoInstallTimeout) return appRunning ? GuardStep.Nothing : GuardStep.StartOld;
        // 入れ替えの途中（exe が読めない）・別の版: 時間いっぱいまで待ち、だめなら戻す
        return elapsed >= GuardTimeout ? GuardStep.Restore : GuardStep.Wait;
    }

    /// <summary>previous フォルダーに前の版があるか（その版。なければ null）</summary>
    public static Version? BackupVersion(string appDir)
    {
        string exe = RecoveryPath(appDir);
        return File.Exists(exe) ? InstallCheck.FileVersion(exe) : null;
    }

    /// <summary>
    /// 前の版に戻す: previous の exe をアプリの exe に、previous の設定を設定に写し、戻した記録を書く。
    /// アプリ（新しい版）は終わらせてから呼ぶこと。うまくいけば null、だめなら理由
    /// </summary>
    public static string? Restore(string appDir, string reason, DateTime now)
    {
        string dir = BackupDir(appDir);
        string recovery = Path.Combine(dir, RecoveryExe);
        if (!File.Exists(recovery)) return L("前の版の控え（previous フォルダー）が見つかりません。");
        string failed = InstallCheck.FileVersion(AppExe(appDir))?.ToString(3) ?? "?";
        string restored = InstallCheck.FileVersion(recovery)?.ToString(3) ?? "?";
        Exception? last = null;
        for (int i = 0; i < 10; i++)
        {
            try
            {
                File.Copy(recovery, AppExe(appDir), overwrite: true);
                last = null;
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(1000);   // 終わったばかりのプロセスが、まだ exe を開いていることがある
            }
        }
        if (last is not null) return L($"前の版の exe を戻せませんでした（{last.Message}）。");
        string config = Path.Combine(dir, ConfigFile);
        try
        {
            if (File.Exists(config)) File.Copy(config, Path.Combine(appDir, ConfigFile), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return L($"前の版には戻しましたが、設定を戻せませんでした（{ex.Message}）。");
        }
        Write(Path.Combine(dir, RolledBackFile), new UpdateRolledBack(failed, restored, now, reason));
        TryDelete(Path.Combine(dir, PendingFile));
        return null;
    }

    /// <summary>戻した版が起動したとき: 戻した記録を読んで消す（なければ null）</summary>
    public static UpdateRolledBack? TakeRolledBack(string appDir)
    {
        string path = Path.Combine(BackupDir(appDir), RolledBackFile);
        var r = Read<UpdateRolledBack>(path);
        if (r is not null) TryDelete(path);
        return r;
    }

    /// <summary>見守りが終わるとき: 更新の途中の記録を消す（ok.txt と控えの exe・設定は、次の更新まで残す）</summary>
    public static void Finish(string appDir) => TryDelete(Path.Combine(BackupDir(appDir), PendingFile));

    private static void Write<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));

    private static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // =====================================================================
    // 見守り・手で戻す（ZephIrisRecovery.exe として、画面の部品を読み込まずに動く）
    // =====================================================================

    /// <summary>
    /// 見守り: 5 秒ごとに様子を見て、Decide のとおりにする。log には経過を書く。
    /// ask は、新しい版を終わらせられなかったときに案内を出す（見守りは勝手にプロセスを強制終了しない）
    /// </summary>
    public static int RunGuard(string appDir, Action<string> log, Action<string> tell)
    {
        if (ReadPending(appDir) is not { } p)
        {
            log("更新の途中の記録がないので、見守りを終わります");   // no-loc（ログだけ）
            return 0;
        }
        log($"更新の見守りを始めます: {p.From} → {p.To}");   // no-loc（ログだけ）
        var sw = Stopwatch.StartNew();
        while (true)
        {
            Thread.Sleep(5000);
            var step = Decide(p, InstallCheck.FileVersion(AppExe(appDir)), IsMarkedOk(appDir, p), AppRunning(), sw.Elapsed, InstallerRunning());
            switch (step)
            {
                case GuardStep.Wait:
                    continue;
                case GuardStep.Done:
                    log($"新しい版 {p.To} がファンの制御を始めました。見守りを終わります");   // no-loc（ログだけ）
                    Finish(appDir);
                    return 0;
                case GuardStep.Nothing:
                    log("入れ替わりませんでしたが、前の版が動いているので、何もしません");   // no-loc（ログだけ）
                    Finish(appDir);
                    return 0;
                case GuardStep.StartOld:
                    log("入れ替わらず、アプリも動いていないので、前の版をそのまま起動します");   // no-loc（ログだけ）
                    Finish(appDir);
                    StartApp(appDir, log);
                    return 0;
                case GuardStep.Restore:
                    log($"新しい版 {p.To} が {GuardTimeout.TotalMinutes:F0} 分のうちにファンの制御を始めなかったので、{p.From} に戻します");   // no-loc（ログだけ）
                    return RestoreAndStart(appDir, L($"新しい版 {p.To} が、{GuardTimeout.TotalMinutes:F0} 分のうちにファンの制御を始めませんでした。"), log, tell);
            }
        }
    }

    /// <summary>アプリを終わらせて、前の版に戻し、起動し直す</summary>
    public static int RestoreAndStart(string appDir, string reason, Action<string> log, Action<string> tell)
    {
        if (!StopApp(TimeSpan.FromSeconds(60)))
        {
            log("アプリを終わらせられないので、前の版に戻せません");   // no-loc（ログだけ）
            tell(L($"Zeph-Iris を終わらせられないため、前の版に戻せませんでした。タスクトレイの Zeph-Iris を終了する（または PC を再起動する）と、ファンは BIOS の設定に戻ります。そのあと、次のファイルをダブルクリックすると前の版に戻せます。\n{RecoveryPath(appDir)}"));
            return 1;
        }
        if (Restore(appDir, reason, DateTime.Now) is { } problem)
        {
            log("前の版に戻せませんでした: " + problem);   // no-loc（ログだけ）
            tell(problem);
            return 1;
        }
        log("前の版の exe と設定に戻しました。起動します");   // no-loc（ログだけ）
        StartApp(appDir, log);
        return 0;
    }

    /// <summary>Zeph-Iris が動いているか（二重起動を防ぐ印で調べる）</summary>
    public static bool AppRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(AppIdentity.SingleInstance, out var m))
            {
                m.Dispose();
                return true;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return true;   // 印はあるが開けない = 動いている
        }
        return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppIdentity.ExeName)).Length > 0;
    }

    /// <summary>Zeph-Iris のインストーラー（Zeph-Iris-<版>-setup.exe と、それが一時フォルダーに出す .tmp）が動いているか</summary>
    public static bool InstallerRunning()
    {
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string n = p.ProcessName;
                if (n.StartsWith(AppIdentity.Name + "-", StringComparison.OrdinalIgnoreCase) && n.Contains("setup", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// <summary>動いているアプリに「終了して」の合図を送り（ファンは BIOS に戻る）、見張りも含めて終わるのを待つ</summary>
    public static bool StopApp(TimeSpan wait)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(AppIdentity.ExitSignal, out var e))
            {
                e.Set();
                e.Dispose();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { }
        var sw = Stopwatch.StartNew();
        string name = Path.GetFileNameWithoutExtension(AppIdentity.ExeName);
        while (sw.Elapsed < wait)
        {
            var ps = Process.GetProcessesByName(name);
            foreach (var p in ps) p.Dispose();
            if (ps.Length == 0) return true;
            Thread.Sleep(500);
        }
        return false;
    }

    /// <summary>アプリを起動する（自動起動のタスクがあればそれで。なければ exe を直接）</summary>
    public static void StartApp(string appDir, Action<string> log)
    {
        try
        {
            if (AutoStart.IsEnabled() && AutoStart.RunTask())
            {
                log("自動起動のタスクで起動しました");   // no-loc（ログだけ）
                return;
            }
            Process.Start(new ProcessStartInfo(AppExe(appDir), "--minimized --restart") { UseShellExecute = false, WorkingDirectory = appDir })?.Dispose();
            log("exe を起動しました");   // no-loc（ログだけ）
        }
        catch (Exception ex)
        {
            log("起動できませんでした: " + ex.Message);   // no-loc（ログだけ）
        }
    }
}

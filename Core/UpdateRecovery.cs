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
/// <param name="Manual">手で戻した（自動で戻したのではない）</param>
/// <param name="SettingsBackup">戻す直前の設定を取っておいた場所（取っておけなければ null）</param>
public sealed record UpdateRolledBack(string Failed, string Restored, DateTime At, string Reason, bool Manual = false, string? SettingsBackup = null);

/// <summary>前の版に戻した結果</summary>
/// <param name="ExeRestored">前の版の exe に戻せた（設定が戻せなくても、前の版は起動できる）</param>
/// <param name="Problem">うまくいかなかった理由（うまくいけば null）</param>
public sealed record RestoreResult(bool ExeRestored, string? Problem);

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
    /// <summary>新しい版が入ってから、戻すと決めるまでに少なくとも待つ時間（インストールが長引いて、待つ時間が足りなくならないように）</summary>
    public static readonly TimeSpan MinNewVersionWait = TimeSpan.FromMinutes(4);
    /// <summary>新しい版が「ちゃんと動いている」と言えるまで、続けて制御していてほしい時間（MainWindow → UpdateConfirm）</summary>
    public static readonly TimeSpan ConfirmAfter = TimeSpan.FromSeconds(90);
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
        // 前の更新の残り（設定・印）を先に消す。消せなければ例外にする（新しい exe と古い設定の組み合わせを残さないため）
        foreach (var name in new[] { PendingFile, OkFile, RolledBackFile, ConfigFile })
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) File.Delete(path);
        }
        // 設定 → exe の順に写す（途中で失敗しても、前の版の exe と別の時の設定が組になって残らない）
        string config = Path.Combine(appDir, ConfigFile);
        if (File.Exists(config)) File.Copy(config, Path.Combine(dir, ConfigFile), overwrite: true);
        string recovery = Path.Combine(dir, RecoveryExe);
        File.Copy(currentExe, recovery, overwrite: true);
        Write(Path.Combine(dir, PendingFile), new UpdatePending(from.ToString(3), to.ToString(3), now));
        return recovery;
    }

    public static UpdatePending? ReadPending(string appDir) => Read<UpdatePending>(Path.Combine(BackupDir(appDir), PendingFile));

    /// <summary>
    /// 新しい版がちゃんと動いているとき: 待っている更新の版がいまの版なら、ok.txt を書く（何度呼んでもよい）。
    /// true = 書いた・もう書いてある・待っている更新がない（もう書かなくてよい）。false = 書けなかった（あとでもう一度）
    /// </summary>
    public static bool MarkStarted(string appDir, Version current)
    {
        try
        {
            if (ReadPending(appDir) is not { } p || p.To != current.ToString(3)) return true;
            string ok = Path.Combine(BackupDir(appDir), OkFile);
            if (File.Exists(ok) && File.ReadAllText(ok).Trim() == p.To) return true;
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
    /// <param name="sinceNewVersion">新しい版の exe を初めて見てからの時間（分からなければ null）。戻すのは、これが MinNewVersionWait を過ぎてから</param>
    public static GuardStep Decide(UpdatePending p, Version? installed, bool okMarked, bool appRunning, TimeSpan elapsed, bool installerRunning = false,
                                   TimeSpan? sinceNewVersion = null)
    {
        if (okMarked) return GuardStep.Done;
        if (installerRunning && elapsed < InstallerMaxWait) return GuardStep.Wait;
        string? v = installed?.ToString(3);
        if (v == p.To)
            return elapsed >= GuardTimeout && (sinceNewVersion is not { } since || since >= MinNewVersionWait) ? GuardStep.Restore : GuardStep.Wait;
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
    /// 設定を写す前に、いまの設定を backups フォルダーに取っておく（新しい版で変えた設定を失わないように）。
    /// アプリ（新しい版）は終わらせてから呼ぶこと
    /// </summary>
    public static RestoreResult Restore(string appDir, string reason, DateTime now, bool manual = false)
    {
        string dir = BackupDir(appDir);
        string recovery = Path.Combine(dir, RecoveryExe);
        if (!File.Exists(recovery)) return new RestoreResult(false, L("前の版の控え（previous フォルダー）が見つかりません。"));
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
        if (last is not null) return new RestoreResult(false, L($"前の版の exe を戻せませんでした（{last.Message}）。"));
        string config = Path.Combine(dir, ConfigFile);
        string live = Path.Combine(appDir, ConfigFile);
        string? kept = null;
        string? problem = null;
        try
        {
            if (File.Exists(config))
            {
                // いまの設定を取っておけたときだけ、更新する前の設定に戻す（取っておけなければ、いまの設定のまま前の版を動かす。
                // 設定の読み込みは、新しい版で増えた項目があっても読める）
                kept = KeepCurrentSettings(appDir, now);
                if (kept is not null || !File.Exists(live)) File.Copy(config, live, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = L($"前の版には戻しましたが、設定を戻せませんでした（{ex.Message}）。");
        }
        try { Write(Path.Combine(dir, RolledBackFile), new UpdateRolledBack(failed, restored, now, reason, manual, kept)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        TryDelete(Path.Combine(dir, PendingFile));
        return new RestoreResult(true, problem);
    }

    /// <summary>
    /// いまの設定を backups\config_日時_restore.json に取っておく（設定画面の「バックアップから戻す」に「復元前」として出る）。
    /// 取っておけたらその場所、設定がない・書けなければ null
    /// </summary>
    internal static string? KeepCurrentSettings(string appDir, DateTime now)
    {
        try
        {
            string live = Path.Combine(appDir, ConfigFile);
            if (!File.Exists(live)) return null;
            string backups = Path.Combine(appDir, "backups");
            Directory.CreateDirectory(backups);
            string path = Path.Combine(backups, $"config_{now:yyyyMMdd_HHmmss}_restore.json");
            File.Copy(live, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
    /// tell は、前の版に戻せなかったときに案内を出す。applySafe は、固まった新しい版を強制終了したあとに安全な回転数を書く
    /// </summary>
    public static int RunGuard(string appDir, Action<string> log, Action<string> tell, Action? applySafe = null)
    {
        if (ReadPending(appDir) is not { } p)
        {
            log("更新の途中の記録がないので、見守りを終わります");   // no-loc（ログだけ）
            return 0;
        }
        log($"更新の見守りを始めます: {p.From} → {p.To}");   // no-loc（ログだけ）
        var sw = Stopwatch.StartNew();
        Stopwatch? newSeen = null;
        var poll = Stopwatch.StartNew();
        while (true)
        {
            Thread.Sleep(5000);
            // スリープしていた（5 秒のはずが長く空いた）ら、待つ時間を数え直す（Stopwatch はスリープ中も進むので、
            // 復帰した直後に、ちゃんと動いている新しい版を「時間切れ」で戻さないように）
            if (poll.Elapsed > TimeSpan.FromSeconds(30))
            {
                log($"スリープから戻ったようなので（{poll.Elapsed.TotalSeconds:F0} 秒空いた）、待つ時間を数え直します");   // no-loc（ログだけ）
                sw.Restart();
                newSeen = null;
            }
            poll.Restart();
            var installed = InstallCheck.FileVersion(AppExe(appDir));
            if (installed?.ToString(3) == p.To) newSeen ??= Stopwatch.StartNew();
            var step = Decide(p, installed, IsMarkedOk(appDir, p), AppRunning(), sw.Elapsed, InstallerRunning(), newSeen?.Elapsed);
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
                    return RestoreAndStart(appDir, L($"新しい版 {p.To} が、{GuardTimeout.TotalMinutes:F0} 分のうちにファンの制御を始めませんでした。"), log, tell, applySafe: applySafe);
            }
        }
    }

    /// <summary>
    /// アプリを終わらせて、前の版に戻し、起動し直す。
    /// まず「終了して」の合図で 60 秒待つ（ファンは BIOS に戻る）。終わらなければ（新しい版が固まっている・見張りが止まった案内を出している）、
    /// このフォルダーの Zeph-Iris.exe を強制終了し、安全な回転数を書いてから戻す（applySafe）
    /// </summary>
    public static int RestoreAndStart(string appDir, string reason, Action<string> log, Action<string> tell, bool manual = false, Action? applySafe = null)
    {
        if (!StopApp(appDir, TimeSpan.FromSeconds(60)))
        {
            log("終了の合図で終わらないので、強制終了します");   // no-loc（ログだけ）
            bool stopped = ForceStop(appDir, log);
            // 強制終了したアプリ（見張りも）は後片付けができず、最後に書いた回転数のまま。
            // 終わらせきれなかったときも、見張りまで止めているかもしれないので、安全な回転数にしておく
            try { applySafe?.Invoke(); }
            catch (Exception ex) { log("安全な回転数を書けません: " + ex.Message); }   // no-loc（ログだけ）
            if (!stopped)
            {
                log("アプリを終わらせられないので、前の版に戻せません");   // no-loc（ログだけ）
                tell(L($"Zeph-Iris を終わらせられないため、前の版に戻せませんでした。タスクトレイの Zeph-Iris を終了する（または PC を再起動する）と、ファンは BIOS の設定に戻ります。そのあと、次のファイルをダブルクリックすると前の版に戻せます。\n{RecoveryPath(appDir)}"));
                return 1;
            }
        }
        var result = Restore(appDir, reason, DateTime.Now, manual);
        if (result.Problem is { } problem)
        {
            log("前の版に戻すときの問題: " + problem);   // no-loc（ログだけ）
            tell(problem);
        }
        if (!result.ExeRestored) return 1;
        log("前の版に戻しました。起動します");   // no-loc（ログだけ）
        StartApp(appDir, log);
        return result.Problem is null ? 0 : 1;
    }

    /// <summary>
    /// このフォルダーの Zeph-Iris.exe（本体と見張り）を強制終了する。見張りが本体を起動し直すことがあるので、2 回まわす。
    /// 全部終わったら true
    /// </summary>
    internal static bool ForceStop(string appDir, Action<string> log)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var p in OwnProcesses(appDir))
            {
                using (p)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                        log($"強制終了しました（PID {p.Id}）");   // no-loc（ログだけ）
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        log($"強制終了できません（PID {p.Id}）: {ex.Message}");   // no-loc（ログだけ）
                    }
                }
            }
            Thread.Sleep(1500);
        }
        var left = OwnProcesses(appDir);
        foreach (var p in left) p.Dispose();
        return left.Count == 0;
    }

    /// <summary>
    /// このフォルダーの Zeph-Iris.exe のプロセス（本体と見張り）。ほかのフォルダーの Zeph-Iris.exe（zip から出した別のコピーなど）は入れない。
    /// 場所を読めないプロセスは、念のためこのフォルダーのものとして扱う
    /// </summary>
    private static List<Process> OwnProcesses(string appDir)
    {
        string exe = Path.GetFullPath(AppExe(appDir));
        var list = new List<Process>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppIdentity.ExeName)))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            if (path is null || string.Equals(Path.GetFullPath(path), exe, StringComparison.OrdinalIgnoreCase)) list.Add(p);
            else p.Dispose();
        }
        return list;
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

    /// <summary>動いているアプリに「終了して」の合図を送り（ファンは BIOS に戻る）、このフォルダーの本体と見張りが終わるのを待つ</summary>
    public static bool StopApp(string appDir, TimeSpan wait)
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
        while (sw.Elapsed < wait)
        {
            var ps = OwnProcesses(appDir);
            foreach (var p in ps) p.Dispose();
            if (ps.Count == 0) return true;
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

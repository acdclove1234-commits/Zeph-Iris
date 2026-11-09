// ==================================================
// AutoStart.cs - タスクスケジューラによるログオン時自動起動 (管理者権限・時間制限なし)
// Dependencies: .NET 8 (MIT License), Windows schtasks.exe (OS標準)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using System.Security;
using System.Text;

namespace ProFanController;

public static class AutoStart
{
    private const string TaskName = AppIdentity.Name;

    public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    /// <summary>
    /// 現在の exe をログオン時に「最上位の特権」で起動するタスクとして登録する。
    /// schtasks の /SC ONLOGON だと既定で「3日で停止」「バッテリー時は起動しない」が付くため、
    /// XML で設定を明示して常駐アプリとして正しく動くようにする。
    /// exePath を渡すと、その exe を登録する（別の場所へ移したとき）
    /// </summary>
    public static bool Enable(string? exePath = null)
    {
        string exe = exePath ?? Environment.ProcessPath ?? throw new InvalidOperationException(L("実行ファイルのパスを取得できません。"));
        string workDir = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";

        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Zeph-Iris をログオン時にタスクトレイで起動します。</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>--minimized</Arguments>
                  <WorkingDirectory>{SecurityElement.Escape(workDir)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        // 一時フォルダーはふだんの権限のプログラムからも書き込めるので、
        //   ・名前は毎回ランダムにする（新しく作れなければ失敗にする＝前もって置かれたファイルは使わない）
        //   ・schtasks が読み終わるまで、読み取り以外（書き換え・削除・名前の変更）をさせないように開いたままにする
        string tmp = Path.Combine(Path.GetTempPath(), $"Zeph-Iris_task_{Path.GetRandomFileName()}.xml");
        try
        {
            var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray(); // schtasks は UTF-16 の XML を要求する
            using (var write = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write.Write(bytes);
                write.Flush(true);
            }
            // 読み取り専用で開き直し、ほかのプログラムには読み取りだけを許す（書き換え・削除・名前の変更はできない）。
            // 書き込み用に開いたままだと schtasks が読めないことがあるため。開き直すまでの間にすり替えられていないかも確かめる
            using (var read = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var check = new byte[bytes.Length + 1];
                int n = 0, r;
                while (n < check.Length && (r = read.Read(check, n, check.Length - n)) > 0) n += r;
                if (n != bytes.Length || !check.AsSpan(0, n).SequenceEqual(bytes))
                {
                    Logger.Warn("自動起動の登録を中止しました（一時ファイルの内容が書いたものと違います）");
                    return false;
                }
                return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F") == 0;
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 消せなくても登録には影響しない */ }
        }
    }

    public static bool Disable() => RunSchtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

    /// <summary>前の名前で登録した自動起動のタスクがあるか（タスクの定義ファイルがあるかを見る）</summary>
    public static bool TaskExists(string taskName)
    {
        try { return File.Exists(Path.Combine(Environment.SystemDirectory, "Tasks", taskName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>前の名前で登録した自動起動のタスクを消す</summary>
    public static bool RemoveTask(string taskName) => RunSchtasks($"/Delete /TN \"{taskName}\" /F") == 0;

    /// <summary>
    /// 自動起動で起動する exe の場所（登録していない・読めなければ null）。
    /// タスクの定義ファイル（System32\Tasks の中の UTF-16 の XML）を直接読む
    /// （schtasks /Query /XML の出力は日本語のフォルダー名が文字化けすることがあるため。管理者権限で動いているので読める）
    /// </summary>
    public static string? RegisteredPath()
    {
        try
        {
            string file = Path.Combine(Environment.SystemDirectory, "Tasks", TaskName);
            return File.Exists(file) ? InstallCheck.CommandFromTaskXml(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int RunSchtasks(string arguments)
    {
        var psi = SystemTool.Start("schtasks.exe", arguments);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var p = Process.Start(psi)!;
        // 標準出力と標準エラーを同時に読む（片方だけ読むと、もう片方がいっぱいになって止まることがある）
        var errTask = p.StandardError.ReadToEndAsync();
        var outTask = p.StandardOutput.ReadToEndAsync();
        p.WaitForExit(10_000);
        string err = errTask.Wait(1000) ? errTask.Result : "";
        outTask.Wait(1000);
        if (p.ExitCode != 0 && !arguments.StartsWith("/Query"))
        {
            Logger.Warn($"schtasks {arguments} failed: {err.Trim()}");
        }
        return p.ExitCode;
    }
}

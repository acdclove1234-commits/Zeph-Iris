// ==================================================
// App.Uninstall.cs - アンインストール（お片付け）の実際の操作と、PawnIO を読み込むための起動し直し
//   (ファンを BIOS に戻して確かめる → ファン制御・LED を止める → ファイアウォールの許可・自動起動・
//    クラッシュの記録の設定を消す → 選んだら PawnIO をアンインストール → 正しく終了 → 選んだら設定・記録を消す)
//   手順の順番と、失敗したときに続けるかどうかは Core/CleanUninstall.cs で決める
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace ProFanController;

public partial class App
{
    /// <summary>
    /// お片付けを始めた。これ以降は設定が変わっても、スマホ監視（とファイアウォールの許可）や見張りプロセスを
    /// 付け直さない（消した直後に、設定の保存をきっかけに作り直されないように）
    /// </summary>
    private bool _cleanupStarted;

    /// <summary>ファンを BIOS に戻し終わるのを待つ最長の時間</summary>
    private static readonly TimeSpan RestoreWait = TimeSpan.FromSeconds(15);

    /// <summary>「アプリの終了後に設定・ログ・記録を消す」プロセスとして起動されたときの引数（後ろに本体のプロセス ID）</summary>
    private const string CleanupDataArgument = "--cleanup-data";

    /// <summary>クラッシュしたときの記録（ミニダンプ）を残すよう Windows に頼むレジストリのキー（HKLM の下）</summary>
    private static string CrashDumpKey => InstallerCleanup.CrashDumpKey(Path.GetFileName(Environment.ProcessPath ?? AppIdentity.ExeName));

    // ----- PawnIO を入れたあとの起動し直し -----

    /// <summary>
    /// アプリを正しく終了して（ファンは BIOS 制御に戻る）、すぐに起動し直す。
    /// PawnIO はハードウェアを開くときにしか読み込まれないので、入れたあとはこれで使えるようになる
    /// </summary>
    public void RestartApplication(string extraArguments = "")
    {
        if (_exiting || _cleanupStarted) return;
        Logger.Info("アプリを起動し直します（PawnIO の読み込みなど）");
        // 新しいアプリは、このアプリが終わる（二重起動の防止の印を離す）まで最大 30 秒待ってから始まる
        StartSelf(extraArguments.Length > 0 ? $"{RestartArgument} {extraArguments}" : RestartArgument);
        ExitApplication();
    }

    /// <summary>セットアップの画面で PawnIO を自動で入れて起動し直したときの引数（もう一度は自動で入れない）</summary>
    public const string PawnIoTriedArgument = "--pawnio-tried";

    /// <summary>この起動の前に、PawnIO を自動で入れて起動し直した（くり返さない）</summary>
    public bool PawnIoAutoTried { get; private set; }

    // ----- お片付け -----

    /// <summary>確認の画面を出す前に、この PC の状態を調べる（schtasks・netsh を使うので、別のスレッドで）</summary>
    public Task<CleanupFacts> GatherCleanupFactsAsync() => Task.Run(() => new CleanupFacts(
        AutoStartRegistered: SafeCheck(AutoStart.IsEnabled),
        FirewallRuleExists: SafeCheck(WebServer.FirewallRuleExists),
        CrashDumpSettingExists: SafeCheck(CrashDumpSettingExists),
        PawnIoInstalled: SafeCheck(PawnIoSetup.IsInstalled)));

    private static bool SafeCheck(Func<bool> check)
    {
        try { return check(); }
        catch (Exception ex)
        {
            Logger.Warn($"お片付けの前の確認に失敗: {ex.Message}");
            return false;
        }
    }

    private static bool CrashDumpSettingExists()
    {
        using var key = Registry.LocalMachine.OpenSubKey(CrashDumpKey);
        return key is not null;
    }

    /// <summary>
    /// お片付けの手順を、決めた順番に行う（画面のスレッドから呼ぶ）。終了と、設定・記録を消すのは、ここでは行わない
    /// （結果を読んでもらってから FinishCleanUninstall で行う）。report には、手順ごとの進み具合と結果を渡す
    /// </summary>
    public async Task<IReadOnlyList<StepResult>> RunCleanupAsync(IReadOnlyList<CleanupStep> plan, Action<StepResult> report)
    {
        _cleanupStarted = true;
        var results = new List<StepResult>();
        Logger.Info($"お片付けを始めます: {string.Join(" → ", plan)}");
        foreach (var step in plan)
        {
            if (step is CleanupStep.ExitApp or CleanupStep.DeleteData) continue;
            StepResult result;
            if (CleanUninstall.SkipReason(step, results) is { } skip)
            {
                result = new StepResult(step, StepStatus.Skipped, skip);
            }
            else
            {
                report(new StepResult(step, StepStatus.Running, L("行っています…")));
                try
                {
                    result = await RunCleanupStepAsync(step, text => report(new StepResult(step, StepStatus.Running, text)));
                }
                catch (Exception ex)
                {
                    Logger.Error($"お片付けの {step} で例外: {ex}");
                    result = new StepResult(step, StepStatus.Failed, L($"失敗しました: {ex.Message}"));
                }
            }
            results.Add(result);
            string line = L($"お片付け: {step}: {result.Status}: {result.Message}");
            if (result.Status == StepStatus.Failed) Logger.Error(line);
            else if (result.Status is StepStatus.Warning or StepStatus.Skipped) Logger.Warn(line);
            else Logger.Info(line);
            report(result);
        }
        if (CleanUninstall.Aborted(results))
        {
            // 何も変えずに、これまでどおり動き続ける
            _cleanupStarted = false;
            Logger.Warn("お片付けを中止しました（ファンを BIOS 制御に戻せなかったため）。制御を続けます");
        }
        return results;
    }

    private async Task<StepResult> RunCleanupStepAsync(CleanupStep step, Action<string> progress)
    {
        StepResult Done(string m) => new(step, StepStatus.Done, m);
        StepResult Warn(string m) => new(step, StepStatus.Warning, m);
        StepResult Fail(string m) => new(step, StepStatus.Failed, m);

        switch (step)
        {
            case CleanupStep.RestoreFans:
            {
                if (_controller is null) return Done(L("ファンの制御は動いていませんでした"));
                Try(L("テスト"), () => _window?.StopTestIfRunning());
                // スリープに入るときと同じ道筋で、触ったすべての端子を BIOS に戻し、戻せたかを受け取る。
                // ここで失敗しても、制御を再開すれば元どおり（このあとの手順は行わない）
                var engine = _controller.Engine;
                var suspend = engine.Suspend();
                bool finished = await Task.WhenAny(suspend, Task.Delay(RestoreWait)) == suspend;
                if (finished && suspend.Result) return Done(L("すべてのファンを BIOS の自動制御に戻しました"));
                engine.Resume();
                return Fail(finished
                    ? L("BIOS 制御に戻せなかったファンの端子があります（端子の名前はログにあります）。")
                    : L($"{RestoreWait.TotalSeconds:F0} 秒たっても、ファンを BIOS 制御に戻し終わりませんでした。"));
            }
            case CleanupStep.StopFanControl:
            {
                // 止めると、もう一度すべての端子を BIOS に戻してから、ハードウェア（PawnIO）を閉じる。
                // スリープと同じ一時停止の状態は、自分で制御を再開してしまう（60 周期で）ので、すぐに止める
                bool stopped = _controller is null || await Task.Run(_controller.Engine.Stop);
                // 制御ループが止まると、見張りプロセスは「フリーズした」と見なして 30 秒後に安全な回転数を書き、90 秒後にアプリを起動し直す。
                // そうならないよう、ここで見張りを終わらせる（止めきれなかったときは、見張りに安全な回転数にしてもらってから終わらせる）
                bool hadWatchdog = _watchdogRunning;
                _watchdogGeneration++;
                _watchdogRunning = false;
                if (stopped) Watchdog.SignalStop();
                else await Task.Run(Watchdog.SignalStopUnsafe);
                string watchdog = hadWatchdog ? (stopped ? L("見張りプロセスも終わらせました") : "") : "";
                return stopped
                    ? Done(L("ファンの制御を止めて、ハードウェアを閉じました") + (watchdog.Length > 0 ? L($"（{watchdog}）") : ""))
                    : Fail(L("ファンの制御が 5 秒以内に止まりませんでした。ファンは BIOS 制御に戻してありますが、念のため")
                           + (hadWatchdog ? L("見張りプロセスが安全な回転数にしました") : L("PC を再起動してください"))
                           + L("（PC を再起動すると BIOS の制御に戻ります）。"));
            }
            case CleanupStep.StopLighting:
            {
                bool wasOn = _controller?.Config.Rgb.Enabled == true;
                Rgb.Dispose();
                return Done(wasOn ? L("ライティングの制御を止めて、デバイスを離しました") : L("ライティングの制御は使っていませんでした（接続を閉じました）"));
            }
            case CleanupStep.RemoveFirewallRule:
            {
                Try(L("スマホ監視"), () => _web?.Stop());
                await Task.Run(WebServer.RemoveFirewall);
                return await Task.Run(WebServer.FirewallRuleExists)
                    ? Fail(L("ファイアウォールの許可を消せませんでした。「Windows セキュリティ」→「ファイアウォールとネットワーク保護」→「詳細設定」→「受信の規則」から「Zeph-Iris Web」を削除してください。"))
                    : Done(L("スマホ監視を止めて、ファイアウォールの許可を消しました"));
            }
            case CleanupStep.RemoveAutoStart:
            {
                await Task.Run(AutoStart.Disable);
                return await Task.Run(AutoStart.IsEnabled)
                    ? Fail(L("自動起動を消せませんでした。タスクスケジューラ（タスク スケジューラ ライブラリ）から「Zeph-Iris」を削除してください。"))
                    : Done(L("ログオン時の自動起動を消しました"));
            }
            case CleanupStep.RemoveCrashDumpSetting:
            {
                Registry.LocalMachine.DeleteSubKeyTree(CrashDumpKey, throwOnMissingSubKey: false);
                return CrashDumpSettingExists()
                    ? Warn(L($"クラッシュしたときの記録の設定を消せませんでした（HKEY_LOCAL_MACHINE\\{CrashDumpKey}）。そのままでも害はありません。"))
                    : Done(L("クラッシュしたときの記録の設定を消しました"));
            }
            case CleanupStep.UninstallPawnIo:
            {
                Logger.Info($"PawnIO のアンインストールを始めます（winget {PawnIoSetup.UninstallArguments}）");
                var run = await Winget.RunAsync(PawnIoSetup.UninstallArguments, line =>
                {
                    if (Winget.Describe(line) is { } text) Dispatcher.InvokeAsync(() => progress(text));
                }, PawnIoSetup.Timeout);
                bool still = await Task.Run(PawnIoSetup.IsInstalled);
                var r = PawnIoSetup.InterpretUninstall(run, still);
                UI.PawnIoInstallPanel.LogRun(L("アンインストール"), run, still, r);
                return !r.Ok ? Fail(r.Message) : r.NeedsPcRestart ? Warn(r.Message) with { PcRestart = true } : Done(r.Message);
            }
            default:
                return new StepResult(step, StepStatus.Skipped, L("ここでは行いません"));
        }
    }

    /// <summary>
    /// お片付けの最後: 選んでいたら「終了後に設定・ログ・記録を消す」プロセスを起動し、そうでなければアプリのフォルダーを
    /// エクスプローラーで開いてから、正しく終了する（見張りプロセスにも正しい終了を伝えるので、起動し直されない）
    /// </summary>
    public void FinishCleanUninstall(bool deleteData)
    {
        string folder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        // インストーラーで入れたとき: アンインストーラーに続きを任せる。アンインストーラーは、このアプリが終わるのを待ってから、
        // アプリ本体を消し、Windows の「インストールされているアプリ」からも外す（選んでいれば設定・ログ・記録も消す）
        if (InstallerCleanup.UninstallerPath(folder) is { } uninstaller)
        {
            try
            {
                Process.Start(new ProcessStartInfo(uninstaller, InstallerCleanup.UninstallerArguments(deleteData))
                {
                    UseShellExecute = false,
                    WorkingDirectory = Environment.SystemDirectory,
                })?.Dispose();
                Logger.Info($"お片付け: アンインストーラーを起動して、アプリを終了します（設定・記録を消す: {(deleteData ? "はい" : "いいえ")}）");
                ExitApplication();
                return;
            }
            catch (Exception ex)
            {
                Logger.Error($"アンインストーラーを起動できません: {ex.Message}");
                MessageBox.Show(L($"アンインストーラーを起動できませんでした（{ex.Message}）。\nアプリが終了したあとに、Windows の「設定」→「アプリ」→「インストールされているアプリ」から Zeph-Iris をアンインストールしてください。"),
                    AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        bool helperStarted = false;
        if (deleteData)
        {
            try
            {
                string exe = Environment.ProcessPath ?? throw new InvalidOperationException(L("実行ファイルのパスが分かりません。"));
                Process.Start(new ProcessStartInfo(exe, $"{CleanupDataArgument} {Environment.ProcessId}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.SystemDirectory,
                })?.Dispose();
                helperStarted = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"設定・記録を消すプロセスを起動できません: {ex.Message}");
                MessageBox.Show(L($"設定・ログ・記録を消す準備ができませんでした（{ex.Message}）。\nアプリが終了したあとに、フォルダーごと削除してください:\n{folder}"),
                    AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        if (!helperStarted) Try(L("フォルダーを開く"), () => SystemTool.Reveal(folder));
        Logger.Info($"お片付け: アプリを終了します（設定・記録を消す: {(helperStarted ? "終了後に行う" : "行わない")}）");
        ExitApplication();
    }

    // ----- 終了後に設定・ログ・記録を消すプロセス -----

    /// <summary>
    /// 「--cleanup-data 本体のプロセスID」で起動されたときは、本体と見張りプロセスが終わるのを待ってから、設定・ログ・記録を消す。
    /// このプロセスはログを書かない（消したばかりの logs フォルダーを作り直さないように）。結果はお知らせの画面で伝える
    /// </summary>
    private static bool TryRunCleanupHelper(string[] args)
    {
        int i = Array.IndexOf(args, CleanupDataArgument);
        if (i < 0) return false;
        int parentPid = i + 1 < args.Length && int.TryParse(args[i + 1], out int p) ? p : -1;
        string folder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string message;
        var icon = MessageBoxImage.Information;
        if (!WaitForExit(parentPid, TimeSpan.FromSeconds(60)) || !WaitForOtherInstances(TimeSpan.FromSeconds(30)))
        {
            message = L("Zeph-Iris がまだ動いているため、設定・ログ・記録は消しませんでした。\n")
                      + L($"アプリを終了してから、次のフォルダーをフォルダーごと削除してください:\n{folder}");
            icon = MessageBoxImage.Warning;
        }
        else
        {
            var failed = CleanUninstall.DeleteData(folder);
            if (failed.Count == 0)
            {
                message = L($"{CleanUninstall.DataDescription}を消しました。\n\n")
                          + L($"このお知らせを閉じると、エクスプローラーでアプリのフォルダーを開きます。フォルダーごと削除すれば、お片付けは終わりです:\n{folder}");
            }
            else
            {
                message = L("消せなかったものがあります（ほかのアプリが開いているかもしれません）:\n・")
                          + string.Join(L("\n・"), failed.Take(10)) + (failed.Count > 10 ? L($"\n…ほか {failed.Count - 10} 個") : "")
                          + L($"\n\nこのお知らせを閉じたあと、次のフォルダーをフォルダーごと削除してください:\n{folder}");
                icon = MessageBoxImage.Warning;
            }
        }
        // 本体の画面はもうないので、ほかのウィンドウの後ろに隠れないように出す（閉じるまでフォルダーを消せないため）
        MessageBox.Show(message, L("Zeph-Iris のお片付け"), MessageBoxButton.OK, icon, MessageBoxResult.OK, MessageBoxOptions.DefaultDesktopOnly);
        try { SystemTool.Reveal(folder); } catch { /* 開けなくても、お知らせで場所は伝えてある */ }
        return true;
    }

    private static bool WaitForExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException) { return true; } // もう終わっている
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>同じ exe から動いているほかのプロセス（見張りプロセスなど）が終わるのを待つ。時間内に終わらなければ false</summary>
    private static bool WaitForOtherInstances(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        string self = Environment.ProcessPath ?? "";
        using var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            using (p)
            {
                if (p.Id == me.Id) continue;
                bool same;
                try { same = string.Equals(p.MainModule?.FileName, self, StringComparison.OrdinalIgnoreCase); }
                catch { same = true; } // 確かめられないときは、同じものとして待つ（消しすぎないほうを選ぶ）
                if (!same) continue;
                var left = until - DateTime.UtcNow;
                try { if (left <= TimeSpan.Zero || !p.WaitForExit((int)left.TotalMilliseconds)) return false; }
                catch (InvalidOperationException) { /* もう終わっている */ }
            }
        }
        return true;
    }
}

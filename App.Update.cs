// ==================================================
// App.Update.cs - アプリの中からの更新 (新しい版の確認・お知らせ・インストーラーの実行・更新したあとのお知らせ)
//   確認と、ダウンロードしたインストーラーの確かめ方は Core/AppUpdater.cs。設定画面の「アップデート」は MainWindow.Update.cs
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Windows;

namespace ProFanController;

public partial class App
{
    /// <summary>GitHub にある、いまより新しい版（なければ null）</summary>
    public UpdateInfo? AvailableUpdate { get; private set; }

    /// <summary>新しい版の有無が変わった（設定画面の表示を直す）</summary>
    public event Action? UpdateStateChanged;

    /// <summary>インストーラーを起動した（もう一度は起動しない）</summary>
    public bool UpdateStarted { get; private set; }

    /// <summary>確かめたインストーラーのファイル（アプリが終わるまで開いたままにして、すり替えを防ぐ）</summary>
    private AppUpdater.VerifiedInstaller? _verifiedInstaller;
    private System.Threading.Timer? _updateTimer;
    private bool _checkingUpdate;

    /// <summary>自動の確認は、前の確認からこれだけたってから</summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(20);

    /// <summary>起動したとき: 更新したあとなら知らせ、1 分後から 6 時間ごとに新しい版を確かめる（設定でオフにできる）</summary>
    private void StartUpdateChecks(AppController controller, bool recovered)
    {
        _startedRecovered = recovered;
        var cfg = controller.Config.Update;
        string now = AppUpdater.CurrentVersion.ToString(3);
        // 更新の見守りが前の版（この版）に戻したとき: 知らせて、同じ版を自動では勧めない
        if (RunningFromInstalledFolder && UpdateRecovery.TakeRolledBack(AppFolder) is { } rb)
        {
            Logger.Warn($"{rb.Failed} から {rb.Restored} に戻しました（{(rb.Manual ? "手で" : "自動で")}・{rb.Reason}・戻す前の設定: {(rb.SettingsBackup is null ? "取っておけず" : Path.GetFileName(rb.SettingsBackup))}）");
            string msg = rb.Manual
                ? L($"前の版 {rb.Restored} に戻しました（設定も更新する前のものです）。")
                : L($"新しい版 {rb.Failed} がうまく起動しなかったため、自動で {rb.Restored} に戻しました（設定も更新する前のものです）。{rb.Reason}");
            if (rb.SettingsBackup is not null) msg += L("戻す直前の設定は、設定 →「安全・バックアップ」の「復元前」のバックアップから戻せます。");
            _window?.ShowAlert(msg, AlertLevel.Warning);
            cfg.NotifiedVersion = "v" + rb.Failed;
            controller.Commit(recordUndo: false);
        }
        if (cfg.LastRunVersion is { Length: > 0 } before && before != now)
        {
            Logger.Info($"更新されました: {before} → {now}");
            _window?.ShowAlert(L($"Zeph-Iris を {before} から {now} に更新しました。"), AlertLevel.Info);
            _tray?.ShowBalloon(AppIdentity.Name, L($"{now} に更新しました。"), AlertLevel.Info);
            // 新しい版で困ったときに戻せるよう、前の版を覚えておく
            cfg.PreviousVersion = before;
            cfg.UpdatedAt = DateTime.Now;
        }
        if (cfg.LastRunVersion != now)
        {
            cfg.LastRunVersion = now;
            controller.Commit(recordUndo: false);
        }
        // 更新したばかりの版が予期せず終了して、見張りプロセスが起動し直したとき: 前の版に戻せることを知らせる
        if (recovered && CanRollBack)
        {
            Logger.Warn($"更新したばかりの版（{now}）が予期せず終了しました（前の版 {cfg.PreviousVersion}）");
            _window?.ShowAlert(L($"更新したばかりの版（{now}）が予期せず終了しました。何度も続くときは、前の版（{cfg.PreviousVersion}）に戻せます。"),
                AlertLevel.Warning, L("前の版に戻す"), () => _window?.BeginRollback());
        }
        // 前の更新でダウンロードしたインストーラーを消す（インストーラーが終わりきるのを待ってから）
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(2));
            int removed = AppUpdater.CleanUpDownloads();
            if (removed > 0) Logger.Info($"前の更新でダウンロードしたインストーラーを片付けました（{removed} 個）");
        });
        _updateTimer = new System.Threading.Timer(_ => Dispatcher.InvokeAsync(AutoCheckForUpdateAsync), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
    }

    /// <summary>前の版に戻せる（前の版が分かっていて、更新してから 14 日以内）</summary>
    public bool CanRollBack => _controller is { } c && AppUpdater.CanRollBack(c.Config.Update, AppUpdater.CurrentVersion, DateTime.Now);

    /// <summary>前の版に戻すとき、更新の前に取っておいた前の版（previous フォルダー）を使える（ネットにつながず、設定も更新する前のものに戻る）</summary>
    public bool CanRestoreLocally =>
        _controller is { } c && RunningFromInstalledFolder && AppUpdater.ParseTag(c.Config.Update.PreviousVersion) is { } previous
        && UpdateRecovery.BackupVersion(AppFolder)?.ToString(3) == previous.ToString(3);

    /// <summary>
    /// 前の版（UpdateConfig.PreviousVersion）に戻す。取っておいた前の版があればそれで（設定も更新する前のものに戻す。いまの設定は backups に取っておく）、
    /// なければ GitHub からインストーラーを取ってきて実行する。うまくいけば null、だめなら理由の文
    /// </summary>
    public async Task<string?> RollBackAsync(IProgress<double>? progress)
    {
        if (UpdateStarted) return null;   // もう戻している・更新している（二度押し）
        if (_controller is null || !CanRollBack || AppUpdater.ParseTag(_controller.Config.Update.PreviousVersion) is not { } previous)
            return L("前の版が分からないため、戻せません。");
        // 更新の前に取っておいた前の版（previous フォルダー）があれば、ネットにつながずにそれで戻す
        if (CanRestoreLocally)
        {
            try
            {
                _controller.Commit(recordUndo: false);
                UpdateStarted = true;
                var psi = new System.Diagnostics.ProcessStartInfo(UpdateRecovery.RecoveryPath(AppFolder))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = UpdateRecovery.BackupDir(AppFolder),
                };
                psi.ArgumentList.Add(UpdateRecovery.RestoreArgument);
                psi.ArgumentList.Add("--yes");
                System.Diagnostics.Process.Start(psi)?.Dispose();
                Logger.Info($"取っておいた前の版 {previous.ToString(3)} に戻します（このアプリは、まもなく終了の合図を受けて終わります）");
                return null;
            }
            catch (Exception ex)
            {
                UpdateStarted = false;
                Logger.Warn($"取っておいた前の版で戻せませんでした。GitHub から取ってきます: {ex.Message}");
            }
        }
        UpdateInfo? info;
        try { info = await AppUpdater.FindReleaseAsync(previous, CancellationToken.None); }
        catch (Exception ex)
        {
            Logger.Warn($"前の版のリリースを読めませんでした: {ex.Message}");
            return L($"前の版を GitHub から読めませんでした（{ex.Message}）。インターネットにつながっているか確かめてください。");
        }
        if (info is null) return L($"前の版（{previous.ToString(3)}）のインストーラーが GitHub に見つかりませんでした。");
        Logger.Info($"前の版に戻します: {AppUpdater.CurrentVersion.ToString(3)} → {previous.ToString(3)}");
        return await InstallUpdateAsync(info, progress);
    }

    private async Task AutoCheckForUpdateAsync()
    {
        var cfg = _controller?.Config.Update;
        if (cfg is null || !cfg.AutoCheck || UpdateStarted || _cleanupStarted || _exiting) return;
        if (cfg.LastChecked is DateTime last && DateTime.Now - last < UpdateCheckInterval && last <= DateTime.Now) return;
        await CheckForUpdateAsync(userAsked: false);
    }

    /// <summary>
    /// GitHub に新しい版を問い合わせ、結果を返す（画面に出す文）。userAsked でないとき（自動の確認）は、
    /// 新しい版を見つけたら、お知らせとタスクトレイの吹き出しで 1 回だけ知らせる
    /// </summary>
    public async Task<string> CheckForUpdateAsync(bool userAsked)
    {
        var controller = _controller;
        if (controller is null || _checkingUpdate) return L("確認しています…");
        _checkingUpdate = true;
        try
        {
            var info = await AppUpdater.CheckAsync(CancellationToken.None);
            var cfg = controller.Config.Update;
            cfg.LastChecked = DateTime.Now;
            AvailableUpdate = info is not null && AppUpdater.IsNewer(info.Version, AppUpdater.CurrentVersion) ? info : null;
            Logger.Info(AvailableUpdate is { } found
                ? $"新しい版があります: {found.Version.ToString(3)}（{found.AssetName}・{found.Size} バイト）"
                : $"新しい版はありません（いまの版 {AppUpdater.CurrentVersion.ToString(3)}・GitHub の最新 {info?.Version.ToString(3) ?? "なし"}）");
            if (AvailableUpdate is { } u && !userAsked && cfg.NotifiedVersion != u.Tag)
            {
                cfg.NotifiedVersion = u.Tag;
                _window?.ShowAlert(L($"新しい版 Zeph-Iris {u.Version.ToString(3)} があります。「更新する」を押すと、ダウンロードして入れ替えます（そのあいだ十数秒、ファンは BIOS の設定で回ります）。"),
                    AlertLevel.Info, L("更新する"), () => _window?.BeginUpdate());
                _tray?.ShowBalloon(AppIdentity.Name, L($"新しい版 {u.Version.ToString(3)} があります。"), AlertLevel.Info);
            }
            controller.Commit(recordUndo: false);
            UpdateStateChanged?.Invoke();
            return AvailableUpdate is { } a
                ? L($"新しい版 {a.Version.ToString(3)} があります。")
                : L($"最新の版です（{AppUpdater.CurrentVersion.ToString(3)}）。");
        }
        catch (Exception ex)
        {
            Logger.Warn($"新しい版を確かめられませんでした: {ex.Message}");
            return L($"新しい版を確かめられませんでした（{ex.Message}）。インターネットにつながっているか確かめてください。");
        }
        finally
        {
            _checkingUpdate = false;
        }
    }

    /// <summary>
    /// 新しい版をダウンロードして確かめ、インストーラーを起動する。うまくいけば null（まもなくインストーラーがこのアプリを終了させる）、
    /// だめなら理由の文を返す
    /// </summary>
    public async Task<string?> InstallUpdateAsync(UpdateInfo info, IProgress<double>? progress)
    {
        if (UpdateStarted) return null;
        if (_cleanupStarted || _exiting) return L("アプリを終了しているところなので、更新しません。");
        UpdateStarted = true;
        try
        {
            Logger.Info($"更新を始めます: {AppUpdater.CurrentVersion.ToString(3)} → {info.Version.ToString(3)}（{info.DownloadUrl}）");
            var installer = await AppUpdater.DownloadAsync(info, progress, CancellationToken.None);
            _controller?.Commit(recordUndo: false); // 終了する前に、いまの設定を書き出しておく
            // 新しい版が起動しなかったら、外から前の版に戻す見守りを用意する。用意できなければ更新しない
            // （いまはこの版がファンを制御しているので、ここでやめても安全。インストーラーを手で実行すれば更新はできる）。
            // 古い版へ戻すとき（GitHub から前の版を入れ直すとき）は使わない（古い版は見守りへの知らせを書かないことがあり、
            // 見守りが「起動しなかった」と思って、戻したのをまた元に戻してしまうため）
            if (AppUpdater.IsNewer(info.Version, AppUpdater.CurrentVersion) && StartUpdateGuard(info.Version) is { } why)
            {
                installer.HeldOpen.Dispose();
                UpdateStarted = false;
                return L($"前の版に戻すための準備ができなかったため、更新を中止しました（{why}）。空き容量を確かめるか、リリースのページからインストーラーを手で実行してください。");
            }
            bool showApp = _window?.IsVisible == true;
            AppUpdater.StartInstaller(installer, showApp);
            _verifiedInstaller = installer;
            Logger.Info($"インストーラーを起動しました（{installer.Path}）。インストーラーがこのアプリを終了させてから入れ替えます");
            Logger.Flush();
            return null;
        }
        catch (Exception ex)
        {
            UpdateStarted = false;
            Logger.Warn($"更新できませんでした: {ex}");
            return L($"更新できませんでした（{ex.Message}）。");
        }
    }

    /// <summary>
    /// インストーラーで入れたフォルダーで動いているか（C:\Program Files\Zeph-Iris か、インストールのときに選んだ別のフォルダー。
    /// インストーラーが置くアンインストーラー unins000.exe があれば、そのフォルダーを更新で入れ替える）。
    /// zip から出しただけの場所なら false（更新で入れ替わるのはこのフォルダーではない）
    /// </summary>
    private static bool RunningFromInstalledFolder =>
        string.Equals(AppFolder, Path.GetFullPath(InstallCheck.RecommendedFolder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
        || File.Exists(Path.Combine(AppFolder, "unins000.exe"));

    private static string AppFolder => Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>
    /// 更新の前: いまの exe と設定を previous フォルダーに写し、そこから「見守り」を起動する（Core/UpdateRecovery.cs）。
    /// 用意できた・使わない場所（インストールしたフォルダーの外）なら null、用意できなければ理由（更新はやめる）
    /// </summary>
    private static string? StartUpdateGuard(Version to)
    {
        try
        {
            if (!RunningFromInstalledFolder || Environment.ProcessPath is not { } exe)
            {
                Logger.Info("インストールした場所の外で動いているので、更新の見守りは使いません");
                return null;
            }
            string guard = UpdateRecovery.Prepare(AppFolder, exe, AppUpdater.CurrentVersion, to, DateTime.Now);
            var psi = new System.Diagnostics.ProcessStartInfo(guard)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(guard)!,
            };
            psi.ArgumentList.Add(UpdateRecovery.GuardArgument);
            psi.ArgumentList.Add(AppFolder);
            using var started = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException(L("見守りのプログラムを起動できませんでした。"));
            Logger.Info($"更新の見守りを起動しました（{UpdateRecovery.GuardTimeout.TotalMinutes:F0} 分のうちに新しい版が制御を始めなければ、{AppUpdater.CurrentVersion.ToString(3)} に戻します）");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"更新の見守りを用意できませんでした（更新はやめます）: {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>更新の見守りに「ちゃんと動いている」と知らせるか（続けて制御しているか）を数える</summary>
    private static readonly UpdateConfirm _updateConfirm = new();

    /// <summary>見守りが起動し直した版は、更新の成功に数えない（更新したばかりの版が落ちたなら、戻すほうがよい）</summary>
    private static bool _startedRecovered;

    /// <summary>
    /// エンジンのスナップショットごと（MainWindow.OnSnapshot から）: 更新した版が UpdateRecovery.ConfirmAfter のあいだ続けて制御していたら、
    /// 見守りに知らせる（書けなければ 10 秒ごとにやり直す）
    /// </summary>
    public static void OnSnapshotForUpdate()
    {
        if (_startedRecovered || !RunningFromInstalledFolder || !_updateConfirm.ShouldTry(Environment.TickCount64)) return;
        bool pending = UpdateRecovery.ReadPending(AppFolder) is { } p && p.To == AppUpdater.CurrentVersion.ToString(3) && !UpdateRecovery.IsMarkedOk(AppFolder, p);
        bool done = UpdateRecovery.MarkStarted(AppFolder, AppUpdater.CurrentVersion);
        _updateConfirm.Result(done);
        if (pending && done) Logger.Info($"更新した版が {UpdateRecovery.ConfirmAfter.TotalSeconds:F0} 秒続けてファンを制御したので、更新の見守りに知らせました");
        else if (!done) Logger.Warn("更新の見守りに知らせる印を書けませんでした。あとでもう一度書きます");
    }
}

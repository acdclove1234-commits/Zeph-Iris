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
        var cfg = controller.Config.Update;
        string now = AppUpdater.CurrentVersion.ToString(3);
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

    /// <summary>前の版（UpdateConfig.PreviousVersion）のインストーラーを GitHub から取ってきて実行する。うまくいけば null、だめなら理由の文</summary>
    public async Task<string?> RollBackAsync(IProgress<double>? progress)
    {
        if (_controller is null || !CanRollBack || AppUpdater.ParseTag(_controller.Config.Update.PreviousVersion) is not { } previous)
            return L("前の版が分からないため、戻せません。");
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
}

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
    private void StartUpdateChecks(AppController controller)
    {
        var cfg = controller.Config.Update;
        string now = AppUpdater.CurrentVersion.ToString(3);
        if (cfg.LastRunVersion is { Length: > 0 } before && before != now)
        {
            Logger.Info($"更新されました: {before} → {now}");
            _window?.ShowAlert(L($"Fanlume を {before} から {now} に更新しました。"), AlertLevel.Info);
            _tray?.ShowBalloon(AppIdentity.Name, L($"{now} に更新しました。"), AlertLevel.Info);
        }
        if (cfg.LastRunVersion != now)
        {
            cfg.LastRunVersion = now;
            controller.Commit(recordUndo: false);
        }
        _updateTimer = new System.Threading.Timer(_ => Dispatcher.InvokeAsync(AutoCheckForUpdateAsync), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
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
                _window?.ShowAlert(L($"新しい版 Fanlume {u.Version.ToString(3)} があります。「更新する」を押すと、ダウンロードして入れ替えます（そのあいだ十数秒、ファンは BIOS の設定で回ります）。"),
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

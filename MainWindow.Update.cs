// ==================================================
// MainWindow.Update.cs - 設定画面の「アップデート」 (いまの版・新しい版の確認・更新する・リリースのページ)
//   確認と更新の本体は App.Update.cs と Core/AppUpdater.cs
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Windows;

namespace ProFanController;

public partial class MainWindow
{
    private bool _updateCardHooked;

    /// <summary>「アップデート」の欄を、いまの状態に合わせる（status を渡せば、その文を出す）</summary>
    private void RefreshUpdateCard(string? status = null)
    {
        if (!_updateCardHooked)
        {
            _updateCardHooked = true;
            var app = CurrentApp;
            void Changed() => Dispatcher.InvokeAsync(() => RefreshUpdateCard());
            app.UpdateStateChanged += Changed;
            Closed += (_, _) => app.UpdateStateChanged -= Changed;
        }
        var u = CurrentApp.AvailableUpdate;
        UpdateVersionText.Text = L($"いまの版: {AppUpdater.CurrentVersion.ToString(3)}");
        if (status is not null) UpdateStatusText.Text = status;
        else if (CurrentApp.UpdateStarted) UpdateStatusText.Text = L("インストーラーを起動しました。まもなくアプリが終了し、入れ替えたあとに起動し直します。");
        else if (u is not null)
            UpdateStatusText.Text = L($"新しい版 {u.Version.ToString(3)} があります（{u.PublishedAt:yyyy/MM/dd} 公開・{u.Size / 1048576.0:F0} MB）。");
        else if (Config.Update.LastChecked is DateTime t) UpdateStatusText.Text = L($"最新の版です（{t:M/d HH:mm} に確認）。");
        else UpdateStatusText.Text = L("まだ確認していません。");
        UpdateInstallButton.Visibility = u is not null && !CurrentApp.UpdateStarted ? Visibility.Visible : Visibility.Collapsed;
        UpdateCheckButton.IsEnabled = !CurrentApp.UpdateStarted;
        bool canRollBack = CurrentApp.CanRollBack && !CurrentApp.UpdateStarted;
        RollbackButton.Visibility = canRollBack ? Visibility.Visible : Visibility.Collapsed;
        if (canRollBack) RollbackButton.Content = L($"前の版（{Config.Update.PreviousVersion}）に戻す");
    }

    private void OnRollbackClick(object sender, RoutedEventArgs e) => BeginRollback();

    /// <summary>前の版に戻す（お知らせの「前の版に戻す」からも呼ぶ）。確かめてから、前の版のインストーラーを取ってきて実行する</summary>
    public async void BeginRollback()
    {
        var app = CurrentApp;
        if (app.UpdateStarted || !app.CanRollBack) return;
        app.ShowMainWindow();
        OpenSettingsTab("general");
        string previous = Config.Update.PreviousVersion ?? "";
        var answer = MessageBox.Show(this,
            L($"前の版（{previous}）に戻します。\n\nGitHub から前の版のインストーラーをダウンロードして確かめたあと、アプリが一度終了します。入れ替えが終わるまでの十数秒、ファンは BIOS の設定で回り、終わるとアプリが起動し直します。設定・ログ・記録はそのまま残ります。\n\n戻しますか？"),
            AppIdentity.Name, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        RollbackButton.IsEnabled = false;
        UpdateCheckButton.IsEnabled = false;
        UpdateInstallButton.IsEnabled = false;
        var progress = new Progress<double>(p => UpdateStatusText.Text = L($"ダウンロードしています… {p:P0}"));
        string? error = await app.RollBackAsync(progress);
        RollbackButton.IsEnabled = true;
        UpdateInstallButton.IsEnabled = true;
        if (error is null)
        {
            RefreshUpdateCard();
            return;
        }
        RefreshUpdateCard(error);
        MessageBox.Show(this, error + L("\n\nリリースのページから、インストーラーを手で実行して更新することもできます。"),
            AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        UpdateCheckButton.IsEnabled = false;
        UpdateStatusText.Text = L("確認しています…");
        try
        {
            string result = await CurrentApp.CheckForUpdateAsync(userAsked: true);
            RefreshUpdateCard(CurrentApp.AvailableUpdate is null ? result : null);
        }
        finally
        {
            UpdateCheckButton.IsEnabled = !CurrentApp.UpdateStarted;
        }
    }

    private void OnInstallUpdateClick(object sender, RoutedEventArgs e) => BeginUpdate();

    private void OnOpenReleasesClick(object sender, RoutedEventArgs e) => SystemTool.Open(AppUpdater.ReleasesPage);

    /// <summary>新しい版に更新する（お知らせの「更新する」からも呼ぶ）。確かめてから、ダウンロードしてインストーラーを起動する</summary>
    public async void BeginUpdate()
    {
        var app = CurrentApp;
        if (app.UpdateStarted) return;
        if (app.AvailableUpdate is not { } info)
        {
            OpenSettingsTab("general");
            return;
        }
        app.ShowMainWindow();
        OpenSettingsTab("general");
        var answer = MessageBox.Show(this,
            L($"Fanlume {info.Version.ToString(3)} に更新します。\n\nインストーラー（{info.Size / 1048576.0:F0} MB）をダウンロードして確かめたあと、アプリが一度終了します。入れ替えが終わるまでの十数秒、ファンは BIOS の設定で回り、終わるとアプリが起動し直します。\n\n更新しますか？"),
            AppIdentity.Name, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK);
        if (answer != MessageBoxResult.OK) return;

        UpdateInstallButton.IsEnabled = false;
        UpdateCheckButton.IsEnabled = false;
        var progress = new Progress<double>(p => UpdateStatusText.Text = L($"ダウンロードしています… {p:P0}"));
        string? error = await app.InstallUpdateAsync(info, progress);
        UpdateInstallButton.IsEnabled = true;
        if (error is null)
        {
            RefreshUpdateCard();
            return;
        }
        RefreshUpdateCard(error);
        MessageBox.Show(this, error + L("\n\nリリースのページから、インストーラーを手で実行して更新することもできます。"),
            AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}

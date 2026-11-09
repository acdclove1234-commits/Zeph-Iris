// ==================================================
// MainWindow.Setup.cs - 設定画面の「安全・バックアップ」のドライバー（PawnIO）とアンインストール（お片付け）
//   (PawnIO がないときのお知らせとインストールの画面への案内・お片付けの確認の画面を開く)
//   PawnIO のインストールの動作は UI/PawnIoInstallPanel、お片付けの操作は App.Uninstall.cs
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Windows;
using System.Windows.Threading;
using ProFanController.UI;

namespace ProFanController;

public partial class MainWindow
{
    /// <summary>ハードウェアの一覧ができたとき: PawnIO の状態を設定画面に伝え、なければお知らせを出す</summary>
    private void UpdateDriverStatus(HardwareCatalog catalog)
    {
        PawnIoPanel.LoadedAtStartup = catalog.PawnIoInstalled;
        PawnIoPanel.Refresh();
        if (catalog.PawnIoInstalled) return;
        ShowAlert(L("PawnIO（ファン制御用のドライバー）が見つかりません。このままでは温度の表示だけで、ファンは制御できません。")
                  + L("設定の「安全・バックアップ」から、ボタン 1 つでインストールできます。"), AlertLevel.Danger,
            L("インストールの画面を開く"), ShowDriverCard);
    }

    /// <summary>設定 →「安全・バックアップ」の PawnIO のカードを表示する（押しただけではインストールしない）</summary>
    private void ShowDriverCard()
    {
        NavSettings.IsChecked = true;
        SetTabSafety.IsChecked = true;
        // 画面の切り替えで一番上に戻るので、並べ終わってから PawnIO のカードまで送る
        Dispatcher.BeginInvoke(() => PawnIoCard.BringIntoView(), DispatcherPriority.Loaded);
    }

    /// <summary>アンインストール（お片付け）: この PC の状態を調べてから、行うことを並べた確認の画面を出す</summary>
    private async void OnCleanUninstallClick(object sender, RoutedEventArgs e)
    {
        if (PawnIoInstallPanel.Busy)
        {
            MessageBox.Show(this, L("PawnIO のインストールが終わってから、もう一度押してください。"), AppIdentity.Name,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        FlushCommit();
        CleanUninstallButton.IsEnabled = false;
        CleanupFacts facts;
        try { facts = await CurrentApp.GatherCleanupFactsAsync(); }
        finally { CleanUninstallButton.IsEnabled = true; }

        var dialog = new UninstallDialog(facts, AppContext.BaseDirectory, CurrentApp.RunCleanupAsync) { Owner = this, Icon = Icon };
        dialog.ShowDialog();
        if (dialog.ExitRequested) CurrentApp.FinishCleanUninstall(dialog.DeleteDataAfterExit);
    }
}

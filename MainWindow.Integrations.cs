// ==================================================
// MainWindow.Integrations.cs - 設定画面の「通知・連携」「安全・バックアップ」
//   (スマホ通知・LED の温度センサーの候補・設定の書き出し/読み込み・バックアップ・部品交換の対応付け)
//   LED のライティング（OpenRGB 連携）の画面そのものは MainWindow.Lighting.cs
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ProFanController.UI;

namespace ProFanController;

public partial class MainWindow
{
    private static App CurrentApp => (App)Application.Current;

    /// <summary>設定画面の、状態を表す表示（バックアップ・通知・RGB）を最新にする</summary>
    private async void RefreshSettingsInfo()
    {
        // バックアップの一覧はフォルダを読むので別スレッドで（画面を開いた瞬間に止まらないように）
        try
        {
            var backups = await Task.Run(ConfigStore.ListBackups);
            BackupInfo.Text = backups.Count == 0
                ? L("まだバックアップはありません")
                : L($"最新のバックアップ: {backups[0].Display} ・ 全部で {backups.Count} 個（{ConfigStore.BackupDirectory}）");
        }
        catch (Exception ex)
        {
            Logger.Warn($"設定のバックアップの一覧を読めません: {ex.Message}");
        }
        if (CurrentApp.Notifier.LastResult.Length > 0) NotifyStatus.Text = CurrentApp.Notifier.LastResult;
        UpdateRgbStatus();
        UpdateNtfyQr();
    }

    // =====================================================================
    // スマホ通知
    // =====================================================================

    private string? _qrTopic;

    private void UpdateNtfyQr()
    {
        string url = Notifier.NtfySubscribeUrl(Config.Notify);
        if (url == _qrTopic) return;
        _qrTopic = url;
        NtfyQr.Source = CreateQr(url);
    }

    private void OnNotifySettingLostFocus(object sender, RoutedEventArgs e)
    {
        Config.Notify.NtfyTopic = Config.Notify.NtfyTopic.Trim();
        Config.Notify.NtfyServer = Config.Notify.NtfyServer.Trim();
        if (Config.Notify.NtfyTopic.Length == 0) Config.Notify.NtfyTopic = NotifyConfig.NewTopic();
        UpdateNtfyQr();
    }

    private void OnNewTopicClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, L("トピック名を作り直しますか？\n\nスマホの ntfy アプリでは、新しいトピック名を購読し直す必要があります。"),
            AppIdentity.Name, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        Config.Notify.NtfyTopic = NotifyConfig.NewTopic();
        UpdateNtfyQr();
    }

    private async void OnTestNotifyClick(object sender, RoutedEventArgs e)
    {
        FlushCommit();
        NotifyStatus.Text = L("送信しています…");
        try { NotifyStatus.Text = await CurrentApp.Notifier.TestAsync(Config.Notify); }
        catch (Exception ex)
        {
            Logger.Warn($"通知のテストに失敗: {ex}");
            NotifyStatus.Text = L($"送れませんでした: {ex.Message}");
        }
    }

    // =====================================================================
    // OpenRGB（温度モードで使うセンサーの候補。画面はライティングのページ）
    // =====================================================================

    /// <summary>「温度」モードで色を決めるセンサーの候補を作り直す（センサーの一覧が変わったとき）</summary>
    private void RebuildRgbSensorOptions()
    {
        var options = new List<RemapOption> { new("", L("自動（CPU と GPU の高いほう）")) };
        options.AddRange(_sensorOptions.Where(o => !o.Id.StartsWith(VirtualSensorConfig.IdPrefix)).Select(o => new RemapOption(o.Id, o.Display)));
        options.AddRange(_sensorOptions.Where(o => o.Id.StartsWith(VirtualSensorConfig.IdPrefix)).Select(o => new RemapOption(o.Id, o.Display)));
        RgbSensorCombo.ItemsSource = options;
    }

    /// <summary>センサーごとの、最後に正しく読めた温度と時刻（LED の温度モード用）</summary>
    private readonly Dictionary<string, (double Value, long AtMs)> _rgbLastGood = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>読めなくなっても、この時間（ミリ秒）は最後に読めた値を使う</summary>
    private const long RgbTempHoldMs = 10_000;

    /// <summary>
    /// LED の色を決める温度（設定がなければ CPU と GPU の高いほう）。App から毎周期呼ばれる。
    /// 一瞬読めなかったセンサーは 10 秒まで前の値を使い、ありえない値（127 °C など）は使わない
    /// （高いほうのセンサーが 1 回読めないだけで、低いほうの温度に色が跳ばないように）
    /// </summary>
    public double? RgbTemperature(EngineSnapshot s)
    {
        long now = Environment.TickCount64;
        double? Read(string id)
        {
            if (s.Values.TryGetValue(id, out var x) && x is float f && FanSafety.IsPlausibleTemp(f))
            {
                _rgbLastGood[id] = (f, now);
                return f;
            }
            return _rgbLastGood.TryGetValue(id, out var g) && now - g.AtMs <= RgbTempHoldMs ? g.Value : null;
        }

        if (Config.Rgb.SensorId.Length > 0) return Read(Config.Rgb.SensorId);
        double? best = null;
        foreach (var role in new[] { StatRoles.CpuTemp, StatRoles.GpuTemp })
        {
            if (_roles.TryGetValue(role, out var id) && Read(id) is double v) best = best is double b ? Math.Max(b, v) : v;
        }
        return best;
    }

    // =====================================================================
    // バックアップ・書き出し・読み込み
    // =====================================================================

    private void OnBackupNowClick(object sender, RoutedEventArgs e)
    {
        FlushCommit();
        string? path = ConfigStore.Backup("manual");
        RefreshSettingsInfo();
        if (path is not null) ShowAlert(L($"設定をバックアップしました: {Path.GetFileName(path)}"), AlertLevel.Info);
    }

    private void OnRestoreBackupClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = RestoreBackupButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 420 };
        foreach (var b in ConfigStore.ListBackups())
        {
            var item = new MenuItem { Header = b.Display };
            item.Click += (_, _) => RestoreBackup(b);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = L("バックアップがありません"), IsEnabled = false });
        menu.IsOpen = true;
    }

    private void RestoreBackup(ConfigBackupInfo backup)
    {
        var answer = MessageBox.Show(this, L($"{backup.Display} の設定に戻しますか？\n\n今の設定もバックアップしてから戻すので、あとで元に戻せます。"),
            L("バックアップから戻す"), MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            FlushCommit();
            var cfg = AppConfig.FromJson(File.ReadAllText(backup.Path));
            ConfigStore.Backup("restore");
            _controller.ReplaceConfig(cfg);
            ShowAlert(L($"{backup.Display} の設定に戻しました（「元に戻す」で取り消せます）。"), AlertLevel.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L($"このバックアップは読み込めませんでした。\n{ex.Message}"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExportSettingsClick(object sender, RoutedEventArgs e)
    {
        FlushCommit();
        var dialog = new SaveFileDialog
        {
            Title = L("設定を書き出す"),
            Filter = L("Fanlume の設定 (*.fanlume.json)|*.fanlume.json"),
            FileName = L($"Fanlume設定_{Environment.MachineName}_{DateTime.Now:yyyyMMdd}{ConfigTransfer.Extension}"),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var pkg = ConfigTransfer.Export(Config, _catalog, Config.Profiles.Select(p => p.Name));
            ConfigTransfer.Save(pkg, dialog.FileName);
            ShowAlert(L($"{pkg.Profiles.Count} 個のプロファイルを書き出しました。このファイルを相手に送り、設定 →「安全・バックアップ」→「設定を読み込む」で読み込んでもらってください。"), AlertLevel.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L($"書き出せませんでした。\n{ex.Message}"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnImportSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_catalog is null)
        {
            MessageBox.Show(this, L("ハードウェアの初期化が終わるまでお待ちください。"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        FlushCommit();
        var dialog = new OpenFileDialog
        {
            Title = L("設定を読み込む"),
            Filter = L("Fanlume の設定 (*.fanlume.json;*.json)|*.fanlume.json;*.json"),
        };
        if (dialog.ShowDialog(this) != true) return;

        SettingsPackage pkg;
        try
        {
            pkg = ConfigTransfer.Load(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L($"このファイルは読み込めません。\n{ex.Message}"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var map = ConfigTransfer.ProposeMapping(pkg, _catalog);
        var remap = RemapDialog.ForImport(pkg, map, _catalog);
        remap.Owner = this;
        remap.Icon = Icon;
        if (remap.ShowDialog() != true) return;

        ConfigStore.Backup("import");
        var cfg = ConfigTransfer.Merge(Config, pkg, remap.SelectedProfiles, map, _catalog, remap.ReplaceSameName, out string summary);
        _controller.ReplaceConfig(cfg);
        Logger.Info($"設定を読み込み: {dialog.FileName}: {summary}");
        ShowAlert(summary + L("。「元に戻す」で取り消せます。読み込んだファンは、カーブを確認してから使ってください。"), AlertLevel.Info,
            L("カーブを確認"), () => NavCurves.IsChecked = true);
    }

    // =====================================================================
    // 部品の交換
    // =====================================================================

    /// <summary>部品の交換を検知したとき（App から呼ばれる）。対応付けの画面を出して、設定を新しいセンサーに置き換える</summary>
    public void ShowHardwareChange(HardwareChangeReport report)
    {
        if (_catalog is null) return;
        var dialog = RemapDialog.ForHardwareChange(report, _catalog);
        dialog.Owner = this;
        dialog.Icon = Icon;
        if (dialog.ShowDialog() == true)
        {
            FlushCommit();
            ConfigStore.Backup("remap");
            var cfg = Config.Clone();
            HardwareChange.Apply(cfg, report.Items, _catalog);
            HardwareChange.Remember(cfg, _catalog, force: true);
            _controller.ReplaceConfig(cfg);
            Logger.Info("部品の交換に合わせて設定を置き換えました:\n" + string.Join("\n", report.Items.Select(i => $"  {i.OldDisplay} → {i.NewId ?? "(なし)"}")));
            ShowAlert(L("新しいハードウェアに合わせて設定を置き換えました（「元に戻す」で戻せます）。念のため、ファンカーブを確認してください。"), AlertLevel.Info,
                L("カーブを確認"), () => NavCurves.IsChecked = true);
        }
        else if (dialog.WizardRequested)
        {
            OpenWizard();
        }
        else
        {
            ShowAlert(L("ハードウェアの変更があります。見つからないセンサーを使っているカーブは、安全のため一定の回転数で回しています。"), AlertLevel.Warning,
                L("対応付けを開く"), () => ShowHardwareChange(HardwareChange.Detect(Config, _catalog!)));
        }
    }
}

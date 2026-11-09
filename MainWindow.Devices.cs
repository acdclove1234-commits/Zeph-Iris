// ==================================================
// MainWindow.Devices.cs - デバイス画面 (ハードウェアのカード表示と、カードの画像の差し替え)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ProFanController.UI;

namespace ProFanController;

public partial class MainWindow
{
    private readonly ObservableCollection<DeviceCardVm> _deviceCards = new();

    private static string ImageDirectory => Path.Combine(AppContext.BaseDirectory, "images");

    private void RebuildDevices()
    {
        if (_catalog is null) return;
        _deviceCards.Clear();
        // マザーボード → CPU → GPU → その他 の順に並べる
        foreach (var d in _catalog.Devices.OrderBy(d => d.Kind switch { "Motherboard" => 0, "Cpu" => 1, "Gpu" => 2, _ => 3 }))
        {
            var setting = Config.DeviceImages.FirstOrDefault(i => i.DeviceKey == d.Key);
            _deviceCards.Add(new DeviceCardVm { Info = d, Image = setting is null ? null : LoadImage(Path.Combine(ImageDirectory, setting.FileName)) });
        }
        if (_lastSnapshot is not null)
        {
            foreach (var c in _deviceCards) c.Update(_lastSnapshot.Values);
        }
    }

    /// <summary>ファイルをロックしないよう、読み込み終わったら手放す形で開く</summary>
    private static ImageSource? LoadImage(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 400; // 大きな写真でもメモリを食わないように縮小して読む
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Logger.Warn($"画像を読み込めません: {path}: {ex.Message}");
            return null;
        }
    }

    private void OnDeviceImageClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DeviceCardVm card } button) return;

        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var pick = new MenuItem { Header = L("画像を選ぶ…") };
        pick.Click += (_, _) => PickDeviceImage(card);
        menu.Items.Add(pick);
        if (card.Image is not null)
        {
            var reset = new MenuItem { Header = L("イラストに戻す") };
            reset.Click += (_, _) => SetDeviceImage(card, null);
            menu.Items.Add(reset);
        }
        menu.IsOpen = true;
    }

    private void PickDeviceImage(DeviceCardVm card)
    {
        var dialog = new OpenFileDialog
        {
            Title = L($"「{card.Name}」の画像を選ぶ"),
            Filter = L("画像ファイル (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp"),
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            // 元のファイルを消したり移動したりしても表示が消えないよう、exe の隣の images\ にコピーしておく
            Directory.CreateDirectory(ImageDirectory);
            string fileName = $"{Guid.NewGuid():N}{Path.GetExtension(dialog.FileName).ToLowerInvariant()}";
            File.Copy(dialog.FileName, Path.Combine(ImageDirectory, fileName));
            SetDeviceImage(card, fileName);
        }
        catch (Exception ex)
        {
            Logger.Error($"画像のコピーに失敗: {ex}");
            MessageBox.Show(this, L($"画像を設定できませんでした。\n{ex.Message}"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetDeviceImage(DeviceCardVm card, string? fileName)
    {
        var old = Config.DeviceImages.FirstOrDefault(i => i.DeviceKey == card.Info.Key);
        if (old is not null)
        {
            Config.DeviceImages.Remove(old);
            // 元に戻す（Ctrl+Z）で使う可能性があるので、古い画像ファイルは消さずに残す
        }
        if (fileName is not null)
        {
            Config.DeviceImages.Add(new DeviceImageConfig { DeviceKey = card.Info.Key, FileName = fileName });
        }
        card.Image = fileName is null ? null : LoadImage(Path.Combine(ImageDirectory, fileName));
        ScheduleCommit();
    }

    private void OnDeviceSensorsClick(object sender, RoutedEventArgs e) => NavSensors.IsChecked = true;
}

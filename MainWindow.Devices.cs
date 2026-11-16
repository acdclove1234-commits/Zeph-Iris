// ==================================================
// MainWindow.Devices.cs - デバイス画面 (ハードウェアのカード表示と、カードの画像の差し替え・貼り付け)
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
            var card = new DeviceCardVm { Info = d };
            ShowImage(card, setting);
            _deviceCards.Add(card);
        }
        if (_lastSnapshot is not null)
        {
            foreach (var c in _deviceCards) c.Update(_lastSnapshot.Values);
        }
    }

    private static void ShowImage(DeviceCardVm card, DeviceImageConfig? setting)
    {
        card.Image = setting is null ? null : LoadImage(Path.Combine(ImageDirectory, setting.FileName));
        // 前の版がネットから取ってきた写真は、作者とライセンスを出し続ける（ライセンスが空なら、元のサイトの名前）
        card.Credit = setting is null || setting.SourceUrl.Length == 0 ? ""
            : L($"写真: {(setting.Author.Length > 0 ? setting.Author : L("作者不明"))}（{(setting.License.Length > 0 ? setting.License : SourceSite(setting.SourceUrl))}）");
    }

    private static string SourceSite(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

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
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add(L("コピーした画像を貼り付け"), () => _ = PasteDeviceImageAsync(card));
        Add(L("ファイルから選ぶ…"), () => PickDeviceImage(card));
        if (card.Image is not null) Add(L("イラストに戻す"), () => SetDeviceImage(card, null));
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
            SetDeviceImage(card, new DeviceImageConfig { DeviceKey = card.Info.Key, FileName = fileName });
        }
        catch (Exception ex)
        {
            Logger.Error($"画像のコピーに失敗: {ex}");
            MessageBox.Show(this, L($"画像を設定できませんでした。\n{ex.Message}"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// クリップボードの画像を使う（ブラウザーで画像を右クリック →「画像をコピー」したもの、画像ファイル）。
    /// このアプリは管理者として動くため、ふだんの権限のブラウザーからはドラッグ＆ドロップできないので、貼り付けで受け取る
    /// </summary>
    private async Task PasteDeviceImageAsync(DeviceCardVm card)
    {
        try
        {
            byte[]? bytes = null;
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bmp)
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var ms = new MemoryStream();
                enc.Save(ms);
                bytes = ms.ToArray();
            }
            else if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } files && File.Exists(files[0]))
            {
                var info = new FileInfo(files[0]!);
                if (info.Length <= DeviceImageFile.MaxBytes) bytes = await File.ReadAllBytesAsync(info.FullName);
            }
            if (bytes is null || DeviceImageFile.Extension(bytes) is not { } ext)
            {
                MessageBox.Show(this, L("クリップボードに画像がありません。ブラウザーで画像を右クリックして「画像をコピー」を選んでから、もう一度押してください。"),
                    AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SetDeviceImage(card, SaveImage(card, bytes, ext));
        }
        catch (Exception ex)
        {
            Logger.Warn($"画像の貼り付けに失敗: {ex.Message}");
            MessageBox.Show(this, L($"画像を貼り付けられませんでした。\n{ex.Message}"), AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static DeviceImageConfig SaveImage(DeviceCardVm card, byte[] bytes, string ext)
    {
        Directory.CreateDirectory(ImageDirectory);
        string fileName = $"{Guid.NewGuid():N}{ext}";
        File.WriteAllBytes(Path.Combine(ImageDirectory, fileName), bytes);
        return new DeviceImageConfig { DeviceKey = card.Info.Key, FileName = fileName };
    }

    private void SetDeviceImage(DeviceCardVm card, DeviceImageConfig? image)
    {
        var old = Config.DeviceImages.FirstOrDefault(i => i.DeviceKey == card.Info.Key);
        if (old is not null)
        {
            Config.DeviceImages.Remove(old);
            // 元に戻す（Ctrl+Z）で使う可能性があるので、古い画像ファイルは消さずに残す
        }
        if (image is not null) Config.DeviceImages.Add(image);
        ShowImage(card, image);
        ScheduleCommit();
    }

    private void OnDeviceSensorsClick(object sender, RoutedEventArgs e) => NavSensors.IsChecked = true;
}

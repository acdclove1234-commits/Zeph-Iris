// ==================================================
// MainWindow.Devices.cs - デバイス画面 (ハードウェアのカード表示と、カードの画像の差し替え・写真を探す・貼り付け)
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
    private bool _photoSearchRunning;

    private static string ImageDirectory => Path.Combine(AppContext.BaseDirectory, "images");

    private void RebuildDevices()
    {
        if (_catalog is null) return;
        PruneUnsuitablePhotos();
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
        DevicePhotoSwitch.IsChecked = Config.AutoDevicePhotos;
        _ = AutoFindPhotosAsync();
    }

    private static void ShowImage(DeviceCardVm card, DeviceImageConfig? setting)
    {
        card.Image = setting is null ? null : LoadImage(Path.Combine(ImageDirectory, setting.FileName));
        card.Credit = setting is null || setting.SourceUrl.Length == 0 ? ""
            : L($"写真: {(setting.Author.Length > 0 ? setting.Author : L("作者不明"))}（{(setting.License.Length > 0 ? setting.License : "Wikimedia Commons")}）");
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
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add(L("ネットで写真を探す…"), () => ShowPhotoPicker(card));
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
    /// クリップボードの画像を使う（ブラウザーで画像を右クリック →「画像をコピー」したもの、画像のアドレス、画像ファイル）。
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
                if (info.Length <= DevicePhotos.MaxImageBytes) bytes = await File.ReadAllBytesAsync(info.FullName);
            }
            else if (Clipboard.ContainsText() && Clipboard.GetText().Trim() is { Length: > 0 } text && text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                bytes = await DevicePhotos.DownloadAsync(text, cts.Token);
            }
            if (bytes is null || DevicePhotos.ImageExtension(bytes) is not { } ext)
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

    /// <summary>
    /// 前の版が選んだネットの写真のうち、製品の写真ではないもの（動画の 1 コマ・グラフなど。DevicePhotos.IsUnsuitableTitle）を外し、
    /// もう一度探せるようにする（例: CPU の写真に、レビュー動画のベンチマークのグラフが付いていた）
    /// </summary>
    private void PruneUnsuitablePhotos()
    {
        var bad = Config.DeviceImages.Where(i => DevicePhotos.IsUnsuitableSource(i.SourceUrl)).ToList();
        foreach (var i in bad)
        {
            Config.DeviceImages.Remove(i);
            Config.DevicePhotoSearched.Remove(i.DeviceKey);
            Logger.Info($"製品の写真ではない画像を外しました（{i.DeviceKey}）: {i.SourceUrl}");
        }
        if (bad.Count > 0) ScheduleCommit();
    }

    private static DeviceImageConfig SaveImage(DeviceCardVm card, byte[] bytes, string ext, PhotoCandidate? from = null)
    {
        Directory.CreateDirectory(ImageDirectory);
        string fileName = $"{Guid.NewGuid():N}{ext}";
        File.WriteAllBytes(Path.Combine(ImageDirectory, fileName), bytes);
        return new DeviceImageConfig
        {
            DeviceKey = card.Info.Key, FileName = fileName,
            SourceUrl = from?.PageUrl ?? "", Author = from?.Author ?? "", License = from?.License ?? "",
        };
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

    // =====================================================================
    // 写真を探す（Wikimedia Commons）
    // =====================================================================

    private void OnDevicePhotoSwitchClick(object sender, RoutedEventArgs e)
    {
        Config.AutoDevicePhotos = DevicePhotoSwitch.IsChecked == true;
        ScheduleCommit();
        if (Config.AutoDevicePhotos) _ = AutoFindPhotosAsync();
    }

    /// <summary>
    /// 画像のないデバイスの写真を、型番で自動で探す（デバイスごとに 1 回だけ）。確かに同じ製品と分かる写真があれば使い、
    /// なければイラストのまま（カードの「画像を変更」から候補を見たり、貼り付けたりできる）
    /// </summary>
    private async Task AutoFindPhotosAsync()
    {
        if (!Config.AutoDevicePhotos || _photoSearchRunning) return;
        var todo = _deviceCards.Where(c => c.Image is null && !Config.DevicePhotoSearched.Contains(c.Info.Key)).ToList();
        if (todo.Count == 0) return;
        _photoSearchRunning = true;
        try
        {
            foreach (var card in todo)
            {
                PhotoCandidate? best = null;
                byte[]? bytes = null;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var found = await DevicePhotos.SearchAsync(card.Name, cts.Token);
                    best = found.FirstOrDefault(c => DevicePhotos.IsConfident(c, card.Name));
                    if (best is not null) bytes = await DevicePhotos.DownloadAsync(best.ImageUrl, cts.Token);
                }
                catch (Exception ex)
                {
                    // つながらないときは、次に起動したときにもう一度探す（探し終えた印を付けない）
                    Logger.Info($"デバイスの写真を探せません（{card.Name}）: {ex.Message}");
                    continue;
                }
                Config.DevicePhotoSearched.Add(card.Info.Key);
                if (best is not null && bytes is not null && DevicePhotos.ImageExtension(bytes) is { } ext
                    && card.Image is null && _deviceCards.Contains(card))
                {
                    SetDeviceImage(card, SaveImage(card, bytes, ext, best));
                    Logger.Info($"デバイスの写真を設定: {card.Name} ← {best.Title}（{best.License}）");
                }
                else
                {
                    ScheduleCommit();
                }
            }
        }
        finally
        {
            _photoSearchRunning = false;
        }
    }

    /// <summary>候補の写真を並べて選ぶ画面（候補がなければ、ブラウザーで探して貼り付ける案内）</summary>
    private void ShowPhotoPicker(DeviceCardVm card)
    {
        var win = new Window
        {
            Title = L($"「{card.Name}」の写真"),
            Owner = this, Width = 760, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("BgBrush"), ShowInTaskbar = false,
        };
        var root = new DockPanel { Margin = new Thickness(18) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("SubTextBrush"), Margin = new Thickness(0, 0, 0, 10) };
        status.Text = L("Wikimedia Commons（自由に使える写真の共有サイト）で、型番の写真を探しています…");
        DockPanel.SetDock(status, Dock.Top);
        root.Children.Add(status);

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        Button Btn(string text, Style? style = null)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 7, 14, 7) };
            if (style is not null) b.Style = style;
            buttons.Children.Add(b);
            return b;
        }
        var browser = Btn(L("ブラウザーで画像を探す"), (Style)FindResource("GhostButton"));
        browser.ToolTip = L("型番で画像検索を開きます。気に入った画像を右クリック →「画像をコピー」して、下の「コピーした画像を貼り付け」を押してください");
        browser.Click += (_, _) =>
        {
            try { SystemTool.OpenUrl(DevicePhotos.BrowserSearchUrl(card.Name)); }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        var paste = Btn(L("コピーした画像を貼り付け"), (Style)FindResource("AccentButton"));
        paste.Click += async (_, _) =>
        {
            await PasteDeviceImageAsync(card);
            if (card.Image is not null) win.Close();
        };
        var close = Btn(L("閉じる"), (Style)FindResource("GhostButton"));
        close.Click += (_, _) => win.Close();
        root.Children.Add(buttons);

        var list = new WrapPanel();
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        win.Content = root;
        win.Loaded += async (_, _) =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var found = await DevicePhotos.SearchAsync(card.Name, cts.Token);
                if (found.Count == 0)
                {
                    status.Text = L("Wikimedia Commons に、この型番の写真は見つかりませんでした。「ブラウザーで画像を探す」で画像をコピーして、「コピーした画像を貼り付け」で設定できます（メーカーのサイトの写真などは、自分で見るためだけに使ってください）。");
                    return;
                }
                status.Text = L("使う写真を選んでください（Wikimedia Commons の写真です。作者とライセンスはカードに表示します）。見つからなければ、ブラウザーで探して貼り付けることもできます。");
                foreach (var c in found.Take(12)) list.Children.Add(PhotoTile(c, async () =>
                {
                    status.Text = L("写真を取ってきています…");
                    using var dl = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var bytes = await DevicePhotos.DownloadAsync(c.ImageUrl, dl.Token);
                    if (bytes is null || DevicePhotos.ImageExtension(bytes) is not { } ext)
                    {
                        status.Text = L("写真を取ってこられませんでした。別の写真を選ぶか、貼り付けてください。");
                        return;
                    }
                    SetDeviceImage(card, SaveImage(card, bytes, ext, c));
                    win.Close();
                }));
            }
            catch (Exception ex)
            {
                status.Text = L($"写真を探せませんでした（{ex.Message}）。「ブラウザーで画像を探す」で探して、貼り付けることもできます。");
            }
        };
        win.ShowDialog();
    }

    private FrameworkElement PhotoTile(PhotoCandidate c, Func<Task> choose)
    {
        var img = new Image { Height = 120, Stretch = Stretch.Uniform };
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(c.ThumbUrl);
            bmp.DecodePixelHeight = 120;
            bmp.EndInit();
            img.Source = bmp;
        }
        catch { /* 見本が出なくても選べる */ }
        var panel = new StackPanel { Width = 210 };
        panel.Children.Add(img);
        panel.Children.Add(new TextBlock { Text = c.Title, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), MaxHeight = 34 });
        panel.Children.Add(new TextBlock
        {
            Text = L($"{c.Author} ・ {c.License}"), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11,
            Foreground = (Brush)FindResource("MutedBrush"),
        });
        var button = new Button { Content = panel, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(8), Style = (Style)FindResource("GhostButton"), ToolTip = c.PageUrl };
        button.Click += async (_, _) => await choose();
        return button;
    }

    private void OnDeviceSensorsClick(object sender, RoutedEventArgs e) => NavSensors.IsChecked = true;
}

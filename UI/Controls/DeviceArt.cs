// ==================================================
// DeviceArt.cs - デバイスのイラスト (マザーボード / CPU / GPU をベクターで描画。画像を指定すればそれを表示)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Windows;
using System.Windows.Media;

namespace ProFanController.UI.Controls;

/// <summary>
/// メーカーの製品写真は同梱できないため、種類ごとの汎用イラストを 100×100 の座標で描いて拡大縮小する。
/// Source に画像を入れると、イラストの代わりにその画像を表示する。
/// </summary>
public sealed class DeviceArt : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(DeviceArt),
        new FrameworkPropertyMetadata("Other", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DeviceNameProperty = DependencyProperty.Register(
        nameof(DeviceName), typeof(string), typeof(DeviceArt),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>デバイスの名前（メーカーの色の帯と名前を、イラストの下に描く: NVIDIA は緑・AMD は赤・Intel は青など）</summary>
    public string DeviceName
    {
        get => (string)GetValue(DeviceNameProperty);
        set => SetValue(DeviceNameProperty, value);
    }

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(DeviceArt),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>"Motherboard" / "Cpu" / "Gpu" / "Other"</summary>
    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private static readonly Color Accent = Color.FromRgb(0x7C, 0x5C, 0xFF);
    private static readonly Color Accent2 = Color.FromRgb(0x22, 0xC7, 0xFF);
    private static readonly Color Pink = Color.FromRgb(0xF4, 0x72, 0xB6);

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        if (Source is not null)
        {
            // 画像は縦横比を保って中央に収める
            double ratio = Source.Width / Math.Max(1, Source.Height);
            double w = ratio >= 1 ? ActualWidth : ActualHeight * ratio;
            double h = ratio >= 1 ? ActualWidth / ratio : ActualHeight;
            if (h > ActualHeight) { h = ActualHeight; w = h * ratio; }
            dc.DrawImage(Source, new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h));
            return;
        }

        // 100×100 の座標系で描いて、コントロールの大きさに合わせる
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(size / 100, size / 100));
        switch (Kind)
        {
            case "Motherboard": DrawMotherboard(dc); break;
            case "Gpu": DrawGpu(dc); break;
            case "Cpu": DrawCpu(dc); break;
            default: DrawChip(dc); break;
        }
        if (Brand(DeviceName) is { } brand) DrawBrand(dc, brand.Name, brand.Color);
        dc.Pop();
        dc.Pop();
    }

    /// <summary>名前からメーカーと、そのメーカーの色を決める（分からなければ null）</summary>
    public static (string Name, Color Color)? Brand(string? deviceName)
    {
        string n = deviceName ?? "";
        bool Has(string w) => n.Contains(w, StringComparison.OrdinalIgnoreCase);
        if (Has("NVIDIA") || Has("GeForce") || Has("RTX ")) return ("NVIDIA", Color.FromRgb(0x76, 0xB9, 0x00));
        if (Has("Radeon") || Has("AMD") || Has("Ryzen")) return ("AMD", Color.FromRgb(0xED, 0x1C, 0x24));
        if (Has("Intel")) return ("Intel", Color.FromRgb(0x00, 0x71, 0xC5));
        if (Has("ASRock")) return ("ASRock", Color.FromRgb(0x00, 0xA0, 0xE9));
        if (Has("ASUS") || Has("ROG")) return ("ASUS", Color.FromRgb(0xE6, 0x00, 0x12));
        if (Has("MSI") || Has("Micro-Star")) return ("MSI", Color.FromRgb(0xE4, 0x00, 0x2B));
        if (Has("Gigabyte") || Has("AORUS")) return ("GIGABYTE", Color.FromRgb(0xF2, 0x8C, 0x00));
        return null;
    }

    /// <summary>イラストの下に、メーカーの色の帯と名前を描く（100×100 の座標）</summary>
    private void DrawBrand(DrawingContext dc, string name, Color color)
    {
        var brush = Freeze(new SolidColorBrush(color));
        dc.DrawRoundedRectangle(brush, null, new Rect(4, 92, 26, 4), 2, 2);
        var text = new FormattedText(name, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 7, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(33, 89.5));
    }

    private static Brush Solid(byte r, byte g, byte b, byte a = 255) => Freeze(new SolidColorBrush(Color.FromArgb(a, r, g, b)));
    private static Brush Vertical(Color top, Color bottom) => Freeze(new LinearGradientBrush(top, bottom, 90));
    private static Brush RgbStrip() => Freeze(new LinearGradientBrush(
        new GradientStopCollection { new(Pink, 0), new(Accent, 0.5), new(Accent2, 1) }, 0));
    private static Pen Line(Brush b, double w) => Freeze(new Pen(b, w));

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private static void DrawMotherboard(DrawingContext dc)
    {
        var pcb = Vertical(Color.FromRgb(0x22, 0x25, 0x34), Color.FromRgb(0x14, 0x16, 0x20));
        dc.DrawRoundedRectangle(pcb, Line(Solid(0x34, 0x38, 0x4C), 0.8), new Rect(8, 4, 84, 92), 3, 3);

        // 電源回路のヒートシンク（左と上）＋ RGB のライン
        var sink = Vertical(Color.FromRgb(0x3A, 0x3F, 0x55), Color.FromRgb(0x24, 0x27, 0x36));
        dc.DrawRoundedRectangle(sink, null, new Rect(12, 8, 12, 42), 2, 2);
        dc.DrawRoundedRectangle(sink, null, new Rect(26, 8, 30, 9), 2, 2);
        dc.DrawRectangle(RgbStrip(), null, new Rect(13, 46, 10, 1.6));

        // CPU ソケット
        dc.DrawRoundedRectangle(Solid(0x4A, 0x50, 0x68), Line(Solid(0x6B, 0x71, 0x8A), 0.6), new Rect(30, 21, 24, 24), 1.5, 1.5);
        dc.DrawRectangle(Solid(0x2A, 0x2E, 0x40), null, new Rect(34, 25, 16, 16));

        // メモリスロット 4 本
        for (int i = 0; i < 4; i++)
            dc.DrawRoundedRectangle(Solid(0x1C, 0x1F, 0x2C), Line(Solid(0x3A, 0x3F, 0x55), 0.5), new Rect(62 + i * 6, 10, 3.2, 40), 1, 1);

        // M.2 シールド
        dc.DrawRoundedRectangle(sink, null, new Rect(28, 52, 34, 7), 1.5, 1.5);

        // PCIe スロット
        dc.DrawRoundedRectangle(Solid(0x2E, 0x32, 0x46), Line(Solid(0x4A, 0x50, 0x68), 0.5), new Rect(14, 64, 52, 3.4), 1, 1);
        dc.DrawRoundedRectangle(Solid(0x1C, 0x1F, 0x2C), Line(Solid(0x3A, 0x3F, 0x55), 0.5), new Rect(14, 78, 52, 3), 1, 1);

        // チップセットのヒートシンク（RGB で光らせる）
        dc.DrawRoundedRectangle(sink, Line(Freeze(new SolidColorBrush(Color.FromArgb(0x99, Accent.R, Accent.G, Accent.B))), 0.8), new Rect(70, 62, 18, 26), 2, 2);
        dc.DrawRectangle(RgbStrip(), null, new Rect(71, 85, 16, 1.6));
    }

    private static void DrawGpu(DrawingContext dc)
    {
        var body = Vertical(Color.FromRgb(0x30, 0x34, 0x46), Color.FromRgb(0x18, 0x1A, 0x26));
        dc.DrawRoundedRectangle(body, Line(Solid(0x45, 0x4A, 0x60), 0.8), new Rect(3, 28, 94, 42), 4, 4);
        dc.DrawRectangle(RgbStrip(), null, new Rect(8, 29.5, 84, 1.6));
        // PCIe 端子
        dc.DrawRectangle(Solid(0xB8, 0x94, 0x4A), null, new Rect(26, 70, 40, 3));

        // ファン 3 基
        foreach (double cx in new[] { 20.0, 50.0, 80.0 })
        {
            var c = new Point(cx, 49);
            dc.DrawEllipse(Solid(0x10, 0x11, 0x1A), Line(Solid(0x4A, 0x50, 0x68), 0.8), c, 13.5, 13.5);
            for (int i = 0; i < 5; i++)
            {
                dc.PushTransform(new RotateTransform(i * 72, cx, 49));
                dc.DrawEllipse(Solid(0x3A, 0x3F, 0x55), null, new Point(cx, 42.5), 3, 6.2);
                dc.Pop();
            }
            dc.DrawEllipse(Solid(0x24, 0x27, 0x36), Line(Freeze(new SolidColorBrush(Accent)), 0.6), c, 3.2, 3.2);
        }
    }

    private static void DrawCpu(DrawingContext dc)
    {
        // 基板（緑がかった色）とヒートスプレッダ（銀色）
        dc.DrawRoundedRectangle(Vertical(Color.FromRgb(0x1F, 0x3A, 0x30), Color.FromRgb(0x14, 0x26, 0x20)),
            Line(Solid(0x2E, 0x4E, 0x42), 0.8), new Rect(14, 14, 72, 72), 4, 4);
        for (int i = 0; i < 8; i++)
        {
            dc.DrawRectangle(Solid(0xB8, 0x94, 0x4A, 0xAA), null, new Rect(20 + i * 8, 16, 2, 2));
            dc.DrawRectangle(Solid(0xB8, 0x94, 0x4A, 0xAA), null, new Rect(20 + i * 8, 82, 2, 2));
        }
        var ihs = Freeze(new LinearGradientBrush(Color.FromRgb(0xC9, 0xCE, 0xDA), Color.FromRgb(0x7D, 0x83, 0x96), 45));
        dc.DrawRoundedRectangle(ihs, Line(Solid(0x5A, 0x60, 0x75), 0.8), new Rect(24, 24, 52, 52), 3, 3);
        dc.DrawRoundedRectangle(null, Line(Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))), 0.6), new Rect(28, 28, 44, 44), 2, 2);
        dc.DrawRectangle(RgbStrip(), null, new Rect(36, 66, 28, 1.6));
    }

    private static void DrawChip(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(Vertical(Color.FromRgb(0x30, 0x34, 0x46), Color.FromRgb(0x18, 0x1A, 0x26)),
            Line(Solid(0x45, 0x4A, 0x60), 0.8), new Rect(22, 22, 56, 56), 5, 5);
        for (int i = 0; i < 6; i++)
        {
            double p = 28 + i * 8.5;
            dc.DrawRectangle(Solid(0x6B, 0x71, 0x8A), null, new Rect(p, 15, 3, 7));
            dc.DrawRectangle(Solid(0x6B, 0x71, 0x8A), null, new Rect(p, 78, 3, 7));
            dc.DrawRectangle(Solid(0x6B, 0x71, 0x8A), null, new Rect(15, p, 7, 3));
            dc.DrawRectangle(Solid(0x6B, 0x71, 0x8A), null, new Rect(78, p, 7, 3));
        }
        dc.DrawRectangle(RgbStrip(), null, new Rect(34, 60, 32, 1.6));
    }
}

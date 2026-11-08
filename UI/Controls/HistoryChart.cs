// ==================================================
// HistoryChart.cs - 履歴グラフ (温度=左軸 / 回転数=右軸・期間切替・マウス位置の値表示)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ProFanController.UI.Controls;

public sealed class ChartSeries
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required SolidColorBrush Brush { get; init; }
    /// <summary>true なら右軸（rpm）、false なら左軸（°C）</summary>
    public bool IsRpm { get; init; }
    public bool Visible { get; set; } = true;
}

public sealed class HistoryChart : FrameworkElement
{
    // 背景・目盛り・文字は、いまのテーマの色（ThemePalette）を使う
    private static Brush BgBrush => ThemePalette.ChartBg;
    private static Pen GridPen => ThemePalette.ChartGrid;
    private static Brush LabelBrush => ThemePalette.ChartLabel;
    private static Pen CursorPen => ThemePalette.Cached("history.cursor", c => new Pen(new SolidColorBrush(ThemeManager.WithAlpha(c.Text, 0x88)), 1));
    private static Brush TipBg => ThemePalette.TipBg;
    private static Brush TextBrush => ThemePalette.Text;
    private static FontFamily UiFont => UiFonts.Family;

    private double? _mouseX;

    public HistoryStore? Store { get; set; }
    public IReadOnlyList<ChartSeries> Series { get; set; } = Array.Empty<ChartSeries>();
    public TimeSpan Range { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>false のときは目盛りや凡例を省いた小さめの表示</summary>
    public bool ShowRpmAxis { get; set; } = true;

    public HistoryChart()
    {
        ClipToBounds = true;
    }

    public void Refresh() => InvalidateVisual();

    private const double PadL = 44, PadT = 14, PadB = 26;
    /// <summary>
    /// これ以上データが空いたら線を切る（アプリを止めていた時間など）。
    /// 起動前の履歴は 1 分ごとの平均なので、1 分おきの点はつなぐ
    /// </summary>
    private const double GapSeconds = 150;
    private double PadR => ShowRpmAxis ? 56 : 14;

    /// <summary>モニターの倍率が変わった（別のモニターへ移した）ら、文字をその倍率で描き直す</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(BgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight), 12, 12);
        var r = new Rect(PadL, PadT, Math.Max(1, ActualWidth - PadL - PadR), Math.Max(1, ActualHeight - PadT - PadB));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (Store is null || Store.Count == 0) return;

        DateTime end = Store.TimeAt(Store.Count - 1);
        DateTime start = end - Range;
        int first = Store.LowerBound(start);
        int columns = Math.Max(1, (int)r.Width);
        double secPerCol = Range.TotalSeconds / columns;

        // 系列ごとに「1 ピクセル列 = 1 区間」の平均を作る
        var visible = Series.Where(s => s.Visible && Store.Has(s.Key)).ToList();
        var buckets = new Dictionary<ChartSeries, double[]>();
        foreach (var s in visible)
        {
            var sum = new double[columns];
            var cnt = new int[columns];
            for (int i = first; i < Store.Count; i++)
            {
                float v = Store.ValueAt(s.Key, i);
                if (float.IsNaN(v)) continue;
                int col = (int)((Store.TimeAt(i) - start).TotalSeconds / secPerCol);
                if (col < 0 || col >= columns) continue;
                sum[col] += v;
                cnt[col]++;
            }
            var avg = new double[columns];
            for (int c = 0; c < columns; c++) avg[c] = cnt[c] > 0 ? sum[c] / cnt[c] : double.NaN;
            buckets[s] = avg;
        }

        // 軸の範囲（温度は 10°C 単位、回転数は 500rpm 単位で丸める）
        var temps = buckets.Where(b => !b.Key.IsRpm).SelectMany(b => b.Value).Where(v => !double.IsNaN(v)).ToList();
        var rpms = buckets.Where(b => b.Key.IsRpm).SelectMany(b => b.Value).Where(v => !double.IsNaN(v)).ToList();
        double tMin = temps.Count > 0 ? Math.Floor(temps.Min() / 10) * 10 : 20;
        double tMax = temps.Count > 0 ? Math.Ceiling(temps.Max() / 10) * 10 : 80;
        if (tMax - tMin < 20) tMax = tMin + 20;
        double rMax = rpms.Count > 0 ? Math.Max(500, Math.Ceiling(rpms.Max() / 500) * 500) : 2000;

        // 目盛り
        for (int i = 0; i <= 4; i++)
        {
            double y = r.Bottom - i / 4.0 * r.Height;
            dc.DrawLine(GridPen, new Point(r.Left, y), new Point(r.Right, y));
            Text(dc, $"{tMin + (tMax - tMin) * i / 4:F0}°", new Point(r.Left - 8, y - 8), dpi, LabelBrush, alignRight: true);
            if (ShowRpmAxis && rpms.Count > 0)
                Text(dc, $"{rMax * i / 4:N0}", new Point(r.Right + 8, y - 8), dpi, LabelBrush);
        }
        for (int i = 0; i <= 4; i++)
        {
            double x = r.Left + i / 4.0 * r.Width;
            var t = start + TimeSpan.FromSeconds(Range.TotalSeconds * i / 4);
            string label = Range.TotalHours >= 2 ? t.ToString("HH:mm") : t.ToString("HH:mm:ss");
            Text(dc, label, new Point(x, r.Bottom + 6), dpi, LabelBrush, center: true);
        }

        // 線
        dc.PushClip(new RectangleGeometry(r));
        foreach (var (s, avg) in buckets)
        {
            double lo = s.IsRpm ? 0 : tMin, hi = s.IsRpm ? rMax : tMax;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                // 1 ピクセル列より記録間隔（1 秒）のほうが長いと、データのない列が交互にできる。
                // そこで線を切ると点線のようになるので、本当の欠損（GapSeconds 以上の空白）のときだけ切る
                int last = -1;
                for (int c = 0; c < columns; c++)
                {
                    if (double.IsNaN(avg[c])) continue;
                    var p = new Point(r.Left + c + 0.5, r.Bottom - (avg[c] - lo) / (hi - lo) * r.Height);
                    if (last < 0 || (c - last) * secPerCol > GapSeconds) ctx.BeginFigure(p, false, false);
                    else ctx.LineTo(p, true, true);
                    last = c;
                }
            }
            geo.Freeze();
            var pen = new Pen(s.Brush, s.IsRpm ? 1.5 : 2) { LineJoin = PenLineJoin.Round };
            if (s.IsRpm) pen.DashStyle = new DashStyle(new double[] { 4, 2 }, 0);
            pen.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }
        dc.Pop();

        // マウス位置の値
        if (_mouseX is double mx && mx >= r.Left && mx <= r.Right && buckets.Count > 0)
        {
            int col = Math.Clamp((int)(mx - r.Left), 0, columns - 1);
            dc.DrawLine(CursorPen, new Point(mx, r.Top), new Point(mx, r.Bottom));
            var time = start + TimeSpan.FromSeconds((col + 0.5) * secPerCol);
            var lines = new List<(string Text, Brush Brush)> { (time.ToString("HH:mm:ss"), LabelBrush) };
            foreach (var (s, avg) in buckets)
            {
                // マウス位置の列にデータがなければ、近くの列の値を使う
                double v = Nearest(avg, col, (int)Math.Ceiling(GapSeconds / secPerCol));
                if (double.IsNaN(v)) continue;
                lines.Add(($"{s.Name}  {(s.IsRpm ? $"{v:N0} rpm" : $"{v:F1} °C")}", s.Brush));
            }
            DrawTip(dc, lines, mx, r, dpi);
        }
    }

    private static double Nearest(double[] values, int index, int reach)
    {
        for (int d = 0; d <= reach; d++)
        {
            if (index - d >= 0 && !double.IsNaN(values[index - d])) return values[index - d];
            if (index + d < values.Length && !double.IsNaN(values[index + d])) return values[index + d];
        }
        return double.NaN;
    }

    private void DrawTip(DrawingContext dc, List<(string Text, Brush Brush)> lines, double x, Rect r, double dpi)
    {
        var fts = lines.Select(l => new FormattedText(l.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(UiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 12, l.Brush, dpi)).ToList();
        double w = fts.Max(f => f.Width) + 20, h = fts.Sum(f => f.Height + 2) + 12;
        double left = x + 12 + w > r.Right ? x - 12 - w : x + 12;
        dc.DrawRoundedRectangle(TipBg, null, new Rect(left, r.Top + 6, w, h), 8, 8);
        double y = r.Top + 12;
        foreach (var ft in fts)
        {
            dc.DrawText(ft, new Point(left + 10, y));
            y += ft.Height + 2;
        }
    }

    private static void Text(DrawingContext dc, string text, Point at, double dpi, Brush brush, bool alignRight = false, bool center = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(UiFont, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal), 12, brush, dpi);
        double x = alignRight ? at.X - ft.Width : center ? at.X - ft.Width / 2 : at.X;
        dc.DrawText(ft, new Point(x, at.Y));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouseX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseX = null;
        InvalidateVisual();
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}

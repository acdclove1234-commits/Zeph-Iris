// ==================================================
// CurveEditor.cs - ドラッグで編集できるファンカーブのグラフ (ライブ動作点・回転数の目盛り付き)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ProFanController.UI.Controls;

/// <summary>
/// 操作:
///   ・点をドラッグ … 温度と Duty を変更（隣の点を追い越さない。Shift で 5 単位に吸着）
///   ・空白をダブルクリック … 点を追加
///   ・点を右クリック … 点を削除（最低 2 点は残す）
/// </summary>
public sealed class CurveEditor : FrameworkElement
{
    private const double HitRadius = 12;

    // 背景・目盛り・文字・アクセント色は、いまのテーマの色（ThemePalette）を使う
    private static Brush BgBrush => ThemePalette.ChartBg;
    private static Pen GridPen => ThemePalette.ChartGrid;
    private static Brush LabelBrush => ThemePalette.ChartLabel;
    private static Brush RpmLabelBrush => ThemePalette.Cached("curve.rpm", c => new SolidColorBrush(c.Accent2));
    private static Color Accent => ThemePalette.Accent;
    private static Color Accent2 => ThemePalette.Accent2;
    private static readonly Pen CurvePen = Freeze(new Pen(new LinearGradientBrush(Accent, Accent2, 0), 3) { LineJoin = PenLineJoin.Round });
    private static Brush FillBrush => ThemePalette.Cached("curve.fill", c => new LinearGradientBrush(ThemeManager.WithAlpha(c.Accent, 0x55), ThemeManager.WithAlpha(c.Accent2, 0x00), 90));
    private static readonly Pen LivePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)), 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Brush LiveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)));
    private static readonly Brush LiveGlow = Freeze(new SolidColorBrush(Color.FromArgb(0x44, 0x34, 0xD3, 0x99)));
    private static readonly Pen PointPen = Freeze(new Pen(new SolidColorBrush(Accent), 3));
    private static readonly Pen StopPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0xFB, 0xBF, 0x24)), 1) { DashStyle = DashStyles.Dot });
    private static readonly Brush StopZone = Freeze(new SolidColorBrush(Color.FromArgb(0x18, 0xFB, 0xBF, 0x24)));
    // 日本語（「現在」など）を確実に表示できるよう、和文フォントへのフォールバックを明示する
    private static FontFamily UiFont => UiFonts.Family;
    private static Brush TipBg => ThemePalette.TipBg;
    private static readonly Brush BandBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0xF4, 0x72, 0xB6)));
    private static readonly Pen BandPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x88, 0xF4, 0x72, 0xB6)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Brush BandLabel = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0xB6)));

    private List<CurvePoint>? _points;
    private IReadOnlyList<DutyBand>? _bands;
    private double? _liveTemp;
    private CalibrationResult? _calibration;
    private double _tMin = 20, _tMax = 100;
    private int _dragIndex = -1;
    private int _hoverIndex = -1;

    /// <summary>点の編集を始める直前（元に戻す用）</summary>
    public event EventHandler? EditStarted;
    /// <summary>点を変更し終えたとき（マウスを離した・追加・削除）に発生</summary>
    public event EventHandler? PointsChanged;

    public List<CurvePoint>? Points
    {
        get => _points;
        set
        {
            _points = value;
            _points?.Sort((a, b) => a.Temp.CompareTo(b.Temp));
            _dragIndex = _hoverIndex = -1;
            InvalidateVisual();
        }
    }

    /// <summary>現在の（平滑化後の）温度。グラフ上に動作点として表示する</summary>
    public double? LiveTemp
    {
        get => _liveTemp;
        set
        {
            if (_liveTemp == value) return;
            _liveTemp = value;
            InvalidateVisual();
        }
    }

    /// <summary>キャリブレーション結果があれば、右側に回転数の目盛りと停止域を表示する</summary>
    public CalibrationResult? Calibration
    {
        get => _calibration;
        set
        {
            _calibration = value;
            InvalidateVisual();
        }
    }

    /// <summary>共振回避帯（この Duty の範囲には留まらない）。横縞で表示する</summary>
    public IReadOnlyList<DutyBand>? AvoidBands
    {
        get => _bands;
        set
        {
            _bands = value;
            InvalidateVisual();
        }
    }

    /// <summary>true のときは表示だけ（自動調整の結果のプレビューなど）</summary>
    public bool ReadOnly { get; set; }

    /// <summary>横軸（温度）の範囲。差分センサーのときは 0〜30 などに変える</summary>
    public void SetRange(double min, double max)
    {
        _tMin = min;
        _tMax = max;
        InvalidateVisual();
    }

    public CurveEditor()
    {
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    private Thickness Pad => new(46, 18, _calibration is null ? 18 : 64, 34);

    private Rect Plot
    {
        get
        {
            var pad = Pad;
            return new Rect(pad.Left, pad.Top,
                Math.Max(1, ActualWidth - pad.Left - pad.Right), Math.Max(1, ActualHeight - pad.Top - pad.Bottom));
        }
    }

    private Point ToScreen(double temp, double duty)
    {
        var r = Plot;
        return new Point(r.Left + (temp - _tMin) / (_tMax - _tMin) * r.Width, r.Bottom - duty / 100.0 * r.Height);
    }

    private (double Temp, double Duty) FromScreen(Point p, bool snap)
    {
        var r = Plot;
        double t = _tMin + (p.X - r.Left) / r.Width * (_tMax - _tMin);
        double d = (r.Bottom - p.Y) / r.Height * 100.0;
        double step = snap ? 5 : 1;
        return (Math.Clamp(Math.Round(t / step) * step, _tMin, _tMax), Math.Clamp(Math.Round(d / step) * step, 0, 100));
    }

    /// <summary>モニターの倍率が変わった（別のモニターへ移した）ら、文字をその倍率で描き直す</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var r = Plot;
        dc.DrawRoundedRectangle(BgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight), 12, 12);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 停止域（この Duty 未満だとファンが止まる）
        if (_calibration is { StopDuty: > 0 } cal)
        {
            double y = ToScreen(_tMin, cal.StopDuty).Y;
            dc.DrawRectangle(StopZone, null, new Rect(r.Left, y, r.Width, r.Bottom - y));
            dc.DrawLine(StopPen, new Point(r.Left, y), new Point(r.Right, y));
        }

        // 共振回避帯
        foreach (var band in _bands ?? Array.Empty<DutyBand>())
        {
            double top = ToScreen(_tMin, band.To).Y, bottom = ToScreen(_tMin, band.From).Y;
            dc.DrawRectangle(BandBrush, null, new Rect(r.Left, top, r.Width, Math.Max(1, bottom - top)));
            dc.DrawLine(BandPen, new Point(r.Left, top), new Point(r.Right, top));
            dc.DrawLine(BandPen, new Point(r.Left, bottom), new Point(r.Right, bottom));
            DrawText(dc, L("共振回避"), new Point(r.Right - 6, top + 2), dpi, BandLabel, rightAlign: true);
        }

        // 目盛り
        double range = _tMax - _tMin;
        int tStep = range <= 40 ? 5 : 10;
        for (double t = Math.Ceiling(_tMin / tStep) * tStep; t <= _tMax; t += tStep)
        {
            var p = ToScreen(t, 0);
            dc.DrawLine(GridPen, new Point(p.X, r.Top), new Point(p.X, r.Bottom));
            DrawText(dc, $"{t:F0}°", new Point(p.X, r.Bottom + 8), dpi, LabelBrush, center: true);
        }
        for (int d = 0; d <= 100; d += 20)
        {
            var p = ToScreen(_tMin, d);
            dc.DrawLine(GridPen, new Point(r.Left, p.Y), new Point(r.Right, p.Y));
            DrawText(dc, $"{d}%", new Point(r.Left - 10, p.Y - 8), dpi, LabelBrush, rightAlign: true);
            if (_calibration?.EstimateRpm(d) is double rpm)
            {
                DrawText(dc, $"{rpm:N0}", new Point(r.Right + 8, p.Y - 8), dpi, RpmLabelBrush);
            }
        }
        if (_calibration is not null)
        {
            DrawText(dc, "rpm", new Point(r.Right + 8, r.Bottom + 8), dpi, RpmLabelBrush);
        }

        if (_points is null || _points.Count == 0) return;

        // カーブ本体（端の外側は水平に延長）と、その下の塗りつぶし
        var pts = new List<Point> { ToScreen(_tMin, _points[0].Duty) };
        pts.AddRange(_points.Select(p => ToScreen(p.Temp, p.Duty)));
        pts.Add(ToScreen(_tMax, _points[^1].Duty));

        var fill = new StreamGeometry();
        using (var ctx = fill.Open())
        {
            ctx.BeginFigure(new Point(pts[0].X, r.Bottom), true, true);
            foreach (var p in pts) ctx.LineTo(p, false, false);
            ctx.LineTo(new Point(pts[^1].X, r.Bottom), false, false);
        }
        fill.Freeze();
        dc.PushClip(new RectangleGeometry(r));
        dc.DrawGeometry(FillBrush, null, fill);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            foreach (var p in pts.Skip(1)) ctx.LineTo(p, true, true);
        }
        line.Freeze();
        dc.DrawGeometry(null, CurvePen, line);
        dc.Pop();

        // 現在の動作点
        if (_liveTemp is double live)
        {
            double t = Math.Clamp(live, _tMin, _tMax);
            double d = CurveMath.Interpolate(_points, t);
            var p = ToScreen(t, d);
            dc.DrawLine(LivePen, new Point(p.X, r.Top), new Point(p.X, r.Bottom));
            dc.DrawEllipse(LiveGlow, null, p, 11, 11);
            dc.DrawEllipse(LiveBrush, null, p, 5, 5);
            DrawTip(dc, L($"現在 {live:F1}°C → {d:F0}%{RpmSuffix(d)}"), p, dpi, LiveBrush);
        }

        // 編集点
        for (int i = 0; i < _points.Count; i++)
        {
            var p = ToScreen(_points[i].Temp, _points[i].Duty);
            bool active = i == _dragIndex || i == _hoverIndex;
            dc.DrawEllipse(Brushes.White, PointPen, p, active ? 8 : 6, active ? 8 : 6);
            if (active)
            {
                DrawTip(dc, $"{_points[i].Temp:F0}°C / {_points[i].Duty:F0}%{RpmSuffix(_points[i].Duty)}", p, dpi, ThemePalette.Text);
            }
        }
    }

    private string RpmSuffix(double duty)
        => _calibration?.EstimateRpm(duty) is double rpm ? L($" (約 {rpm:N0} rpm)") : "";

    private void DrawText(DrawingContext dc, string text, Point at, double dpi, Brush brush, bool center = false, bool rightAlign = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(UiFont, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal), 12, brush, dpi);
        double x = center ? at.X - ft.Width / 2 : rightAlign ? at.X - ft.Width : at.X;
        dc.DrawText(ft, new Point(x, at.Y));
    }

    private void DrawTip(DrawingContext dc, string text, Point anchor, double dpi, Brush fg)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(UiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 12, fg, dpi);
        double w = ft.Width + 16, h = ft.Height + 8;
        double x = Math.Clamp(anchor.X - w / 2, 4, Math.Max(4, ActualWidth - w - 4));
        double y = anchor.Y - h - 14;
        if (y < 4) y = anchor.Y + 14;
        dc.DrawRoundedRectangle(TipBg, null, new Rect(x, y, w, h), 6, 6);
        dc.DrawText(ft, new Point(x + 8, y + 4));
    }

    private int HitTestPoint(Point mouse)
    {
        if (_points is null) return -1;
        for (int i = 0; i < _points.Count; i++)
        {
            var p = ToScreen(_points[i].Temp, _points[i].Duty);
            if ((p - mouse).Length <= HitRadius) return i;
        }
        return -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_points is null || ReadOnly) return;
        var pos = e.GetPosition(this);
        int hit = HitTestPoint(pos);

        if (hit >= 0)
        {
            EditStarted?.Invoke(this, EventArgs.Empty);
            _dragIndex = hit;
            CaptureMouse();
        }
        else if (e.ClickCount == 2 && Plot.Contains(pos))
        {
            var (t, d) = FromScreen(pos, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            if (_points.All(p => Math.Abs(p.Temp - t) >= 1))
            {
                EditStarted?.Invoke(this, EventArgs.Empty);
                _points.Add(new CurvePoint { Temp = t, Duty = d });
                _points.Sort((a, b) => a.Temp.CompareTo(b.Temp));
                PointsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_points is null || ReadOnly) return;
        var pos = e.GetPosition(this);

        if (_dragIndex >= 0 && e.LeftButton == MouseButtonState.Pressed)
        {
            var (t, d) = FromScreen(pos, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            // 隣の点を追い越さない（温度の並び順を保つ）
            double lo = _dragIndex > 0 ? _points[_dragIndex - 1].Temp + 1 : _tMin;
            double hi = _dragIndex < _points.Count - 1 ? _points[_dragIndex + 1].Temp - 1 : _tMax;
            _points[_dragIndex].Temp = Math.Clamp(t, lo, Math.Max(lo, hi));
            _points[_dragIndex].Duty = d;
            InvalidateVisual();
            return;
        }

        int hover = HitTestPoint(pos);
        if (hover != _hoverIndex)
        {
            _hoverIndex = hover;
            Cursor = hover >= 0 ? Cursors.Hand : Cursors.Cross;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragIndex < 0) return;
        _dragIndex = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
        PointsChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (_points is null || _points.Count <= 2 || ReadOnly) return;
        int hit = HitTestPoint(e.GetPosition(this));
        if (hit < 0) return;
        EditStarted?.Invoke(this, EventArgs.Empty);
        _points.RemoveAt(hit);
        _hoverIndex = -1;
        InvalidateVisual();
        PointsChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex >= 0 && _dragIndex < 0)
        {
            _hoverIndex = -1;
            InvalidateVisual();
        }
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}

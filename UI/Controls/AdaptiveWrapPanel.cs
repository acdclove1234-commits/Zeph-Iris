// ==================================================
// AdaptiveWrapPanel.cs - 画面の幅に合わせて列の数を決め、カードを同じ幅に広げて並べるパネル
//                        (ウィンドウを広げると、列が増えるか、カードが広がる。CompleteRows なら最後の行が欠けない列の数にする)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Windows;
using System.Windows.Controls;

namespace ProFanController.UI.Controls;

/// <summary>
/// カードを左から順に並べるパネル。列の数は「幅 ÷ ItemMinWidth」（カードの数より多くはしない）で決め、
/// 1 列の幅は残りの幅を等分する（ただし ItemMaxWidth まで）。子の Margin は 1 列の幅に含む。
/// CompleteRows なら、最後の行が欠けない列の数にする（CardGrid.BalancedColumns）。割り切れないときは、各行の枚数の差を 1 枚までにする（CardGrid.RowSizes）
/// </summary>
public sealed class AdaptiveWrapPanel : Panel
{
    public static readonly DependencyProperty ItemMinWidthProperty = DependencyProperty.Register(
        nameof(ItemMinWidth), typeof(double), typeof(AdaptiveWrapPanel),
        new FrameworkPropertyMetadata(240.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemMaxWidthProperty = DependencyProperty.Register(
        nameof(ItemMaxWidth), typeof(double), typeof(AdaptiveWrapPanel),
        new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CompleteRowsProperty = DependencyProperty.Register(
        nameof(CompleteRows), typeof(bool), typeof(AdaptiveWrapPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>1 列の幅の下限（これより狭くなるなら列を減らす。子の Margin を含む）</summary>
    public double ItemMinWidth
    {
        get => (double)GetValue(ItemMinWidthProperty);
        set => SetValue(ItemMinWidthProperty, value);
    }

    /// <summary>1 列の幅の上限（カードが少なく幅が余るときも、これより広げない）</summary>
    public double ItemMaxWidth
    {
        get => (double)GetValue(ItemMaxWidthProperty);
        set => SetValue(ItemMaxWidthProperty, value);
    }

    /// <summary>
    /// 最後の行が欠けないように列の数を選ぶ（CardGrid.BalancedColumns。例: 10 枚なら 5 列 × 2 行、狭いと 2 列 × 5 行）。
    /// 列を減らした分、カードは広がる
    /// </summary>
    public bool CompleteRows
    {
        get => (bool)GetValue(CompleteRowsProperty);
        set => SetValue(CompleteRowsProperty, value);
    }

    private (int Columns, double Slot) Layout(double width, int count) =>
        CardGrid.Layout(width, count, ItemMinWidth, ItemMaxWidth, CompleteRows);

    private List<UIElement> Visible()
    {
        var list = new List<UIElement>(InternalChildren.Count);
        foreach (UIElement c in InternalChildren)
            if (c is not null && c.Visibility != Visibility.Collapsed) list.Add(c);
        return list;
    }

    protected override Size MeasureOverride(Size available)
    {
        var items = Visible();
        foreach (UIElement c in InternalChildren)
            if (c is not null && c.Visibility == Visibility.Collapsed) c.Measure(new Size(0, 0));
        if (items.Count == 0) return new Size(0, 0);
        var (cols, slot) = Layout(available.Width, items.Count);
        double height = 0;
        int start = 0;
        foreach (int n in CardGrid.RowSizes(items.Count, cols, CompleteRows))
        {
            double rowHeight = 0;
            for (int i = start; i < start + n; i++)
            {
                items[i].Measure(new Size(slot, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, items[i].DesiredSize.Height);
            }
            height += rowHeight;
            start += n;
        }
        int usedCols = Math.Min(cols, items.Count);
        return new Size(slot * usedCols, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var items = Visible();
        if (items.Count == 0) return final;
        var (cols, slot) = Layout(final.Width, items.Count);
        double y = 0;
        int start = 0;
        // 行ごとの枚数（CompleteRows なら各行の差は 1 枚まで）
        foreach (int n in CardGrid.RowSizes(items.Count, cols, CompleteRows))
        {
            int end = start + n;
            double rowHeight = 0;
            for (int i = start; i < end; i++) rowHeight = Math.Max(rowHeight, items[i].DesiredSize.Height);
            for (int i = start; i < end; i++) items[i].Arrange(new Rect((i - start) * slot, y, slot, rowHeight));
            y += rowHeight;
            start = end;
        }
        return final;
    }
}

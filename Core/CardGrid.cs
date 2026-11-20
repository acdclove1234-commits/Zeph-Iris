// ==================================================
// CardGrid.cs - カードを並べるときの列の数と 1 列の幅の計算 (画面の幅に合わせて広げる。AdaptiveWrapPanel が使う)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

public static class CardGrid
{
    /// <summary>
    /// 幅 width に count 枚を並べるときの列の数と 1 列の幅。
    /// 列の数は「幅 ÷ minWidth」（カードの数より多くはしない）、1 列の幅は幅を等分した値（maxWidth まで）
    /// </summary>
    public static (int Columns, double Slot) Layout(double width, int count, double minWidth, double maxWidth)
    {
        minWidth = Math.Max(1, minWidth);
        if (count <= 0) return (1, minWidth);
        if (double.IsInfinity(width) || double.IsNaN(width)) return (count, minWidth);
        int cols = Math.Clamp((int)Math.Floor(width / minWidth), 1, count);
        double slot = Math.Min(width / cols, Math.Max(minWidth, maxWidth));
        // 1 列しか入らないほど狭いときは、その幅に収める（はみ出して切れないように）
        if (cols == 1) slot = Math.Min(slot, Math.Max(0, width));
        return (cols, slot);
    }

    /// <summary>
    /// completeRows なら、最後の行が欠けないように列の数を選ぶ（BalancedColumns）。1 列の幅は、選んだ列の数で幅を等分した値（maxWidth まで）。
    /// 列を減らした分だけカードは広くなる（入る列の数の半分までしか減らさないので、広がるのは 2 倍まで）
    /// </summary>
    public static (int Columns, double Slot) Layout(double width, int count, double minWidth, double maxWidth, bool completeRows)
    {
        var (cols, slot) = Layout(width, count, minWidth, maxWidth);
        if (!completeRows || count <= 0 || double.IsInfinity(width) || double.IsNaN(width)) return (cols, slot);
        int balanced = BalancedColumns(count, cols);
        if (balanced == cols) return (cols, slot);
        return (balanced, Math.Min(width / balanced, Math.Max(Math.Max(1, minWidth), maxWidth)));
    }

    /// <summary>
    /// 1 行に fit 列まで入るとき、count 枚を並べる列の数。
    /// count を割り切れる列の数が fit 〜 fit の半分にあれば、そのいちばん大きい数（どの行も同じ枚数。例: 10 枚で 7 列入るなら 5 列 × 2 行、
    /// 4 列入るなら 2 列 × 5 行）。なければ、行の数は増やさない列の数のうちいちばん少ない数（各行の枚数の差は RowSizes で 1 枚までにする。
    /// 例: 7 枚で 4 列入るなら 4 列で 4 + 3、7 枚で 3 列入るなら 3 列で 3 + 2 + 2）。
    /// 割り切れる数を選ぶので、幅が少し変わっただけで列の数が大きく変わることがある（10 枚では、5 列入る幅から 4 列しか入らない幅になると
    /// 5 列 × 2 行から 2 列 × 5 行になる）。それでもカードの幅は、入る列の数で等分した幅の 2 倍までにしかならない
    /// </summary>
    public static int BalancedColumns(int count, int fit)
    {
        if (count <= 0) return 1;
        fit = Math.Clamp(fit, 1, count);
        for (int c = fit; c >= 1 && c * 2 >= fit; c--)
            if (count % c == 0) return c;
        int rows = (count + fit - 1) / fit;
        return (count + rows - 1) / rows;
    }

    /// <summary>
    /// columns 列で count 枚を並べるときの、行ごとの枚数（上の行から）。
    /// completeRows でなければ、左から順に詰める（最後の行だけ少ない）。completeRows なら、行の数はそのままで、
    /// 各行の枚数の差を 1 枚までにする（多い行を上に。例: 3 列で 7 枚なら 3 + 2 + 2。割り切れるならどの行も columns 枚）
    /// </summary>
    public static int[] RowSizes(int count, int columns, bool completeRows)
    {
        if (count <= 0) return Array.Empty<int>();
        columns = Math.Clamp(columns, 1, count);
        int rows = (count + columns - 1) / columns;
        var sizes = new int[rows];
        if (!completeRows)
        {
            for (int r = 0; r < rows; r++) sizes[r] = Math.Min(columns, count - r * columns);
            return sizes;
        }
        int q = count / rows, rem = count % rows;
        for (int r = 0; r < rows; r++) sizes[r] = q + (r < rem ? 1 : 0);
        return sizes;
    }
}

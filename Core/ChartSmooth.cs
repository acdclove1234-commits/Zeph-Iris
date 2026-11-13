// ==================================================
// ChartSmooth.cs - 履歴のグラフの線をなめらかにする計算（表示だけ。記録はそのまま）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// グラフの線を、前後の時間の平均で描くための計算。
/// 2°C 刻みでしか読めないセンサー（グラボのメモリの温度など）の行ったり来たりや、CPU の一瞬のトゲをならし、
/// ゆっくりした上がり下がりは残す
/// </summary>
public static class ChartSmooth
{
    /// <summary>平均する時間（秒）。期間が長いほど広くする（5 分なら 10 秒、30 分なら 15 秒、1 時間なら 30 秒、6 時間なら 3 分、24 時間なら 10 分）</summary>
    public static double WindowSec(TimeSpan range) => Math.Clamp(range.TotalSeconds / 120, 10, 600);

    /// <summary>グラフの 1 列が secPerCol 秒のとき、前後に何列ずつ平均するか</summary>
    public static int HalfColumns(TimeSpan range, double secPerCol) =>
        secPerCol <= 0 ? 0 : (int)Math.Round(WindowSec(range) / secPerCol / 2);

    /// <summary>
    /// 前後 half 列の平均（データのない列は数えない。データのない列はグラフでも描かないので、そのまま空ける）
    /// </summary>
    public static double[] Columns(double[] values, int half)
    {
        int n = values.Length;
        var result = new double[n];
        if (half <= 0)
        {
            Array.Copy(values, result, n);
            return result;
        }
        var sum = new double[n + 1];
        var cnt = new int[n + 1];
        for (int i = 0; i < n; i++)
        {
            bool has = !double.IsNaN(values[i]);
            sum[i + 1] = sum[i] + (has ? values[i] : 0);
            cnt[i + 1] = cnt[i] + (has ? 1 : 0);
        }
        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(values[i]))
            {
                result[i] = double.NaN;
                continue;
            }
            int lo = Math.Max(0, i - half), hi = Math.Min(n, i + half + 1);
            result[i] = (sum[hi] - sum[lo]) / (cnt[hi] - cnt[lo]);   // 自分の列があるので 0 で割らない
        }
        return result;
    }
}

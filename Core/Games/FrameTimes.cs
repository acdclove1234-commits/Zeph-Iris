// ==================================================
// FrameTimes.cs - フレーム時間（ms）を数えて、平均 FPS と 1% Low・0.1% Low を出す
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// フレーム時間の度数分布（0.1 ms きざみ、1 秒まで）。何時間遊んでも使うメモリは同じ。
///   平均 FPS   = フレーム数 ÷ かかった時間
///   1% Low     = 長いほうから 1% のところのフレーム時間を FPS にしたもの（99 パーセンタイル）
///   0.1% Low   = 同じく 0.1%（99.9 パーセンタイル）
/// 1 秒より長いフレーム（ロード画面・一時停止・最小化）は、止まっていたとみなして数えない
/// </summary>
public sealed class FrameTimes
{
    public const double BinMs = 0.1;
    public const double MaxFrameMs = 1000;
    private readonly int[] _bins = new int[(int)(MaxFrameMs / BinMs) + 1];

    public long Count { get; private set; }
    public double TotalMs { get; private set; }

    public void Add(double ms)
    {
        if (double.IsNaN(ms) || ms <= 0 || ms > MaxFrameMs) return;
        _bins[Math.Min(_bins.Length - 1, (int)(ms / BinMs))]++;
        Count++;
        TotalMs += ms;
    }

    public void Add(FrameTimes other)
    {
        for (int i = 0; i < _bins.Length; i++) _bins[i] += other._bins[i];
        Count += other.Count;
        TotalMs += other.TotalMs;
    }

    public double AverageFps => TotalMs > 0 ? Count * 1000.0 / TotalMs : double.NaN;

    /// <summary>長いほうから fraction（0.01 = 1%）のところのフレーム時間（ms）</summary>
    public double Percentile(double fraction)
    {
        if (Count == 0) return double.NaN;
        long need = Math.Max(1, (long)Math.Ceiling(Count * fraction));
        long seen = 0;
        for (int i = _bins.Length - 1; i >= 0; i--)
        {
            seen += _bins[i];
            if (seen >= need) return (i + 0.5) * BinMs;
        }
        return BinMs / 2;
    }

    public double LowFps(double fraction) => Count == 0 ? double.NaN : 1000.0 / Percentile(fraction);
}

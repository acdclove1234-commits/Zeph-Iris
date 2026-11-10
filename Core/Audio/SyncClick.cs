// ==================================================
// SyncClick.cs - 同期テストのクリック音の形（鳴らす側 SyncTestPlayer と、テストで同じ音を作るために共有する）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>同期テストのクリック音: 0.6 秒ごとに 5 ミリ秒の 1 kHz の音（なめらかに立ち上がって消える）</summary>
public static class SyncClick
{
    /// <summary>クリックの間隔（秒）</summary>
    public const double Interval = 0.6;
    /// <summary>クリック 1 回の長さ（秒）</summary>
    public const double Length = 0.005;
    public const double Frequency = 1000;
    public const double Amplitude = 0.5;

    /// <summary>鳴らし始めから frame 個目（1 チャンネル分）の音の値</summary>
    public static float Sample(long frame, int rate)
    {
        long period = (long)(Interval * rate);
        long len = (long)(Length * rate);
        long p = frame % period;
        if (p >= len) return 0;
        double t = (double)p / rate;
        double env = 0.5 - 0.5 * Math.Cos(2 * Math.PI * p / len); // 端を丸めてプツッという雑音を防ぐ
        return (float)(Amplitude * env * Math.Sin(2 * Math.PI * Frequency * t));
    }
}

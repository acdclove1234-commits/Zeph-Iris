// ==================================================
// SensorStats.cs - センサーごとの最小・最大・平均（アプリを起動してから、またはリセットしてから）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// センサーの画面に出す最小・最大・平均を、センサーごとに数える。
/// 画面を開いていないあいだも数える（ゲーム中の最高温度などを、あとで見られるように）。
/// 読めなかった値（null・NaN）は数えない
/// </summary>
public sealed class SensorStats
{
    public sealed class Entry
    {
        public float Min { get; private set; } = float.NaN;
        public float Max { get; private set; } = float.NaN;
        public int Count { get; private set; }
        private double _sum;
        public float Average => Count > 0 ? (float)(_sum / Count) : float.NaN;

        public void Add(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return;
            if (Count == 0 || v < Min) Min = v;
            if (Count == 0 || v > Max) Max = v;
            _sum += v;
            Count++;
        }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>数え始めた時刻（起動・リセット）</summary>
    public DateTime Since { get; private set; } = DateTime.Now;

    public void Add(IReadOnlyDictionary<string, float?> values)
    {
        foreach (var (id, v) in values)
        {
            if (v is not float f) continue;
            if (!_entries.TryGetValue(id, out var e)) _entries[id] = e = new Entry();
            e.Add(f);
        }
    }

    public Entry? Get(string id) => _entries.TryGetValue(id, out var e) ? e : null;

    public void Reset(DateTime now)
    {
        _entries.Clear();
        Since = now;
    }
}

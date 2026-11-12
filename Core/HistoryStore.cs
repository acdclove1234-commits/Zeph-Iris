// ==================================================
// HistoryStore.cs - 温度・回転数の履歴 (最大 24 時間のリングバッファ) と CSV 出力
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Text;

namespace ProFanController;

/// <summary>
/// 1 秒 1 サンプル × 24 時間を保持する。系列ごとに float 配列を持ち、欠損は NaN。
/// 画面スレッドからだけ使う。
/// </summary>
public sealed class HistoryStore
{
    public const int Capacity = 24 * 60 * 60;

    private readonly DateTime[] _times = new DateTime[Capacity];
    private readonly Dictionary<string, float[]> _series = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private int _head;   // 次に書き込む物理位置
    private int _count;

    public int Count => _count;

    public static string RpmKey(string controlId) => "rpm:" + controlId;
    public static string DutyKey(string controlId) => "duty:" + controlId;

    /// <summary>CSV の列名などに使う表示名を登録する</summary>
    public void SetName(string key, string name) => _names[key] = name;

    public string NameOf(string key) => _names.TryGetValue(key, out var n) ? n : key;

    public void Add(EngineSnapshot s, IEnumerable<string> temperatureIds)
    {
        int pos = _head;
        _times[pos] = s.Time;

        // 前回の値が残らないよう、この位置はいったん全系列 NaN にする
        foreach (var arr in _series.Values) arr[pos] = float.NaN;

        foreach (var id in temperatureIds)
        {
            if (s.Values.TryGetValue(id, out var v) && v.HasValue) Write(id, pos, v.Value);
        }
        foreach (var f in s.Fans)
        {
            if (f.Rpm.HasValue) Write(RpmKey(f.ControlId), pos, (float)f.Rpm.Value);
            if (f.Duty.HasValue) Write(DutyKey(f.ControlId), pos, (float)f.Duty.Value);
        }

        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>
    /// 長期の記録（1 分ごとの平均）から、起動前の履歴を埋める。ライブの記録より前に、古い順で呼ぶこと
    /// </summary>
    public void AddRaw(DateTime time, IReadOnlyDictionary<string, float> values)
    {
        if (_count > 0 && time <= TimeAt(_count - 1)) return; // 時刻の順序を崩さない
        int pos = _head;
        _times[pos] = time;
        foreach (var arr in _series.Values) arr[pos] = float.NaN;
        foreach (var (key, v) in values) Write(key, pos, v);
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    private void Write(string key, int pos, float value)
    {
        if (!_series.TryGetValue(key, out var arr))
        {
            arr = new float[Capacity];
            Array.Fill(arr, float.NaN);
            _series[key] = arr;
        }
        arr[pos] = value;
    }

    /// <summary>
    /// いまの中身の写し（画面のスレッドで作り、重い集計は別のスレッドで写しに対して行う）。
    /// 写しは記録のある分だけを古い順に詰めて持つ（24 時間分なら 1 系列 約 340 KB）
    /// </summary>
    public HistoryStore Snapshot()
    {
        var copy = new HistoryStore();
        for (int i = 0; i < _count; i++) copy._times[i] = TimeAt(i);
        foreach (var (key, src) in _series)
        {
            var dst = new float[Capacity];
            int start = Physical(0);
            int firstPart = Math.Min(_count, Capacity - start);
            Array.Copy(src, start, dst, 0, firstPart);
            if (firstPart < _count) Array.Copy(src, 0, dst, firstPart, _count - firstPart);
            if (_count < Capacity) Array.Fill(dst, float.NaN, _count, Capacity - _count);
            copy._series[key] = dst;
        }
        foreach (var (k, v) in _names) copy._names[k] = v;
        copy._count = _count;
        copy._head = _count % Capacity;
        return copy;
    }

    /// <summary>論理インデックス（0 = 最も古い）→ 物理位置</summary>
    private int Physical(int logical) => (_head - _count + logical + Capacity) % Capacity;

    public DateTime TimeAt(int logical) => _times[Physical(logical)];

    public float ValueAt(string key, int logical)
        => _series.TryGetValue(key, out var arr) ? arr[Physical(logical)] : float.NaN;

    public bool Has(string key) => _series.ContainsKey(key);

    /// <summary>指定時刻以降の最初の論理インデックス（二分探索）</summary>
    public int LowerBound(DateTime time)
    {
        int lo = 0, hi = _count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (TimeAt(mid) < time) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>指定期間を CSV で書き出す（Excel で文字化けしないよう BOM 付き UTF-8）</summary>
    public int ExportCsv(string path, DateTime from, IReadOnlyList<string> keys)
    {
        int start = LowerBound(from);
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine("Time," + string.Join(",", keys.Select(k => Quote(NameOf(k)))));

        var sb = new StringBuilder();
        for (int i = start; i < _count; i++)
        {
            sb.Clear();
            sb.Append(TimeAt(i).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            foreach (var k in keys)
            {
                float v = ValueAt(k, i);
                sb.Append(',');
                if (!float.IsNaN(v)) sb.Append(v.ToString("0.#", CultureInfo.InvariantCulture));
            }
            w.WriteLine(sb.ToString());
        }
        return _count - start;
    }

    public IEnumerable<string> Keys => _series.Keys;

    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}

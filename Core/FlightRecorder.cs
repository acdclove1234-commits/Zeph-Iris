// ==================================================
// FlightRecorder.cs - 異常の前後を自動で記録する (緊急全開・ポンプ異常などの前後 10 分を CSV と概要で保存)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Text;

namespace ProFanController;

/// <summary>保存した記録 1 件</summary>
public sealed record FlightRecord(string CsvPath, string SummaryPath, DateTime Time, string Title, string Summary)
{
    public string TimeText => $"{Time:yyyy/MM/dd HH:mm:ss}";
}

/// <summary>
/// 飛行機のフライトレコーダーのように、危険な状態になったときの前後の記録を残す。
/// 発生した瞬間には「後」のデータがないので、10 分待ってから「前 10 分＋後 10 分」をまとめて保存する。
/// 待っている間に別の異常が起きたら、同じ記録にまとめる（最長 30 分まで延長）。
/// 画面スレッドから使う（HistoryStore を読むため）。
/// </summary>
public sealed class FlightRecorder
{
    public static readonly TimeSpan Before = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan After = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxExtension = TimeSpan.FromMinutes(30);
    private const int MaxRecords = 100;

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "records");

    private DateTime? _first;
    private DateTime _saveAt;
    private string _profile = "";
    private readonly List<(DateTime Time, string Message)> _events = new();

    public bool Pending => _first is not null;
    public event Action<FlightRecord>? Saved;
    /// <summary>記録を保存できなかった（理由）</summary>
    public event Action<string>? SaveFailed;

    /// <summary>異常が起きた（記録の予約）</summary>
    public void Trigger(string message, string profile, DateTime now)
    {
        if (_first is null)
        {
            _first = now;
            _profile = profile;
        }
        _events.Add((now, message));
        var latest = now + After;
        var cap = _first.Value + MaxExtension;
        _saveAt = latest < cap ? latest : cap;
    }

    /// <summary>毎秒呼ぶ。保存の時刻になったら保存する</summary>
    public void Tick(HistoryStore history, DateTime now)
    {
        if (_first is not null && now >= _saveAt) Save(history);
    }

    /// <summary>アプリの終了時: 待っている記録があれば、その時点までのデータで保存する</summary>
    public void Flush(HistoryStore history)
    {
        if (_first is not null) Save(history);
    }

    private void Save(HistoryStore h)
    {
        var first = _first!.Value;
        var events = _events.ToList();
        _first = null;
        _events.Clear();
        if (h.Count == 0) return;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string stamp = first.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string csv = Path.Combine(Directory, $"{stamp}.csv");
            string txt = Path.Combine(Directory, $"{stamp}.txt");
            var from = first - Before;

            var keys = h.Keys
                .OrderBy(k => k.StartsWith("duty:") ? 2 : k.StartsWith("rpm:") ? 1 : 0)
                .ThenBy(h.NameOf)
                .ToList();
            int rows = h.ExportCsv(csv, from, keys);

            // 概要: 何が起きたか・最高温度・ファンの回転数
            var sb = new StringBuilder();
            sb.AppendLine(events[0].Message);
            sb.AppendLine();
            sb.AppendLine(L($"発生: {first:yyyy/MM/dd HH:mm:ss} ／ プロファイル: {_profile}"));
            sb.AppendLine(L($"記録: {from:HH:mm:ss} 〜 {h.TimeAt(h.Count - 1):HH:mm:ss}（{rows:N0} 行）"));
            if (events.Count > 1)
            {
                sb.AppendLine();
                sb.AppendLine(L("この間に起きたこと:"));
                foreach (var (t, m) in events) sb.AppendLine($"  {t:HH:mm:ss}  {m}");
            }
            sb.AppendLine();
            sb.AppendLine(L("最高値:"));
            int start = h.LowerBound(from);
            foreach (var key in keys.Where(k => !k.StartsWith("duty:")))
            {
                double max = double.MinValue;
                for (int i = start; i < h.Count; i++)
                {
                    float v = h.ValueAt(key, i);
                    if (!float.IsNaN(v)) max = Math.Max(max, v);
                }
                if (max == double.MinValue) continue;
                bool rpm = key.StartsWith("rpm:");
                if (!rpm && !h.NameOf(key).Contains("°C")) continue;
                sb.AppendLine($"  {h.NameOf(key)}: {(rpm ? $"{max:N0}" : $"{max:F1}")}");
            }
            File.WriteAllText(txt, sb.ToString(), new UTF8Encoding(true));
            Logger.Info($"異常の前後の記録を保存: {csv}");

            // 古い記録を消せなくても、いまの記録は保存できているので、保存できたとして知らせる
            try
            {
                foreach (var old in List().Skip(MaxRecords))
                {
                    File.Delete(old.CsvPath);
                    File.Delete(old.SummaryPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.Warn($"古い異常の記録を消せません: {ex.Message}"); }
            Saved?.Invoke(new FlightRecord(csv, txt, first, events[0].Message, sb.ToString()));
        }
        catch (Exception ex)
        {
            Logger.Error($"異常の記録の保存に失敗: {ex.Message}");
            SaveFailed?.Invoke(ex.Message);
        }
    }

    /// <summary>新しい順の記録一覧</summary>
    public static List<FlightRecord> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return new();
        var list = new List<FlightRecord>();
        foreach (var txt in System.IO.Directory.GetFiles(Directory, "*.txt"))
        {
            string name = Path.GetFileNameWithoutExtension(txt);
            if (!DateTime.TryParseExact(name, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;
            string summary = File.ReadAllText(txt);
            string title = summary.Split('\n')[0].Trim();
            list.Add(new FlightRecord(Path.ChangeExtension(txt, ".csv"), txt, time, title, summary));
        }
        return list.OrderByDescending(r => r.Time).ToList();
    }
}

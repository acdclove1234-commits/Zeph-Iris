// ==================================================
// GameLog.cs - ゲームの記録を data\games.jsonl（1 行 = 1 回）に残す・読む
//   この PC の中だけに置く（報告用の zip にも入れない）
// Dependencies: .NET 8 / System.Text.Json (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Text;
using System.Text.Json;

namespace ProFanController;

public static class GameLog
{
    public const string FileName = "games.jsonl";
    /// <summary>これより古い記録は、書き足すときに消す</summary>
    public const int KeepDays = 365;
    /// <summary>ふつうは記録に残さない短さ（秒。描いていた時間）</summary>
    public const double MinPlayedSec = 60;

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath => Path.Combine(LongTermStore.Directory, FileName);

    public static string ToLine(GameSession s) => JsonSerializer.Serialize(s, Options);

    public static GameSession? FromLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<GameSession>(line, Options);
            return s is { App.Length: > 0 } ? s : null;
        }
        catch (JsonException)
        {
            return null;   // 書きかけで切れた行など
        }
    }

    /// <summary>短すぎる記録（少し触っただけ）は残さない</summary>
    public static bool WorthKeeping(GameSession s) => s.PlayedSeconds >= MinPlayedSec && s.Frames > 0;

    public static void Append(GameSession s, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, ToLine(s) + "\n", new UTF8Encoding(false));
    }

    /// <summary>新しい順に読む（limit 件まで）</summary>
    public static List<GameSession> Load(string? path = null, int limit = 200)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new List<GameSession>();
        return File.ReadAllLines(path, Encoding.UTF8)
            .Select(FromLine).OfType<GameSession>()
            .OrderByDescending(s => s.Start)
            .Take(limit)
            .ToList();
    }

    /// <summary>記録を書き直す（消すとき・古いものを捨てるとき）。keep が false の記録を消す</summary>
    public static int Rewrite(Func<GameSession, bool> keep, string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return 0;
        var all = File.ReadAllLines(path, Encoding.UTF8).Select(FromLine).OfType<GameSession>().ToList();
        var kept = all.Where(keep).OrderBy(s => s.Start).ToList();
        if (kept.Count == all.Count) return 0;
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Concat(kept.Select(s => ToLine(s) + "\n")), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        return all.Count - kept.Count;
    }

    /// <summary>同じ回かどうか（消すときに使う）</summary>
    public static bool Same(GameSession a, GameSession b) =>
        a.App.Equals(b.App, StringComparison.OrdinalIgnoreCase) && a.Start == b.Start;
}

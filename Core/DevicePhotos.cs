// ==================================================
// DevicePhotos.cs - デバイスの写真を型番で探す (Wikimedia Commons の自由に使える写真。作者・ライセンスも持ってくる)
// Dependencies: .NET 8 / System.Text.Json (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProFanController;

/// <summary>写真の候補 1 枚（Wikimedia Commons のファイル）</summary>
public sealed record PhotoCandidate(string Title, string ThumbUrl, string ImageUrl, string PageUrl, string Author, string License, int Score);

/// <summary>
/// デバイスの写真を、型番で Wikimedia Commons から探す。Commons の写真は自由に使えるライセンス（CC BY-SA など）なので、
/// 作者とライセンスを一緒に保存して画面に出す。メーカーの製品写真や画像検索の結果は自動では使わない（著作権・利用規約のため。
/// 利用者が自分でブラウザから貼り付けるのは自由）。
/// 送るのはデバイスの名前（型番）だけ
/// </summary>
public static class DevicePhotos
{
    private const string Api = "https://commons.wikimedia.org/w/api.php";
    /// <summary>1 枚の写真の大きさの上限（バイト）</summary>
    public const int MaxImageBytes = 15 * 1024 * 1024;
    /// <summary>画面に出す大きさ（横のピクセル）</summary>
    public const int ImageWidth = 800;

    private static HttpClient? _http;
    private static HttpClient Http => _http ??= CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // Wikimedia の決まり: 何のソフトかと連絡先（リポジトリの URL）を User-Agent に書く
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppIdentity.Name}/{AppUpdater.CurrentVersion.ToString(3)} (https://github.com/{AppUpdater.Repository})");
        return http;
    }

    // 型番に付いてくるが、写真の題名には出てこない言葉（探す言葉から外す）
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvidia", "amd", "intel", "geforce", "radeon", "corporation", "technology", "co", "ltd", "inc", "wifi", "wi-fi", "oc", "argb", "rgb",
        "edition", "gaming", "series", "the", "with", "and", "-", "(r)", "(tm)",
    };

    /// <summary>デバイスの名前から、探す言葉（型番らしい言葉）を取り出す</summary>
    public static IReadOnlyList<string> Terms(string deviceName)
    {
        string s = Regex.Replace(deviceName ?? "", @"\(.*?\)|\[.*?\]|®|™", " ");
        // "5070Ti" → "5070 Ti"（題名では分かれていることが多い）
        s = Regex.Replace(s, @"(\d)(Ti|Super|XT|XTX)\b", "$1 $2", RegexOptions.IgnoreCase);
        return s.Split(new[] { ' ', '\t', '_', '/', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !Noise.Contains(t) && t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>型番の数字の入った言葉（"5070"・"B860"・"245KF" など）。題名にこれが全部ないと、同じ製品とはみなさない</summary>
    public static IReadOnlyList<string> KeyTerms(string deviceName) => Terms(deviceName).Where(t => t.Any(char.IsDigit)).ToList();

    /// <summary>
    /// 題名がどれくらいデバイスに合っているか。型番の数字の言葉が 1 つでも足りなければ 0。
    /// それ以外は、題名に含まれる言葉の数
    /// </summary>
    public static int Score(string title, string deviceName)
    {
        string t = Normalize(title);
        var keys = KeyTerms(deviceName);
        if (keys.Count == 0) return 0;
        foreach (var k in keys)
            if (!Variants(k).Any(v => ContainsWord(t, Normalize(v)))) return 0;
        return Terms(deviceName).Count(w => ContainsWord(t, Normalize(w)));
    }

    /// <summary>
    /// 型番の言い換え: CPU の末尾の F（内蔵 GPU なし）は同じ写真でよいので、"245KF" は "245K" でも合う
    /// （"245" だけにはしない: 245 と 245K は別の製品）
    /// </summary>
    private static IEnumerable<string> Variants(string key)
    {
        yield return key;
        if (key.Length > 3 && (key.EndsWith("F", StringComparison.OrdinalIgnoreCase)) && char.IsLetter(key[^2])) yield return key[..^1];
    }

    /// <summary>
    /// 自動で使ってよいくらい確かか: 型番の数字の言葉が全部あり、ほかの言葉（メーカー名・シリーズ名）も 1 つ以上ある
    /// （"RTX 5070 Ti" だけだと別のメーカーのグラボの写真のこともあるため）
    /// </summary>
    public static bool IsConfident(PhotoCandidate c, string deviceName)
        => c.Score >= KeyTerms(deviceName).Count + 1;

    /// <summary>候補を探す（よく合うものから並べる。合わないものは入れない）</summary>
    public static async Task<IReadOnlyList<PhotoCandidate>> SearchAsync(string deviceName, CancellationToken ct)
    {
        var terms = Terms(deviceName);
        if (KeyTerms(deviceName).Count == 0) return Array.Empty<PhotoCandidate>();
        string query = string.Join(' ', terms);
        string url = Api + "?action=query&format=json&generator=search&gsrnamespace=6&gsrlimit=20"
                     + "&prop=imageinfo&iiprop=url|extmetadata|mime&iiextmetadatafilter=Artist|LicenseShortName"
                     + $"&iiurlwidth={ImageWidth}&gsrsearch={Uri.EscapeDataString(query)}";
        using var res = await Http.GetAsync(url, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return Parse(doc.RootElement, deviceName);
    }

    /// <summary>API の返事から候補を作る（テストでも使う）</summary>
    public static IReadOnlyList<PhotoCandidate> Parse(JsonElement root, string deviceName)
    {
        var list = new List<PhotoCandidate>();
        if (!root.TryGetProperty("query", out var q) || !q.TryGetProperty("pages", out var pages)) return list;
        foreach (var page in pages.EnumerateObject())
        {
            var p = page.Value;
            string title = p.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
            if (!p.TryGetProperty("imageinfo", out var infos) || infos.GetArrayLength() == 0) continue;
            var info = infos[0];
            string mime = Str(info, "mime");
            if (mime is not ("image/jpeg" or "image/png" or "image/webp")) continue;   // 動画・PDF・SVG は使わない
            int score = Score(title, deviceName);
            if (score == 0) continue;
            string thumb = Str(info, "thumburl"), page2 = Str(info, "descriptionurl");
            if (!IsCommonsUrl(thumb) || !page2.StartsWith("https://", StringComparison.Ordinal)) continue;
            string author = "", license = "";
            if (info.TryGetProperty("extmetadata", out var meta))
            {
                author = StripHtml(MetaValue(meta, "Artist"));
                license = StripHtml(MetaValue(meta, "LicenseShortName"));
            }
            list.Add(new PhotoCandidate(CleanTitle(title), thumb, thumb, page2, author, license, score));
        }
        return list.OrderByDescending(c => c.Score).ThenBy(c => c.Title, StringComparer.Ordinal).ToList();
    }

    /// <summary>画像を取ってくる（Commons の画像・利用者が貼り付けた URL）。大きすぎるもの・画像でないものは null</summary>
    public static async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        using var res = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return null;
        if (res.Content.Headers.ContentLength is long len && len > MaxImageBytes) return null;
        await using var s = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxImageBytes) return null;
        }
        var bytes = ms.ToArray();
        return ImageExtension(bytes) is null ? null : bytes;
    }

    /// <summary>画像の種類（ファイルの先頭のバイトで見る）から拡張子を決める。画像でなければ null</summary>
    public static string? ImageExtension(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G') return ".png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P') return ".webp";
        if (b.Length >= 2 && b[0] == (byte)'B' && b[1] == (byte)'M') return ".bmp";
        if (b.Length >= 6 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F') return ".gif";
        return null;
    }

    /// <summary>ブラウザで画像検索を開く URL（利用者が自分で選んで貼り付ける用）</summary>
    public static string BrowserSearchUrl(string deviceName)
        => "https://www.bing.com/images/search?q=" + Uri.EscapeDataString(string.Join(' ', Terms(deviceName).Prepend(Brand(deviceName)).Where(s => s.Length > 0).Distinct()));

    /// <summary>名前の最初に付いているメーカー名（NVIDIA・AMD・Intel は検索の役に立つので残す）</summary>
    private static string Brand(string deviceName)
    {
        string first = (deviceName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) || first.Equals("AMD", StringComparison.OrdinalIgnoreCase)
               || first.Equals("Intel", StringComparison.OrdinalIgnoreCase) ? first : "";
    }

    /// <summary>Wikimedia の画像の置き場所（縮小画像は thumb.wikimedia.org、元の画像は upload.wikimedia.org）</summary>
    private static bool IsCommonsUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
           && u.Host is "upload.wikimedia.org" or "thumb.wikimedia.org";

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string MetaValue(JsonElement meta, string name)
        => meta.TryGetProperty(name, out var m) && m.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>"File:PNY RTX 5070 Ti at Computex 2025.jpg" → "PNY RTX 5070 Ti at Computex 2025"</summary>
    private static string CleanTitle(string title)
    {
        string t = title.StartsWith("File:", StringComparison.OrdinalIgnoreCase) ? title[5..] : title;
        int dot = t.LastIndexOf('.');
        return dot > 0 ? t[..dot] : t;
    }

    public static string StripHtml(string html)
        => System.Net.WebUtility.HtmlDecode(Regex.Replace(html ?? "", "<[^>]*>", "")).Trim();

    private static string Normalize(string s) => " " + Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim() + " ";

    private static bool ContainsWord(string normalizedHaystack, string normalizedNeedle)
    {
        string n = normalizedNeedle.Trim();
        return n.Length > 0 && normalizedHaystack.Contains(" " + n + " ", StringComparison.Ordinal);
    }
}

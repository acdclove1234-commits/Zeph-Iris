// ==================================================
// Loc.cs - 画面の言葉の翻訳（各国語対応）
//   ・元の言葉は日本語のまま書く。日本語の文そのものを見出しにして、言語ごとの翻訳表を引く
//     （表にない言葉は日本語のまま出る。翻訳が抜けても壊れない）
//   ・コードの中: L("保存しました")、値を入れる文は L($"{n} 個のファン") と書く
//     （$"..." の穴は {0} {1:F1} のような番号に置き換えて表を引き、訳した文に値を入れる）
//   ・XAML の言葉は、画面を作るときに UI/XamlLocalizer.cs が置き換える
//   ・翻訳表は Assets/Lang/<言語>.txt（1 行 1 件: 見出しのハッシュ 8 桁 TAB 訳。改行は \n）。
//     見出しの一覧は tools/LocTool が、ソースから作る（Assets/Lang/keys.tsv）
//   ・ログは作った人が読むので訳さない（日本語のまま）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

global using static ProFanController.Loc;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace ProFanController;

/// <summary>
/// 画面の言葉を訳す入口（global using static で、どこからでも L(...) / LF(...) と書ける）。
/// ほかの名前と重ならないよう、ここには L と LF だけを置く（言語の一覧・読み込みは Localization）
/// </summary>
public static class Loc
{
    /// <summary>画面に出す言葉を、いまの言語に訳す（表になければそのまま）</summary>
    public static string L(string ja) => Localization.Lookup(ja);

    /// <summary>値を入れる文: L($"{n} 個のファン") → 表の "{0} 個のファン" を引いて、値を入れる</summary>
    public static string L(ref LocString s) => s.ToStringAndClear();

    /// <summary>番号つきの文（"{0} 個のファン" など）を訳してから値を入れる</summary>
    public static string LF(string format, params object?[] args)
    {
        string f = Localization.Lookup(format);
        try { return string.Format(CultureInfo.CurrentCulture, f, args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, format, args); } // 訳の番号が壊れていても、元の文で出す
    }
}

/// <summary>画面に出せる言語</summary>
/// <param name="Code">言語の名前（config.json に書く値。BCP 47）</param>
/// <param name="NativeName">その言語での名前（言語の選択欄に出す）</param>
/// <param name="EnglishName">英語での名前</param>
/// <param name="Fonts">この言語の文字を、日本語のフォントより先に探すフォント（中国語の漢字を中国語の字形にするため など）</param>
public sealed record LanguageInfo(string Code, string NativeName, string EnglishName, string Fonts = "");

public static class Localization
{
    /// <summary>元の言葉の言語（翻訳表を引かない）</summary>
    public const string Source = "ja";

    public static readonly LanguageInfo[] Languages =
    {
        new("ja", "日本語", "Japanese"),
        new("en", "English", "English"),
        new("zh-Hans", "简体中文", "Chinese (Simplified)", "Microsoft YaHei UI, Microsoft YaHei"),
        new("zh-Hant", "繁體中文", "Chinese (Traditional)", "Microsoft JhengHei UI, Microsoft JhengHei"),
        new("ko", "한국어", "Korean", "Malgun Gothic"),
        new("ru", "Русский", "Russian"),
        new("uk", "Українська", "Ukrainian"),
        new("de", "Deutsch", "German"),
        new("fr", "Français", "French"),
        new("es", "Español", "Spanish"),
        new("it", "Italiano", "Italian"),
        new("pt-BR", "Português (Brasil)", "Portuguese (Brazil)"),
        new("pl", "Polski", "Polish"),
        new("nl", "Nederlands", "Dutch"),
        new("tr", "Türkçe", "Turkish"),
        new("vi", "Tiếng Việt", "Vietnamese"),
        new("th", "ไทย", "Thai", "Leelawadee UI, Tahoma"),
        new("id", "Bahasa Indonesia", "Indonesian"),
    };

    private static Dictionary<uint, string> _table = new();

    /// <summary>いま画面に使っている言語</summary>
    public static LanguageInfo Current { get; private set; } = Languages[0];
    public static string Language => Current.Code;
    public static bool IsSource => Current.Code == Source;
    /// <summary>読み込んだ訳の数</summary>
    public static int Count => _table.Count;

    /// <summary>
    /// 設定の値（空なら Windows の表示言語）から使う言語を決めて、翻訳表を読み込む。
    /// 起動して画面を作る前に呼ぶ。表示言語を選び直したときにも呼び、そのあと画面を作り直す（App.SwitchLanguage）
    /// </summary>
    public static void Init(string? setting, string? windowsLanguage = null)
    {
        var lang = Find(Resolve(setting, windowsLanguage ?? WindowsUiLanguage()));
        Current = lang;
        _table = lang.Code == Source ? new() : LoadEmbedded(lang.Code);
    }

    /// <summary>設定の値から、実際に使う言語の名前（「自動」なら Windows の表示言語から決めたもの）</summary>
    public static string Effective(string? setting) => Resolve(setting, WindowsUiLanguage());

    /// <summary>
    /// ほかの言語の表を引く（表示言語を選び直したときの「起動し直しますか」を、選んだ言語で出すため。
    /// 毎回その言語の表を読むので、何度も使う場所では使わない）
    /// </summary>
    public static string LookupIn(string code, string ja)
    {
        if (Find(code).Code == Source) return ja;
        if (Find(code).Code == Current.Code) return Lookup(ja);
        return LoadEmbedded(Find(code).Code).TryGetValue(Hash(ja), out var t) ? t : ja;
    }

    /// <summary>テスト用: 翻訳表を直接入れる（null なら元の言葉に戻す）</summary>
    internal static void UseTable(string code, TextReader? reader)
    {
        Current = Find(code);
        _table = code == Source || reader is null ? new() : Parse(reader);
    }

    public static LanguageInfo Find(string? code) =>
        Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) ?? Languages[0];

    /// <summary>
    /// 設定の値と Windows の表示言語（"zh-TW" など）から、使う言語の名前を決める。
    /// 対応していない言語は英語にする（日本語が読めない人のほうが多いため）
    /// </summary>
    public static string Resolve(string? setting, string? windowsLanguage)
    {
        if (!string.IsNullOrWhiteSpace(setting) && Languages.Any(l => string.Equals(l.Code, setting, StringComparison.OrdinalIgnoreCase)))
            return Find(setting).Code;
        string w = (windowsLanguage ?? "").Trim();
        if (w.Length == 0) return Source;
        if (w.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            // 繁体字: 台湾・香港・マカオと、Hant を明示したもの。それ以外（中国・シンガポール・Hans）は簡体字
            bool hant = w.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                        || w.EndsWith("-TW", StringComparison.OrdinalIgnoreCase)
                        || w.EndsWith("-HK", StringComparison.OrdinalIgnoreCase)
                        || w.EndsWith("-MO", StringComparison.OrdinalIgnoreCase);
            return hant ? "zh-Hant" : "zh-Hans";
        }
        if (w.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) return "pt-BR";
        string two = w.Split('-')[0];
        if (two.Equals("in", StringComparison.OrdinalIgnoreCase)) two = "id"; // インドネシア語の古い名前
        var match = Languages.FirstOrDefault(l => string.Equals(l.Code, two, StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? "en";
    }

    /// <summary>Windows の表示言語（コンソールの事情で置き換わらない、本来の値）</summary>
    private static string WindowsUiLanguage()
    {
        try { return new CultureInfo(GetUserDefaultUILanguage()).Name; }
        catch { return CultureInfo.InstalledUICulture.Name; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    // ----- 引く -----

    /// <summary>表を引く（なければそのまま）</summary>
    public static string Lookup(string ja)
    {
        if (_table.Count == 0 || string.IsNullOrEmpty(ja)) return ja;
        return _table.TryGetValue(Hash(ja), out var t) ? t : ja;
    }

    /// <summary>表にその言葉の訳があるか</summary>
    public static bool Has(string ja) => _table.ContainsKey(Hash(ja));

    /// <summary>
    /// 文と文をつなぐ。前の文が「。」「、」などの全角の文字で終わるとき（日本語・中国語）はそのまま、
    /// それ以外（英語・韓国語・タイ語など、文の間に空白を入れる言語）は空白を 1 つ挟む。
    /// 訳の表は行末の空白が消えやすいので、つなぎ目の空白は訳に持たせずここで足す
    /// </summary>
    public static string JoinSentences(string first, string second)
    {
        if (first.Length == 0 || second.Length == 0 || char.IsWhiteSpace(first[^1]) || char.IsWhiteSpace(second[0])) return first + second;
        return IsWide(first[^1]) ? first + second : first + " " + second;
    }

    /// <summary>
    /// 短い名前を並べるときの区切り（日本語は「・」、ほかの言語は「、」の訳。英語なら ", "）。
    /// 「・」の訳は箇条書きの印（•）なので、区切りには使わない
    /// </summary>
    public static string ListSeparator => IsSource ? "・" : Lookup("、");

    /// <summary>全角の文字（かな・漢字・全角の記号）か。これで終わる文のあとには空白を入れない</summary>
    internal static bool IsWide(char c) =>
        c is >= '　' and <= 'ヿ' or >= '㐀' and <= '䶿' or >= '一' and <= '鿿'
          or >= '豈' and <= '﫿' or >= '＀' and <= '￯';

    /// <summary>見出しのハッシュ（FNV-1a 32 ビット。UTF-16 の文字ごと）</summary>
    public static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s)
        {
            h ^= c;
            h *= 16777619;
        }
        return h;
    }

    public static string HashText(string s) => Hash(s).ToString("x8", CultureInfo.InvariantCulture);

    // ----- 翻訳表を読む -----

    private static Dictionary<uint, string> LoadEmbedded(string code)
    {
        var asm = typeof(Localization).Assembly;
        string? name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($"Lang.{code}.txt", StringComparison.OrdinalIgnoreCase));
        if (name is null) return new();
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) return new();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader);
    }

    /// <summary>翻訳表: 1 行 1 件「ハッシュ 8 桁 TAB 訳」。# で始まる行と空行は読み飛ばす。\n は改行、\t はタブ、\\ は \</summary>
    internal static Dictionary<uint, string> Parse(TextReader reader)
    {
        var table = new Dictionary<uint, string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length < 10 || line[0] == '#' || line[8] != '\t') continue;
            if (!uint.TryParse(line.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint h)) continue;
            table[h] = Unescape(line.AsSpan(9));
        }
        return table;
    }

    public static string Unescape(ReadOnlySpan<char> s)
    {
        if (s.IndexOf('\\') < 0) return s.ToString();
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => n });
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}

/// <summary>
/// 訳した言葉を含む値を、言語ごとに 1 回だけ作って使い回す（選択肢の一覧など）。
/// 表示言語を切り替えたら、次に読んだときに作り直す（static readonly に L() の結果を入れると、起動したときの言語のまま残るため）
/// </summary>
public sealed class PerLanguage<T>
{
    private sealed record Entry(string Language, T Value);
    private readonly Func<T> _make;
    private Entry? _entry;

    public PerLanguage(Func<T> make) => _make = make;

    public T Value
    {
        get
        {
            var e = _entry;
            string language = Localization.Language;
            if (e is null || e.Language != language) _entry = e = new Entry(language, _make());
            return e.Value;
        }
    }
}

/// <summary>
/// L($"...") の $"..." を受け取る。文字の部分はそのまま、穴は {0} {1:F1} のような番号にして見出しを作り、
/// 訳した文に値を入れる（string.Format と同じ書き方。文字の中の { と } は {{ }} にしておく）
/// </summary>
[InterpolatedStringHandler]
public ref struct LocString
{
    private StringBuilder? _key;
    private object?[]? _args;
    private int _count;

    public LocString(int literalLength, int formattedCount)
    {
        _key = new StringBuilder(literalLength + formattedCount * 4);
        _args = formattedCount == 0 ? Array.Empty<object?>() : new object?[formattedCount];
        _count = 0;
    }

    public void AppendLiteral(string s)
    {
        foreach (char c in s)
        {
            if (c is '{' or '}') _key!.Append(c);
            _key!.Append(c);
        }
    }

    public void AppendFormatted<T>(T value) => AppendFormatted(value, 0, null);
    public void AppendFormatted<T>(T value, string? format) => AppendFormatted(value, 0, format);
    public void AppendFormatted<T>(T value, int alignment) => AppendFormatted(value, alignment, null);

    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        _key!.Append('{').Append(_count.ToString(CultureInfo.InvariantCulture));
        if (alignment != 0) _key.Append(',').Append(alignment.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(format)) _key.Append(':').Append(format);
        _key.Append('}');
        _args![_count++] = value;
    }

    public string ToStringAndClear()
    {
        string key = _key?.ToString() ?? "";
        var args = _args ?? Array.Empty<object?>();
        _key = null;
        _args = null;
        return LF(key, args);
    }
}

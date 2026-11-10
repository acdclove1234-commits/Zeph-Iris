// ==================================================
// LocTool - 画面の言葉（翻訳の見出し）をソースから集める
//   dotnet run --project tools/LocTool -- extract        … Assets/Lang/keys.tsv を作り直す（ハッシュ TAB 見出し TAB 最初の場所）
//   dotnet run --project tools/LocTool -- lint           … L() を通していない日本語の文字列を一覧にする（ログ・no-loc と書いた行は除く。--skipped で除いたもの）
//   dotnet run --project tools/LocTool -- wrap           … 訳す文字列を L(…) で包む（--dry で数えるだけ）
//   dotnet run --project tools/LocTool -- missing en     … en.txt にない見出しを「ハッシュ TAB 見出し」で出す
//   dotnet run --project tools/LocTool -- stats          … 言語ごとに、訳してある割合
//   dotnet run --project tools/LocTool -- check          … CI 用: 訳し漏れ・keys.tsv の作り忘れ・穴の番号の違いがあれば 1 を返す
//   dotnet run --project tools/LocTool -- rename 古 新   … ソースの言葉を変えたあと（extract の前）、訳を新しい見出しへ付け替える
//   見出しの作り方は実行時（Core/Loc.cs・UI/XamlLocalizer.cs）と同じ:
//     XAML: Text / Content / ToolTip / Header / Title の値（{} で始まる値はそれを除く）、TextBox の Tag、Binding の StringFormat
//     C#: L("…") / LF("…", …) の文字列、L($"…") は穴を {0} {1:F1} にし、文字の { } を {{ }} にしたもの
// Dependencies: .NET 8 (MIT License), Microsoft.CodeAnalysis.CSharp (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

Console.OutputEncoding = new UTF8Encoding(false);
var root = FindRoot();
string cmd = args.Length > 0 ? args[0] : "extract";
var langDir = Path.Combine(root, "Assets", "Lang");

switch (cmd)
{
    case "extract":
    {
        var keys = Extract(root);
        Directory.CreateDirectory(langDir);
        var sb = new StringBuilder();
        sb.Append("# 翻訳の見出し（tools/LocTool の extract で作る。手で書き換えない）: ハッシュ TAB 見出し TAB 最初に出てくる場所\n");
        var seen = new Dictionary<uint, string>();
        int collisions = 0;
        foreach (var k in keys)
        {
            uint h = Hash(k.Key);
            if (seen.TryGetValue(h, out var other))
            {
                if (other != k.Key) { Console.Error.WriteLine($"ハッシュが重なりました: {h:x8} 「{other}」と「{k.Key}」"); collisions++; }
                continue;
            }
            seen[h] = k.Key;
            sb.Append(h.ToString("x8", CultureInfo.InvariantCulture)).Append('\t').Append(Escape(k.Key)).Append('\t').Append(k.Where).Append('\n');
        }
        File.WriteAllText(Path.Combine(langDir, "keys.tsv"), sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"見出し {seen.Count} 件（XAML {keys.Count(k => k.Where.Contains(".xaml"))}・C# {keys.Count(k => k.Where.Contains(".cs"))} か所）。重なり {collisions}");
        return collisions == 0 ? 0 : 1;
    }
    case "lint":
    {
        bool skipped = args.Contains("--skipped");
        int n = 0;
        foreach (var (file, line, text) in Lint(root, skipped))
        {
            Console.WriteLine($"{file}:{line}\t{text}");
            n++;
        }
        Console.Error.WriteLine(skipped ? $"訳さないと決めた日本語: {n} か所" : $"L() を通していない日本語: {n} か所");
        return 0;
    }
    case "wrap":
    {
        int n = Wrap(root, dryRun: args.Contains("--dry"));
        Console.Error.WriteLine($"L() で包んだ文字列: {n} か所");
        return 0;
    }
    case "missing":
    {
        string lang = args.Length > 1 ? args[1] : "en";
        var table = ReadTable(Path.Combine(langDir, lang + ".txt"));
        int n = 0;
        foreach (var (h, key) in ReadKeys(langDir))
        {
            if (table.ContainsKey(h)) continue;
            Console.WriteLine($"{h:x8}\t{Escape(key)}");
            n++;
        }
        Console.Error.WriteLine($"{lang}: 訳していない見出し {n} 件");
        return 0;
    }
    case "stats":
    {
        var keys = ReadKeys(langDir).ToList();
        foreach (var f in Directory.GetFiles(langDir, "*.txt").OrderBy(f => f))
        {
            var table = ReadTable(f);
            int done = keys.Count(k => table.ContainsKey(k.Hash));
            int stale = table.Keys.Count(h => keys.All(k => k.Hash != h));
            Console.WriteLine($"{Path.GetFileNameWithoutExtension(f),-8} {done,5} / {keys.Count} ({100.0 * done / Math.Max(1, keys.Count):F1}%)  使われていない訳 {stale}");
        }
        return 0;
    }
    case "check":
    {
        // CI 用: 1 つでも問題があれば 1 を返す
        //   ・L() を通していない日本語がない
        //   ・keys.tsv がソースと合っている（extract し忘れていない）
        //   ・どの言語も全部訳してあり、使われていない訳がない
        //   ・訳の中の {0} {1:F1} などの穴が、見出しと同じ番号だけ（足りない・多いと、表示がおかしくなるか例外になる）
        int problems = 0;
        void Problem(string text) { Console.WriteLine(text); problems++; }
        foreach (var (file, line, text) in Lint(root)) Problem($"L() を通していない日本語: {file}:{line}\t{text}");
        var fresh = Extract(root).Select(k => Hash(k.Key)).ToHashSet();
        var keys = ReadKeys(langDir).ToList();
        var saved = keys.Select(k => k.Hash).ToHashSet();
        if (!fresh.SetEquals(saved)) Problem($"keys.tsv がソースと合っていません（extract してください）: 足りない {fresh.Except(saved).Count()} 件・余分 {saved.Except(fresh).Count()} 件");
        var holeRx = new Regex(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}(?!\})", RegexOptions.CultureInvariant);
        HashSet<string> Holes(string s) => holeRx.Matches(s).Select(m => m.Groups[1].Value).ToHashSet();
        foreach (var f in Directory.GetFiles(langDir, "*.txt").OrderBy(f => f))
        {
            string lang = Path.GetFileNameWithoutExtension(f);
            var table = ReadTable(f);
            foreach (var (h, key) in keys)
            {
                if (!table.TryGetValue(h, out var tr)) { Problem($"{lang}: 訳がありません {h:x8}\t{key}"); continue; }
                var want = Holes(Unescape(key));
                var got = Holes(tr);
                if (!want.SetEquals(got)) Problem($"{lang}: 穴の番号が違います {h:x8}（見出し {{{string.Join("},{", want.Order())}}}・訳 {{{string.Join("},{", got.Order())}}}）\t{tr}");
            }
            foreach (var h in table.Keys.Where(h => !saved.Contains(h))) Problem($"{lang}: 使われていない訳 {h:x8}");
        }
        Console.WriteLine(problems == 0 ? "翻訳の確認: 問題なし" : $"翻訳の確認: 問題 {problems} 件");
        return problems == 0 ? 0 : 1;
    }
    case "rename":
    {
        // ソースの文字列の中の言葉を変えたあと（extract の前）に、訳を新しい見出しへ付け替え、訳の中の同じ言葉も置き換える。
        // 見出しは日本語の文のハッシュなので、アプリ名のような言葉を変えると、そのままでは訳が見つからなくなるため
        if (args.Length < 3 || args.Length % 2 == 0)
        {
            Console.Error.WriteLine("使い方: rename <古い言葉> <新しい言葉> [<古い言葉> <新しい言葉> …]");
            return 2;
        }
        var pairs = new List<(string Old, string New)>();
        for (int i = 1; i + 1 < args.Length; i += 2) pairs.Add((args[i], args[i + 1]));
        string Apply(string s)
        {
            foreach (var (o, n) in pairs) s = s.Replace(o, n, StringComparison.Ordinal);
            return s;
        }
        var moves = new Dictionary<uint, uint>();
        foreach (var (h, key) in ReadKeys(langDir))
        {
            string source = Unescape(key);
            string renamed = Apply(source);
            if (renamed != source) moves[h] = Hash(renamed);
        }
        int files = 0, lines = 0;
        foreach (var f in Directory.GetFiles(langDir, "*.txt"))
        {
            var text = File.ReadAllLines(f);
            bool changed = false;
            for (int i = 0; i < text.Length; i++)
            {
                string line = text[i];
                if (line.StartsWith('#'))
                {
                    string header = Apply(line);
                    changed |= header != line;
                    text[i] = header;
                    continue;
                }
                if (line.Length < 10 || line[8] != '\t'
                    || !uint.TryParse(line.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint h)
                    || !moves.TryGetValue(h, out uint moved)) continue;
                text[i] = moved.ToString("x8", CultureInfo.InvariantCulture) + "\t" + Apply(line[9..]);
                changed = true;
                lines++;
            }
            if (!changed) continue;
            File.WriteAllText(f, string.Join("\n", text) + "\n", new UTF8Encoding(false));
            files++;
        }
        Console.WriteLine($"付け替えた見出し {moves.Count} 件・訳 {lines} 行（{files} ファイル）。続けて extract を実行してください");
        return 0;
    }
    default:
        Console.Error.WriteLine("使い方: extract | lint | missing <言語> | stats | rename <古い言葉> <新しい言葉> …");
        return 2;
}

static string FindRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "ProFanController.csproj"))) d = d.Parent;
    return d?.FullName ?? Directory.GetCurrentDirectory();
}

static IEnumerable<string> SourceFiles(string root, string pattern) =>
    Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
        .Where(f =>
        {
            string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            return !rel.StartsWith("Tests/") && !rel.StartsWith("tools/") && !rel.Contains("/bin/") && !rel.Contains("/obj/")
                   && !rel.StartsWith("bin/") && !rel.StartsWith("obj/");
        })
        .OrderBy(f => f, StringComparer.Ordinal);

static List<(string Key, string Where)> Extract(string root)
{
    var list = new List<(string, string)>();
    foreach (var f in SourceFiles(root, "*.xaml")) list.AddRange(ExtractXaml(root, f));
    foreach (var f in SourceFiles(root, "*.cs")) list.AddRange(ExtractCs(root, f));
    return list;
}

static IEnumerable<(string, string)> ExtractXaml(string root, string file)
{
    string[] XamlAttributes = { "Text", "Content", "ToolTip", "Header", "Title" };
    string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
    var settings = new XmlReaderSettings { IgnoreComments = true };
    using var r = XmlReader.Create(file, settings);
    var info = (IXmlLineInfo)r;
    var found = new List<(string, string)>();
    while (r.Read())
    {
        if (r.NodeType != XmlNodeType.Element || !r.MoveToFirstAttribute()) continue;
        string element = "";
        do
        {
            string name = r.LocalName, v = r.Value;
            if (!HasJapanese(v)) continue;
            string where = $"{rel}:{info.LineNumber}";
            if (v.StartsWith("{}")) { found.Add((v[2..], where)); continue; }
            if (v.StartsWith("{"))
            {
                // {Binding …, StringFormat={}{0:F1} 倍}
                var m = Regex.Match(v, @"StringFormat=\{\}(.*)\}\s*$");
                if (m.Success && HasJapanese(m.Groups[1].Value)) found.Add((m.Groups[1].Value, where));
                continue;
            }
            if (XamlAttributes.Contains(name)) found.Add((v, where));
            else if (name == "Tag") found.Add((v, where + " (Tag)"));
        } while (r.MoveToNextAttribute());
        r.MoveToElement();
        element = r.LocalName;
        // Tag は入力欄（TextBox）の案内のときだけ訳す
        for (int i = found.Count - 1; i >= 0 && found[i].Item2.EndsWith(" (Tag)"); i--)
        {
            if (element == "TextBox") found[i] = (found[i].Item1, found[i].Item2[..^6]);
            else found.RemoveAt(i);
        }
    }
    return found;
}

static IEnumerable<(string, string)> ExtractCs(string root, string file)
{
    string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: rel);
    foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
        int at = KeyArgument(inv);
        if (at < 0 || inv.ArgumentList.Arguments.Count <= at) continue;
        var arg = inv.ArgumentList.Arguments[at].Expression;
        string? key = KeyOf(arg);
        if (key is null || key.Length == 0) continue;
        int line = arg.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        yield return (key, $"{rel}:{line}");
    }
}

static bool IsLocCall(InvocationExpressionSyntax inv) => KeyArgument(inv) >= 0;

/// <summary>見出しになる引数の位置（L・LF は 1 つ目、Localization.LookupIn は 2 つ目）。訳す呼び出しでなければ -1</summary>
static int KeyArgument(InvocationExpressionSyntax inv) => inv.Expression switch
{
    IdentifierNameSyntax id => id.Identifier.Text is "L" or "LF" ? 0 : -1,
    MemberAccessExpressionSyntax ma when ma.Expression.ToString() is "Loc" or "ProFanController.Loc" && ma.Name.Identifier.Text is "L" or "LF" => 0,
    MemberAccessExpressionSyntax ma when ma.Expression.ToString() is "Localization" && ma.Name.Identifier.Text is "LookupIn" => 1,
    _ => -1,
};

/// <summary>実行時と同じ見出し（L($"…") は LocString と同じ作り方）</summary>
static string? KeyOf(ExpressionSyntax e)
{
    switch (e)
    {
        case LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression):
            return lit.Token.ValueText;
        case InterpolatedStringExpressionSyntax interp:
        {
            var sb = new StringBuilder();
            int i = 0;
            foreach (var c in interp.Contents)
            {
                if (c is InterpolatedStringTextSyntax t)
                {
                    foreach (char ch in t.TextToken.ValueText)
                    {
                        if (ch is '{' or '}') sb.Append(ch);
                        sb.Append(ch);
                    }
                }
                else if (c is InterpolationSyntax h)
                {
                    sb.Append('{').Append(i++.ToString(CultureInfo.InvariantCulture));
                    if (h.AlignmentClause is not null)
                    {
                        string a = h.AlignmentClause.Value.ToString().Replace(" ", "");
                        if (a != "0") sb.Append(',').Append(a);
                    }
                    if (h.FormatClause is not null && h.FormatClause.FormatStringToken.ValueText.Length > 0)
                        sb.Append(':').Append(h.FormatClause.FormatStringToken.ValueText);
                    sb.Append('}');
                }
            }
            return sb.ToString();
        }
        case ParenthesizedExpressionSyntax p:
            return KeyOf(p.Expression);
        default:
            return null;
    }
}

/// <summary>L() を通していない日本語の文字列（訳さないと決まっているもの＝ SkipReason が null でないものは除く）</summary>
static IEnumerable<(string, int, string)> Lint(string root, bool showSkipped = false)
{
    foreach (var f in SourceFiles(root, "*.cs"))
    {
        string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
        if (rel == "Core/Loc.cs") continue;
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: rel);
        var lines = tree.GetText().Lines;
        foreach (var s in tree.GetRoot().DescendantNodes().OfType<ExpressionSyntax>().Where(IsJapaneseString))
        {
            if (IsInsideLoc(s)) continue;
            string? skip = SkipReason(s, rel, lines);
            if ((skip is null) == showSkipped) continue;
            int line = s.GetLocation().GetLineSpan().StartLinePosition.Line;
            string shown = s.ToString().Replace("\r", "").Replace("\n", "⏎");
            if (shown.Length > 120) shown = shown[..120] + "…";
            yield return (rel, line + 1, showSkipped ? $"[{skip}] {shown}" : shown);
        }
    }
}

static bool IsJapaneseString(SyntaxNode node) => node switch
{
    LiteralExpressionSyntax lit => lit.IsKind(SyntaxKind.StringLiteralExpression) && HasJapanese(lit.Token.ValueText),
    InterpolatedStringExpressionSyntax i => i.Contents.OfType<InterpolatedStringTextSyntax>().Any(t => HasJapanese(t.TextToken.ValueText)),
    _ => false,
};

/// <summary>
/// 訳さない文字列なら、その理由（null なら訳す）。
/// ログ・属性・定数（const・既定の引数・case）・比べるための文字列・ファイル名・行末に // no-loc・設定の既定値（AppConfig のフィールド）
/// </summary>
static string? SkipReason(ExpressionSyntax s, string file, Microsoft.CodeAnalysis.Text.TextLineCollection lines)
{
    int line = s.GetLocation().GetLineSpan().StartLinePosition.Line;
    if (lines[line].ToString().Contains("no-loc")) return "no-loc";
    if (IsInsideLog(s)) return "log";
    if (s.Ancestors().OfType<AttributeSyntax>().Any()) return "attribute";
    string value = s is LiteralExpressionSyntax lit ? lit.Token.ValueText : s.ToString();
    if (value.Length > 400 || value.Contains("<html", StringComparison.OrdinalIgnoreCase)) return "long";
    if (Regex.IsMatch(value, @"^[^\s、・（）]+\.(txt|bat|ps1|json|csv|exe|lnk|zip|log|md)$")) return "file name";

    // 親を、かっこ・条件式（? :）・?? をたどって、文字列の使われ方を見る
    SyntaxNode n = s;
    while (n.Parent is ParenthesizedExpressionSyntax or ConditionalExpressionSyntax
           || n.Parent is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression })
        n = n.Parent;
    var p = n.Parent;
    switch (p)
    {
        case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression):
            return "compare";
        case CaseSwitchLabelSyntax or ConstantPatternSyntax:
            return "constant";
        case EqualsValueClauseSyntax { Parent: ParameterSyntax }:
            return "constant";
        case ElementAccessExpressionSyntax or BracketedArgumentListSyntax:
            return "key";
        case ArgumentSyntax { Parent: BracketedArgumentListSyntax }:
            return "key";
        case ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax inv } }:
        {
            string name = inv.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                IdentifierNameSyntax id => id.Identifier.Text,
                _ => "",
            };
            if (name is "Contains" or "StartsWith" or "EndsWith" or "Equals" or "IndexOf" or "LastIndexOf" or "Replace" or "Split"
                or "Trim" or "TrimEnd" or "TrimStart" or "Compare" or "ContainsKey" or "TryGetValue" or "Remove" or "GetProperty"
                or "Start" or "Combine" or "nameof")
                return "compare";
            break;
        }
    }
    // const の値、設定の既定値（AppConfig のフィールド・プロパティの初期値。保存される値なので訳さない）
    var field = s.Ancestors().OfType<FieldDeclarationSyntax>().FirstOrDefault();
    if (field is not null && field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword))) return "constant";
    if (s.Ancestors().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault() is { } local && local.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword))) return "constant";
    if (file == "Core/AppConfig.cs" && (field is not null || s.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault()?.Initializer is not null)
        && !s.Ancestors().OfType<MethodDeclarationSyntax>().Any())
        return "config default";
    return null;
}

static bool HasJapanese(string s) => s.Any(c => c is >= '　' and <= 'ヿ' or >= '㐀' and <= '鿿' or >= '＀' and <= '￯');

/// <summary>訳す文字列を L(…) で包む（中の穴の文字列も、それぞれ包む）</summary>
static int Wrap(string root, bool dryRun)
{
    int total = 0;
    foreach (var f in SourceFiles(root, "*.cs"))
    {
        string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
        if (rel == "Core/Loc.cs") continue;
        string text = File.ReadAllText(f);
        var tree = CSharpSyntaxTree.ParseText(text, path: rel);
        var lines = tree.GetText().Lines;
        var targets = tree.GetRoot().DescendantNodes().OfType<ExpressionSyntax>()
            .Where(IsJapaneseString)
            .Where(s => !IsInsideLoc(s) && SkipReason(s, rel, lines) is null)
            .ToList();
        if (targets.Count == 0) continue;
        // 後ろから書き換える（前の位置がずれないように）。外側と内側の両方を包むときは、内側を先に（後ろの位置から）
        var edits = new List<(int Pos, string Text)>();
        foreach (var t in targets)
        {
            edits.Add((t.Span.End, ")"));
            edits.Add((t.SpanStart, "L("));
        }
        var sb = new StringBuilder(text);
        foreach (var (pos, ins) in edits.OrderByDescending(e => e.Pos).ThenBy(e => e.Text == "L(" ? 1 : 0))
            sb.Insert(pos, ins);
        total += targets.Count;
        Console.WriteLine($"{rel}: {targets.Count}");
        if (!dryRun) File.WriteAllText(f, sb.ToString(), new UTF8Encoding(text.Length > 0 && File.ReadAllBytes(f) is [0xEF, 0xBB, 0xBF, ..]));
    }
    return total;
}

/// <summary>L()/LF() の最初の引数そのもの（穴の中の文字列は、それぞれ L() が要る）</summary>
static bool IsInsideLoc(ExpressionSyntax s)
{
    SyntaxNode? n = s;
    while (n?.Parent is ParenthesizedExpressionSyntax) n = n.Parent;
    return n?.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax inv } list } a
           && KeyArgument(inv) is var at && at >= 0 && list.Arguments.IndexOf(a) == at;
}

/// <summary>ログに書く文（Logger.Info / Warn / Error / Debug の引数の中）。ログは作った人が読むので訳さない</summary>
static bool IsInsideLog(SyntaxNode s) => s.Ancestors().OfType<InvocationExpressionSyntax>().Any(inv =>
    inv.Expression is MemberAccessExpressionSyntax ma && ma.Expression.ToString() == "Logger"
    || inv.Expression is IdentifierNameSyntax { Identifier.Text: "Log" or "LogInfo" or "LogWarn" });

static bool HasNonAscii(string s) => s.Any(c => c > 0x7F);

static uint Hash(string s)
{
    uint h = 2166136261;
    foreach (char c in s)
    {
        h ^= c;
        h *= 16777619;
    }
    return h;
}

static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

/// <summary>Escape の逆（\\ \r \n \t を元の文字に戻す）</summary>
static string Unescape(string s)
{
    var sb = new StringBuilder(s.Length);
    for (int i = 0; i < s.Length; i++)
    {
        if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
        char c = s[++i];
        sb.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => c });
    }
    return sb.ToString();
}

static IEnumerable<(uint Hash, string Key)> ReadKeys(string langDir)
{
    foreach (var line in File.ReadLines(Path.Combine(langDir, "keys.tsv")))
    {
        if (line.StartsWith('#') || line.Length < 10) continue;
        var parts = line.Split('\t');
        yield return (uint.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture), parts[1]);
    }
}

static Dictionary<uint, string> ReadTable(string path)
{
    var t = new Dictionary<uint, string>();
    if (!File.Exists(path)) return t;
    foreach (var line in File.ReadLines(path))
    {
        if (line.StartsWith('#') || line.Length < 10 || line[8] != '\t') continue;
        if (uint.TryParse(line.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint h)) t[h] = line[9..];
    }
    return t;
}

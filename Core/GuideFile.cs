// ==================================================
// GuideFile.cs - アプリの「使い方」で開く説明書（アプリのフォルダーの説明書。なければ GitHub の README）を決める
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// 「使い方」で開く説明書。日本語の画面なら「はじめにお読みください.txt」、ほかの言語なら README_en.txt（説明書は日本語と英語だけ）。
/// アプリのフォルダーになければ（開発中・消した など）、GitHub の README を開く
/// </summary>
public static class GuideFile
{
    public const string JapaneseFile = "はじめにお読みください.txt";
    public const string EnglishFile = "README_en.txt";

    /// <summary>画面の言語に合う説明書のファイル名</summary>
    public static string FileFor(string language) =>
        language.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? JapaneseFile : EnglishFile;

    /// <summary>GitHub の README（日本語の画面なら日本語の README）</summary>
    public static string WebFor(string language, string repository) =>
        language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
            ? $"https://github.com/{repository}/blob/main/README.ja.md"
            : $"https://github.com/{repository}#readme";

    /// <summary>アプリのフォルダーにある説明書（なければ、もう一方の言語の説明書。どちらもなければ null）</summary>
    public static string? LocalFor(string appDir, string language, Func<string, bool> exists)
    {
        string first = Path.Combine(appDir, FileFor(language));
        if (exists(first)) return first;
        string other = Path.Combine(appDir, FileFor(language) == JapaneseFile ? EnglishFile : JapaneseFile);
        return exists(other) ? other : null;
    }
}

// 「使い方」で開く説明書（GuideFile）と、OpenRGB が入っていないかの判断（OpenRgbAutoStart.NotInstalled）
using ProFanController;

namespace LogicTests;

public static class GuideFileTests
{
    public static void TestGuideForLanguage()
    {
        T.Check(GuideFile.FileFor("ja") == GuideFile.JapaneseFile, "Japanese UI → Japanese guide");
        T.Check(GuideFile.FileFor("en") == GuideFile.EnglishFile, "English UI → English guide");
        T.Check(GuideFile.FileFor("de") == GuideFile.EnglishFile, "other languages → English guide (guides are ja/en only)");

        string dir = @"C:\App";
        var both = new HashSet<string> { Path.Combine(dir, GuideFile.JapaneseFile), Path.Combine(dir, GuideFile.EnglishFile) };
        T.Check(GuideFile.LocalFor(dir, "ko", both.Contains) == Path.Combine(dir, GuideFile.EnglishFile), "both present → the one for the language");
        var jaOnly = new HashSet<string> { Path.Combine(dir, GuideFile.JapaneseFile) };
        T.Check(GuideFile.LocalFor(dir, "en", jaOnly.Contains) == Path.Combine(dir, GuideFile.JapaneseFile), "missing → the other language's guide");
        T.Check(GuideFile.LocalFor(dir, "en", _ => false) is null, "none → null (open GitHub instead)");

        T.Check(GuideFile.WebFor("ja", "o/r") == "https://github.com/o/r/blob/main/README.ja.md", "Japanese web README");
        T.Check(GuideFile.WebFor("fr", "o/r") == "https://github.com/o/r#readme", "English web README");
    }

    public static void TestGuideFilesShipped()
    {
        // 説明書の名前を変えたら、ここで分かるように（Distribution のファイルがアプリのフォルダーに入る）
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ProFanController.csproj"))) d = d.Parent;
        T.Check(d is not null, "repository root found");
        if (d is null) return;
        string dist = Path.Combine(d.FullName, "Distribution");
        T.Check(File.Exists(Path.Combine(dist, "変更履歴.txt")) && File.Exists(Path.Combine(dist, "CHANGELOG_en.txt")), "change logs exist next to the guides");
        // ダウンロードから起動して Program Files に移すときも、インストーラーと同じ説明書を写す（足したファイルを写し忘れない）
        foreach (var f in Directory.GetFiles(dist).Select(Path.GetFileName).Where(n => n != "make-dist.ps1"))
            T.Check(InstallCheck.AppFiles.Contains(f), $"{f} is copied when the app moves itself to Program Files");
        T.Check(File.Exists(Path.Combine(dist, GuideFile.JapaneseFile)), "Japanese guide exists in Distribution");
        T.Check(File.Exists(Path.Combine(dist, GuideFile.EnglishFile)), "English guide exists in Distribution");
    }

    public static void TestOpenRgbNotInstalled()
    {
        T.Check(OpenRgbAutoStart.NotInstalled(new(false, false, false, null)), "nothing running, no service, no exe → not installed");
        T.Check(!OpenRgbAutoStart.NotInstalled(new(false, false, true, null)), "running (e.g. from a zip) → installed");
        T.Check(!OpenRgbAutoStart.NotInstalled(new(true, true, false, null)), "service (even disabled) → installed");
        T.Check(!OpenRgbAutoStart.NotInstalled(new(false, false, false, @"C:\Program Files\OpenRGB\OpenRGB.exe")), "exe in Program Files → installed");
    }
}

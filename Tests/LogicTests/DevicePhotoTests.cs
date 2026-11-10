// ==================================================
// DevicePhotoTests.cs - デバイスの写真を型番で探すときの決まり（どの写真なら同じ製品とみなすか）
// Dependencies: .NET 8 / System.Text.Json (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Text.Json;
using ProFanController;

namespace LogicTests;

public static class DevicePhotoTests
{
    /// <summary>名前から探す言葉を取り出す（メーカーの飾りの言葉は外し、"5070Ti" は分ける）</summary>
    public static void TestTerms()
    {
        var t = DevicePhotos.Terms("NVIDIA GeForce RTX 5070 Ti");
        T.Check(t.SequenceEqual(new[] { "RTX", "5070", "Ti" }), $"GPU terms ({string.Join(",", t)})");
        var p = DevicePhotos.Terms("PNY GeForce RTX 5070Ti ARGB Epic-X OC");
        T.Check(p.Contains("5070") && p.Contains("Ti") && p.Contains("PNY") && !p.Contains("ARGB"), $"5070Ti split, ARGB dropped ({string.Join(",", p)})");
        T.Check(DevicePhotos.KeyTerms("ASRock B860 Steel Legend WiFi").SequenceEqual(new[] { "B860" }), "board key term B860");
        T.Check(DevicePhotos.KeyTerms("Intel Core Ultra 5 245KF").SequenceEqual(new[] { "245KF" }), "CPU key term 245KF");
        T.Check(DevicePhotos.KeyTerms("Nuvoton NCT6686D").Count == 1, "chip has a key term");
        T.Check(DevicePhotos.KeyTerms("Generic controller").Count == 0, "no model number → nothing to search");
    }

    /// <summary>型番の数字が題名にないと 0（別の製品）。メーカー名などが合うほど高い</summary>
    public static void TestScore()
    {
        const string gpu = "NVIDIA GeForce RTX 5070 Ti";
        T.Check(DevicePhotos.Score("File:PNY RTX 5070 Ti at Computex 2025.jpg", gpu) == 3, "RTX 5070 Ti photo matches all three words");
        T.Check(DevicePhotos.Score("File:RTX 5080 Founders Edition.jpg", gpu) == 0, "5080 is a different card");
        T.Check(DevicePhotos.Score("File:Intel-Core-Ultra-5-Badge-2023.png", "Intel Core Ultra 5 245KF") == 0, "badge without 245 is not the CPU");
        T.Check(DevicePhotos.Score("File:Intel Core Ultra 5 245K box.jpg", "Intel Core Ultra 5 245KF") > 0, "245K box matches 245KF (same chip, KF = no iGPU)");
        T.Check(DevicePhotos.Score("File:London Country G610 BPH.jpg", "Logitech Gaming Keyboard G610") == 1, "bus G610 only matches the number");
    }

    /// <summary>自動で使うのは、型番の数字に加えてほかの言葉も合うときだけ（キーボード G610 にバスの写真を使わない）</summary>
    public static void TestConfidence()
    {
        PhotoCandidate C(string title, string name) => new(title, "t", "i", "p", "a", "l", DevicePhotos.Score(title, name));
        T.Check(DevicePhotos.IsConfident(C("PNY RTX 5070 Ti at Computex 2025", "NVIDIA GeForce RTX 5070 Ti"), "NVIDIA GeForce RTX 5070 Ti"), "GPU photo is confident");
        T.Check(!DevicePhotos.IsConfident(C("London Country G610 BPH", "Logitech Gaming Keyboard G610"), "Logitech Gaming Keyboard G610"), "bus photo is not used automatically");
        T.Check(DevicePhotos.IsConfident(C("Logitech G610 keyboard", "Logitech Gaming Keyboard G610"), "Logitech Gaming Keyboard G610"), "keyboard photo with the brand is confident");
    }

    /// <summary>API の返事を候補にする: 画像だけ・Commons の URL だけ・作者とライセンスは HTML を外す・よく合う順</summary>
    public static void TestParse()
    {
        const string json = """
        {"query":{"pages":{
          "1":{"title":"File:PNY RTX 5070 Ti at Computex 2025.jpg","imageinfo":[{"mime":"image/jpeg",
               "thumburl":"https://upload.wikimedia.org/a/800px-x.jpg","descriptionurl":"https://commons.wikimedia.org/wiki/File:x.jpg",
               "extmetadata":{"Artist":{"value":"<a href=\"//x\">Some &amp; One</a>"},"LicenseShortName":{"value":"CC BY-SA 4.0"}}}]},
          "2":{"title":"File:RTX 50 video.webm","imageinfo":[{"mime":"video/webm","thumburl":"https://upload.wikimedia.org/b.jpg","descriptionurl":"https://c/b"}]},
          "3":{"title":"File:RTX 5070 Ti.png","imageinfo":[{"mime":"image/png","thumburl":"https://evil.example/c.png","descriptionurl":"https://c/c"}]},
          "4":{"title":"File:GeForce RTX 5070 Ti card.png","imageinfo":[{"mime":"image/png",
               "thumburl":"https://upload.wikimedia.org/d.png","descriptionurl":"https://commons.wikimedia.org/wiki/File:d.png"}]}
        }}}
        """;
        using var doc = JsonDocument.Parse(json);
        var list = DevicePhotos.Parse(doc.RootElement, "NVIDIA GeForce RTX 5070 Ti");
        T.Check(list.Count == 2, $"video and non-Commons URL skipped ({list.Count} left)");
        T.Check(list[0].Title == "GeForce RTX 5070 Ti card" || list[0].Title == "PNY RTX 5070 Ti at Computex 2025", "titles cleaned");
        var pny = list.First(c => c.Title.StartsWith("PNY", StringComparison.Ordinal));
        T.Check(pny.Author == "Some & One" && pny.License == "CC BY-SA 4.0", $"author and licence without HTML ({pny.Author} / {pny.License})");
    }

    /// <summary>画像の種類は中身で見る（拡張子や返事の種類を信じない）</summary>
    public static void TestImageExtension()
    {
        T.Check(DevicePhotos.ImageExtension(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }) == ".png", "PNG");
        T.Check(DevicePhotos.ImageExtension(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }) == ".jpg", "JPEG");
        T.Check(DevicePhotos.ImageExtension("<html>"u8) is null, "HTML is not an image");
        T.Check(DevicePhotos.BrowserSearchUrl("NVIDIA GeForce RTX 5070 Ti").StartsWith("https://www.bing.com/images/search?q=NVIDIA%20RTX%205070%20Ti", StringComparison.Ordinal),
            DevicePhotos.BrowserSearchUrl("NVIDIA GeForce RTX 5070 Ti"));
    }
}

public static class DevicePhotoLiveTests
{
    /// <summary>調査（DEVICE_PHOTO_LIVE=1）: 実際に Wikimedia Commons を探して、候補と自動で使うかを表示する</summary>
    public static void TestLiveSearch()
    {
        if (Environment.GetEnvironmentVariable("DEVICE_PHOTO_LIVE") != "1") return;
        foreach (var name in new[] { "NVIDIA GeForce RTX 5070 Ti", "ASRock B860 Steel Legend WiFi", "Intel Core Ultra 5 245KF", "Nuvoton NCT6686D" })
        {
            var list = DevicePhotos.SearchAsync(name, CancellationToken.None).GetAwaiter().GetResult();
            T.Info($"{name}: {list.Count} candidates; auto: {list.FirstOrDefault(c => DevicePhotos.IsConfident(c, name))?.Title ?? "-"}");
            foreach (var c in list.Take(3)) T.Info($"   {c.Score} {c.Title} | {c.Author} | {c.License}");
        }
    }
}

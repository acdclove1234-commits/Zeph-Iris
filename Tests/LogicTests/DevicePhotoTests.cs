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

    /// <summary>
    /// 同じ数字でも格の言葉が違えば別の製品（"Samsung SSD 980 1TB" に "980 PRO" の写真が付いていたことがある）
    /// </summary>
    public static void TestTierWordsMakeADifferentProduct()
    {
        T.Check(DevicePhotos.Score("File:Samsung 980 PRO PCIe 4.0 NVMe SSD 1TB-top.jpg", "Samsung SSD 980 1TB") == 0, "980 PRO is not the 980");
        T.Check(DevicePhotos.Score("File:Samsung 980 PRO NVMe SSD 1TB.jpg", "Samsung SSD 980 PRO 1TB") > 0, "980 PRO matches 980 PRO");
        T.Check(DevicePhotos.Score("File:RTX 5070 Founders Edition.jpg", "NVIDIA GeForce RTX 5070 Ti") == 0, "plain 5070 is not the 5070 Ti");
        T.Check(DevicePhotos.Score("File:Sapphire AMD Radeon RX 7900 XTX.jpg", "AMD Radeon RX 7900 XT") == 0, "7900 XTX is not the 7900 XT");
        T.Check(DevicePhotos.Score("File:Sapphire AMD Radeon RX 7900 XTX.jpg", "AMD Radeon RX 7900 XTX") > 0, "7900 XTX matches itself");
        T.Check(DevicePhotos.Score("File:NVIDIA RTX 5070Ti card.jpg", "NVIDIA GeForce RTX 5070 Ti") > 0, "5070Ti written together still matches");
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

    /// <summary>
    /// 製品の写真ではないものは使わない。Ryzen 7 9800X3D で探すと、Commons にはレビュー動画から切り出したコマ
    /// （3840×2160 の PNG。ベンチマークのグラフ）しかなく、それが CPU の写真になっていた
    /// </summary>
    public static void TestRejectsVideoFramesAndCharts()
    {
        const string json = """
        {"query":{"pages":{
          "1":{"title":"File:Video zum AMD Ryzen 7 9800X3D (ZMASLO) 01.png","imageinfo":[{"mime":"image/png","width":3840,"height":2160,
               "thumburl":"https://upload.wikimedia.org/a.png","descriptionurl":"https://commons.wikimedia.org/wiki/File:a.png"}]},
          "2":{"title":"File:AMD Ryzen 7 9800X3D frame.png","imageinfo":[{"mime":"image/png","width":1920,"height":1080,
               "thumburl":"https://upload.wikimedia.org/b.png","descriptionurl":"https://commons.wikimedia.org/wiki/File:b.png"}]},
          "3":{"title":"File:AMD Ryzen 7 9800X3D benchmark chart.jpg","imageinfo":[{"mime":"image/jpeg","width":1200,"height":800,
               "thumburl":"https://upload.wikimedia.org/c.jpg","descriptionurl":"https://commons.wikimedia.org/wiki/File:c.jpg"}]},
          "4":{"title":"File:AMD Ryzen 7 9800X3D top.jpg","imageinfo":[{"mime":"image/jpeg","width":4000,"height":3000,
               "thumburl":"https://upload.wikimedia.org/d.jpg","descriptionurl":"https://commons.wikimedia.org/wiki/File:d.jpg"}]}
        }}}
        """;
        using var doc = JsonDocument.Parse(json);
        var list = DevicePhotos.Parse(doc.RootElement, "AMD Ryzen 7 9800X3D");
        T.Check(list.Count == 1 && list[0].Title == "AMD Ryzen 7 9800X3D top", $"video frames (title or 16:9 screen-size PNG) and charts are skipped ({string.Join(", ", list.Select(c => c.Title))})");
        T.Check(!DevicePhotos.IsVideoFrame("image/jpeg", 1920, 1080) && !DevicePhotos.IsVideoFrame("image/png", 1920, 1200), "a JPEG photo or another PNG size is not taken for a frame");
        T.Check(!DevicePhotos.IsUnsuitableTitle("PNY RTX 5070 Ti at Computex 2025") && !DevicePhotos.IsUnsuitableTitle("Screenless device"),
            "ordinary product photo titles pass (whole words only)");
        // 前の版が付けた写真（保存してある元のページ）も見分けて外せる
        T.Check(DevicePhotos.IsUnsuitableSource("https://commons.wikimedia.org/wiki/File:Video_zum_AMD_Ryzen_7_9800X3D_(ZMASLO)_01.png"), "a saved video-frame photo is recognised from its source page");
        T.Check(!DevicePhotos.IsUnsuitableSource("https://commons.wikimedia.org/wiki/File:PNY_RTX_5070_Ti_at_Computex_2025.jpg") && !DevicePhotos.IsUnsuitableSource(""),
            "product photos and own images are kept");
    }

    /// <summary>
    /// Openverse の返事を候補にする: Commons と同じく型番が合うものだけ。NC・ND のライセンス・Commons の重複・
    /// 大人向け・https でない URL・GIF は使わない。ライセンスは "CC BY-SA 2.0" の形にする
    /// </summary>
    public static void TestParseOpenverse()
    {
        const string json = """
        {"result_count":6,"results":[
          {"title":"Logitech G610 keyboard","url":"https://live.staticflickr.com/1/a_b.jpg","thumbnail":"https://api.openverse.org/v1/images/a/thumb/",
           "foreign_landing_url":"https://www.flickr.com/photos/x/1","creator":"Jane &amp; Co","license":"by-sa","license_version":"2.0",
           "source":"flickr","filetype":"jpg","width":2048,"height":1365,"mature":false},
          {"title":"Logitech G610 on a desk","url":"https://live.staticflickr.com/2/b.jpg","thumbnail":"https://api.openverse.org/v1/images/b/thumb/",
           "foreign_landing_url":"https://www.flickr.com/photos/x/2","creator":"Bob","license":"by-nc","license_version":"2.0","source":"flickr","filetype":"jpg"},
          {"title":"Logitech G610 keyboard","url":"https://upload.wikimedia.org/c.jpg","thumbnail":"https://api.openverse.org/v1/images/c/thumb/",
           "foreign_landing_url":"https://commons.wikimedia.org/wiki/File:c.jpg","creator":"C","license":"by","license_version":"4.0","source":"wikimedia","filetype":"jpg"},
          {"title":"Logitech G610 animated","url":"https://live.staticflickr.com/3/d.gif","thumbnail":"","foreign_landing_url":"https://www.flickr.com/photos/x/3",
           "creator":"D","license":"cc0","license_version":"1.0","source":"flickr","filetype":"gif"},
          {"title":"Logitech G610 close-up","url":"http://insecure.example/e.jpg","thumbnail":"","foreign_landing_url":"https://www.flickr.com/photos/x/4",
           "creator":"E","license":"cc0","license_version":"1.0","source":"flickr","filetype":"jpg"},
          {"title":"Logitech G610","url":"https://live.staticflickr.com/4/f.jpg","thumbnail":"","foreign_landing_url":"https://www.flickr.com/photos/x/5",
           "creator":"","license":"cc0","license_version":"1.0","source":"flickr","filetype":null,"mature":true}
        ]}
        """;
        using var doc = JsonDocument.Parse(json);
        var list = DevicePhotos.ParseOpenverse(doc.RootElement, "Logitech Gaming Keyboard G610");
        T.Check(list.Count == 1, $"only the CC BY-SA Flickr photo is kept ({string.Join(", ", list.Select(c => c.Title))})");
        var c = list[0];
        T.Check(c.License == "CC BY-SA 2.0" && c.Author == "Jane & Co", $"licence and author shown like Commons ({c.License} / {c.Author})");
        // 画像は Openverse の縮小画像から取る（元の置き場所の Flickr などへは問い合わせない）。元のページは作者の表示用
        T.Check(c.ThumbUrl.StartsWith("https://api.openverse.org/", StringComparison.Ordinal) && c.ImageUrl == c.ThumbUrl
                && c.PageUrl.StartsWith("https://www.flickr.com/", StringComparison.Ordinal), "image comes from api.openverse.org only; source page kept for credit");
        T.Check(DevicePhotos.IsConfident(c, "Logitech Gaming Keyboard G610"), "a Flickr photo with the brand and model is used automatically");
        T.Check(DevicePhotos.OpenverseLicense("cc0", "1.0") == "CC0 1.0" && DevicePhotos.OpenverseLicense("pdm", "") == "Public Domain Mark"
                && DevicePhotos.OpenverseLicense("by-nd", "4.0") == "" && DevicePhotos.OpenverseLicense("by-nc-sa", "2.0") == "", "licence names; NC and ND are not used");
    }

    /// <summary>2 つのサイトの候補をまとめる: よく合う順、同じ点なら Commons を先に、同じ画像は 1 つだけ</summary>
    public static void TestMerge()
    {
        PhotoCandidate C(string title, string url, int score) => new(title, url, url, "p", "a", "l", score);
        var merged = DevicePhotos.Merge(new[] { C("commons 3", "https://u/1", 3), C("commons 2", "https://u/2", 2), C("openverse 3", "https://f/3", 3), C("dup", "https://u/1", 4) });
        T.Check(merged.Select(c => c.Title).SequenceEqual(new[] { "commons 3", "openverse 3", "commons 2" }), $"order and de-duplication ({string.Join(", ", merged.Select(c => c.Title))})");
    }

    /// <summary>見つからなかったデバイスは 30 日たったら探し直す（前の版の「探し終えた」印は捨てて、すぐ探し直す）</summary>
    public static void TestResearchSchedule()
    {
        var now = new DateTime(2026, 11, 13, 12, 0, 0);
        T.Check(DevicePhotos.IsSearchDue(null, now), "never searched → search now");
        T.Check(!DevicePhotos.IsSearchDue(now.AddDays(-29), now), "searched 29 days ago → wait");
        T.Check(DevicePhotos.IsSearchDue(now.AddDays(-30), now), "searched 30 days ago → search again");
        T.Check(DevicePhotos.IsSearchDue(now.AddDays(5), now), "a date in the future (clock was wrong) → search again");
        var cfg = AppConfig.FromJson("""{ "DevicePhotoSearched": ["/gpu-amd/0"], "DevicePhotoSearchedAt": { "/intelcpu/0": "2026-11-01T00:00:00" } }""");
        T.Check(cfg.DevicePhotoSearched is null && !cfg.ToJson().Contains("\"DevicePhotoSearched\"", StringComparison.Ordinal),
            "the old 'searched' list is dropped and not saved");
        T.Check(cfg.DevicePhotoSearchedAt.Count == 1 && cfg.DevicePhotoSearchedAt.ContainsKey("/intelcpu/0"), "the new dates are kept");
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
    /// <summary>
    /// 調査（DEVICE_PHOTO_LIVE=1）: 実際に Wikimedia Commons と Openverse を探して、候補と自動で使うかを表示する。
    /// DEVICE_PHOTO_NAMES に「;」区切りで名前を入れると、その名前で探す
    /// </summary>
    public static void TestLiveSearch()
    {
        if (Environment.GetEnvironmentVariable("DEVICE_PHOTO_LIVE") != "1") return;
        var names = Environment.GetEnvironmentVariable("DEVICE_PHOTO_NAMES") is { Length: > 0 } n
            ? n.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : new[] { "NVIDIA GeForce RTX 5070 Ti", "ASRock B860 Steel Legend WiFi", "Intel Core Ultra 5 245KF", "Nuvoton NCT6686D" };
        foreach (var name in names)
        {
            var r = DevicePhotos.SearchAsync(name, CancellationToken.None).GetAwaiter().GetResult();
            T.Info($"{name}: {r.Found.Count} candidates{(r.Complete ? "" : " (a site did not answer)")}; auto: {r.Found.FirstOrDefault(c => DevicePhotos.IsConfident(c, name))?.Title ?? "-"}");
            foreach (var c in r.Found.Take(4)) T.Info($"   {c.Score} {c.Title} | {c.Author} | {c.License} | {new Uri(c.PageUrl).Host}");
        }
    }
}

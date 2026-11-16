// ==================================================
// DeviceImageFileTests.cs - デバイスのカードの画像（貼り付けた画像の種類・前の版の写真の設定）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using ProFanController;

namespace LogicTests;

public static class DeviceImageFileTests
{
    /// <summary>画像の種類は中身で見る（拡張子を信じない）</summary>
    public static void TestImageExtension()
    {
        T.Check(DeviceImageFile.Extension(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }) == ".png", "PNG");
        T.Check(DeviceImageFile.Extension(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }) == ".jpg", "JPEG");
        T.Check(DeviceImageFile.Extension("<html>"u8) is null, "HTML is not an image");
    }

    /// <summary>
    /// ネットで写真を探す設定をなくしても、前の版の設定ファイルは読める。なくした項目は保存し直さず、
    /// 前の版が取ってきた写真（作者・ライセンス付き）はそのまま残る
    /// </summary>
    public static void TestOldPhotoSettingsLoad()
    {
        var cfg = AppConfig.FromJson("""
            {
              "AutoDevicePhotos": false,
              "DevicePhotoSearched": ["/gpu-amd/0"],
              "DevicePhotoSearchedAt": { "/intelcpu/0": "2026-11-01T00:00:00" },
              "DeviceImages": [ { "DeviceKey": "/gpu/0", "FileName": "a.jpg", "SourceUrl": "https://example.org/a", "Author": "A", "License": "CC BY 4.0" } ],
              "SomethingNewer": 1
            }
            """);
        string json = cfg.ToJson();
        T.Check(!json.Contains("AutoDevicePhotos", StringComparison.Ordinal) && !json.Contains("DevicePhotoSearched", StringComparison.Ordinal),
            "removed photo-search settings are not saved again");
        T.Check(json.Contains("SomethingNewer", StringComparison.Ordinal), "other unknown settings are still kept");
        T.Check(cfg.DeviceImages.Count == 1 && cfg.DeviceImages[0].FileName == "a.jpg" && cfg.DeviceImages[0].Author == "A"
                && cfg.DeviceImages[0].License == "CC BY 4.0", "saved photos and their credit are kept");
    }
}

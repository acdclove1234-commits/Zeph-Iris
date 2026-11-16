// ==================================================
// DeviceImageFile.cs - デバイスのカードに使う、利用者が用意した画像（貼り付け・ファイル）の決まり
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

public static class DeviceImageFile
{
    /// <summary>1 枚の画像の大きさの上限（バイト）</summary>
    public const int MaxBytes = 15 * 1024 * 1024;

    /// <summary>画像の種類（ファイルの先頭のバイトで見る）から拡張子を決める。画像でなければ null</summary>
    public static string? Extension(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G') return ".png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P') return ".webp";
        if (b.Length >= 2 && b[0] == (byte)'B' && b[1] == (byte)'M') return ".bmp";
        if (b.Length >= 6 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F') return ".gif";
        return null;
    }
}

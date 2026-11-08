// ==================================================
// UiFonts.cs - 埋め込みフォント (Montserrat + Zen Maru Gothic) をコードから使うための共通定義
// Dependencies: WPF / .NET 8 (MIT License), Montserrat / ProFan Maru（Zen Maru Gothic の改変版）(SIL OFL 1.1)
// See CREDITS.txt for full license details.
// ==================================================

using System.Windows.Media;

namespace ProFanController.UI;

public static class UiFonts
{
    /// <summary>
    /// グラフなど OnRender で描く文字用。XAML の UiFont と同じ組み合わせ。
    /// 画面の言語のフォント（中国語・韓国語・タイ語など）を、日本語のフォントより先に探す（表示言語を切り替えたら作り直す）
    /// </summary>
    public static FontFamily Family => _family.Value;
    private static readonly PerLanguage<FontFamily> _family = new(() => new FontFamily(
        new Uri("pack://application:,,,/"),
        $"./Assets/Fonts/#Montserrat, {LanguageFonts}./Assets/Fonts/#ProFan Maru, Yu Gothic UI"));

    /// <summary>画面の言語で先に探すフォント（「…, 」の形。なければ空）</summary>
    public static string LanguageFonts => Localization.Current.Fonts is { Length: > 0 } f ? f + ", " : "";
}

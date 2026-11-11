// ==================================================
// Converters.cs - XAML のバインド用の変換 (ラジオボタンと文字列の値を結び付ける)
// Dependencies: WPF / .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;
using System.Windows.Data;

namespace ProFanController.UI;

/// <summary>
/// 値が ConverterParameter と同じなら true。ラジオボタンで選ばれたときは、その ConverterParameter を書き戻す。
/// 例: IsChecked="{Binding Mode, Converter={StaticResource Equals}, ConverterParameter=Target}"
/// </summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Equals(value?.ToString(), parameter?.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? parameter?.ToString() : Binding.DoNothing;
}

/// <summary>
/// ファンの一覧の 2 行目: 種類（ファン・ポンプ）と、つないでいる端子の名前（例「ファン · CPU Fan」）。
/// 端子の名前は、ハードウェアを見つけたあと（Names を入れたあと）に分かる。分からなければ端子の ID を出す
/// </summary>
public sealed class FanSubtitleConverter : IMultiValueConverter
{
    /// <summary>端子の ID → 名前（MainWindow がハードウェアを見つけたときに入れ直す）</summary>
    public static IReadOnlyDictionary<string, string> Names { get; set; } = new Dictionary<string, string>();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        string role = values.Length > 0 && values[0] as string == FanRoles.Pump ? L("ポンプ") : L("ファン");
        string id = values.Length > 1 ? values[1] as string ?? "" : "";
        return $"{role} · {(Names.TryGetValue(id, out var n) ? n : id)}";
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

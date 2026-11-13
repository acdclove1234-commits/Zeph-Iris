// ==================================================
// GpuTempCheck.cs - グラボは見えているのに、温度が 1 つも読めないときに知らせる
//   （グラボのドライバーが入れ替わった・壊れたとき、使用率やメモリは読めても、温度・ファンがドライバーから取れなくなることがある）
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Text.RegularExpressions;

namespace ProFanController;

public static class GpuTempCheck
{
    /// <summary>単体のグラボらしい名前（CPU に入っているグラフィックス「Radeon(TM) Graphics」「UHD Graphics」などは、もともと温度がないことが多いので除く）</summary>
    private static readonly Regex Discrete = new(@"\b(RX\s?\d|RTX|GTX|Arc\b|Radeon\s?Pro|Quadro|TITAN|Radeon\s?VII)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool LooksDiscrete(string name) => Discrete.IsMatch(name);

    /// <summary>
    /// 温度が読めないグラボの名前。温度が 0 個のグラボのうち、前は読めていたもの・単体のグラボらしいもの
    /// </summary>
    public static List<string> Missing(IEnumerable<DeviceInfo> devices, IEnumerable<KnownDevice> known)
    {
        var k = known.ToList();
        return devices
            .Where(d => d.Kind == "Gpu" && d.TempCount == 0)
            .Where(d => LooksDiscrete(d.Name) || k.Any(x => x.Key == d.Key && x.Name == d.Name && x.HadTemps))
            .Select(d => d.Name)
            .Distinct()
            .ToList();
    }

    /// <summary>画面に出す案内</summary>
    public static string Message(IReadOnlyList<string> names) =>
        L($"{string.Join(L("・"), names)} の温度を読めません（グラボは見えていますが、温度とファンの値がグラボのドライバーから取れません）。ドライバーが入れ替わったときに起きることがあります。メーカーのサイトから最新のドライバー（AMD Software・NVIDIA のドライバーなど）を入れ直して PC を再起動すると、直ることがあります。直るまでは、グラボの温度での制御と緊急全開は働きません。");
}

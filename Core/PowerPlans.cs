// ==================================================
// PowerPlans.cs - Windows の電源プランの一覧取得と切り替え (powercfg.exe を呼び出す)
// Dependencies: .NET 8 (MIT License), Windows powercfg.exe (OS標準)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ProFanController;

public sealed record PowerPlan(string Guid, string Name, bool Active);

public static partial class PowerPlans
{
    // 例: 「電源設定の GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (バランス) *」
    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\((.+?)\)\s*(\*)?")]
    private static partial Regex PlanLine();

    public static List<PowerPlan> List()
    {
        try
        {
            return PlanLine().Matches(Run("/list"))
                .Select(m => new PowerPlan(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim(), m.Groups[3].Success))
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Warn($"電源プランの取得に失敗: {ex.Message}");
            return new();
        }
    }

    public static string? ActiveGuid() => List().FirstOrDefault(p => p.Active)?.Guid;

    /// <summary>電源プランを切り替える。切り替えられたら true</summary>
    public static bool SetActive(string guid)
    {
        try
        {
            int code = RunCode($"/setactive {guid}");
            if (code != 0)
            {
                Logger.Warn($"電源プランの切り替えに失敗: {guid}（powercfg の終了コード {code}）");
                return false;
            }
            Logger.Info($"電源プランを切り替え: {guid}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"電源プランの切り替えに失敗: {ex.Message}");
            return false;
        }
    }

    private static int RunCode(string args)
    {
        var psi = SystemTool.Start("powercfg.exe", args);
        using var p = Process.Start(psi)!;
        if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } return -1; }
        return p.ExitCode;
    }

    private static string Run(string args)
    {
        // powercfg はコンソールのコードページ（日本語なら Shift_JIS）で出力するので、それに合わせて読む
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var psi = SystemTool.Start("powercfg.exe", args);
        psi.RedirectStandardOutput = true;
        psi.StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        return output;
    }
}

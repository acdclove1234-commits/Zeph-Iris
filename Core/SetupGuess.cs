// ==================================================
// SetupGuess.cs - セットアップウィザードで、端子に何がつながっているかの最初の推定と、ファンの下限の決め方
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

namespace ProFanController;

/// <summary>
/// セットアップウィザードの最初の推定（初めての人が、どれがポンプか分からなくても危なくないように）。
/// ・名前にポンプ（Pump・AIO）と付いている、または 2200 rpm より速い → ポンプ（ポンプは、ふだん BIOS に任せたまま監視だけ）
/// ・0 rpm → 使わない（何もつながっていないか、止まっているファン。あとで回ったら「ファン」に戻す）
/// ・1500 rpm 以上で回っている「ファン」は、ポンプかもしれないので、下限を上げて遅くしすぎない
/// </summary>
public static class SetupGuess
{
    public const string Fan = FanRoles.Fan, Pump = FanRoles.Pump, Unused = "Unused";
    /// <summary>これより速ければポンプとみなす</summary>
    public const double PumpRpm = 2200;
    /// <summary>これ以上で回っているファンは、ポンプかもしれない</summary>
    public const double MaybePumpRpm = 1500;
    /// <summary>回っていれば「つながっている」とみなす（テストで回った・止まっていたファンが回り出した）</summary>
    public const double SpinningRpm = 200;
    /// <summary>測っていないファンの下限（%）</summary>
    public const double DefaultFanMinDuty = 25;

    public static string Role(string name, double rpm)
    {
        if (LooksLikePump(name)) return Pump;
        if (rpm > PumpRpm) return Pump;
        if (rpm < 1) return Unused;
        return Fan;
    }

    public static bool LooksLikePump(string name) =>
        name.Contains("pump", StringComparison.OrdinalIgnoreCase) || name.Contains("aio", StringComparison.OrdinalIgnoreCase);

    /// <summary>「ファン」にしたけれど、ポンプかもしれない（速く回っている）</summary>
    public static bool MaybePump(string role, double rpm) => role == Fan && rpm >= MaybePumpRpm;

    /// <summary>
    /// ファンの下限（%）。測ってあれば「止まらない最低」＋5（60 まで）。
    /// 測っていなくて、最初に見たとき速く回っていた（ポンプかもしれない）なら、ポンプの下限より下げない
    /// </summary>
    public static double FanMinDuty(double? stopDuty, double firstRpm, double pumpMinDuty)
    {
        if (stopDuty is { } s) return Math.Min(60, s + 5);
        return firstRpm >= MaybePumpRpm ? Math.Max(DefaultFanMinDuty, pumpMinDuty) : DefaultFanMinDuty;
    }
}

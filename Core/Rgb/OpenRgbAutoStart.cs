// ==================================================
// OpenRgbAutoStart.cs - OpenRGB が動いていないとき、SDK サーバーをオンにして起動する
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using Microsoft.Win32;

namespace ProFanController;

/// <summary>
/// OpenRGB につながらないとき、同じ PC の OpenRGB を起動する。
///   1. OpenRGB がサービスとして入っていて（1.0 以降）、止まっていれば、サービスを起動する（無効にしてあれば触らない）
///   2. OpenRGB がどこにも動いていなければ、Program Files の OpenRGB.exe を「--server --startminimized」で起動する
/// このアプリは管理者権限で動くので、起動するのは管理者しか書き換えられない場所（Program Files）の exe だけ。
/// ほかの場所（zip を展開したフォルダーなど）の exe は、すり替えられると管理者権限で動いてしまうので起動しない。
/// 動いている OpenRGB を止めたり起動し直したりはしない（使っている人の OpenRGB に手を出さない）
/// </summary>
public sealed class OpenRgbAutoStart
{
    public const string ServiceName = "OpenRGB";
    public const string ExeName = "OpenRGB.exe";
    public const string Arguments = "--server --startminimized";

    /// <summary>このアプリを起動しているあいだに試す回数（起動してもすぐ落ちる OpenRGB を何度も起動しない）</summary>
    public const int MaxAttempts = 3;
    /// <summary>次に試すまでの間（OpenRGB がデバイスを探し終わるのを待つ）</summary>
    public const double RetryGapSec = 120;
    /// <summary>つながらない状態がこれだけ続いてから起動する（一瞬切れただけのときは起動しない）</summary>
    public const double FailingBeforeSec = 8;
    /// <summary>Windows の起動直後は、OpenRGB 自身の自動起動を先に待つ（2 つ起動しないように）</summary>
    public const double MinUptimeSec = 60;
    /// <summary>これだけ長くつながっていたら、試した回数を数え直す（あとで OpenRGB が落ちたときも起動できるように）</summary>
    public const double HealthySec = 600;

    public enum Action { None, StartService, StartExe }

    /// <summary>この PC の OpenRGB の様子（テストでは作り物を渡す）</summary>
    public readonly record struct Facts(bool ServiceInstalled, bool ServiceDisabled, bool Running, string? Exe);

    private readonly object _lock = new();
    private int _attempts;
    private double _lastAttempt = double.NegativeInfinity;
    private double _failingSince = double.NaN;
    private double _connectedAt = double.NaN;

    /// <summary>何をするか（サービスを起動する・exe を起動する・何もしない）</summary>
    public static Action Decide(Facts f)
    {
        if (f.Running) return Action.None;                                      // 動いている（サービスでも画面でも）
        if (f.ServiceInstalled && !f.ServiceDisabled) return Action.StartService;
        return f.Exe is null ? Action.None : Action.StartExe;
    }

    /// <summary>同じ PC の OpenRGB を指しているか（ほかの PC の OpenRGB は起動できない）</summary>
    public static bool IsLocal(string? host)
    {
        string h = (host ?? "").Trim().Trim('[', ']');
        return h.Length == 0 || h.Equals("localhost", StringComparison.OrdinalIgnoreCase) || h is "127.0.0.1" or "::1" or "0.0.0.0"
               || h.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>いま試してよいか（つながらない時間・前に試してからの時間・回数・Windows の起動からの時間）</summary>
    public bool ShouldTry(double now, double uptimeSec)
    {
        if (double.IsNaN(_failingSince)) _failingSince = now;
        return _attempts < MaxAttempts && now - _failingSince >= FailingBeforeSec && now - _lastAttempt >= RetryGapSec
               && uptimeSec >= MinUptimeSec;
    }

    /// <summary>起動を試した（count: 回数に数える）</summary>
    public void MarkAttempt(double now, bool count = true)
    {
        if (count) _attempts++;
        _lastAttempt = now;
    }

    /// <summary>OpenRGB につながった</summary>
    public void Connected(double now)
    {
        lock (_lock)
        {
            _failingSince = double.NaN;
            if (double.IsNaN(_connectedAt)) _connectedAt = now;
        }
    }

    /// <summary>接続が切れた・つながらなかった</summary>
    public void Disconnected(double now)
    {
        lock (_lock)
        {
            if (!double.IsNaN(_connectedAt) && now - _connectedAt >= HealthySec) _attempts = 0;
            _connectedAt = double.NaN;
        }
    }

    /// <summary>
    /// つながらないときに呼ぶ。起動したら、画面に出す文を返す（何もしなければ null）。
    /// force: 「接続テスト」から（待たずに、回数にも数えずに試す）
    /// </summary>
    public string? OnFailure(RgbConfig cfg, double now, bool force = false)
    {
        if (!cfg.AutoStartOpenRgb || !IsLocal(cfg.Host)) return null;
        lock (_lock) return Launch(now, force);
    }

    private string? Launch(double now, bool force)
    {
        if (!force && !ShouldTry(now, Environment.TickCount64 / 1000.0)) return null;
        var facts = Inspect();
        var action = Decide(facts);
        if (action == Action.None) return null;
        MarkAttempt(now, count: !force);
        try
        {
            if (action == Action.StartService)
            {
                using var p = Process.Start(SystemTool.Start("sc.exe", $"start {ServiceName}"));
                p?.WaitForExit(5000);
                Logger.Info($"OpenRGB: つながらないので、OpenRGB のサービスを起動しました（sc の結果: {(p is { HasExited: true } ? p.ExitCode : -1)}）");   // no-loc（ログだけ）
                return L("OpenRGB のサービスを起動しました。デバイスを探し終わるまで、少し待ちます…");
            }
            Process.Start(new ProcessStartInfo(facts.Exe!, Arguments)
            {
                UseShellExecute = true,   // ハンドルを受け継がせない（このアプリのポートなどを OpenRGB が持ったままにならないように）
                WorkingDirectory = Path.GetDirectoryName(facts.Exe!)!,
                WindowStyle = ProcessWindowStyle.Minimized,
            })?.Dispose();
            Logger.Info($"OpenRGB: つながらないので、OpenRGB を起動しました: {facts.Exe} {Arguments}");   // no-loc（ログだけ）
            return L("OpenRGB を起動しました（SDK サーバーもオン）。デバイスを探し終わるまで、少し待ちます…");
        }
        catch (Exception ex)
        {
            Logger.Warn($"OpenRGB を起動できませんでした: {ex.Message}");   // no-loc（ログだけ）
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // この PC の OpenRGB を調べる
    // ---------------------------------------------------------------------

    public static Facts Inspect()
    {
        bool running = false;
        try
        {
            var ps = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName));
            running = ps.Length > 0;
            foreach (var p in ps) p.Dispose();
        }
        catch { /* 一覧が取れなければ、動いていないとみなす */ }

        bool installed = false, disabled = false;
        string? serviceExe = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            if (key?.GetValue("ImagePath") is string image && image.Contains(ExeName, StringComparison.OrdinalIgnoreCase))
            {
                installed = true;
                disabled = key.GetValue("Start") is int start && start == 4;
                serviceExe = ExeFromCommand(image);
            }
        }
        catch { /* 読めなければ、サービスはないとみなす */ }

        return new Facts(installed, disabled, running, FindExe(serviceExe));
    }

    /// <summary>起動してよい OpenRGB.exe（Program Files の中にあるものだけ）</summary>
    public static string? FindExe(string? hint = null)
    {
        var candidates = new List<string>();
        if (hint is not null) candidates.Add(hint);
        foreach (var root in ProtectedRoots())
        {
            candidates.Add(Path.Combine(root, "OpenRGB", ExeName));
            candidates.Add(Path.Combine(root, "OpenRGB", "OpenRGB Windows 64-bit", ExeName));
        }
        return candidates.FirstOrDefault(c => IsProtectedPath(c) && File.Exists(c));
    }

    /// <summary>管理者しか書き換えられない場所（Program Files）の中か</summary>
    public static bool IsProtectedPath(string path) => IsUnder(path, ProtectedRoots());

    public static bool IsUnder(string path, IEnumerable<string> roots)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }
        return roots.Any(r => r.Length > 0 && full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ProtectedRoots() =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>"C:\…\OpenRGB.exe" --service のようなコマンドから exe のパスを取り出す</summary>
    public static string? ExeFromCommand(string command)
    {
        string c = command.Trim();
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c[..(exe + 4)] : null;
    }
}

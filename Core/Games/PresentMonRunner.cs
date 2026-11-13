// ==================================================
// PresentMonRunner.cs - Intel の PresentMon（MIT License・アプリのフォルダーに同梱）を動かして、
//                       描いたフレームを 1 枚ずつ受け取る
// Dependencies: .NET 8 (MIT License) / PresentMon (MIT License, Intel Corporation)
// See CREDITS.txt for full license details.
// ==================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProFanController;

/// <summary>
/// PresentMon をコンソールなしで動かし、CSV を標準出力で受け取る。
/// Windows のイベントトレース（ETW）で、どのアプリが何 ms ごとに描いたかだけを見る（画面の中身は見ない）。
/// 負担を軽くするため、表示・GPU・入力の追跡は切る（FPS とフレーム時間だけ）。
/// 止めるときは、プロセスを終わらせたあと、残ったトレースのセッションも閉じる
/// </summary>
public sealed class PresentMonRunner : IDisposable
{
    public const string SessionName = "Zeph-Iris";
    public const string Folder = "PresentMon";
    public const string ExeName = "PresentMon.exe";

    public static readonly string[] Arguments =
    {
        "--output_stdout", "--no_console_stats", "--session_name", SessionName, "--stop_existing_session",
        "--no_track_display", "--no_track_gpu", "--no_track_input", "--v1_metrics", "--exclude", "dwm.exe",
    };

    private readonly object _gate = new();
    private Process? _process;
    private bool _stopping;

    /// <summary>止めていないのに終わった（終了コード）</summary>
    public event Action<int>? Exited;

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, Folder, ExeName);
    public static bool Available => File.Exists(ExePath);

    public bool Running
    {
        get { lock (_gate) return _process is { HasExited: false }; }
    }

    public void Start(Action<PresentFrame> onFrame)
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) return;
            _stopping = false;
            // 管理者権限で動かすので、Intel の正しい署名があるものだけを動かす（すり替えられた exe は動かさない）
            var sign = Authenticode.Inspect(ExePath);
            if (sign.State != SignState.Valid || !SignaturePolicy.IsOrganization(sign.Subject, "Intel Corporation"))
                throw new InvalidOperationException(L("PresentMon の電子署名を確かめられません（Intel の署名がありません）。アプリを入れ直してください。"));
            var psi = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(ExePath)!,
            };
            foreach (var a in Arguments) psi.ArgumentList.Add(a);
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var csv = new PresentMonCsv();
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { } line && csv.TryParse(line, out var f)) onFrame(f);
            };
            p.ErrorDataReceived += (_, _) => { };   // 警告（権限など）は読み捨てる
            p.Exited += (_, _) =>
            {
                int code = -1;
                try { code = p.ExitCode; } catch { }
                bool expected;
                lock (_gate) expected = _stopping || _process != p;
                if (!expected) Exited?.Invoke(code);
            };
            p.Start();
            ChildJob.Add(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _process = p;
        }
    }

    public void Stop()
    {
        Process? p;
        lock (_gate)
        {
            _stopping = true;
            p = _process;
            _process = null;
        }
        if (p is null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
        finally
        {
            p.Dispose();
        }
        CloseTraceSession();
    }

    /// <summary>終わらせた PresentMon が残したトレースのセッションを閉じる</summary>
    private static void CloseTraceSession()
    {
        if (!Available) return;
        try
        {
            var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "--terminate_existing_session", "--session_name", SessionName }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return;
            if (!p.WaitForExit(5000)) p.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    public void Dispose() => Stop();
}

/// <summary>いちばん手前のウィンドウのプロセス ID</summary>
public static class ForegroundApp
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static int? ProcessId()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        GetWindowThreadProcessId(h, out uint pid);
        return pid == 0 ? null : (int)pid;
    }
}

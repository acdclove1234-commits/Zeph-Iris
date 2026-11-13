// ==================================================
// ChildJob.cs - このアプリが起動した補助のプロセス（PresentMon）を、アプリが落ちても残さない
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProFanController;

/// <summary>
/// Windows のジョブ（KILL_ON_JOB_CLOSE）にプロセスを入れる。
/// このアプリが終わる・落ちると、ジョブが閉じて、入れたプロセスも Windows が終わらせる
/// </summary>
public static class ChildJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    private static readonly Lazy<IntPtr> Job = new(() =>
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimit>())) return IntPtr.Zero;
        return job;   // アプリが終わるまで閉じない（閉じたときに中のプロセスが終わる）
    });

    /// <summary>入れられたら true</summary>
    public static bool Add(Process p)
    {
        try
        {
            var job = Job.Value;
            return job != IntPtr.Zero && AssignProcessToJobObject(job, p.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

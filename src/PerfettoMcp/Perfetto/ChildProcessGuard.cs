using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PerfettoMcp.Logging;

namespace PerfettoMcp.Perfetto;

// Windows: a kill-on-close job object ends trace_processor children even on a hard kill (Linux: OrphanRegistry).
internal static class ChildProcessGuard
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lazy<IntPtr> Job = new(CreateJob);

    public static void Attach(Process child)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        IntPtr job = Job.Value;
        if (job == IntPtr.Zero)
        {
            return;
        }
        try
        {
            if (!AssignProcessToJobObject(job, child.Handle))
            {
                Log.Debug($"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"job assignment failed: {ex.Message}");
        }
    }

    private static IntPtr CreateJob()
    {
        try
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            JobObjectExtendedLimitInformationData info = new();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            int size = Marshal.SizeOf<JobObjectExtendedLimitInformationData>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                {
                    Log.Debug($"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            // Never closed on purpose: process exit closes it, which triggers kill-on-close.
            return job;
        }
        catch (Exception ex)
        {
            Log.Debug($"job object unavailable: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationData
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}

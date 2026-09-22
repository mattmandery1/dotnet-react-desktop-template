using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dotnet10Template.RuntimeHost;

internal sealed class WindowsJobObject : IDisposable
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private readonly RuntimeHostLog log;
    private SafeFileHandle? handle;
    private bool disposed;

    public WindowsJobObject(RuntimeHostLog log)
    {
        this.log = log;

        if (!OperatingSystem.IsWindows())
        {
            log.Append("Job Object fallback unavailable because this platform is not Windows.");
            return;
        }

        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, pointer, (uint)length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        log.Append("Windows Job Object created with KILL_ON_JOB_CLOSE fallback.");
    }

    public void Add(Process process)
    {
        if (handle is null)
        {
            return;
        }

        if (!AssignProcessToJobObject(handle, process.Handle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"AssignProcessToJobObject failed for child PID {process.Id}.");
        }

        log.Append($"Assigned child PID {process.Id} to RuntimeHost Job Object.");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        log.Append("Closing RuntimeHost Job Object; remaining assigned child processes will be terminated by Windows if graceful cleanup did not complete.");
        handle?.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle hJob,
        uint jobObjectInformationClass,
        IntPtr lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

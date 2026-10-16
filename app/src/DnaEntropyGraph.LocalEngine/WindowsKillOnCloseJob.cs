using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DnaEntropyGraph.LocalEngine;

/// <summary>
/// A Windows Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> (issue #734). Windows ends every process in the job when the last handle to
/// it closes, and the handle closes when this app dies for any reason (a crash, Task Manager, a power loss of the session), which no
/// <c>Closing</c> event can promise. Children started by a child are in the job too. Dispose closes the handle, so it ends every child.
/// <para>
/// THEORY (unverified): a process is assigned a few milliseconds after it starts, so anything it spawned in that gap is outside the job; the worker
/// is a Python that spawns nothing that early.
/// </para>
/// </summary>
public sealed class WindowsKillOnCloseJob : IChildProcessGroup, IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private IntPtr _job;

    public WindowsKillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Job Objects are Windows only.");
        }

        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var info = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = KillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                var error = Marshal.GetLastWin32Error();
                Dispose();
                throw new Win32Exception(error);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var job = _job;
        if (job == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(WindowsKillOnCloseJob));
        }

        if (!AssignProcessToJobObject(job, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        var job = Interlocked.Exchange(ref _job, IntPtr.Zero);
        if (job != IntPtr.Zero)
        {
            CloseHandle(job);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
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
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

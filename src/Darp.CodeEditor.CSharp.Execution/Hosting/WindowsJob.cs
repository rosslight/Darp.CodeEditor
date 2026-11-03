using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Darp.CodeEditor.CSharp.Execution.Hosting;

/// <summary>A job object that kills its processes when its last handle closes, including when the host dies.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly SafeJobHandle _handle;

    private WindowsJob(SafeJobHandle handle)
    {
        _handle = handle;
    }

    public static WindowsJob Create()
    {
        // Not inheritable: only the host holds a handle, so the job closes when the host dies.
        SafeJobHandle handle = CreateJobObject(0, null);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create a job object for the runner.");
        var limits = new ExtendedLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose };
        if (
            !SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformation,
                ref limits,
                Marshal.SizeOf<ExtendedLimitInformation>()
            )
        )
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "Could not configure the runner's job object.");
        }
        return new WindowsJob(handle);
    }

    /// <summary>Assigns the process; fails rather than leave a runner the host does not own.</summary>
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.SafeHandle))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not assign the runner to its job object.");
    }

    public void Dispose() => _handle.Dispose();

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateJobObjectW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16
    )]
    private static partial SafeJobHandle CreateJobObject(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        ref ExtendedLimitInformation information,
        int length
    );

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>JOBOBJECT_EXTENDED_LIMIT_INFORMATION; nint and nuint keep the layout right on 32 and 64 bit.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}

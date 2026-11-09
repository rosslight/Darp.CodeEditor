using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Darp.CodeEditor.Lsp;

/// <summary>
/// On Windows, a Job Object that kills its processes when its last handle closes. The app holds the only handle and
/// never closes it, so Windows closes it when the app ends, also when the app is killed, and the processes end with it.
/// </summary>
/// <remarks>
/// A process joins right after it starts. A child that it starts before that moment is not in the job; language
/// servers that watch the parent process id from <c>initialize</c> end anyway. Elsewhere this does nothing.
/// </remarks>
internal static partial class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [SupportedOSPlatform("windows")]
    private static readonly Lazy<nint> s_job = new(Create);

    /// <summary>Adds <paramref name="process"/>, which has just started, to the app's job.</summary>
    /// <exception cref="Win32Exception">Windows refused.</exception>
    public static void Add(Process process)
    {
        if (OperatingSystem.IsWindows() && !AssignProcessToJobObject(s_job.Value, process.Handle))
            throw new Win32Exception();
    }

    [SupportedOSPlatform("windows")]
    private static nint Create()
    {
        nint job = CreateJobObject(0, null);
        if (job == 0)
            throw new Win32Exception();
        var information = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (
            !SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformation,
                in information,
                Unsafe.SizeOf<ExtendedLimitInformation>()
            )
        )
        {
            throw new Win32Exception();
        }
        return job;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateJobObjectW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16
    )]
    private static partial nint CreateJobObject(nint attributes, string? name);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        nint job,
        int informationClass,
        in ExtendedLimitInformation information,
        int length
    );

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    /// <summary><c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
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
    }

    /// <summary><c>IO_COUNTERS</c>.</summary>
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

    /// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}

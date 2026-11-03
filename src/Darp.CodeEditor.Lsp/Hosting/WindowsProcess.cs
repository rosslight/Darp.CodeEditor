using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Darp.CodeEditor.Lsp.Hosting;

/// <summary>
/// A child process on Windows, in its own job object; see <see cref="ChildProcess"/>. <see cref="Process"/> cannot
/// create a process suspended, so this calls <c>CreateProcessW</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsProcess : ChildProcess.IPlatformProcess
{
    private const int CreateSuspended = 0x00000004;
    private const int CreateUnicodeEnvironment = 0x00000400;
    private const int ExtendedStartupInfoPresent = 0x00080000;
    private const int CreateNoWindow = 0x08000000;
    private const int StartFUseStdHandles = 0x00000100;
    private const int HandleFlagInherit = 0x00000001;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private static readonly nint s_procThreadAttributeHandleList = 0x00020002;

    private readonly SafeProcessHandle _process;
    private readonly SafeJobHandle _job;
    private readonly RegisteredWaitHandle _exitWait;
    private readonly WaitHandle _exitHandle;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WindowsProcess(
        SafeProcessHandle process,
        SafeJobHandle job,
        int id,
        Stream standardInput,
        Stream standardOutput,
        Stream standardError
    )
    {
        _process = process;
        _job = job;
        Id = id;
        StandardInput = standardInput;
        StandardOutput = standardOutput;
        StandardError = standardError;
        _exitHandle = new EventWaitHandle(false, EventResetMode.ManualReset)
        {
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false),
        };
        _exitWait = ThreadPool.RegisterWaitForSingleObject(
            _exitHandle,
            (_, _) => _exited.TrySetResult(GetExitCodeProcess(_process, out int code) ? code : -1),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true
        );
    }

    public int Id { get; }

    public Stream StandardInput { get; }

    public Stream StandardOutput { get; }

    public Stream StandardError { get; }

    public Task<int> Exited => _exited.Task;

    public static WindowsProcess Start(ProcessStartInfo startInfo)
    {
        SafeJobHandle job = CreateKillOnCloseJob();
        var pipes = new List<SafeFileHandle>(6);
        nint attributes = 0;
        nint commandLine = 0;
        nint environment = 0;
        try
        {
            // The child's ends are inheritable, this process's ends are not.
            (SafeFileHandle inputRead, SafeFileHandle inputWrite) = CreatePipe(pipes, childReads: true);
            (SafeFileHandle outputRead, SafeFileHandle outputWrite) = CreatePipe(pipes, childReads: false);
            (SafeFileHandle errorRead, SafeFileHandle errorWrite) = CreatePipe(pipes, childReads: false);

            // Only the three pipe ends are inherited, not every inheritable handle of this process.
            nint[] inherited =
            [
                inputRead.DangerousGetHandle(),
                outputWrite.DangerousGetHandle(),
                errorWrite.DangerousGetHandle(),
            ];
            attributes = CreateHandleListAttribute(inherited, out nint handleList);
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartFUseStdHandles,
                    StandardInput = inherited[0],
                    StandardOutput = inherited[1],
                    StandardError = inherited[2],
                },
                AttributeList = attributes,
            };
            commandLine = Marshal.StringToHGlobalUni(BuildCommandLine(startInfo));
            environment = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(startInfo));
            string? workingDirectory = startInfo.WorkingDirectory is { Length: > 0 } directory ? directory : null;
            bool created;
            ProcessInformation information;
            try
            {
                created = CreateProcess(
                    null,
                    commandLine,
                    0,
                    0,
                    inheritHandles: true,
                    CreateSuspended | CreateNoWindow | ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    environment,
                    workingDirectory,
                    ref startup,
                    out information
                );
            }
            finally
            {
                Marshal.FreeHGlobal(handleList);
            }
            if (!created)
            {
                int error = Marshal.GetLastPInvokeError();
                throw new Win32Exception(
                    error,
                    $"Could not start {startInfo.FileName}: {new Win32Exception(error).Message}"
                );
            }

            var process = new SafeProcessHandle(information.Process, ownsHandle: true);
            try
            {
                // In the job before it runs a single instruction, so nothing it starts escapes the job.
                if (!AssignProcessToJobObject(job, process))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not assign the process to its job.");
                if (ResumeThread(information.Thread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not resume the process.");
            }
            catch
            {
                TerminateProcess(process, 1);
                process.Dispose();
                throw;
            }
            finally
            {
                CloseHandle(information.Thread);
            }

            // The child holds its own copies now; closing these lets this process see the end of its output.
            inputRead.Dispose();
            outputWrite.Dispose();
            errorWrite.Dispose();
            return new WindowsProcess(
                process,
                job,
                information.ProcessId,
                new FileStream(inputWrite, FileAccess.Write, 4096, isAsync: false),
                new FileStream(outputRead, FileAccess.Read, 4096, isAsync: false),
                new FileStream(errorRead, FileAccess.Read, 4096, isAsync: false)
            );
        }
        catch
        {
            foreach (SafeFileHandle pipe in pipes)
                pipe.Dispose();
            job.Dispose();
            throw;
        }
        finally
        {
            if (attributes != 0)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            Marshal.FreeHGlobal(commandLine);
            Marshal.FreeHGlobal(environment);
        }
    }

    /// <summary>Ends every process in the job, the child and what it started.</summary>
    public void Kill() => TerminateJobObject(_job, 1);

    public void Dispose()
    {
        _exitWait.Unregister(null);
        _exitHandle.Dispose();
        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        // Closing the only handle of the job also kills what is left in it.
        _job.Dispose();
        _process.Dispose();
    }

    private static SafeJobHandle CreateKillOnCloseJob()
    {
        // Not inheritable: only this process holds a handle, so the job closes when this process dies.
        SafeJobHandle job = CreateJobObject(0, null);
        if (job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create a job object.");
        var limits = new ExtendedLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose };
        if (
            !SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformation,
                ref limits,
                Marshal.SizeOf<ExtendedLimitInformation>()
            )
        )
        {
            int error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error, "Could not configure the job object.");
        }
        return job;
    }

    private static (SafeFileHandle Read, SafeFileHandle Write) CreatePipe(List<SafeFileHandle> owned, bool childReads)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
        if (!CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref security, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create a pipe.");
        owned.Add(read);
        owned.Add(write);
        SafeFileHandle own = childReads ? write : read;
        if (!SetHandleInformation(own, HandleFlagInherit, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not configure a pipe.");
        return (read, write);
    }

    /// <returns>The attribute list; <paramref name="handleList"/> holds the handles and must live until the process exists.</returns>
    private static nint CreateHandleListAttribute(nint[] handles, out nint handleList)
    {
        nint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        nint list = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            int error = Marshal.GetLastPInvokeError();
            Marshal.FreeHGlobal(list);
            throw new Win32Exception(error, "Could not create a process attribute list.");
        }
        handleList = Marshal.AllocHGlobal(nint.Size * handles.Length);
        Marshal.Copy(handles, 0, handleList, handles.Length);
        if (
            !UpdateProcThreadAttribute(
                list,
                0,
                s_procThreadAttributeHandleList,
                handleList,
                nint.Size * handles.Length,
                0,
                0
            )
        )
        {
            int error = Marshal.GetLastPInvokeError();
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            Marshal.FreeHGlobal(handleList);
            throw new Win32Exception(error, "Could not set the inherited handles.");
        }
        return list;
    }

    /// <summary>Quotes the program and the arguments so that the C runtime's parser reads them back unchanged.</summary>
    internal static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var builder = new StringBuilder();
        AppendArgument(builder, startInfo.FileName);
        foreach (string argument in startInfo.ArgumentList)
        {
            builder.Append(' ');
            AppendArgument(builder, argument);
        }
        return builder.ToString();
    }

    private static void AppendArgument(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            builder.Append(argument);
            return;
        }
        builder.Append('"');
        for (int i = 0; i < argument.Length; )
        {
            int backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }
            if (i == argument.Length)
            {
                // Doubled, so that they do not escape the closing quote.
                builder.Append('\\', backslashes * 2);
                break;
            }
            if (argument[i] == '"')
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            else
                builder.Append('\\', backslashes).Append(argument[i]);
            i++;
        }
        builder.Append('"');
    }

    /// <summary>The environment as <c>KEY=VALUE</c> strings separated and ended by NUL, sorted as Windows expects.</summary>
    private static string BuildEnvironmentBlock(ProcessStartInfo startInfo)
    {
        var builder = new StringBuilder();
        foreach (
            (string key, string? value) in startInfo.Environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
        )
        {
            if (value is not null)
                builder.Append(key).Append('=').Append(value).Append('\0');
        }
        return builder.Append('\0').ToString();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        ref SecurityAttributes pipeAttributes,
        int size
    );

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(SafeFileHandle handle, int mask, int flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        nint attributeList,
        int attributeCount,
        int flags,
        ref nint size
    );

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        nint attributeList,
        int flags,
        nint attribute,
        nint value,
        nint size,
        nint previousValue,
        nint returnSize
    );

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateProcessW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcess(
        string? applicationName,
        nint commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        int creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation
    );

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out int exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

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
    private static partial bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    /// <summary>STARTUPINFOW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    /// <summary>STARTUPINFOEXW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    /// <summary>PROCESS_INFORMATION.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

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

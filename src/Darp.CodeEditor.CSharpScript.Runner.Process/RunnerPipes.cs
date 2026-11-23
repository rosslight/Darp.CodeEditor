using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// The app starts the runner on redirected standard streams. This moves the connection to the app off them, before any
/// code of the script API loads, so that a program a script starts has valid streams and can't touch the connection:
/// it reads an empty standard input, and what it or native code prints goes to the log pipe, which is standard error.
/// </summary>
internal static partial class RunnerPipes
{
    /// <summary>Keeps the null device open for the process's whole life, as its standard input.</summary>
    private static SafeFileHandle? s_nullInput;

    /// <summary>Returns the connection's streams, on descriptors that child processes don't inherit.</summary>
    public static (Stream Input, Stream Output) TakeConnection()
    {
        (SafeFileHandle input, SafeFileHandle output) = OperatingSystem.IsWindows() ? MoveOnWindows() : MoveOnUnix();
        // UTF-8 whatever the platform's console code page, as the app reads it. The runtime's crash reports go straight
        // to standard error, so they reach the same pipe.
        TextWriter log = TextWriter.Synchronized(
            new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true }
        );
        Console.SetOut(log);
        Console.SetError(log);
        return (
            new FileStream(input, FileAccess.Read, bufferSize: 0),
            new FileStream(output, FileAccess.Write, bufferSize: 0)
        );
    }

    private static (SafeFileHandle Input, SafeFileHandle Output) MoveOnUnix()
    {
        const int standardInput = 0;
        const int standardOutput = 1;
        const int standardError = 2;
        var input = new SafeFileHandle(Private(standardInput), ownsHandle: true);
        var output = new SafeFileHandle(Private(standardOutput), ownsHandle: true);
        using (
            SafeFileHandle nullDevice = File.OpenHandle(
                "/dev/null",
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            )
        )
            Check(dup2((int)nullDevice.DangerousGetHandle(), standardInput));
        Check(dup2(standardError, standardOutput));
        return (input, output);

        // FIOCLEX rather than fcntl, which is variadic: on Apple's arm64 a variadic argument goes on the stack.
        static nint Private(int descriptor)
        {
            int moved = Check(dup(descriptor));
            Check(ioctl(moved, OperatingSystem.IsMacOS() ? 0x20006601u : 0x5451u));
            return moved;
        }
    }

    private static int Check(int result) =>
        result >= 0 ? result : throw new Win32Exception(Marshal.GetLastPInvokeError());

    [SupportedOSPlatform("windows")]
    private static (SafeFileHandle Input, SafeFileHandle Output) MoveOnWindows()
    {
        const int standardInput = -10;
        const int standardOutput = -11;
        const int standardError = -12;
        const uint inheritFlag = 1;
        nint input = GetStdHandle(standardInput);
        nint output = GetStdHandle(standardOutput);
        CheckWindows(SetHandleInformation(input, inheritFlag, 0));
        CheckWindows(SetHandleInformation(output, inheritFlag, 0));
        s_nullInput = File.OpenHandle(
            "NUL",
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Inheritable
        );
        CheckWindows(SetStdHandle(standardInput, s_nullInput.DangerousGetHandle()));
        CheckWindows(SetStdHandle(standardOutput, GetStdHandle(standardError)));
        return (new SafeFileHandle(input, ownsHandle: true), new SafeFileHandle(output, ownsHandle: true));
    }

    private static void CheckWindows(bool succeeded)
    {
        if (!succeeded)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup(int descriptor);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup2(int descriptor, int target);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int ioctl(int descriptor, nuint request);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int standardHandle);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int standardHandle, nint handle);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);
}

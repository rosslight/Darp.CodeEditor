using System.Globalization;
using System.Runtime.InteropServices;
using Darp.CodeEditor.CSharp.Execution.Protocol;

namespace Darp.CodeEditor.CSharp.Runner;

internal static class ExitCodes
{
    public const int Success = 0;

    /// <summary>The host died before the runner could bind its lifetime to it.</summary>
    public const int ParentGone = 3;
}

/// <summary>Ends the runner when the host dies, even while a script is busy.</summary>
/// <remarks>
/// Every platform: the runner exits when its input closes, which happens when the host dies. That needs a responsive
/// runtime, so Linux also asks the kernel for <c>SIGKILL</c> when the host thread that started the runner ends, and
/// macOS polls the parent process ID. On Windows the host's job object kills the runner.
/// </remarks>
internal static partial class ParentProcess
{
    private const int PrSetPdeathsig = 1;
    private const int SigKill = 9;

    /// <returns>False when the host is already gone.</returns>
    public static bool Bind(string[] args)
    {
        int? parent = ParseParent(args);
        if (OperatingSystem.IsLinux())
        {
            if (prctl(PrSetPdeathsig, SigKill, 0, 0, 0) != 0)
            {
                throw new InvalidOperationException(
                    $"prctl(PR_SET_PDEATHSIG) failed with error {Marshal.GetLastPInvokeError()}."
                );
            }
            // The host may have died before prctl: then this process already has another parent.
            return parent is not { } expected || getppid() == expected;
        }
        if (OperatingSystem.IsMacOS() && parent is { } host)
        {
            if (getppid() != host)
                return false;
            var watcher = new Thread(() => WatchParent(host)) { IsBackground = true, Name = "Parent watcher" };
            watcher.Start();
        }
        return true;
    }

    private static void WatchParent(int parent)
    {
        while (getppid() == parent)
            Thread.Sleep(500);
        _exit(ExitCodes.ParentGone);
    }

    private static int? ParseParent(string[] args)
    {
        int index = Array.IndexOf(args, RunnerNames.ParentProcessIdArgument);
        return index >= 0 && index + 1 < args.Length
            ? int.Parse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture)
            : null;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    [LibraryImport("libc")]
    private static partial int getppid();

    [LibraryImport("libc")]
    private static partial void _exit(int status);
}

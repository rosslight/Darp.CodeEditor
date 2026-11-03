using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Darp.CodeEditor.Lsp.Hosting;

/// <summary>A child process on Linux, macOS and other Unix systems; see <see cref="ChildProcess"/>.</summary>
internal sealed class UnixProcess : ChildProcess.IPlatformProcess
{
    private readonly Process _process;

    private UnixProcess(Process process)
    {
        _process = process;
        Id = process.Id;
        Exited = WaitForExitAsync(process);
    }

    public int Id { get; }

    public Stream StandardInput => _process.StandardInput.BaseStream;

    public Stream StandardOutput => _process.StandardOutput.BaseStream;

    public Stream StandardError => _process.StandardError.BaseStream;

    public Task<int> Exited { get; }

    public static async Task<UnixProcess> StartAsync(ProcessStartInfo startInfo)
    {
        Process process = await LauncherThread
            .RunAsync(() =>
            {
                ProcessStartInfo start =
                    OperatingSystem.IsLinux() && DeathSignal.IsAvailable
                        ? DeathSignal.Wrap(startInfo)
                        : Copy(startInfo, startInfo.FileName, startInfo.ArgumentList);
                return Process.Start(start)
                    ?? throw new InvalidOperationException($"{startInfo.FileName} did not start.");
            })
            .ConfigureAwait(false);
        return new UnixProcess(process);
    }

    public void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It already exited.
        }
    }

    public void Dispose() => _process.Dispose();

    private static async Task<int> WaitForExitAsync(Process process)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static ProcessStartInfo Copy(ProcessStartInfo source, string fileName, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = source.WorkingDirectory,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach ((string key, string? value) in source.Environment)
            start.Environment[key] = value;
        return start;
    }

    /// <summary>Asks the kernel for <c>SIGKILL</c> when the thread that started the child ends; Linux only.</summary>
    private static class DeathSignal
    {
        private static readonly Lazy<bool> s_available = new(Probe);

        /// <summary>Whether <c>setpriv</c> and the shell check work here. Checked once, on the launcher thread.</summary>
        public static bool IsAvailable => s_available.Value;

        /// <summary>
        /// Runs <c>setpriv --pdeathsig KILL -- /bin/sh -c 'check; exec "$0" "$@"' program arguments...</c>. The child
        /// keeps its process id through both <c>exec</c>s, and the signal setting survives them.
        /// </summary>
        public static ProcessStartInfo Wrap(ProcessStartInfo startInfo) =>
            Copy(
                startInfo,
                "setpriv",
                ["--pdeathsig", "KILL", "--", "/bin/sh", "-c", Script, startInfo.FileName, .. startInfo.ArgumentList]
            );

        /// <summary>
        /// <c>$PPID</c> is read when the shell starts, after <c>setpriv</c> armed the signal: if it is no longer this
        /// process, this process died before, and the child would never get the signal.
        /// </summary>
        private static string Script { get; } =
            string.Create(
                CultureInfo.InvariantCulture,
                $"[ \"$PPID\" = \"{Environment.ProcessId}\" ] || exit {ChildProcess.ParentGoneExitCode}; exec \"$0\" \"$@\""
            );

        private static bool Probe()
        {
            try
            {
                using Process? probe = Process.Start(Wrap(new ProcessStartInfo("true")));
                if (probe is not null && probe.WaitForExit(TimeSpan.FromSeconds(5)) && probe.ExitCode == 0)
                    return true;
            }
            catch (Win32Exception) { }
            Trace.TraceWarning(
                "setpriv --pdeathsig is not available: child processes start without being tied to this process."
            );
            return false;
        }
    }

    /// <summary>
    /// Starts processes from one thread that never ends: Linux sends the death signal when the thread that started a
    /// child ends, not only when the process does.
    /// </summary>
    private static class LauncherThread
    {
        private static readonly BlockingCollection<Action> s_work = [];
        private static readonly Lazy<Thread> s_thread = new(() =>
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "Child process launcher" };
            thread.Start();
            return thread;
        });

        public static Task<T> RunAsync<T>(Func<T> work)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = s_thread.Value;
            s_work.Add(() =>
            {
                try
                {
                    result.SetResult(work());
                }
                catch (Exception exception)
                {
                    result.SetException(exception);
                }
            });
            return result.Task;
        }

        private static void Run()
        {
            foreach (Action work in s_work.GetConsumingEnumerable())
                work();
        }
    }
}

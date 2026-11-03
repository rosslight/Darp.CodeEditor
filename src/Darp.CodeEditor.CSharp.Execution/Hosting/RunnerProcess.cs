using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Darp.CodeEditor.CSharp.Execution.Protocol;

namespace Darp.CodeEditor.CSharp.Execution.Hosting;

/// <summary>A runner process the host owns: it ends when the host disposes it and when the host dies.</summary>
/// <remarks>
/// Linux: the runner kills itself with <c>PR_SET_PDEATHSIG</c> when the thread that started it ends, so runners are
/// started from <see cref="LauncherThread"/>, which lives as long as the app. Windows: the runner is assigned to a job
/// object that kills it when the host closes the job's only handle, which the OS does when the host dies.
/// </remarks>
internal sealed class RunnerProcess : IDisposable
{
    private const int StandardErrorLines = 40;

    private readonly Process _process;
    private readonly IDisposable? _job;
    private readonly ConcurrentQueue<string> _standardError = new();
    private readonly Task _standardErrorDrained;

    private RunnerProcess(Process process, IDisposable? job, Action<string>? output)
    {
        _process = process;
        _job = job;
        Id = process.Id;
        Input = process.StandardOutput.BaseStream;
        Output = process.StandardInput.BaseStream;
        _standardErrorDrained = DrainStandardErrorAsync(process.StandardError, output);
        Exited = process.WaitForExitAsync();
    }

    public int Id { get; }

    /// <summary>What the runner writes: its standard output.</summary>
    public Stream Input { get; }

    /// <summary>What the runner reads: its standard input.</summary>
    public Stream Output { get; }

    /// <summary>Completes when the process has exited.</summary>
    public Task Exited { get; }

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    /// <summary>The last lines the runner wrote to standard error.</summary>
    public async Task<string> GetStandardErrorAsync()
    {
        // The pipe closes when the process exits, unless a process the script started still holds it.
        await Task.WhenAny(_standardErrorDrained, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        return string.Join('\n', _standardError);
    }

    public static async Task<RunnerProcess> StartAsync(RunnerLocation location, Action<string>? output)
    {
        var start = new ProcessStartInfo(location.Executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(location.Executable),
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(RunnerNames.ParentProcessIdArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (location.DotNetRoot is { } dotNetRoot)
            start.Environment["DOTNET_ROOT"] = dotNetRoot;

        if (OperatingSystem.IsWindows())
        {
            // The runner reads its first request only after this returns, so it runs no script before it is in the job.
            // If the host dies before the assignment, the runner reads the end of its input and exits.
            var job = WindowsJob.Create();
            Process? process = null;
            try
            {
                process = Process.Start(start) ?? throw new InvalidOperationException("The runner did not start.");
                job.Assign(process);
                return new RunnerProcess(process, job, output);
            }
            catch
            {
                job.Dispose();
                KillAndDispose(process);
                throw;
            }
        }
        Process started = await LauncherThread.StartAsync(start).ConfigureAwait(false);
        return new RunnerProcess(started, job: null, output);
    }

    /// <summary>Kills the runner and everything it started that is still in its process tree.</summary>
    public void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    public void Dispose()
    {
        Kill();
        // On Windows, closing the job also ends processes that left the runner's tree.
        _job?.Dispose();
        _process.Dispose();
    }

    private static void KillAndDispose(Process? process)
    {
        if (process is null)
            return;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        process.Dispose();
    }

    private async Task DrainStandardErrorAsync(StreamReader reader, Action<string>? output)
    {
        var lines = new OutputLines(line =>
        {
            _standardError.Enqueue(line);
            while (_standardError.Count > StandardErrorLines)
                _standardError.TryDequeue(out _);
            try
            {
                output?.Invoke(line);
            }
            catch (Exception exception)
            {
                // A failing callback must not stop the draining, or the runner blocks on a full pipe.
                Trace.TraceError($"CSharpScriptRunnerOptions.Output failed: {exception}");
            }
        });
        // Keep reading, so a runner that writes much never blocks on a full pipe, and read in chunks, not lines.
        char[] buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            lines.Write(buffer.AsSpan(0, read));
        lines.Complete();
    }

    /// <summary>Starts processes from one thread that never ends.</summary>
    private static class LauncherThread
    {
        private static readonly BlockingCollection<Action> s_work = [];
        private static readonly Lazy<Thread> s_thread = new(() =>
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "C# script runner launcher" };
            thread.Start();
            return thread;
        });

        public static Task<Process> StartAsync(ProcessStartInfo start)
        {
            var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = s_thread.Value;
            s_work.Add(() =>
            {
                try
                {
                    started.SetResult(
                        Process.Start(start) ?? throw new InvalidOperationException("The runner did not start.")
                    );
                }
                catch (Exception exception)
                {
                    started.SetException(exception);
                }
            });
            return started.Task;
        }

        private static void Run()
        {
            foreach (Action work in s_work.GetConsumingEnumerable())
                work();
        }
    }
}

/// <summary>Where the runner is, and the .NET runtime it runs on.</summary>
/// <param name="Executable">The runner's app host.</param>
/// <param name="DotNetRoot">The .NET root the app host should use, or null to let it search.</param>
internal sealed record RunnerLocation(string Executable, string? DotNetRoot)
{
    public const string RunnerName = "Darp.CodeEditor.CSharp.Runner";

    /// <summary>
    /// Finds the runner in <paramref name="runnerDirectory"/>, by default <c>helpers/csharp-runner</c> next to the app.
    /// A published app has the runtime in <c>helpers/dotnet</c>; in a build output the runner uses the runtime that
    /// runs the host.
    /// </summary>
    public static RunnerLocation Find(string? runnerDirectory)
    {
        string directory = Path.GetFullPath(
            runnerDirectory ?? Path.Combine(AppContext.BaseDirectory, "helpers", "csharp-runner")
        );
        string executable = Path.Combine(directory, OperatingSystem.IsWindows() ? RunnerName + ".exe" : RunnerName);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                $"The C# script runner is not at {executable}. Import Darp.CodeEditor.CSharp.Execution.targets into the "
                    + "app project, or set CSharpScriptRunnerOptions.RunnerDirectory.",
                executable
            );
        }
        string privateRuntime = Path.GetFullPath(Path.Combine(directory, "..", "dotnet"));
        return new RunnerLocation(executable, Directory.Exists(privateRuntime) ? privateRuntime : FindHostDotNetRoot());
    }

    private static string? FindHostDotNetRoot()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 })
            return null;
        // <root>/shared/Microsoft.NETCore.App/<version>/ when the host runs on an installed runtime.
        string runtime = RuntimeEnvironment.GetRuntimeDirectory();
        string root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        if (Directory.Exists(Path.Combine(root, "host", "fxr")))
            return root;
        // Set by the dotnet CLI for the processes it starts, such as test hosts.
        return Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath
            ? Path.GetDirectoryName(hostPath)
            : null;
    }
}

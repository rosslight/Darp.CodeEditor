using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace Darp.CodeEditor.Lsp.Hosting;

/// <summary>
/// A child process this process owns, talked to over its standard input and output. It ends when it is disposed and
/// when this process dies, also when this process is killed.
/// </summary>
/// <remarks>
/// <para>
/// Windows: the process is created suspended, assigned to a job object that kills its processes when the job's only
/// handle closes, and then resumed, so nothing it starts escapes the job. The OS closes the handle when this process
/// dies.
/// </para>
/// <para>
/// Linux: the process starts through <c>setpriv --pdeathsig KILL</c> from a thread that lives as long as the app, so
/// the kernel kills it when this process dies. A shell then checks that its parent is still this process, which covers
/// a death before the signal was armed, and runs the program in its place. Without a working <c>setpriv</c> the
/// process starts directly. Other platforms start it directly too; such children should exit when their input ends.
/// </para>
/// </remarks>
public sealed class ChildProcess : IAsyncDisposable
{
    /// <summary>The exit code of the Linux check when this process was gone before the child could bind to it.</summary>
    public const int ParentGoneExitCode = 125;

    private const int StandardErrorLines = 50;
    private static readonly TimeSpan s_exitTimeout = TimeSpan.FromSeconds(5);

    private readonly IPlatformProcess _process;
    private readonly Action<string>? _standardError;
    private readonly ConcurrentQueue<string> _standardErrorTail = new();
    private readonly Task _standardErrorDrained;
    private int _disposed;

    private ChildProcess(IPlatformProcess process, Action<string>? standardError)
    {
        _process = process;
        _standardError = standardError;
        _standardErrorDrained = Task.Run(() => DrainStandardErrorAsync(process.StandardError));
    }

    /// <summary>The process id.</summary>
    public int Id => _process.Id;

    /// <summary>The child's standard input: what this process writes to it.</summary>
    public Stream StandardInput => _process.StandardInput;

    /// <summary>The child's standard output: what this process reads from it.</summary>
    public Stream StandardOutput => _process.StandardOutput;

    /// <summary>Completes with the exit code when the process has exited.</summary>
    public Task<int> Exited => _process.Exited;

    /// <summary>The last lines the child wrote to standard error.</summary>
    public string StandardErrorTail => string.Join('\n', _standardErrorTail);

    /// <summary>Starts a process with redirected standard streams, without blocking the calling thread.</summary>
    /// <param name="startInfo">
    /// The program, its <see cref="ProcessStartInfo.ArgumentList"/>, environment and working directory. Redirection,
    /// shell execution and window settings are set here.
    /// </param>
    /// <param name="standardError">Receives each line the child writes to standard error, on a thread-pool thread.</param>
    public static async Task<ChildProcess> StartAsync(ProcessStartInfo startInfo, Action<string>? standardError = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.Arguments.Length > 0)
            throw new ArgumentException("Pass the arguments in ArgumentList.", nameof(startInfo));
        IPlatformProcess process = OperatingSystem.IsWindows()
            ? await StartOnWindowsAsync(startInfo).ConfigureAwait(false)
            : await UnixProcess.StartAsync(startInfo).ConfigureAwait(false);
        return new ChildProcess(process, standardError);
    }

    /// <summary>Creating a process takes milliseconds; the caller may be a UI thread.</summary>
    [SupportedOSPlatform("windows")]
    private static Task<WindowsProcess> StartOnWindowsAsync(ProcessStartInfo startInfo) =>
        Task.Run(() => WindowsProcess.Start(startInfo));

    /// <summary>Kills the process and the processes it started, as far as the platform tracks them.</summary>
    public void Kill() => _process.Kill();

    /// <summary>Kills the process, waits a few seconds for it to exit, and releases its streams.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _process.Kill();
        await Task.WhenAny(_process.Exited, Task.Delay(s_exitTimeout)).ConfigureAwait(false);
        // The pipe closes when the process exits, unless a process it started still holds it.
        await Task.WhenAny(_standardErrorDrained, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        _process.Dispose();
    }

    private async Task DrainStandardErrorAsync(Stream stream)
    {
        // Keeps reading, so a child that writes much never blocks on a full pipe.
        using var reader = new StreamReader(stream, Encoding.UTF8);
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _standardErrorTail.Enqueue(line);
                while (_standardErrorTail.Count > StandardErrorLines)
                    _standardErrorTail.TryDequeue(out _);
                try
                {
                    _standardError?.Invoke(line);
                }
                catch (Exception exception)
                {
                    Trace.TraceError($"Handling the standard error of process {Id} failed: {exception}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The pipe was closed by disposal.
        }
    }

    /// <summary>A started process with the platform's ownership in place.</summary>
    internal interface IPlatformProcess : IDisposable
    {
        int Id { get; }

        Stream StandardInput { get; }

        Stream StandardOutput { get; }

        Stream StandardError { get; }

        Task<int> Exited { get; }

        void Kill();
    }
}

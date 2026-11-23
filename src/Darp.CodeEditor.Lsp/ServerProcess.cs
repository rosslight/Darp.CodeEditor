using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Darp.CodeEditor.Lsp;

/// <summary>
/// A child process that speaks over its standard input and output, such as a language server. Each line it writes to
/// standard error goes to a callback, and the last lines stay for error messages.
/// </summary>
/// <remarks>
/// The process is a child of the app. On Windows it runs in <see cref="ChildProcessJob"/> from right after its start,
/// so it ends with the app, also when the app is killed. Elsewhere it may watch its standard input, or the parent's
/// process id that a language server receives in <c>initialize</c>.
/// </remarks>
internal sealed class ServerProcess : IDisposable
{
    /// <summary>How many of the last lines of standard error stay for error messages.</summary>
    private const int TailLines = 100;

    /// <summary>Lines of the tail are cut to this many characters.</summary>
    private const int MaxTailLineLength = 1000;

    private readonly Process _process;
    private readonly Action<string>? _standardError;

    /// <summary>The last lines of standard error; guarded by itself, since the lines arrive on another thread.</summary>
    private readonly Queue<string> _tail = new();

    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _standardErrorEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ServerProcess(Process process, Action<string>? standardError)
    {
        _process = process;
        _standardError = standardError;
    }

    /// <summary>The process id.</summary>
    public int Id => _process.Id;

    /// <summary>The process's standard input.</summary>
    public Stream Input => _process.StandardInput.BaseStream;

    /// <summary>The process's standard output.</summary>
    public Stream Output => _process.StandardOutput.BaseStream;

    /// <summary>The exit code, once the process has exited.</summary>
    public int ExitCode => _process.ExitCode;

    /// <summary>Whether the process has exited.</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>
    /// Completes when the process has exited, also while a child it left behind holds standard error open, which
    /// <see cref="WaitForExitAsync"/> waits for.
    /// </summary>
    public Task Exited => _exited.Task;

    /// <summary>Completes when standard error has been read to its end.</summary>
    public Task StandardErrorEnded => _standardErrorEnded.Task;

    /// <summary>The last lines that the process wrote to standard error, one per line.</summary>
    public string StandardErrorTail
    {
        get
        {
            lock (_tail)
                return string.Join('\n', _tail);
        }
    }

    /// <summary>Starts a process with redirected standard input, output and error.</summary>
    /// <param name="startInfo">The command line, working directory and environment; the redirection is set here.</param>
    /// <param name="standardError">Receives each line of standard error, on a thread-pool thread.</param>
    /// <exception cref="Win32Exception">The process did not start, or did not join the Job Object.</exception>
    public static ServerProcess Start(ProcessStartInfo startInfo, Action<string>? standardError)
    {
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var server = new ServerProcess(process, standardError);
        process.ErrorDataReceived += server.OnErrorData;
        process.Exited += (_, _) => server._exited.TrySetResult();
        process.Start();
        try
        {
            ChildProcessJob.Add(process);
        }
        catch
        {
            server.Dispose();
            throw;
        }
        process.BeginErrorReadLine();
        return server;
    }

    /// <summary>Completes when the process has exited and its standard error has been read to the end.</summary>
    public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _process.WaitForExitAsync(cancellationToken);

    /// <summary>Kills the process and its children, unless it has exited already.</summary>
    public void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        // It exited meanwhile, or, on Windows, is exiting.
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
    }

    /// <summary>Kills the process and releases its handles and streams.</summary>
    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }

    private void OnErrorData(object sender, DataReceivedEventArgs e)
    {
        // Null marks the end of the stream.
        if (e.Data is not { } line)
        {
            _standardErrorEnded.TrySetResult();
            return;
        }
        lock (_tail)
        {
            if (_tail.Count == TailLines)
                _tail.Dequeue();
            _tail.Enqueue(line.Length > MaxTailLineLength ? line[..MaxTailLineLength] : line);
        }
        _standardError?.Invoke(line);
    }
}

namespace Darp.CodeEditor.Lsp.Hosting;

/// <summary>The streams to a language server, when it ended, and how to end it: usually a <see cref="ChildProcess"/>.</summary>
public sealed class LanguageServerTransport : IAsyncDisposable
{
    private readonly Func<ValueTask> _dispose;
    private int _disposed;

    /// <param name="input">What the client reads: the server's output.</param>
    /// <param name="output">What the client writes: the server's input.</param>
    /// <param name="exited">Completes when the server ended.</param>
    /// <param name="dispose">Ends the server and releases the streams.</param>
    /// <param name="processId">The server's process, if it is one.</param>
    public LanguageServerTransport(
        Stream input,
        Stream output,
        Task exited,
        Func<ValueTask> dispose,
        int? processId = null
    )
    {
        Input = input;
        Output = output;
        Exited = exited;
        _dispose = dispose;
        ProcessId = processId;
    }

    /// <summary>What the client reads: the server's output.</summary>
    public Stream Input { get; }

    /// <summary>What the client writes: the server's input.</summary>
    public Stream Output { get; }

    /// <summary>Completes when the server ended.</summary>
    public Task Exited { get; }

    public int? ProcessId { get; }

    /// <summary>Starts <paramref name="startInfo"/> as a <see cref="ChildProcess"/> and talks over its standard streams.</summary>
    public static async Task<LanguageServerTransport> StartProcessAsync(
        System.Diagnostics.ProcessStartInfo startInfo,
        Action<string>? standardError = null
    )
    {
        ChildProcess process = await ChildProcess.StartAsync(startInfo, standardError).ConfigureAwait(false);
        return new LanguageServerTransport(
            process.StandardOutput,
            process.StandardInput,
            process.Exited,
            process.DisposeAsync,
            process.Id
        );
    }

    /// <summary>Ends the server, if it still runs, and releases the streams.</summary>
    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _disposed, 1) == 0 ? _dispose() : ValueTask.CompletedTask;
}

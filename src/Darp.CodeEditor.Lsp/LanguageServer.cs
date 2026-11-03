using System.Text.Json;
using Darp.CodeEditor.Lsp.Protocol;

namespace Darp.CodeEditor.Lsp;

/// <summary>The server end of LSP: a <see cref="JsonRpcConnection"/> with the lifecycle of the protocol.</summary>
/// <remarks>
/// Until an <c>initialize</c> request is answered with its result, requests answer with
/// <see cref="ErrorCodes.ServerNotInitialized"/> and notifications are dropped; a failed or cancelled initialize does
/// not count. A second <c>initialize</c> answers with <see cref="ErrorCodes.InvalidRequest"/>, as does
/// every request after <c>shutdown</c>. The <c>exit</c> notification ends the connection. Lifecycle messages run
/// <see cref="DispatchMode.Serial"/>, so the messages after them see their outcome.
/// </remarks>
public sealed class LanguageServer : IAsyncDisposable
{
    private readonly Func<InitializeParams, CancellationToken, Task<InitializeResult>> _initialize;
    private readonly Func<Task>? _shutdown;
    private State _state;
    private int _exitCode = 1;

    /// <param name="initialize">Answers <c>initialize</c>; an exception answers it with an error.</param>
    /// <param name="shutdown">Runs on <c>shutdown</c>, before it is answered.</param>
    /// <param name="log">Receives protocol errors and handler failures.</param>
    /// <param name="limits">How much the client may send; <see cref="JsonRpcLimits"/>' defaults when null.</param>
    public LanguageServer(
        Stream input,
        Stream output,
        Func<InitializeParams, CancellationToken, Task<InitializeResult>> initialize,
        Func<Task>? shutdown = null,
        Action<string>? log = null,
        JsonRpcLimits? limits = null
    )
    {
        _initialize = initialize;
        _shutdown = shutdown;
        Connection = new JsonRpcConnection(input, output, log, limits) { Filter = Admit };
        // The state changes only when the response is the result: a cancelled initialize does not count.
        Connection.OnRequest(
            LspMethods.Initialize,
            InitializeAsync,
            DispatchMode.Serial,
            answered: () => _state = State.Initialized
        );
        Connection.OnRequest(
            LspMethods.Shutdown,
            ShutdownAsync,
            DispatchMode.Serial,
            answered: () => _state = State.ShutDown
        );
        Connection.OnNotification(LspMethods.Exit, ExitAsync, DispatchMode.Serial);
    }

    private enum State
    {
        Created,
        Initialized,
        ShutDown,
    }

    /// <summary>Registers the server's handlers before <see cref="RunAsync"/>, and talks to the client.</summary>
    public JsonRpcConnection Connection { get; }

    /// <summary>Serves the client until <c>exit</c> or the end of the input.</summary>
    /// <returns>The exit code for the process: 0 after <c>shutdown</c> and <c>exit</c>, otherwise 1.</returns>
    public async Task<int> RunAsync()
    {
        Connection.Start();
        await Connection.Completion.ConfigureAwait(false);
        return _exitCode;
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();

    /// <remarks>Runs on the dispatch loop, which also runs the lifecycle handlers one at a time.</remarks>
    private JsonRpcException? Admit(string method) =>
        (_state, method) switch
        {
            (_, "exit") => null,
            (State.Created, "initialize") => null,
            (State.Created, _) => new JsonRpcException(
                ErrorCodes.ServerNotInitialized,
                "The server is not initialized."
            ),
            (State.Initialized, "initialize") => new JsonRpcException(
                ErrorCodes.InvalidRequest,
                "The server is already initialized."
            ),
            (State.ShutDown, _) => new JsonRpcException(ErrorCodes.InvalidRequest, "The server is shutting down."),
            _ => null,
        };

    private async Task<InitializeResult?> InitializeAsync(
        InitializeParams parameters,
        CancellationToken cancellationToken
    )
    {
        return await _initialize(parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement?> ShutdownAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (_shutdown is not null)
            await _shutdown().ConfigureAwait(false);
        return null;
    }

    private Task ExitAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        _exitCode = _state == State.ShutDown ? 0 : 1;
        Connection.Stop();
        return Task.CompletedTask;
    }
}

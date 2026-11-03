using System.Text.Json;
using Darp.CodeEditor.Lsp.Hosting;
using Darp.CodeEditor.Lsp.Protocol;

namespace Darp.CodeEditor.Lsp;

/// <summary>
/// The client end of LSP: initializes a server, answers the requests a server sends its client, and shuts the server
/// down.
/// </summary>
/// <remarks>
/// It answers <c>workspace/configuration</c> from <see cref="LanguageClientOptions.Settings"/>, accepts
/// <c>client/registerCapability</c>, <c>client/unregisterCapability</c> and <c>window/workDoneProgress/create</c>,
/// and logs <c>window/logMessage</c> and <c>window/showMessage</c>. <c>$/progress</c> is ignored.
/// </remarks>
public sealed class LanguageClient : IAsyncDisposable
{
    private readonly LanguageServerTransport _transport;
    private readonly LanguageClientOptions _options;
    private int _disposed;

    private LanguageClient(
        JsonRpcConnection connection,
        LanguageServerTransport transport,
        InitializeResult initialized,
        LanguageClientOptions options
    )
    {
        Connection = connection;
        _transport = transport;
        _options = options;
        Capabilities = initialized.Capabilities;
        ServerInfo = initialized.ServerInfo;
        Completion = Task.WhenAny(connection.Completion, transport.Exited);
    }

    /// <summary>Sends requests and notifications to the server.</summary>
    public JsonRpcConnection Connection { get; }

    public ServerCapabilities Capabilities { get; }

    public ServerInfo? ServerInfo { get; }

    /// <summary>The server's process, if it is one.</summary>
    public int? ProcessId => _transport.ProcessId;

    /// <summary>Completes when the connection ended or the server exited.</summary>
    public Task Completion { get; }

    /// <summary>Connects to the server and completes <c>initialize</c> and <c>initialized</c>.</summary>
    /// <remarks>The client owns <paramref name="transport"/> from now on; it is disposed when initialization fails.</remarks>
    /// <exception cref="JsonRpcException">The server refused <c>initialize</c>.</exception>
    /// <exception cref="IOException">The connection ended first.</exception>
    public static async Task<LanguageClient> StartAsync(
        LanguageServerTransport transport,
        LanguageClientOptions options,
        CancellationToken cancellationToken = default
    )
    {
        Action<string> log = options.Log ?? (static _ => { });
        var connection = new JsonRpcConnection(transport.Input, transport.Output, log, options.Limits);
        try
        {
            Register(connection, options, log);
            options.Configure?.Invoke(connection);
            connection.Start();
            // The server may die instead of answering; its exit ends the wait.
            Task<InitializeResult?> initialize = connection.SendRequestAsync(
                LspMethods.Initialize,
                options.InitializeParams,
                cancellationToken
            );
            if (await Task.WhenAny(initialize, transport.Exited).ConfigureAwait(false) != initialize)
                throw new IOException("The language server exited before it was initialized.");
            InitializeResult initialized =
                await initialize.ConfigureAwait(false)
                ?? throw new JsonRpcException(ErrorCodes.InternalError, "The server answered initialize with null.");
            await connection
                .SendNotificationAsync(LspMethods.Initialized, new InitializedParams())
                .ConfigureAwait(false);
            return new LanguageClient(connection, transport, initialized, options);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Shuts the server down with <c>shutdown</c> and <c>exit</c>, waits for it to exit within
    /// <see cref="LanguageClientOptions.ShutdownTimeout"/>, and then ends it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (!Completion.IsCompleted)
        {
            using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
            try
            {
                await Connection.SendRequestAsync(LspMethods.Shutdown, default, timeout.Token).ConfigureAwait(false);
                // Disposing the connection drops what was not written yet.
                await Connection
                    .SendNotificationAsync(LspMethods.Exit, default)
                    .WaitAsync(timeout.Token)
                    .ConfigureAwait(false);
                await _transport.Exited.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is OperationCanceledException or IOException or JsonRpcException)
            {
                _options.Log?.Invoke($"The language server did not shut down cleanly: {exception.Message}");
            }
        }
        await Connection.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the server at once, without <c>shutdown</c>: for a server that stopped answering or reading. Completes
    /// <see cref="Completion"/>; disposing afterwards does nothing.
    /// </summary>
    public async ValueTask AbortAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // The process first, so that it ends even if the connection waits for it.
        await _transport.DisposeAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
    }

    private static void Register(JsonRpcConnection connection, LanguageClientOptions options, Action<string> log)
    {
        connection.OnRequest(
            LspMethods.WorkspaceConfiguration,
            (parameters, _) => Task.FromResult<IReadOnlyList<JsonElement?>?>([.. parameters.Items.Select(Setting)])
        );
        connection.OnRequest(LspMethods.RegisterCapability, (_, _) => Task.FromResult<JsonElement?>(null));
        connection.OnRequest(LspMethods.UnregisterCapability, (_, _) => Task.FromResult<JsonElement?>(null));
        connection.OnRequest(LspMethods.WorkDoneProgressCreate, (_, _) => Task.FromResult<JsonElement?>(null));
        connection.OnNotification(LspMethods.LogMessage, (message, _) => Log(message));
        connection.OnNotification(LspMethods.ShowMessage, (message, _) => Log(message));

        JsonElement? Setting(ConfigurationItem item)
        {
            try
            {
                return options.Settings?.Invoke(item);
            }
            catch (Exception exception)
            {
                log($"Reading the setting {item.Section} failed: {exception}");
                return null;
            }
        }

        Task Log(LogMessageParams message)
        {
            log($"{message.Type}: {message.Message}");
            return Task.CompletedTask;
        }
    }
}

/// <param name="InitializeParams">The <c>initialize</c> request: capabilities, process id, initialization options.</param>
public sealed record LanguageClientOptions(InitializeParams InitializeParams)
{
    /// <summary>Answers <c>workspace/configuration</c> per item; null, the default, lets the server use its default.</summary>
    public Func<ConfigurationItem, JsonElement?>? Settings { get; init; }

    /// <summary>Registers further handlers, such as for <c>textDocument/publishDiagnostics</c>, before the connection starts.</summary>
    public Action<JsonRpcConnection>? Configure { get; init; }

    /// <summary>Receives protocol errors and the server's log messages.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>How much the server may send in one message.</summary>
    public JsonRpcLimits? Limits { get; init; }

    /// <summary>How long disposal waits for <c>shutdown</c>, <c>exit</c> and the server's end before it ends the server.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

using System.IO.Pipelines;
using System.Text.Json;
using System.Threading.Channels;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.Lsp;
using Darp.CodeEditor.Lsp.Protocol;

namespace Darp.CodeEditor.CSharp.Server.Tests;

/// <summary>
/// The server's session in the test process, over in-memory pipes, with a client on <c>Darp.CodeEditor.Lsp</c>. The
/// first <c>initialize</c> does all its work but is answered only once the client cancelled it: the race a real
/// client hits only by chance.
/// </summary>
internal sealed class InProcessServer : IAsyncDisposable
{
    private readonly LanguageServer _server;
    private readonly Task<int> _running;
    private readonly TaskCompletionSource _firstInitializeHeld = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly Channel<int> _exitedClients = Channel.CreateUnbounded<int>();
    private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
    private readonly Channel<PublishDiagnosticsParams> _diagnostics = Channel.CreateUnbounded<PublishDiagnosticsParams>();
    private int _initializeCount;

    public InProcessServer()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var session = new ScriptSession(
            ServerOptions.Default with { DiagnosticsDelay = TimeSpan.FromMilliseconds(50), WarmUp = false },
            clientExited: processId => _exitedClients.Writer.TryWrite(processId)
        );
        _server = new LanguageServer(
            toServer.Reader.AsStream(),
            toClient.Writer.AsStream(),
            (parameters, cancellationToken) => InitializeAsync(session, parameters, cancellationToken),
            session.ShutdownAsync
        );
        session.Connect(_server.Connection);
        _running = _server.RunAsync();

        Client = new JsonRpcConnection(toClient.Reader.AsStream(), toServer.Writer.AsStream());
        Client.OnNotification(LspMethods.ShowMessage, (parameters, _) => Write(_messages, parameters.Message));
        Client.OnNotification(LspMethods.PublishDiagnostics, (parameters, _) => Write(_diagnostics, parameters));
        Client.Start();
    }

    public JsonRpcConnection Client { get; }

    /// <summary>Completes once the first <c>initialize</c> did its work; its answer waits until it is cancelled.</summary>
    public Task FirstInitializeHeld => _firstInitializeHeld.Task;

    /// <summary>The client processes the session saw end, in order.</summary>
    public ChannelReader<int> ExitedClients => _exitedClients.Reader;

    /// <summary>The texts of the <c>window/showMessage</c> notifications the server sent.</summary>
    public ChannelReader<string> Messages => _messages.Reader;

    public ChannelReader<PublishDiagnosticsParams> Diagnostics => _diagnostics.Reader;

    /// <summary>Cancelled when the test run is cancelled, or after 30 seconds.</summary>
    public static CancellationToken Timeout
    {
        get
        {
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return timeout.Token;
        }
    }

    public static InitializeParams InitializeParams(CSharpScriptEnvironment environment, int? processId = null) =>
        new(
            new ClientCapabilities(),
            processId,
            InitializationOptions: JsonDocument
                .Parse(CSharpScriptInitializationOptions.Create(environment).ToJsonString())
                .RootElement.Clone()
        );

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _server.DisposeAsync();
        await _running;
    }

    private async Task<InitializeResult> InitializeAsync(
        ScriptSession session,
        InitializeParams parameters,
        CancellationToken cancellationToken
    )
    {
        InitializeResult result = await session.InitializeAsync(parameters, cancellationToken);
        if (Interlocked.Increment(ref _initializeCount) == 1)
        {
            _firstInitializeHeld.SetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) { }
        }
        return result;
    }

    private static Task Write<T>(Channel<T> channel, T value)
    {
        channel.Writer.TryWrite(value);
        return Task.CompletedTask;
    }
}

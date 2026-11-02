using System.Reflection;
using EmmyLua.LanguageServer.Framework.Protocol.JsonRpc;
using EmmyLua.LanguageServer.Framework.Server;
using EmmyLua.LanguageServer.Framework.Server.Scheduler;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary>The C# script language server: EmmyLua's protocol layer with the lifecycle rules it leaves out.</summary>
/// <remarks>
/// The framework answers <c>shutdown</c> but keeps reading after <c>exit</c>, accepts requests before
/// <c>initialize</c> and drops requests after <c>shutdown</c> without a response. This class handles those cases,
/// and dispatches messages with <see cref="InlineScheduler"/>.
/// </remarks>
internal sealed class ScriptLanguageServer : LanguageServer
{
    private readonly ScriptSession _session;
    private bool _initializeReceived;
    private bool _shutdownReceived;

    public ScriptLanguageServer(Stream input, Stream output, ServerOptions options)
        : base(input, output)
    {
        // The default scheduler starts a thread; it is replaced before the first message.
        (Scheduler as IDisposable)?.Dispose();
        SetScheduler(new InlineScheduler());
        AddJsonSerializeContext(ServerJsonContext.Default);

        _session = new ScriptSession(this, options);
        AddHandler(new TextDocumentSyncHandler(_session));
        AddHandler(new CompletionHandler(_session));
        AddHandler(new HoverHandler(_session));
        AddHandler(new SignatureHelpHandler(_session));
        AddHandler(new SemanticTokensHandler(_session));
        OnInitialize(
            (request, serverInfo) =>
            {
                serverInfo.Name = "Darp.CodeEditor.CSharp.Server";
                serverInfo.Version = typeof(ScriptLanguageServer)
                    .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
                // Throws for invalid initialization options; the framework answers initialize with the error.
                _session.Initialize(request);
                return Task.CompletedTask;
            }
        );
    }

    /// <summary>0 after <c>shutdown</c> and <c>exit</c>; 1 when the input ended or <c>exit</c> came without <c>shutdown</c>.</summary>
    public int ExitCode { get; private set; } = 1;

    /// <remarks>Runs on the thread that reads messages, before they are dispatched.</remarks>
    protected override bool BaseHandle(Message message)
    {
        switch (message)
        {
            case NotificationMessage { Method: "exit" }:
                ExitCode = _shutdownReceived ? 0 : 1;
                Exit();
                return true;
            case RequestMessage { Method: "initialize" }:
                _initializeReceived = true;
                break;
            case RequestMessage request when !_initializeReceived:
                Writer.WriteResponse(
                    request.Id,
                    null,
                    new ResponseError(ErrorCodes.ServerNotInitialized, "The server is not initialized.", null)
                );
                return true;
            case NotificationMessage when !_initializeReceived:
                return true;
            case RequestMessage { Method: "shutdown" }:
                _shutdownReceived = true;
                _session.Shutdown();
                break;
            case RequestMessage request when _shutdownReceived:
                Writer.WriteResponse(
                    request.Id,
                    null,
                    new ResponseError(ErrorCodes.InvalidRequest, "The server is shutting down.", null)
                );
                return true;
        }
        return base.BaseHandle(message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _session.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Dispatches each message on the thread that read it, up to the handler's first <c>await</c>.
/// </summary>
/// <remarks>
/// Text changes are therefore applied in order, requests capture the document version they were sent for, and a
/// request's cancellation token exists before the next message, a <c>$/cancelRequest</c> for example, is read.
/// Handlers move their work to the thread pool. The framework's default scheduler runs one message at a time, so a
/// slow request delays every later message and cancellations for queued requests are lost.
/// </remarks>
internal sealed class InlineScheduler : IScheduler
{
    public void Schedule(Func<Message, Task> action, Message message)
    {
        Task dispatch = action(message);
        if (!dispatch.IsCompleted)
        {
            dispatch.ContinueWith(
                static (task, state) => Console.Error.WriteLine($"Dispatching {state} failed: {task.Exception}"),
                (message as MethodMessage)?.Method,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }
        else if (dispatch.IsFaulted)
        {
            Console.Error.WriteLine($"Dispatching {(message as MethodMessage)?.Method} failed: {dispatch.Exception}");
        }
    }
}

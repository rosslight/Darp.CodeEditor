using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.CSharp.Scripting;
using Darp.CodeEditor.Lsp;
using Darp.CodeEditor.Lsp.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Diagnostic = Microsoft.CodeAnalysis.Diagnostic;
using LspDiagnostic = Darp.CodeEditor.Lsp.Protocol.Diagnostic;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary>The server's state: the script workspace, the open documents and the diagnostics of each version.</summary>
/// <remarks>
/// Text synchronization runs <see cref="DispatchMode.Serial"/> on the connection's dispatch loop, so document state
/// changes one message at a time, and requests read it there, before their first <c>await</c>. Background work
/// receives immutable snapshots.
/// </remarks>
internal sealed class ScriptSession
{
    private static readonly ServerCapabilities s_capabilities = new(
        TextDocumentSync: new TextDocumentSyncOptions(OpenClose: true, Change: TextDocumentSyncKind.Incremental),
        CompletionProvider: CompletionHandler.Options,
        HoverProvider: true,
        SignatureHelpProvider: SignatureHelpHandler.Options,
        SemanticTokensProvider: SemanticTokensHandler.Options
    );

    private static readonly ServerInfo s_serverInfo = new(
        "Darp.CodeEditor.CSharp.Server",
        typeof(ScriptSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    );

    private readonly ServerOptions _options;
    private readonly Action<int> _clientExited;
    private readonly Dictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private JsonRpcConnection? _connection;
    private InitializeAttempt? _attempt;
    private Task<ScriptWorkspace>? _workspace;

    /// <param name="clientExited">Runs when the client process that <c>initialize</c> named ends.</param>
    public ScriptSession(ServerOptions options, Action<int> clientExited)
    {
        _options = options;
        _clientExited = clientExited;
    }

    public ServerOptions Options => _options;

    public ClientFeatures Client { get; private set; } = ClientFeatures.From(new ClientCapabilities());

    public CompletionResults Completions { get; } = new();

    /// <summary>Completes once the environment is loaded. Faults when it cannot be loaded.</summary>
    public Task<ScriptWorkspace> Workspace =>
        _workspace ?? throw new InvalidOperationException("The server is not initialized.");

    private JsonRpcConnection Connection =>
        _connection ?? throw new InvalidOperationException("The session is not connected.");

    /// <summary>Registers the handlers on the server's connection, and keeps it for notifications to the client.</summary>
    public void Connect(JsonRpcConnection connection)
    {
        _connection = connection;
        connection.OnNotification(
            LspMethods.Initialized,
            (_, _) =>
            {
                Start();
                return Task.CompletedTask;
            }
        );
        connection.OnNotification(
            LspMethods.DidOpen,
            (parameters, _) =>
            {
                Open(parameters.TextDocument);
                return Task.CompletedTask;
            }
        );
        connection.OnNotification(
            LspMethods.DidChange,
            (parameters, _) =>
            {
                Change(parameters.TextDocument, parameters.ContentChanges);
                return Task.CompletedTask;
            }
        );
        connection.OnNotification(
            LspMethods.DidClose,
            (parameters, _) =>
            {
                Close(parameters.TextDocument.Uri);
                return Task.CompletedTask;
            }
        );
        CompletionHandler.Register(connection, this);
        HoverHandler.Register(connection, this);
        SignatureHelpHandler.Register(connection, this);
        SemanticTokensHandler.Register(connection, this);
    }

    /// <summary>Answers <c>initialize</c>: reads the environment, which loads once the client has the answer.</summary>
    /// <remarks>
    /// Nothing starts here. The client can cancel the request after it ran, and then a retry owns the session; only
    /// the attempt whose result became the answer is followed by <c>initialized</c>, which <see cref="Start"/>s it.
    /// </remarks>
    /// <exception cref="JsonRpcException">The options are invalid or name files or values that do not exist.</exception>
    public Task<InitializeResult> InitializeAsync(InitializeParams request, CancellationToken cancellationToken)
    {
        _attempt = null;
        ResolvedScriptEnvironment resolved;
        try
        {
            CSharpScriptEnvironment environment =
                (request.InitializationOptions is { } options ? CSharpScriptInitializationOptions.Read(options) : null)
                ?? new CSharpScriptEnvironment();
            // Resolving only reads file headers, so its errors can answer the request.
            resolved = ResolvedScriptEnvironment.Resolve(environment);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new JsonRpcException(ErrorCodes.InvalidParams, exception.Message);
        }
        _attempt = new InitializeAttempt(resolved, ClientFeatures.From(request.Capabilities), request.ProcessId);
        return Task.FromResult(new InitializeResult(s_capabilities, s_serverInfo));
    }

    /// <summary>Stops pending diagnostics; the server answers no more requests.</summary>
    public Task ShutdownAsync()
    {
        foreach (OpenDocument document in _documents.Values)
            document.Close();
        return Task.CompletedTask;
    }

    public bool TryGetSnapshot(string uri, [NotNullWhen(true)] out DocumentSnapshot? snapshot)
    {
        snapshot = _documents.TryGetValue(uri, out OpenDocument? document) ? document.Current : null;
        return snapshot is not null;
    }

    /// <summary>Logs failures of work nobody awaits.</summary>
    public static void Observe(Task task, string what) =>
        task.ContinueWith(
            static (t, state) => Console.Error.WriteLine($"{state} failed: {t.Exception?.GetBaseException()}"),
            what,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

    /// <summary>
    /// Starts loading the environment of the answered <c>initialize</c>, the warm-up and watching the client process.
    /// </summary>
    /// <remarks>
    /// Runs on <c>initialized</c>, which the client sends once it has the result, and on <c>didOpen</c>, for clients
    /// that skip it. The connection admits both only after an <c>initialize</c> was answered with its result, and runs
    /// them and <c>initialize</c> one at a time, so <see cref="_attempt"/> is the answered one.
    /// </remarks>
    private void Start()
    {
        if (_workspace is not null || _attempt is not { } attempt)
            return;
        Client = attempt.Client;
        _workspace = Task.Run(() => ScriptWorkspace.Load(attempt.Environment));
        Observe(ReportLoadFailureAsync(_workspace), "Reporting the environment");
        if (_options.WarmUp)
            Observe(WarmUp.RunAsync(_workspace, Client, _options.CompletionListLimits), "Warm-up");
        if (attempt.ProcessId is { } processId)
            Observe(WatchClientAsync(processId), "Watching the client process");
    }

    private void Open(TextDocumentItem item)
    {
        Start();
        if (_documents.Remove(item.Uri, out OpenDocument? previous))
        {
            previous.Close();
            Completions.Remove(item.Uri);
        }
        var document = new OpenDocument(item.Uri, item.Version, SourceText.From(item.Text));
        _documents.Add(item.Uri, document);
        ScheduleDiagnostics(document.Current);
    }

    /// <summary>Applies the changes of a newer version. Changes for the current or an older version are ignored.</summary>
    private void Change(
        VersionedTextDocumentIdentifier identifier,
        IReadOnlyList<TextDocumentContentChangeEvent> changes
    )
    {
        if (!_documents.TryGetValue(identifier.Uri, out OpenDocument? document))
            return;
        DocumentSnapshot current = document.Current;
        // Versions only grow; requests and cached Roslyn documents rely on one text per version.
        if (identifier.Version <= current.Version)
        {
            Console.Error.WriteLine(
                $"Ignored a change of {identifier.Uri} to version {identifier.Version}: the document is at version {current.Version}."
            );
            return;
        }
        SourceText text = current.Text;
        LineMap lines = current.Lines;
        for (int i = 0; i < changes.Count; i++)
        {
            TextDocumentContentChangeEvent change = changes[i];
            text = change.Range is { } range
                ? text.WithChanges(new TextChange(lines.GetSpan(range), change.Text))
                : SourceText.From(change.Text);
            if (i < changes.Count - 1)
                lines = LineMap.Create(text);
        }
        ScheduleDiagnostics(document.Update(identifier.Version, text));
    }

    private void Close(string uri)
    {
        if (!_documents.Remove(uri, out OpenDocument? document))
            return;
        document.Close();
        Completions.Remove(uri);
        // Clients keep showing published diagnostics until they are replaced.
        Observe(PublishAsync(uri, null, []), "Clearing diagnostics");
    }

    /// <summary>Tells the user when the environment cannot be loaded, such as when the globals type does not exist.</summary>
    private async Task ReportLoadFailureAsync(Task<ScriptWorkspace> workspace)
    {
        try
        {
            await workspace.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Loading the script environment failed: {exception}");
            await Connection
                .SendNotificationAsync(
                    LspMethods.ShowMessage,
                    new LogMessageParams(
                        MessageType.Error,
                        $"The C# script environment could not be loaded: {exception.Message}"
                    )
                )
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tells the server when the client process ends. Its input usually ends with it, but not when another process,
    /// such as a child of the client, still holds the pipe open.
    /// </summary>
    private async Task WatchClientAsync(int processId)
    {
        try
        {
            using Process client = Process.GetProcessById(processId);
            await client.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The process ended before the server looked.
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            Console.Error.WriteLine($"Cannot watch the client process {processId}: {exception.Message}");
            return;
        }
        _clientExited(processId);
    }

    /// <summary>Publishes compiler diagnostics for <paramref name="snapshot"/> once the text stayed unchanged for a while.</summary>
    private void ScheduleDiagnostics(DocumentSnapshot snapshot) =>
        Observe(PublishDiagnosticsAsync(snapshot), "Diagnostics");

    private async Task PublishDiagnosticsAsync(DocumentSnapshot snapshot)
    {
        CancellationToken changed = snapshot.Changed;
        try
        {
            await Task.Delay(_options.DiagnosticsDelay, changed).ConfigureAwait(false);
            ScriptWorkspace workspace = await Workspace.ConfigureAwait(false);
            Document document = snapshot.Owner.GetDocument(workspace, snapshot);
            ImmutableArray<Diagnostic> diagnostics = await ScriptDiagnostics
                .GetAsync(document, changed)
                .ConfigureAwait(false);
            changed.ThrowIfCancellationRequested();
            await PublishAsync(
                    snapshot.Owner.Uri,
                    snapshot.Version,
                    ScriptDiagnostics.ToLsp(diagnostics, snapshot.Lines)
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (changed.IsCancellationRequested)
        {
            // A newer version or closing the document made these diagnostics obsolete.
        }
    }

    private Task PublishAsync(string uri, int? version, List<LspDiagnostic> diagnostics) =>
        Connection.SendNotificationAsync(
            LspMethods.PublishDiagnostics,
            new PublishDiagnosticsParams(uri, diagnostics, version)
        );

    /// <summary>What an <c>initialize</c> read, to start once its result became the answer.</summary>
    private sealed record InitializeAttempt(ResolvedScriptEnvironment Environment, ClientFeatures Client, int? ProcessId);
}

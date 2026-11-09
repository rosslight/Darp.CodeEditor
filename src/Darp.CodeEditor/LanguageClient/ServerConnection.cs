using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.LanguageClient;

/// <summary>
/// One process of a <see cref="LanguageServer"/>: it starts the process, initializes the server, keeps the documents
/// open and in sync in it, answers its requests and passes its diagnostics on, and ends once, for whatever reason.
/// </summary>
/// <remarks>
/// <para>
/// Its server creates it, starts it, and stops or kills it; the connection tells the server when it runs and when it
/// ended. Members marked as guarded are guarded by the server's lock, which the connection shares.
/// </para>
/// <para>
/// When the process has ended and nothing more arrives from it, its documents' diagnostics are cleared: they came from
/// this process.
/// </para>
/// </remarks>
internal sealed class ServerConnection
{
    private readonly LanguageServer _server;
    private readonly LanguageServerOptions _options;
    private readonly Lock _lock;

    /// <summary>Null once the server answered <c>initialize</c>; otherwise why it did not.</summary>
    private readonly TaskCompletionSource<Exception?> _initialized = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    /// <summary>Guarded: the documents open in the server, from <see cref="OnRunning"/> on.</summary>
    private readonly Dictionary<ServerDocument, OpenDocument> _documents = [];

    private readonly SettingsChange _settingsChange;

    /// <summary>Guarded: the connection's life, from <see cref="Start"/> on.</summary>
    private Task? _run;

    /// <summary>Guarded: the process, once it started.</summary>
    private ServerProcess? _process;

    /// <summary>Guarded: set once the server answered <c>initialize</c>.</summary>
    private Running? _running;

    /// <summary>Guarded: the process was killed, or is not to start.</summary>
    private bool _isKilled;

    /// <summary>Guarded: a request went unanswered too long.</summary>
    private bool _hung;

    /// <summary>Guarded: why the server did not answer <c>initialize</c>, when it answered something else.</summary>
    private string? _failure;

    public ServerConnection(LanguageServer server, LanguageServerOptions options, Lock serverLock)
    {
        _server = server;
        _options = options;
        _lock = serverLock;
        _settingsChange = new SettingsChange(this);
    }

    /// <summary>
    /// Completes with null once the server answered <c>initialize</c>, or with why it did not: the process did not
    /// start or answer, or the server was disposed.
    /// </summary>
    public Task<Exception?> Initialized => _initialized.Task;

    /// <summary>Guarded: whether the server answered <c>initialize</c> and the process has not ended.</summary>
    public bool IsRunning => _running is not null;

    /// <summary>Guarded: how long the server has been running since it answered <c>initialize</c>; null before.</summary>
    public TimeSpan? RunningFor => _running is { } running ? _options.TimeProvider.GetElapsedTime(running.Since) : null;

    /// <summary>Guarded: starts the process in the background.</summary>
    public void Start() => _run = Task.Run(RunAsync);

    /// <summary>
    /// Guarded: the server answered <c>initialize</c>. Sends <c>initialized</c>, then opens <paramref name="documents"/>.
    /// </summary>
    public void OnRunning(JsonRpcConnection rpc, bool isFullSync, IEnumerable<ServerDocument> documents)
    {
        _running = new Running(rpc, _options.TimeProvider.GetTimestamp(), isFullSync);
        rpc.Notify(
            "initialized",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
        );
        foreach (ServerDocument document in documents)
            Open(document);
    }

    /// <summary>Guarded: opens a document in the running server, with its current text.</summary>
    public void Open(ServerDocument document)
    {
        if (_running is { } running)
            _documents[document] = new OpenDocument(this, running, document);
    }

    /// <summary>Guarded: sends a change of an open document.</summary>
    public void Change(
        ServerDocument document,
        TextSnapshot snapshot,
        IReadOnlyList<TextDocumentContentChangeEvent> changes
    )
    {
        if (_documents.TryGetValue(document, out OpenDocument? open))
            open.Change(snapshot, changes);
    }

    /// <summary>Guarded: closes a document in the server.</summary>
    public void Close(ServerDocument document)
    {
        if (_documents.Remove(document, out OpenDocument? open))
            open.Close();
    }

    /// <summary>Guarded: moves a document to its new URI: closes the old one and opens the new one.</summary>
    public void Move(ServerDocument document)
    {
        Close(document);
        Open(document);
    }

    /// <summary>Guarded: tells the running server that the settings may have changed.</summary>
    public void NotifySettingsChanged()
    {
        if (_running is { } running)
            _settingsChange.Queue(running.Rpc);
    }

    /// <summary>
    /// Stops the server for good: <c>shutdown</c> and <c>exit</c> when it runs, then a kill after
    /// <paramref name="timeLimit"/>, or at once when it does not run. Completes when the process has ended.
    /// </summary>
    public async Task StopAsync(TimeSpan timeLimit)
    {
        _initialized.TrySetResult(new ObjectDisposedException(nameof(LanguageServer)));
        Task? run;
        Running? running;
        lock (_lock)
            (run, running) = (_run, _running);
        if (run is null)
            return;
        if (running is not null)
        {
            using var timeout = new CancellationTokenSource(timeLimit, _options.TimeProvider);
            try
            {
                await running.Rpc.SendRequestAsync("shutdown", null, timeout.Token).ConfigureAwait(false);
                running.Rpc.Notify("exit", null);
                await run.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            // Past the time limit, or the server refused or ended: the kill below ends it.
            catch (Exception e) when (e is OperationCanceledException or IOException or JsonRpcException) { }
        }
        Kill();
        await run.ConfigureAwait(false);
    }

    /// <summary>Kills the server at once, without the protocol, for good.</summary>
    public void Abandon()
    {
        _initialized.TrySetResult(new ObjectDisposedException(nameof(LanguageServer)));
        Kill();
    }

    /// <summary>Kills the process, or keeps it from starting.</summary>
    private void Kill()
    {
        ServerProcess? process;
        lock (_lock)
        {
            _isKilled = true;
            process = _process;
        }
        process?.Kill();
    }

    private async Task RunAsync()
    {
        ServerProcess process;
        try
        {
            process = ServerProcess.Start(StartInfo(), _options.StandardError);
        }
        // Whatever kept the process from starting is what StartAsync reports.
        catch (Exception e)
        {
            End($"did not start: {e.Message}", hung: false, ranFor: null);
            return;
        }
        bool isKilled;
        lock (_lock)
        {
            _process = process;
            isKilled = _isKilled;
        }
        if (isKilled)
            process.Kill();

        var rpc = new JsonRpcConnection(process.Output, process.Input, HandleRequestAsync, HandleNotification);
        rpc.Start();
        if (await InitializeAsync(rpc).ConfigureAwait(false))
            await Task.WhenAny(rpc.Ended, process.WaitForExitAsync()).ConfigureAwait(false);

        // The end: nothing arrives from the process after this.
        process.Kill();
        await rpc.DisposeAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        List<ServerDocument> documents;
        bool hung;
        string failure;
        TimeSpan? ranFor;
        lock (_lock)
        {
            documents = [.. _documents.Keys];
            _documents.Clear();
            hung = _hung;
            ranFor = RunningFor;
            _running = null;
            failure =
                _failure
                ?? (
                    rpc.Ended.Result is { } reason
                        ? $"broke the connection: {reason.Message}"
                        : $"exited with code {process.ExitCode} before it answered 'initialize'"
                );
        }
        foreach (ServerDocument document in documents)
            document.SetDiagnostics([]);
        string tail = process.StandardErrorTail;
        process.Dispose();
        End(tail.Length > 0 ? $"{failure}. Its standard error ended with:\n{tail}" : $"{failure}.", hung, ranFor);
    }

    /// <summary>Reports the end: StartAsync learns why, and the server what comes next.</summary>
    private void End(string failure, bool hung, TimeSpan? ranFor)
    {
        _initialized.TrySetResult(
            new InvalidOperationException($"The language server '{_options.FileName}' {failure}")
        );
        _server.OnEnded(this, hung, ranFor);
    }

    /// <summary>Sends <c>initialize</c>, and tells the server when it answered.</summary>
    /// <returns>Whether the server answered.</returns>
    private async Task<bool> InitializeAsync(JsonRpcConnection rpc)
    {
        string? rootUri = null;
        WorkspaceFolder[]? workspaceFolders = null;
        if (_options.RootPath is { } root)
        {
            rootUri = LanguageServer.FileUri(root);
            workspaceFolders =
            [
                new WorkspaceFolder(rootUri, Path.GetFileName(Path.TrimEndingDirectorySeparator(root))),
            ];
        }
        byte[] parameters;
        try
        {
            parameters = JsonSerializer.SerializeToUtf8Bytes(
                new InitializeParams(
                    Environment.ProcessId,
                    rootUri,
                    workspaceFolders,
                    _options.InitializationOptions,
                    Capabilities()
                ),
                LspJsonContext.Default.InitializeParams
            );
        }
        // The app's initialization options are JSON nodes, which may hold values that don't serialize.
        catch (Exception e)
        {
            return Failed($"got no 'initialize': its initialization options do not serialize: {e.Message}");
        }
        try
        {
            JsonElement result = await RequestAsync(rpc, "initialize", writer => writer.WriteRawValue(parameters, true))
                .ConfigureAwait(false);
            InitializeResult initialized =
                result.Deserialize(LspJsonContext.Default.InitializeResult)
                ?? throw new JsonException("The result is null.");
            _server.OnInitialized(this, rpc, IsFullSync(initialized.Capabilities.TextDocumentSync));
            _initialized.TrySetResult(null);
            return true;
        }
        catch (TimeoutException e)
        {
            return Failed(e.Message);
        }
        catch (JsonRpcException e)
        {
            return Failed($"answered 'initialize' with an error: {e.Message}");
        }
        catch (JsonException e)
        {
            return Failed($"answered 'initialize' with an invalid result: {e.Message}");
        }
        // The connection ended: the end tells why.
        catch (IOException)
        {
            return false;
        }

        bool Failed(string failure)
        {
            lock (_lock)
                _failure = failure;
            return false;
        }
    }

    /// <summary>
    /// Sends a request. A server that does not answer within the server's hang limit counts as hung: the process is
    /// killed.
    /// </summary>
    /// <exception cref="TimeoutException">The server did not answer in time.</exception>
    private async Task<JsonElement> RequestAsync(
        JsonRpcConnection rpc,
        string method,
        Action<Utf8JsonWriter>? writeParams
    )
    {
        TimeSpan limit = _server.HangLimit(this);
        // The time runs from before the server can see the request.
        using var timeout = new CancellationTokenSource(limit, _options.TimeProvider);
        try
        {
            return await rpc.SendRequestAsync(method, writeParams, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            lock (_lock)
                _hung = true;
            Kill();
            throw new TimeoutException($"did not answer '{method}' within {limit.TotalSeconds:0} s");
        }
    }

    /// <summary>Answers the server's requests, on the thread pool.</summary>
    private Task<JsonNode?> HandleRequestAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken
    ) =>
        Task.FromResult<JsonNode?>(
            method switch
            {
                "workspace/configuration" => Configuration(
                    parameters.Deserialize(LspJsonContext.Default.ConfigurationParams)
                        ?? throw new JsonException("The params are missing.")
                ),
                // Registrations are acknowledged; only the features the client implements take effect.
                "client/registerCapability" or "client/unregisterCapability" => null,
                _ => throw new JsonRpcException(
                    JsonRpcErrorCodes.MethodNotFound,
                    $"The client does not handle '{method}'."
                ),
            }
        );

    /// <summary>The app's settings for each item, from <see cref="LanguageServerOptions.Settings"/>.</summary>
    private JsonArray Configuration(ConfigurationParams parameters)
    {
        var settings = new JsonArray();
        foreach (ConfigurationItem item in parameters.Items)
        {
            string? scopePath =
                item.ScopeUri is not null
                && Uri.TryCreate(item.ScopeUri, UriKind.Absolute, out Uri? scope)
                && scope.IsFile
                    ? scope.LocalPath
                    : null;
            // A copy: the app may answer with the same node twice, and a node has one parent.
            settings.Add(
                _options.Settings?.Invoke(new LanguageServerSettingsRequest(item.Section, scopePath))?.DeepClone()
            );
        }
        return settings;
    }

    /// <summary>Handles the server's notifications, on the read loop. Only diagnostics matter to the client.</summary>
    private void HandleNotification(string method, JsonElement parameters)
    {
        if (method != "textDocument/publishDiagnostics")
            return;
        PublishDiagnosticsParams published =
            parameters.Deserialize(LspJsonContext.Default.PublishDiagnosticsParams)
            ?? throw new JsonException("The params are missing.");
        ServerDocument? document = null;
        TextSnapshot? snapshot = null;
        lock (_lock)
        {
            foreach (OpenDocument open in _documents.Values)
            {
                if (open.Uri == published.Uri)
                {
                    document = open.Document;
                    snapshot = open.Analyzed(published.Version);
                    break;
                }
            }
        }
        if (document is null || snapshot is null)
            return;
        document.SetDiagnostics([.. published.Diagnostics.Select(d => ToDiagnostic(snapshot, d))]);
    }

    private static Diagnostic ToDiagnostic(TextSnapshot snapshot, LspDiagnostic diagnostic)
    {
        int start = Positions.ToOffset(snapshot, diagnostic.Range.Start);
        int end = Positions.ToOffset(snapshot, diagnostic.Range.End);
        DiagnosticSeverity severity = diagnostic.Severity switch
        {
            2 => DiagnosticSeverity.Warning,
            3 => DiagnosticSeverity.Information,
            4 => DiagnosticSeverity.Hint,
            // 1, and as in VS Code, none.
            _ => DiagnosticSeverity.Error,
        };
        return new Diagnostic(
            snapshot,
            TextRange.FromBounds(start, Math.Max(start, end)),
            severity,
            diagnostic.Code,
            diagnostic.Message
        );
    }

    private ProcessStartInfo StartInfo()
    {
        var startInfo = new ProcessStartInfo(_options.FileName) { WorkingDirectory = _options.WorkingDirectory ?? "" };
        foreach (string argument in _options.Arguments)
            startInfo.ArgumentList.Add(argument);
        foreach ((string name, string? value) in _options.EnvironmentVariables)
        {
            if (value is null)
                startInfo.Environment.Remove(name);
            else
                startInfo.Environment[name] = value;
        }
        return startInfo;
    }

    /// <summary>What the client implements, and nothing more, so that servers fall back where they can.</summary>
    private static JsonObject Capabilities() =>
        new()
        {
            ["workspace"] = new JsonObject
            {
                ["configuration"] = true,
                ["didChangeConfiguration"] = new JsonObject { ["dynamicRegistration"] = false },
            },
            ["textDocument"] = new JsonObject
            {
                ["synchronization"] = new JsonObject { ["dynamicRegistration"] = false },
                ["publishDiagnostics"] = new JsonObject { ["versionSupport"] = true },
            },
        };

    /// <summary>Whether the server wants the full text with every change, rather than the changed ranges.</summary>
    private static bool IsFullSync(JsonElement? sync) =>
        sync switch
        {
            { ValueKind: JsonValueKind.Number } kind => kind.TryGetInt32(out int k) && k == TextDocumentSyncKind.Full,
            { ValueKind: JsonValueKind.Object } options => options.TryGetProperty("change", out JsonElement change)
                && change.TryGetInt32(out int k)
                && k == TextDocumentSyncKind.Full,
            _ => false,
        };

    /// <summary>A server that answered <c>initialize</c>.</summary>
    /// <param name="Rpc">Its connection.</param>
    /// <param name="Since">When it answered, as a timestamp of the options' clock.</param>
    /// <param name="IsFullSync">Whether it wants the full text with every change.</param>
    private sealed record Running(JsonRpcConnection Rpc, long Since, bool IsFullSync);

    /// <summary>
    /// A document open in the server: what the server has of it, and what is still to send. At most one of its messages
    /// waits in the write queue; changes made meanwhile join it. Its members are guarded by the server's lock, except
    /// <see cref="Write"/>, which takes it.
    /// </summary>
    private sealed class OpenDocument : OutgoingMessage
    {
        /// <summary>
        /// Changes that wait beyond these limits, behind a server that does not read, are dropped for the full text,
        /// which then costs less.
        /// </summary>
        private const int MaxChanges = 1_000;

        private const int MaxChangedLength = 1_000_000;

        /// <summary>How many of the versions sent are kept for diagnostics that name their version.</summary>
        private const int MaxSentVersions = 64;

        private readonly ServerConnection _connection;
        private readonly Running _running;

        /// <summary>The snapshots sent, oldest first: the texts that the server's diagnostics can refer to.</summary>
        private readonly List<TextSnapshot> _sent = [];

        /// <summary>The text that the server has once the queued message is written.</summary>
        private TextSnapshot _latest;

        /// <summary>The changes not sent yet; null when the full text is to be sent instead.</summary>
        private List<TextDocumentContentChangeEvent>? _changes = [];

        private int _changedLength;
        private bool _isOpened;
        private bool _isClosed;
        private bool _isQueued;

        /// <summary>Queues the document's <c>didOpen</c>, with its current text.</summary>
        public OpenDocument(ServerConnection connection, Running running, ServerDocument document)
        {
            _connection = connection;
            _running = running;
            Document = document;
            Uri = document.Uri;
            _latest = document.CurrentSnapshot;
            Queue();
        }

        public ServerDocument Document { get; }

        /// <summary>The URI that the server knows the document by.</summary>
        public string Uri { get; }

        public void Change(TextSnapshot snapshot, IReadOnlyList<TextDocumentContentChangeEvent> changes)
        {
            // Versions only grow: a didOpen already has the text of this version or a later one.
            if (_isClosed || snapshot.Version <= _latest.Version)
                return;
            _latest = snapshot;
            // A didOpen not written yet takes the latest text.
            if (!_isOpened)
                return;
            if (_changes is not null && !_running.IsFullSync)
            {
                _changes.AddRange(changes);
                foreach (TextDocumentContentChangeEvent change in changes)
                    _changedLength += change.Text.Length;
                if (_changes.Count > MaxChanges || _changedLength > MaxChangedLength)
                    _changes = null;
            }
            else
            {
                _changes = null;
            }
            Queue();
        }

        public void Close()
        {
            _isClosed = true;
            Queue();
        }

        /// <summary>
        /// The text that diagnostics of <paramref name="version"/> refer to, or the latest one sent for diagnostics
        /// without a version; null for a version not sent, or no longer kept. Versions before it are dropped: the
        /// server is past them.
        /// </summary>
        public TextSnapshot? Analyzed(int? version)
        {
            if (version is null)
                return _sent.Count > 0 ? _sent[^1] : null;
            int index = _sent.FindIndex(snapshot => snapshot.Version == version);
            if (index < 0)
                return null;
            _sent.RemoveRange(0, index);
            return _sent[0];
        }

        public override bool Write(Utf8JsonWriter writer)
        {
            string method;
            Action<Utf8JsonWriter> writeParams;
            lock (_connection._lock)
            {
                _isQueued = false;
                TextSnapshot snapshot = _latest;
                List<TextDocumentContentChangeEvent>? changes = _changes;
                if (_isClosed)
                {
                    if (!_isOpened)
                        return false;
                    _isOpened = false;
                    (method, writeParams) = ("textDocument/didClose", WriteClose);
                }
                else if (!_isOpened)
                {
                    _isOpened = true;
                    (method, writeParams) = ("textDocument/didOpen", w => WriteOpen(w, snapshot));
                }
                else if (changes is { Count: 0 })
                {
                    return false;
                }
                else
                {
                    (method, writeParams) = ("textDocument/didChange", w => WriteChange(w, snapshot, changes));
                }
                if (!_isClosed)
                {
                    _changes = [];
                    _changedLength = 0;
                    _sent.Add(snapshot);
                    if (_sent.Count > MaxSentVersions)
                        _sent.RemoveAt(0);
                }
            }
            // Outside the lock: the text can be long.
            WriteNotification(writer, method, writeParams);
            return true;
        }

        /// <summary>
        /// Nothing to do: the message failed because the connection ended, and the next server opens the document
        /// anew.
        /// </summary>
        public override void Fail(Exception exception) { }

        private void Queue()
        {
            if (_isQueued)
                return;
            _isQueued = true;
            _running.Rpc.Send(this);
        }

        private void WriteOpen(Utf8JsonWriter writer, TextSnapshot snapshot) =>
            JsonSerializer.Serialize(
                writer,
                new DidOpenTextDocumentParams(
                    new TextDocumentItem(Uri, Document.Language.Id, snapshot.Version, snapshot.GetText())
                ),
                LspJsonContext.Default.DidOpenTextDocumentParams
            );

        private void WriteChange(
            Utf8JsonWriter writer,
            TextSnapshot snapshot,
            IReadOnlyList<TextDocumentContentChangeEvent>? changes
        ) =>
            JsonSerializer.Serialize(
                writer,
                new DidChangeTextDocumentParams(
                    new VersionedTextDocumentIdentifier(Uri, snapshot.Version),
                    changes ?? [new TextDocumentContentChangeEvent(null, snapshot.GetText())]
                ),
                LspJsonContext.Default.DidChangeTextDocumentParams
            );

        private void WriteClose(Utf8JsonWriter writer) =>
            JsonSerializer.Serialize(
                writer,
                new DidCloseTextDocumentParams(new TextDocumentIdentifier(Uri)),
                LspJsonContext.Default.DidCloseTextDocumentParams
            );
    }

    /// <summary><c>workspace/didChangeConfiguration</c>: at most one waits in the write queue.</summary>
    private sealed class SettingsChange : OutgoingMessage
    {
        private readonly ServerConnection _connection;

        /// <summary>Guarded by the server's lock.</summary>
        private bool _isQueued;

        public SettingsChange(ServerConnection connection) => _connection = connection;

        /// <summary>Guarded by the server's lock.</summary>
        public void Queue(JsonRpcConnection rpc)
        {
            if (_isQueued)
                return;
            _isQueued = true;
            rpc.Send(this);
        }

        public override bool Write(Utf8JsonWriter writer)
        {
            lock (_connection._lock)
                _isQueued = false;
            WriteNotification(
                writer,
                "workspace/didChangeConfiguration",
                w =>
                    JsonSerializer.Serialize(
                        w,
                        new DidChangeConfigurationParams(null),
                        LspJsonContext.Default.DidChangeConfigurationParams
                    )
            );
            return true;
        }

        public override void Fail(Exception exception) { }
    }
}

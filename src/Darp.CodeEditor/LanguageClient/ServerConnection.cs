using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darp.CodeEditor.Highlighting;
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
/// <para>
/// Editors that show a document ask for its hover, semantic tokens, completion and signature help. Their requests take
/// the path of every request,
/// with its hang limit, and their caller's cancellation sends <c>$/cancelRequest</c>. Each document's text goes out
/// before a request that follows it, so a server answers about the text that was current when the editor asked.
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

    /// <summary>Guarded: the app's answer for all settings is being computed, on the thread pool.</summary>
    private bool _isComputingSettings;

    /// <summary>Guarded: the settings changed while they were computed, so another computation follows.</summary>
    private bool _isSettingsChangedAgain;

    /// <summary>
    /// Guarded: the start's settings went out, so documents open in the server. Until then, documents don't reach
    /// it: they open afterwards, with their text and path of that moment.
    /// </summary>
    private bool _areDocumentsOpen;

    public ServerConnection(LanguageServer server, LanguageServerOptions options, Lock serverLock)
    {
        _server = server;
        _options = options;
        _lock = serverLock;
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
    /// Guarded: the server answered <c>initialize</c>, and <c>initialized</c> went out. It runs; its documents open once
    /// the start's settings went out, in <see cref="OpenDocuments"/>.
    /// </summary>
    public void OnRunning(JsonRpcConnection rpc, bool isFullSync, ServerFeatures features)
    {
        _running = new Running(rpc, _options.TimeProvider.GetTimestamp(), isFullSync, features);
        _isComputingSettings = true;
    }

    /// <summary>
    /// Guarded: the app's settings for the start are computed. Sends them, then opens <paramref name="documents"/>, so
    /// that the server has its settings before it checks them.
    /// </summary>
    /// <param name="settings">All settings; null when the app's callback threw, which drops this push.</param>
    /// <param name="documents">The server's open documents.</param>
    public void OpenDocuments(JsonNode? settings, IEnumerable<ServerDocument> documents)
    {
        if (_running is not { } running)
            return;
        if (settings is not null)
            SendSettings(running.Rpc, settings);
        _areDocumentsOpen = true;
        foreach (ServerDocument document in documents)
            Open(document);
        SettingsComputed();
    }

    /// <summary>
    /// Guarded: opens a document in the running server, with its current text. Its editors learn that its features
    /// changed.
    /// </summary>
    public void Open(ServerDocument document)
    {
        // Not before the start's settings went out: until then, the server has not heard of the document.
        if (_running is not { } running || !_areDocumentsOpen)
            return;
        _documents[document] = new OpenDocument(this, running, document);
        document.OnLanguageFeaturesChanged();
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

    /// <summary>
    /// The server's hover at <paramref name="position"/> of an open document, for an editor. Null when the server does
    /// not run, offers no hover or failed, and when <paramref name="cancellationToken"/> cancelled the request.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="snapshot">The document's text that <paramref name="position"/> is in, for the hover's range.</param>
    /// <param name="position">Where the pointer rests.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public Task<HoverInfo?> HoverAsync(
        ServerDocument document,
        TextSnapshot snapshot,
        TextPosition position,
        CancellationToken cancellationToken
    )
    {
        if (Target(document) is not { Features.Hover: true } target)
            return Task.FromResult<HoverInfo?>(null);
        var parameters = new TextDocumentPositionParams(
            new TextDocumentIdentifier(target.Uri),
            Positions.ToLsp(position)
        );
        return RequestForEditorAsync(
            target.Rpc,
            "textDocument/hover",
            writer => JsonSerializer.Serialize(writer, parameters, LspJsonContext.Default.TextDocumentPositionParams),
            result => HoverInfo.Read(result, snapshot),
            cancellationToken
        );
    }

    /// <summary>
    /// The server's semantic tokens of an open document, for an editor: of the lines from <paramref name="start"/> to
    /// <paramref name="end"/> when it answers ranges, otherwise of all <paramref name="lineCount"/> lines. Null when the
    /// server does not run, offers no semantic tokens or failed, and when <paramref name="cancellationToken"/>
    /// cancelled the request.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="start">The start of the first line the editor shows.</param>
    /// <param name="end">The end of the last line the editor shows.</param>
    /// <param name="lineCount">The number of lines of the text.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public Task<SemanticTokenLines?> SemanticTokensAsync(
        ServerDocument document,
        TextPosition start,
        TextPosition end,
        int lineCount,
        CancellationToken cancellationToken
    )
    {
        if (Target(document) is not { Features.SemanticTokens: { } provider } target)
            return Task.FromResult<SemanticTokenLines?>(null);
        var textDocument = new TextDocumentIdentifier(target.Uri);
        if (provider.Range)
        {
            var range = new SemanticTokensRangeParams(
                textDocument,
                new LspRange(Positions.ToLsp(start), Positions.ToLsp(end))
            );
            return RequestForEditorAsync(
                target.Rpc,
                "textDocument/semanticTokens/range",
                writer => JsonSerializer.Serialize(writer, range, LspJsonContext.Default.SemanticTokensRangeParams),
                result => SemanticTokenLines.Read(result, provider.Legend, start.Line - 1, end.Line - 1),
                cancellationToken
            );
        }
        var full = new SemanticTokensParams(textDocument);
        return RequestForEditorAsync(
            target.Rpc,
            "textDocument/semanticTokens/full",
            writer => JsonSerializer.Serialize(writer, full, LspJsonContext.Default.SemanticTokensParams),
            result => SemanticTokenLines.Read(result, provider.Legend, 0, lineCount - 1),
            cancellationToken
        );
    }

    /// <summary>
    /// The server's completion at the end of <paramref name="word"/> in an open document, for an editor. Null when the
    /// server does not run, offers no completion or failed, and when <paramref name="cancellationToken"/> cancelled the
    /// request. The list resolves its items with this process only.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="snapshot">The document's text that the word is in.</param>
    /// <param name="word">The word before the caret, which ends at the caret: what items without an edit replace.</param>
    /// <param name="trigger">How completion was asked for.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public Task<CompletionList?> CompletionAsync(
        ServerDocument document,
        TextSnapshot snapshot,
        TextRange word,
        CompletionTrigger trigger,
        CancellationToken cancellationToken
    )
    {
        if (Target(document) is not { Features.Completion: { } provider } target)
            return Task.FromResult<CompletionList?>(null);
        var parameters = new CompletionParams(
            new TextDocumentIdentifier(target.Uri),
            Positions.ToLsp(snapshot.GetPosition(word.End)),
            new CompletionContext(trigger.Kind, trigger.Character?.ToString())
        );
        JsonRpcConnection rpc = target.Rpc;
        Func<CompletionItem, CancellationToken, Task<CompletionItem?>>? resolve = provider.Resolves
            ? (item, token) =>
                RequestForEditorAsync(
                    rpc,
                    "completionItem/resolve",
                    writer => item.Source.WriteTo(writer),
                    result => CompletionList.Resolved(item, result, snapshot),
                    token
                )
            : null;
        return RequestForEditorAsync(
            rpc,
            "textDocument/completion",
            writer => JsonSerializer.Serialize(writer, parameters, LspJsonContext.Default.CompletionParams),
            result => CompletionList.Read(result, snapshot, word, provider.CommitCharacters, resolve),
            cancellationToken
        );
    }

    /// <summary>
    /// The server's signature help at <paramref name="offset"/> of an open document, for an editor. Null when the
    /// server does not run, offers no signature help, failed or has none there, and when
    /// <paramref name="cancellationToken"/> cancelled the request.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="snapshot">The document's text that the offset is in.</param>
    /// <param name="offset">The caret.</param>
    /// <param name="trigger">How signature help was asked for.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public Task<SignatureHelpInfo?> SignatureHelpAsync(
        ServerDocument document,
        TextSnapshot snapshot,
        int offset,
        SignatureHelpTrigger trigger,
        CancellationToken cancellationToken
    )
    {
        if (Target(document) is not { Features.SignatureHelp: not null } target)
            return Task.FromResult<SignatureHelpInfo?>(null);
        var parameters = new SignatureHelpParams(
            new TextDocumentIdentifier(target.Uri),
            Positions.ToLsp(snapshot.GetPosition(offset)),
            new SignatureHelpContext(
                trigger.Kind,
                IsRetrigger: trigger.Showing is not null,
                trigger.Character?.ToString(),
                trigger.Showing?.ToContext()
            )
        );
        return RequestForEditorAsync(
            target.Rpc,
            "textDocument/signatureHelp",
            writer => JsonSerializer.Serialize(writer, parameters, LspJsonContext.Default.SignatureHelpParams),
            SignatureHelpInfo.Read,
            cancellationToken
        );
    }

    /// <summary>
    /// What the running server offers the editors of a document; null while it does not run or the document is not
    /// open in it.
    /// </summary>
    public ServerFeatures? FeaturesOf(ServerDocument document) => Target(document)?.Features;

    /// <summary>
    /// Guarded: sends all settings again to the running server, once the app's callback has answered on the thread
    /// pool. One computation runs at a time, and changes meanwhile make one more.
    /// </summary>
    public void NotifySettingsChanged()
    {
        if (_isComputingSettings)
        {
            _isSettingsChangedAgain = true;
            return;
        }
        // A server that starts gets all settings anyway.
        if (_running is null)
            return;
        _isComputingSettings = true;
        _ = Task.Run(PushSettings);
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
        Task ended = Task.WhenAny(rpc.Ended, process.WaitForExitAsync());
        try
        {
            if (await InitializeAsync(rpc).ConfigureAwait(false) is var (isFullSync, features))
            {
                rpc.Notify(
                    "initialized",
                    writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteEndObject();
                    }
                );
                _server.OnInitialized(this, rpc, isFullSync, features);
                _initialized.TrySetResult(null);
                // All settings go before the documents. The app's callback may block: the end of the process
                // abandons the wait, and the answer is dropped.
                Task<JsonNode?> settings = Task.Run(AllSettings);
                if (await Task.WhenAny(settings, ended).ConfigureAwait(false) == settings)
                {
                    _server.OpenDocuments(this, await settings.ConfigureAwait(false));
                    await ended.ConfigureAwait(false);
                }
            }
        }
        // The connection's end: whatever went wrong ends it as a failure, so that the server moves on.
        catch (Exception e)
        {
            lock (_lock)
                _failure ??= $"failed: {e.Message}";
        }

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
        {
            document.SetDiagnostics([]);
            document.OnLanguageFeaturesChanged();
        }
        string tail = process.StandardErrorTail;
        process.Dispose();
        failure = failure.TrimEnd('.');
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

    /// <summary>Sends <c>initialize</c>.</summary>
    /// <returns>
    /// Whether the server wants the full text with every change, and what it offers the editors; null when it did not
    /// answer.
    /// </returns>
    private async Task<(bool IsFullSync, ServerFeatures Features)?> InitializeAsync(JsonRpcConnection rpc)
    {
        string? rootUri = null;
        WorkspaceFolder[]? workspaceFolders = null;
        if (_options.RootPath is { } root)
        {
            rootUri = DocumentUris.File(root);
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
            ServerCapabilities capabilities = initialized.Capabilities;
            return (IsFullSync(capabilities.TextDocumentSync), ServerFeatures.Read(capabilities));
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
            return null;
        }

        (bool, ServerFeatures)? Failed(string failure)
        {
            lock (_lock)
                _failure = failure;
            return null;
        }
    }

    /// <summary>The running server and the URI it knows an open document by; null while it does not run or the
    /// document is not open in it.</summary>
    private (JsonRpcConnection Rpc, string Uri, ServerFeatures Features)? Target(ServerDocument document)
    {
        lock (_lock)
        {
            return _running is { } running && _documents.TryGetValue(document, out OpenDocument? open)
                ? (running.Rpc, open.Uri, running.Features)
                : null;
        }
    }

    /// <summary>
    /// Sends an editor's request and reads its result, off the UI thread. The connection deals with a server that
    /// failed, so the editor gets null then, as it does when it cancelled the request.
    /// </summary>
    private async Task<T?> RequestForEditorAsync<T>(
        JsonRpcConnection rpc,
        string method,
        Action<Utf8JsonWriter> writeParams,
        Func<JsonElement, T?> read,
        CancellationToken cancellationToken
    )
        where T : class
    {
        try
        {
            return read(await RequestAsync(rpc, method, writeParams, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        // The process ended or hung, and restarts; or the server refused, or answered with something else.
        catch (Exception e) when (e is IOException or TimeoutException or JsonRpcException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends a request. A server that does not answer within the server's hang limit counts as hung: the process is
    /// killed. Cancelling <paramref name="cancellationToken"/> cancels the request in the server.
    /// </summary>
    /// <exception cref="TimeoutException">The server did not answer in time.</exception>
    private async Task<JsonElement> RequestAsync(
        JsonRpcConnection rpc,
        string method,
        Action<Utf8JsonWriter>? writeParams,
        CancellationToken cancellationToken = default
    )
    {
        TimeSpan limit = _server.HangLimit(this);
        // The time runs from before the server can see the request.
        using var timeout = new CancellationTokenSource(limit, _options.TimeProvider);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            return await rpc.SendRequestAsync(method, writeParams, cancellation.Token).ConfigureAwait(false);
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
        string identity = DocumentUris.Identity(published.Uri);
        ServerDocument? document = null;
        TextSnapshot? snapshot = null;
        lock (_lock)
        {
            foreach ((ServerDocument candidate, OpenDocument open) in _documents)
            {
                if (open.Identity == identity)
                {
                    document = candidate;
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

    /// <summary>
    /// The app's answer for all settings, that is for a null section, copied; an empty object without one, never
    /// null, which some servers fail on. Runs on the thread pool, as the app's callback may take its time.
    /// </summary>
    /// <returns>Null when the app's callback threw, which drops this push.</returns>
    private JsonNode? AllSettings()
    {
        try
        {
            return _options.Settings?.Invoke(new LanguageServerSettingsRequest(null, null))?.DeepClone()
                ?? new JsonObject();
        }
        // The app's callback failed: this push is dropped, and the next change sends the settings again.
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Computes all settings on the thread pool and sends them, unless the server ended meanwhile. Not awaited: the
    /// app's callback may block, and nothing waits for it.
    /// </summary>
    private void PushSettings()
    {
        JsonNode? settings = AllSettings();
        lock (_lock)
        {
            if (settings is not null && _running is { } running)
                SendSettings(running.Rpc, settings);
            SettingsComputed();
        }
    }

    /// <summary>Guarded: a computation of all settings ended; another starts when the settings changed meanwhile.</summary>
    private void SettingsComputed()
    {
        if (_isSettingsChangedAgain && _running is not null)
        {
            _isSettingsChangedAgain = false;
            _ = Task.Run(PushSettings);
            return;
        }
        _isComputingSettings = false;
    }

    /// <summary>
    /// <c>workspace/didChangeConfiguration</c> with all settings. Some servers read their settings only here, such as
    /// VS Code's JSON server; others pull them after it.
    /// </summary>
    private static void SendSettings(JsonRpcConnection rpc, JsonNode settings) =>
        rpc.Notify(
            "workspace/didChangeConfiguration",
            writer =>
                JsonSerializer.Serialize(
                    writer,
                    new DidChangeConfigurationParams(settings),
                    LspJsonContext.Default.DidChangeConfigurationParams
                )
        );

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
                ["hover"] = new JsonObject { ["contentFormat"] = new JsonArray("markdown", "plaintext") },
                ["completion"] = new JsonObject
                {
                    ["completionItem"] = new JsonObject
                    {
                        // Items insert their text as it is.
                        ["snippetSupport"] = false,
                        ["commitCharactersSupport"] = true,
                        ["documentationFormat"] = new JsonArray("markdown", "plaintext"),
                        ["deprecatedSupport"] = true,
                        ["preselectSupport"] = true,
                        ["tagSupport"] = new JsonObject { ["valueSet"] = new JsonArray(1) },
                        ["insertReplaceSupport"] = true,
                        ["resolveSupport"] = new JsonObject
                        {
                            ["properties"] = new JsonArray("detail", "documentation", "additionalTextEdits"),
                        },
                    },
                    ["completionItemKind"] = new JsonObject
                    {
                        ["valueSet"] = new JsonArray([.. Enumerable.Range(1, 25).Select(k => JsonValue.Create(k))]),
                    },
                    ["contextSupport"] = true,
                },
                ["signatureHelp"] = new JsonObject
                {
                    ["signatureInformation"] = new JsonObject
                    {
                        ["documentationFormat"] = new JsonArray("markdown", "plaintext"),
                        ["parameterInformation"] = new JsonObject { ["labelOffsetSupport"] = true },
                        ["activeParameterSupport"] = true,
                    },
                    ["contextSupport"] = true,
                },
                ["semanticTokens"] = new JsonObject
                {
                    ["requests"] = new JsonObject { ["range"] = true, ["full"] = true },
                    ["tokenTypes"] = new JsonArray([
                        .. SemanticTokenStyles.TokenTypes.Select(t => JsonValue.Create(t)),
                    ]),
                    ["tokenModifiers"] = new JsonArray([
                        .. SemanticTokenStyles.TokenModifiers.Select(m => JsonValue.Create(m)),
                    ]),
                    ["formats"] = new JsonArray("relative"),
                    // Semantic colors are drawn over the TextMate colors.
                    ["augmentsSyntaxTokens"] = true,
                },
            },
        };

    /// <summary>
    /// Whether the server wants the full text with every change, rather than the changed ranges: a kind, or an object
    /// with a <c>change</c> kind. A value of another JSON kind means the default.
    /// </summary>
    private static bool IsFullSync(JsonElement? sync)
    {
        JsonElement? kind =
            sync is { ValueKind: JsonValueKind.Object } options
            && options.TryGetProperty("change", out JsonElement change)
                ? change
                : sync;
        return kind is { ValueKind: JsonValueKind.Number } number
            && number.TryGetInt32(out int value)
            && value == TextDocumentSyncKind.Full;
    }

    /// <summary>A server that answered <c>initialize</c>.</summary>
    /// <param name="Rpc">Its connection.</param>
    /// <param name="Since">When it answered, as a timestamp of the options' clock.</param>
    /// <param name="IsFullSync">Whether it wants the full text with every change.</param>
    /// <param name="Features">What it offers the editors.</param>
    private sealed record Running(JsonRpcConnection Rpc, long Since, bool IsFullSync, ServerFeatures Features);

    /// <summary>
    /// A document open in the server: what the server has of it, and what is still to send. At most one of its messages
    /// waits in the write queue; changes made meanwhile join it. Its members are guarded by the server's lock, except
    /// <see cref="Write"/> and <see cref="Written"/>, which take it.
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

        /// <summary>The snapshots written, oldest first: the texts that the server's diagnostics can refer to.</summary>
        private readonly List<TextSnapshot> _sent = [];

        /// <summary>The snapshot of the message that the write loop is writing, until it is on the stream.</summary>
        private TextSnapshot? _writing;

        /// <summary>The text that the server has once the queued message is written; null once it is closed.</summary>
        private TextSnapshot? _latest;

        /// <summary>The changes not sent yet; null when the full text is to be sent instead.</summary>
        private List<TextDocumentContentChangeEvent>? _changes = [];

        private readonly string _languageId;
        private int _changedLength;
        private bool _isOpened;
        private bool _isQueued;

        /// <summary>Queues the document's <c>didOpen</c>, with its current text.</summary>
        public OpenDocument(ServerConnection connection, Running running, ServerDocument document)
        {
            _connection = connection;
            _running = running;
            _languageId = document.Language.Id;
            Uri = document.Uri;
            Identity = DocumentUris.Identity(Uri);
            _latest = document.CurrentSnapshot;
            Queue();
        }

        /// <summary>The URI that the server knows the document by.</summary>
        public string Uri { get; }

        /// <summary>What names the document in the server's URIs, whatever form they take.</summary>
        public string Identity { get; }

        public void Change(TextSnapshot snapshot, IReadOnlyList<TextDocumentContentChangeEvent> changes)
        {
            // Versions only grow: a didOpen already has the text of this version or a later one.
            if (_latest is null || snapshot.Version <= _latest.Version)
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

        /// <summary>
        /// Closes the document in the server: a <c>didClose</c> of its own once the write loop took its <c>didOpen</c>,
        /// and nothing before. A message of it that still waits writes nothing, and it keeps no text.
        /// </summary>
        public void Close()
        {
            if (_isOpened)
            {
                string uri = Uri;
                _running.Rpc.Notify(
                    "textDocument/didClose",
                    writer =>
                        JsonSerializer.Serialize(
                            writer,
                            new DidCloseTextDocumentParams(new TextDocumentIdentifier(uri)),
                            LspJsonContext.Default.DidCloseTextDocumentParams
                        )
                );
            }
            _latest = null;
            _changes = null;
            _writing = null;
            _sent.Clear();
        }

        /// <summary>
        /// The text that diagnostics of <paramref name="version"/> refer to, or the latest one written for diagnostics
        /// without a version; null for a version not sent, or no longer kept. Versions before it are dropped: the
        /// server is past them.
        /// </summary>
        public TextSnapshot? Analyzed(int? version)
        {
            if (version is null)
                return _sent.Count > 0 ? _sent[^1] : null;
            // A server that answers quickly may name the version whose message is still being finished here.
            if (_writing?.Version == version)
            {
                _sent.Clear();
                return _writing;
            }
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
                // Closed: its didClose went on its own.
                if (_latest is not { } snapshot)
                    return false;
                List<TextDocumentContentChangeEvent>? changes = _changes;
                if (!_isOpened)
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
                _changes = [];
                _changedLength = 0;
                _writing = snapshot;
            }
            // Outside the lock: the text can be long.
            WriteNotification(writer, method, writeParams);
            return true;
        }

        /// <summary>The server can have the text now: diagnostics without a version refer to it from here on.</summary>
        public override void Written()
        {
            lock (_connection._lock)
            {
                if (_writing is null)
                    return;
                _sent.Add(_writing);
                _writing = null;
                if (_sent.Count > MaxSentVersions)
                    _sent.RemoveAt(0);
            }
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
                    new TextDocumentItem(Uri, _languageId, snapshot.Version, snapshot.GetText())
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
    }
}

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Darp.CodeEditor.Lsp;
using Darp.CodeEditor.Lsp.Hosting;
using Darp.CodeEditor.Lsp.Protocol;
using Protocol = Darp.CodeEditor.Lsp.Protocol;

namespace Darp.CodeEditor.LanguageServices;

/// <summary>
/// Language features from a language server over LSP, for all editors of one language: one server per service.
/// </summary>
/// <remarks>
/// <para>
/// The server starts when the first document opens, or earlier with <see cref="PrewarmAsync"/>. When it crashes, the
/// service restarts it with growing delays and opens the documents again; meanwhile requests answer nothing, so the
/// editors keep their syntax highlighting only, and <see cref="ILanguageService.Invalidated"/> brings the features
/// back.
/// </para>
/// <para>
/// One loop on the thread pool keeps a copy of every open document and sends in the order the editors called:
/// changes before the requests made after them. Calls on the UI thread only queue. The loop waits until each change is
/// written, and edits that queued up meanwhile go out as one <c>didChange</c>, so a server that stops reading does not
/// make the queue grow by a message per keystroke. Results are converted with the document version they were requested
/// for, and requests whose token is cancelled send <c>$/cancelRequest</c>.
/// </para>
/// </remarks>
public sealed class LspLanguageService : ILanguageService, IAsyncDisposable
{
    private static readonly Dictionary<string, ClassificationKind?> s_tokenTypes = new(StringComparer.Ordinal)
    {
        ["namespace"] = ClassificationKind.Namespace,
        ["type"] = ClassificationKind.Class,
        ["class"] = ClassificationKind.Class,
        ["struct"] = ClassificationKind.Struct,
        ["interface"] = ClassificationKind.Interface,
        ["enum"] = ClassificationKind.Enum,
        ["delegate"] = ClassificationKind.Delegate,
        ["typeParameter"] = ClassificationKind.TypeParameter,
        ["function"] = ClassificationKind.Method,
        ["method"] = ClassificationKind.Method,
        ["extensionMethod"] = ClassificationKind.ExtensionMethod,
        ["property"] = ClassificationKind.Property,
        ["event"] = ClassificationKind.Event,
        ["field"] = ClassificationKind.Field,
        ["constant"] = ClassificationKind.Constant,
        ["enumMember"] = ClassificationKind.EnumMember,
        ["variable"] = ClassificationKind.Local,
        ["parameter"] = ClassificationKind.Parameter,
        ["label"] = ClassificationKind.Label,
    };

    private readonly LspLanguageServiceOptions _options;
    private readonly Dictionary<string, ClassificationKind?> _tokenTypes;
    private readonly Supervisor<LanguageClient> _supervisor;
    private readonly HangTimeout _timeout;
    private readonly TimeProvider _time;
    private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>(
        new UnboundedChannelOptions { SingleReader = true }
    );
    private readonly Task _loop;
    private readonly ConcurrentDictionary<string, CompletionResult> _completions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Protocol.SignatureHelp> _signatures = new(StringComparer.Ordinal);

    // Only the loop touches these.
    private readonly Dictionary<string, ServerDocument> _documents = new(StringComparer.Ordinal);
    private LanguageClient? _client;

    private volatile ServerFeatures _features = ServerFeatures.None;
    private long _lastResultId;
    private int _state;

    public LspLanguageService(LspLanguageServiceOptions options)
        : this(options, TimeProvider.System) { }

    /// <param name="options">How to start the server and talk to it.</param>
    /// <param name="time">The clock of the hang timeouts.</param>
    internal LspLanguageService(LspLanguageServiceOptions options, TimeProvider time)
    {
        _options = options;
        _time = time;
        _tokenTypes = new Dictionary<string, ClassificationKind?>(s_tokenTypes, StringComparer.Ordinal);
        foreach (
            (string name, ClassificationKind? kind) in options.TokenTypes
                ?? new Dictionary<string, ClassificationKind?>()
        )
            _tokenTypes[name] = kind;
        _timeout = new HangTimeout(
            options.ResponseTimeout,
            options.MaxResponseTimeout,
            (options.Restart ?? new SupervisorOptions()).StableAfter,
            time
        );
        _supervisor = new Supervisor<LanguageClient>(StartClientAsync, client => client.Completion, options.Restart);
        _supervisor.Started += client => Post(() => OnStartedAsync(client));
        _supervisor.Failed += (client, exception) =>
        {
            if (client is not null)
            {
                ClearFeatures(client);
                _timeout.Ended(client, hung: false);
            }
            Post(() => OnFailed(client, exception));
            // At once, before the supervisor starts the next server: the state never says Running for an ended one.
            SetState(LanguageServerState.Restarting);
        };
        _loop = Task.Run(RunAsync);
    }

    public LanguageServiceCapabilities Capabilities => _features.Capabilities;

    public LanguageServerState State => (LanguageServerState)Volatile.Read(ref _state);

    /// <summary>The running server's process, if it is one.</summary>
    public int? ServerProcessId => _features.Client?.ProcessId;

    /// <summary>The name and version the running server reported.</summary>
    public ServerInfo? ServerInfo => _features.Client?.ServerInfo;

    public event EventHandler? Invalidated;

    public event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;

    /// <summary>Raised on any thread when <see cref="State"/> changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Starts the server before the first document opens.</summary>
    /// <returns>A task that completes when the server runs, or fails when its first start failed.</returns>
    /// <exception cref="ObjectDisposedException">The service was disposed.</exception>
    public async Task PrewarmAsync(CancellationToken cancellationToken = default)
    {
        await StartServer().WaitAsync(cancellationToken).ConfigureAwait(false);
        // The supervisor queued the start before this completed; this waits until the loop ran it.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(started.SetResult))
            throw new ObjectDisposedException(nameof(LspLanguageService));
        await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void OpenDocument(DocumentSnapshot document)
    {
        if (!Post(() => OpenAsync(document)))
            return;
        try
        {
            // The start's outcome reaches the editors through Invalidated; a failed start is logged and retried.
            _ = StartServer();
        }
        catch (ObjectDisposedException)
        {
            // Disposal is in progress.
        }
    }

    public void ChangeDocument(DocumentChange change) => _work.Writer.TryWrite(new Work(change, null));

    public void CloseDocument(string documentId) => Post(() => CloseAsync(documentId));

    public Task<CompletionList?> GetCompletionsAsync(
        DocumentVersion document,
        int position,
        CompletionTrigger trigger,
        CancellationToken cancellationToken
    ) =>
        RequestAsync<Protocol.CompletionList, CompletionList>(
            document,
            features => features.Completion,
            (client, open) =>
                client.Connection.SendRequestAsync(
                    LspMethods.Completion,
                    new CompletionParams(open.Identifier, open.Text.GetPosition(position), ToContext(trigger)),
                    cancellationToken
                ),
            (list, text, features) => MapCompletions(document.DocumentId, list, text, position, features),
            cancellationToken
        );

    public async Task<CompletionItemDetails?> ResolveCompletionItemAsync(
        CompletionItemReference reference,
        CancellationToken cancellationToken
    )
    {
        if (
            !_completions.TryGetValue(reference.DocumentId, out CompletionResult? result)
            || result.ResultId != reference.ResultId
            || (uint)reference.Index >= (uint)result.Items.Length
        )
        {
            return null;
        }
        Protocol.CompletionItem item = result.Items[reference.Index];
        if (item.Data is null && result.DefaultData is { } data)
            item = item with { Data = data };
        if (result.Client.Capabilities.CompletionProvider?.ResolveProvider == true && _features.Client == result.Client)
        {
            try
            {
                item =
                    await AnswerAsync(
                            result.Client,
                            result.Client.Connection.SendRequestAsync(
                                LspMethods.CompletionResolve,
                                item,
                                cancellationToken
                            ),
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                    ?? item;
            }
            catch (JsonRpcException exception)
            {
                Log($"Resolving the completion item {item.Label} failed: {exception.Message}");
            }
            catch (IOException)
            {
                return null;
            }
        }
        return new CompletionItemDetails(
            Describe(item),
            item.AdditionalTextEdits is { } edits ? MapEdits(edits, result.Text) : null
        );
    }

    public async Task<IReadOnlyList<ClassifiedRange>> GetClassificationsAsync(
        DocumentVersion document,
        TextRange range,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<ClassifiedRange>? classified = _features.TokensRange
            ? await RequestAsync<SemanticTokens, IReadOnlyList<ClassifiedRange>>(
                    document,
                    features => features.TokensRange,
                    (client, open) =>
                        client.Connection.SendRequestAsync(
                            LspMethods.SemanticTokensRange,
                            new SemanticTokensRangeParams(open.Identifier, open.Text.GetRange(range)),
                            cancellationToken
                        ),
                    (tokens, text, features) => Decode(tokens.Data, text, features.TokenKinds, range),
                    cancellationToken
                )
                .ConfigureAwait(false)
            : await RequestAsync<SemanticTokens, IReadOnlyList<ClassifiedRange>>(
                    document,
                    features => features.TokensFull,
                    // The tokens of a version serve all its ranges, so no single request cancels them.
                    (client, open) => open.GetFullTokens(client),
                    (tokens, text, features) => Decode(tokens.Data, text, features.TokenKinds, range),
                    cancellationToken
                )
                .ConfigureAwait(false);
        return classified ?? [];
    }

    public Task<QuickInfo?> GetQuickInfoAsync(
        DocumentVersion document,
        int position,
        CancellationToken cancellationToken
    ) =>
        RequestAsync<Hover, QuickInfo>(
            document,
            features => features.Hover,
            (client, open) =>
                client.Connection.SendRequestAsync(
                    LspMethods.Hover,
                    new HoverParams(open.Identifier, open.Text.GetPosition(position)),
                    cancellationToken
                ),
            (hover, text, _) =>
                Markdown.ToBlocks(hover.Contents) is { Count: > 0 } content
                    ? new QuickInfo(
                        hover.Range is { } hovered ? text.GetTextRange(hovered) : text.GetWordRange(position),
                        content
                    )
                    : null,
            cancellationToken
        );

    public Task<SignatureHelp?> GetSignatureHelpAsync(
        DocumentVersion document,
        int position,
        SignatureHelpTrigger trigger,
        CancellationToken cancellationToken
    ) =>
        RequestAsync<Protocol.SignatureHelp, SignatureHelp>(
            document,
            features => features.SignatureHelp,
            (client, open) =>
                client.Connection.SendRequestAsync(
                    LspMethods.SignatureHelp,
                    new SignatureHelpParams(
                        open.Identifier,
                        open.Text.GetPosition(position),
                        new SignatureHelpContext(
                            trigger.Kind switch
                            {
                                SignatureHelpTriggerKind.TriggerCharacter => Protocol
                                    .SignatureHelpTriggerKind
                                    .TriggerCharacter,
                                SignatureHelpTriggerKind.ContentChange => Protocol
                                    .SignatureHelpTriggerKind
                                    .ContentChange,
                                _ => Protocol.SignatureHelpTriggerKind.Invoked,
                            },
                            trigger.IsRetrigger,
                            trigger.Kind == SignatureHelpTriggerKind.TriggerCharacter
                                ? trigger.Character.ToString()
                                : null,
                            trigger.IsRetrigger ? _signatures.GetValueOrDefault(document.DocumentId) : null
                        )
                    ),
                    cancellationToken
                ),
            (help, text, _) =>
            {
                _signatures[document.DocumentId] = help;
                return MapSignatureHelp(help, text, position);
            },
            cancellationToken
        );

    /// <summary>Shuts the server down and stops serving the editors.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _state, (int)LanguageServerState.Disposed) == (int)LanguageServerState.Disposed)
            return;
        StateChanged?.Invoke(this, EventArgs.Empty);
        // Ending the server first also ends a write that waits for it.
        await _supervisor.DisposeAsync().ConfigureAwait(false);
        _work.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
    }

    /// <exception cref="ObjectDisposedException">The service was disposed.</exception>
    private Task<LanguageClient> StartServer()
    {
        if (
            Interlocked.CompareExchange(ref _state, (int)LanguageServerState.Starting, (int)LanguageServerState.Stopped)
            == (int)LanguageServerState.Stopped
        )
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        return _supervisor.StartAsync();
    }

    private async Task<LanguageClient> StartClientAsync(CancellationToken cancellationToken)
    {
        LanguageServerTransport transport = await _options.StartServer(cancellationToken).ConfigureAwait(false);
        var options = new LanguageClientOptions(CreateInitializeParams())
        {
            Settings = _options.Settings,
            Log = _options.Log,
            Limits = _options.Limits,
            Configure = connection =>
                connection.OnNotification(
                    LspMethods.PublishDiagnostics,
                    (diagnostics, _) =>
                    {
                        Post(() => OnDiagnostics(connection, diagnostics));
                        return Task.CompletedTask;
                    }
                ),
        };
        // A server that never answers initialize counts as a failed start, which is retried.
        TimeSpan timeout = _timeout.Of(null);
        using var timer = new CancellationTokenSource(timeout, _time);
        using var initializing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        try
        {
            return await LanguageClient.StartAsync(transport, options, initializing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A hang like any other: the next start waits longer, so that a server that starts slowly gets to run.
            _timeout.Ended(null, hung: true);
            throw new TimeoutException(
                $"The language server did not answer initialize within {timeout.TotalSeconds:0.#} s."
            );
        }
    }

    private InitializeParams CreateInitializeParams() =>
        new(
            new ClientCapabilities
            {
                General = new GeneralClientCapabilities { PositionEncodings = ["utf-16"] },
                Workspace = new WorkspaceClientCapabilities { Configuration = true },
                TextDocument = new TextDocumentClientCapabilities
                {
                    Completion = new CompletionClientCapabilities
                    {
                        ContextSupport = true,
                        CompletionItem = new CompletionItemClientCapabilities
                        {
                            CommitCharactersSupport = true,
                            DocumentationFormat = [MarkupKind.Markdown, MarkupKind.PlainText],
                            LabelDetailsSupport = true,
                            ResolveSupport = new CompletionResolveSupport([
                                "documentation",
                                "detail",
                                "additionalTextEdits",
                            ]),
                        },
                        CompletionList = new CompletionListClientCapabilities
                        {
                            ItemDefaults = ["commitCharacters", "editRange", "insertTextFormat", "data"],
                        },
                    },
                    Hover = new HoverClientCapabilities { ContentFormat = [MarkupKind.Markdown, MarkupKind.PlainText] },
                    SignatureHelp = new SignatureHelpClientCapabilities
                    {
                        ContextSupport = true,
                        SignatureInformation = new SignatureInformationClientCapabilities
                        {
                            DocumentationFormat = [MarkupKind.Markdown, MarkupKind.PlainText],
                            ParameterInformation = new ParameterInformationClientCapabilities(LabelOffsetSupport: true),
                            ActiveParameterSupport = true,
                        },
                    },
                    PublishDiagnostics = new PublishDiagnosticsClientCapabilities { VersionSupport = true },
                    SemanticTokens = new SemanticTokensClientCapabilities(
                        new SemanticTokensRequests(Range: true, Full: true),
                        [.. _tokenTypes.Keys],
                        ["static", "readonly", "defaultLibrary"],
                        ["relative"]
                    ),
                },
            }
        )
        {
            ProcessId = Environment.ProcessId,
            ClientInfo = new ClientInfo(_options.ClientName),
            RootUri = _options.RootUri,
            WorkspaceFolders = _options.RootUri is { } root ? [new WorkspaceFolder(root, "root")] : null,
            InitializationOptions = _options.InitializationOptions,
        };

    private bool Post(Func<ValueTask> work) => _work.Writer.TryWrite(new Work(null, work));

    private bool Post(Action work) =>
        Post(() =>
        {
            work();
            return ValueTask.CompletedTask;
        });

    private async Task RunAsync()
    {
        ChannelReader<Work> reader = _work.Reader;
        var changes = new List<DocumentChange>();
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out Work work))
            {
                try
                {
                    if (work.Change is { } change)
                    {
                        changes.Clear();
                        changes.Add(change);
                        // Edits that queued up while the last one was written go out as one didChange.
                        while (
                            reader.TryPeek(out Work next)
                            && next.Change is { } more
                            && more.DocumentId == change.DocumentId
                        )
                        {
                            reader.TryRead(out _);
                            changes.Add(more);
                        }
                        await ChangeAsync(changes).ConfigureAwait(false);
                    }
                    else if (work.Run is { } run)
                    {
                        await run().ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    Log($"The language service failed: {exception}");
                }
            }
        }
    }

    private async ValueTask OnStartedAsync(LanguageClient client)
    {
        _client = client;
        _features = ServerFeatures.From(client, _tokenTypes);
        foreach (ServerDocument document in _documents.Values)
            document.CancelFullTokens();
        Log($"The language server {client.ServerInfo?.Name} {client.ServerInfo?.Version} runs.");
        foreach (ServerDocument document in _documents.Values)
            await NotifyAsync(client, LspMethods.DidOpen, document.CreateOpenParams(_options.LanguageId))
                .ConfigureAwait(false);
        // A server that ended meanwhile stays Restarting; OnFailed follows.
        if (!HasEnded(client))
        {
            SetState(LanguageServerState.Running);
            // If it ended in between, its failure may have said Restarting before this said Running.
            if (HasEnded(client))
                SetState(LanguageServerState.Restarting);
        }
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    private void OnFailed(LanguageClient? client, Exception? exception)
    {
        if (State == LanguageServerState.Disposed)
            return;
        if (exception is not null)
            Log($"Starting the language server failed: {exception.Message}");
        if (client is not null && client == _client)
        {
            Log("The language server stopped; it restarts.");
            _client = null;
            _features = ServerFeatures.None;
            foreach (ServerDocument document in _documents.Values)
                document.CancelFullTokens();
            _completions.Clear();
            _signatures.Clear();
            foreach (ServerDocument document in _documents.Values)
                DiagnosticsChanged?.Invoke(
                    this,
                    new DiagnosticsChangedEventArgs(document.Id, document.Text.Version, [])
                );
            Invalidated?.Invoke(this, EventArgs.Empty);
        }
        SetState(LanguageServerState.Restarting);
    }

    private async ValueTask OpenAsync(DocumentSnapshot snapshot)
    {
        if (_documents.ContainsKey(snapshot.DocumentId))
            await CloseAsync(snapshot.DocumentId).ConfigureAwait(false);
        var document = new ServerDocument(
            snapshot.DocumentId,
            ToUri(snapshot.DocumentId),
            new DocumentText(snapshot.Version, snapshot.Text)
        );
        _documents[snapshot.DocumentId] = document;
        if (_client is { } client)
            await NotifyAsync(client, LspMethods.DidOpen, document.CreateOpenParams(_options.LanguageId))
                .ConfigureAwait(false);
    }

    private async ValueTask ChangeAsync(List<DocumentChange> changes)
    {
        if (!_documents.TryGetValue(changes[0].DocumentId, out ServerDocument? document))
            return;
        var events = new List<TextDocumentContentChangeEvent>(changes.Count);
        foreach (DocumentChange change in changes)
        {
            DocumentText before = document.Text;
            document.Text = before.Apply(change.Version, change.Change);
            document.CancelFullTokens();
            var removed = new TextRange(change.Change.Offset, change.Change.RemovedLength);
            events.Add(new TextDocumentContentChangeEvent(change.Change.InsertedText, before.GetRange(removed)));
        }
        if (_client is not { } client || _features.Sync == TextDocumentSyncKind.None)
            return;
        // Full synchronization sends the text once, however many edits queued up.
        IReadOnlyList<TextDocumentContentChangeEvent> contentChanges =
            _features.Sync == TextDocumentSyncKind.Full
                ? [new TextDocumentContentChangeEvent(document.Text.Text)]
                : events;
        await NotifyAsync(
                client,
                LspMethods.DidChange,
                new DidChangeTextDocumentParams(
                    new VersionedTextDocumentIdentifier(document.Uri, document.Text.Version),
                    contentChanges
                )
            )
            .ConfigureAwait(false);
    }

    private async ValueTask CloseAsync(string documentId)
    {
        if (!_documents.Remove(documentId, out ServerDocument? document))
            return;
        document.CancelFullTokens();
        _completions.TryRemove(documentId, out _);
        _signatures.TryRemove(documentId, out _);
        if (_client is { } client)
            await NotifyAsync(client, LspMethods.DidClose, new DidCloseTextDocumentParams(document.Identifier))
                .ConfigureAwait(false);
    }

    private void OnDiagnostics(JsonRpcConnection connection, PublishDiagnosticsParams published)
    {
        if (_client?.Connection != connection || FindByUri(published.Uri) is not { } document)
            return;
        DocumentText text = document.Text;
        // Diagnostics of an older version are stale; the server publishes the current version's later.
        if (published.Version is { } version && version != text.Version)
            return;
        DiagnosticInfo[] diagnostics =
        [
            .. published
                .Diagnostics.Where(d => d.Severity != Protocol.DiagnosticSeverity.Hint)
                .Select(d => new DiagnosticInfo(
                    text.GetTextRange(d.Range),
                    d.Severity switch
                    {
                        Protocol.DiagnosticSeverity.Error => DiagnosticSeverity.Error,
                        Protocol.DiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
                        _ => DiagnosticSeverity.Info,
                    },
                    d.Code ?? d.Source ?? "",
                    d.Message
                )),
        ];
        DiagnosticsChanged?.Invoke(this, new DiagnosticsChangedEventArgs(document.Id, text.Version, diagnostics));
    }

    private ServerDocument? FindByUri(string uri)
    {
        foreach (ServerDocument document in _documents.Values)
        {
            if (document.Uri == uri)
                return document;
        }
        // Servers may write the URI another way, such as with other escapes.
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? published))
            return null;
        return _documents.Values.FirstOrDefault(d =>
            Uri.TryCreate(d.Uri, UriKind.Absolute, out Uri? sent) && sent == published
        );
    }

    /// <summary>
    /// Sends a request from the loop, after the changes that came before it, for the document version it names.
    /// </summary>
    /// <param name="send">Sends the request; runs on the loop while the document is at the requested version.</param>
    /// <param name="map">Converts the response with the text of the requested version.</param>
    /// <returns>Null when the server does not support the request, is not running, or the version is gone.</returns>
    private async Task<TResult?> RequestAsync<TResponse, TResult>(
        DocumentVersion document,
        Func<ServerFeatures, bool> supported,
        Func<LanguageClient, ServerDocument, Task<TResponse?>> send,
        Func<TResponse, DocumentText, ServerFeatures, TResult?> map,
        CancellationToken cancellationToken
    )
        where TResponse : class
        where TResult : class
    {
        if (!supported(_features))
            return null;
        var sent = new TaskCompletionSource<Sent<TResponse>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = Post(() =>
        {
            try
            {
                ServerFeatures features = _features;
                sent.SetResult(
                    cancellationToken.IsCancellationRequested
                    || features.Client is not { } client
                    || !supported(features)
                    || !_documents.TryGetValue(document.DocumentId, out ServerDocument? open)
                    || open.Text.Version != document.Version
                        ? null
                        : new Sent<TResponse>(send(client, open), open.Text, features, client)
                );
            }
            catch (Exception exception)
            {
                sent.SetException(exception);
            }
        });
        if (!queued)
            return null;
        if (await sent.Task.WaitAsync(cancellationToken).ConfigureAwait(false) is not { } request)
            return null;
        TResponse? response;
        try
        {
            response = await AnswerAsync(request.Client, request.Response, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcException exception)
            when (exception.Code is ErrorCodes.RequestCancelled or ErrorCodes.ContentModified)
        {
            return null;
        }
        catch (IOException)
        {
            // The connection ended: the server restarts and the editors ask again.
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A shared request that a change, closing or a new server made obsolete.
            return null;
        }
        return response is null ? null : map(response, request.Text, request.Features);
    }

    /// <summary>Sends a notification and waits until it is written: a server that stops reading counts as hung.</summary>
    private async ValueTask NotifyAsync<T>(LanguageClient client, NotificationType<T> type, T parameters)
    {
        TimeSpan timeout = _timeout.Of(client);
        try
        {
            await client
                .Connection.SendNotificationAsync(type, parameters)
                .WaitAsync(timeout, _time)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Abort(client, $"it read nothing for {timeout.TotalSeconds:0.#} s");
        }
        catch (IOException)
        {
            // The connection ended: the server restarts and gets the documents again.
        }
    }

    /// <summary>
    /// Waits for the answer to a request while the caller wants it. A server that gives none within its
    /// <see cref="HangTimeout"/> counts as hung: it is ended, and restarts.
    /// </summary>
    /// <exception cref="IOException">The server counts as hung.</exception>
    private async Task<T?> AnswerAsync<T>(LanguageClient client, Task<T?> response, CancellationToken cancellationToken)
    {
        TimeSpan timeout = _timeout.Of(client);
        T? answer;
        try
        {
            answer = await response.WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Abort(client, $"it answered nothing for {timeout.TotalSeconds:0.#} s");
            throw new IOException("The language server stopped answering.");
        }
        _timeout.Answered(client);
        return answer;
    }

    /// <summary>Ends a server that stopped answering or reading; the supervisor starts a new one.</summary>
    private void Abort(LanguageClient client, string reason)
    {
        // The first to find it hung ends it; the state says so at once.
        if (!ClearFeatures(client))
            return;
        _timeout.Ended(client, hung: true);
        Log($"The language server is hung: {reason}. It is ended and restarts.");
        _ = AbortAsync();
        SetState(LanguageServerState.Restarting);

        async Task AbortAsync()
        {
            try
            {
                await client.AbortAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Log($"Ending the hung language server failed: {exception}");
            }
        }
    }

    private CompletionList MapCompletions(
        string documentId,
        Protocol.CompletionList list,
        DocumentText text,
        int position,
        ServerFeatures features
    )
    {
        CompletionItemDefaults? defaults = list.ItemDefaults;
        TextRange? defaultRange = defaults?.EditRange is { } editRange ? text.GetTextRange(editRange.Replace) : null;
        IReadOnlyList<char>? defaultCommitCharacters = defaults?.CommitCharacters is { } commit
            ? ToChars(commit)
            : null;
        Protocol.CompletionItem[] items = [.. list.Items.OrderBy(i => i.SortText ?? i.Label, StringComparer.Ordinal)];
        TextRange applicable =
            defaultRange
            ?? (
                items.FirstOrDefault(i => i.TextEdit is not null)?.TextEdit is { } first
                    ? text.GetTextRange(first.Replace)
                    : text.GetWordRange(position)
            );
        var mapped = new CompletionItem[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            Protocol.CompletionItem item = items[i];
            TextRange? range = item.TextEdit is { } edit ? text.GetTextRange(edit.Replace) : null;
            string insert = item.TextEdit?.NewText ?? item.TextEditText ?? item.InsertText ?? item.Label;
            if ((item.InsertTextFormat ?? defaults?.InsertTextFormat) == InsertTextFormat.Snippet)
                insert = Snippet.ToPlainText(insert);
            mapped[i] = new CompletionItem(
                item.Label,
                item.FilterText ?? item.Label,
                insert,
                ToKind(item.Kind),
                item.LabelDetails?.Description ?? item.LabelDetails?.Detail,
                range == applicable ? null : range,
                item.CommitCharacters is { } characters ? ToChars(characters) : defaultCommitCharacters,
                item.AdditionalTextEdits is { } additional ? MapEdits(additional, text) : null
            );
        }
        long resultId = Interlocked.Increment(ref _lastResultId);
        if (features.Client is { } client)
            _completions[documentId] = new CompletionResult(resultId, client, text, items, defaults?.Data);
        return new CompletionList(resultId, applicable, mapped, list.IsIncomplete);
    }

    private static SignatureHelp? MapSignatureHelp(Protocol.SignatureHelp help, DocumentText text, int position)
    {
        if (help.Signatures.Count == 0)
            return null;
        SignatureInfo[] signatures =
        [
            .. help.Signatures.Select(signature =>
            {
                var parameters = new List<ParameterInfo>();
                int searchFrom = 0;
                foreach (ParameterInformation parameter in signature.Parameters ?? [])
                {
                    TextRange? range = null;
                    string label;
                    if (parameter.Label.Text is { } name)
                    {
                        label = name;
                        int index = signature.Label.IndexOf(name, searchFrom, StringComparison.Ordinal);
                        if (index >= 0)
                        {
                            range = new TextRange(index, name.Length);
                            searchFrom = index + name.Length;
                        }
                    }
                    else
                    {
                        int start = Math.Clamp(parameter.Label.Start, 0, signature.Label.Length);
                        int end = Math.Clamp(parameter.Label.End, start, signature.Label.Length);
                        label = signature.Label[start..end];
                        range = TextRange.FromBounds(start, end);
                    }
                    parameters.Add(new ParameterInfo(label, Markdown.ToPlainText(parameter.Documentation), range));
                }
                return new SignatureInfo(
                    signature.Label,
                    Markdown.ToPlainText(signature.Documentation),
                    parameters,
                    signature.ActiveParameter
                );
            }),
        ];
        return new SignatureHelp(
            signatures,
            Math.Clamp(help.ActiveSignature ?? 0, 0, signatures.Length - 1),
            help.ActiveParameter ?? 0,
            text.GetArgumentListRange(position)
        );
    }

    private static IReadOnlyList<ClassifiedRange> Decode(
        int[] data,
        DocumentText text,
        ClassificationKind?[] kinds,
        TextRange range
    )
    {
        var classified = new List<ClassifiedRange>(data.Length / 5);
        int line = 0;
        int character = 0;
        for (int i = 0; i + 4 < data.Length; i += 5)
        {
            line += data[i];
            character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
            int type = data[i + 3];
            if ((uint)type >= (uint)kinds.Length || kinds[type] is not { } kind)
                continue;
            int start = text.GetOffset(new Position(line, character));
            var token = new TextRange(start, Math.Clamp(data[i + 2], 0, text.Text.Length - start));
            if (token.End >= range.Start && token.Start <= range.End)
                classified.Add(new ClassifiedRange(token, kind));
        }
        return classified;
    }

    private static IReadOnlyList<MarkupBlock> Describe(Protocol.CompletionItem item)
    {
        var blocks = new List<MarkupBlock>();
        if (
            item.Detail is { Length: > 0 } detail
            && item.Documentation?.Value.Contains(detail, StringComparison.Ordinal) != true
        )
        {
            blocks.Add(new MarkupBlock(MarkupBlockKind.Code, detail));
        }
        blocks.AddRange(Markdown.ToBlocks(item.Documentation));
        return blocks;
    }

    private static TextEdit[] MapEdits(IReadOnlyList<Protocol.TextEdit> edits, DocumentText text) =>
        [.. edits.Select(e => new TextEdit(text.GetTextRange(e.Range), e.NewText))];

    /// <summary>
    /// LSP's context: typing a server's trigger character is <c>TriggerCharacter</c>, asking again for an incomplete
    /// list is <c>TriggerForIncompleteCompletions</c>, and everything else, also typing the first letter of a word, is
    /// <c>Invoked</c>.
    /// </summary>
    private static CompletionContext ToContext(CompletionTrigger trigger) =>
        trigger.Kind switch
        {
            CompletionTriggerKind.TriggerCharacter => new CompletionContext(
                Protocol.CompletionTriggerKind.TriggerCharacter,
                trigger.Character.ToString()
            ),
            CompletionTriggerKind.IncompleteList => new CompletionContext(
                Protocol.CompletionTriggerKind.TriggerForIncompleteCompletions
            ),
            _ => new CompletionContext(Protocol.CompletionTriggerKind.Invoked),
        };

    private static CompletionItemKind ToKind(Protocol.CompletionItemKind? kind) =>
        kind switch
        {
            Protocol.CompletionItemKind.Method
            or Protocol.CompletionItemKind.Function
            or Protocol.CompletionItemKind.Constructor => CompletionItemKind.Method,
            Protocol.CompletionItemKind.Field => CompletionItemKind.Field,
            Protocol.CompletionItemKind.Variable => CompletionItemKind.Local,
            Protocol.CompletionItemKind.Class => CompletionItemKind.Class,
            Protocol.CompletionItemKind.Interface => CompletionItemKind.Interface,
            Protocol.CompletionItemKind.Module => CompletionItemKind.Namespace,
            Protocol.CompletionItemKind.Property => CompletionItemKind.Property,
            Protocol.CompletionItemKind.Enum => CompletionItemKind.Enum,
            Protocol.CompletionItemKind.Keyword => CompletionItemKind.Keyword,
            Protocol.CompletionItemKind.Snippet => CompletionItemKind.Snippet,
            Protocol.CompletionItemKind.EnumMember => CompletionItemKind.EnumMember,
            Protocol.CompletionItemKind.Constant => CompletionItemKind.Constant,
            Protocol.CompletionItemKind.Struct => CompletionItemKind.Struct,
            Protocol.CompletionItemKind.Event => CompletionItemKind.Event,
            Protocol.CompletionItemKind.TypeParameter => CompletionItemKind.TypeParameter,
            _ => CompletionItemKind.Text,
        };

    private static char[] ToChars(IReadOnlyList<string> characters) =>
        [.. characters.Where(c => c.Length == 1).Select(c => c[0])];

    private string ToUri(string documentId) =>
        _options.DocumentUri?.Invoke(documentId)
        ?? (
            Path.IsPathRooted(documentId)
                ? new Uri(documentId).AbsoluteUri
                : "file:///" + Uri.EscapeDataString(documentId)
        );

    /// <summary>Takes the features of <paramref name="client"/> away, on any thread.</summary>
    /// <returns>False when they were not its features, such as when it ended before.</returns>
    private bool ClearFeatures(LanguageClient client)
    {
        ServerFeatures features = _features;
        return features.Client == client
            && Interlocked.CompareExchange(ref _features, ServerFeatures.None, features) == features;
    }

    /// <summary>Whether <paramref name="client"/> ended or was found hung, which may not have reached the loop yet.</summary>
    private bool HasEnded(LanguageClient client) => _features.Client != client || client.Completion.IsCompleted;

    /// <summary>Sets the state on any thread; the last call wins, and nothing follows Disposed.</summary>
    private void SetState(LanguageServerState state)
    {
        int current = Volatile.Read(ref _state);
        while (current != (int)state && current != (int)LanguageServerState.Disposed)
        {
            int seen = Interlocked.CompareExchange(ref _state, (int)state, current);
            if (seen == current)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            current = seen;
        }
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    /// <summary>A change to send, or other work for the loop.</summary>
    private readonly record struct Work(DocumentChange? Change, Func<ValueTask>? Run);

    private sealed record Sent<TResponse>(
        Task<TResponse?> Response,
        DocumentText Text,
        ServerFeatures Features,
        LanguageClient Client
    )
        where TResponse : class;

    /// <param name="Items">The server's items in the order of the editor's list.</param>
    /// <param name="DefaultData">The list's default data, which resolve sends for items without their own.</param>
    private sealed record CompletionResult(
        long ResultId,
        LanguageClient Client,
        DocumentText Text,
        Protocol.CompletionItem[] Items,
        JsonElement? DefaultData
    );

    /// <summary>A document the editors opened, with its text as the loop last changed it.</summary>
    private sealed class ServerDocument(string id, string uri, DocumentText text)
    {
        private FullTokens? _fullTokens;

        public string Id { get; } = id;

        public string Uri { get; } = uri;

        public TextDocumentIdentifier Identifier { get; } = new(uri);

        public DocumentText Text { get; set; } = text;

        public DidOpenTextDocumentParams CreateOpenParams(string languageId) =>
            new(new TextDocumentItem(Uri, languageId, Text.Version, Text.Text));

        /// <summary>
        /// The tokens of the whole document, requested once per version and server; every range of the version uses
        /// them, so no single caller cancels them.
        /// </summary>
        public Task<SemanticTokens?> GetFullTokens(LanguageClient client)
        {
            if (_fullTokens is { } cached && cached.Text == Text && cached.Client == client)
                return cached.Tokens;
            CancelFullTokens();
            var cancellation = new CancellationTokenSource();
            Task<SemanticTokens?> tokens = client.Connection.SendRequestAsync(
                LspMethods.SemanticTokensFull,
                new SemanticTokensParams(Identifier),
                cancellation.Token
            );
            _fullTokens = new FullTokens(Text, client, tokens, cancellation);
            return tokens;
        }

        /// <summary>Cancels the request for the whole document's tokens once a change, closing or a new server made it obsolete.</summary>
        public void CancelFullTokens()
        {
            if (_fullTokens is not { } cached)
                return;
            _fullTokens = null;
            cached.Cancellation.Cancel();
            cached.Cancellation.Dispose();
        }

        private sealed record FullTokens(
            DocumentText Text,
            LanguageClient Client,
            Task<SemanticTokens?> Tokens,
            CancellationTokenSource Cancellation
        );
    }

    /// <summary>What the running server supports, for any thread.</summary>
    private sealed record ServerFeatures(
        LanguageClient? Client,
        LanguageServiceCapabilities Capabilities,
        TextDocumentSyncKind Sync,
        bool Completion,
        bool Hover,
        bool SignatureHelp,
        bool TokensRange,
        bool TokensFull,
        ClassificationKind?[] TokenKinds
    )
    {
        /// <summary>No server runs; every feature is off.</summary>
        public static ServerFeatures None { get; } =
            new(
                null,
                LanguageServiceCapabilities.None,
                TextDocumentSyncKind.None,
                false,
                false,
                false,
                false,
                false,
                []
            );

        public static ServerFeatures From(LanguageClient client, Dictionary<string, ClassificationKind?> tokenTypes)
        {
            ServerCapabilities capabilities = client.Capabilities;
            SemanticTokensOptions? tokens = capabilities.SemanticTokensProvider;
            SignatureHelpOptions? signatureHelp = capabilities.SignatureHelpProvider;
            return new ServerFeatures(
                client,
                new LanguageServiceCapabilities(
                    ToChars(capabilities.CompletionProvider?.TriggerCharacters ?? []),
                    ToChars(capabilities.CompletionProvider?.AllCommitCharacters ?? []),
                    ToChars([.. signatureHelp?.TriggerCharacters ?? [], .. signatureHelp?.RetriggerCharacters ?? []])
                ),
                capabilities.TextDocumentSync?.Change ?? TextDocumentSyncKind.None,
                capabilities.CompletionProvider is not null,
                capabilities.HoverProvider == true,
                signatureHelp is not null,
                tokens?.Range == true,
                tokens?.Full == true,
                [.. (tokens?.Legend.TokenTypes ?? []).Select(type => tokenTypes.GetValueOrDefault(type))]
            );
        }
    }

    /// <summary>Snippet syntax as plain text, for servers that send snippets although the client does not ask for them.</summary>
    private static class Snippet
    {
        public static string ToPlainText(string snippet)
        {
            var builder = new StringBuilder(snippet.Length);
            for (int i = 0; i < snippet.Length; i++)
            {
                char c = snippet[i];
                if (c == '\\' && i + 1 < snippet.Length && snippet[i + 1] is '$' or '}' or '\\')
                {
                    builder.Append(snippet[++i]);
                }
                else if (c == '$' && i + 1 < snippet.Length && char.IsAsciiDigit(snippet[i + 1]))
                {
                    while (i + 1 < snippet.Length && char.IsAsciiDigit(snippet[i + 1]))
                        i++;
                }
                else if (c == '$' && i + 2 < snippet.Length && snippet[i + 1] == '{')
                {
                    // ${1:text} keeps the text, ${1|a,b|} the first choice, ${1} nothing.
                    int j = i + 2;
                    while (j < snippet.Length && char.IsAsciiDigit(snippet[j]))
                        j++;
                    if (j < snippet.Length && snippet[j] == '|')
                    {
                        int end = snippet.IndexOfAny([',', '|'], j + 1);
                        builder.Append(snippet, j + 1, (end < 0 ? snippet.Length : end) - j - 1);
                        int close = snippet.IndexOf('}', j);
                        i = close < 0 ? snippet.Length : close;
                        continue;
                    }
                    i = j < snippet.Length && snippet[j] == ':' ? j : j - 1;
                    if (j < snippet.Length && snippet[j] == '}')
                        i = j;
                }
                else if (c != '}' || !IsPlaceholderEnd(snippet, i))
                {
                    builder.Append(c);
                }
            }
            return builder.ToString();
        }

        /// <summary>Whether a '}' closes a <c>${n:</c> placeholder, by counting the openings before it.</summary>
        private static bool IsPlaceholderEnd(string snippet, int index)
        {
            int open = 0;
            for (int i = 0; i < index; i++)
            {
                if (snippet[i] == '\\')
                    i++;
                else if (snippet[i] == '$' && i + 1 < snippet.Length && snippet[i + 1] == '{')
                    open++;
                else if (snippet[i] == '}' && open > 0)
                    open--;
            }
            return open > 0;
        }
    }
}

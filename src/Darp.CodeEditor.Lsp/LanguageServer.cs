using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Darp.CodeEditor.LanguageServices;

namespace Darp.CodeEditor.Lsp;

/// <summary>
/// Serves one <see cref="ILanguageService"/> to a Language Server Protocol client, in-process: text sync, completion
/// with lazily resolved descriptions, hover, signature help, pushed diagnostics and semantic tokens.
/// </summary>
/// <remarks>
/// <para>
/// Messages are JSON-RPC strings without LSP headers. <see cref="Receive"/> only queues a message, so it can be called
/// on the UI thread. One loop on the thread pool parses messages in order and owns the open documents; requests then
/// run concurrently and answer through the send callback, from any thread.
/// </para>
/// <para>
/// The initialize request is answered once the service exists, like the AvaloniaEdit editor switches its features on
/// when the service is ready. Each request works on the document version current when it arrived, and
/// <c>$/cancelRequest</c> cancels it.
/// </para>
/// </remarks>
public sealed class LanguageServer : IAsyncDisposable
{
    private const int RequestCancelled = -32800;
    private const int MethodNotFound = -32601;
    private const int InternalError = -32603;
    private static readonly TimeSpan s_diagnosticsDelay = TimeSpan.FromMilliseconds(250);

    private readonly Task<ILanguageService> _service;
    private readonly Action<string> _send;
    private readonly Channel<string> _inbox = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true }
    );
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);
    private readonly Task _loop;

    // Owned by the message loop.
    private readonly Dictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private bool _clientSupportsItemDefaults;

    /// <param name="service">The service, possibly still being created.</param>
    /// <param name="send">Sends a message to the client. Called from thread-pool threads, possibly concurrently.</param>
    public LanguageServer(Task<ILanguageService> service, Action<string> send)
    {
        _service = service;
        _send = send;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Raised on a thread-pool thread when the service failed a request.</summary>
    public event Action<Exception>? RequestFailed;

    /// <summary>For measurements: raised on a thread-pool thread after a response was sent, with the method and the
    /// time from receiving the request to sending the response (service work plus serialization).</summary>
    public event Action<string, TimeSpan>? RequestHandled;

    /// <summary>Token types of the semantic tokens legend, in <see cref="ClassificationKind"/> order.</summary>
    public static IReadOnlyList<string> TokenTypes { get; } =
    [.. Enum.GetNames<ClassificationKind>().Select(n => char.ToLowerInvariant(n[0]) + n[1..])];

    /// <summary>Queues a message from the client. Never blocks.</summary>
    public void Receive(string message) => _inbox.Writer.TryWrite(message);

    public async ValueTask DisposeAsync()
    {
        _inbox.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
        foreach (CancellationTokenSource request in _requests.Values)
            request.Cancel();
    }

    private async Task RunAsync()
    {
        await foreach (string message in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Dispatch(message);
            }
            catch (Exception exception)
            {
                // A malformed message must not stop the loop; the client sees a missing answer or a stale document.
                RequestFailed?.Invoke(exception);
            }
        }
        foreach (OpenDocument document in _documents.Values)
            document.Diagnostics?.Cancel();
    }

    private void Dispatch(string message)
    {
        using JsonDocument json = JsonDocument.Parse(message);
        JsonElement root = json.RootElement;
        // Responses to server requests have no method; the adapter sends no requests.
        if (
            !root.TryGetProperty("method", out JsonElement methodElement) || methodElement.GetString() is not { } method
        )
            return;
        JsonElement parameters = root.TryGetProperty("params", out JsonElement p) ? p : default;
        if (root.TryGetProperty("id", out JsonElement idElement))
            HandleRequest(idElement.GetRawText(), method, parameters);
        else
            HandleNotification(method, parameters);
    }

    private void HandleNotification(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "textDocument/didOpen":
            {
                TextDocumentItem item = Deserialize(
                    parameters,
                    LspJsonContext.Default.DidOpenTextDocumentParams
                ).TextDocument;
                var document = new OpenDocument(
                    item.LanguageId,
                    new DocumentVersion(item.Uri, item.Version, item.Text)
                );
                _documents[item.Uri] = document;
                ScheduleDiagnostics(document);
                break;
            }
            case "textDocument/didChange":
            {
                DidChangeTextDocumentParams change = Deserialize(
                    parameters,
                    LspJsonContext.Default.DidChangeTextDocumentParams
                );
                if (!_documents.TryGetValue(change.TextDocument.Uri, out OpenDocument? document))
                    return;
                document.Current = document.Current.WithChanges(change.ContentChanges, change.TextDocument.Version);
                ScheduleDiagnostics(document);
                break;
            }
            case "textDocument/didClose":
            {
                string uri = Deserialize(
                    parameters,
                    LspJsonContext.Default.DidCloseTextDocumentParams
                ).TextDocument.Uri;
                if (_documents.Remove(uri, out OpenDocument? document))
                    document.Diagnostics?.Cancel();
                break;
            }
            case "$/cancelRequest":
            {
                string id = Deserialize(parameters, LspJsonContext.Default.CancelParams).Id.GetRawText();
                if (_requests.TryGetValue(id, out CancellationTokenSource? request))
                    request.Cancel();
                break;
            }
            case "exit":
                _inbox.Writer.TryComplete();
                break;
        }
    }

    private void HandleRequest(string id, string method, JsonElement parameters)
    {
        long received = Stopwatch.GetTimestamp();
        switch (method)
        {
            case "initialize":
            {
                InitializeParams initialize = Deserialize(parameters, LspJsonContext.Default.InitializeParams);
                _clientSupportsItemDefaults =
                    initialize.Capabilities?.TextDocument?.Completion?.CompletionList?.ItemDefaults is { } defaults
                    && defaults.Contains("editRange")
                    && defaults.Contains("commitCharacters");
                Start(id, method, received, InitializeAsync, LspJsonContext.Default.InitializeResult);
                break;
            }
            case "shutdown":
                SendResponse(id, writer => writer.WriteNullValue());
                break;
            case "textDocument/completion":
            {
                (DocumentVersion document, Position position, CompletionContext? context) = Locate(parameters);
                bool itemDefaults = _clientSupportsItemDefaults;
                Start(
                    id,
                    method,
                    received,
                    token => CompleteAsync(document, position, context, itemDefaults, token),
                    LspJsonContext.Default.CompletionListResult
                );
                break;
            }
            case "completionItem/resolve":
            {
                LspCompletionItem item = Deserialize(parameters, LspJsonContext.Default.LspCompletionItem);
                Start(
                    id,
                    method,
                    received,
                    token => ResolveAsync(item, token),
                    LspJsonContext.Default.LspCompletionItem
                );
                break;
            }
            case "textDocument/hover":
            {
                (DocumentVersion document, Position position, _) = Locate(parameters);
                Start(
                    id,
                    method,
                    received,
                    token => HoverAsync(document, position, token),
                    LspJsonContext.Default.Hover
                );
                break;
            }
            case "textDocument/signatureHelp":
            {
                (DocumentVersion document, Position position, _) = Locate(parameters);
                Start(
                    id,
                    method,
                    received,
                    token => SignatureHelpAsync(document, position, token),
                    LspJsonContext.Default.SignatureHelpResult
                );
                break;
            }
            case "textDocument/semanticTokens/full":
            case "textDocument/semanticTokens/range":
            {
                SemanticTokensParams tokens = Deserialize(parameters, LspJsonContext.Default.SemanticTokensParams);
                DocumentVersion document = GetDocument(tokens.TextDocument.Uri);
                TextRange range = tokens.Range is { } r
                    ? TextRange.FromBounds(document.ToOffset(r.Start), document.ToOffset(r.End))
                    : new TextRange(0, document.Text.Length);
                Start(
                    id,
                    method,
                    received,
                    token => SemanticTokensAsync(document, range, token),
                    LspJsonContext.Default.SemanticTokens
                );
                break;
            }
            default:
                SendError(id, MethodNotFound, $"Unsupported method {method}");
                break;
        }
    }

    private async Task<InitializeResult?> InitializeAsync(CancellationToken cancellationToken)
    {
        ILanguageService service = await _service.WaitAsync(cancellationToken).ConfigureAwait(false);
        LanguageServiceCapabilities capabilities = service.Capabilities;
        string[] signatureTriggers = [.. capabilities.SignatureHelpTriggerCharacters.Select(c => c.ToString())];
        return new InitializeResult(
            new ServerCapabilities(
                "utf-16",
                new TextDocumentSyncOptions(OpenClose: true, Change: 2),
                new CompletionOptions(
                    [.. capabilities.CompletionTriggerCharacters.Select(c => c.ToString())],
                    ResolveProvider: true
                ),
                HoverProvider: true,
                new SignatureHelpOptions(signatureTriggers, signatureTriggers),
                new SemanticTokensOptions(new SemanticTokensLegend([.. TokenTypes], []), Range: true, Full: true)
            ),
            new ServerInfo("Darp.CodeEditor.Lsp")
        );
    }

    private async Task<CompletionListResult?> CompleteAsync(
        DocumentVersion document,
        Position position,
        CompletionContext? context,
        bool itemDefaults,
        CancellationToken cancellationToken
    )
    {
        ILanguageService service = await _service.WaitAsync(cancellationToken).ConfigureAwait(false);
        int offset = document.ToOffset(position);
        CompletionList? list = await service
            .GetCompletionsAsync(
                document.Snapshot,
                offset,
                ToTrigger(document.Text, offset, context),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (list is null)
            return null;

        Range editRange = document.ToRange(list.ApplicableRange);
        string uri = document.Snapshot.DocumentId;
        var items = new LspCompletionItem[list.Items.Count];
        for (int i = 0; i < items.Length; i++)
        {
            CompletionItem item = list.Items[i];
            items[i] = new LspCompletionItem(
                item.DisplayText,
                ToLspKind(item.Kind),
                item.InlineDescription,
                // The service already sorted the list; the client keeps that order among equally good matches.
                i.ToString("D5"),
                item.FilterText,
                itemDefaults ? item.InsertText : null,
                itemDefaults ? null : new TextEdit(editRange, item.InsertText),
                null,
                new CompletionItemData(uri, list.ResultId, i)
            );
        }
        CompletionItemDefaults? defaults = itemDefaults
            ? new CompletionItemDefaults(
                editRange,
                [.. service.Capabilities.CompletionCommitCharacters.Select(c => c.ToString())]
            )
            : null;
        return new CompletionListResult(false, defaults, items);
    }

    private async Task<LspCompletionItem?> ResolveAsync(LspCompletionItem item, CancellationToken cancellationToken)
    {
        ILanguageService service = await _service.WaitAsync(cancellationToken).ConfigureAwait(false);
        CompletionItemData data = item.Data;
        string? description = await service
            .GetCompletionDescriptionAsync(
                new CompletionItemReference(data.Uri, data.ResultId, data.Index),
                cancellationToken
            )
            .ConfigureAwait(false);
        return description is null ? item : item with { Documentation = MarkupContent.PlainText(description) };
    }

    private async Task<Hover?> HoverAsync(
        DocumentVersion document,
        Position position,
        CancellationToken cancellationToken
    )
    {
        ILanguageService service = await _service.WaitAsync(cancellationToken).ConfigureAwait(false);
        QuickInfo? info = await service
            .GetQuickInfoAsync(document.Snapshot, document.ToOffset(position), cancellationToken)
            .ConfigureAwait(false);
        return info is null ? null : new Hover(MarkupContent.PlainText(info.Text), document.ToRange(info.Range));
    }

    private async Task<SignatureHelpResult?> SignatureHelpAsync(
        DocumentVersion document,
        Position position,
        CancellationToken cancellationToken
    )
    {
        ILanguageService service = await _service.WaitAsync(cancellationToken).ConfigureAwait(false);
        SignatureHelp? help = await service
            .GetSignatureHelpAsync(document.Snapshot, document.ToOffset(position), cancellationToken)
            .ConfigureAwait(false);
        if (help is null)
            return null;
        return new SignatureHelpResult(
            [.. help.Signatures.Select(ToSignatureInformation)],
            help.ActiveSignature,
            help.ActiveParameter
        );
    }

    private async Task<SemanticTokens?> SemanticTokensAsync(
        DocumentVersion document,
        TextRange range,
        CancellationToken cancellationToken
    )
    {
        ILanguageService service = await _service.WaitAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ClassifiedRange> ranges = await service
            .GetClassificationsAsync(document.Snapshot, range, cancellationToken)
            .ConfigureAwait(false);
        // Relative encoding: line delta, start delta (relative to the previous token on the same line), length, type,
        // modifiers.
        var data = new List<int>(ranges.Count * 5);
        var previous = new Position(0, 0);
        foreach (ClassifiedRange classified in ranges.OrderBy(r => r.Range.Start))
        {
            Position start = document.ToPosition(classified.Range.Start);
            int lineDelta = start.Line - previous.Line;
            data.Add(lineDelta);
            data.Add(lineDelta == 0 ? start.Character - previous.Character : start.Character);
            data.Add(classified.Range.Length);
            data.Add((int)classified.Kind);
            data.Add(0);
            previous = start;
        }
        return new SemanticTokens([.. data]);
    }

    private void ScheduleDiagnostics(OpenDocument document)
    {
        document.Diagnostics?.Cancel();
        var cancellation = new CancellationTokenSource();
        document.Diagnostics = cancellation;
        _ = PublishDiagnosticsAsync(document.LanguageId, document.Current, cancellation.Token);
    }

    private async Task PublishDiagnosticsAsync(
        string languageId,
        DocumentVersion document,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Like the AvaloniaEdit editor: wait until typing pauses, and drop the result once the text changed.
            await Task.Delay(s_diagnosticsDelay, cancellationToken).ConfigureAwait(false);
            ILanguageService service = await _service.WaitAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<DiagnosticInfo> diagnostics = await service
                .GetDiagnosticsAsync(document.Snapshot, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var parameters = new PublishDiagnosticsParams(
                document.Snapshot.DocumentId,
                document.Version,
                [
                    .. diagnostics.Select(d => new LspDiagnostic(
                        document.ToRange(d.Range),
                        d.Severity switch
                        {
                            DiagnosticSeverity.Error => 1,
                            DiagnosticSeverity.Warning => 2,
                            _ => 3,
                        },
                        d.Id,
                        languageId,
                        d.Message
                    )),
                ]
            );
            SendNotification(
                "textDocument/publishDiagnostics",
                writer => JsonSerializer.Serialize(writer, parameters, LspJsonContext.Default.PublishDiagnosticsParams)
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            RequestFailed?.Invoke(exception);
        }
    }

    private (DocumentVersion Document, Position Position, CompletionContext? Context) Locate(JsonElement parameters)
    {
        TextDocumentPositionParams p = Deserialize(parameters, LspJsonContext.Default.TextDocumentPositionParams);
        return (GetDocument(p.TextDocument.Uri), p.Position, p.Context);
    }

    private DocumentVersion GetDocument(string uri) =>
        _documents.TryGetValue(uri, out OpenDocument? document)
            ? document.Current
            : throw new InvalidOperationException($"Document {uri} is not open.");

    private void Start<T>(
        string id,
        string method,
        long received,
        Func<CancellationToken, Task<T?>> handler,
        JsonTypeInfo<T> typeInfo
    )
        where T : class
    {
        // Not linked to anything and never disposed, so a late $/cancelRequest can always call Cancel.
        var cancellation = new CancellationTokenSource();
        _requests[id] = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                T? result = await handler(cancellation.Token).ConfigureAwait(false);
                SendResponse(
                    id,
                    writer =>
                    {
                        if (result is null)
                            writer.WriteNullValue();
                        else
                            JsonSerializer.Serialize(writer, result, typeInfo);
                    }
                );
                RequestHandled?.Invoke(method, Stopwatch.GetElapsedTime(received));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                SendError(id, RequestCancelled, "Request cancelled");
            }
            catch (Exception exception)
            {
                SendError(id, InternalError, exception.Message);
                RequestFailed?.Invoke(exception);
            }
            finally
            {
                _requests.TryRemove(id, out _);
            }
        });
    }

    private void SendResponse(string id, Action<Utf8JsonWriter> writeResult) =>
        Send(writer =>
        {
            writer.WritePropertyName("id");
            writer.WriteRawValue(id);
            writer.WritePropertyName("result");
            writeResult(writer);
        });

    private void SendError(string id, int code, string message) =>
        Send(writer =>
        {
            writer.WritePropertyName("id");
            writer.WriteRawValue(id);
            writer.WritePropertyName("error");
            JsonSerializer.Serialize(writer, new ResponseError(code, message), LspJsonContext.Default.ResponseError);
        });

    private void SendNotification(string method, Action<Utf8JsonWriter> writeParams) =>
        Send(writer =>
        {
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            writeParams(writer);
        });

    private void Send(Action<Utf8JsonWriter> writeBody)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writeBody(writer);
            writer.WriteEndObject();
        }
        _send(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static T Deserialize<T>(JsonElement element, JsonTypeInfo<T> typeInfo) =>
        element.Deserialize(typeInfo) ?? throw new JsonException($"Missing {typeof(T).Name}.");

    /// <remarks>
    /// LSP clients report typing and an explicit request both as "invoked". A word that starts right before the caret
    /// is treated as typing, which is what the AvaloniaEdit editor sends for the same keystroke.
    /// </remarks>
    private static CompletionTrigger ToTrigger(string text, int offset, CompletionContext? context)
    {
        if (context is { TriggerKind: 2, TriggerCharacter: [char character] })
            return new CompletionTrigger(CompletionTriggerKind.TriggerCharacter, character);
        if (
            offset > 0
            && (char.IsLetter(text[offset - 1]) || text[offset - 1] == '_')
            && (offset == 1 || !(char.IsLetterOrDigit(text[offset - 2]) || text[offset - 2] == '_'))
        )
            return new CompletionTrigger(CompletionTriggerKind.Insertion, text[offset - 1]);
        return new CompletionTrigger(CompletionTriggerKind.Invoke);
    }

    private static SignatureInformation ToSignatureInformation(SignatureInfo signature)
    {
        // Parameter labels are substrings of the signature label; LSP takes their offsets.
        int searchFrom = Math.Max(0, signature.Label.IndexOf('('));
        var parameters = new ParameterInformation[signature.Parameters.Count];
        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo parameter = signature.Parameters[i];
            int start = signature.Label.IndexOf(parameter.Label, searchFrom, StringComparison.Ordinal);
            int[] label = start < 0 ? [0, 0] : [start, start + parameter.Label.Length];
            searchFrom = Math.Max(searchFrom, label[1]);
            parameters[i] = new ParameterInformation(label, ToMarkup(parameter.Documentation));
        }
        return new SignatureInformation(signature.Label, ToMarkup(signature.Documentation), parameters);
    }

    private static MarkupContent? ToMarkup(string? text) => text is null ? null : MarkupContent.PlainText(text);

    private static int ToLspKind(CompletionItemKind kind) =>
        kind switch
        {
            CompletionItemKind.Method or CompletionItemKind.ExtensionMethod => 2,
            CompletionItemKind.Field => 5,
            CompletionItemKind.Local or CompletionItemKind.Parameter => 6,
            CompletionItemKind.Class or CompletionItemKind.Delegate => 7,
            CompletionItemKind.Interface => 8,
            CompletionItemKind.Namespace => 9,
            CompletionItemKind.Property => 10,
            CompletionItemKind.Enum => 13,
            CompletionItemKind.Keyword => 14,
            CompletionItemKind.Snippet => 15,
            CompletionItemKind.EnumMember => 20,
            CompletionItemKind.Constant => 21,
            CompletionItemKind.Struct => 22,
            CompletionItemKind.Event => 23,
            CompletionItemKind.TypeParameter => 25,
            _ => 1,
        };

    private sealed class OpenDocument(string languageId, DocumentVersion current)
    {
        public string LanguageId { get; } = languageId;
        public DocumentVersion Current { get; set; } = current;
        public CancellationTokenSource? Diagnostics { get; set; }
    }
}

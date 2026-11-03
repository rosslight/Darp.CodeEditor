using System.Text.Json;
using Darp.CodeEditor.Lsp.Protocol;

namespace Darp.CodeEditor.Lsp.Testing;

/// <summary>
/// A small language server on this library, for tests and the NativeAOT check: words are its symbols, and each
/// "error" in a document is a diagnostic.
/// </summary>
/// <remarks>
/// Hovering "slow" waits until the request is cancelled. Hovering "stale" waits until the document changes and then
/// answers <see cref="ErrorCodes.ContentModified"/>. After <c>initialized</c> the server asks the client for its
/// <c>greeting</c> setting with <c>workspace/configuration</c>, and hovers greet with it.
/// </remarks>
internal sealed class ToyServer
{
    public static readonly RequestType<JsonElement, JsonElement> Configuration = new(
        "workspace/configuration",
        LspJsonContext.Default.JsonElement,
        LspJsonContext.Default.JsonElement
    );

    public static readonly SemanticTokensLegend Legend = new(["word", "number"], ["long"]);

    private readonly Dictionary<string, ToyDocument> _documents = new(StringComparer.Ordinal);
    private bool _insertReplace;
    private string _greeting = "Hello";

    public ToyServer(Stream input, Stream output, Action<string>? log = null)
    {
        Server = new LanguageServer(input, output, InitializeAsync, log: log);
        JsonRpcConnection rpc = Server.Connection;
        rpc.OnNotification(LspMethods.Initialized, OnInitializedAsync);
        rpc.OnNotification(LspMethods.DidOpen, OnOpenAsync);
        rpc.OnNotification(LspMethods.DidChange, OnChangeAsync);
        rpc.OnNotification(LspMethods.DidClose, OnCloseAsync);
        rpc.OnRequest(LspMethods.Completion, OnCompletion);
        rpc.OnRequest(LspMethods.CompletionResolve, OnResolve);
        rpc.OnRequest(LspMethods.Hover, OnHoverAsync);
        rpc.OnRequest(LspMethods.SignatureHelp, OnSignatureHelp);
        rpc.OnRequest(LspMethods.SemanticTokensRange, OnSemanticTokens);
    }

    public LanguageServer Server { get; }

    private Task<InitializeResult> InitializeAsync(InitializeParams parameters, CancellationToken cancellationToken)
    {
        if (
            parameters.InitializationOptions is { ValueKind: JsonValueKind.Object } options
            && options.TryGetProperty("fail", out _)
        )
            throw new JsonRpcException(ErrorCodes.RequestFailed, "Initialization failed as asked.");
        _insertReplace = parameters.Capabilities.TextDocument?.Completion?.CompletionItem?.InsertReplaceSupport == true;
        return Task.FromResult(
            new InitializeResult(
                new ServerCapabilities
                {
                    PositionEncoding = "utf-16",
                    TextDocumentSync = new TextDocumentSyncOptions
                    {
                        OpenClose = true,
                        Change = TextDocumentSyncKind.Incremental,
                    },
                    CompletionProvider = new CompletionOptions { TriggerCharacters = ["."], ResolveProvider = true },
                    HoverProvider = true,
                    SignatureHelpProvider = new SignatureHelpOptions { TriggerCharacters = ["(", ","] },
                    SemanticTokensProvider = new SemanticTokensOptions(Legend, Range: true),
                },
                new ServerInfo("toy", "1.0")
            )
        );
    }

    private async Task OnInitializedAsync(InitializedParams parameters, CancellationToken cancellationToken)
    {
        JsonElement settings = await Server
            .Connection.SendRequestAsync(Configuration, ToJson("""{"items":[{"section":"toy"}]}"""), cancellationToken)
            .ConfigureAwait(false);
        if (
            settings.ValueKind == JsonValueKind.Array
            && settings.GetArrayLength() > 0
            && settings[0] is { ValueKind: JsonValueKind.Object } toy
            && toy.TryGetProperty("greeting", out JsonElement greeting)
        )
            _greeting = greeting.GetString() ?? _greeting;
    }

    private async Task OnOpenAsync(DidOpenTextDocumentParams parameters, CancellationToken cancellationToken)
    {
        TextDocumentItem item = parameters.TextDocument;
        var document = new ToyDocument(item.Uri, item.Version, item.Text);
        _documents[item.Uri] = document;
        await Server
            .Connection.SendNotificationAsync(
                LspMethods.LogMessage,
                new LogMessageParams(MessageType.Info, $"Opened {item.Uri}")
            )
            .ConfigureAwait(false);
        await PublishDiagnosticsAsync(document).ConfigureAwait(false);
    }

    private async Task OnChangeAsync(DidChangeTextDocumentParams parameters, CancellationToken cancellationToken)
    {
        if (!_documents.TryGetValue(parameters.TextDocument.Uri, out ToyDocument? document))
            return;
        string text = document.Text;
        foreach (TextDocumentContentChangeEvent change in parameters.ContentChanges)
        {
            text = change.Range is { } range
                ? string.Concat(
                    text.AsSpan(0, ToOffset(text, range.Start)),
                    change.Text,
                    text.AsSpan(ToOffset(text, range.End))
                )
                : change.Text;
        }
        document.Update(parameters.TextDocument.Version, text);
        await PublishDiagnosticsAsync(document).ConfigureAwait(false);
    }

    private Task OnCloseAsync(DidCloseTextDocumentParams parameters, CancellationToken cancellationToken)
    {
        _documents.Remove(parameters.TextDocument.Uri);
        return Server.Connection.SendNotificationAsync(
            LspMethods.PublishDiagnostics,
            new PublishDiagnosticsParams(parameters.TextDocument.Uri, [])
        );
    }

    private Task<CompletionList?> OnCompletion(CompletionParams parameters, CancellationToken cancellationToken)
    {
        if (!_documents.TryGetValue(parameters.TextDocument.Uri, out ToyDocument? document))
            return Task.FromResult<CompletionList?>(null);
        string text = document.Text;
        (int start, int end) = WordAt(text, ToOffset(text, parameters.Position));
        var insert = new DocumentRange(ToPosition(text, start), parameters.Position);
        var replace = new DocumentRange(ToPosition(text, start), ToPosition(text, end));
        CompletionTextEdit Edit(string newText) =>
            _insertReplace
                ? new CompletionTextEdit(newText, insert, replace)
                : new CompletionTextEdit(newText, replace);
        return Task.FromResult<CompletionList?>(
            new CompletionList(
                [
                    new CompletionItem("alpha")
                    {
                        Kind = CompletionItemKind.Variable,
                        TextEdit = Edit("alpha"),
                        Documentation = new MarkupContent(MarkupKind.PlainText, "The first letter."),
                        CommitCharacters = ["."],
                        Data = ToJson("""{"id":1,"nested":[true,null]}"""),
                    },
                    new CompletionItem("beta")
                    {
                        Kind = CompletionItemKind.Function,
                        TextEdit = Edit("beta()"),
                        AdditionalTextEdits = [new TextEdit(new DocumentRange(default, default), "-- uses beta\n")],
                        Data = ToJson("""{"id":2}"""),
                    },
                ],
                IsIncomplete: true
            )
        );
    }

    private Task<CompletionItem?> OnResolve(CompletionItem item, CancellationToken cancellationToken) =>
        Task.FromResult<CompletionItem?>(
            item with
            {
                Detail = $"resolved {item.Data?.GetProperty("id")}",
                Documentation = new MarkupContent(MarkupKind.Markdown, $"**{item.Label}**"),
            }
        );

    private async Task<Hover?> OnHoverAsync(HoverParams parameters, CancellationToken cancellationToken)
    {
        // Reads the document before the first await, so it sees exactly the changes that came before the request.
        if (!_documents.TryGetValue(parameters.TextDocument.Uri, out ToyDocument? document))
            return null;
        string text = document.Text;
        (int start, int end) = WordAt(text, ToOffset(text, parameters.Position));
        string word = text[start..end];
        CancellationToken changed = document.Changed;
        if (word == "slow")
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        if (word == "stale")
        {
            using var stale = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, changed);
            try
            {
                await Task.Delay(Timeout.Infinite, stale.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (changed.IsCancellationRequested)
            {
                throw new JsonRpcException(ErrorCodes.ContentModified, "The document changed.");
            }
        }
        return new Hover(
            new MarkupContent(MarkupKind.Markdown, $"{_greeting}, **{word}**"),
            new DocumentRange(ToPosition(text, start), ToPosition(text, end))
        );
    }

    private Task<SignatureHelp?> OnSignatureHelp(SignatureHelpParams parameters, CancellationToken cancellationToken)
    {
        if (!_documents.TryGetValue(parameters.TextDocument.Uri, out ToyDocument? document))
            return Task.FromResult<SignatureHelp?>(null);
        string text = document.Text;
        int offset = ToOffset(text, parameters.Position);
        int open = text.LastIndexOf('(', Math.Max(offset - 1, 0));
        int activeParameter = open < 0 ? 0 : text.AsSpan(open, offset - open).Count(',');
        const string label = "add(int a, int b)";
        return Task.FromResult<SignatureHelp?>(
            new SignatureHelp(
                [
                    new SignatureInformation(label)
                    {
                        Documentation = new MarkupContent(MarkupKind.PlainText, "Adds two numbers."),
                        Parameters =
                        [
                            new ParameterInformation(new ParameterLabel(4, 9)),
                            new ParameterInformation(new ParameterLabel(11, 16)),
                        ],
                    },
                ],
                ActiveSignature: 0,
                ActiveParameter: activeParameter
            )
        );
    }

    /// <summary>Classifies words as <c>word</c> or <c>number</c>; words longer than five characters are <c>long</c>.</summary>
    private Task<SemanticTokens?> OnSemanticTokens(
        SemanticTokensRangeParams parameters,
        CancellationToken cancellationToken
    )
    {
        if (!_documents.TryGetValue(parameters.TextDocument.Uri, out ToyDocument? document))
            return Task.FromResult<SemanticTokens?>(null);
        string text = document.Text;
        int end = ToOffset(text, parameters.Range.End);
        var data = new List<int>();
        Position previous = default;
        for (int i = ToOffset(text, parameters.Range.Start); i < end; i++)
        {
            if (!char.IsLetterOrDigit(text[i]) || (i > 0 && char.IsLetterOrDigit(text[i - 1])))
                continue;
            (_, int wordEnd) = WordAt(text, i);
            Position position = ToPosition(text, i);
            int length = wordEnd - i;
            data.AddRange([
                position.Line - previous.Line,
                position.Line == previous.Line ? position.Character - previous.Character : position.Character,
                length,
                char.IsDigit(text[i]) ? 1 : 0,
                length > 5 ? 1 : 0,
            ]);
            previous = position;
        }
        return Task.FromResult<SemanticTokens?>(new SemanticTokens([.. data]));
    }

    private Task PublishDiagnosticsAsync(ToyDocument document)
    {
        var diagnostics = new List<Diagnostic>();
        for (
            int i = document.Text.IndexOf("error", StringComparison.Ordinal);
            i >= 0;
            i = document.Text.IndexOf("error", i + 1, StringComparison.Ordinal)
        )
        {
            diagnostics.Add(
                new Diagnostic(
                    new DocumentRange(ToPosition(document.Text, i), ToPosition(document.Text, i + 5)),
                    "Found an error.",
                    DiagnosticSeverity.Error,
                    Code: "TOY1",
                    Source: "toy"
                )
            );
        }
        return Server.Connection.SendNotificationAsync(
            LspMethods.PublishDiagnostics,
            new PublishDiagnosticsParams(document.Uri, diagnostics, document.Version)
        );
    }

    public static JsonElement ToJson(string json) =>
        JsonSerializer.Deserialize(json, LspJsonContext.Default.JsonElement);

    /// <summary>The offset of <paramref name="position"/>, whose character counts UTF-16 code units like C# strings.</summary>
    public static int ToOffset(string text, Position position)
    {
        int lineStart = 0;
        for (int line = 0; line < position.Line; line++)
        {
            int next = text.IndexOf('\n', lineStart);
            if (next < 0)
                return text.Length;
            lineStart = next + 1;
        }
        return Math.Min(lineStart + position.Character, text.Length);
    }

    public static Position ToPosition(string text, int offset)
    {
        int lineStart = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        return new Position(text.AsSpan(0, lineStart).Count('\n'), offset - lineStart);
    }

    private static (int Start, int End) WordAt(string text, int offset)
    {
        int start = offset;
        while (start > 0 && char.IsLetterOrDigit(text[start - 1]))
            start--;
        int end = offset;
        while (end < text.Length && char.IsLetterOrDigit(text[end]))
            end++;
        return (start, end);
    }

    private sealed class ToyDocument
    {
        private CancellationTokenSource _changed = new();

        public ToyDocument(string uri, int version, string text)
        {
            Uri = uri;
            Version = version;
            Text = text;
        }

        public string Uri { get; }

        public int Version { get; private set; }

        public string Text { get; private set; }

        /// <summary>Cancelled when the document changes.</summary>
        public CancellationToken Changed => _changed.Token;

        public void Update(int version, string text)
        {
            Version = version;
            Text = text;
            CancellationTokenSource previous = _changed;
            _changed = new CancellationTokenSource();
            previous.Cancel();
        }
    }
}

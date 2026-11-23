using System.Text.Json.Nodes;
using AvaloniaEdit.Document;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.LanguageClient;

/// <summary>
/// What ties a <see cref="CodeDocument"/> to the language server that serves it: open there from creation until
/// <see cref="CodeDocument.Dispose"/>. Each change reaches the server as an incremental change, captured on the thread
/// that changes the text with work that grows with the change, not with the document.
/// </summary>
internal sealed class ServerDocument
{
    private readonly LanguageServer _server;

    /// <summary>
    /// The parts of the change in progress, each in positions of the text before it. Only the thread that changes the
    /// text uses it.
    /// </summary>
    private List<TextDocumentContentChangeEvent> _changes = [];

    public ServerDocument(LanguageServer server, CodeDocument owner, string uri, JsonNode? data)
    {
        _server = server;
        Owner = owner;
        Uri = uri;
        Data = data;
    }

    /// <summary>The document that the server serves.</summary>
    public CodeDocument Owner { get; }

    /// <summary>What the server needs beyond the text, in its own format: goes with each <c>didOpen</c>.</summary>
    public JsonNode? Data { get; }

    /// <summary>The document's URI in its server: from its file path, or untitled. Guarded by the server's lock.</summary>
    public string Uri { get; set; }

    public void OnChanging(TextDocument buffer, DocumentChangeEventArgs change)
    {
        int start = change.Offset;
        int end = change.Offset + change.RemovalLength;
        string text = change.InsertedText.Text;
        // An offset between \r and \n has no line and column: the change takes the whole line break.
        if (IsInsideLineBreak(buffer, start))
        {
            start--;
            text = "\r" + text;
        }
        if (IsInsideLineBreak(buffer, end))
        {
            end++;
            text += "\n";
        }
        _changes.Add(
            new TextDocumentContentChangeEvent(
                new LspRange(Positions.ToLsp(buffer.GetLocation(start)), Positions.ToLsp(buffer.GetLocation(end))),
                text
            )
        );
    }

    public void OnChanged(TextSnapshot snapshot)
    {
        List<TextDocumentContentChangeEvent> changes = _changes;
        _changes = [];
        _server.Changed(this, snapshot, changes);
    }

    public void OnFilePathChanging(string? path) => _server.Move(this, path);

    public Task<HoverInfo?> HoverAsync(
        TextSnapshot snapshot,
        TextPosition position,
        CancellationToken cancellationToken
    ) => _server.HoverAsync(this, snapshot, position, cancellationToken);

    public Task<SemanticTokenLines?> SemanticTokensAsync(
        TextPosition start,
        TextPosition end,
        int lineCount,
        CancellationToken cancellationToken
    ) => _server.SemanticTokensAsync(this, start, end, lineCount, cancellationToken);

    public Task<CompletionList?> CompletionAsync(
        TextSnapshot snapshot,
        TextRange word,
        CompletionTrigger trigger,
        CancellationToken cancellationToken
    ) => _server.CompletionAsync(this, snapshot, word, trigger, cancellationToken);

    public Task<SignatureHelpInfo?> SignatureHelpAsync(
        TextSnapshot snapshot,
        int offset,
        SignatureHelpTrigger trigger,
        CancellationToken cancellationToken
    ) => _server.SignatureHelpAsync(this, snapshot, offset, trigger, cancellationToken);

    public Task<IReadOnlyList<Occurrence>?> DocumentHighlightsAsync(
        TextSnapshot snapshot,
        int offset,
        CancellationToken cancellationToken
    ) => _server.DocumentHighlightsAsync(this, snapshot, offset, cancellationToken);

    public ServerFeatures? Features => _server.FeaturesOf(this);

    public TimeProvider TimeProvider => _server.TimeProvider;

    public void OnDisposed() => _server.Close(this);

    private static bool IsInsideLineBreak(TextDocument buffer, int offset) =>
        offset > 0
        && offset < buffer.TextLength
        && buffer.GetCharAt(offset - 1) == '\r'
        && buffer.GetCharAt(offset) == '\n';
}

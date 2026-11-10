using AvaloniaEdit.Document;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.LanguageClient;

/// <summary>
/// A document from <see cref="LanguageServer.CreateDocument"/>: open in its server from creation until
/// <see cref="CodeDocument.Dispose"/>. Each change reaches the server as an incremental change, captured on the thread
/// that changes the text with work that grows with the change, not with the document.
/// </summary>
internal sealed class ServerDocument : CodeDocument
{
    private readonly LanguageServer _server;

    /// <summary>
    /// The parts of the change in progress, each in positions of the text before it. Only the thread that changes the
    /// text uses it.
    /// </summary>
    private List<TextDocumentContentChangeEvent> _changes = [];

    public ServerDocument(LanguageServer server, CodeLanguage language, string text, string? filePath, string uri)
        : base(language, text, filePath)
    {
        _server = server;
        Uri = uri;
    }

    /// <summary>The document's URI in its server: from its file path, or untitled. Guarded by the server's lock.</summary>
    public string Uri { get; set; }

    private protected override void OnChanging(TextDocument buffer, DocumentChangeEventArgs change)
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

    private protected override void OnChanged(TextSnapshot snapshot)
    {
        List<TextDocumentContentChangeEvent> changes = _changes;
        _changes = [];
        _server.Changed(this, snapshot, changes);
    }

    private protected override void OnFilePathChanging(string? path) => _server.Move(this, path);

    private protected override void OnDisposed() => _server.Close(this);

    private static bool IsInsideLineBreak(TextDocument buffer, int offset) =>
        offset > 0
        && offset < buffer.TextLength
        && buffer.GetCharAt(offset - 1) == '\r'
        && buffer.GetCharAt(offset) == '\n';
}

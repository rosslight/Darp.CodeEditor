using Darp.CodeEditor.LanguageServices;

namespace Darp.CodeEditor.Lsp;

/// <summary>One immutable version of an open document, with the line starts to convert LSP positions.</summary>
internal sealed class DocumentVersion
{
    private readonly int[] _lineStarts;

    public DocumentVersion(string uri, int version, string text)
    {
        Snapshot = new DocumentSnapshot(uri, version, text);
        _lineStarts = ComputeLineStarts(text);
    }

    public DocumentSnapshot Snapshot { get; }

    public string Text => Snapshot.Text;

    public int Version => Snapshot.Version;

    /// <summary>Applies changes in order; each range refers to the text after the previous change.</summary>
    public DocumentVersion WithChanges(IReadOnlyList<TextDocumentContentChangeEvent> changes, int version)
    {
        DocumentVersion current = this;
        foreach (TextDocumentContentChangeEvent change in changes)
        {
            string text = change.Range is { } range
                ? string.Concat(
                    current.Text.AsSpan(0, current.ToOffset(range.Start)),
                    change.Text,
                    current.Text.AsSpan(current.ToOffset(range.End))
                )
                : change.Text;
            current = new DocumentVersion(Snapshot.DocumentId, version, text);
        }
        return current;
    }

    public int ToOffset(Position position)
    {
        if (position.Line >= _lineStarts.Length)
            return Text.Length;
        int lineStart = _lineStarts[position.Line];
        int lineEnd = position.Line + 1 < _lineStarts.Length ? _lineStarts[position.Line + 1] : Text.Length;
        return Math.Min(lineStart + position.Character, lineEnd);
    }

    public Position ToPosition(int offset)
    {
        int line = Array.BinarySearch(_lineStarts, offset);
        if (line < 0)
            line = ~line - 1;
        return new Position(line, offset - _lineStarts[line]);
    }

    public Range ToRange(TextRange range) => new(ToPosition(range.Start), ToPosition(range.End));

    private static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int>(text.Length / 32 + 1) { 0 };
        for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
            starts.Add(i + 1);
        return [.. starts];
    }
}

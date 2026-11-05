using AvaloniaEdit.Document;

namespace Darp.CodeEditor;

/// <summary>
/// The text of a <see cref="CodeDocument"/> at one version, or a string that was compiled without a document.
/// Immutable, and safe to read from any thread.
/// </summary>
/// <remarks>
/// The document makes a snapshot after every change, sharing the parts of the text that did not change, so taking one
/// costs nothing. Holding a snapshot keeps its text alive. Positions that the library reports come with the snapshot
/// they refer to, such as <see cref="Diagnostic.Snapshot"/>.
/// </remarks>
public sealed class TextSnapshot
{
    /// <summary>A document's snapshot is a rope that shares its nodes with the document; a compiled string is a string.</summary>
    private readonly ITextSource _text;

    /// <summary>
    /// The offset where each line starts, built on first use: ropes have no line index. Threads that race build the
    /// same index, and either one is kept.
    /// </summary>
    private int[]? _lineStarts;

    internal TextSnapshot(ITextSource text, int version)
    {
        _text = text;
        Version = version;
    }

    /// <summary>A snapshot of a string that was compiled without a document.</summary>
    internal static TextSnapshot FromString(string text) => new(new StringTextSource(text), 0);

    /// <summary>
    /// Counts the document's changes: each change of the text makes a snapshot with a higher version. 0 for a compiled
    /// string.
    /// </summary>
    public int Version { get; }

    /// <summary>The length of the text, in UTF-16 code units.</summary>
    public int Length => _text.TextLength;

    /// <summary>The document's version in AvaloniaEdit, to follow its changes; null for a compiled string.</summary>
    internal ITextSourceVersion? SourceVersion => _text.Version;

    /// <summary>The text. For a document's snapshot, each call copies it into a new string.</summary>
    public string GetText() => _text.Text;

    /// <summary>The text of <paramref name="range"/>, clamped to the text.</summary>
    public string GetText(TextRange range)
    {
        (int start, int length) = Clamp(range);
        return _text.GetText(start, length);
    }

    /// <summary>The line and column of <paramref name="offset"/>, clamped to the text.</summary>
    public TextPosition GetPosition(int offset)
    {
        int[] lineStarts = _lineStarts ??= ReadLineStarts();
        offset = Math.Clamp(offset, 0, Length);
        int line = Array.BinarySearch(lineStarts, offset);
        if (line < 0)
            line = ~line - 1;
        return new TextPosition(line + 1, offset - lineStarts[line] + 1);
    }

    /// <summary>The offset of <paramref name="position"/>, clamped to the end of its line and to the last line.</summary>
    public int GetOffset(TextPosition position)
    {
        int[] lineStarts = _lineStarts ??= ReadLineStarts();
        int line = Math.Clamp(position.Line, 1, lineStarts.Length) - 1;
        int start = lineStarts[line];
        int end = line + 1 < lineStarts.Length ? LineEnd(lineStarts[line + 1]) : Length;
        return start + Math.Clamp(position.Column, 1, end - start + 1) - 1;
    }

    /// <summary>The start and length of <paramref name="range"/> within the text.</summary>
    internal (int Start, int Length) Clamp(TextRange range)
    {
        int start = Math.Clamp(range.Start, 0, Length);
        // In long, so that a range to int.MaxValue does not overflow.
        int end = (int)Math.Clamp((long)range.Start + range.Length, start, Length);
        return (start, end - start);
    }

    /// <summary>Where the line break before <paramref name="nextLineStart"/> starts.</summary>
    private int LineEnd(int nextLineStart) =>
        nextLineStart >= 2 && _text.GetCharAt(nextLineStart - 1) == '\n' && _text.GetCharAt(nextLineStart - 2) == '\r'
            ? nextLineStart - 2
            : nextLineStart - 1;

    /// <summary>Scans the text once for line breaks: <c>\r\n</c>, <c>\n</c> and <c>\r</c>.</summary>
    private int[] ReadLineStarts()
    {
        var starts = new List<int> { 0 };
        using TextReader reader = _text.CreateReader();
        Span<char> buffer = stackalloc char[4096];
        int offset = 0;
        // A \r at the end of the previous chunk: a \n at the start of this one belongs to its line break.
        bool afterCarriageReturn = false;
        int read;
        while ((read = reader.Read(buffer)) > 0)
        {
            ReadOnlySpan<char> chunk = buffer[..read];
            int i = 0;
            if (afterCarriageReturn && chunk[0] == '\n')
            {
                starts[^1]++;
                i = 1;
            }
            afterCarriageReturn = false;
            while (chunk[i..].IndexOfAny('\r', '\n') is var found and >= 0)
            {
                i += found;
                if (chunk[i] == '\r' && i + 1 < chunk.Length && chunk[i + 1] == '\n')
                    i++;
                else if (chunk[i] == '\r' && i + 1 == chunk.Length)
                    afterCarriageReturn = true;
                i++;
                starts.Add(offset + i);
            }
            offset += read;
        }
        return [.. starts];
    }
}

using Darp.CodeEditor.Lsp.Protocol;

namespace Darp.CodeEditor.LanguageServices;

/// <summary>
/// One version of a document's text with its line starts, to convert between offsets and LSP positions. Immutable, so
/// requests and their results use the version they were made for.
/// </summary>
/// <remarks>LSP characters count UTF-16 code units, like offsets into a C# string.</remarks>
internal sealed class DocumentText
{
    private readonly int[] _lineStarts;

    public DocumentText(int version, string text)
    {
        Version = version;
        Text = text;
        _lineStarts = FindLineStarts(text);
    }

    public int Version { get; }

    public string Text { get; }

    /// <summary>Applies a change of the previous version.</summary>
    public DocumentText Apply(int version, TextChange change)
    {
        int offset = Math.Clamp(change.Offset, 0, Text.Length);
        int end = Math.Clamp(change.Offset + change.RemovedLength, offset, Text.Length);
        return new DocumentText(version, string.Concat(Text.AsSpan(0, offset), change.InsertedText, Text.AsSpan(end)));
    }

    public Position GetPosition(int offset)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        int line = Array.BinarySearch(_lineStarts, offset);
        if (line < 0)
            line = ~line - 1;
        return new Position(line, offset - _lineStarts[line]);
    }

    /// <summary>The offset of a position; positions past the end of a line or the text are clamped to it.</summary>
    public int GetOffset(Position position)
    {
        if (position.Line < 0)
            return 0;
        if (position.Line >= _lineStarts.Length)
            return Text.Length;
        int start = _lineStarts[position.Line];
        // A line ends before its line break, also before the \r of \r\n.
        int end = position.Line + 1 < _lineStarts.Length ? _lineStarts[position.Line + 1] - 1 : Text.Length;
        if (end > start && Text[end - 1] == '\r')
            end--;
        return Math.Min(start + Math.Max(0, position.Character), end);
    }

    public DocumentRange GetRange(TextRange range) => new(GetPosition(range.Start), GetPosition(range.End));

    public TextRange GetTextRange(DocumentRange range)
    {
        int start = GetOffset(range.Start);
        return TextRange.FromBounds(start, Math.Max(start, GetOffset(range.End)));
    }

    /// <summary>The identifier characters around <paramref name="offset"/>, for servers that give no range.</summary>
    public TextRange GetWordRange(int offset)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        int start = offset;
        while (start > 0 && IsWordPart(Text[start - 1]))
            start--;
        int end = offset;
        while (end < Text.Length && IsWordPart(Text[end]))
            end++;
        return TextRange.FromBounds(start, end);
    }

    /// <summary>
    /// The argument list that <paramref name="offset"/> is in: from after its unclosed '(' up to the offset. Brackets
    /// in strings and comments are not told apart, which is good enough to place signature help.
    /// </summary>
    public TextRange GetArgumentListRange(int offset)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        int depth = 0;
        for (int i = offset - 1; i >= 0; i--)
        {
            switch (Text[i])
            {
                case ')' or ']' or '}':
                    depth++;
                    break;
                case '(' or '[' or '{' when depth > 0:
                    depth--;
                    break;
                case '(':
                    return TextRange.FromBounds(i + 1, offset);
                case '[' or '{' or ';':
                    return new TextRange(offset, 0);
            }
        }
        return new TextRange(offset, 0);
    }

    private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int[] FindLineStarts(string text)
    {
        var starts = new List<int>(text.Length / 32 + 1) { 0 };
        for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
            starts.Add(i + 1);
        return [.. starts];
    }
}

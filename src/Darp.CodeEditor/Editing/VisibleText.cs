using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The part of each line that the editor works on: its first <see cref="LineLimit"/> characters. From its
/// release after 12.0.0, AvaloniaEdit collapses the rest of a longer line into "...", which shows none of it (design.md
/// #127). The editor's views work on this part only, so that their work per line is bounded whatever the line's length
/// or what lies in its hidden tail.
/// </summary>
internal static class VisibleText
{
    /// <summary>How many characters of a line the editor works on, AvaloniaEdit's <c>VisualLine.LENGTH_LIMIT</c>.</summary>
    public const int LineLimit = 3_000;

    /// <summary>Where the part of <paramref name="line"/> that the editor works on ends: at its end, or where its hidden tail starts.</summary>
    public static int End(DocumentLine line) => line.Offset + Math.Min(line.Length, LineLimit);

    /// <summary>Whether <paramref name="line"/> is longer than the part the editor works on.</summary>
    public static bool IsCollapsed(DocumentLine line) => line.Length > LineLimit;

    /// <summary>Whether <paramref name="offset"/> is in the hidden tail of <paramref name="line"/>.</summary>
    public static bool IsInTail(DocumentLine line, int offset) => IsCollapsed(line) && offset >= End(line);

    /// <summary>
    /// Whether text can go in at <paramref name="offset"/>: not in read-only text, nor in a long line's hidden tail,
    /// which the editor's own edits stay out of.
    /// </summary>
    public static bool CanInsert(TextArea textArea, int offset)
    {
        DocumentLine line = textArea.Document.GetLineByOffset(offset);
        return textArea.ReadOnlySectionProvider.CanInsert(offset) && !IsInTail(line, offset);
    }

    /// <summary>
    /// Whether the text from <paramref name="offset"/> on, <paramref name="length"/> long, can go: none of it read-only,
    /// in a long line's hidden tail or in the line break after it.
    /// </summary>
    public static bool CanDelete(TextArea textArea, int offset, int length)
    {
        var segment = new TextSegment { StartOffset = offset, Length = length };
        return textArea.ReadOnlySectionProvider.GetDeletableSegments(segment).Sum(deletable => deletable.Length)
                == length
            && !TouchesTail(textArea.Document, offset, length);
    }

    /// <summary>
    /// Whether the text from <paramref name="offset"/> on, <paramref name="length"/> long, starts in a long line's
    /// hidden tail, or reaches into one or the line break after it.
    /// </summary>
    public static bool TouchesTail(TextDocument document, int offset, int length)
    {
        DocumentLine? line = document.GetLineByOffset(offset);
        if (IsInTail(line, offset))
            return true;
        for (; line?.Offset < offset + length; line = line.NextLine)
        {
            if (IsCollapsed(line) && End(line) < offset + length && line.Offset + line.TotalLength > offset)
                return true;
        }
        return false;
    }
}

using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The part of each line that the editor shows and edits: its first <see cref="LineLimit"/> characters. AvaloniaEdit
/// collapses the rest of a longer line into "...", which shows none of it. The editor's views work on this part only,
/// so that their work per line is bounded whatever the line's length or what lies in its hidden tail.
/// </summary>
internal static class VisibleText
{
    /// <summary>How many characters of a line the editor shows and edits, AvaloniaEdit's default.</summary>
    public const int LineLimit = 3_000;

    /// <summary>Where the shown part of <paramref name="line"/> ends: at its end, or where its hidden tail starts.</summary>
    public static int End(DocumentLine line) => line.Offset + Math.Min(line.Length, LineLimit);

    /// <summary>Whether <paramref name="line"/> is longer than the editor shows.</summary>
    public static bool IsCollapsed(DocumentLine line) => line.Length > LineLimit;

    /// <summary>
    /// Whether text can go in at <paramref name="offset"/>: not in read-only text, nor in a long line's hidden tail. It
    /// is AvaloniaEdit's check before a paste, <c>TextArea.CanInsert</c>, which is internal.
    /// </summary>
    public static bool CanInsert(TextArea textArea, int offset)
    {
        DocumentLine line = textArea.Document.GetLineByOffset(offset);
        return textArea.ReadOnlySectionProvider.CanInsert(offset) && (!IsCollapsed(line) || offset < End(line));
    }
}

using AvaloniaEdit.Document;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Tab, Shift+Tab and Backspace in indentation by the tab stops of a document's indentation, as VS Code's <c>tab</c>,
/// <c>outdent</c> and <c>deleteLeft</c> with its default <c>editor.useTabStops</c>: from the text and the selection to
/// the edits and the selection after them.
/// </summary>
internal static class TabStops
{
    /// <summary>
    /// Tab: without a selection on a line of whitespace only, the line takes the indentation that
    /// <paramref name="goodIndentation"/> gives it by its number, or one level, unless it starts with it already.
    /// Otherwise, with a selection inside one line, short of the whole line, or without one, whitespace up to the next
    /// tab stop from its start replaces it; whole lines, one or more, go to their next tab stop (<see cref="Shift"/>).
    /// </summary>
    public static TabEdit Tab(
        TextDocument text,
        TextSelection selection,
        Indentation indentation,
        Func<int, string?> goodIndentation
    )
    {
        TextRange range = selection.Range;
        DocumentLine line = text.GetLineByOffset(range.Start);
        string lineText = text.GetText(line);
        if (range.Length == 0 && IndentRules.Leading(lineText) == lineText.Length)
        {
            string good = new Indenting(indentation).Normalize(goodIndentation(line.LineNumber) ?? "\t");
            if (!lineText.StartsWith(good, StringComparison.Ordinal))
                return new TabEdit(
                    [new TextEdit(line.Offset, line.Length, good)],
                    new TextSelection(line.Offset + good.Length)
                );
        }
        if (
            range.Length > 0
            && (range.End > line.EndOffset || (range.Start == line.Offset && range.End == line.EndOffset))
        )
            return Shift(text, selection, indentation, outdent: false);
        int columns = new Indenting(indentation).Width(text.GetText(line.Offset, range.Start - line.Offset));
        string whitespace = indentation.UseTabs ? "\t" : new string(' ', indentation.Size - columns % indentation.Size);
        return new TabEdit(
            [new TextEdit(range.Start, range.Length, whitespace)],
            new TextSelection(range.Start + whitespace.Length)
        );
    }

    /// <summary>Ctrl+]: the selected lines, or the caret's, go to their next tab stop (<see cref="Shift"/>).</summary>
    public static TabEdit Indent(TextDocument text, TextSelection selection, Indentation indentation) =>
        Shift(text, selection, indentation, outdent: false);

    /// <summary>Shift+Tab: the selected lines, or the caret's, go to their previous tab stop (<see cref="Shift"/>).</summary>
    public static TabEdit Outdent(TextDocument text, TextSelection selection, Indentation indentation) =>
        Shift(text, selection, indentation, outdent: true);

    /// <summary>
    /// What Backspace deletes at <paramref name="caret"/> in its line's indentation: back to the tab stop before the
    /// caret, from inside the indentation or right after it, or anywhere on a line of whitespace only. Null elsewhere,
    /// and at the line's start.
    /// </summary>
    public static TextRange? DeleteLeft(TextDocument text, int caret, Indentation indentation)
    {
        DocumentLine line = text.GetLineByOffset(caret);
        int column = caret - line.Offset;
        string lineText = text.GetText(line);
        if (column == 0 || column > IndentRules.Leading(lineText))
            return null;
        var indenting = new Indenting(indentation);
        int from = indenting.Width(lineText.AsSpan(0, column));
        int to = Math.Max(0, from - 1 - (from - 1) % indentation.Size);
        return TextRange.FromBounds(line.Offset + indenting.OffsetOf(lineText, to), caret);
    }

    /// <summary>
    /// The selected lines, or the caret's, with their indentation at the next or the previous tab stop, as VS Code's
    /// <c>ShiftCommand</c>. A last line that the selection touches only at its start takes no part, nor do lines
    /// without indentation when they outdent, nor empty lines among several that indent. The selection follows the text
    /// as VS Code's tracked selection does, and its start stays where it was when it is in the first line's
    /// indentation. On a line of whitespace only, the caret goes to the end of the new indentation.
    /// </summary>
    private static TabEdit Shift(TextDocument text, TextSelection selection, Indentation indentation, bool outdent)
    {
        (int start, int end) = (selection.Range.Start, selection.Range.End);
        bool empty = start == end;
        DocumentLine first = text.GetLineByOffset(start);
        DocumentLine last = text.GetLineByOffset(end);
        if (last != first && last.Offset == end)
            last = last.PreviousLine;
        var indenting = new Indenting(indentation);
        var edits = new List<TextEdit>();
        bool startStays = false;
        for (DocumentLine line = first; ; line = line.NextLine)
        {
            string lineText = text.GetText(line);
            int whitespace = IndentRules.Leading(lineText);
            if (!(outdent ? whitespace == 0 : lineText.Length == 0 && first != last))
            {
                string shifted = outdent
                    ? indenting.Unshift(lineText[..whitespace])
                    : indenting.Shift(lineText[..whitespace]);
                if (line == first && !empty)
                    startStays = start - line.Offset <= whitespace;
                if (!lineText.AsSpan(0, whitespace).SequenceEqual(shifted))
                    edits.Add(new TextEdit(line.Offset, whitespace, shifted));
            }
            if (line == last)
                break;
        }
        if (edits.Count == 0)
            return new TabEdit([], selection);
        int newStart;
        int newEnd;
        if (empty && string.IsNullOrWhiteSpace(text.GetText(first)))
            newStart = newEnd = edits[0].Offset + edits[0].Text.Length;
        else
        {
            // A caret stays before text that goes in where it is only at its line's end.
            newStart = MapThrough(edits, start, sticksBefore: !empty || start == first.EndOffset);
            newEnd = empty ? newStart : MapThrough(edits, end, sticksBefore: false);
            if (startStays && newStart > start)
                newStart = start;
        }
        return new TabEdit(
            edits,
            selection.Anchor <= selection.Caret
                ? new TextSelection(newStart, newEnd)
                : new TextSelection(newEnd, newStart)
        );
    }

    /// <summary>
    /// Where <paramref name="offset"/> goes through <paramref name="edits"/>: text inserted at it goes after it if it
    /// <paramref name="sticksBefore"/>, and a replacement keeps it where old and new text overlap.
    /// </summary>
    private static int MapThrough(List<TextEdit> edits, int offset, bool sticksBefore)
    {
        int delta = 0;
        foreach ((int start, int removed, string text) in edits)
        {
            int end = start + removed;
            int inserted = text.Length;
            if (offset < start)
                break;
            if (offset > end)
            {
                delta += inserted - removed;
                continue;
            }
            int common = Math.Min(removed, inserted);
            int mapped =
                removed == 0 ? (sticksBefore ? offset : offset + inserted)
                : offset < start + common || (offset == start + common && (removed > inserted || sticksBefore)) ? offset
                : removed > inserted ? start + inserted
                : offset + inserted - removed;
            return mapped + delta;
        }
        return offset + delta;
    }
}

/// <summary>The edits of a Tab or Shift+Tab, and the selection after them.</summary>
internal readonly record struct TabEdit(IReadOnlyList<TextEdit> Edits, TextSelection Selection);

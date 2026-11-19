using AvaloniaEdit.Document;

namespace Darp.CodeEditor.Search;

/// <summary>The ranges that Find in Selection searches, as VS Code makes them of the selection.</summary>
internal static class FindScope
{
    /// <summary>
    /// The ranges of the selection that are not empty, each over its lines whole if it spans several, but without a
    /// last line that it only reaches the start of; one within a line stays as it is. Null when every range is empty.
    /// </summary>
    /// <param name="document">The text.</param>
    /// <param name="ranges">The selection's ranges: one, or a rectangle's of each line.</param>
    /// <param name="trimEnd">
    /// Whether a range that ends at the start of a later line ends at the end of the line before, as the toggle and its
    /// key take the selection; the automatic Find in Selection doesn't.
    /// </param>
    public static List<TextRange>? OfSelection(TextDocument document, IEnumerable<ISegment> ranges, bool trimEnd)
    {
        List<TextRange> scope = [.. ranges.Where(range => range.Length > 0).Select(range => Widen(range))];
        return scope.Count > 0 ? scope : null;

        TextRange Widen(ISegment range)
        {
            int end = range.EndOffset;
            // A range that is not empty and ends at a line's start starts on a line before.
            if (trimEnd && document.GetLineByOffset(end) is var endLine && end == endLine.Offset)
                end = endLine.PreviousLine.EndOffset;
            DocumentLine first = document.GetLineByOffset(range.Offset);
            DocumentLine last = document.GetLineByOffset(end);
            if (first == last)
                return TextRange.FromBounds(range.Offset, end);
            if (end == last.Offset)
                last = last.PreviousLine;
            return TextRange.FromBounds(first.Offset, last.EndOffset);
        }
    }
}

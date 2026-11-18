using AvaloniaEdit.Document;

namespace Darp.CodeEditor.Search;

/// <summary>The ranges that Find in Selection searches, as VS Code makes them of the selection.</summary>
internal static class FindScope
{
    /// <summary>
    /// The scope that the selection's ranges make, as VS Code's Find in Selection takes them: the ranges that are not
    /// empty, each widened as <see cref="Widen"/> does. Null when every range is empty.
    /// </summary>
    /// <param name="document">The text.</param>
    /// <param name="ranges">The selection's ranges: one, or a rectangle's of each line.</param>
    /// <param name="trimEnd">
    /// Whether a range that ends at the start of a later line ends at the end of the line before, as the toggle and its
    /// key take the selection; VS Code's automatic Find in Selection doesn't.
    /// </param>
    public static List<TextRange>? OfSelection(TextDocument document, IEnumerable<ISegment> ranges, bool trimEnd)
    {
        List<TextRange> scope =
        [
            .. ranges
                .Where(range => range.Length > 0)
                .Select(range => Widen(document, trimEnd ? TrimEnd(document, range) : ToRange(range))),
        ];
        return scope.Count > 0 ? scope : null;
    }

    /// <summary>
    /// The range that VS Code searches for a range of the scope: one over several lines covers them whole, from the
    /// first line's start to the last line's end, but leaves out a last line that it only reaches the start of; one
    /// within a line stays as it is.
    /// </summary>
    public static TextRange Widen(TextDocument document, TextRange range)
    {
        DocumentLine first = document.GetLineByOffset(range.Start);
        DocumentLine last = document.GetLineByOffset(range.End);
        if (first == last)
            return range;
        if (range.End == last.Offset)
            last = last.PreviousLine;
        return TextRange.FromBounds(first.Offset, last.EndOffset);
    }

    /// <summary>A range that ends at the start of a later line, to the end of the line before.</summary>
    private static TextRange TrimEnd(TextDocument document, ISegment range)
    {
        DocumentLine last = document.GetLineByOffset(range.EndOffset);
        return range.EndOffset == last.Offset && last.Offset > range.Offset
            ? TextRange.FromBounds(range.Offset, last.PreviousLine.EndOffset)
            : ToRange(range);
    }

    private static TextRange ToRange(ISegment segment) => new(segment.Offset, segment.Length);
}

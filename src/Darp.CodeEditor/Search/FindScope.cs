using AvaloniaEdit.Document;

namespace Darp.CodeEditor.Search;

/// <summary>The ranges that Find in Selection searches, as VS Code makes them of the selection.</summary>
internal static class FindScope
{
    /// <summary>
    /// The ranges of the selection, one or a rectangle's of each line, that are not empty, each over its lines whole if
    /// it spans several, but without a last line that it only reaches the start of; one within a line stays as it is.
    /// Null when every range is empty.
    /// </summary>
    public static List<TextRange>? OfSelection(TextDocument document, IEnumerable<ISegment> ranges)
    {
        List<TextRange> scope = [.. ranges.Where(range => range.Length > 0).Select(range => Widen(range))];
        return scope.Count > 0 ? scope : null;

        TextRange Widen(ISegment range)
        {
            DocumentLine first = document.GetLineByOffset(range.Offset);
            DocumentLine last = document.GetLineByOffset(range.EndOffset);
            if (first == last)
                return new TextRange(range.Offset, range.Length);
            if (range.EndOffset == last.Offset)
                last = last.PreviousLine;
            return TextRange.FromBounds(first.Offset, last.EndOffset);
        }
    }
}

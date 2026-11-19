using AvaloniaEdit.Document;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The ranges that smart select goes through, as VS Code's <c>smartSelect.ts</c> finds and combines them: its word
/// ranges (<c>wordSelections.ts</c>), without the parts of words, and its bracket ranges
/// (<c>bracketSelections.ts</c>), with the lines around a range, as <c>editor.smartSelect</c> selects leading and
/// trailing whitespace by default.
/// </summary>
internal static class SmartSelectRanges
{
    /// <summary>
    /// The word ranges at <paramref name="caret"/>: the word there, its line when it holds only spaces and tabs, and the
    /// whole text.
    /// </summary>
    public static IEnumerable<TextRange> Words(TextDocument document, int caret)
    {
        DocumentLine line = document.GetLineByOffset(caret);
        string text = document.GetText(line.Offset, VisibleText.End(line) - line.Offset);
        if (WordRules.WordAt(text, Math.Min(caret - line.Offset, text.Length)) is { } word)
            yield return new TextRange(line.Offset + word.Start, word.Length);
        if (text.Length > 0 && text.AsSpan().TrimStart(" \t").IsEmpty)
            yield return TextRange.FromBounds(line.Offset, line.EndOffset);
        yield return new TextRange(0, document.TextLength);
    }

    /// <summary>
    /// The bracket ranges at <paramref name="caret"/>: for each pair around it, from the innermost out, the text between
    /// its brackets, the pair with them, and for a pair over several lines, the pair from the text that starts its line,
    /// or from the line above when that line's text starts where an opening bracket on a line of its own does.
    /// </summary>
    public static IEnumerable<TextRange> Brackets(
        TextDocument document,
        int caret,
        IReadOnlyList<CharacterPair> brackets,
        Func<int, int[]?> tokensOf
    )
    {
        foreach ((int open, int close) in BracketMatcher.PairsAround(document, caret, brackets, tokensOf))
        {
            yield return TextRange.FromBounds(open + 1, close);
            yield return TextRange.FromBounds(open, close + 1);
            DocumentLine line = document.GetLineByOffset(open);
            if (line == document.GetLineByOffset(close))
                continue;
            int column = open - line.Offset;
            ISegment leading = TextUtilities.GetLeadingWhitespace(document, line);
            // xxxxxxxx {
            //
            // }
            if (leading.Length != column)
            {
                yield return TextRange.FromBounds(leading.EndOffset, close + 1);
                yield return TextRange.FromBounds(line.Offset, close + 1);
            }
            // xxxxxxxx
            // {
            //
            // }
            if (line.PreviousLine is not { } above)
                continue;
            ISegment aboveLeading = TextUtilities.GetLeadingWhitespace(document, above);
            if (aboveLeading.Length < above.Length && aboveLeading.Length == column)
            {
                yield return TextRange.FromBounds(aboveLeading.EndOffset, close + 1);
                yield return TextRange.FromBounds(above.Offset, close + 1);
            }
        }
    }

    /// <summary>
    /// The ranges that expanding a selection at <paramref name="caret"/> goes through, from the innermost out, as VS
    /// Code's <c>provideSelectionRanges</c> combines its providers' <paramref name="ranges"/>: those around the caret,
    /// each around the one before; and where a range grows onto other lines, first the lines of the one before, from
    /// their first text to their last, and then whole.
    /// </summary>
    public static List<TextRange> Combine(TextDocument document, IEnumerable<TextRange> ranges, int caret)
    {
        // The innermost first: the latest start, then the earliest end.
        List<TextRange> around = [.. ranges.Where(range => range.Start <= caret && caret <= range.End)];
        around.Sort((a, b) => a.Start != b.Start ? b.Start.CompareTo(a.Start) : a.End.CompareTo(b.End));
        var nested = new List<TextRange>();
        foreach (TextRange range in around)
        {
            if (nested.Count == 0 || (Contains(range, nested[^1]) && range != nested[^1]))
                nested.Add(range);
        }
        var combined = new List<TextRange>();
        for (int i = 0; i < nested.Count; i++)
        {
            if (i > 0)
                combined.AddRange(Lines(document, nested[i - 1], nested[i]));
            combined.Add(nested[i]);
        }
        return combined;
    }

    /// <summary>
    /// Where <paramref name="current"/> reaches other lines than <paramref name="previous"/>, the lines of
    /// <paramref name="previous"/> from their first text to their last, and then whole, where they lie between the two.
    /// </summary>
    private static IEnumerable<TextRange> Lines(TextDocument document, TextRange previous, TextRange current)
    {
        DocumentLine first = document.GetLineByOffset(previous.Start);
        DocumentLine last = document.GetLineByOffset(previous.End);
        if (first == document.GetLineByOffset(current.Start) && last == document.GetLineByOffset(current.End))
            yield break;
        ISegment leading = TextUtilities.GetLeadingWhitespace(document, first);
        ISegment trailing = TextUtilities.GetTrailingWhitespace(document, last);
        // VS Code puts the first and last text of a line without any before the line's start: such a range starts
        // before every range on its line, and ends before the text it should hold.
        bool startsBlank = leading.Length == first.Length;
        bool endsBlank = trailing.Offset == last.Offset;
        var trimmed = TextRange.FromBounds(startsBlank ? first.Offset : leading.EndOffset, trailing.Offset);
        bool insideCurrent = startsBlank
            ? current.Start < first.Offset && trimmed.End <= current.End
            : Contains(current, trimmed) && trimmed != current;
        if (!endsBlank && Contains(trimmed, previous) && (startsBlank || trimmed != previous) && insideCurrent)
            yield return trimmed;
        var whole = TextRange.FromBounds(first.Offset, last.EndOffset);
        if ((startsBlank || endsBlank || whole != trimmed) && Contains(current, whole) && whole != current)
            yield return whole;
    }

    private static bool Contains(TextRange outer, TextRange inner) =>
        outer.Start <= inner.Start && inner.End <= outer.End;
}

using AvaloniaEdit.Document;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The ranges that expanding the selection goes through, as VS Code's smart select finds them: its word and bracket
/// providers (<c>wordSelections.ts</c>, <c>bracketSelections.ts</c>), and how <c>provideSelectionRanges</c> combines them
/// with a server's.
/// </summary>
internal static class SelectionRangeProviders
{
    /// <summary>
    /// VS Code's word ranges at <paramref name="caret"/>: the part of the word between its humps, underscores and
    /// dashes with <paramref name="subwords"/>, the word, a line of whitespace only, and the whole text.
    /// </summary>
    public static IEnumerable<TextRange> Words(TextDocument document, int caret, bool subwords)
    {
        DocumentLine line = document.GetLineByOffset(caret);
        string text = document.GetText(line);
        int column = caret - line.Offset;
        if (WordRules.WordAtPosition(text, column) is var (start, end))
        {
            if (subwords && Subword(text[start..end], column - start) is var (from, to))
                yield return TextRange.FromBounds(line.Offset + start + from, line.Offset + start + to);
            yield return TextRange.FromBounds(line.Offset + start, line.Offset + end);
        }
        if (text.Length > 0 && FirstText(text) == text.Length)
            yield return TextRange.FromBounds(line.Offset, line.EndOffset);
        yield return new TextRange(0, document.TextLength);
    }

    /// <summary>
    /// The part of <paramref name="word"/> around <paramref name="offset"/> between humps, as <c>Bar</c> of
    /// <c>fooBar</c>, and underscores or dashes: VS Code's <c>_addInWordRanges</c>, whose last character seen going left
    /// starts the way right. Null for none.
    /// </summary>
    private static (int Start, int End)? Subword(string word, int offset)
    {
        int start = offset;
        int end = offset;
        int last = -1;
        for (; start >= 0; start--)
        {
            int c = start < word.Length ? word[start] : -1;
            if (start != offset && c is '_' or '-')
                break;
            if (IsLower(c) && IsUpper(last))
                break;
            last = c;
        }
        start++;
        for (; end < word.Length; end++)
        {
            char c = word[end];
            if (IsUpper(c) && IsLower(last))
                break;
            if (c is '_' or '-')
                break;
            last = c;
        }
        return start < end ? (start, end) : null;

        static bool IsLower(int c) => c is >= 'a' and <= 'z';
        static bool IsUpper(int c) => c is >= 'A' and <= 'Z';
    }

    /// <summary>
    /// VS Code's bracket ranges around <paramref name="caret"/>: for each pair of brackets around it, from the inside
    /// out, the text between them, the pair with it, and for a pair over several lines, from the text that starts its
    /// first line, or from a line above with just the text before an opening bracket on its own line.
    /// </summary>
    public static List<TextRange> Brackets(
        TextDocument document,
        int caret,
        IReadOnlyList<CharacterPair> brackets,
        Func<int, int[]?> tokensOf
    )
    {
        // Going right: the closing brackets without their opening ones, per kind, in order.
        var closings = new Queue<int>?[brackets.Count];
        var open = new int[brackets.Count];
        int unpaired = 0;
        foreach (
            (int offset, int pair, bool isOpen) in BracketMatcher.Scan(
                document,
                caret,
                forward: true,
                brackets,
                tokensOf
            )
        )
        {
            if (isOpen)
                open[pair]++;
            else if (--open[pair] < 0)
            {
                open[pair] = 0;
                (closings[pair] ??= new Queue<int>()).Enqueue(offset);
                unpaired++;
            }
        }
        var ranges = new List<TextRange>();
        // Going left: each opening bracket without its closing one pairs with the next of those closing ones.
        var closed = new int[brackets.Count];
        foreach (
            (int offset, int pair, bool isOpen) in BracketMatcher.Scan(
                document,
                caret,
                forward: false,
                brackets,
                tokensOf
            )
        )
        {
            if (unpaired == 0)
                break;
            if (!isOpen)
                closed[pair]++;
            else if (--closed[pair] < 0)
            {
                closed[pair] = 0;
                if (closings[pair] is not { Count: > 0 } queue)
                    continue;
                int close = queue.Dequeue();
                unpaired--;
                ranges.Add(TextRange.FromBounds(offset + 1, close));
                var outer = TextRange.FromBounds(offset, close + 1);
                ranges.Add(outer);
                AddBracketLeading(document, outer, ranges);
            }
        }
        return ranges;
    }

    /// <summary>VS Code's <c>_addBracketLeading</c>, for a pair over several lines.</summary>
    private static void AddBracketLeading(TextDocument document, TextRange bracket, List<TextRange> ranges)
    {
        DocumentLine line = document.GetLineByOffset(bracket.Start);
        if (line == document.GetLineByOffset(bracket.End))
            return;
        // xxxxxxxx {
        //
        // }
        int column = bracket.Start - line.Offset;
        int firstText = FirstText(document.GetText(line));
        if (firstText < line.Length && firstText != column)
        {
            ranges.Add(TextRange.FromBounds(line.Offset + firstText, bracket.End));
            ranges.Add(TextRange.FromBounds(line.Offset, bracket.End));
        }
        // xxxxxxxx
        // {
        //
        // }
        if (line.PreviousLine is not { } above)
            return;
        string aboveText = document.GetText(above);
        int aboveFirst = FirstText(aboveText);
        if (aboveFirst < aboveText.Length && aboveFirst == column && aboveFirst != LastText(aboveText) + 1)
        {
            ranges.Add(TextRange.FromBounds(above.Offset + aboveFirst, bracket.End));
            ranges.Add(TextRange.FromBounds(above.Offset, bracket.End));
        }
    }

    /// <summary>
    /// The ranges that expanding the selection at <paramref name="caret"/> goes through, from the innermost out, as VS
    /// Code's <c>provideSelectionRanges</c> combines its providers' <paramref name="ranges"/>: those around the caret,
    /// each around the one before; with <paramref name="whitespace"/>, where a range grows onto other lines, the lines
    /// of the one before from their first text to their last, and then whole.
    /// </summary>
    public static List<TextRange> Combine(
        TextDocument document,
        IEnumerable<TextRange> ranges,
        int caret,
        bool whitespace
    )
    {
        List<TextRange> around = [.. ranges.Where(range => range.Start <= caret && caret <= range.End)];
        // The innermost first: the latest start, then the earliest end.
        around.Sort((a, b) => a.Start != b.Start ? b.Start.CompareTo(a.Start) : a.End.CompareTo(b.End));
        var nested = new List<TextRange>();
        foreach (TextRange range in around)
        {
            if (nested.Count == 0 || Contains(range, nested[^1]) && range != nested[^1])
                nested.Add(range);
        }
        if (!whitespace || nested.Count == 0)
            return nested;
        var result = new List<TextRange> { nested[0] };
        for (int i = 1; i < nested.Count; i++)
        {
            TextRange previous = nested[i - 1];
            TextRange current = nested[i];
            DocumentLine startLine = document.GetLineByOffset(previous.Start);
            DocumentLine endLine = document.GetLineByOffset(previous.End);
            if (
                startLine != document.GetLineByOffset(current.Start)
                || endLine != document.GetLineByOffset(current.End)
            )
            {
                string startText = document.GetText(startLine);
                string endText = document.GetText(endLine);
                // VS Code's column of the first text of a line of whitespace only is 0, before the line's start: such a
                // range equals no other, and one that ends there holds nothing that starts on its line.
                bool startsBeforeLine = FirstText(startText) == startText.Length;
                bool endsBeforeLine = FirstText(endText) == endText.Length;
                var trimmed = TextRange.FromBounds(
                    startsBeforeLine ? startLine.Offset : startLine.Offset + FirstText(startText),
                    endsBeforeLine ? endLine.Offset : endLine.Offset + LastText(endText) + 1
                );
                bool currentHoldsTrimmed = startsBeforeLine
                    ? current.Start < startLine.Offset && trimmed.End <= current.End
                    : Contains(current, trimmed) && current != trimmed;
                if (
                    !endsBeforeLine
                    && Contains(trimmed, previous)
                    && (startsBeforeLine || trimmed != previous)
                    && currentHoldsTrimmed
                )
                    result.Add(trimmed);
                var full = TextRange.FromBounds(startLine.Offset, endLine.EndOffset);
                if (
                    Contains(full, previous)
                    && (startsBeforeLine || endsBeforeLine || full != trimmed)
                    && Contains(current, full)
                    && current != full
                )
                    result.Add(full);
            }
            result.Add(current);
        }
        return result;
    }

    private static bool Contains(TextRange outer, TextRange inner) =>
        outer.Start <= inner.Start && inner.End <= outer.End;

    /// <summary>The index of the first character of <paramref name="line"/> that is no space or tab; its length for none.</summary>
    private static int FirstText(string line)
    {
        int index = 0;
        while (index < line.Length && line[index] is ' ' or '\t')
            index++;
        return index;
    }

    /// <summary>The index of the last character of <paramref name="line"/> that is no space or tab; -1 for none.</summary>
    private static int LastText(string line)
    {
        int index = line.Length - 1;
        while (index >= 0 && line[index] is ' ' or '\t')
            index--;
        return index;
    }
}

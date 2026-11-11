namespace Darp.CodeEditor.Editing;

/// <summary>
/// Guesses how a text indents, as VS Code does with <c>editor.detectIndentation</c>: a port of its
/// <c>indentationGuesser.ts</c>. Whether lines indent with tabs or spaces goes by how many lines start with each, and
/// the size of one level by the differences in indentation from line to line, among 2, 4, 6, 8, 3, 5 and 7.
/// </summary>
internal static class IndentationGuesser
{
    /// <summary>How many lines from the start the guess reads at most.</summary>
    private const int MaxLines = 10_000;

    /// <summary>The sizes to choose from, the even ones first, as VS Code prefers them.</summary>
    private static readonly int[] s_sizes = [2, 4, 6, 8, 3, 5, 7];

    /// <summary>
    /// The indentation of <paramref name="text"/>; <paramref name="fallback"/>, or its parts, where the text gives no
    /// answer, as when it has no indented lines.
    /// </summary>
    public static Indentation Guess(ReadOnlySpan<char> text, Indentation fallback)
    {
        int linesWithTabs = 0;
        int linesWithSpaces = 0;
        // The last line with more than whitespace, and where its text starts.
        ReadOnlySpan<char> previous = default;
        int previousIndentation = 0;
        // How often each difference from one line to the next occurs.
        Span<int> differences = stackalloc int[9];
        int lineCount = 0;
        while (lineCount++ < MaxLines)
        {
            int end = text.IndexOfAny('\r', '\n');
            ReadOnlySpan<char> line = end < 0 ? text : text[..end];
            if (Indented(line) is { } indented)
            {
                (int tabs, int spaces, int indentation) = indented;
                if (tabs > 0)
                    linesWithTabs++;
                else if (spaces > 1)
                    linesWithSpaces++;
                (int difference, bool looksLikeAlignment) = SpacesDiff(
                    previous,
                    previousIndentation,
                    line,
                    indentation
                );
                // Alignment, as of a declaration continued after a comma, is no hint, unless it matches the fallback.
                if (!looksLikeAlignment || !fallback.UseTabs && fallback.Size == difference)
                {
                    if (difference < differences.Length)
                        differences[difference]++;
                    previous = line;
                    previousIndentation = indentation;
                }
            }
            if (end < 0)
                break;
            int next = end + 1;
            if (text[end] == '\r' && next < text.Length && text[next] == '\n')
                next++;
            text = text[next..];
        }

        bool useTabs = linesWithTabs == linesWithSpaces ? fallback.UseTabs : linesWithTabs > linesWithSpaces;
        if (useTabs)
            return new Indentation(fallback.Size, UseTabs: true);
        int size = fallback.Size;
        int score = 0;
        foreach (int candidate in s_sizes)
        {
            if (differences[candidate] > score)
            {
                score = differences[candidate];
                size = candidate;
            }
        }
        // 2 wins over 4 with at least 2/3 of its occurrences, as in YAML, where deeper nesting gives some 4s.
        if (size == 4 && differences[4] > 0 && differences[2] > 0 && 3 * differences[2] >= 2 * differences[4])
            size = 2;
        return new Indentation(size, UseTabs: false);
    }

    /// <summary>
    /// The tabs and spaces before <paramref name="line"/>'s text, and where its text starts; null for a line of
    /// whitespace only.
    /// </summary>
    private static (int Tabs, int Spaces, int Indentation)? Indented(ReadOnlySpan<char> line)
    {
        int tabs = 0;
        int spaces = 0;
        for (int i = 0; i < line.Length; i++)
        {
            switch (line[i])
            {
                case '\t':
                    tabs++;
                    break;
                case ' ':
                    spaces++;
                    break;
                default:
                    return (tabs, spaces, i);
            }
        }
        return null;
    }

    /// <summary>
    /// The difference in spaces between the indentation of two lines, <paramref name="a"/> above
    /// <paramref name="b"/>, and whether <paramref name="b"/> seems aligned to <paramref name="a"/> rather than
    /// indented: VS Code's <c>spacesDiff</c>. A difference in tabs and spaces at once counts as so many spaces per tab.
    /// </summary>
    private static (int Difference, bool LooksLikeAlignment) SpacesDiff(
        ReadOnlySpan<char> a,
        int aIndentation,
        ReadOnlySpan<char> b,
        int bIndentation
    )
    {
        int i = 0;
        while (i < aIndentation && i < bIndentation && a[i] == b[i])
            i++;
        int aSpaces = a[i..aIndentation].Count(' ');
        int aTabs = aIndentation - i - aSpaces;
        int bSpaces = b[i..bIndentation].Count(' ');
        int bTabs = bIndentation - i - bSpaces;
        if (aSpaces > 0 && aTabs > 0 || bSpaces > 0 && bTabs > 0)
            return (0, false);
        int tabsDiff = Math.Abs(aTabs - bTabs);
        int spacesDiff = Math.Abs(aSpaces - bSpaces);
        if (tabsDiff == 0)
        {
            // Such as "const a = b + c," above "      d = b - c;".
            bool looksLikeAlignment =
                spacesDiff > 0
                && bSpaces >= 1
                && bSpaces - 1 < a.Length
                && bSpaces < b.Length
                && b[bSpaces] != ' '
                && a[bSpaces - 1] == ' '
                && a[^1] == ',';
            return (spacesDiff, looksLikeAlignment);
        }
        return spacesDiff % tabsDiff == 0 ? (spacesDiff / tabsDiff, false) : (0, false);
    }
}

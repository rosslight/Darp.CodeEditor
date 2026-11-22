namespace Darp.CodeEditor.Editing;

/// <summary>
/// Guesses how a text indents, as VS Code's <c>editor.detectIndentation</c> does (its <c>indentationGuesser.ts</c>):
/// tabs or spaces by how many lines start with each, and the size of a level by how often lines indent by each of 2,
/// 4, 6, 8, 3, 5 and 7 spaces more or less than the line above.
/// </summary>
internal static class IndentationGuesser
{
    private const int MaxLines = 10_000;

    /// <summary>The sizes to guess, in order of preference: the even ones first.</summary>
    private static readonly int[] s_sizes = [2, 4, 6, 8, 3, 5, 7];

    /// <summary>
    /// The indentation of <paramref name="text"/>, from its first 10,000 lines; <paramref name="fallback"/>, or the part
    /// of it the text gives no answer to, as for a text without indented lines.
    /// </summary>
    public static Indentation Guess(ReadOnlySpan<char> text, Indentation fallback)
    {
        int linesWithTabs = 0;
        int linesWithSpaces = 0;
        // How often each difference in spaces from one indented line to the next occurs, up to 8.
        Span<int> differences = stackalloc int[9];
        // The last line with text that counted, up to where its text starts.
        ReadOnlySpan<char> previous = default;
        for (int line = 0; line < MaxLines && !text.IsEmpty; line++)
        {
            int end = text.IndexOfAny('\r', '\n');
            ReadOnlySpan<char> current = end < 0 ? text : text[..end];
            text = end < 0 ? default : text[(end + (text[end..].StartsWith("\r\n") ? 2 : 1))..];
            int indentation = current.IndexOfAnyExcept(' ', '\t');
            if (indentation < 0)
                continue;
            ReadOnlySpan<char> whitespace = current[..indentation];
            if (whitespace.Contains('\t'))
                linesWithTabs++;
            else if (whitespace.Length > 1)
                linesWithSpaces++;
            (int difference, bool alignment) = Difference(previous, current, indentation);
            // An alignment, as of a declaration continued after a comma, is no hint, unless it is the fallback's size.
            if (alignment && (fallback.UseTabs || fallback.Size != difference))
                continue;
            if (difference < differences.Length)
                differences[difference]++;
            previous = current;
        }

        bool useTabs = linesWithTabs == linesWithSpaces ? fallback.UseTabs : linesWithTabs > linesWithSpaces;
        if (useTabs)
            return fallback with { UseTabs = true };
        int size = fallback.Size;
        int score = 0;
        foreach (int candidate in s_sizes)
        {
            if (differences[candidate] > score)
                (size, score) = (candidate, differences[candidate]);
        }
        // 2 wins over 4 with at least two thirds of its count, as in YAML, where deeper nesting gives some 4s.
        if (size == 4 && differences[2] > 0 && 3 * differences[2] >= 2 * differences[4])
            size = 2;
        return new Indentation(size, UseTabs: false);
    }

    /// <summary>
    /// How many spaces <paramref name="current"/>'s indentation differs from <paramref name="previous"/>'s, a tab
    /// counting as so many spaces where both change, and whether <paramref name="current"/> looks aligned to a part of
    /// <paramref name="previous"/> rather than indented: VS Code's <c>spacesDiff</c>.
    /// </summary>
    private static (int Difference, bool Alignment) Difference(
        ReadOnlySpan<char> previous,
        ReadOnlySpan<char> current,
        int indentation
    )
    {
        int previousIndentation = previous.IndexOfAnyExcept(' ', '\t') is var found and >= 0 ? found : 0;
        int common = previous[..previousIndentation].CommonPrefixLength(current[..indentation]);
        ReadOnlySpan<char> before = previous[common..previousIndentation];
        ReadOnlySpan<char> after = current[common..indentation];
        int beforeSpaces = before.Count(' ');
        int afterSpaces = after.Count(' ');
        int beforeTabs = before.Length - beforeSpaces;
        int afterTabs = after.Length - afterSpaces;
        if ((beforeSpaces > 0 && beforeTabs > 0) || (afterSpaces > 0 && afterTabs > 0))
            return (0, false);
        int tabs = Math.Abs(beforeTabs - afterTabs);
        int spaces = Math.Abs(beforeSpaces - afterSpaces);
        if (tabs > 0)
            return (spaces % tabs == 0 ? spaces / tabs : 0, false);
        // Such as "const a = b + c," above "      d = b - c;".
        bool alignment =
            spaces > 0
            && afterSpaces > 0
            && afterSpaces - 1 < previous.Length
            && afterSpaces < current.Length
            && current[afterSpaces] != ' '
            && previous[afterSpaces - 1] == ' '
            && previous[^1] == ',';
        return (spaces, alignment);
    }
}

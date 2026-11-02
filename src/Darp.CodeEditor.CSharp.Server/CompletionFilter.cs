namespace Darp.CodeEditor.CSharp.Server;

/// <summary>
/// Scores how a completion item matches the typed text, with the rules of the editor's own filter
/// (<c>Darp.CodeEditor.Completion.CompletionMatcher</c>): prefix, then word parts, then substring. A list the server
/// filters therefore keeps every item the editor would show, unless it is cut to its best matches.
/// </summary>
internal static class CompletionFilter
{
    /// <returns>Higher is better; 0 means the item does not match.</returns>
    public static int Score(string text, string typed)
    {
        if (typed.Length == 0)
            return 1;
        if (text.StartsWith(typed, StringComparison.Ordinal))
            return 5;
        if (text.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            return 4;
        if (IsWordPartMatch(text, typed))
            return 3;
        return text.Contains(typed, StringComparison.OrdinalIgnoreCase) ? 2 : 0;
    }

    /// <summary>
    /// True when the typed text matches the starts of the item's word parts in order, such as "rt" for
    /// "ReadTemperature".
    /// </summary>
    private static bool IsWordPartMatch(string text, string typed)
    {
        int position = 0;
        for (int i = 0; i < typed.Length; i++)
        {
            // Continue inside the current word part, or jump to the next part that starts with this character.
            if (i > 0 && position < text.Length && EqualsIgnoreCase(text[position], typed[i]))
            {
                position++;
                continue;
            }
            while (
                position < text.Length && !(IsWordStart(text, position) && EqualsIgnoreCase(text[position], typed[i]))
            )
                position++;
            if (position == text.Length)
                return false;
            position++;
        }
        return true;
    }

    private static bool IsWordStart(string text, int index) =>
        index == 0
        || (char.IsUpper(text[index]) && !char.IsUpper(text[index - 1]))
        || (!char.IsLetterOrDigit(text[index - 1]) && char.IsLetterOrDigit(text[index]));

    private static bool EqualsIgnoreCase(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}

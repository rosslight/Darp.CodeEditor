namespace Darp.CodeEditor.Completion;

/// <summary>Scores how well a completion item matches what the user typed, with ordinal comparisons only.</summary>
internal static class CompletionMatcher
{
    public const int MaxScore = 100;

    /// <returns>Higher is better, up to <see cref="MaxScore"/>; 0 means the item does not match.</returns>
    public static int Score(string text, string query)
    {
        if (query.Length == 0)
            return 1;
        if (text.StartsWith(query, StringComparison.Ordinal))
            return text.Length == query.Length ? MaxScore : 90;
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return text.Length == query.Length ? 85 : 80;
        if (IsWordPartMatch(text, query))
            return 60;
        if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 40;
        return 0;
    }

    /// <summary>
    /// True when the query matches the starts of the item's word parts in order, such as "rt" or "ReTe" for
    /// "ReadTemperature" and "sl" for "set_led".
    /// </summary>
    private static bool IsWordPartMatch(string text, string query)
    {
        int position = 0;
        for (int i = 0; i < query.Length; i++)
        {
            // Continue inside the current word part, or jump to the next part that starts with this character.
            if (i > 0 && position < text.Length && EqualsIgnoreCase(text[position], query[i]))
            {
                position++;
                continue;
            }
            while (
                position < text.Length && !(IsWordStart(text, position) && EqualsIgnoreCase(text[position], query[i]))
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

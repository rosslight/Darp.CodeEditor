using AvaloniaEdit.Document;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Search;

/// <summary>What the user searches for, and how it matches.</summary>
/// <param name="Text">The text, taken literally.</param>
/// <param name="MatchCase">Whether case counts.</param>
/// <param name="WholeWord">Whether a match must not continue a word on either side, as in VS Code.</param>
internal sealed record SearchQuery(string Text, bool MatchCase, bool WholeWord);

/// <summary>The matches of a search in one version of the text.</summary>
/// <param name="Version">The version of the text searched.</param>
/// <param name="Matches">
/// The matches, which follow the edits made since once the editor keeps them up to date, as AvaloniaEdit's search
/// does.
/// </param>
/// <param name="IsComplete">Whether the search found every match, rather than stopping at the limit.</param>
internal sealed record SearchResult(int Version, TextSegmentCollection<TextSegment> Matches, bool IsComplete);

/// <summary>
/// Finds the matches of a query in a snapshot, line by line, so that matches don't span lines. Runs in the background.
/// </summary>
internal static class TextSearch
{
    /// <summary>
    /// The most matches a search keeps: twice those of a word on every line of 5,000 lines, and as many as Replace All
    /// changes in about 0.1 s.
    /// </summary>
    public const int MaxMatches = 10_000;

    /// <summary>Finds the matches of <paramref name="query"/>, a text that is not empty, in the snapshot.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    public static SearchResult Find(TextSnapshot snapshot, SearchQuery query, CancellationToken cancellationToken)
    {
        var matches = new TextSegmentCollection<TextSegment>();
        bool isComplete = FindAll(snapshot.GetText(), query, matches, cancellationToken);
        return new SearchResult(snapshot.Version, matches, isComplete);
    }

    /// <returns>Whether it found every match.</returns>
    private static bool FindAll(
        string text,
        SearchQuery query,
        TextSegmentCollection<TextSegment> matches,
        CancellationToken cancellationToken
    )
    {
        StringComparison comparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int length = query.Text.Length;
        for (int lineStart = 0; ; )
        {
            cancellationToken.ThrowIfCancellationRequested();
            int lineEnd = text.AsSpan(lineStart).IndexOfAny('\r', '\n') is var found and >= 0
                ? lineStart + found
                : text.Length;
            for (int at = lineStart; ; at += length)
            {
                int next = text.AsSpan(at, lineEnd - at).IndexOf(query.Text, comparison);
                if (next < 0)
                    break;
                at += next;
                if (query.WholeWord && !IsWholeWord(text, at, length))
                    continue;
                if (matches.Count == MaxMatches)
                    return false;
                matches.Add(new TextSegment { StartOffset = at, Length = length });
            }
            if (lineEnd == text.Length)
                return true;
            lineStart = text.AsSpan(lineEnd).StartsWith("\r\n") ? lineEnd + 2 : lineEnd + 1;
        }
    }

    /// <summary>
    /// Whether a match starts and ends at word boundaries, as VS Code decides: next to whitespace, a word separator or
    /// the end of the line, or with such a character at its own edge.
    /// </summary>
    private static bool IsWholeWord(string text, int start, int length)
    {
        int end = start + length;
        bool left =
            start == 0
            || !WordCharacters.IsWordCharacter(text[start - 1])
            || !WordCharacters.IsWordCharacter(text[start]);
        bool right =
            end == text.Length
            || !WordCharacters.IsWordCharacter(text[end])
            || !WordCharacters.IsWordCharacter(text[end - 1]);
        return left && right;
    }
}

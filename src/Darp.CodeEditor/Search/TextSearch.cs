using System.Diagnostics;
using System.Text.RegularExpressions;
using AvaloniaEdit.Document;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Search;

/// <summary>What the user searches for, and how it matches.</summary>
/// <param name="Text">The text, or a .NET regular expression.</param>
/// <param name="MatchCase">Whether case counts.</param>
/// <param name="WholeWord">Whether a match must not continue a word on either side, as in VS Code.</param>
/// <param name="UseRegex">Whether <paramref name="Text"/> is a regular expression.</param>
internal sealed record SearchQuery(string Text, bool MatchCase, bool WholeWord, bool UseRegex);

/// <summary>How a search ended.</summary>
internal enum SearchOutcome
{
    /// <summary>It found every match.</summary>
    Complete,

    /// <summary>It stopped at <see cref="TextSearch.MaxMatches"/>: there are more.</summary>
    TooManyMatches,

    /// <summary>It stopped after <see cref="TextSearch.TimeLimit"/>: there may be more.</summary>
    OutOfTime,

    /// <summary>The regular expression is invalid.</summary>
    InvalidPattern,
}

/// <summary>A match, and what replaces it; empty when the search was not for replacing.</summary>
internal sealed class SearchMatch(string replacement) : TextSegment
{
    public string Replacement { get; } = replacement;
}

/// <summary>The matches of a search in one version of the text.</summary>
/// <param name="Version">The version of the text searched.</param>
/// <param name="Query">What was searched for.</param>
/// <param name="Replacement">What replaces each match, or null when the search was not for replacing.</param>
/// <param name="Matches">
/// The matches, which follow the edits made since once the editor keeps them up to date, as AvaloniaEdit's search
/// does.
/// </param>
/// <param name="Outcome">How the search ended.</param>
/// <param name="Error">Why the regular expression is invalid.</param>
internal sealed record SearchResult(
    int Version,
    SearchQuery Query,
    string? Replacement,
    TextSegmentCollection<SearchMatch> Matches,
    SearchOutcome Outcome,
    string? Error
)
{
    /// <summary>Whether the search found every match.</summary>
    public bool IsComplete => Outcome == SearchOutcome.Complete;
}

/// <summary>
/// Finds the matches of a query in a snapshot, line by line, so that <c>^</c> and <c>$</c> match at every line's
/// start and end, whatever its line break; matches don't span lines. Runs in the background.
/// </summary>
/// <remarks>
/// A search has limits, so that a pathological regular expression such as <c>(a+)+$</c>, or a query that matches
/// everywhere, can't keep a thread busy or fill the memory: <see cref="TimeLimit"/> and <see cref="MaxMatches"/>.
/// Measured on 5,000 lines of JSON (SearchBenchmarks and ReplaceAllBenchmarks, i7-13700): a word on every line, 5,000
/// matches, takes 1.2 ms; a regular expression with 10,000 matches 2.3 ms, and 0.2 ms with none. Each match kept takes
/// about 140 bytes while searching. Replacing 10,000 matches takes the UI thread about 0.1 s, with the frame after it.
/// </remarks>
internal static class TextSearch
{
    /// <summary>
    /// The most matches a search keeps: twice those of a word on every line of 5,000 lines, and as many as Replace All
    /// changes in about 0.1 s.
    /// </summary>
    public const int MaxMatches = 10_000;

    /// <summary>
    /// How long a search may take: about 400 times what 10,000 matches in 5,000 lines take, so that a slower machine
    /// or a costly expression still finishes. One that takes longer backtracks catastrophically, as <c>(a+)+$</c> does.
    /// A single match can run for as long again before the regular expression's own timeout stops it.
    /// </summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Finds the matches of <paramref name="query"/> in <paramref name="snapshot"/>, with the text that replaces each
    /// one when <paramref name="replacement"/> is given: itself, or for a regular expression with its substitutions,
    /// such as <c>$1</c>.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    public static SearchResult Find(
        TextSnapshot snapshot,
        SearchQuery query,
        string? replacement,
        CancellationToken cancellationToken
    )
    {
        var matches = new TextSegmentCollection<SearchMatch>();
        Regex regex;
        try
        {
            RegexOptions options = query.MatchCase
                ? RegexOptions.CultureInvariant
                : RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
            regex = new Regex(query.UseRegex ? query.Text : Regex.Escape(query.Text), options, TimeLimit);
        }
        catch (ArgumentException e)
        {
            return new SearchResult(
                snapshot.Version,
                query,
                replacement,
                matches,
                SearchOutcome.InvalidPattern,
                e.Message
            );
        }
        return new SearchResult(
            snapshot.Version,
            query,
            replacement,
            matches,
            FindAll(snapshot.GetText(), query, regex, replacement, matches, cancellationToken),
            null
        );
    }

    private static SearchOutcome FindAll(
        string text,
        SearchQuery query,
        Regex regex,
        string? replacement,
        TextSegmentCollection<SearchMatch> matches,
        CancellationToken cancellationToken
    )
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            for (int lineStart = 0; ; )
            {
                int lineEnd = text.AsSpan(lineStart).IndexOfAny('\r', '\n') is var found and >= 0
                    ? lineStart + found
                    : text.Length;
                for (
                    Match match = regex.Match(text, lineStart, lineEnd - lineStart);
                    match.Success;
                    match = match.NextMatch()
                )
                {
                    // Every match counts, also one that Match Whole Word rejects: each may have taken long to find.
                    if (IsOutOfTime(start, cancellationToken))
                        return SearchOutcome.OutOfTime;
                    if (query.WholeWord && !IsWholeWord(text, match.Index, match.Length))
                        continue;
                    if (matches.Count == MaxMatches)
                        return SearchOutcome.TooManyMatches;
                    matches.Add(
                        new SearchMatch(
                            replacement is null ? ""
                            : query.UseRegex ? match.Result(replacement)
                            : replacement
                        )
                        {
                            StartOffset = match.Index,
                            Length = match.Length,
                        }
                    );
                }
                if (IsOutOfTime(start, cancellationToken))
                    return SearchOutcome.OutOfTime;
                if (lineEnd == text.Length)
                    return SearchOutcome.Complete;
                lineStart = text.AsSpan(lineEnd).StartsWith("\r\n") ? lineEnd + 2 : lineEnd + 1;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return SearchOutcome.OutOfTime;
        }
    }

    /// <summary>Whether the search took its time; throws once it is cancelled.</summary>
    private static bool IsOutOfTime(long start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Stopwatch.GetElapsedTime(start) >= TimeLimit;
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
            || (length > 0 && !WordCharacters.IsWordCharacter(text[start]));
        bool right =
            end == text.Length
            || !WordCharacters.IsWordCharacter(text[end])
            || (length > 0 && !WordCharacters.IsWordCharacter(text[end - 1]));
        return left && right;
    }
}

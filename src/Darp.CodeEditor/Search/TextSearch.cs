using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using AvaloniaEdit.Document;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Search;

/// <summary>What the user searches for, and how it matches.</summary>
/// <param name="Text">The text, or a .NET regular expression; its line breaks are <c>\n</c>.</param>
/// <param name="MatchCase">Whether case counts.</param>
/// <param name="WholeWord">Whether a match must not continue a word on either side, as in VS Code.</param>
/// <param name="UseRegex">Whether <paramref name="Text"/> is a regular expression.</param>
internal sealed record SearchQuery(string Text, bool MatchCase, bool WholeWord, bool UseRegex)
{
    /// <summary>
    /// Whether the query can match a line break, as VS Code decides: text with a line break, or a regular expression
    /// with one, or with <c>\n</c>, <c>\r</c> or <c>\W</c>. Its matches may then span lines.
    /// </summary>
    public bool IsMultiline => UseRegex ? IsMultilineRegex(Text) : Text.Contains('\n');

    /// <summary>A port of VS Code's <c>isMultilineRegexSource</c>.</summary>
    private static bool IsMultilineRegex(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\n')
                return true;
            if (pattern[i] != '\\')
                continue;
            if (++i == pattern.Length)
                break;
            if (pattern[i] is 'n' or 'r' or 'W')
                return true;
        }
        return false;
    }
}

/// <summary>What replaces the matches of a search.</summary>
/// <param name="Text">
/// The replacement: for a regular expression in VS Code's syntax (<see cref="ReplacePattern"/>), otherwise as it is.
/// </param>
/// <param name="PreserveCase">
/// Whether a replacement without groups takes the case of each match, as VS Code's Preserve Case does.
/// </param>
internal sealed record SearchReplacement(string Text, bool PreserveCase);

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
/// <param name="Replacement">What replaces the matches, or null when the search was not for replacing.</param>
/// <param name="Matches">
/// The matches, which follow the edits made since once the editor keeps them up to date, as AvaloniaEdit's search
/// does.
/// </param>
/// <param name="Outcome">How the search ended.</param>
/// <param name="Error">Why the regular expression is invalid.</param>
internal sealed record SearchResult(
    int Version,
    SearchQuery Query,
    SearchReplacement? Replacement,
    TextSegmentCollection<SearchMatch> Matches,
    SearchOutcome Outcome,
    string? Error
)
{
    /// <summary>Whether the search found every match.</summary>
    public bool IsComplete => Outcome == SearchOutcome.Complete;
}

/// <summary>
/// The edit that replaces every match of a search in one change: <see cref="Text"/> in place of the text from the first
/// match to the end of the last, with <see cref="Map"/> saying where each match was, so that anchors, markers and the
/// selection follow each replacement as they do separate ones.
/// </summary>
/// <param name="Start">Where the first match starts.</param>
/// <param name="Length">The length of the text from the first match to the end of the last one.</param>
/// <param name="Text">What replaces that text.</param>
/// <param name="Map">The replacements, one after the other.</param>
/// <param name="Count">The number of matches.</param>
internal sealed record SearchEdit(int Start, int Length, string Text, OffsetChangeMap Map, int Count);

/// <summary>
/// Finds the matches of a query in a snapshot, as VS Code's <c>TextModelSearch</c> does. A query that can't match a
/// line break is searched line by line, so that <c>^</c> and <c>$</c> match at every line's start and end, whatever its
/// line break. One that can (<see cref="SearchQuery.IsMultiline"/>) is searched across lines, in the text with its
/// line breaks as <c>\n</c>, and <c>^</c> and <c>$</c> match at every line's start and end too. Runs in the background.
/// </summary>
/// <remarks>
/// A search has limits, so that a pathological regular expression such as <c>(a+)+$</c>, or a query that matches
/// everywhere, can't keep a thread busy or fill the memory: <see cref="TimeLimit"/> and <see cref="MaxMatches"/>.
/// Measured on 5,000 lines of JSON (SearchBenchmarks and ReplaceAllBenchmarks, i7-13700): a word on every line, 5,000
/// matches, takes 1.3 ms; a regular expression with 10,000 matches 2.4 ms, and 0.3 ms with none. Each match kept takes
/// about 140 bytes while searching. Replacing 10,000 matches takes the UI thread about 80 ms, with the frame after it.
/// </remarks>
internal static class TextSearch
{
    /// <summary>
    /// The most matches a search keeps, and the editor highlights, as VS Code's <c>MATCHES_LIMIT</c>: about 2.8 MB
    /// while searching. Replace All replaces all matches, also beyond it (<see cref="ReplaceAll"/>).
    /// </summary>
    public const int MaxMatches = 19_999;

    /// <summary>
    /// How long a search may take: about 400 times what 10,000 matches in 5,000 lines take, so that a slower machine
    /// or a costly expression still finishes. One that takes longer backtracks catastrophically, as <c>(a+)+$</c> does.
    /// A single match can run for as long again before the regular expression's own timeout stops it.
    /// </summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(1);

    /// <summary>The longest selection that the search takes as its query, as in VS Code.</summary>
    public const int MaxQueryLength = 524_288;

    private static readonly SearchValues<char> s_lineBreaks = SearchValues.Create("\r\n");

    /// <summary>
    /// A regular expression that matches <paramref name="text"/>, as VS Code escapes text for its query: with a
    /// backslash before each of <c>\ { } * + ? | ^ $ . [ ] ( )</c>.
    /// </summary>
    public static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '\\' or '{' or '}' or '*' or '+' or '?' or '|' or '^' or '$' or '.' or '[' or ']' or '(' or ')')
                escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    /// <summary>
    /// Finds the matches of <paramref name="query"/> in <paramref name="snapshot"/>, at most <see cref="MaxMatches"/>,
    /// with the text that replaces each one when <paramref name="replacement"/> is given (<see cref="Replacing"/>).
    /// </summary>
    /// <param name="snapshot">The text.</param>
    /// <param name="query">What to search for.</param>
    /// <param name="replacement">What replaces the matches; null when the search is not for replacing.</param>
    /// <param name="scopes">
    /// The ranges to search in, as VS Code's Find in Selection; null for all of the text. A match lies inside one
    /// range, which counts as its whole text: <c>^</c> matches at its start, as in VS Code.
    /// </param>
    /// <param name="cancellationToken">Stops the search.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    public static SearchResult Find(
        TextSnapshot snapshot,
        SearchQuery query,
        SearchReplacement? replacement,
        IReadOnlyList<TextRange>? scopes,
        CancellationToken cancellationToken
    )
    {
        var matches = new TextSegmentCollection<SearchMatch>();
        if (!TryCreateRegex(query, out Regex? regex, out string? error))
        {
            return new SearchResult(snapshot.Version, query, replacement, matches, SearchOutcome.InvalidPattern, error);
        }
        string text = snapshot.GetText();
        Replacing? replacing = replacement is null ? null : new Replacing(text, query, replacement);
        SearchOutcome outcome = Scan(
            text,
            query,
            regex,
            scopes,
            (start, length, match, lineBreak) =>
            {
                if (matches.Count == MaxMatches)
                    return false;
                matches.Add(
                    new SearchMatch(replacing?.Build(start, match, lineBreak) ?? "")
                    {
                        StartOffset = start,
                        Length = length,
                    }
                );
                return true;
            },
            cancellationToken
        );
        return new SearchResult(snapshot.Version, query, replacement, matches, outcome, null);
    }

    /// <summary>
    /// The edit that replaces every match of <paramref name="query"/> in <paramref name="snapshot"/>, however many
    /// there are, in one change, as VS Code's Replace All does beyond the matches it highlights. Null when there is no
    /// match.
    /// </summary>
    /// <returns>
    /// The edit, unless the search ran out of time or the regular expression is invalid: then null, with the outcome.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    public static (SearchEdit? Edit, SearchOutcome Outcome) ReplaceAll(
        TextSnapshot snapshot,
        SearchQuery query,
        SearchReplacement replacement,
        IReadOnlyList<TextRange>? scopes,
        CancellationToken cancellationToken
    )
    {
        if (!TryCreateRegex(query, out Regex? regex, out _))
            return (null, SearchOutcome.InvalidPattern);
        string text = snapshot.GetText();
        var replacing = new Replacing(text, query, replacement);
        var replaced = new StringBuilder();
        var map = new OffsetChangeMap();
        int first = -1;
        int copied = 0;
        int count = 0;
        SearchOutcome outcome = Scan(
            text,
            query,
            regex,
            scopes,
            (start, length, match, lineBreak) =>
            {
                if (first < 0)
                    first = copied = start;
                string value = replacing.Build(start, match, lineBreak);
                // Where the match is once the replacements before it are made.
                map.Add(new OffsetChangeMapEntry(first + replaced.Length + start - copied, length, value.Length));
                replaced.Append(text, copied, start - copied).Append(value);
                copied = start + length;
                count++;
                return true;
            },
            cancellationToken
        );
        if (outcome != SearchOutcome.Complete || first < 0)
            return (null, outcome);
        return (new SearchEdit(first, copied - first, replaced.ToString(), map, count), outcome);
    }

    /// <summary>The query as a regular expression, across lines when it can match a line break.</summary>
    private static bool TryCreateRegex(
        SearchQuery query,
        [NotNullWhen(true)] out Regex? regex,
        [NotNullWhen(false)] out string? error
    )
    {
        RegexOptions options = RegexOptions.CultureInvariant;
        if (!query.MatchCase)
            options |= RegexOptions.IgnoreCase;
        // As VS Code's "m" flag: ^ and $ match at the line breaks inside the text searched.
        if (query.IsMultiline)
            options |= RegexOptions.Multiline;
        try
        {
            regex = new Regex(query.UseRegex ? query.Text : Regex.Escape(query.Text), options, TimeLimit);
            error = null;
            return true;
        }
        catch (ArgumentException e)
        {
            regex = null;
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Takes a match: its start and length in the text, the regular expression's match, and the line break of its
    /// line when the search knows it, else null.
    /// </summary>
    /// <returns>Whether the search goes on.</returns>
    private delegate bool MatchFound(int start, int length, Match match, string? lineBreak);

    /// <summary>Finds the matches in <paramref name="text"/>, range by range, in order.</summary>
    /// <returns>
    /// <see cref="SearchOutcome.Complete"/>, <see cref="SearchOutcome.OutOfTime"/>, or
    /// <see cref="SearchOutcome.TooManyMatches"/> when <paramref name="found"/> stopped the search.
    /// </returns>
    private static SearchOutcome Scan(
        string text,
        SearchQuery query,
        Regex regex,
        IReadOnlyList<TextRange>? scopes,
        MatchFound found,
        CancellationToken cancellationToken
    )
    {
        var clock = new Clock(Stopwatch.GetTimestamp(), cancellationToken);
        try
        {
            foreach ((int start, int end) in Ranges(text.Length, scopes))
            {
                SearchOutcome outcome = query.IsMultiline
                    ? ScanAcrossLines(text, start, end, query, regex, found, clock)
                    : ScanLines(text, start, end, query, regex, found, clock);
                if (outcome != SearchOutcome.Complete)
                    return outcome;
            }
            return SearchOutcome.Complete;
        }
        catch (RegexMatchTimeoutException)
        {
            return SearchOutcome.OutOfTime;
        }
    }

    /// <summary>
    /// The ranges to search: all of the text, or the scopes within it, in order, those that overlap merged, as in
    /// VS Code.
    /// </summary>
    private static List<(int Start, int End)> Ranges(int length, IReadOnlyList<TextRange>? scopes)
    {
        if (scopes is null)
            return [(0, length)];
        var ranges = new List<(int Start, int End)>();
        foreach (TextRange scope in scopes.OrderBy(scope => scope.Start).ThenBy(scope => scope.End))
        {
            int start = Math.Clamp(scope.Start, 0, length);
            int end = Math.Clamp(scope.End, start, length);
            if (ranges.Count > 0 && ranges[^1].End > start)
                ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, end));
            else
                ranges.Add((start, end));
        }
        return ranges;
    }

    /// <summary>Finds the matches line by line, each line from its start to its end within the range.</summary>
    private static SearchOutcome ScanLines(
        string text,
        int start,
        int end,
        SearchQuery query,
        Regex regex,
        MatchFound found,
        Clock clock
    )
    {
        // As AvaloniaEdit's new lines take it: the line's own, else the line's before, else the system's.
        string? lineBreak = null;
        for (int lineStart = start; ; )
        {
            int lineEnd = text.AsSpan(lineStart, end - lineStart).IndexOfAny(s_lineBreaks) is var i and >= 0
                ? lineStart + i
                : end;
            // The line's own line break, also where the range ends inside the line.
            int lineBreakAt = lineEnd < end ? lineEnd : IndexOfLineBreak(text, end);
            lineBreak =
                lineBreakAt >= 0 ? LineBreakAt(text, lineBreakAt) : lineBreak ?? LineBreakBefore(text, lineStart);
            SearchOutcome outcome = ScanText(text, lineStart, lineEnd, query, regex, found, lineBreak, null, clock);
            if (outcome != SearchOutcome.Complete)
                return outcome;
            if (clock.IsOutOfTime())
                return SearchOutcome.OutOfTime;
            if (lineEnd == end)
                return SearchOutcome.Complete;
            lineStart = lineEnd + LineBreakAt(text, lineEnd).Length;
        }
    }

    /// <summary>
    /// Finds the matches across lines, as VS Code does: in the range's text with its line breaks as <c>\n</c>, so that
    /// <c>\n</c> matches every line break, and their places in the text.
    /// </summary>
    private static SearchOutcome ScanAcrossLines(
        string text,
        int start,
        int end,
        SearchQuery query,
        Regex regex,
        MatchFound found,
        Clock clock
    )
    {
        if (!text.AsSpan(start, end - start).Contains('\r'))
            return ScanText(text, start, end, query, regex, found, null, null, clock);
        LineFeedText joined = LineFeedText.Of(text, start, end);
        return ScanText(joined.Text, 0, joined.Text.Length, query, regex, found, null, joined, clock);
    }

    /// <summary>
    /// Finds the matches in the text from <paramref name="start"/> to <paramref name="end"/> of
    /// <paramref name="input"/>, which counts as all of the text: <c>^</c> matches at its start, and Match Whole Word
    /// takes its ends for word boundaries, as in VS Code.
    /// </summary>
    /// <param name="input">The text searched: the snapshot's, or the range's with its line breaks as <c>\n</c>.</param>
    /// <param name="start">Where the text searched starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="query">The query.</param>
    /// <param name="regex">The query's regular expression.</param>
    /// <param name="found">Takes the matches.</param>
    /// <param name="lineBreak">The line break of the line searched; null for a search across lines.</param>
    /// <param name="joined">Where <paramref name="input"/> is the range's text with its line breaks as <c>\n</c>.</param>
    /// <param name="clock">The search's time.</param>
    private static SearchOutcome ScanText(
        string input,
        int start,
        int end,
        SearchQuery query,
        Regex regex,
        MatchFound found,
        string? lineBreak,
        LineFeedText? joined,
        Clock clock
    )
    {
        for (Match match = regex.Match(input, start, end - start); match.Success; match = match.NextMatch())
        {
            // Every match counts, also one that Match Whole Word rejects: each may have taken long to find.
            if (clock.IsOutOfTime())
                return SearchOutcome.OutOfTime;
            int matchEnd = match.Index + match.Length;
            if (!query.WholeWord || IsWholeWord(input, start, end, match.Index, match.Length))
            {
                bool goesOn = joined is null
                    ? found(match.Index, match.Length, match, lineBreak)
                    : found(
                        joined.ToText(match.Index),
                        joined.ToText(matchEnd) - joined.ToText(match.Index),
                        match,
                        lineBreak
                    );
                if (!goesOn)
                    return SearchOutcome.TooManyMatches;
            }
            // As VS Code's search: a match that ends at the end of the text is the last, also before an empty match.
            if (matchEnd == end)
                break;
        }
        return SearchOutcome.Complete;
    }

    /// <summary>Where the first line break at or after <paramref name="offset"/> starts; -1 for none.</summary>
    private static int IndexOfLineBreak(string text, int offset) =>
        text.AsSpan(offset).IndexOfAny(s_lineBreaks) is var i and >= 0 ? offset + i : -1;

    /// <summary>The line break that starts at <paramref name="offset"/>.</summary>
    private static string LineBreakAt(string text, int offset) =>
        text[offset] == '\n' ? "\n"
        : offset + 1 < text.Length && text[offset + 1] == '\n' ? "\r\n"
        : "\r";

    /// <summary>The line break before the line of <paramref name="offset"/>, else the system's.</summary>
    private static string LineBreakBefore(string text, int offset)
    {
        int last = text.AsSpan(0, offset).LastIndexOfAny(s_lineBreaks);
        if (last < 0)
            return Environment.NewLine;
        return text[last] == '\n' && last > 0 && text[last - 1] == '\r' ? "\r\n" : text[last].ToString();
    }

    /// <summary>
    /// The line break of the line of <paramref name="offset"/>: its own, else the line's before, else the system's.
    /// </summary>
    private static string LineBreakOfLine(string text, int offset) =>
        IndexOfLineBreak(text, offset) is var i and >= 0 ? LineBreakAt(text, i) : LineBreakBefore(text, offset);

    /// <summary>
    /// Whether a match starts and ends at word boundaries, as VS Code decides: next to whitespace, a word separator or
    /// the end of the text searched, or with such a character at its own edge.
    /// </summary>
    private static bool IsWholeWord(string text, int textStart, int textEnd, int start, int length)
    {
        int end = start + length;
        bool left =
            start == textStart
            || !WordCharacters.IsWordCharacter(text[start - 1])
            || (length > 0 && !WordCharacters.IsWordCharacter(text[start]));
        bool right =
            end == textEnd
            || !WordCharacters.IsWordCharacter(text[end])
            || (length > 0 && !WordCharacters.IsWordCharacter(text[end - 1]));
        return left && right;
    }

    /// <summary>The time a search has, and its cancellation.</summary>
    private readonly record struct Clock(long Start, CancellationToken CancellationToken)
    {
        /// <summary>Whether the search took its time; throws once it is cancelled.</summary>
        public bool IsOutOfTime()
        {
            CancellationToken.ThrowIfCancellationRequested();
            return Stopwatch.GetElapsedTime(Start) >= TimeLimit;
        }
    }

    /// <summary>
    /// Builds the text that replaces each match: the replacement itself, or for a regular expression with its
    /// substitutions in VS Code's syntax (<see cref="ReplacePattern"/>), such as <c>$1</c> or <c>\U$1</c>, in the case of
    /// the match with Preserve Case. Its line breaks are those of the match's line, as VS Code makes them its text's.
    /// </summary>
    private sealed class Replacing(string text, SearchQuery query, SearchReplacement replacement)
    {
        private readonly ReplacePattern _pattern = query.UseRegex
            ? ReplacePattern.Parse(replacement.Text)
            : ReplacePattern.Static(replacement.Text);

        /// <summary>The text that replaces the match at <paramref name="start"/>.</summary>
        public string Build(int start, Match match, string? lineBreak) =>
            TextUtilities.NormalizeNewLines(
                _pattern.Build(match, replacement.PreserveCase),
                lineBreak ?? LineBreakOfLine(text, start)
            );
    }

    /// <summary>
    /// A range of the text with its line breaks as <c>\n</c>, as VS Code searches across lines, and where its offsets
    /// are in the text: before each <c>\r\n</c> that became <c>\n</c>, one more.
    /// </summary>
    private sealed class LineFeedText
    {
        /// <summary>Where the range starts in the text.</summary>
        private readonly int _start;

        /// <summary>The offsets in <see cref="Text"/> of the <c>\n</c> of each <c>\r\n</c>, in order.</summary>
        private readonly List<int> _joined;

        private LineFeedText(string text, int start, List<int> joined)
        {
            Text = text;
            _start = start;
            _joined = joined;
        }

        public string Text { get; }

        public static LineFeedText Of(string text, int start, int end)
        {
            var joined = new List<int>();
            var builder = new StringBuilder(end - start);
            for (int i = start; i < end; i++)
            {
                if (text[i] != '\r')
                {
                    builder.Append(text[i]);
                    continue;
                }
                if (i + 1 < end && text[i + 1] == '\n')
                {
                    joined.Add(builder.Length);
                    i++;
                }
                builder.Append('\n');
            }
            return new LineFeedText(builder.ToString(), start, joined);
        }

        /// <summary>
        /// The offset in the text of <paramref name="offset"/> of <see cref="Text"/>: a match that starts at a joined
        /// <c>\n</c> starts at its <c>\r</c>, and one that ends after it ends after it, as in VS Code.
        /// </summary>
        public int ToText(int offset)
        {
            int before = _joined.BinarySearch(offset);
            return _start + offset + (before < 0 ? ~before : before);
        }
    }
}

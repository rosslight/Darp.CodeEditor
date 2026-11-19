using System.Text.RegularExpressions;
using AvaloniaEdit.Document;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Search;

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
/// Finds the matches of a query in a snapshot, as VS Code's <c>TextModelSearch</c> does, and its <c>findNextMatch</c>,
/// <c>findPreviousMatch</c> and Replace All beyond the matches it keeps: by code points, so that no
/// match starts or ends inside a surrogate pair. A query that can't match a line break is searched line by line, so
/// that <c>^</c> and <c>$</c> match at every line's start and end, whatever its line break. One that can
/// (<see cref="SearchQuery.IsMultiline"/>) is searched across lines, in the text with its line breaks as <c>\n</c>.
/// Runs in the background.
/// </summary>
/// <remarks>
/// A search has limits, so that a pathological regular expression such as <c>(a+)+$</c>, or a query that matches
/// everywhere, can't keep a thread busy or fill the memory: <see cref="TimeLimit"/> and <see cref="MaxMatches"/>.
/// Measured on 5,000 lines of JSON (SearchBenchmarks, i7-13700): a word on every line, 5,000 matches, takes 1.3 ms; a
/// regular expression with 10,000 matches 2.3 ms, and 0.2 ms with none; one across the end of every line 1.1 ms, or
/// 1.9 ms in lines apart by <c>\r\n</c>. A search allocates about 280 bytes per match.
/// </remarks>
internal static class TextSearch
{
    /// <summary>
    /// The most matches a search keeps, and the editor highlights, VS Code's <c>MATCHES_LIMIT</c>: about 5.6 MB.
    /// Beyond them, <see cref="FindNext"/>, <see cref="FindPrevious"/> and <see cref="ReplaceAll"/> reach every match.
    /// </summary>
    public const int MaxMatches = 19_999;

    /// <summary>
    /// How long a search may take: about 400 times what 10,000 matches in 5,000 lines take, so that a slower machine
    /// or a costly expression still finishes. One that takes longer backtracks catastrophically, as <c>(a+)+$</c> does.
    /// A single match can run for as long again before the regular expression's own timeout stops it.
    /// </summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Finds the matches of <paramref name="query"/> in <paramref name="snapshot"/>, at most <see cref="MaxMatches"/>,
    /// with the text that replaces each one when <paramref name="replacement"/> is given (<see cref="Replacing"/>).
    /// </summary>
    /// <param name="snapshot">The text.</param>
    /// <param name="query">What to search for.</param>
    /// <param name="replacement">What replaces the matches; null when the search is not for replacing.</param>
    /// <param name="scope">
    /// The ranges to search in, for Find in Selection; null for all of the text. A match lies inside one range, which
    /// counts as its whole text: <c>^</c> matches at its start.
    /// </param>
    /// <param name="cancellationToken">Stops the search.</param>
    /// <param name="clock">The clock of the time limit, the document's; the system's for null.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    public static SearchResult Find(
        TextSnapshot snapshot,
        SearchQuery query,
        SearchReplacement? replacement,
        IReadOnlyList<TextRange>? scope,
        CancellationToken cancellationToken,
        TimeProvider? clock = null
    )
    {
        var matches = new TextSegmentCollection<SearchMatch>();
        string text = snapshot.GetText();
        if (!query.TryCreateRegex(HasSurrogates(text), out Regex? regex, out string? error))
            return new SearchResult(snapshot.Version, query, replacement, matches, SearchOutcome.InvalidPattern, error);
        Replacing? replacing = replacement is null ? null : new Replacing(query, replacement, snapshot.LineBreak);
        SearchOutcome outcome = new SearchScan(
            text,
            query,
            regex,
            (start, length, match) =>
            {
                if (matches.Count == MaxMatches)
                    return false;
                matches.Add(new SearchMatch(replacing?.Build(match) ?? "") { StartOffset = start, Length = length });
                return true;
            },
            clock ?? TimeProvider.System,
            cancellationToken
        ).Scan(scope);
        return new SearchResult(snapshot.Version, query, replacement, matches, outcome, null);
    }

    /// <summary>
    /// The edit that replaces every match of <paramref name="query"/> in <paramref name="snapshot"/>, however many
    /// there are, also beyond the matches that a search keeps: one change of the text, combined here, off the UI
    /// thread, for <see cref="TextChanges.Apply(CombinedEdit)"/>.
    /// </summary>
    /// <returns>
    /// The edit, null without a match; null also where the search ran out of time or the regular expression is
    /// invalid, with that outcome.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    public static (CombinedEdit? Edit, SearchOutcome Outcome) ReplaceAll(
        TextSnapshot snapshot,
        SearchQuery query,
        SearchReplacement replacement,
        IReadOnlyList<TextRange>? scope,
        CancellationToken cancellationToken,
        TimeProvider? clock = null
    )
    {
        string text = snapshot.GetText();
        if (!query.TryCreateRegex(HasSurrogates(text), out Regex? regex, out _))
            return (null, SearchOutcome.InvalidPattern);
        var replacing = new Replacing(query, replacement, snapshot.LineBreak);
        var edits = new List<TextEdit>();
        SearchOutcome outcome = new SearchScan(
            text,
            query,
            regex,
            (start, length, match) =>
            {
                edits.Add(new TextEdit(start, length, replacing.Build(match)));
                return true;
            },
            clock ?? TimeProvider.System,
            cancellationToken
        ).Scan(scope);
        CombinedEdit? edit =
            outcome == SearchOutcome.Complete && edits.Count > 0
                ? CombinedEdit.Of(new StringTextSource(text), edits)
                : null;
        return (edit, outcome);
    }

    /// <summary>
    /// The first match that starts at <paramref name="from"/> or after it, searched from there, also beyond those that
    /// a search keeps (<see cref="MaxMatches"/>), with what replaces it; with <paramref name="skipEmpty"/>, not an
    /// empty match at <paramref name="from"/>, the current one. It does not go round to the start.
    /// </summary>
    /// <returns>The match, null for none, and whether the search ran out of time.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    public static (SearchMatch? Match, SearchOutcome Outcome) FindNext(
        TextSnapshot snapshot,
        SearchQuery query,
        SearchReplacement? replacement,
        IReadOnlyList<TextRange>? scope,
        int from,
        bool skipEmpty,
        CancellationToken cancellationToken,
        TimeProvider? clock = null
    )
    {
        string text = snapshot.GetText();
        if (!query.TryCreateRegex(HasSurrogates(text), out Regex? regex, out _))
            return (null, SearchOutcome.InvalidPattern);
        from = Math.Clamp(from, 0, text.Length);
        if (SearchScan.IsInsideLineBreak(text, from))
            from++;
        Replacing? replacing = replacement is null ? null : new Replacing(query, replacement, snapshot.LineBreak);
        SearchMatch? next = null;
        SearchOutcome outcome = new SearchScan(
            text,
            query,
            regex,
            (start, length, match) =>
            {
                if (start < from || (skipEmpty && length == 0 && start == from))
                    return true;
                next = new SearchMatch(replacing?.Build(match) ?? "") { StartOffset = start, Length = length };
                return false;
            },
            clock ?? TimeProvider.System,
            cancellationToken
        ).Scan(scope, from);
        return (next, outcome == SearchOutcome.OutOfTime ? outcome : SearchOutcome.Complete);
    }

    /// <summary>
    /// The last match that starts before <paramref name="before"/>, also beyond those that a search keeps: searched
    /// from <paramref name="from"/>, the start of a match that a search of the same text and query found, on to
    /// <paramref name="before"/>, or with <see cref="int.MaxValue"/> to the end, for the last match of all.
    /// </summary>
    /// <returns>The match, null for none, and whether the search ran out of time.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    public static (SearchMatch? Match, SearchOutcome Outcome) FindPrevious(
        TextSnapshot snapshot,
        SearchQuery query,
        IReadOnlyList<TextRange>? scope,
        int from,
        int before,
        CancellationToken cancellationToken,
        TimeProvider? clock = null
    )
    {
        string text = snapshot.GetText();
        if (!query.TryCreateRegex(HasSurrogates(text), out Regex? regex, out _))
            return (null, SearchOutcome.InvalidPattern);
        TextRange? last = null;
        SearchOutcome outcome = new SearchScan(
            text,
            query,
            regex,
            (start, length, _) =>
            {
                if (start >= before)
                    return false;
                last = new TextRange(start, length);
                return true;
            },
            clock ?? TimeProvider.System,
            cancellationToken
        ).Scan(scope, Math.Clamp(from, 0, text.Length));
        SearchMatch? previous = last is { } found
            ? new SearchMatch("") { StartOffset = found.Start, Length = found.Length }
            : null;
        return (previous, outcome == SearchOutcome.OutOfTime ? outcome : SearchOutcome.Complete);
    }

    /// <summary>Whether the text has surrogates, which a regular expression's atoms must not split.</summary>
    private static bool HasSurrogates(string text) => text.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF') >= 0;

    /// <summary>
    /// Builds the text that replaces each match: the replacement itself, or for a regular expression with its
    /// substitutions (<see cref="ReplacePattern"/>), such as <c>$1</c> or <c>\U$1</c>, in the case of the match with
    /// Preserve Case. Its line breaks are the document's, once it is expanded, as a text model makes those of the text
    /// that goes in: <c>\r$1\n</c> is one line break when <c>$1</c> is empty.
    /// </summary>
    private sealed class Replacing
    {
        private readonly ReplacePattern _pattern;
        private readonly bool _preserveCase;
        private readonly string _lineBreak;

        public Replacing(SearchQuery query, SearchReplacement replacement, string lineBreak)
        {
            _pattern = query.UseRegex
                ? ReplacePattern.Parse(replacement.Text)
                : ReplacePattern.Static(replacement.Text);
            _preserveCase = replacement.PreserveCase;
            _lineBreak = lineBreak;
        }

        /// <summary>The text that replaces <paramref name="match"/>.</summary>
        public string Build(Match match)
        {
            string text = _pattern.Build(match, _preserveCase);
            return text.AsSpan().IndexOfAny('\r', '\n') < 0 ? text : TextUtilities.NormalizeNewLines(text, _lineBreak);
        }
    }
}

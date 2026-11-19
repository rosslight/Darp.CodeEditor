using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Search;

/// <summary>Takes a match: its start and length in the text, and the regular expression's match.</summary>
/// <returns>Whether the search goes on.</returns>
internal delegate bool MatchFound(int start, int length, Match match);

/// <summary>
/// One search of a text for a query, for <see cref="TextSearch"/>: it finds the matches range by range, in order, by
/// code points, and stops once it takes its time (<see cref="TextSearch.TimeLimit"/>) or is cancelled.
/// </summary>
internal sealed class SearchScan
{
    private static readonly SearchValues<char> s_lineBreaks = SearchValues.Create("\r\n");

    private readonly string _text;
    private readonly SearchQuery _query;
    private readonly Regex _regex;
    private readonly MatchFound _found;
    private readonly CancellationToken _cancellationToken;
    private readonly long _start = Stopwatch.GetTimestamp();

    /// <summary>Whether the regular expression has groups, whose bounds a match must not put inside a pair.</summary>
    private readonly bool _hasGroups;

    public SearchScan(
        string text,
        SearchQuery query,
        Regex regex,
        MatchFound found,
        CancellationToken cancellationToken
    )
    {
        _text = text;
        _query = query;
        _regex = regex;
        _found = found;
        _cancellationToken = cancellationToken;
        _hasGroups = regex.GetGroupNumbers().Length > 1;
    }

    /// <summary>
    /// Finds the matches in the ranges of <paramref name="scope"/>, or all of the text, from <paramref name="from"/>
    /// on: there the regular expression starts, as in the middle of a line or of a range searched across lines.
    /// </summary>
    /// <returns>
    /// <see cref="SearchOutcome.Complete"/>, <see cref="SearchOutcome.OutOfTime"/>, or
    /// <see cref="SearchOutcome.TooManyMatches"/> when the one that takes the matches stopped the search.
    /// </returns>
    public SearchOutcome Scan(IReadOnlyList<TextRange>? scope, int from = 0)
    {
        bool acrossLines = _query.IsMultiline;
        try
        {
            foreach ((int start, int end) in Ranges(scope))
            {
                if (end < from)
                    continue;
                int at = Math.Max(start, from);
                SearchOutcome outcome = acrossLines ? AcrossLines(start, end, at) : Lines(start, end, at);
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
    /// The ranges to search: all of the text, or the scope's ranges within it, in order, those that overlap merged,
    /// A range that an edit left starting or ending between a <c>\r</c> and its <c>\n</c> starts
    /// after the line break, or ends before it.
    /// </summary>
    private List<(int Start, int End)> Ranges(IReadOnlyList<TextRange>? scope)
    {
        int length = _text.Length;
        if (scope is null)
            return [(0, length)];
        var ranges = new List<(int Start, int End)>();
        foreach (TextRange range in scope.OrderBy(range => range.Start).ThenBy(range => range.End))
        {
            int start = Math.Clamp(range.Start, 0, length);
            int end = Math.Clamp(range.End, start, length);
            if (IsInsideLineBreak(_text, start))
                start++;
            if (IsInsideLineBreak(_text, end))
                end--;
            end = Math.Max(start, end);
            if (ranges.Count > 0 && ranges[^1].End > start)
                ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, end));
            else
                ranges.Add((start, end));
        }
        return ranges;
    }

    /// <summary>
    /// Finds the matches line by line, each line from its start to its end within the range, from the line of
    /// <paramref name="from"/> on, and in it from there.
    /// </summary>
    private SearchOutcome Lines(int start, int end, int from)
    {
        int lineStart = _text.AsSpan(start, from - start).LastIndexOfAny(s_lineBreaks) is var last and >= 0
            ? start + last + 1
            : start;
        while (true)
        {
            int lineEnd = _text.AsSpan(lineStart, end - lineStart).IndexOfAny(s_lineBreaks) is var i and >= 0
                ? lineStart + i
                : end;
            SearchOutcome outcome = Within(_text, lineStart, lineEnd, Math.Max(lineStart, from), null);
            if (outcome != SearchOutcome.Complete)
                return outcome;
            if (IsOutOfTime())
                return SearchOutcome.OutOfTime;
            if (lineEnd == end)
                return SearchOutcome.Complete;
            lineStart = Math.Min(lineEnd + LineBreakLength(_text, lineEnd), end);
        }
    }

    /// <summary>
    /// Finds the matches across lines: in the range's text with its line breaks as <c>\n</c>, so
    /// that <c>\n</c> matches every line break, and their places in the text.
    /// </summary>
    private SearchOutcome AcrossLines(int start, int end, int from)
    {
        if (!_text.AsSpan(start, end - start).Contains('\r'))
            return Within(_text, start, end, from, null);
        LineFeedText joined = LineFeedText.Of(_text, start, end);
        return Within(joined.Text, 0, joined.Text.Length, joined.FromText(from), joined);
    }

    /// <summary>
    /// Finds the matches in <paramref name="input"/> from <paramref name="start"/> to <paramref name="end"/>, which
    /// count as all of the text: <c>^</c> matches at its start, and Match Whole Word takes its ends for word
    /// boundaries. The regular expression starts at <paramref name="from"/>.
    /// </summary>
    /// <param name="input">The text, or the range's with its line breaks as <c>\n</c>.</param>
    /// <param name="start">Where the text searched starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="from">Where the regular expression starts, within the text searched.</param>
    /// <param name="joined">
    /// Where <paramref name="input"/> is the range's text with its line breaks as <c>\n</c>.
    /// </param>
    private SearchOutcome Within(string input, int start, int end, int from, LineFeedText? joined)
    {
        // The text that the regular expression searches, and where it starts in the input: the input, or once a
        // search starts after the start of the text searched, that text alone, as a start of its own needs.
        string searched = input;
        int shift = 0;
        Match match = from <= start ? _regex.Match(input, start, end - start) : MatchFrom(from);
        while (match.Success)
        {
            // Every match counts, also one that Match Whole Word rejects: each may have taken long to find.
            if (IsOutOfTime())
                return SearchOutcome.OutOfTime;
            int index = shift + match.Index;
            int matchEnd = index + match.Length;
            // By code points: no match starts inside a surrogate pair, and the next try starts after it, as VS
            // Code steps past an empty match.
            if (IsInsideSurrogatePair(input, index))
            {
                if (index >= end)
                    break;
                match = MatchFrom(index + 1);
                continue;
            }
            if (
                !SplitsAPair(input, shift, match)
                && (!_query.WholeWord || IsWholeWord(input, start, end, index, match.Length))
            )
            {
                bool goesOn = joined is null
                    ? _found(index, match.Length, match)
                    : _found(joined.ToText(index), joined.ToText(matchEnd) - joined.ToText(index), match);
                if (!goesOn)
                    return SearchOutcome.TooManyMatches;
            }
            // A match that ends at the end of the text is the last, also before an empty one there.
            if (matchEnd == end)
                break;
            match = match.NextMatch();
        }
        return SearchOutcome.Complete;

        Match MatchFrom(int offset)
        {
            if (ReferenceEquals(searched, input) && (start > 0 || end < input.Length))
                (searched, shift) = (input[start..end], start);
            return _regex.Match(searched, offset - shift);
        }
    }

    /// <summary>Whether the search took its time; throws once it is cancelled.</summary>
    private bool IsOutOfTime()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return Stopwatch.GetElapsedTime(_start) >= TextSearch.TimeLimit;
    }

    /// <summary>Whether <paramref name="offset"/> is between a <c>\r</c> and its <c>\n</c>.</summary>
    public static bool IsInsideLineBreak(string text, int offset) =>
        offset > 0 && offset < text.Length && text[offset - 1] == '\r' && text[offset] == '\n';

    /// <summary>
    /// Whether the match ends, or one of its groups starts or ends, between the two halves of a surrogate pair, where a
    /// replacement would split it.
    /// </summary>
    private bool SplitsAPair(string input, int shift, Match match)
    {
        if (IsInsideSurrogatePair(input, shift + match.Index + match.Length))
            return true;
        if (!_hasGroups)
            return false;
        foreach (Group group in match.Groups)
        {
            int groupStart = shift + group.Index;
            if (
                group.Success
                && (IsInsideSurrogatePair(input, groupStart) || IsInsideSurrogatePair(input, groupStart + group.Length))
            )
                return true;
        }
        return false;
    }

    /// <summary>Whether <paramref name="offset"/> is between the two halves of a surrogate pair.</summary>
    private static bool IsInsideSurrogatePair(string text, int offset) =>
        offset > 0
        && offset < text.Length
        && char.IsHighSurrogate(text[offset - 1])
        && char.IsLowSurrogate(text[offset]);

    /// <summary>The length of the line break that starts at <paramref name="offset"/>.</summary>
    private static int LineBreakLength(string text, int offset) =>
        text[offset] == '\r' && offset + 1 < text.Length && text[offset + 1] == '\n' ? 2 : 1;

    /// <summary>
    /// Whether a match starts and ends at word boundaries: next to whitespace, a word separator or
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

    /// <summary>
    /// A range of the text with its line breaks as <c>\n</c>, as a search across lines reads it, and where its offsets
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
        /// The offset in <see cref="Text"/> of <paramref name="offset"/> of the text: of a <c>\r\n</c>, or between its
        /// two, that of its <c>\n</c>.
        /// </summary>
        public int FromText(int offset)
        {
            // How many \r\n start before the offset, as the offsets of their \r grow.
            int low = 0;
            int high = _joined.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (_start + _joined[middle] + middle < offset)
                    low = middle + 1;
                else
                    high = middle;
            }
            return offset - _start - low;
        }

        /// <summary>
        /// The offset in the text of <paramref name="offset"/> of <see cref="Text"/>: a match that starts at a joined
        /// <c>\n</c> starts at its <c>\r</c>, and one that ends after it ends after it.
        /// </summary>
        public int ToText(int offset)
        {
            int before = _joined.BinarySearch(offset);
            return _start + offset + (before < 0 ? ~before : before);
        }
    }
}

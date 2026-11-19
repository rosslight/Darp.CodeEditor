namespace Darp.CodeEditor.Lsp;

/// <summary>
/// Ranks completion items against the text typed before the caret, as VS Code's suggest widget does by default: the
/// items whose filter text matches, best first, with the characters of each label that matched. The editor and the C#
/// script server share it, so that they rank alike.
/// </summary>
/// <remarks>
/// <para>
/// A match is fuzzy: the typed characters appear in the filter text in order, ignoring case, and the first of them at a
/// word start, a capital of a camel-case word, or after a separator. Scores favour a common prefix, matching case,
/// contiguous characters and word starts, and penalise skipped characters. Equal scores keep the order of the items,
/// which <see cref="CompletionSortKey"/> sorts as the server's sort texts say. Nothing typed matches every item with
/// the same score.
/// </para>
/// <para>
/// Lists of up to <see cref="MaxGracefulItems"/> items tolerate a typo: an item that the typed text does not match
/// also matches with two neighbouring characters of it swapped, after the first character and up to the seventh and
/// eighth, at a cost of 3, the best swap counting, so that "prnit" matches "print". Longer lists match strictly, as
/// each swap is another match.
/// </para>
/// <para>
/// One ranking fills at most <see cref="MaxCellsPerRank"/> cells of the scoring tables, as many as some 12 ms take:
/// matching 2,000 labels of 128 characters against 63 typed characters fills about 8 million. Past them, an item's
/// score is that of the way that one pass finds, which still tolerates a typo (<see cref="FuzzyScorer"/>).
/// </para>
/// <para>
/// This is a port of VS Code's <c>fuzzyScore</c>, <c>fuzzyScoreGraceful</c> and <c>anyScore</c>
/// (<c>src/vs/base/common/filters.ts</c>, 1.145.0, MIT license), with its default options: the first character must
/// match strongly, and a full match is boosted. Emoji are no separators. Texts are matched up to their first 128
/// characters. Case is ignored by .NET's invariant lower case, which differs from JavaScript's for the few letters
/// whose case depends on their context, such as the Greek final sigma.
/// </para>
/// <para>
/// An instance keeps the tables of the matching for reuse: use it on one thread at a time. They take about 150 KB.
/// </para>
/// </remarks>
internal sealed class CompletionMatcher
{
    /// <summary>
    /// The longest typed text and filter text that are matched; the rest is ignored. A caller that copies the typed
    /// text needs no more of it than this.
    /// </summary>
    public const int MaxLength = 128;

    /// <summary>The most items that <see cref="Rank"/> matches gracefully, tolerating a typo; longer lists strictly.</summary>
    public const int MaxGracefulItems = 2000;

    /// <summary>The most cells of the scoring tables that one ranking fills (<see cref="FuzzyScorer.CellsLeft"/>).</summary>
    public const long MaxCellsPerRank = 2_000_000;

    /// <summary>The score of an item when nothing is typed: below any score of a match.</summary>
    private const int DefaultScore = -100;

    private readonly FuzzyScorer _scorer = new();

    /// <summary>The cells of the scoring tables that one ranking fills at most.</summary>
    private readonly long _cellsPerRank;

    /// <param name="cellsPerRank">The cells of the scoring tables that one ranking fills at most.</param>
    public CompletionMatcher(long cellsPerRank = MaxCellsPerRank) => _cellsPerRank = cellsPerRank;

    /// <summary>
    /// The items that match, best first; equal scores keep the order of <paramref name="items"/>. Up to
    /// <see cref="MaxGracefulItems"/> items match gracefully. Matching a long list against a long typed text takes
    /// long, so it belongs off the UI thread; it stops soon once <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public List<RankedCompletion> Rank(
        IReadOnlyList<CompletionCandidate> items,
        CancellationToken cancellationToken = default
    )
    {
        bool graceful = items.Count <= MaxGracefulItems;
        var ranked = new List<RankedCompletion>();
        _scorer.CellsLeft = _cellsPerRank;
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                // An item takes up to some 50 µs: a check every 64 items keeps the wait short and its cost small.
                if (i % 64 == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                CompletionCandidate item = items[i];
                if (Match(item.Typed, item.Label, item.FilterText, item.FilterTextIsLabel, graceful) is { } match)
                    ranked.Add(new RankedCompletion(i, match.Score, match.Highlights));
            }
        }
        finally
        {
            _scorer.CellsLeft = long.MaxValue;
        }
        // A stable order: List.Sort is not stable, so the index decides between equal scores.
        ranked.Sort(static (a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Index.CompareTo(b.Index));
        return ranked;
    }

    /// <summary>
    /// How <paramref name="typed"/> matches an item: the score of its filter text, and the characters of its label
    /// that matched, ascending. Null when it does not match.
    /// </summary>
    /// <param name="typed">
    /// The text typed from where the item's edit starts up to the caret; only its first <see cref="MaxLength"/>
    /// characters are read.
    /// </param>
    /// <param name="label">The item's label, which shows.</param>
    /// <param name="filterText">The text the item is matched by: its label, unless the server gave another.</param>
    /// <param name="filterTextIsLabel">
    /// Whether the whole filter text equals the label, ignoring case: then the label shows the filter text's matched
    /// characters, as VS Code shows them; otherwise its own, by the most lenient score. The caller knows it from
    /// reading the item once, as comparing whole texts costs as much as they are long.
    /// </param>
    /// <param name="graceful">Whether a typo is tolerated, as in a list of up to <see cref="MaxGracefulItems"/>.</param>
    public (int Score, int[] Highlights)? Match(
        string typed,
        string label,
        string filterText,
        bool filterTextIsLabel,
        bool graceful = false
    )
    {
        // Whitespace that the item's edit starts with is not typed text.
        int start = 0;
        while (start < typed.Length && typed[start] is ' ' or '\t')
            start++;
        if (start == typed.Length)
            return (DefaultScore, []);
        string typedLow = Lower(typed);
        string filterTextLow = Lower(filterText);
        (int Score, int[] Positions)? scored = graceful
            ? GracefulScore(typed, typedLow, start, filterText, filterTextLow)
            : _scorer.Score(typed, typedLow, start, filterText, filterTextLow, firstMatchCanBeWeak: false);
        if (scored is not { } match)
            return null;
        if (filterTextIsLabel)
            return match;
        // The label shows: its characters that match, by the most lenient score, with the filter text's score.
        return (match.Score, AnyScore(typed, typedLow, start, label));
    }

    /// <summary>
    /// The first <see cref="MaxLength"/> characters of <paramref name="text"/>, all that matching reads, in lower case:
    /// a server's label of millions of characters costs no more than its start.
    /// </summary>
    private static string Lower(string text) =>
        text.Length <= MaxLength ? text.ToLowerInvariant() : text[..MaxLength].ToLowerInvariant();

    /// <summary>
    /// VS Code's <c>anyScore</c>: the characters of <paramref name="word"/> that match, also weakly and from a later
    /// typed character; none when nothing does.
    /// </summary>
    private int[] AnyScore(string pattern, string patternLow, int patternStart, string word)
    {
        string wordLow = Lower(word);
        int max = Math.Min(13, pattern.Length);
        for (int start = patternStart; start < max; start++)
        {
            if (_scorer.Score(pattern, patternLow, start, word, wordLow, firstMatchCanBeWeak: true) is { } match)
                return match.Positions;
        }
        return [];
    }

    /// <summary>
    /// VS Code's <c>fuzzyScoreGraceful</c>: <see cref="FuzzyScorer.Score"/> for <paramref name="pattern"/> as typed, or
    /// when that does not match, the best with two neighbouring characters swapped, one pair at a time, each swap
    /// costing 3; null when none matches.
    /// </summary>
    private (int Score, int[] Positions)? GracefulScore(
        string pattern,
        string patternLow,
        int patternStart,
        string word,
        string wordLow
    )
    {
        (int Score, int[] Positions)? top = _scorer.Score(
            pattern,
            patternLow,
            patternStart,
            word,
            wordLow,
            firstMatchCanBeWeak: false
        );
        if (top is not null || pattern.Length < 3)
            return top;
        // At most 7 tries, after the first character; VS Code counts them from the start of the whole pattern.
        int tries = Math.Min(7, pattern.Length - 1);
        for (int moving = patternStart + 1; moving < tries; moving++)
        {
            // Alike neighbours have nothing to swap.
            if (
                pattern[moving] != pattern[moving + 1]
                && _scorer.Score(
                    Swapped(pattern, moving),
                    Swapped(patternLow, moving),
                    patternStart,
                    word,
                    wordLow,
                    firstMatchCanBeWeak: false
                )
                    is { } candidate
                && (top is not { } best || candidate.Score - 3 > best.Score)
            )
            {
                top = (candidate.Score - 3, candidate.Positions);
            }
        }
        return top;
    }

    /// <summary><paramref name="text"/> with the characters at <paramref name="position"/> and after it swapped.</summary>
    private static string Swapped(string text, int position)
    {
        char[] swapped = text.ToCharArray();
        (swapped[position], swapped[position + 1]) = (swapped[position + 1], swapped[position]);
        return new string(swapped);
    }
}

/// <summary>An item to rank: the text typed for it, and its texts.</summary>
/// <param name="Typed">
/// The text typed from where the item's edit starts up to the caret; only its first
/// <see cref="CompletionMatcher.MaxLength"/> characters are read.
/// </param>
/// <param name="Label">The label, whose matched characters show.</param>
/// <param name="FilterText">The text it is matched by: its label, unless the server gave another.</param>
/// <param name="FilterTextIsLabel">Whether the whole filter text equals the label, ignoring case.</param>
internal readonly record struct CompletionCandidate(
    string Typed,
    string Label,
    string FilterText,
    bool FilterTextIsLabel
);

/// <summary>An item that matches.</summary>
/// <param name="Index">Its index in the ranked items.</param>
/// <param name="Score">How well it matches: higher is better.</param>
/// <param name="Highlights">The positions of its label's characters that matched, ascending.</param>
internal readonly record struct RankedCompletion(int Index, int Score, int[] Highlights);

/// <summary>
/// The order of a server's items before anything is typed, as VS Code sorts them: by sort text, ignoring case, then by
/// label, then by kind.
/// </summary>
internal readonly record struct CompletionSortKey : IComparable<CompletionSortKey>
{
    private readonly string _sortTextLow;
    private readonly string _label;
    private readonly int _kind;

    /// <param name="sortText">The item's sort text: its label, unless the server gave another.</param>
    /// <param name="label">Its label.</param>
    /// <param name="kind">Its kind, as the protocol numbers them.</param>
    public CompletionSortKey(string sortText, string label, int kind)
    {
        _sortTextLow = sortText.ToLowerInvariant();
        _label = label;
        _kind = kind;
    }

    public int CompareTo(CompletionSortKey other)
    {
        int bySortText = string.CompareOrdinal(_sortTextLow, other._sortTextLow);
        if (bySortText != 0)
            return bySortText;
        int byLabel = string.CompareOrdinal(_label, other._label);
        return byLabel != 0 ? byLabel : _kind.CompareTo(other._kind);
    }
}

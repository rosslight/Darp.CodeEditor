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
/// Lists of up to <see cref="MaxGracefulItems"/> items tolerate a typo, as VS Code's suggest widget does with
/// <c>fuzzyScoreGracefulAggressive</c>: an item also matches with two neighbouring characters of the typed text
/// swapped, after the first character and up to the seventh and eighth, at a cost of 3, and the best of these and the
/// text as typed counts, so that "prnit" matches "print", and "cno" matches "console" as "con" does. Longer lists match
/// strictly, as each swap is another match.
/// </para>
/// <para>
/// A ranking whose scoring tables would fill more than <see cref="MaxCellsPerRank"/> cells, as many as some 12 ms take,
/// scores each of its items the cheaper way: by the way that one pass finds, as the table scores that way
/// (<see cref="FuzzyScorer.ScoreInOnePass"/>), and with swaps only for an item that the typed text does not match, as
/// VS Code's <c>fuzzyScoreGraceful</c>. Matching 2,000 labels of 128 characters against 63 typed characters fills some
/// 8 million for the text as typed alone. It decides before it scores, so that alike items score alike.
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

    /// <summary>The most cells of the scoring tables that one ranking fills (<see cref="FuzzyScorer.Cells"/>).</summary>
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
    /// <see cref="MaxGracefulItems"/> items match gracefully, and all of them by tables while those stay within
    /// <see cref="MaxCellsPerRank"/>. Matching a long list against a long typed text takes long, so it belongs off the
    /// UI thread; it stops soon once <paramref name="cancellationToken"/> is cancelled. Each text is read in lower case
    /// once, and a typed text that items share, as they mostly do, is made ready once, with its swaps.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public List<RankedCompletion> Rank(
        IReadOnlyList<CompletionCandidate> items,
        CancellationToken cancellationToken = default
    )
    {
        var texts = new RankedTexts(items, items.Count <= MaxGracefulItems);
        bool exact = Cells(texts, cancellationToken) <= _cellsPerRank;
        var ranked = new List<RankedCompletion>();
        for (int i = 0; i < items.Count; i++)
        {
            // An item takes up to some 50 µs: a check every 64 items keeps the wait short and its cost small.
            if (i % 64 == 0)
                cancellationToken.ThrowIfCancellationRequested();
            CompletionCandidate item = items[i];
            if (
                Match(texts.Typed(i), item.Label, texts, i, item.FilterText, item.FilterTextIsLabel, exact) is { } match
            )
            {
                ranked.Add(new RankedCompletion(i, match.Score, match.Highlights));
            }
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
    /// <param name="exact">
    /// Whether scores come from the tables; otherwise the cheaper way, as a ranking past <see cref="MaxCellsPerRank"/>.
    /// Both find the same items.
    /// </param>
    public (int Score, int[] Highlights)? Match(
        string typed,
        string label,
        string filterText,
        bool filterTextIsLabel,
        bool graceful = false,
        bool exact = true
    )
    {
        var texts = new RankedTexts([new CompletionCandidate(typed, label, filterText, filterTextIsLabel)], graceful);
        return Match(texts.Typed(0), label, texts, 0, filterText, filterTextIsLabel, exact);
    }

    /// <summary>How the item at <paramref name="index"/> of <paramref name="texts"/> matches, as <see cref="Match(string, string, string, bool, bool, bool)"/> says.</summary>
    private (int Score, int[] Highlights)? Match(
        TypedText typed,
        string label,
        RankedTexts texts,
        int index,
        string filterText,
        bool filterTextIsLabel,
        bool exact
    )
    {
        if (typed.Start == typed.Text.Length)
            return (DefaultScore, []);
        string filterTextLow = texts.FilterTextLow(index);
        (int Score, int[] Positions)? scored = GracefulScore(typed, filterText, filterTextLow, exact);
        if (scored is not { } match)
            return null;
        if (filterTextIsLabel)
            return match;
        // The label shows: its characters that match, by the most lenient score, with the filter text's score.
        return (match.Score, AnyScore(typed, label, texts.LabelLow(index), exact));
    }

    /// <summary>
    /// The cells of the scoring tables that ranking the items of <paramref name="texts"/> by them fills, up to the first
    /// count past the budget: for each item, its filter text's for the text as typed and for each swap that it tries,
    /// and, when the filter text is another than the label and may match, the label's table that finds its matched
    /// characters.
    /// </summary>
    private long Cells(RankedTexts texts, CancellationToken cancellationToken)
    {
        long cells = 0;
        for (int i = 0; i < texts.Count && cells <= _cellsPerRank; i++)
        {
            if (i % 64 == 0)
                cancellationToken.ThrowIfCancellationRequested();
            TypedText typed = texts.Typed(i);
            if (typed.Start == typed.Text.Length)
                continue;
            string filterTextLow = texts.FilterTextLow(i);
            long filtered = _scorer.Cells(typed.Low, typed.Start, filterTextLow);
            foreach ((_, string swappedLow) in typed.Swaps)
                filtered += _scorer.Cells(swappedLow, typed.Start, filterTextLow);
            cells += filtered;
            // Only an item that may match shows its label.
            if (!texts.FilterTextIsLabel(i) && filtered > 0)
                cells += AnyTable(typed.Low, typed.Start, texts.LabelLow(i)).Cells;
        }
        return cells;
    }

    /// <summary>The table's score, or with <paramref name="exact"/> false the cheaper one's.</summary>
    private (int Score, int[] Positions)? Score(
        string pattern,
        string patternLow,
        int patternStart,
        string word,
        string wordLow,
        bool firstMatchCanBeWeak,
        bool exact
    ) =>
        exact
            ? _scorer.Score(pattern, patternLow, patternStart, word, wordLow, firstMatchCanBeWeak)
            : FuzzyScorer.ScoreInOnePass(pattern, patternLow, patternStart, word, wordLow, firstMatchCanBeWeak);

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
    private int[] AnyScore(TypedText typed, string word, string wordLow, bool exact)
    {
        int start = AnyTable(typed.Low, typed.Start, wordLow).Start;
        return
            start >= 0
            && Score(typed.Text, typed.Low, start, word, wordLow, firstMatchCanBeWeak: true, exact) is { } match
            ? match.Positions
            : [];
    }

    /// <summary>
    /// Where <see cref="AnyScore"/> matches from, and the cells of its table: the first typed character, from
    /// <paramref name="patternStart"/> and before the fourteenth, from which the word holds the rest of the typed text
    /// in order, as a match that may start weakly needs no more; -1 and none when there is no such character.
    /// </summary>
    private (int Start, long Cells) AnyTable(string patternLow, int patternStart, string wordLow)
    {
        int max = Math.Min(13, patternLow.Length);
        for (int start = patternStart; start < max; start++)
        {
            if (_scorer.Cells(patternLow, start, wordLow) is > 0 and var cells)
                return (start, cells);
        }
        return (-1, 0);
    }

    /// <summary>
    /// VS Code's <c>fuzzyScoreGracefulAggressive</c>, by the tables: the best score of the text as typed and with two
    /// neighbouring characters swapped (<see cref="TypedText.Swaps"/>), one pair at a time, each swap costing 3. The
    /// cheaper way, with <paramref name="exact"/> false, is its <c>fuzzyScoreGraceful</c>: the swaps only when the text
    /// as typed does not match. Without swaps, as for a strict match, the text as typed. Null when none matches.
    /// </summary>
    private (int Score, int[] Positions)? GracefulScore(TypedText typed, string word, string wordLow, bool exact)
    {
        (int Score, int[] Positions)? top = Score(
            typed.Text,
            typed.Low,
            typed.Start,
            word,
            wordLow,
            firstMatchCanBeWeak: false,
            exact
        );
        if (top is not null && !exact)
            return top;
        foreach ((string swapped, string swappedLow) in typed.Swaps)
        {
            if (
                Score(swapped, swappedLow, typed.Start, word, wordLow, firstMatchCanBeWeak: false, exact)
                    is { } candidate
                && (top is not { } best || candidate.Score - 3 > best.Score)
            )
            {
                top = (candidate.Score - 3, candidate.Positions);
            }
        }
        return top;
    }

    /// <summary>
    /// A typed text as matching reads it, made ready once for the items that share it: in lower case, where it starts
    /// after the whitespace that an item's edit starts with, and, matched gracefully, its swaps: the text with a
    /// character swapped with the next, one at a time, from after the first typed character up to the seventh, where
    /// the two differ, as alike neighbours have nothing to swap; none for fewer than three characters. VS Code counts
    /// the seven from the start of the whole text.
    /// </summary>
    private sealed class TypedText
    {
        public TypedText(string text, bool graceful)
        {
            Text = text;
            Low = Lower(text);
            while (Start < text.Length && text[Start] is ' ' or '\t')
                Start++;
            if (!graceful || text.Length < 3)
                return;
            int tries = Math.Min(7, text.Length - 1);
            var swaps = new List<(string, string)>();
            for (int moving = Start + 1; moving < tries; moving++)
            {
                if (text[moving] != text[moving + 1])
                    swaps.Add((Swapped(text, moving), Swapped(Low, moving)));
            }
            Swaps = [.. swaps];
        }

        public string Text { get; }

        public string Low { get; }

        public int Start { get; }

        /// <summary>The text with a swap, and in lower case, as many as it has.</summary>
        public (string Text, string Low)[] Swaps { get; } = [];

        /// <summary><paramref name="text"/> with the characters at <paramref name="position"/> and after it swapped.</summary>
        private static string Swapped(string text, int position) =>
            string.Create(
                text.Length,
                (text, position),
                static (span, state) =>
                {
                    state.text.AsSpan().CopyTo(span);
                    (span[state.position], span[state.position + 1]) = (span[state.position + 1], span[state.position]);
                }
            );
    }

    /// <summary>
    /// The texts of a ranking's items, each read in lower case once, when matching first needs it, and the typed text
    /// that items share made ready once.
    /// </summary>
    private sealed class RankedTexts(IReadOnlyList<CompletionCandidate> items, bool graceful)
    {
        private readonly IReadOnlyList<CompletionCandidate> _items = items;
        private readonly bool _graceful = graceful;
        private readonly string?[] _filterTextLows = new string?[items.Count];
        private readonly string?[] _labelLows = new string?[items.Count];
        private TypedText? _typed;

        public int Count => _items.Count;

        public bool FilterTextIsLabel(int index) => _items[index].FilterTextIsLabel;

        /// <summary>The typed text of the item at <paramref name="index"/>: the one before, when the items share it.</summary>
        public TypedText Typed(int index)
        {
            string text = _items[index].Typed;
            if (_typed is not { } typed || typed.Text != text)
                _typed = typed = new TypedText(text, _graceful);
            return typed;
        }

        public string FilterTextLow(int index) => _filterTextLows[index] ??= Lower(_items[index].FilterText);

        public string LabelLow(int index) => _labelLows[index] ??= Lower(_items[index].Label);
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

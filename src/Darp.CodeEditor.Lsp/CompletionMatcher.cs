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
/// This is a port of VS Code's <c>fuzzyScore</c> and <c>anyScore</c> (<c>src/vs/base/common/filters.ts</c>, 1.145.0, MIT
/// license), with its default options: the first character must match strongly, and a full match is boosted. It has
/// no typo-tolerant variant, and emoji are no separators. Texts are matched up to their first 128 characters.
/// </para>
/// <para>An instance keeps the tables of the matching for reuse: use it on one thread at a time.</para>
/// </remarks>
internal sealed class CompletionMatcher
{
    /// <summary>The longest typed text and filter text that are matched; the rest is ignored.</summary>
    private const int MaxLength = 128;

    /// <summary>The score of an item when nothing is typed: below any score of a match.</summary>
    private const int DefaultScore = -100;

    private const int Stride = MaxLength + 1;
    private const byte Diagonal = 1;
    private const byte Left = 2;
    private const byte LeftLeft = 3;

    /// <summary>For each typed position, the first and the last filter text position that it can match.</summary>
    private readonly int[] _minWordMatchPos = new int[2 * MaxLength + 1];

    private readonly int[] _maxWordMatchPos = new int[2 * MaxLength + 1];

    /// <summary>Per typed position (row) and filter text position (column): the best score, the length of the
    /// contiguous match that ends there, and where the best score came from.</summary>
    private readonly int[] _table = new int[Stride * Stride];

    private readonly int[] _diagonal = new int[Stride * Stride];
    private readonly byte[] _arrows = new byte[Stride * Stride];

    /// <summary>
    /// The items that match, best first; equal scores keep the order of <paramref name="items"/>.
    /// </summary>
    public List<RankedCompletion> Rank(IReadOnlyList<CompletionCandidate> items)
    {
        var ranked = new List<RankedCompletion>();
        for (int i = 0; i < items.Count; i++)
        {
            CompletionCandidate item = items[i];
            if (Match(item.Typed, item.Label, item.FilterText) is { } match)
                ranked.Add(new RankedCompletion(i, match.Score, match.Highlights));
        }
        // A stable order: List.Sort is not stable, so the index decides between equal scores.
        ranked.Sort(static (a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Index.CompareTo(b.Index));
        return ranked;
    }

    /// <summary>
    /// How <paramref name="typed"/> matches an item: the score of its filter text, and the characters of its label
    /// that matched, ascending. Null when it does not match.
    /// </summary>
    /// <param name="typed">The text typed from where the item's edit starts up to the caret.</param>
    /// <param name="label">The item's label, which shows.</param>
    /// <param name="filterText">The text the item is matched by: its label, unless the server gave another.</param>
    public (int Score, int[] Highlights)? Match(string typed, string label, string filterText)
    {
        // Whitespace that the item's edit starts with is not typed text.
        int start = 0;
        while (start < typed.Length && typed[start] is ' ' or '\t')
            start++;
        if (start == typed.Length)
            return (DefaultScore, []);
        string typedLow = typed.ToLowerInvariant();
        if (
            Score(typed, typedLow, start, filterText, filterText.ToLowerInvariant(), firstMatchCanBeWeak: false)
            is not { } match
        )
        {
            return null;
        }
        if (string.Equals(filterText, label, StringComparison.OrdinalIgnoreCase))
            return match;
        // The label shows: its characters that match, by the most lenient score, with the filter text's score.
        return (match.Score, AnyScore(typed, typedLow, start, label));
    }

    /// <summary>
    /// VS Code's <c>anyScore</c>: the characters of <paramref name="word"/> that match, also weakly and from a later
    /// typed character; none when nothing does.
    /// </summary>
    private int[] AnyScore(string pattern, string patternLow, int patternStart, string word)
    {
        string wordLow = word.ToLowerInvariant();
        int max = Math.Min(13, pattern.Length);
        for (int start = patternStart; start < max; start++)
        {
            if (Score(pattern, patternLow, start, word, wordLow, firstMatchCanBeWeak: true) is { } match)
                return match.Positions;
        }
        return [];
    }

    /// <summary>
    /// VS Code's <c>fuzzyScore</c>, with <c>boostFullMatch</c>: the score of <paramref name="pattern"/> from
    /// <paramref name="patternStart"/> in <paramref name="word"/>, and the positions in the word that matched,
    /// ascending; null without a match.
    /// </summary>
    private (int Score, int[] Positions)? Score(
        string pattern,
        string patternLow,
        int patternStart,
        string word,
        string wordLow,
        bool firstMatchCanBeWeak
    )
    {
        int patternLength = Math.Min(pattern.Length, MaxLength);
        int wordLength = Math.Min(word.Length, MaxLength);
        if (patternStart >= patternLength || wordLength == 0 || patternLength - patternStart > wordLength)
            return null;
        if (!IsPatternInWord(patternLow, patternStart, patternLength, wordLow, wordLength))
            return null;
        FillInMaxWordMatchPos(patternLength, wordLength, patternStart, patternLow, wordLow);

        bool hasStrongFirstMatch = false;
        int row = 1;
        int column = 1;
        for (int patternPos = patternStart; patternPos < patternLength; row++, patternPos++)
        {
            // Only the word positions that this pattern position can match, and that the next row can reach.
            int minWordMatchPos = _minWordMatchPos[patternPos];
            int maxWordMatchPos = _maxWordMatchPos[patternPos];
            int nextMaxWordMatchPos = patternPos + 1 < patternLength ? _maxWordMatchPos[patternPos + 1] : wordLength;
            column = minWordMatchPos + 1;
            for (int wordPos = minWordMatchPos; wordPos < nextMaxWordMatchPos; column++, wordPos++)
            {
                int score = int.MinValue;
                if (wordPos <= maxWordMatchPos)
                {
                    score = DoScore(
                        pattern,
                        patternLow,
                        patternPos,
                        patternStart,
                        word,
                        wordLow,
                        wordPos,
                        wordLength,
                        newMatchStart: _diagonal[At(row - 1, column - 1)] == 0,
                        ref hasStrongFirstMatch
                    );
                }
                bool canComeDiagonal = score != int.MinValue;
                int diagonalScore = canComeDiagonal ? score + _table[At(row - 1, column - 1)] : 0;

                bool canComeLeft = wordPos > minWordMatchPos;
                // A penalty for the start of a gap.
                int leftScore = canComeLeft
                    ? _table[At(row, column - 1)] + (_diagonal[At(row, column - 1)] > 0 ? -5 : 0)
                    : 0;
                bool canComeLeftLeft = wordPos > minWordMatchPos + 1 && _diagonal[At(row, column - 1)] > 0;
                int leftLeftScore = canComeLeftLeft
                    ? _table[At(row, column - 2)] + (_diagonal[At(row, column - 2)] > 0 ? -5 : 0)
                    : 0;

                int cell = At(row, column);
                if (
                    canComeLeftLeft
                    && (!canComeLeft || leftLeftScore >= leftScore)
                    && (!canComeDiagonal || leftLeftScore >= diagonalScore)
                )
                {
                    // Jumping over a diagonal means a match earlier in the word.
                    (_table[cell], _arrows[cell], _diagonal[cell]) = (leftLeftScore, LeftLeft, 0);
                }
                else if (canComeLeft && (!canComeDiagonal || leftScore >= diagonalScore))
                {
                    // Left means a match earlier in the word.
                    (_table[cell], _arrows[cell], _diagonal[cell]) = (leftScore, Left, 0);
                }
                else
                {
                    // IsPatternInWord found a match, so the diagonal is the remaining way.
                    (_table[cell], _arrows[cell], _diagonal[cell]) = (
                        diagonalScore,
                        Diagonal,
                        _diagonal[At(row - 1, column - 1)] + 1
                    );
                }
            }
        }

        if (!hasStrongFirstMatch && !firstMatchCanBeWeak)
            return null;

        row--;
        column--;
        int total = _table[At(row, column)];
        var positions = new int[patternLength - patternStart];
        int backwardsDiagonalLength = 0;
        int maxMatchColumn = 0;
        while (row >= 1)
        {
            // The column where the path goes diagonally up.
            int diagonalColumn = column;
            do
            {
                byte arrow = _arrows[At(row, diagonalColumn)];
                if (arrow == LeftLeft)
                    diagonalColumn -= 2;
                else if (arrow == Left)
                    diagonalColumn -= 1;
                else
                    break;
            } while (diagonalColumn >= 1);

            // Keep the backwards diagonal instead, if it makes a longer contiguous match.
            if (
                backwardsDiagonalLength > 1
                && patternLow[patternStart + row - 1] == wordLow[column - 1]
                && !IsUpperCaseAt(diagonalColumn - 1, word, wordLow)
                && backwardsDiagonalLength + 1 > _diagonal[At(row, diagonalColumn)]
            )
            {
                diagonalColumn = column;
            }
            backwardsDiagonalLength = diagonalColumn == column ? backwardsDiagonalLength + 1 : 1;
            if (maxMatchColumn == 0)
                maxMatchColumn = diagonalColumn;
            row--;
            column = diagonalColumn - 1;
            positions[row] = column;
        }

        // The word matches the pattern with all its characters: ahead of the others.
        if (wordLength == patternLength)
            total += 2;
        // A penalty for each skipped character of the word.
        total -= maxMatchColumn - patternLength;
        return (total, positions);
    }

    /// <summary>
    /// Whether the pattern's characters occur in the word in order, ignoring case; fills in the first word position
    /// each can match.
    /// </summary>
    private bool IsPatternInWord(string patternLow, int patternPos, int patternLength, string wordLow, int wordLength)
    {
        int wordPos = 0;
        while (patternPos < patternLength && wordPos < wordLength)
        {
            if (patternLow[patternPos] == wordLow[wordPos])
            {
                _minWordMatchPos[patternPos] = wordPos;
                patternPos++;
            }
            wordPos++;
        }
        return patternPos == patternLength;
    }

    /// <summary>The last word position that each pattern position can match.</summary>
    private void FillInMaxWordMatchPos(
        int patternLength,
        int wordLength,
        int patternStart,
        string patternLow,
        string wordLow
    )
    {
        int patternPos = patternLength - 1;
        int wordPos = wordLength - 1;
        while (patternPos >= patternStart && wordPos >= 0)
        {
            if (patternLow[patternPos] == wordLow[wordPos])
            {
                _maxWordMatchPos[patternPos] = wordPos;
                patternPos--;
            }
            wordPos--;
        }
    }

    /// <summary>The score of matching a pattern character at a word position; <see cref="int.MinValue"/> for none.</summary>
    private static int DoScore(
        string pattern,
        string patternLow,
        int patternPos,
        int patternStart,
        string word,
        string wordLow,
        int wordPos,
        int wordLength,
        bool newMatchStart,
        ref bool firstMatchIsStrong
    )
    {
        if (patternLow[patternPos] != wordLow[wordPos])
            return int.MinValue;

        int score = 1;
        bool isGapLocation = false;
        if (wordPos == patternPos - patternStart)
        {
            // A common prefix: "foobar" for "foobaz".
            score = pattern[patternPos] == word[wordPos] ? 7 : 5;
        }
        else if (IsUpperCaseAt(wordPos, word, wordLow) && (wordPos == 0 || !IsUpperCaseAt(wordPos - 1, word, wordLow)))
        {
            // A capital: "foo" for "forOthers".
            score = pattern[patternPos] == word[wordPos] ? 7 : 5;
            isGapLocation = true;
        }
        else if (IsSeparatorAt(wordLow, wordPos) && (wordPos == 0 || !IsSeparatorAt(wordLow, wordPos - 1)))
        {
            // A separator: "." for "foo.bar".
            score = 5;
        }
        else if (IsSeparatorAt(wordLow, wordPos - 1) || IsWhitespaceAt(wordLow, wordPos - 1))
        {
            // After a separator: "foo" for "bar_foo".
            score = 5;
            isGapLocation = true;
        }

        if (score > 1 && patternPos == patternStart)
            firstMatchIsStrong = true;
        if (!isGapLocation)
        {
            isGapLocation =
                IsUpperCaseAt(wordPos, word, wordLow)
                || IsSeparatorAt(wordLow, wordPos - 1)
                || IsWhitespaceAt(wordLow, wordPos - 1);
        }

        if (patternPos == patternStart)
        {
            // The first character, not at the word's start: a penalty for the gap before it.
            if (wordPos > 0)
                score -= isGapLocation ? 3 : 5;
        }
        else if (newMatchStart)
        {
            // The start of a new match, after a gap.
            score += isGapLocation ? 2 : 0;
        }
        else
        {
            // Part of a contiguous match: a bonus, unless a gap would be preferred here.
            score += isGapLocation ? 0 : 1;
        }

        // A match on the word's last character pretends a gap after it, as every other match has one.
        if (wordPos + 1 == wordLength)
            score -= isGapLocation ? 3 : 5;
        return score;
    }

    private static bool IsUpperCaseAt(int position, string word, string wordLow) => word[position] != wordLow[position];

    private static bool IsSeparatorAt(string text, int index) =>
        index >= 0
        && index < text.Length
        && text[index]
            is '_'
                or '-'
                or '.'
                or ' '
                or '/'
                or '\\'
                or '\''
                or '"'
                or ':'
                or '$'
                or '<'
                or '>'
                or '('
                or ')'
                or '['
                or ']'
                or '{'
                or '}';

    private static bool IsWhitespaceAt(string text, int index) =>
        index >= 0 && index < text.Length && text[index] is ' ' or '\t';

    private static int At(int row, int column) => row * Stride + column;
}

/// <summary>An item to rank: the text typed for it, and its texts.</summary>
/// <param name="Typed">The text typed from where the item's edit starts up to the caret.</param>
/// <param name="Label">The label, whose matched characters show.</param>
/// <param name="FilterText">The text it is matched by: its label, unless the server gave another.</param>
internal readonly record struct CompletionCandidate(string Typed, string Label, string FilterText);

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

namespace Darp.CodeEditor.Lsp;

/// <summary>
/// VS Code's <c>fuzzyScore</c> (<c>src/vs/base/common/filters.ts</c>, 1.145.0, MIT license), with <c>boostFullMatch</c>:
/// the best score of a typed text in a filter text, by a table of every way it matches. Texts are matched up to their
/// first <see cref="CompletionMatcher.MaxLength"/> characters. Once the cells it may fill are spent
/// (<see cref="CellsLeft"/>), it scores the one way that a single pass finds instead, as the table would score that
/// way: a long typed text against long filter texts fills thousands of cells for each. An instance keeps the table for
/// reuse: use it on one thread at a time. It takes about 150 KB.
/// </summary>
internal sealed class FuzzyScorer
{
    private const int MaxLength = CompletionMatcher.MaxLength;
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
    /// The cells of the table that scoring may still fill: a score that would fill more is found in one pass instead.
    /// </summary>
    public long CellsLeft { get; set; } = long.MaxValue;

    /// <summary>
    /// The best score of <paramref name="pattern"/> from <paramref name="patternStart"/> in <paramref name="word"/>, and
    /// the positions in the word that matched, ascending; null without a match.
    /// </summary>
    public (int Score, int[] Positions)? Score(
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
        long cells = 0;
        for (int patternPos = patternStart; patternPos < patternLength; patternPos++)
        {
            int next = patternPos + 1 < patternLength ? _maxWordMatchPos[patternPos + 1] : wordLength;
            cells += next - _minWordMatchPos[patternPos];
        }
        if (cells > CellsLeft)
            return OnePass(pattern, patternLow, patternStart, word, wordLow, firstMatchCanBeWeak);
        CellsLeft -= cells;

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
    /// The score of one way that <paramref name="pattern"/> matches, found in one pass and scored as the table scores
    /// that way, so that it ranks among the table's scores, at most as high as the best way: the first character where
    /// it first matches strongly, or at all with <paramref name="firstMatchCanBeWeak"/>, and each next one where it
    /// first matches after the one before. Null when the pattern does not match so.
    /// </summary>
    private static (int Score, int[] Positions)? OnePass(
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
        int count = patternLength - patternStart;
        int first = -1;
        int total = 0;
        for (int wordPos = 0; wordPos <= wordLength - count && first < 0; wordPos++)
        {
            bool strong = false;
            int score = DoScore(
                pattern,
                patternLow,
                patternStart,
                patternStart,
                word,
                wordLow,
                wordPos,
                wordLength,
                true,
                ref strong
            );
            if (score != int.MinValue && (strong || firstMatchCanBeWeak))
                (first, total) = (wordPos, score);
        }
        if (first < 0)
            return null;
        var positions = new int[count];
        positions[0] = first;
        bool unused = false;
        int previous = first;
        for (int i = 1; i < count; i++)
        {
            int patternPos = patternStart + i;
            int found = wordLow.AsSpan(previous + 1, wordLength - previous - 1).IndexOf(patternLow[patternPos]);
            if (found < 0)
                return null;
            int wordPos = previous + 1 + found;
            bool newMatchStart = wordPos > previous + 1;
            // The table's moves to the left cost a gap once, where it starts.
            if (newMatchStart)
                total -= 5;
            total += DoScore(
                pattern,
                patternLow,
                patternPos,
                patternStart,
                word,
                wordLow,
                wordPos,
                wordLength,
                newMatchStart,
                ref unused
            );
            positions[i] = previous = wordPos;
        }
        // As the table: a gap after the last match, a full match, and the skipped characters.
        if (previous + 1 < wordLength)
            total -= 5;
        if (wordLength == patternLength)
            total += 2;
        total -= previous + 1 - patternLength;
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

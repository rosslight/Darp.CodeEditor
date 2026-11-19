using AvaloniaEdit.Document;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Words as VS Code's <c>cursorWordOperations.ts</c> finds them for <c>cursorWordLeft</c>, <c>cursorWordEndRight</c>,
/// <c>deleteWordLeft</c>, <c>deleteWordRight</c> and a double-click, and its <c>getWordAtPosition</c> for Ctrl+D: runs
/// of word characters and runs of separators, by the default <c>editor.wordSeparators</c>, with spaces and tabs between
/// them. Columns are character indexes in a line's text.
/// </summary>
/// <remarks>The words come from the word separators, not from a language's <c>wordPattern</c>.</remarks>
internal static class WordRules
{
    private const string Separators = "`~!@#$%^&*()-=+[{]}\\|;:'\",.<>/?";

    /// <summary>
    /// What a double-click at <paramref name="column"/> selects: the word it touches, the one on the left first, then
    /// the one on the right; a run of separators that it is inside of; otherwise what lies between the words on either
    /// side, up to the line's ends.
    /// </summary>
    public static TextRange DoubleClicked(string line, int column)
    {
        Word? previous = Previous(line, column);
        Word? next = Next(line, column);
        if (previous is { } left && left.Touches(column))
            return left.Range;
        if (next is { } right && right.Touches(column))
            return right.Range;
        return TextRange.FromBounds(previous?.End ?? 0, next?.Start ?? line.Length);
    }

    /// <summary>
    /// The run of word characters that <paramref name="column"/> touches, the one on the left first, for Ctrl+D; null at
    /// separators and spaces.
    /// </summary>
    public static TextRange? WordAt(string line, int column) =>
        Previous(line, column) is { Type: CharClass.Regular } left && left.Touches(column) ? left.Range
        : Next(line, column) is { Type: CharClass.Regular } right && right.Touches(column) ? right.Range
        : null;

    /// <summary>
    /// The occurrences of <paramref name="word"/>, a word, in <paramref name="text"/> as whole words, with case, at most
    /// <paramref name="max"/>: those that no word character comes right before or after on their line, as VS Code finds
    /// a word's occurrences without a language server.
    /// </summary>
    public static List<TextRange> Occurrences(TextReader text, string word, int max)
    {
        var found = new List<TextRange>();
        // The text in blocks that keep the end of the block before: a match whose next character is in the next block,
        // and the character before it.
        int kept = word.Length + 1;
        char[] block = new char[Math.Max(4096, 4 * kept)];
        int start = 0;
        int length = text.ReadBlock(block, 0, block.Length);
        while (true)
        {
            bool last = length < block.Length;
            // The matches that start here and not in the next block, after the character kept before them.
            int end = last ? length - word.Length + 1 : length - kept + 1;
            for (int at = start == 0 ? 0 : 1; at < end; at++)
            {
                int next = block.AsSpan(at, end - at + word.Length - 1).IndexOf(word, StringComparison.Ordinal);
                if (next < 0)
                    break;
                at += next;
                if (
                    (at == 0 || !IsWordCharacter(block[at - 1]))
                    && (at + word.Length == length || !IsWordCharacter(block[at + word.Length]))
                )
                {
                    found.Add(new TextRange(start + at, word.Length));
                    if (found.Count == max)
                        return found;
                }
            }
            if (last)
                return found;
            Array.Copy(block, length - kept, block, 0, kept);
            start += length - kept;
            length = kept + text.ReadBlock(block, kept, block.Length - kept);
        }
    }

    /// <summary>
    /// The selection of a drag that a double-click on <paramref name="start"/> began, with the pointer at
    /// <paramref name="column"/> of <paramref name="line"/>, which starts at <paramref name="lineOffset"/>: it grows by
    /// the words under the pointer, and keeps the word it started with.
    /// </summary>
    public static TextSelection DragTo(TextRange start, string line, int lineOffset, int column)
    {
        TextRange under =
            Previous(line, column) is { Type: CharClass.Regular } left && left.Start < column && column < left.End
                ? left.Range
            : Next(line, column) is { Type: CharClass.Regular } right && right.Start < column && column < right.End
                ? right.Range
            : new TextRange(column, 0);
        int wordStart = lineOffset + under.Start;
        int wordEnd = lineOffset + under.End;
        int caret =
            Contains(start, wordStart) ? start.End
            : lineOffset + column <= start.Start ? wordStart
            : Contains(start, wordEnd) ? start.Start
            : wordEnd;
        return new TextSelection(caret > start.Start ? start.Start : start.End, caret);

        static bool Contains(TextRange range, int offset) => range.Start <= offset && offset <= range.End;
    }

    /// <summary>
    /// Where Ctrl+Left puts the caret: at the start of the word or run of separators before <paramref name="caret"/>,
    /// past a single separator right after a word character, such as the dot of <c>foo.</c>; at the line's start
    /// without one. From a line's start, it starts from the end of the line before.
    /// </summary>
    public static int MoveLeft(TextDocument text, int caret)
    {
        DocumentLine line = text.GetLineByOffset(caret);
        int column = caret - line.Offset;
        if (column == 0 && line.PreviousLine is { } previous)
            (line, column) = (previous, previous.Length);
        string lineText = text.GetText(line);
        Word? word = Previous(lineText, column);
        if (
            word is { Type: CharClass.Separator, Length: 1 } separator
            && separator.Start > 0
            && Classify(lineText[separator.Start - 1]) == CharClass.Regular
        )
            word = Previous(lineText, separator.Start);
        return line.Offset + (word?.Start ?? 0);
    }

    /// <summary>
    /// Where Ctrl+Right puts the caret: at the end of the word or run of
    /// separators after <paramref name="caret"/>, past a single separator right before a word; at the line's end without
    /// one. From a line's end, it starts from the start of the line after.
    /// </summary>
    public static int MoveRight(TextDocument text, int caret)
    {
        DocumentLine line = text.GetLineByOffset(caret);
        int column = caret - line.Offset;
        if (column == line.Length && line.NextLine is { } next)
            (line, column) = (next, 0);
        string lineText = text.GetText(line);
        Word? word = Next(lineText, column);
        if (
            word is { Type: CharClass.Separator, Length: 1 } separator
            && separator.End < lineText.Length
            && Classify(lineText[separator.End]) == CharClass.Regular
        )
            word = Next(lineText, separator.End);
        return line.Offset + (word?.End ?? lineText.Length);
    }

    /// <summary>
    /// What Ctrl+Backspace deletes before <paramref name="caret"/>: two or more spaces
    /// and tabs alone; else back to the start of the word or run of separators before it; else to the line's start; at
    /// the line's start, the line break before it. Null at the text's start.
    /// </summary>
    public static TextRange? DeleteLeft(TextDocument text, int caret)
    {
        if (caret == 0)
            return null;
        DocumentLine line = text.GetLineByOffset(caret);
        string lineText = text.GetText(line);
        int column = caret - line.Offset;
        int whitespace = column;
        while (whitespace > 0 && IsWhitespace(lineText[whitespace - 1]))
            whitespace--;
        int start =
            whitespace < column - 1 ? line.Offset + whitespace
            : Previous(lineText, column) is { } word ? line.Offset + word.Start
            : column > 0 ? line.Offset
            : line.PreviousLine.EndOffset;
        return TextRange.FromBounds(start, caret);
    }

    /// <summary>
    /// What Ctrl+Delete deletes after <paramref name="caret"/>: spaces and tabs
    /// alone; else up to the end of the next word or run of separators; else to the line's end; at the line's end, the
    /// line break and the next line's whitespace before its first word. Null at the text's end.
    /// </summary>
    public static TextRange? DeleteRight(TextDocument text, int caret)
    {
        if (caret == text.TextLength)
            return null;
        DocumentLine line = text.GetLineByOffset(caret);
        string lineText = text.GetText(line);
        int column = caret - line.Offset;
        int whitespace = column;
        while (whitespace < lineText.Length && IsWhitespace(lineText[whitespace]))
            whitespace++;
        int end =
            whitespace > column ? line.Offset + whitespace
            : Next(lineText, column) is { } word ? line.Offset + word.End
            : column < lineText.Length || line.NextLine is not { } next ? line.EndOffset
            : next.Offset + (Next(text.GetText(next), 0)?.Start ?? next.Length);
        return TextRange.FromBounds(caret, end);
    }

    /// <summary>
    /// The nearest word or run of separators left of <paramref name="column"/>: the one that the character before the column is in, which may go on past the
    /// column, or else the one before the spaces there. Null when there is none.
    /// </summary>
    private static Word? Previous(string line, int column)
    {
        CharClass? type = null;
        for (int i = column - 1; i >= 0; i--)
        {
            CharClass current = Classify(line[i]);
            if (type is { } word && current != word)
                return new Word(i + 1, EndOf(line, word, i + 1), word);
            if (current != CharClass.Whitespace)
                type = current;
        }
        return type is { } first ? new Word(0, EndOf(line, first, 0), first) : null;
    }

    /// <summary>The nearest word or run of separators right of <paramref name="column"/>, likewise.</summary>
    private static Word? Next(string line, int column)
    {
        CharClass? type = null;
        for (int i = column; i < line.Length; i++)
        {
            CharClass current = Classify(line[i]);
            if (type is { } word && current != word)
                return new Word(StartOf(line, word, i), i, word);
            if (current != CharClass.Whitespace)
                type = current;
        }
        return type is { } last ? new Word(StartOf(line, last, line.Length), line.Length, last) : null;
    }

    private static int EndOf(string line, CharClass type, int start)
    {
        int end = start;
        while (end < line.Length && Classify(line[end]) == type)
            end++;
        return end;
    }

    private static int StartOf(string line, CharClass type, int end)
    {
        int start = end;
        while (start > 0 && Classify(line[start - 1]) == type)
            start--;
        return start;
    }

    /// <summary>Only spaces and tabs are whitespace; every character but them and the separators is a word's.</summary>
    private static CharClass Classify(char c) =>
        IsWhitespace(c) ? CharClass.Whitespace
        : Separators.Contains(c) ? CharClass.Separator
        : CharClass.Regular;

    private static bool IsWhitespace(char c) => c is ' ' or '\t';

    /// <summary>A character of a word, where a line break ends one too.</summary>
    private static bool IsWordCharacter(char c) => c is not ('\r' or '\n') && Classify(c) == CharClass.Regular;

    private enum CharClass
    {
        Regular,
        Whitespace,
        Separator,
    }

    /// <summary>A run of word characters or of separators, from <see cref="Start"/> to before <see cref="End"/>.</summary>
    private readonly record struct Word(int Start, int End, CharClass Type)
    {
        public int Length => End - Start;

        public TextRange Range => TextRange.FromBounds(Start, End);

        /// <summary>Whether <paramref name="column"/> touches it: a word also at its ends, separators only before its end.</summary>
        public bool Touches(int column) =>
            Start <= column && (Type == CharClass.Regular ? column <= End : column < End);
    }
}

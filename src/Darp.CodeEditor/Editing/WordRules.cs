using AvaloniaEdit.Document;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// VS Code's words for double-click and for deleting by words, a port of its <c>cursorWordOperations.ts</c>: runs of
/// word characters and runs of separators, by VS Code's default word separators; spaces and tabs between them.
/// </summary>
/// <remarks>
/// VS Code takes these words from the <c>editor.wordSeparators</c> setting, not from a language's <c>wordPattern</c>,
/// and so do they. Columns are character indexes in a line's text.
/// </remarks>
internal static class WordRules
{
    /// <summary>
    /// What a double-click at <paramref name="column"/> selects: the word it touches, the one on the left first, then
    /// the one on the right; a run of separators it is inside of; otherwise what lies between the neighboring words or
    /// separators, up to the line's ends.
    /// </summary>
    public static (int Start, int End) WordAt(string line, int column)
    {
        Word? previous = PreviousWord(line, column);
        Word? next = NextWord(line, column);
        if (previous is { } left && left.Touches(column))
            return (left.Start, left.End);
        if (next is { } right && right.Touches(column))
            return (right.Start, right.End);
        return (previous?.End ?? 0, next?.Start ?? line.Length);
    }

    /// <summary>
    /// The word that VS Code's <c>getWordAtPosition</c> finds at <paramref name="column"/>, for Ctrl+D: a run of word
    /// characters that the column touches, the one on the left first. Null at separators and spaces.
    /// </summary>
    public static (int Start, int End)? WordAtCaret(string line, int column)
    {
        return Touched(PreviousWord(line, column)) ?? Touched(NextWord(line, column));

        (int Start, int End)? Touched(Word? word) =>
            word is { Type: CharClass.Regular } found && found.Start <= column && column <= found.End
                ? (found.Start, found.End)
                : null;
    }

    /// <summary>
    /// Where a drag that started with a double-click puts the caret, at <paramref name="column"/> of
    /// <paramref name="line"/>, which starts at <paramref name="lineOffset"/>: it grows the selection by whole words,
    /// and keeps the word it started with, <paramref name="start"/>.
    /// </summary>
    /// <returns>The selection's anchor and caret, as offsets.</returns>
    public static (int Anchor, int Caret) DragTo(TextRange start, string line, int lineOffset, int column)
    {
        int position = lineOffset + column;
        (int wordStart, int wordEnd) =
            PreviousWord(line, column) is { Type: CharClass.Regular } left && left.Start < column && column < left.End
                ? (left.Start, left.End)
            : NextWord(line, column) is { Type: CharClass.Regular } right && right.Start < column && column < right.End
                ? (right.Start, right.End)
            : (column, column);
        int caret;
        if (Contains(start, position))
            caret = start.End;
        else if (position <= start.Start)
            caret = Contains(start, lineOffset + wordStart) ? start.End : lineOffset + wordStart;
        else
            caret = Contains(start, lineOffset + wordEnd) ? start.Start : lineOffset + wordEnd;
        // The selection keeps the start word whichever way it grows.
        return (caret > start.Start ? start.Start : start.End, caret);

        static bool Contains(TextRange range, int offset) => range.Start <= offset && offset <= range.End;
    }

    /// <summary>
    /// What VS Code's <c>deleteWordLeft</c> deletes before <paramref name="caret"/>: a run of two or more spaces or
    /// tabs alone; else back to the start of the word or the run of separators before it; else to the line's start;
    /// at the line's start, the line break before it. Null at the text's start.
    /// </summary>
    public static TextRange? DeleteWordLeft(TextDocument document, int caret)
    {
        if (caret == 0)
            return null;
        DocumentLine line = document.GetLineByOffset(caret);
        string text = document.GetText(line);
        int column = caret - line.Offset;
        int lastNonWhitespace = column - 1;
        while (lastNonWhitespace >= 0 && IsWhitespace(text[lastNonWhitespace]))
            lastNonWhitespace--;
        int start =
            lastNonWhitespace + 1 < column - 1 ? line.Offset + lastNonWhitespace + 1
            : PreviousWord(text, column) is { } word ? line.Offset + word.Start
            : column > 0 ? line.Offset
            : line.PreviousLine.EndOffset;
        return TextRange.FromBounds(start, caret);
    }

    /// <summary>
    /// What VS Code's <c>deleteWordRight</c> deletes after <paramref name="caret"/>: spaces and tabs alone; else to the
    /// end of the next word or run of separators; else to the line's end; at the line's end, the line break and the
    /// next line's whitespace before its first word. Null at the text's end.
    /// </summary>
    public static TextRange? DeleteWordRight(TextDocument document, int caret)
    {
        if (caret == document.TextLength)
            return null;
        DocumentLine line = document.GetLineByOffset(caret);
        string text = document.GetText(line);
        int column = caret - line.Offset;
        int firstNonWhitespace = column;
        while (firstNonWhitespace < text.Length && IsWhitespace(text[firstNonWhitespace]))
            firstNonWhitespace++;
        int end;
        if (column < firstNonWhitespace)
            end = line.Offset + firstNonWhitespace;
        else if (NextWord(text, column) is { } word)
            end = line.Offset + word.End;
        else if (column < text.Length || line.NextLine is not { } next)
            end = line.EndOffset;
        else
            end = next.Offset + (NextWord(document.GetText(next), 0)?.Start ?? next.Length);
        return TextRange.FromBounds(caret, end);
    }

    /// <summary>
    /// The word at <paramref name="column"/>, as VS Code's <c>getWordAtPosition</c> finds it, such as for the search's
    /// query: the run of word characters it touches, the one on the left first; null where it touches none.
    /// </summary>
    public static (int Start, int End)? WordAtPosition(string line, int column) =>
        PreviousWord(line, column) is { Type: CharClass.Regular } left && left.Touches(column) ? (left.Start, left.End)
        : NextWord(line, column) is { Type: CharClass.Regular } right && right.Touches(column)
            ? (right.Start, right.End)
        : null;

    /// <summary>
    /// Where VS Code's <c>cursorWordLeft</c>, Ctrl+Left, puts the caret: at the start of the word or run of separators
    /// before <paramref name="caret"/>, and past a single separator right after a word, such as the dot of
    /// <c>foo.bar</c>; at the line's start without one. From a line's start, it starts from the end of the line before.
    /// </summary>
    public static int MoveWordLeft(TextDocument document, int caret)
    {
        DocumentLine line = document.GetLineByOffset(caret);
        int column = caret - line.Offset;
        if (column == 0 && line.PreviousLine is { } previous)
            (line, column) = (previous, previous.Length);
        string text = document.GetText(line);
        Word? word = PreviousWord(text, column);
        if (
            word is { Type: CharClass.Separator } separator
            && separator.End - separator.Start == 1
            && separator.Start > 0
            && Classify(text[separator.Start - 1]) == CharClass.Regular
        )
            word = PreviousWord(text, separator.Start);
        return line.Offset + (word?.Start ?? 0);
    }

    /// <summary>
    /// Where VS Code's <c>cursorWordEndRight</c>, Ctrl+Right, puts the caret: at the end of the word or run of
    /// separators after <paramref name="caret"/>, and past a single separator right before a word; at the line's end
    /// without one. From a line's end, it starts from the start of the line after.
    /// </summary>
    public static int MoveWordRight(TextDocument document, int caret)
    {
        DocumentLine line = document.GetLineByOffset(caret);
        int column = caret - line.Offset;
        if (column == line.Length && line.NextLine is { } next)
            (line, column) = (next, 0);
        string text = document.GetText(line);
        Word? word = NextWord(text, column);
        if (
            word is { Type: CharClass.Separator } separator
            && separator.End - separator.Start == 1
            && separator.End < text.Length
            && Classify(text[separator.End]) == CharClass.Regular
        )
            word = NextWord(text, separator.End);
        return line.Offset + (word?.End ?? text.Length);
    }

    /// <summary>
    /// The word or run of separators that ends at or after <paramref name="column"/> and starts before it, the nearest
    /// one to the left of <paramref name="column"/>; null if there is none on the line.
    /// </summary>
    private static Word? PreviousWord(string line, int column)
    {
        CharClass? type = null;
        for (int i = column - 1; i >= 0; i--)
        {
            CharClass current = Classify(line[i]);
            if (type is { } word && current != word)
                return new Word(i + 1, EndOfWord(line, word, i + 1), word);
            if (current != CharClass.Whitespace)
                type = current;
        }
        return type is { } first ? new Word(0, EndOfWord(line, first, 0), first) : null;
    }

    /// <summary>The nearest word or run of separators to the right of <paramref name="column"/>, or around it.</summary>
    private static Word? NextWord(string line, int column)
    {
        CharClass? type = null;
        for (int i = column; i < line.Length; i++)
        {
            CharClass current = Classify(line[i]);
            if (type is { } word && current != word)
                return new Word(StartOfWord(line, word, i - 1), i, word);
            if (current != CharClass.Whitespace)
                type = current;
        }
        return type is { } last ? new Word(StartOfWord(line, last, line.Length - 1), line.Length, last) : null;
    }

    private static int EndOfWord(string line, CharClass type, int start)
    {
        int end = start;
        while (end < line.Length && Classify(line[end]) == type)
            end++;
        return end;
    }

    private static int StartOfWord(string line, CharClass type, int last)
    {
        int start = last;
        while (start >= 0 && Classify(line[start]) == type)
            start--;
        return start + 1;
    }

    /// <summary>VS Code's classes: only spaces and tabs are whitespace; everything but them and the separators is a word.</summary>
    private static CharClass Classify(char c) =>
        IsWhitespace(c) ? CharClass.Whitespace
        : WordCharacters.IsSeparator(c) ? CharClass.Separator
        : CharClass.Regular;

    private static bool IsWhitespace(char c) => c is ' ' or '\t';

    private enum CharClass
    {
        Regular,
        Whitespace,
        Separator,
    }

    /// <summary>A run of word characters or of separators, from <see cref="Start"/> to before <see cref="End"/>.</summary>
    private readonly record struct Word(int Start, int End, CharClass Type)
    {
        /// <summary>
        /// Whether a double-click at <paramref name="column"/> selects it: a word also at its ends, a run of separators
        /// only up to before its end.
        /// </summary>
        public bool Touches(int column) =>
            Start <= column && (Type == CharClass.Regular ? column <= End : column < End);
    }
}

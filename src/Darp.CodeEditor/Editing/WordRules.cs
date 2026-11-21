using AvaloniaEdit.Document;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The word at the caret and its whole-word occurrences, by VS Code's default word separators
/// (<see cref="WordCharacters"/>). Columns are character indexes in a line's text.
/// </summary>
internal static class WordRules
{
    /// <summary>
    /// How far around the caret <see cref="WordAt(TextDocument, int)"/> reads, as VS Code reads the word on a long line.
    /// </summary>
    private const int WordReach = 500;

    /// <summary>The run of word characters that <paramref name="column"/> touches; null at separators and spaces.</summary>
    public static TextRange? WordAt(string line, int column)
    {
        int start = column;
        while (start > 0 && WordCharacters.IsWordCharacter(line[start - 1]))
            start--;
        int end = column;
        while (end < line.Length && WordCharacters.IsWordCharacter(line[end]))
            end++;
        return start < end ? TextRange.FromBounds(start, end) : null;
    }

    /// <summary>
    /// The word at <paramref name="caret"/>, by its offsets, as <see cref="WordAt(string, int)"/> finds it within
    /// <see cref="WordReach"/> characters on either side on its line; null at separators and spaces.
    /// </summary>
    public static TextRange? WordAt(TextDocument text, int caret)
    {
        DocumentLine line = text.GetLineByOffset(caret);
        int start = Math.Max(line.Offset, caret - WordReach);
        string around = text.GetText(start, Math.Min(line.EndOffset, caret + WordReach) - start);
        return WordAt(around, caret - start) is { } word ? new TextRange(start + word.Start, word.Length) : null;
    }

    /// <summary>
    /// The occurrences of <paramref name="word"/>, a word, in <paramref name="text"/> as whole words, with case, at most
    /// <paramref name="max"/>: those that no word character comes right before or after, as VS Code finds a word's
    /// occurrences without a language server.
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
                    (at == 0 || !WordCharacters.IsWordCharacter(block[at - 1]))
                    && (at + word.Length == length || !WordCharacters.IsWordCharacter(block[at + word.Length]))
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
}

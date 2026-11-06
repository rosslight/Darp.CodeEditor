using AvaloniaEdit.Document;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Finds the bracket next to the caret and its match, among the language's brackets of one character each. As in VS
/// Code, brackets in strings, comments and regular expressions do not count, by the tokens of their lines as last
/// tokenized; lines without tokens count as code.
/// </summary>
internal static class BracketMatcher
{
    /// <summary>
    /// How many characters the search for a match reads at most, line breaks included, so that it stays well within a
    /// frame on the UI thread: about 5,000 lines of 20 characters. A match further away shows no pair.
    /// </summary>
    public const int MaxDistance = 100_000;

    /// <summary>
    /// The bracket at <paramref name="caret"/> and its match, by their offsets: the bracket after the caret first, as
    /// in VS Code, then the one before. Null when neither is a bracket with a match.
    /// </summary>
    /// <param name="document">The text.</param>
    /// <param name="caret">The caret's offset.</param>
    /// <param name="brackets">The language's brackets.</param>
    /// <param name="tokensOf">The tokens of a line by its number, as last tokenized; null for none.</param>
    public static (int Open, int Close)? FindPair(
        TextDocument document,
        int caret,
        IReadOnlyList<CharacterPair> brackets,
        Func<int, int[]?> tokensOf
    )
    {
        foreach (int offset in (ReadOnlySpan<int>)[caret, caret - 1])
        {
            if (offset < 0 || offset >= document.TextLength)
                continue;
            char c = document.GetCharAt(offset);
            foreach (CharacterPair pair in brackets)
            {
                if (
                    pair is not { Open: [char open], Close: [char close] }
                    || open == close
                    || (c != open && c != close)
                )
                    continue;
                DocumentLine line = document.GetLineByOffset(offset);
                if (IsIgnored(tokensOf(line.LineNumber), offset - line.Offset))
                    break;
                bool forward = c == open;
                if (Find(document, offset, open, close, forward, tokensOf) is { } match)
                    return forward ? (offset, match) : (match, offset);
            }
        }
        return null;
    }

    /// <summary>
    /// The bracket that matches the one at <paramref name="from"/>: the next closing bracket at the same depth going
    /// forward, or the opening one going backward, within <see cref="MaxDistance"/> characters. The text is read in
    /// blocks into a buffer, and a line's tokens only where a bracket is, so the search allocates nothing per line.
    /// </summary>
    private static int? Find(
        TextDocument document,
        int from,
        char open,
        char close,
        bool forward,
        Func<int, int[]?> tokensOf
    )
    {
        int start = forward ? from + 1 : Math.Max(0, from - MaxDistance);
        int end = forward ? Math.Min(document.TextLength, from + 1 + MaxDistance) : from;
        Span<char> block = stackalloc char[4096];
        int depth = 0;
        // The line of the last bracket: its bounds, as DocumentLine computes them anew each time, and its tokens.
        (int Start, int End) line = (-1, -1);
        int[]? tokens = null;
        // Going forward, one reader through the window; going backward, one for each block, from the end.
        using TextReader? ahead = forward ? document.CreateReader(start, end - start) : null;
        for (int done = 0; done < end - start; )
        {
            int length = Math.Min(block.Length, end - start - done);
            int blockStart = forward ? start + done : end - done - length;
            if (ahead is not null)
                ahead.ReadBlock(block[..length]);
            else
            {
                using TextReader behind = document.CreateReader(blockStart, length);
                behind.ReadBlock(block[..length]);
            }
            ReadOnlySpan<char> text = block[..length];
            for (int index = Next(text, forward ? -1 : length); index >= 0; index = Next(text, index))
            {
                char c = text[index];
                int offset = blockStart + index;
                if (offset < line.Start || offset > line.End)
                {
                    DocumentLine found = document.GetLineByOffset(offset);
                    line = (found.Offset, found.EndOffset);
                    tokens = tokensOf(found.LineNumber);
                }
                if (IsIgnored(tokens, offset - line.Start))
                    continue;
                if (c == (forward ? open : close))
                    depth++;
                else if (depth-- == 0)
                    return offset;
            }
            done += length;
        }
        return null;

        // The next bracket after index in the block going forward, or before it going backward; -1 for none.
        int Next(ReadOnlySpan<char> text, int index) =>
            forward
                ? text[(index + 1)..].IndexOfAny(open, close) is var found and >= 0
                    ? index + 1 + found
                    : -1
                : text[..index].LastIndexOfAny(open, close);
    }

    /// <summary>Whether a bracket at <paramref name="column"/> is in a string, comment or regular expression.</summary>
    private static bool IsIgnored(int[]? tokens, int column) =>
        tokens is not null && TokenTypes.At(tokens, column) != TokenType.Other;
}

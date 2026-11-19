using System.Buffers;
using System.Text;
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
    /// The opening bracket that a closing bracket at <paramref name="offset"/> would match: the closest one before it
    /// that is not closed, within <see cref="MaxDistance"/> characters. Null for none.
    /// </summary>
    public static int? FindOpening(
        TextDocument document,
        int offset,
        char open,
        char close,
        Func<int, int[]?> tokensOf
    ) => Find(document, offset, open, close, forward: false, tokensOf);

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
        using var buffer = new SearchBuffer();
        int depth = 0;
        // The line of the last bracket: its bounds, as DocumentLine computes them anew each time, and its tokens.
        (int Start, int End) line = (-1, -1);
        int[]? tokens = null;
        for (int done = 0; done < end - start; )
        {
            int length = Math.Min(SearchBuffer.BlockLength, end - start - done);
            int blockStart = forward ? start + done : end - done - length;
            buffer.Read(document, blockStart, length);
            ReadOnlySpan<char> text = buffer.Text;
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

    /// <summary>
    /// The innermost pair of brackets around <paramref name="offset"/>, by their offsets, of any of the language's
    /// brackets: the first closing bracket after it without its opening one after it, and its match, as VS Code's
    /// <c>findEnclosingBrackets</c> finds them. Null when there is none within <see cref="MaxDistance"/>.
    /// </summary>
    public static (int Open, int Close)? FindEnclosing(
        TextDocument document,
        int offset,
        IReadOnlyList<CharacterPair> brackets,
        Func<int, int[]?> tokensOf
    )
    {
        // Per kind of bracket, how many opened after the offset are still open.
        var open = new int[brackets.Count];
        foreach ((int at, int pair, bool isOpen) in Scan(document, offset, forward: true, brackets, tokensOf))
        {
            if (isOpen)
                open[pair]++;
            else if (--open[pair] == -1)
            {
                (string opening, string closing) = brackets[pair];
                if (Find(document, at, opening[0], closing[0], forward: false, tokensOf) is { } match)
                    return (match, at);
            }
        }
        return null;
    }

    /// <summary>
    /// The first of the language's brackets at <paramref name="offset"/> or after it, opening or closing, as VS Code's
    /// <c>findNextBracket</c> finds it; null when there is none within <see cref="MaxDistance"/>.
    /// </summary>
    public static int? FindNext(
        TextDocument document,
        int offset,
        IReadOnlyList<CharacterPair> brackets,
        Func<int, int[]?> tokensOf
    ) => Scan(document, offset, forward: true, brackets, tokensOf).Select(found => (int?)found.Offset).FirstOrDefault();

    /// <summary>
    /// The language's brackets from <paramref name="from"/> on, going forward, or before it, going backward, that are
    /// code, within <see cref="MaxDistance"/> characters: each one's offset, the index of its pair, and whether it opens.
    /// </summary>
    public static IEnumerable<(int Offset, int Pair, bool IsOpen)> Scan(
        TextDocument document,
        int from,
        bool forward,
        IReadOnlyList<CharacterPair> brackets,
        Func<int, int[]?> tokensOf
    )
    {
        int start = forward ? Math.Clamp(from, 0, document.TextLength) : Math.Max(0, from - MaxDistance);
        int end = forward
            ? Math.Min(document.TextLength, start + MaxDistance)
            : Math.Clamp(from, 0, document.TextLength);
        if (start >= end || SearchCharacters(brackets) is not { } characters)
            yield break;
        using var buffer = new SearchBuffer();
        // The line of the last bracket: its bounds and its tokens.
        (int Start, int End) line = (-1, -1);
        int[]? tokens = null;
        for (int done = 0; done < end - start; )
        {
            int length = Math.Min(SearchBuffer.BlockLength, end - start - done);
            int blockStart = forward ? start + done : end - done - length;
            buffer.Read(document, blockStart, length);
            int index = forward ? -1 : length;
            while ((index = Next(index)) >= 0)
            {
                if (PairOf(buffer.Text[index], brackets) is not (int pair, bool isOpen))
                    continue;
                int offset = blockStart + index;
                if (offset < line.Start || offset > line.End)
                {
                    DocumentLine found = document.GetLineByOffset(offset);
                    line = (found.Offset, found.EndOffset);
                    tokens = tokensOf(found.LineNumber);
                }
                if (!IsIgnored(tokens, offset - line.Start))
                    yield return (offset, pair, isOpen);
            }
            done += length;
        }

        int Next(int index) =>
            forward
                ? buffer.Text[(index + 1)..].IndexOfAny(characters) is var found and >= 0
                    ? index + 1 + found
                    : -1
                : buffer.Text[..index].LastIndexOfAny(characters);
    }

    private static SearchValues<char>? SearchCharacters(IReadOnlyList<CharacterPair> brackets)
    {
        Span<char> characters =
            brackets.Count <= 64 ? stackalloc char[brackets.Count * 2] : new char[brackets.Count * 2];
        int length = 0;
        foreach (CharacterPair pair in brackets)
        {
            if (pair is { Open: [char open], Close: [char close] } && open != close)
            {
                characters[length++] = open;
                characters[length++] = close;
            }
        }
        return length == 0 ? null : SearchValues.Create(characters[..length]);
    }

    /// <summary>Copies a block directly from the rope, without creating a string or a sliced rope for each block.</summary>
    private sealed class SearchBuffer : TextWriter
    {
        public const int BlockLength = 4096;

        private readonly char[] _buffer = ArrayPool<char>.Shared.Rent(BlockLength);
        private int _length;

        public override Encoding Encoding => Encoding.Unicode;

        public ReadOnlySpan<char> Text => _buffer.AsSpan(0, _length);

        public void Read(TextDocument document, int offset, int length)
        {
            _length = 0;
            document.WriteTextTo(this, offset, length);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            buffer.AsSpan(index, count).CopyTo(_buffer.AsSpan(_length));
            _length += count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                ArrayPool<char>.Shared.Return(_buffer);
            base.Dispose(disposing);
        }
    }

    /// <summary>The pair of brackets of one character each that <paramref name="c"/> opens or closes; null for none.</summary>
    private static (int Pair, bool IsOpen)? PairOf(char c, IReadOnlyList<CharacterPair> brackets)
    {
        for (int i = 0; i < brackets.Count; i++)
        {
            if (brackets[i] is { Open: [char open], Close: [char close] } && open != close && (c == open || c == close))
                return (i, c == open);
        }
        return null;
    }
}

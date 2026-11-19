using AvaloniaEdit.Document;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Finds the bracket next to the caret and its match, and the pairs around an offset, among the language's brackets of
/// one character each. As in VS Code, brackets in strings, comments and regular expressions do not count, by the tokens
/// of their lines as last tokenized; lines without tokens count as code. Each search reads a bounded part of the text.
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
    /// in VS Code, then the one before. Brackets pair as VS Code's bracket pairs nest them
    /// (<see cref="BracketNesting"/>), so an opening bracket that a closing one of an outer pair closes has no match.
    /// Null when neither is a bracket with a match.
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
        (string opens, string closes) = Characters(brackets);
        foreach (int offset in (ReadOnlySpan<int>)[caret, caret - 1])
        {
            if (offset < 0 || offset >= document.TextLength)
                continue;
            char c = document.GetCharAt(offset);
            int open = opens.IndexOf(c);
            int close = closes.IndexOf(c);
            if (open < 0 && close < 0)
                continue;
            DocumentLine line = document.GetLineByOffset(offset);
            if (IsIgnored(tokensOf(line.LineNumber), offset - line.Offset))
                continue;
            if (open >= 0 && Closing(document, offset, open, opens, closes, tokensOf) is { } closing)
                return (offset, closing);
            if (open < 0 && OpenBefore(document, offset, opens, closes, tokensOf).Close(close) is var (_, opening))
                return (opening, offset);
        }
        return null;
    }

    /// <summary>
    /// The opening bracket that a closing one at <paramref name="offset"/> would match, within
    /// <see cref="MaxDistance"/> characters before it; null for none.
    /// </summary>
    public static int? FindOpening(TextDocument document, int offset, CharacterPair pair, Func<int, int[]?> tokensOf) =>
        Find(document, offset, pair.Open[0], pair.Close[0], forward: false, tokensOf);

    /// <summary>
    /// Whether a closing bracket of <paramref name="pair"/> after <paramref name="offset"/> closes no bracket, among all
    /// of <paramref name="brackets"/> within <see cref="MaxDistance"/> characters on each side: a closing bracket
    /// closes the closest open bracket of its kind, and those opened inside it stay unclosed, as VS Code's bracket
    /// pairs have it.
    /// </summary>
    public static bool UnmatchedClosing(
        TextDocument document,
        int offset,
        CharacterPair pair,
        IReadOnlyList<CharacterPair> brackets,
        Func<int, int[]?> tokensOf
    )
    {
        // A kind is an opening character, which may have several closing ones.
        string opens = string.Concat(brackets.Select(b => b.Open[0]).Distinct());
        string closes = string.Concat(brackets.Select(b => b.Close));
        int[] kindOf = [.. brackets.Select(b => opens.IndexOf(b.Open[0]))];
        var nesting = new BracketNesting(opens.Length);
        bool unmatched = false;
        Scan(
            document,
            Math.Max(0, offset - MaxDistance),
            Math.Min(document.TextLength, offset + MaxDistance),
            forward: true,
            opens + closes,
            tokensOf,
            (c, at) =>
            {
                if (opens.IndexOf(c) is var kind and >= 0)
                {
                    nesting.Open(kind, at);
                    return false;
                }
                return nesting.Close(kindOf[closes.IndexOf(c)]) is null
                    && (unmatched = at >= offset && c == pair.Close[0]);
            }
        );
        return unmatched;
    }

    /// <summary>
    /// The innermost pair of brackets around <paramref name="offset"/>, by their offsets, as VS Code's bracket pairs
    /// nest them (<see cref="BracketNesting"/>): the first closing bracket from the offset on that closes a bracket
    /// open at the offset, and that one, each within <see cref="MaxDistance"/> characters. Brackets open at the offset
    /// that it closes with it stay unclosed, and a closing bracket that closes none is no bracket. Null for none.
    /// </summary>
    public static (int Open, int Close)? FindEnclosing(
        TextDocument document,
        int offset,
        IReadOnlyList<CharacterPair> brackets,
        Func<int, int[]?> tokensOf
    )
    {
        (string opens, string closes) = Characters(brackets);
        BracketNesting nesting = OpenBefore(document, offset, opens, closes, tokensOf);
        int around = nesting.Depth;
        (int Open, int Close)? pair = null;
        if (around > 0)
        {
            Scan(
                document,
                offset,
                Math.Min(document.TextLength, offset + MaxDistance),
                forward: true,
                opens + closes,
                tokensOf,
                (c, at) =>
                {
                    if (opens.IndexOf(c) is var kind and >= 0)
                        nesting.Open(kind, at);
                    else if (nesting.Close(closes.IndexOf(c)) is var (index, opening) && index < around)
                        pair = (opening, at);
                    return pair is not null;
                }
            );
        }
        return pair;
    }

    /// <summary>
    /// The first bracket from <paramref name="offset"/> on, opening or closing, as VS Code's bracket pairs find it: a
    /// closing bracket that closes no bracket open there is none (<see cref="BracketNesting"/>). Within
    /// <see cref="MaxDistance"/> characters on each side; null for none.
    /// </summary>
    public static int? FindNext(
        TextDocument document,
        int offset,
        IReadOnlyList<CharacterPair> brackets,
        Func<int, int[]?> tokensOf
    )
    {
        (string opens, string closes) = Characters(brackets);
        BracketNesting nesting = OpenBefore(document, offset, opens, closes, tokensOf);
        int? next = null;
        if (opens.Length > 0)
        {
            Scan(
                document,
                offset,
                Math.Min(document.TextLength, offset + MaxDistance),
                forward: true,
                opens + closes,
                tokensOf,
                (c, at) =>
                {
                    if (opens.IndexOf(c) >= 0 || nesting.Close(closes.IndexOf(c)) is not null)
                        next = at;
                    return next is not null;
                }
            );
        }
        return next;
    }

    /// <summary>
    /// The pairs of brackets around <paramref name="offset"/>, from the innermost out, as VS Code's bracket selection
    /// ranges pair them, each kind on its own: the closing brackets from the offset on that close none of their kind
    /// after it, in order, with the opening brackets before it that none of their kind before it closes, within
    /// <see cref="MaxDistance"/> characters on each side.
    /// </summary>
    public static List<(int Open, int Close)> PairsAround(
        TextDocument document,
        int offset,
        IReadOnlyList<CharacterPair> brackets,
        Func<int, int[]?> tokensOf
    )
    {
        (string opens, string closes) = Characters(brackets);
        string characters = opens + closes;
        var pairs = new List<(int Open, int Close)>();
        if (opens.Length == 0)
            return pairs;
        // Per kind, the brackets passed that wait for their match, and the closing brackets that wait for an opening.
        int[] waiting = new int[opens.Length];
        Queue<int>[] closings = [.. opens.Select(_ => new Queue<int>())];
        int unpaired = 0;
        Scan(
            document,
            offset,
            Math.Min(document.TextLength, offset + MaxDistance),
            forward: true,
            characters,
            tokensOf,
            (c, at) =>
            {
                if (opens.IndexOf(c) is var kind and >= 0)
                    waiting[kind]++;
                else if (waiting[kind = closes.IndexOf(c)] > 0)
                    waiting[kind]--;
                else
                {
                    closings[kind].Enqueue(at);
                    unpaired++;
                }
                return false;
            }
        );
        if (unpaired == 0)
            return pairs;
        Array.Clear(waiting);
        Scan(
            document,
            Math.Max(0, offset - MaxDistance),
            offset,
            forward: false,
            characters,
            tokensOf,
            (c, at) =>
            {
                if (closes.IndexOf(c) is var kind and >= 0)
                    waiting[kind]++;
                else if (waiting[kind = opens.IndexOf(c)] > 0)
                    waiting[kind]--;
                else if (closings[kind].TryDequeue(out int close))
                {
                    pairs.Add((at, close));
                    unpaired--;
                }
                return unpaired == 0;
            }
        );
        return pairs;
    }

    /// <summary>
    /// Whether the brackets from <paramref name="start"/> to <paramref name="end"/> nest among themselves: each closing
    /// bracket closes the bracket opened last among them, and none stays open. Then the brackets open at either end are
    /// the same, and so is the pair that <see cref="FindEnclosing"/> finds around them, within its bounds.
    /// </summary>
    public static bool Nested(
        TextDocument document,
        int start,
        int end,
        IReadOnlyList<CharacterPair> brackets,
        Func<int, int[]?> tokensOf
    )
    {
        (string opens, string closes) = Characters(brackets);
        var open = new Stack<int>();
        bool crossed = false;
        Scan(
            document,
            start,
            end,
            forward: true,
            opens + closes,
            tokensOf,
            (c, _) =>
            {
                if (opens.IndexOf(c) is var kind and >= 0)
                    open.Push(kind);
                else
                    crossed = !open.TryPop(out int last) || last != closes.IndexOf(c);
                return crossed;
            }
        );
        return !crossed && open.Count == 0;
    }

    /// <summary>
    /// The closing bracket of the opening one of <paramref name="kind"/> at <paramref name="offset"/>, within
    /// <see cref="MaxDistance"/> characters after it; null for none, also when a closing bracket of a pair opened before
    /// it closes it.
    /// </summary>
    private static int? Closing(
        TextDocument document,
        int offset,
        int kind,
        string opens,
        string closes,
        Func<int, int[]?> tokensOf
    )
    {
        var nesting = new BracketNesting(opens.Length);
        nesting.Open(kind, offset);
        // The brackets open before it, read only for a closing bracket that closes none after it.
        BracketNesting? before = null;
        int? closing = null;
        bool closed = false;
        Scan(
            document,
            offset + 1,
            Math.Min(document.TextLength, offset + 1 + MaxDistance),
            forward: true,
            opens + closes,
            tokensOf,
            (c, at) =>
            {
                if (opens.IndexOf(c) is var open and >= 0)
                {
                    nesting.Open(open, at);
                    return false;
                }
                int close = closes.IndexOf(c);
                if (nesting.Close(close) is var (index, _))
                {
                    if (index == 0)
                        closing = at;
                    return index == 0;
                }
                before ??= OpenBefore(document, offset, opens, closes, tokensOf);
                return closed = before.IsOpen(close);
            }
        );
        return closed ? null : closing;
    }

    /// <summary>
    /// The brackets open at <paramref name="offset"/>, from those within <see cref="MaxDistance"/> characters before it.
    /// </summary>
    private static BracketNesting OpenBefore(
        TextDocument document,
        int offset,
        string opens,
        string closes,
        Func<int, int[]?> tokensOf
    )
    {
        var nesting = new BracketNesting(opens.Length);
        if (opens.Length == 0)
            return nesting;
        Scan(
            document,
            Math.Max(0, offset - MaxDistance),
            offset,
            forward: true,
            opens + closes,
            tokensOf,
            (c, at) =>
            {
                if (opens.IndexOf(c) is var kind and >= 0)
                    nesting.Open(kind, at);
                else
                    nesting.Close(closes.IndexOf(c));
                return false;
            }
        );
        return nesting;
    }

    /// <summary>The opening and the closing characters of the brackets of one character each, by kind.</summary>
    private static (string Opens, string Closes) Characters(IReadOnlyList<CharacterPair> brackets)
    {
        string opens = "";
        string closes = "";
        foreach (CharacterPair pair in brackets)
        {
            if (pair is { Open: [char open], Close: [char close] } && open != close)
            {
                opens += open;
                closes += close;
            }
        }
        return (opens, closes);
    }

    /// <summary>
    /// The bracket that matches the one at <paramref name="from"/>: the next closing bracket at the same depth going
    /// forward, or the opening one going backward, within <see cref="MaxDistance"/> characters.
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
        int depth = 0;
        int? found = null;
        int start = forward ? from + 1 : Math.Max(0, from - MaxDistance);
        int end = forward ? Math.Min(document.TextLength, from + 1 + MaxDistance) : from;
        Scan(
            document,
            start,
            end,
            forward,
            $"{open}{close}",
            tokensOf,
            (c, offset) =>
            {
                if (c == (forward ? open : close))
                    depth++;
                else if (depth-- == 0)
                    found = offset;
                return found is not null;
            }
        );
        return found;
    }

    /// <summary>
    /// Gives <paramref name="visit"/> each bracket of <paramref name="chars"/> from <paramref name="start"/> to
    /// <paramref name="end"/>, in code, going forward or backward, until it returns true. The text is read in blocks
    /// into a buffer, and a line's tokens only where a bracket is, so the scan allocates nothing per line.
    /// </summary>
    private static void Scan(
        TextDocument document,
        int start,
        int end,
        bool forward,
        string chars,
        Func<int, int[]?> tokensOf,
        Func<char, int, bool> visit
    )
    {
        Span<char> block = stackalloc char[4096];
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
                int offset = blockStart + index;
                if (offset < line.Start || offset > line.End)
                {
                    DocumentLine found = document.GetLineByOffset(offset);
                    line = (found.Offset, found.EndOffset);
                    tokens = tokensOf(found.LineNumber);
                }
                if (!IsIgnored(tokens, offset - line.Start) && visit(text[index], offset))
                    return;
            }
            done += length;
        }

        // The next bracket after index in the block going forward, or before it going backward; -1 for none.
        int Next(ReadOnlySpan<char> text, int index) =>
            forward
                ? text[(index + 1)..].IndexOfAny(chars) is var found and >= 0
                    ? index + 1 + found
                    : -1
                : text[..index].LastIndexOfAny(chars);
    }

    /// <summary>Whether a bracket at <paramref name="column"/> is in a string, comment or regular expression.</summary>
    private static bool IsIgnored(int[]? tokens, int column) =>
        tokens is not null && TokenTypes.At(tokens, column) != TokenType.Other;
}

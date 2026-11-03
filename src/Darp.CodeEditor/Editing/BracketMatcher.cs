using AvaloniaEdit.Document;

namespace Darp.CodeEditor.Editing;

/// <summary>Whether a position is code, or inside a comment or string, as far as syntax highlighting knows.</summary>
internal enum TokenKind
{
    Code,
    Comment,
    String,

    /// <summary>Highlighting has no answer yet, for example while the grammar loads.</summary>
    Unknown,
}

/// <summary>Finds matching brackets, ignoring brackets in comments and strings.</summary>
internal static class BracketMatcher
{
    /// <summary>How far to look for a match, so the search stays well under a millisecond.</summary>
    public const int MaxDistance = 20_000;

    /// <summary>
    /// The bracket pair at the caret: the bracket just before it, else the one just after it, and its match.
    /// </summary>
    public static (int Open, int Close)? FindPairAt(
        TextDocument document,
        int caret,
        IReadOnlyList<BracketPair> brackets,
        Func<int, TokenKind> kindAt
    )
    {
        foreach (int offset in (ReadOnlySpan<int>)[caret - 1, caret])
        {
            if (offset < 0 || offset >= document.TextLength || kindAt(offset) != TokenKind.Code)
                continue;
            char c = document.GetCharAt(offset);
            foreach (BracketPair pair in brackets)
            {
                if (c == pair.Open && FindClose(document, offset, pair, kindAt) is { } close)
                    return (offset, close);
                if (c == pair.Close && FindOpen(document, offset, pair, kindAt) is { } open)
                    return (open, offset);
            }
        }
        return null;
    }

    /// <summary>The opening bracket that the closing bracket at <paramref name="close"/> (or a new one there) matches.</summary>
    public static int? FindOpen(TextDocument document, int close, BracketPair pair, Func<int, TokenKind> kindAt)
    {
        int start = Math.Max(0, close - MaxDistance);
        string text = document.GetText(start, close - start);
        int depth = 0;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            char c = text[i];
            if ((c != pair.Open && c != pair.Close) || kindAt(start + i) != TokenKind.Code)
                continue;
            if (c == pair.Close)
                depth++;
            else if (depth-- == 0)
                return start + i;
        }
        return null;
    }

    public static int? FindClose(TextDocument document, int open, BracketPair pair, Func<int, TokenKind> kindAt)
    {
        int start = open + 1;
        string text = document.GetText(start, Math.Min(document.TextLength, open + MaxDistance) - start);
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((c != pair.Open && c != pair.Close) || kindAt(start + i) != TokenKind.Code)
                continue;
            if (c == pair.Open)
                depth++;
            else if (depth-- == 0)
                return start + i;
        }
        return null;
    }
}

using System.Globalization;
using System.Text;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Indentation whitespace in a document's indentation, as VS Code computes it: by its width in columns, with tabs
/// to the next stop.
/// </summary>
internal readonly struct Indenting(Indentation indentation)
{
    private readonly Indentation _indentation = indentation;

    /// <summary>
    /// The leading whitespace of <paramref name="text"/> in the same width, in tabs and spaces or in spaces, as the
    /// document indents, with the rest after it as it is, such as the <c> * </c> of a doc comment.
    /// </summary>
    public string Normalize(string text)
    {
        int whitespace = IndentRules.Leading(text);
        string normalized = Whitespace(Width(text.AsSpan(0, whitespace)));
        return whitespace == text.Length ? normalized : normalized + text[whitespace..];
    }

    /// <summary>One level more: to the next stop.</summary>
    public string Shift(string whitespace) =>
        Whitespace((Width(whitespace) / _indentation.Size + 1) * _indentation.Size);

    /// <summary>One level less: to the stop before, or none.</summary>
    public string Unshift(string whitespace)
    {
        int size = _indentation.Size;
        return Whitespace(Math.Max(0, (Width(whitespace) + size - 1) / size - 1) * size);
    }

    /// <summary>
    /// The offset in <paramref name="line"/>'s leading whitespace where <paramref name="column"/> starts, before a tab
    /// that ends past it, as VS Code places a line comment; the end of the whitespace when it is narrower.
    /// </summary>
    public int OffsetOf(string line, int column)
    {
        int columns = 0;
        int offset = 0;
        for (; offset < line.Length && line[offset] is ' ' or '\t'; offset++)
        {
            int next = line[offset] == '\t' ? (columns / _indentation.Size + 1) * _indentation.Size : columns + 1;
            if (next > column)
                return offset;
            columns = next;
        }
        return offset;
    }

    /// <summary>
    /// The column after <paramref name="text"/> when it starts at <paramref name="column"/>, as the text shows: a tab to
    /// the next stop, and a character with its combining marks one column, or two when it is wide, as East Asian wide
    /// and full-width characters and emoji are.
    /// </summary>
    public int Width(ReadOnlySpan<char> text, int column = 0)
    {
        int columns = column;
        for (int i = 0; i < text.Length; )
        {
            char c = text[i];
            if (c == '\t')
            {
                columns = (columns / _indentation.Size + 1) * _indentation.Size;
                i++;
            }
            // ASCII not followed by a combining mark, as indentation always is, needs no grapheme.
            else if (c < 0x80 && (i + 1 == text.Length || text[i + 1] < 0x80))
            {
                columns++;
                i++;
            }
            else
            {
                Rune.DecodeFromUtf16(text[i..], out Rune first, out _);
                columns += IsWide(first.Value) ? 2 : 1;
                i += StringInfo.GetNextTextElementLength(text[i..]);
            }
        }
        return columns;
    }

    /// <summary>Whether a character shows two columns wide: East Asian wide and full-width ones, and emoji, by block.</summary>
    private static bool IsWide(int c) =>
        c
            is (>= 0x2E80 and <= 0xD7AF)
                or (>= 0xF900 and <= 0xFAFF)
                or (>= 0xFF01 and <= 0xFF5E)
                or (>= 0x1F1E6 and <= 0x1F1FF)
                or 0x231A
                or 0x231B
                or 0x23F0
                or 0x23F3
                or (>= 0x2600 and <= 0x27BF)
                or 0x2B50
                or 0x2B55
                or (>= 0x1F300 and <= 0x1F64F)
                or (>= 0x1F680 and <= 0x1F6FC)
                or (>= 0x1F7E0 and <= 0x1F7F0)
                or (>= 0x1F900 and <= 0x1F9FF)
                or (>= 0x1FA70 and <= 0x1FAF6);

    /// <summary>Whitespace <paramref name="columns"/> wide, in tabs and spaces or in spaces, as the document indents.</summary>
    public string Whitespace(int columns) =>
        _indentation.UseTabs
            ? new string('\t', columns / _indentation.Size) + new string(' ', columns % _indentation.Size)
            : new string(' ', columns);

    /// <summary>The length of <see cref="Whitespace"/> for <paramref name="columns"/>, without making it.</summary>
    public int Length(int columns) =>
        _indentation.UseTabs ? columns / _indentation.Size + columns % _indentation.Size : columns;
}

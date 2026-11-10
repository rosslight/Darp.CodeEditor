namespace Darp.CodeEditor.Editing;

/// <summary>
/// Indentation whitespace in a document's indentation, as VS Code computes it: by its width in columns, with tabs
/// to the next stop.
/// </summary>
internal readonly struct Indenting(Indentation indentation)
{
    private readonly Indentation _indentation = indentation;

    /// <summary>The same width, in tabs and spaces or in spaces, as the document indents.</summary>
    public string Normalize(string whitespace) => Whitespace(Width(whitespace));

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
    /// The column after <paramref name="whitespace"/> when it starts at <paramref name="column"/>, tabs to the next
    /// stop: its width in columns, from the start of a line.
    /// </summary>
    public int Width(ReadOnlySpan<char> whitespace, int column = 0)
    {
        int columns = column;
        foreach (char c in whitespace)
            columns = c == '\t' ? (columns / _indentation.Size + 1) * _indentation.Size : columns + 1;
        return columns;
    }

    /// <summary>Whitespace <paramref name="columns"/> wide, in tabs and spaces or in spaces, as the document indents.</summary>
    public string Whitespace(int columns) =>
        _indentation.UseTabs
            ? new string('\t', columns / _indentation.Size) + new string(' ', columns % _indentation.Size)
            : new string(' ', columns);

    /// <summary>The length of <see cref="Whitespace"/> for <paramref name="columns"/>, without making it.</summary>
    public int Length(int columns) =>
        _indentation.UseTabs ? columns / _indentation.Size + columns % _indentation.Size : columns;
}

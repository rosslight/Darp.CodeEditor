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

    private int Width(string whitespace)
    {
        int columns = 0;
        foreach (char c in whitespace)
            columns = c == '\t' ? (columns / _indentation.Size + 1) * _indentation.Size : columns + 1;
        return columns;
    }

    private string Whitespace(int columns) =>
        _indentation.UseTabs
            ? new string('\t', columns / _indentation.Size) + new string(' ', columns % _indentation.Size)
            : new string(' ', columns);
}

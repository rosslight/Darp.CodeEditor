using System.Buffers;
using Darp.CodeEditor.Lsp.Protocol;
using Microsoft.CodeAnalysis.Text;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary>Converts between offsets and LSP positions: lines end at \n, \r\n or \r; characters are UTF-16 units.</summary>
/// <remarks>
/// Roslyn's <see cref="SourceText.Lines"/> also ends lines at U+0085, U+2028 and U+2029, so its line numbers can
/// differ from the client's.
/// </remarks>
internal sealed class LineMap
{
    private readonly int[] _starts;
    private readonly int[] _ends;

    private LineMap(int[] starts, int[] ends)
    {
        _starts = starts;
        _ends = ends;
    }

    public int LineCount => _starts.Length;

    public static LineMap Create(SourceText text)
    {
        var starts = new List<int> { 0 };
        var ends = new List<int>();
        char[] buffer = ArrayPool<char>.Shared.Rent(4096);
        int lastCarriageReturn = -2;
        for (int chunk = 0; chunk < text.Length; chunk += buffer.Length)
        {
            int count = Math.Min(buffer.Length, text.Length - chunk);
            text.CopyTo(chunk, buffer, 0, count);
            for (int i = 0; i < count; i++)
            {
                int offset = chunk + i;
                if (buffer[i] == '\r')
                {
                    ends.Add(offset);
                    starts.Add(offset + 1);
                    lastCarriageReturn = offset;
                }
                else if (buffer[i] == '\n')
                {
                    if (lastCarriageReturn == offset - 1)
                    {
                        starts[^1] = offset + 1;
                    }
                    else
                    {
                        ends.Add(offset);
                        starts.Add(offset + 1);
                    }
                }
            }
        }
        ArrayPool<char>.Shared.Return(buffer);
        ends.Add(text.Length);
        return new LineMap([.. starts], [.. ends]);
    }

    /// <summary>The offset of <paramref name="position"/>. Positions past a line end or the last line are clamped.</summary>
    public int GetOffset(Position position)
    {
        if (position.Line < 0)
            return 0;
        if (position.Line >= _starts.Length)
            return _ends[^1];
        int start = _starts[position.Line];
        return start + Math.Clamp(position.Character, 0, _ends[position.Line] - start);
    }

    public Position GetPosition(int offset)
    {
        int line = GetLine(offset);
        return new Position(line, offset - _starts[line]);
    }

    public int GetLine(int offset)
    {
        int index = Array.BinarySearch(_starts, offset);
        return index >= 0 ? index : ~index - 1;
    }

    public int GetLineStart(int line) => _starts[line];

    /// <summary>The offset where the line's text ends, before its line break.</summary>
    public int GetLineEnd(int line) => _ends[line];

    public DocumentRange GetRange(TextSpan span) => new(GetPosition(span.Start), GetPosition(span.End));

    public TextSpan GetSpan(DocumentRange range) => TextSpan.FromBounds(GetOffset(range.Start), GetOffset(range.End));
}

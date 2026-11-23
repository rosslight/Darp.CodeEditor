using System.Buffers;
using Darp.CodeEditor.Lsp;
using Microsoft.CodeAnalysis.Text;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>
/// Converts between offsets and the client's line positions. The client's lines end at <c>\r\n</c>, <c>\n</c> and
/// <c>\r</c>; Roslyn's also end at three Unicode line breaks. A script that may hold one gets its lines from a copy with
/// spaces in their place, which keeps every offset, and any other script uses Roslyn's lines as they are.
/// </summary>
internal sealed class LineMap
{
    private static readonly SearchValues<char> s_unicodeBreaks = SearchValues.Create("\u0085\u2028\u2029");

    private readonly TextLineCollection _lines;

    /// <summary>Stays true after the last one is deleted: that costs a copy of the text per version, nothing else.</summary>
    private readonly bool _mayHaveUnicodeBreaks;

    private LineMap(SourceText source, bool mayHaveUnicodeBreaks)
    {
        _mayHaveUnicodeBreaks = mayHaveUnicodeBreaks;
        _lines = mayHaveUnicodeBreaks
            ? SourceText
                .From(source.ToString().Replace('\u0085', ' ').Replace('\u2028', ' ').Replace('\u2029', ' '))
                .Lines
            : source.Lines;
    }

    /// <summary>The map of <paramref name="source"/>, whose text is <paramref name="text"/>.</summary>
    public static LineMap Of(SourceText source, string text) => new(source, text.AsSpan().ContainsAny(s_unicodeBreaks));

    /// <summary>This map for <paramref name="source"/>, the text after <paramref name="inserted"/> went in.</summary>
    public LineMap WithChange(SourceText source, string inserted) =>
        new(source, _mayHaveUnicodeBreaks || inserted.AsSpan().ContainsAny(s_unicodeBreaks));

    /// <summary>The offset of <paramref name="position"/>, clamped to its line and to the text.</summary>
    public int ToOffset(LspPosition position)
    {
        if (position.Line >= _lines.Count)
            return _lines[^1].End;
        TextLine line = _lines[position.Line];
        return (int)Math.Min((long)line.Start + position.Character, line.End);
    }

    /// <summary>The client's line and column of <paramref name="offset"/>.</summary>
    public LspPosition ToPosition(int offset)
    {
        LinePosition position = _lines.GetLinePosition(offset);
        return new LspPosition(position.Line, position.Character);
    }
}

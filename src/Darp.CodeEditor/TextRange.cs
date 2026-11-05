namespace Darp.CodeEditor;

/// <summary>A span of a document's text, in offsets: UTF-16 code units from the start of the text.</summary>
/// <remarks>Ranges are not checked when created. Members that take a range clamp it to the text.</remarks>
/// <param name="Start">The offset of the first character.</param>
/// <param name="Length">The number of UTF-16 code units.</param>
public readonly record struct TextRange(int Start, int Length)
{
    /// <summary>The offset after the last character.</summary>
    public int End => Start + Length;

    /// <summary>The span from <paramref name="start"/> to <paramref name="end"/>.</summary>
    public static TextRange FromBounds(int start, int end) => new(start, end - start);
}

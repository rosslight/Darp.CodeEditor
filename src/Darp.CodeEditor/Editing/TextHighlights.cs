using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Darp.CodeEditor.Editing;

/// <summary>Draws ranges of the text in a color, as VS Code draws its decorations of occurrences.</summary>
internal static class TextHighlights
{
    /// <summary>
    /// Draws the ranges of <paramref name="highlights"/> that are in view, each in the brush and pen that
    /// <paramref name="style"/> gives it; a range that <paramref name="style"/> gives no brush is not drawn.
    /// </summary>
    public static void Draw<T>(
        TextView textView,
        DrawingContext context,
        TextSegmentCollection<T> highlights,
        Func<T, (IBrush? Brush, IPen? Pen)> style
    )
        where T : TextSegment
    {
        if (!textView.VisualLinesValid)
            return;
        if (textView.VisualLines is not [VisualLine first, ..] || textView.VisualLines[^1] is not { } last)
            return;
        int start = first.FirstDocumentLine.Offset;
        foreach (T highlight in highlights.FindOverlappingSegments(start, last.LastDocumentLine.EndOffset - start))
        {
            (IBrush? brush, IPen? pen) = style(highlight);
            if (brush is null)
                continue;
            // Rectangles rather than a geometry each, which cost a frame tens of kilobytes per highlight.
            if (WithinTextLine(textView, highlight) is { } rectangle)
            {
                context.DrawRectangle(brush, pen, PixelAligned(rectangle));
                continue;
            }
            foreach (Rect part in BackgroundGeometryBuilder.GetRectsForSegment(textView, highlight))
                context.DrawRectangle(brush, pen, PixelAligned(part));
        }
    }

    /// <summary>
    /// Where the text view shows <paramref name="segment"/>, when it lies within one row of a line in view, as most
    /// highlights do: from the positions of its ends in the row, which cost far less than the text's bounds. Null
    /// otherwise.
    /// </summary>
    private static Rect? WithinTextLine(TextView textView, ISegment segment)
    {
        DocumentLine line = textView.Document.GetLineByOffset(segment.Offset);
        if (segment.EndOffset > line.EndOffset || textView.GetVisualLine(line.LineNumber) is not { } visualLine)
            return null;
        int lineStart = visualLine.FirstDocumentLine.Offset;
        int start = visualLine.GetVisualColumn(segment.Offset - lineStart);
        int end = visualLine.GetVisualColumn(segment.EndOffset - lineStart);
        TextLine row = visualLine.GetTextLine(start);
        if (visualLine.GetTextLine(end, isAtEndOfLine: true) != row)
            return null;
        double left = visualLine.GetTextLineVisualXPosition(row, start);
        double right = visualLine.GetTextLineVisualXPosition(row, end);
        double top = visualLine.GetTextLineVisualYPosition(row, VisualYPosition.LineTop);
        Vector scroll = textView.ScrollOffset;
        return new Rect(left - scroll.X, top - scroll.Y, right - left, row.Height);
    }

    /// <summary>A rectangle with its edges on whole pixels, as the text view's other backgrounds are drawn.</summary>
    private static Rect PixelAligned(Rect rectangle)
    {
        double left = Math.Round(rectangle.Left);
        double top = Math.Round(rectangle.Top);
        return new Rect(left, top, Math.Round(rectangle.Right) - left, Math.Round(rectangle.Bottom) - top);
    }

    /// <summary>
    /// <paramref name="brush"/> at half its opacity, as VS Code shows the selection's occurrences while the text doesn't
    /// have the keyboard.
    /// </summary>
    public static IBrush Faded(IBrush brush) =>
        brush is ISolidColorBrush solid
            ? new ImmutableSolidColorBrush(
                Color.FromArgb((byte)Math.Round(solid.Color.A / 2.0), solid.Color.R, solid.Color.G, solid.Color.B)
            )
            : brush;
}

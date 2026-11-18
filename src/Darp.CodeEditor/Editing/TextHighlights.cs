using Avalonia.Media;
using Avalonia.Media.Immutable;
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
            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true };
            builder.AddSegment(textView, highlight);
            if (builder.CreateGeometry() is { } geometry)
                context.DrawGeometry(brush, pen, geometry);
        }
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

using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Highlighting;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Draws the spaces and tabs inside the selection as faint dots and arrows, in the color theme's whitespace color, as
/// VS Code's <c>renderWhitespace: selection</c> does. Nothing outside the selection.
/// </summary>
internal sealed class WhitespaceLayer(TextArea textArea) : SelectionOverlay(textArea)
{
    private TextAreaColors? _colors;

    /// <summary>The colors of the color theme, once it is loaded.</summary>
    public TextAreaColors? Colors
    {
        set
        {
            _colors = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        TextView textView = TextArea.TextView;
        Selection selection = TextArea.Selection;
        if (_colors is not { } colors || selection.IsEmpty || !textView.VisualLinesValid)
            return;
        var pen = new Pen(colors.Whitespace);
        double cell = textView.WideSpaceWidth;
        foreach (SelectionSegment segment in selection.Segments)
        {
            foreach (VisualLine line in textView.VisualLines)
            {
                int lineStart = line.FirstDocumentLine.Offset;
                int start = Math.Max(segment.StartOffset, lineStart);
                int end = Math.Min(segment.EndOffset, line.LastDocumentLine.EndOffset);
                if (start >= end)
                    continue;
                string text = textView.Document.GetText(start, end - start);
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] is not (' ' or '\t'))
                        continue;
                    int column = line.GetVisualColumn(start + i - lineStart);
                    Point left = line.GetVisualPosition(column, VisualYPosition.TextMiddle) - textView.ScrollOffset;
                    if (left.X + cell < 0 || left.X > Bounds.Width)
                        continue;
                    // Whole pixels, so that the marks stay sharp.
                    double x = Math.Round(left.X);
                    double y = Math.Round(left.Y);
                    if (text[i] == ' ')
                        context.FillRectangle(colors.Whitespace, new Rect(x + Math.Round(cell / 2) - 1, y - 1, 2, 2));
                    else
                        DrawArrow(context, pen, x + 1, x + Math.Max(3, Math.Round(cell) - 1), y + 0.5);
                }
            }
        }
    }

    /// <summary>An arrow to the right, as VS Code draws a tab.</summary>
    private static void DrawArrow(DrawingContext context, IPen pen, double from, double to, double y)
    {
        double head = Math.Max(2, Math.Round((to - from) / 3));
        context.DrawLine(pen, new Point(from, y), new Point(to, y));
        context.DrawLine(pen, new Point(to - head, y - head), new Point(to, y));
        context.DrawLine(pen, new Point(to - head, y + head), new Point(to, y));
    }
}

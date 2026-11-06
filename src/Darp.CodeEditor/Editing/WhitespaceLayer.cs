using Avalonia;
using Avalonia.Controls;
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
/// <remarks>
/// A layer of the text view of its own, above the selection, which would hide marks drawn in the layers below it, and
/// below the text. It draws again when the selection, the lines or the scroll position change.
/// </remarks>
internal sealed class WhitespaceLayer : Control, IDisposable
{
    private readonly TextArea _textArea;
    private TextAreaColors? _colors;

    public WhitespaceLayer(TextArea textArea)
    {
        _textArea = textArea;
        IsHitTestVisible = false;
        textArea.SelectionChanged += OnChanged;
        textArea.TextView.VisualLinesChanged += OnChanged;
        textArea.TextView.ScrollOffsetChanged += OnChanged;
        textArea.TextView.InsertLayer(this, KnownLayer.Selection, LayerInsertionPosition.Above);
    }

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
        TextView textView = _textArea.TextView;
        Selection selection = _textArea.Selection;
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

    public void Dispose()
    {
        _textArea.SelectionChanged -= OnChanged;
        _textArea.TextView.VisualLinesChanged -= OnChanged;
        _textArea.TextView.ScrollOffsetChanged -= OnChanged;
        _textArea.TextView.Layers.Remove(this);
    }

    /// <summary>An arrow to the right, as VS Code draws a tab.</summary>
    private static void DrawArrow(DrawingContext context, IPen pen, double from, double to, double y)
    {
        double head = Math.Max(2, Math.Round((to - from) / 3));
        context.DrawLine(pen, new Point(from, y), new Point(to, y));
        context.DrawLine(pen, new Point(to - head, y - head), new Point(to, y));
        context.DrawLine(pen, new Point(to - head, y + head), new Point(to, y));
    }

    private void OnChanged(object? sender, EventArgs e) => InvalidateVisual();
}

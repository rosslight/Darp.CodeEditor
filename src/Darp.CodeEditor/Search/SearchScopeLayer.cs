using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Highlighting;

namespace Darp.CodeEditor.Search;

/// <summary>
/// Highlights the lines that Find in Selection searches, across the text area's width, in the color theme's
/// <c>editor.findRangeHighlightBackground</c> and border, as VS Code does: behind the selection and the text.
/// </summary>
internal sealed class SearchScopeLayer : IBackgroundRenderer, IDisposable
{
    private readonly TextView _textView;
    private TextAreaColors? _colors;
    private TextSegmentCollection<TextSegment>? _scope;

    public SearchScopeLayer(TextView textView)
    {
        _textView = textView;
        textView.BackgroundRenderers.Add(this);
    }

    /// <summary>Behind the selection, in the layer that the text view draws itself whenever a layer changes.</summary>
    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>The colors of the color theme, once it is loaded.</summary>
    public TextAreaColors? Colors
    {
        set
        {
            _colors = value;
            _textView.InvalidateLayer(Layer);
        }
    }

    /// <summary>The ranges whose lines to highlight, which follow the edits; null for none.</summary>
    public TextSegmentCollection<TextSegment>? Scope
    {
        set
        {
            _scope = value;
            _textView.InvalidateLayer(Layer);
        }
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_scope is not { Count: > 0 } scope || _colors is not { } colors || !textView.VisualLinesValid)
            return;
        foreach (VisualLine line in textView.VisualLines)
        {
            int start = line.FirstDocumentLine.Offset;
            if (scope.FindOverlappingSegments(start, line.LastDocumentLine.EndOffset - start).Count == 0)
                continue;
            var rectangle = new Rect(0, line.VisualTop - textView.VerticalOffset, textView.Bounds.Width, line.Height);
            drawingContext.DrawRectangle(colors.FindRange, colors.FindRangeBorder, rectangle);
        }
    }

    public void Dispose() => _textView.BackgroundRenderers.Remove(this);
}

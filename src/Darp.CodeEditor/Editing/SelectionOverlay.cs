using Avalonia.Controls;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// A layer of the text view above the selection, which would hide marks drawn in the layers below it, and below the
/// text. It draws again when the selection, the lines or the scroll position change, until disposed.
/// </summary>
internal abstract class SelectionOverlay : Control, IDisposable
{
    protected SelectionOverlay(TextArea textArea)
    {
        TextArea = textArea;
        IsHitTestVisible = false;
        textArea.SelectionChanged += OnChanged;
        textArea.TextView.VisualLinesChanged += OnChanged;
        textArea.TextView.ScrollOffsetChanged += OnChanged;
        textArea.TextView.InsertLayer(this, KnownLayer.Selection, LayerInsertionPosition.Above);
    }

    protected TextArea TextArea { get; }

    public virtual void Dispose()
    {
        TextArea.SelectionChanged -= OnChanged;
        TextArea.TextView.VisualLinesChanged -= OnChanged;
        TextArea.TextView.ScrollOffsetChanged -= OnChanged;
        TextArea.TextView.Layers.Remove(this);
    }

    private void OnChanged(object? sender, EventArgs e) => InvalidateVisual();
}

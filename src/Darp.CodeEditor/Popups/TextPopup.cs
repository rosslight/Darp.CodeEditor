using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// Content anchored to a line of text, drawn on the window's overlay layer. It never creates a native window: closing
/// one waits for the compositor synchronously (tens of milliseconds per keystroke on Windows), and opening one stalls
/// with software rendering on Linux. It does not use Avalonia's <see cref="Popup"/> either, whose host, template and
/// positioner made the first popup of a JIT-compiled app take 15 ms.
/// </summary>
/// <remarks>
/// The content opens on the preferred side of the line, or on the other side when only that one has room. When
/// neither has room it takes the larger side and is limited to its height. It moves left at the window's right edge.
/// Above the line it is attached by its bottom edge, so it stays next to the line while its height changes. While
/// open, it follows scrolling and resizing, and it closes when its line leaves the view or the editor leaves the
/// window.
/// </remarks>
internal sealed class TextPopup
{
    private readonly CodeEditor _editor;
    private readonly Control _content;
    private readonly bool _preferAbove;
    private OverlayLayer? _layer;
    private int _offset;
    private double? _x;

    /// <param name="above">True to prefer the side above the line (signature help), false for below (completion).</param>
    public TextPopup(CodeEditor editor, Control content, bool above)
    {
        _editor = editor;
        _content = content;
        _preferAbove = above;
    }

    public bool IsOpen => _layer is not null;

    /// <summary>Moves the content left (negative) or right of its anchor, for example to align list text with a word.</summary>
    public double HorizontalOffset { get; set; }

    public event EventHandler? Closed;

    /// <summary>Opens the content at the line of <paramref name="offset"/>, or moves it there when it is open.</summary>
    /// <param name="x">The horizontal anchor in text view coordinates; null for the x of <paramref name="offset"/>.</param>
    public void Show(int offset, double? x = null)
    {
        _offset = offset;
        _x = x;
        if (_layer is null)
        {
            if (OverlayLayer.GetOverlayLayer(_editor) is not { } layer)
                return;
            _layer = layer;
            layer.Children.Add(_content);
            layer.SizeChanged += OnLayerSizeChanged;
            _editor.TextArea.TextView.ScrollOffsetChanged += OnScrollOffsetChanged;
            _editor.DetachedFromVisualTree += OnEditorDetached;
        }
        if (!Place())
            Hide();
    }

    public void Hide()
    {
        if (_layer is not { } layer)
            return;
        _layer = null;
        layer.Children.Remove(_content);
        layer.SizeChanged -= OnLayerSizeChanged;
        _editor.TextArea.TextView.ScrollOffsetChanged -= OnScrollOffsetChanged;
        _editor.DetachedFromVisualTree -= OnEditorDetached;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void OnScrollOffsetChanged(object? sender, EventArgs e)
    {
        if (!Place())
            Hide();
    }

    private void OnLayerSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!Place())
            Hide();
    }

    private void OnEditorDetached(object? sender, VisualTreeAttachmentEventArgs e) => Hide();

    /// <summary>Places the content next to its line as shown now. Returns false when the line is out of view.</summary>
    private bool Place()
    {
        if (_layer is not { } layer || AnchorInLayer(layer) is not { } anchor)
            return false;
        Size available = layer.Bounds.Size;
        _content.MaxHeight = double.PositiveInfinity;
        _content.MaxWidth = available.Width;
        _content.Measure(available);
        Size size = _content.DesiredSize;

        double below = available.Height - anchor.Bottom;
        double above = anchor.Top;
        bool fitsPreferred = size.Height <= (_preferAbove ? above : below);
        bool fitsOther = size.Height <= (_preferAbove ? below : above);
        bool placeAbove = fitsPreferred ? _preferAbove
            : fitsOther ? !_preferAbove
            : above > below;
        _content.MaxHeight = Math.Max(0, placeAbove ? above : below);
        if (placeAbove)
        {
            _content.ClearValue(Canvas.TopProperty);
            Canvas.SetBottom(_content, available.Height - anchor.Top);
        }
        else
        {
            _content.ClearValue(Canvas.BottomProperty);
            Canvas.SetTop(_content, anchor.Bottom);
        }
        Canvas.SetLeft(_content, Math.Clamp(anchor.X + HorizontalOffset, 0, Math.Max(0, available.Width - size.Width)));
        return true;
    }

    /// <summary>The anchor line in layer coordinates, or null when it is out of view.</summary>
    private Rect? AnchorInLayer(OverlayLayer layer)
    {
        TextView textView = _editor.TextArea.TextView;
        TextDocument document = _editor.Document;
        if (_offset > document.TextLength || !textView.IsAttachedToVisualTree())
            return null;
        var position = new TextViewPosition(document.GetLocation(_offset));
        Vector scroll = textView.ScrollOffset;
        Point top = textView.GetVisualPosition(position, VisualYPosition.LineTop) - scroll;
        Point bottom = textView.GetVisualPosition(position, VisualYPosition.LineBottom) - scroll;
        if (bottom.Y <= 0 || top.Y >= textView.Bounds.Height)
            return null;
        Point? origin = textView.TranslatePoint(new Point(_x ?? top.X, top.Y), layer);
        return origin is { } o ? new Rect(o.X, o.Y, 1, bottom.Y - top.Y) : null;
    }
}

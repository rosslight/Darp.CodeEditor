using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// Content next to a place in an editor's text, in the window's overlay layer: it stays inside the window, and opens
/// without an OS window. It takes the editor's theme variant, so that a <c>ThemeVariantScope</c> around the editor
/// reaches it, and the app's font where the editor is, not the editor's monospace one.
/// </summary>
/// <remarks>
/// It opens above the place's line, or below it when only that side has room; when neither has, it takes the larger
/// side and is limited to its height. Above the line it keeps its bottom edge there, so it stays next to the line when
/// its content grows. It moves left at the window's right edge.
/// </remarks>
internal sealed class EditorPopup
{
    private readonly CodeEditor _editor;
    private readonly ThemeVariantScope _root;
    private OverlayLayer? _layer;

    /// <summary>The bindings to the editor's theme variant and to the font around it, while open.</summary>
    private IDisposable[] _bindings = [];

    public EditorPopup(CodeEditor editor, Control content)
    {
        _editor = editor;
        _root = new ThemeVariantScope { Child = content };
    }

    /// <summary>The popup's outermost control, for the pointer over it.</summary>
    public Control Root => _root;

    public bool IsOpen => _layer is not null;

    /// <summary>
    /// Opens the popup next to <paramref name="anchor"/>, a character's box in <paramref name="relativeTo"/>, or moves
    /// it there; also after its content changed.
    /// </summary>
    public void Show(Visual relativeTo, Rect anchor)
    {
        if (_layer is null)
        {
            if (OverlayLayer.GetOverlayLayer(_editor) is not { } layer)
                return;
            _layer = layer;
            var bindings = new List<IDisposable>
            {
                _root.Bind(
                    ThemeVariantScope.RequestedThemeVariantProperty,
                    _editor.GetObservable(ThemeVariantScope.ActualThemeVariantProperty)
                ),
            };
            if (_editor.GetVisualParent() is AvaloniaObject around)
            {
                bindings.Add(
                    _root.Bind(TextElement.FontFamilyProperty, around.GetObservable(TextElement.FontFamilyProperty))
                );
                bindings.Add(
                    _root.Bind(TextElement.FontSizeProperty, around.GetObservable(TextElement.FontSizeProperty))
                );
            }
            _bindings = [.. bindings];
            layer.Children.Add(_root);
        }
        if (!Place(_layer, relativeTo, anchor))
            Hide();
    }

    public void Hide()
    {
        if (_layer is not { } layer)
            return;
        _layer = null;
        layer.Children.Remove(_root);
        foreach (IDisposable binding in _bindings)
            binding.Dispose();
        _bindings = [];
    }

    /// <returns>False when the anchor is not in the layer's window.</returns>
    private bool Place(OverlayLayer layer, Visual relativeTo, Rect anchor)
    {
        if (
            relativeTo.TranslatePoint(anchor.TopLeft, layer) is not { } top
            || relativeTo.TranslatePoint(anchor.BottomLeft, layer) is not { } bottom
        )
        {
            return false;
        }
        Size space = layer.Bounds.Size;
        _root.MaxHeight = double.PositiveInfinity;
        _root.Measure(new Size(space.Width, double.PositiveInfinity));
        Size size = _root.DesiredSize;
        double above = top.Y;
        double below = space.Height - bottom.Y;
        bool placeAbove = size.Height <= above || (size.Height > below && above >= below);
        _root.MaxHeight = Math.Max(0, placeAbove ? above : below);
        Canvas.SetLeft(_root, Math.Clamp(top.X, 0, Math.Max(0, space.Width - size.Width)));
        if (placeAbove)
        {
            _root.ClearValue(Canvas.TopProperty);
            Canvas.SetBottom(_root, space.Height - top.Y);
        }
        else
        {
            _root.ClearValue(Canvas.BottomProperty);
            Canvas.SetTop(_root, bottom.Y);
        }
        return true;
    }
}

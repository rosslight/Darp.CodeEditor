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
/// <see cref="PopupPlacement"/> decides where it goes.
/// </summary>
/// <remarks>
/// While open, it is placed again after each layout pass in which the window or its place moved, as when the window is
/// resized or the text reflows, and it closes when its place's line leaves the view it is in or the window, which
/// <see cref="LeftView"/> reports.
/// </remarks>
internal sealed class EditorPopup
{
    private readonly CodeEditor _editor;
    private readonly ThemeVariantScope _root;
    private readonly PopupSide _side;
    private OverlayLayer? _layer;

    /// <summary>The bindings to the editor's theme variant and to the font around it, while open.</summary>
    private IDisposable[] _bindings = [];

    /// <summary>The visual of the place, and the place: a character's box in it, read again at each placing.</summary>
    private (Visual RelativeTo, Func<Rect> Anchor)? _place;

    /// <summary>The window's size, and the place's line and left edge in it, at the last placing; null to place again.</summary>
    private (Size Space, Rect Line, double Left)? _placed;

    /// <param name="editor">The editor whose text the popup is next to.</param>
    /// <param name="content">What the popup shows.</param>
    /// <param name="side">The side of its line it opens on where both have room.</param>
    public EditorPopup(CodeEditor editor, Control content, PopupSide side = PopupSide.Above)
    {
        _editor = editor;
        _root = new ThemeVariantScope { Child = content };
        _side = side;
    }

    /// <summary>Raised when the popup closed because its place's line left the view.</summary>
    public event EventHandler? LeftView;

    /// <summary>The popup's outermost control, for the pointer over it.</summary>
    public Control Root => _root;

    public bool IsOpen => _layer is not null;

    /// <summary>
    /// Opens the popup next to <paramref name="anchor"/>, a character's box in <paramref name="relativeTo"/>, or moves
    /// it there; also after its content changed.
    /// </summary>
    public void Show(Visual relativeTo, Func<Rect> anchor)
    {
        _place = (relativeTo, anchor);
        _placed = null;
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
            layer.LayoutUpdated += OnLayoutUpdated;
        }
        Place();
    }

    public void Hide()
    {
        if (_layer is not { } layer)
            return;
        _layer = null;
        _place = null;
        layer.LayoutUpdated -= OnLayoutUpdated;
        layer.Children.Remove(_root);
        foreach (IDisposable binding in _bindings)
            binding.Dispose();
        _bindings = [];
    }

    /// <summary>A layout pass ended: the window or the place may have moved.</summary>
    private void OnLayoutUpdated(object? sender, EventArgs e) => Place();

    /// <summary>
    /// Places the popup next to its place as the window shows it now, unless neither moved since the last placing;
    /// closes it when the place's line is out of its view or of the window.
    /// </summary>
    private void Place()
    {
        if (_layer is not { } layer || _place is not var (relativeTo, getAnchor))
            return;
        Size space = layer.Bounds.Size;
        // Before its first layout, the layer has no size to place in: the layout pass that adding the popup starts
        // places it.
        if (space.Width <= 0 || space.Height <= 0)
            return;
        Rect anchor = getAnchor();
        if (
            anchor.Bottom <= 0
            || anchor.Top >= relativeTo.Bounds.Height
            || relativeTo.TransformToVisual(layer) is not { } toLayer
        )
        {
            LeaveView();
            return;
        }
        // The line as its view shows it, in the layer, as a scaled editor shows it too; the place itself may be
        // scrolled out of it sideways.
        Rect line = new Rect(0, anchor.Top, relativeTo.Bounds.Width, anchor.Height).TransformToAABB(toLayer);
        double left = anchor.TopLeft.Transform(toLayer).X;
        // Placing changes the layout, and the next layout pass finds nothing moved.
        if (_placed == (space, line, left))
            return;
        _placed = (space, line, left);
        _root.MaxHeight = double.PositiveInfinity;
        _root.Measure(new Size(space.Width, double.PositiveInfinity));
        if (PopupPlacement.Place(line, left, _root.DesiredSize, new Rect(space), _side) is not { } placed)
        {
            LeaveView();
            return;
        }
        _root.MaxHeight = placed.MaxHeight;
        Canvas.SetLeft(_root, placed.Bounds.Left);
        // Above its line, its bottom edge stays there as its content grows.
        if (placed.Above)
        {
            _root.ClearValue(Canvas.TopProperty);
            Canvas.SetBottom(_root, space.Height - placed.Bounds.Bottom);
        }
        else
        {
            _root.ClearValue(Canvas.BottomProperty);
            Canvas.SetTop(_root, placed.Bounds.Top);
        }
    }

    private void LeaveView()
    {
        Hide();
        LeftView?.Invoke(this, EventArgs.Empty);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// Content in a <see cref="PopupFrame"/> next to a place in an editor's text, or beside another popup, in the window's
/// overlay layer: it stays inside the window, and opens without an OS window. It takes the editor's theme variant, so
/// that a <c>ThemeVariantScope</c> around the editor reaches it, and the app's font where the editor is, not the
/// editor's monospace one. <see cref="PopupPlacement"/> decides where it goes.
/// </summary>
/// <remarks>
/// <para>
/// While open, it is placed again after each layout pass in which the window, its place or the popup it is beside
/// moved, as when the window is resized or the text reflows. Next to a line, it closes when the line leaves the view it
/// is in, which <see cref="LeftView"/> reports.
/// </para>
/// <para>
/// A resizable popup resizes by its edges away from what it is next to: its outer side and the edge away from the
/// line. <see cref="Resized"/> reports the size dragged to, and its owner shows it again at that size.
/// </para>
/// </remarks>
internal sealed class EditorPopup
{
    private readonly TemplatedControl _editor;
    private readonly ThemeVariantScope _root;
    private readonly PopupSide _side;
    private OverlayLayer? _layer;

    /// <summary>The bindings to the editor's theme variant and to the font around it, while open.</summary>
    private IDisposable[] _bindings = [];

    /// <summary>The visual of the place, and the place: a character's box in it, read again at each placing.</summary>
    private (Visual RelativeTo, Func<Rect> Anchor)? _line;

    /// <summary>The popup it is beside, and the width it would like there; null while it is next to a line.</summary>
    private (EditorPopup Owner, double Width)? _beside;

    /// <summary>What the last placing went by; null to place it again.</summary>
    private object? _placedBy;

    /// <param name="editor">The editor whose text the popup is next to.</param>
    /// <param name="content">What the popup shows, inside its frame.</param>
    /// <param name="name">The frame's name, for tests.</param>
    /// <param name="side">The side of its line it opens on where both have room.</param>
    public EditorPopup(TemplatedControl editor, Control content, string name, PopupSide side = PopupSide.Above)
    {
        _editor = editor;
        Frame = new PopupFrame { Name = name, Child = content };
        Frame.Resized += size => Resized?.Invoke(size);
        _root = new ThemeVariantScope { Child = Frame };
        _side = side;
    }

    /// <summary>Raised when the popup closed because its place's line left the view.</summary>
    public event EventHandler? LeftView;

    /// <summary>Raised while the user drags an edge, with the size the popup would have.</summary>
    public event Action<Size>? Resized;

    /// <summary>Whether its edges away from what it is next to resize it, from its next placing on; false by default.</summary>
    public bool IsResizable { get; set; }

    /// <summary>The frame around the content, which the popup's owner paints.</summary>
    public PopupFrame Frame { get; }

    /// <summary>The popup's outermost control, for the pointer over it.</summary>
    public Control Root => _root;

    public bool IsOpen => _layer is not null;

    /// <summary>Where the popup was placed last, in the overlay layer; null while it is closed or not placed yet.</summary>
    public PopupBounds? Placed { get; private set; }

    /// <summary>
    /// Opens the popup next to <paramref name="anchor"/>, a character's box in <paramref name="relativeTo"/>, whose left
    /// edge is where the popup's goes, or moves it there; also after its content changed.
    /// </summary>
    public void Show(Visual relativeTo, Func<Rect> anchor)
    {
        _line = (relativeTo, anchor);
        _beside = null;
        _placedBy = null;
        if (Open())
            Place();
    }

    /// <summary>
    /// Opens the popup beside <paramref name="owner"/>, <paramref name="width"/> wide where there is room, as
    /// <see cref="PopupPlacement.Beside"/> places it; also after its content changed. It follows the owner.
    /// </summary>
    public void ShowBeside(EditorPopup owner, double width)
    {
        _beside = (owner, width);
        _line = null;
        _placedBy = null;
        if (Open())
            Place();
    }

    public void Hide()
    {
        _line = null;
        _beside = null;
        Placed = null;
        if (_layer is not { } layer)
            return;
        _layer = null;
        layer.LayoutUpdated -= OnLayoutUpdated;
        layer.Children.Remove(_root);
        foreach (IDisposable binding in _bindings)
            binding.Dispose();
        _bindings = [];
    }

    /// <summary>Adds the popup to the overlay layer, unless it is there.</summary>
    /// <returns>Whether the popup is in the layer.</returns>
    private bool Open()
    {
        if (_layer is not null)
            return true;
        if (OverlayLayer.GetOverlayLayer(_editor) is not { } layer)
            return false;
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
            bindings.Add(_root.Bind(TextElement.FontSizeProperty, around.GetObservable(TextElement.FontSizeProperty)));
        }
        _bindings = [.. bindings];
        layer.Children.Add(_root);
        layer.LayoutUpdated += OnLayoutUpdated;
        return true;
    }

    /// <summary>A layout pass ended: the window, the place or the popup it is beside may have moved.</summary>
    private void OnLayoutUpdated(object? sender, EventArgs e) => Place();

    /// <summary>
    /// Places the popup as the window shows its place now, unless nothing moved since the last placing; closes it when
    /// its line is out of its view.
    /// </summary>
    private void Place()
    {
        if (_layer is not { } layer)
            return;
        Size space = layer.Bounds.Size;
        var window = new Rect(space);
        PopupBounds placed;
        if (_line is var (relativeTo, getAnchor))
        {
            Rect anchor = getAnchor();
            if (
                anchor.Bottom <= 0
                || anchor.Top >= relativeTo.Bounds.Height
                || relativeTo.TranslatePoint(anchor.TopLeft, layer) is not { } top
            )
            {
                Hide();
                LeftView?.Invoke(this, EventArgs.Empty);
                return;
            }
            var line = new Rect(top, anchor.Size);
            // Placing changes the layout, and the next layout pass finds nothing moved.
            if (Equals(_placedBy, (space, line)))
                return;
            _placedBy = (space, line);
            _root.MaxHeight = double.PositiveInfinity;
            _root.Measure(new Size(space.Width, double.PositiveInfinity));
            placed = PopupPlacement.Place(line, _root.DesiredSize, window, _side);
        }
        else if (_beside is var (owner, width))
        {
            // Until the owner is placed, there is nothing to be beside.
            _root.IsVisible = owner.Placed is not null;
            if (owner.Placed is not { } other || Equals(_placedBy, (space, other, width)))
                return;
            _placedBy = (space, other, width);
            _root.ClearValue(Layoutable.WidthProperty);
            _root.MaxHeight = double.PositiveInfinity;
            placed = PopupPlacement.Beside(other, width, HeightAt, window);
            _root.Width = placed.Bounds.Width;
        }
        else
        {
            return;
        }
        Placed = placed;
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
        Frame.ResizableEdges = IsResizable ? EdgesAway(placed) : Edges.None;
    }

    /// <summary>The popup's height at <paramref name="width"/>.</summary>
    private double HeightAt(double width)
    {
        _root.Measure(new Size(width, double.PositiveInfinity));
        return _root.DesiredSize.Height;
    }

    /// <summary>
    /// The edges away from what the popup is next to: its outer side, which is the left one only left of the popup it
    /// is beside, and the edge away from the line.
    /// </summary>
    private Edges EdgesAway(PopupBounds placed)
    {
        bool leftOfOwner = _beside?.Owner.Placed is { } other && placed.Bounds.Right <= other.Bounds.Left + 0.5;
        return (leftOfOwner ? Edges.Left : Edges.Right) | (placed.Above ? Edges.Top : Edges.Bottom);
    }
}

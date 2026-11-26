using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// A <see cref="PopupFrame"/> next to a place in an editor's text, or beside another popup, in the window's overlay
/// layer: it stays inside the window, and opens without an OS window. It takes the editor's theme variant, so that a
/// <c>ThemeVariantScope</c> around the editor reaches it, and the app's font where the editor is, not the editor's
/// monospace one. <see cref="PopupPlacement"/> decides where it goes.
/// </summary>
/// <remarks>
/// <para>
/// While open, it is placed again after each layout pass in which the window, its place or the popup it is beside
/// moved, as when the window is resized or the text reflows. Next to a line, it closes when the line leaves the view it
/// is in or the window, which <see cref="LeftView"/> reports.
/// </para>
/// <para>
/// Shown again in place, it keeps the left edge and the side of its line that it had, so that a button that changed its
/// content stays under the pointer: content wider than the room right of it wraps, and content taller than the room on
/// its side scrolls. Once the window leaves it less room right of that edge than it had, it is placed anew.
/// </para>
/// <para>
/// A resizable popup resizes by its edges away from what it is next to: its outer side and the edge away from the
/// line. <see cref="Resized"/> reports the size dragged to and the edges dragged, and its owner shows it again at that
/// size, on the same side of its line until the user lets go.
/// </para>
/// <para>
/// Its root names the editor as its owner (<see cref="EditorPopups"/>), so that the keyboard in it counts as in the
/// editor.
/// </para>
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
    private (Visual RelativeTo, Func<Rect> Anchor)? _line;

    /// <summary>
    /// Where it was, shown again in place: it keeps that left edge and side of the line; null to place it anew.
    /// </summary>
    private PopupBounds? _kept;

    /// <summary>The popup it is beside, and the width it would like there; null while it is next to a line.</summary>
    private (EditorPopup Owner, double Width)? _beside;

    /// <summary>What the last placing went by; null to place it again.</summary>
    private object? _placedBy;

    /// <param name="editor">The editor whose text the popup is next to.</param>
    /// <param name="frame">What the popup shows.</param>
    /// <param name="side">The side of its line it opens on where both have room.</param>
    public EditorPopup(CodeEditor editor, PopupFrame frame, PopupSide side = PopupSide.Above)
    {
        _editor = editor;
        Frame = frame;
        frame.Resized += (size, edges) => Resized?.Invoke(size, edges);
        _root = new ThemeVariantScope { Child = frame };
        EditorPopups.SetOwner(_root, editor);
        _side = side;
    }

    /// <summary>Raised when the popup closed because its place's line left the view.</summary>
    public event EventHandler? LeftView;

    /// <summary>Raised while the user drags an edge, with the size the popup would have.</summary>
    public event Action<Size, Edges>? Resized;

    /// <summary>Raised when the popup was placed: <see cref="Placed"/> says where.</summary>
    public event EventHandler? PlacedChanged;

    /// <summary>
    /// How far left of its place's left edge the popup's left edge goes, as completion's list lines its labels up with
    /// the word; none by default.
    /// </summary>
    public double Inset { get; init; }

    /// <summary>
    /// Where its left edge goes in place of its place's, within the window, as a hover the user resizes keeps it; null
    /// for its place's.
    /// </summary>
    public double? Left { get; set; }

    /// <summary>Whether its edges away from what it is next to resize it, from its next placing on; false by default.</summary>
    public bool IsResizable { get; set; }

    /// <summary>The frame around the content.</summary>
    public PopupFrame Frame { get; }

    /// <summary>The popup's outermost control, for the pointer over it.</summary>
    public Control Root => _root;

    public bool IsOpen => _layer is not null;

    /// <summary>Where the popup was placed last, in the overlay layer; null while it is closed or not placed yet.</summary>
    public PopupBounds? Placed { get; private set; }

    /// <summary>
    /// Opens the popup next to <paramref name="anchor"/>, a character's box in <paramref name="relativeTo"/>, or moves
    /// it there; also after its content changed.
    /// </summary>
    /// <param name="relativeTo">The visual of the place.</param>
    /// <param name="anchor">The place: a character's box in <paramref name="relativeTo"/>, read again at each placing.</param>
    /// <param name="inPlace">
    /// Whether an open popup keeps its left edge and its side of the line, until it is shown otherwise.
    /// </param>
    public void Show(Visual relativeTo, Func<Rect> anchor, bool inPlace = false)
    {
        _kept = inPlace ? Placed : null;
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
    /// its line is out of its view or of the window.
    /// </summary>
    private void Place()
    {
        if (_layer is not { } layer)
            return;
        Size space = layer.Bounds.Size;
        // Before its first layout, the layer has no size to place in: the layout pass that adding the popup starts
        // places it.
        if (space.Width <= 0 || space.Height <= 0)
            return;
        var window = new Rect(space);
        PopupBounds placed;
        if (_line is var (relativeTo, getAnchor))
        {
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
            // A window that narrowed past it leaves it less room where it was: it takes a place anew.
            if (_kept is { } was && was.Bounds.Left > space.Width - was.Bounds.Width)
                _kept = null;
            double left = _kept?.Bounds.Left ?? Left ?? anchor.TopLeft.Transform(toLayer).X - Inset;
            // Placing changes the layout, and the next layout pass finds nothing moved. Content that just changed has its
            // width only after that pass, which then places the popup again.
            double laidOutWidth = _root.DesiredSize.Width;
            if (Equals(_placedBy, (space, line, left, laidOutWidth)))
                return;
            _placedBy = (space, line, left, laidOutWidth);
            _root.MaxWidth = _kept is null ? double.PositiveInfinity : space.Width - left;
            _root.MaxHeight = double.PositiveInfinity;
            _root.Measure(new Size(space.Width, double.PositiveInfinity));
            // While the user drags an edge, it stays on its side of the line rather than jump from under the pointer;
            // it takes a side again at its first placing after they let go.
            bool? above = _kept?.Above ?? (Frame.IsDragging && Placed is { } shown ? shown.Above : null);
            (PopupSide side, bool keepSide) = above is { } kept
                ? (kept ? PopupSide.Above : PopupSide.Below, true)
                : (_side, false);
            if (PopupPlacement.Place(line, left, _root.DesiredSize, window, side, keepSide) is not { } next)
            {
                LeaveView();
                return;
            }
            placed = next;
        }
        else if (_beside is var (owner, width))
        {
            // Until the owner is placed, there is nothing to be beside.
            _root.IsVisible = owner.Placed is not null;
            if (owner.Placed is not { } ownerPlaced)
                return;
            // As it is laid out: whole rows of a list can leave it shorter than its room.
            PopupBounds other = owner._root.Bounds is { Width: > 0 } laidOut
                ? ownerPlaced with
                {
                    Bounds = laidOut,
                }
                : ownerPlaced;
            if (Equals(_placedBy, (space, other, width)))
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
        PlacedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes the popup, as its line left the view: its owner first, which may give the keyboard back from it.</summary>
    private void LeaveView()
    {
        LeftView?.Invoke(this, EventArgs.Empty);
        Hide();
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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The outside of an editor popup, as VS Code draws its editor widgets: a background, a 1-pixel border, small corners
/// and a shadow, all from the editor's color theme. It is the editor's own control with its values set locally, so an
/// app's styles for borders, flyouts or tooltips never reach it.
/// </summary>
/// <remarks>
/// <para>
/// While the keyboard is in the popup, as in a hover that the user put it into, a 1-pixel outline in the theme's
/// <c>focusBorder</c> shows inside its border, as VS Code draws its focused widgets: <c>outline: 1px solid</c> with
/// <c>outline-offset: -1px</c>.
/// </para>
/// <para>
/// A popup whose owner names <see cref="ResizableEdges"/> resizes by dragging those edges, and the corners between
/// them, within a few pixels inside its border: it reports the size dragged to, within the window, through
/// <see cref="Resized"/>, and its owner applies it.
/// </para>
/// </remarks>
internal class PopupFrame : Border
{
    /// <summary>The corners' radius, as VS Code's widgets.</summary>
    public const double Radius = 3;

    /// <summary>How far inside its edges the pointer grabs them.</summary>
    private const double Grip = 5;

    private static readonly Cursor s_horizontal = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor s_vertical = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor s_topLeft = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor s_topRight = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor s_bottomLeft = new(StandardCursorType.BottomLeftCorner);
    private static readonly Cursor s_bottomRight = new(StandardCursorType.BottomRightCorner);

    /// <summary>
    /// A drag under way: where it started in the window, the frame's bounds and most height then, and its edges.
    /// </summary>
    private (Point Start, Rect Bounds, double MaxHeight, Edges Edges)? _drag;

    public PopupFrame()
    {
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(Radius);
        // Before the child, which the frame adds after it: over the frame's background, under its content.
        _outline.IsVisible = false;
        VisualChildren.Add(_outline);
        // Ahead of the content, which selects text or items where the pointer presses.
        AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMovedTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleasedTunnel, RoutingStrategies.Tunnel);
    }

    /// <summary>Which edges resize the frame now; none by default.</summary>
    public Func<Edges>? ResizableEdges { get; set; }

    /// <summary>Whether the user drags an edge now.</summary>
    public bool IsResizing => _drag is not null;

    /// <summary>
    /// Raised while the user drags an edge, with the size the frame would have, and its most height moved as much as
    /// its height: content shorter than the frame's most height keeps its height, and the drag moves the limit.
    /// </summary>
    public event Action<Size, double>? Resized;

    /// <summary>The outline while the keyboard is in the popup, over the frame: a visual child besides its child.</summary>
    private readonly FocusOutline _outline = new();

    /// <summary>
    /// Paints the frame, and sets the foreground that its text inherits; <paramref name="focus"/> outlines it while the
    /// keyboard is in it.
    /// </summary>
    public void Paint(IBrush background, IBrush border, IBrush foreground, BoxShadows shadow, IBrush? focus = null)
    {
        Background = background;
        BorderBrush = border;
        BoxShadow = shadow;
        TextElement.SetForeground(this, foreground);
        _outline.Pen = focus is null ? null : new ImmutablePen(focus.ToImmutable());
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _outline.Measure(availableSize);
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Inside the border, a pixel wide, with the corners' radius less the border's.
        Thickness border = BorderThickness;
        _outline.Radius = Math.Max(0, Radius - border.Left);
        _outline.Arrange(new Rect(finalSize).Deflate(border));
        return base.ArrangeOverride(finalSize);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsKeyboardFocusWithinProperty)
            _outline.IsVisible = change.GetNewValue<bool>();
    }

    private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        Edges edges = EdgesAt(e.GetPosition(this));
        if (
            edges == Edges.None
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || TopLevel.GetTopLevel(this) is not { } window
            || this.TranslatePoint(default, window) is not { } at
        )
        {
            return;
        }
        double maxHeight = double.IsInfinity(MaxHeight) ? Bounds.Height : Math.Max(MaxHeight, Bounds.Height);
        _drag = (e.GetPosition(window), new Rect(at, Bounds.Size), maxHeight, edges);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMovedTunnel(object? sender, PointerEventArgs e)
    {
        if (_drag is not var (start, bounds, maxHeight, edges) || TopLevel.GetTopLevel(this) is not { } window)
        {
            Cursor = CursorOf(EdgesAt(e.GetPosition(this)));
            return;
        }
        Vector delta = e.GetPosition(window) - start;
        Size room = window.ClientSize;
        double width = bounds.Width;
        double height = bounds.Height;
        if (edges.HasFlag(Edges.Right))
            width = Math.Min(bounds.Width + delta.X, room.Width - bounds.Left);
        else if (edges.HasFlag(Edges.Left))
            width = Math.Min(bounds.Width - delta.X, bounds.Right);
        if (edges.HasFlag(Edges.Bottom))
            height = Math.Min(bounds.Height + delta.Y, room.Height - bounds.Top);
        else if (edges.HasFlag(Edges.Top))
            height = Math.Min(bounds.Height - delta.Y, bounds.Bottom);
        Resized?.Invoke(
            new Size(Math.Max(0, width), Math.Max(0, height)),
            Math.Max(0, maxHeight + height - bounds.Height)
        );
        e.Handled = true;
    }

    private void OnPointerReleasedTunnel(object? sender, PointerReleasedEventArgs e)
    {
        if (_drag is null)
            return;
        _drag = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>The resizable edges that <paramref name="point"/> is on.</summary>
    private Edges EdgesAt(Point point)
    {
        Edges allowed = ResizableEdges?.Invoke() ?? Edges.None;
        if (allowed == Edges.None)
            return Edges.None;
        Size size = Bounds.Size;
        Edges edges = Edges.None;
        if (point.X <= Grip)
            edges |= Edges.Left;
        else if (point.X >= size.Width - Grip)
            edges |= Edges.Right;
        if (point.Y <= Grip)
            edges |= Edges.Top;
        else if (point.Y >= size.Height - Grip)
            edges |= Edges.Bottom;
        return edges & allowed;
    }

    private static Cursor? CursorOf(Edges edges) =>
        edges switch
        {
            Edges.Left | Edges.Top => s_topLeft,
            Edges.Right | Edges.Top => s_topRight,
            Edges.Left | Edges.Bottom => s_bottomLeft,
            Edges.Right | Edges.Bottom => s_bottomRight,
            Edges.Left or Edges.Right => s_horizontal,
            Edges.Top or Edges.Bottom => s_vertical,
            _ => null,
        };

    /// <summary>Edges of a popup.</summary>
    [Flags]
    public enum Edges
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
    }

    /// <summary>A 1-pixel outline at the edge of its bounds, which takes no pointer.</summary>
    private sealed class FocusOutline : Control
    {
        public FocusOutline() => IsHitTestVisible = false;

        public IPen? Pen
        {
            get;
            set
            {
                field = value;
                InvalidateVisual();
            }
        }

        public double Radius { get; set; }

        public override void Render(DrawingContext context)
        {
            Rect inside = new Rect(Bounds.Size).Deflate(0.5);
            if (Pen is { } pen && inside.Width > 0 && inside.Height > 0)
                context.DrawRectangle(null, pen, inside, Radius, Radius);
        }
    }
}

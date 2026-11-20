using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Darp.CodeEditor.Popups;

/// <summary>The edges of a popup that resize it.</summary>
[Flags]
internal enum Edges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
}

/// <summary>
/// The outside of an editor popup, as VS Code draws its editor widgets: a background, a 1-pixel border, small corners
/// and a shadow, in the colors that <see cref="Paint"/> gives it. Its values are set on it, so that an app's styles
/// never reach it. Its content draws only what it shows.
/// </summary>
/// <remarks>
/// <para>
/// While the keyboard is in the popup, a 1-pixel outline in the color theme's <c>focusBorder</c> shows inside its
/// border, as VS Code draws its focused widgets: <c>outline: 1px solid</c> with <c>outline-offset: -1px</c>.
/// </para>
/// <para>
/// Dragging one of its <see cref="ResizableEdges"/>, or a corner between two, within a few pixels inside its border,
/// reports the size dragged to through <see cref="Resized"/>, within the window, with the edges dragged: its owner
/// applies what they change.
/// </para>
/// </remarks>
internal sealed class PopupFrame : Border
{
    /// <summary>The corners' radius, as VS Code's widgets.</summary>
    private const double Radius = 3;

    /// <summary>How far inside its edges the pointer grabs them.</summary>
    private const double Grip = 5;

    private static readonly Cursor s_horizontal = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor s_vertical = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor s_topLeft = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor s_topRight = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor s_bottomLeft = new(StandardCursorType.BottomLeftCorner);
    private static readonly Cursor s_bottomRight = new(StandardCursorType.BottomRightCorner);

    /// <summary>A drag under way: where it started in the window, the frame's bounds in the window then, and its edges.</summary>
    private (Point Start, Rect Bounds, Edges Edges)? _drag;

    /// <summary>The outline while the keyboard is in the popup: a visual child besides its child, over its background.</summary>
    private readonly FocusOutline _outline = new() { IsVisible = false };

    public PopupFrame()
    {
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(Radius);
        // Before the child, which the frame adds after it: under the content.
        VisualChildren.Add(_outline);
        // Not what the window around passes down: the popup's text is as the editor sets it.
        TextElement.SetFontWeight(this, FontWeight.Normal);
        TextElement.SetFontStyle(this, FontStyle.Normal);
        // Ahead of the content, which would take the press for itself.
        AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMovedTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleasedTunnel, RoutingStrategies.Tunnel);
    }

    /// <summary>The edges that resize the frame now; none by default.</summary>
    public Edges ResizableEdges { get; set; }

    /// <summary>Whether the user is dragging an edge.</summary>
    public bool IsDragging => _drag is not null;

    /// <summary>Raised while the user drags an edge, with the size that the frame would have, and the edges dragged.</summary>
    public event Action<Size, Edges>? Resized;

    /// <summary>
    /// Paints the frame, and gives its text <paramref name="foreground"/>; <paramref name="focus"/> outlines it while
    /// the keyboard is in it.
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
        _outline.Radius = Math.Max(0, Radius - BorderThickness.Left);
        _outline.Arrange(new Rect(finalSize).Deflate(BorderThickness));
        return base.ArrangeOverride(finalSize);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsKeyboardFocusWithinProperty)
            _outline.IsVisible = change.GetNewValue<bool>();
    }

    /// <summary>A drag ends without its release too, as when the popup closes under it.</summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _drag = null;
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
            return;
        _drag = (e.GetPosition(window), new Rect(at, Bounds.Size), edges);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMovedTunnel(object? sender, PointerEventArgs e)
    {
        if (_drag is not var (start, bounds, edges) || TopLevel.GetTopLevel(this) is not { } window)
        {
            Cursor = CursorOf(EdgesAt(e.GetPosition(this)));
            return;
        }
        Vector delta = e.GetPosition(window) - start;
        Size room = window.ClientSize;
        double width =
            edges.HasFlag(Edges.Right) ? Math.Min(bounds.Width + delta.X, room.Width - bounds.Left)
            : edges.HasFlag(Edges.Left) ? Math.Min(bounds.Width - delta.X, bounds.Right)
            : bounds.Width;
        double height =
            edges.HasFlag(Edges.Bottom) ? Math.Min(bounds.Height + delta.Y, room.Height - bounds.Top)
            : edges.HasFlag(Edges.Top) ? Math.Min(bounds.Height - delta.Y, bounds.Bottom)
            : bounds.Height;
        Resized?.Invoke(new Size(Math.Max(0, width), Math.Max(0, height)), edges);
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
        Size size = Bounds.Size;
        Edges edges =
            (
                point.X <= Grip ? Edges.Left
                : point.X >= size.Width - Grip ? Edges.Right
                : Edges.None
            )
            | (
                point.Y <= Grip ? Edges.Top
                : point.Y >= size.Height - Grip ? Edges.Bottom
                : Edges.None
            );
        return edges & ResizableEdges;
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

    private static Cursor? CursorOf(Edges edges) =>
        edges switch
        {
            Edges.None => null,
            Edges.Left | Edges.Top => s_topLeft,
            Edges.Right | Edges.Top => s_topRight,
            Edges.Left | Edges.Bottom => s_bottomLeft,
            Edges.Right | Edges.Bottom => s_bottomRight,
            Edges.Left or Edges.Right => s_horizontal,
            _ => s_vertical,
        };
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

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
/// Dragging one of its <see cref="ResizableEdges"/>, or a corner between two, within a few pixels inside its border,
/// reports the size dragged to through <see cref="Resized"/>, within the window, with the edges dragged: its owner
/// applies what they change.
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

    public PopupFrame()
    {
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(Radius);
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

    /// <summary>Raised while the user drags an edge, with the size that the frame would have, and the edges dragged.</summary>
    public event Action<Size, Edges>? Resized;

    /// <summary>Paints the frame, and gives its text <paramref name="foreground"/>.</summary>
    public void Paint(IBrush background, IBrush border, IBrush foreground, BoxShadows shadow)
    {
        Background = background;
        BorderBrush = border;
        BoxShadow = shadow;
        TextElement.SetForeground(this, foreground);
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

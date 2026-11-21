using Avalonia;

namespace Darp.CodeEditor.Popups;

/// <summary>The side of its line that a popup opens on where both sides have room.</summary>
internal enum PopupSide
{
    Above,
    Below,
}

/// <summary>Where a popup goes in the window.</summary>
/// <param name="Bounds">Its bounds, as tall as its content, up to <paramref name="MaxHeight"/>.</param>
/// <param name="Above">
/// Whether it is above its line: its bottom edge then stays where it is as its content grows, so that it stays next to
/// the line.
/// </param>
/// <param name="MaxHeight">The most it may grow to where it is, in whole pixels.</param>
internal readonly record struct PopupBounds(Rect Bounds, bool Above, double MaxHeight);

/// <summary>Where the editor's popups go: next to a line of the text, inside the window.</summary>
internal static class PopupPlacement
{
    /// <summary>
    /// Places a popup of <paramref name="size"/> next to <paramref name="line"/>, its left edge at
    /// <paramref name="left"/>: on the <paramref name="preferred"/> side, or on the other side when only that has room.
    /// When neither has room, it takes the larger side, limited to its height. It moves left at the window's right
    /// edge, and right at its left edge, and is at most as wide as the window.
    /// </summary>
    /// <param name="line">The line, as its view shows it: as wide as the view, as high as the line.</param>
    /// <param name="left">Where the popup's left edge goes, as at the start of a word.</param>
    /// <param name="size">The popup's size.</param>
    /// <param name="window">The window.</param>
    /// <param name="preferred">The side it opens on where both have room.</param>
    /// <returns>
    /// Where it goes; null when the window does not show the line, as of an editor beyond its edge: the popup would
    /// point at nothing.
    /// </returns>
    public static PopupBounds? Place(Rect line, double left, Size size, Rect window, PopupSide preferred)
    {
        if (!line.Intersects(window))
            return null;
        double above = line.Top - window.Top;
        double below = window.Bottom - line.Bottom;
        bool placeAbove =
            preferred == PopupSide.Below
                ? size.Height > below && above > below
                : size.Height <= above || (size.Height > below && above >= below);
        // In whole pixels, so that rounding the layout cannot take it past the window's edge.
        double room = Math.Max(0, Math.Floor(placeAbove ? above : below));
        double width = Math.Min(size.Width, window.Width);
        double height = Math.Min(size.Height, room);
        double x = Math.Clamp(left, window.Left, window.Right - width);
        return new PopupBounds(
            new Rect(x, placeAbove ? line.Top - height : line.Bottom, width, height),
            placeAbove,
            room
        );
    }
}

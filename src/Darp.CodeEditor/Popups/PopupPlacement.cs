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
/// Whether it is above its line, or above or beside a popup that is: its bottom edge then stays where it is as its
/// content grows, so that it stays next to the line.
/// </param>
/// <param name="MaxHeight">The most it may grow to where it is, in whole pixels.</param>
/// <param name="Line">The line it is next to, or the line of the popup it is beside.</param>
internal readonly record struct PopupBounds(Rect Bounds, bool Above, double MaxHeight, Rect Line);

/// <summary>Where the editor's popups go: next to a line of the text, or beside another popup, inside the window.</summary>
internal static class PopupPlacement
{
    /// <summary>The narrowest a popup beside another gets; with less room on either side, it goes below or above it.</summary>
    public const double MinBesideWidth = 200;

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
            room,
            line
        );
    }

    /// <summary>
    /// Places a popup <paramref name="width"/> wide beside <paramref name="owner"/>, as completion's details beside its
    /// list: right of it, else left of it, else narrower on the side with more room, down to
    /// <see cref="MinBesideWidth"/>, else below it, or above it when it is above its line; and when that side has less
    /// room than it needs and the line's other side more, on the line's other side. Beside it, its top lines up with
    /// the owner's below the line, and its bottom above it, so that neither moves away from the line.
    /// </summary>
    /// <param name="owner">Where the other popup is, as it is laid out.</param>
    /// <param name="width">The width it would like.</param>
    /// <param name="heightAt">Its height at a width: text that wraps is taller when narrower.</param>
    /// <param name="window">The window.</param>
    public static PopupBounds Beside(PopupBounds owner, double width, Func<double, double> heightAt, Rect window)
    {
        Rect other = owner.Bounds;
        double right = window.Right - other.Right;
        double left = other.Left - window.Left;
        double x;
        if (right >= width)
            x = other.Right;
        else if (left >= width)
            x = other.Left - width;
        else if (Math.Max(right, left) >= MinBesideWidth)
        {
            width = Math.Floor(Math.Max(right, left));
            x = right >= left ? other.Right : other.Left - width;
        }
        else
        {
            width = Math.Min(width, window.Width);
            x = Math.Clamp(other.Left, window.Left, window.Right - width);
            Rect line = owner.Line;
            double wanted = heightAt(width);
            double far = owner.Above ? other.Top - window.Top : window.Bottom - other.Bottom;
            double near = owner.Above ? window.Bottom - line.Bottom : line.Top - window.Top;
            bool above = wanted > far && near > far ? !owner.Above : owner.Above;
            double room = Math.Max(0, Math.Floor(above == owner.Above ? far : near));
            double stacked = Math.Min(wanted, room);
            double y =
                above == owner.Above ? (above ? other.Top - stacked : other.Bottom)
                : above ? line.Top - stacked
                : line.Bottom;
            return new PopupBounds(new Rect(x, y, width, stacked), above, room, line);
        }
        double height = Math.Min(heightAt(width), window.Height);
        double top = Math.Clamp(owner.Above ? other.Bottom - height : other.Top, window.Top, window.Bottom - height);
        return new PopupBounds(new Rect(x, top, width, height), owner.Above, height, owner.Line);
    }
}

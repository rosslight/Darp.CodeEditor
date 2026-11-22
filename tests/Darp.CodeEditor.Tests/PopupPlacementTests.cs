using Avalonia;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Tests;

/// <summary>Where the editor's popups go: next to their line, inside the window.</summary>
public sealed class PopupPlacementTests
{
    private static readonly Rect s_window = new(0, 0, 400, 300);

    [Theory]
    [InlineData("above", 100, true)]
    [InlineData("below", 100, false)]
    // Only the other side has room for its 60 pixels.
    [InlineData("above", 20, false)]
    [InlineData("below", 260, true)]
    public void A_popup_opens_on_its_side_of_the_line_or_on_the_other_where_only_that_has_room(
        string side,
        double lineTop,
        bool above
    )
    {
        Rect line = Line(lineTop);

        PopupBounds placed = PopupPlacement.Place(line, 50, new Size(100, 60), s_window, Side(side)).ShouldNotBeNull();

        placed.Above.ShouldBe(above);
        placed.Bounds.ShouldBe(new Rect(50, above ? line.Top - 60 : line.Bottom, 100, 60));
    }

    [Theory]
    [InlineData("above")]
    [InlineData("below")]
    public void Where_neither_side_has_room_it_takes_the_larger_one_in_whole_pixels(string side)
    {
        // 40 pixels above the line, 42.5 below it.
        PopupBounds placed = PopupPlacement
            .Place(Line(40), 50, new Size(100, 80), new Rect(0, 0, 400, 100.5), Side(side))
            .ShouldNotBeNull();

        placed.Above.ShouldBeFalse();
        placed.MaxHeight.ShouldBe(42);
        placed.Bounds.ShouldBe(new Rect(50, 58, 100, 42));
    }

    [Fact]
    public void It_moves_into_the_window_at_its_edges_and_is_no_wider_than_it()
    {
        PopupPlacement
            .Place(Line(100), 350, new Size(100, 60), s_window, PopupSide.Below)
            .ShouldNotBeNull()
            .Bounds.Left.ShouldBe(300);
        PopupPlacement
            .Place(Line(100), -21, new Size(100, 60), s_window, PopupSide.Below)
            .ShouldNotBeNull()
            .Bounds.Left.ShouldBe(0);
        PopupBounds wide = PopupPlacement
            .Place(Line(100), 350, new Size(500, 60), s_window, PopupSide.Below)
            .ShouldNotBeNull();
        wide.Bounds.Left.ShouldBe(0);
        wide.Bounds.Width.ShouldBe(400);
    }

    [Fact]
    public void A_line_that_the_window_does_not_show_gets_no_popup()
    {
        // Above the window, as a line of an editor scrolled partly out of it, and below it.
        PopupPlacement.Place(Line(-52), 50, new Size(100, 60), s_window, PopupSide.Below).ShouldBeNull();
        PopupPlacement.Place(Line(300), 50, new Size(100, 60), s_window, PopupSide.Above).ShouldBeNull();
    }

    [Theory]
    // An editor right of the window, and one left of it, 300 pixels wide.
    [InlineData(400)]
    [InlineData(-300)]
    public void A_line_of_an_editor_beyond_the_windows_side_gets_no_popup(double viewLeft) =>
        PopupPlacement
            .Place(new Rect(viewLeft, 100, 300, 18), viewLeft + 50, new Size(100, 60), s_window, PopupSide.Below)
            .ShouldBeNull();

    /// <summary>A line at <paramref name="top"/>, as a view the window's width shows it.</summary>
    private static Rect Line(double top) => new(0, top, 400, 18);

    private static PopupSide Side(string side) => side == "above" ? PopupSide.Above : PopupSide.Below;
}

using Avalonia;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Tests;

/// <summary>Where the editor's popups go: next to their line, or beside another popup, inside the window.</summary>
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
        var line = new Rect(50, lineTop, 8, 18);

        PopupBounds placed = PopupPlacement.Place(line, new Size(100, 60), s_window, Side(side));

        placed.Above.ShouldBe(above);
        placed.Bounds.ShouldBe(new Rect(50, above ? line.Top - 60 : line.Bottom, 100, 60));
    }

    [Theory]
    [InlineData("above")]
    [InlineData("below")]
    public void Where_neither_side_has_room_it_takes_the_larger_one_in_whole_pixels(string side)
    {
        // 40 pixels above the line, 42.5 below it.
        var line = new Rect(50, 40, 8, 18);

        PopupBounds placed = PopupPlacement.Place(line, new Size(100, 80), new Rect(0, 0, 400, 100.5), Side(side));

        placed.Above.ShouldBeFalse();
        placed.MaxHeight.ShouldBe(42);
        placed.Bounds.ShouldBe(new Rect(50, 58, 100, 42));
    }

    [Fact]
    public void It_moves_left_at_the_windows_right_edge_and_is_no_wider_than_the_window()
    {
        var line = new Rect(350, 100, 8, 18);

        PopupPlacement.Place(line, new Size(100, 60), s_window, PopupSide.Below).Bounds.Left.ShouldBe(300);
        PopupBounds wide = PopupPlacement.Place(line, new Size(500, 60), s_window, PopupSide.Below);
        wide.Bounds.Left.ShouldBe(0);
        wide.Bounds.Width.ShouldBe(400);
    }

    [Theory]
    // Right of it, where there is room; else left of it.
    [InlineData(100, 500, 150, 300, 150, false)]
    [InlineData(300, 520, 150, 150, 150, false)]
    // Narrower, on the side with more room, while that leaves at least 200 pixels.
    [InlineData(100, 550, 300, 300, 250, false)]
    // Below it otherwise, at most as wide as the window.
    [InlineData(100, 420, 150, 100, 150, true)]
    [InlineData(100, 320, 400, 0, 320, true)]
    public void Beside_another_popup_it_goes_right_then_left_then_narrower_then_below(
        double ownerLeft,
        double windowWidth,
        double width,
        double left,
        double placedWidth,
        bool below
    )
    {
        var owner = new PopupBounds(new Rect(ownerLeft, 50, 200, 120), Above: false, MaxHeight: 230);

        // Narrower content is taller, as wrapped text is.
        PopupBounds placed = PopupPlacement.Beside(owner, width, w => 9000 / w, new Rect(0, 0, windowWidth, 400));

        placed.Bounds.Left.ShouldBe(left);
        placed.Bounds.Width.ShouldBe(placedWidth);
        placed.Bounds.Height.ShouldBe(9000 / placedWidth);
        placed.Bounds.Top.ShouldBe(below ? owner.Bounds.Bottom : owner.Bounds.Top);
    }

    [Fact]
    public void Beside_a_popup_above_its_line_it_lines_up_with_its_bottom_and_goes_above_it_where_it_has_no_room()
    {
        var owner = new PopupBounds(new Rect(100, 200, 200, 120), Above: true, MaxHeight: 320);

        PopupBounds beside = PopupPlacement.Beside(owner, 150, _ => 60, new Rect(0, 0, 500, 400));
        PopupBounds above = PopupPlacement.Beside(owner, 150, _ => 300, new Rect(0, 0, 320, 400));

        beside.Above.ShouldBeTrue();
        beside.Bounds.ShouldBe(new Rect(300, 260, 150, 60));
        // Above it, limited to the room there.
        above.Above.ShouldBeTrue();
        above.Bounds.ShouldBe(new Rect(100, 0, 150, 200));
    }

    private static PopupSide Side(string side) => side == "above" ? PopupSide.Above : PopupSide.Below;
}

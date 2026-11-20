using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// Scrolls a popup's content up and down, as VS Code's widgets do: its <see cref="PopupSlider"/> takes a gutter of its
/// own while the content is taller than the scroller, so that it never covers the text, and the content wraps to the
/// width left. Nothing scrolls across. The wheel scrolls too, and while it has the keyboard, the arrows, Page Up, Page
/// Down, Home and End.
/// </summary>
internal sealed class PopupScroller : Control
{
    /// <summary>How far one notch of the wheel and one arrow key scroll.</summary>
    private const double Step = 40;

    private readonly PopupSlider _slider = new();
    private double _offset;

    /// <summary>The part of the content to scroll into view at the next arrange; null for none.</summary>
    private Control? _scrollTo;

    /// <summary>
    /// Whether the content was taller than the scroller at the last measure, so that the slider took its gutter: the
    /// next measure starts from there, as content mostly keeps its height, so that its text is laid out once.
    /// </summary>
    private bool _gutter;

    public PopupScroller()
    {
        ClipToBounds = true;
        // The popup's frame shows the keyboard in it.
        FocusAdorner = null;
        VisualChildren.Add(_slider);
        _slider.Scrolled += ScrollTo;
    }

    /// <summary>What scrolls. Replacing it scrolls back to the top.</summary>
    public Control? Child
    {
        get;
        set
        {
            if (field is { } old)
            {
                VisualChildren.Remove(old);
                LogicalChildren.Remove(old);
            }
            field = value;
            _offset = 0;
            if (value is not null)
            {
                LogicalChildren.Add(value);
                // Under the slider.
                VisualChildren.Insert(0, value);
            }
            InvalidateMeasure();
        }
    }

    /// <summary>
    /// Scrolls <paramref name="part"/>, a part of <see cref="Child"/>, into view once it is laid out: as little as it
    /// takes, its top first when it is taller than the scroller.
    /// </summary>
    public void ScrollIntoView(Control part)
    {
        _scrollTo = part;
        InvalidateArrange();
    }

    /// <summary>The slider's colors: at rest, under the pointer, and while dragged.</summary>
    public (IBrush Normal, IBrush Hover, IBrush Active) SliderBrushes
    {
        set => _slider.BarBrushes = value;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _slider.Measure(availableSize);
        if (Child is not { } child)
            return default;
        bool fits = availableSize.Width > PopupSlider.Thickness;
        bool gutter = _gutter && fits;
        Measure(gutter);
        if (fits && gutter != (child.DesiredSize.Height > availableSize.Height))
        {
            // The content's height changed sides: measured again for the other width, which may change it back.
            gutter = !gutter;
            Measure(gutter);
            if (!gutter && child.DesiredSize.Height > availableSize.Height)
            {
                gutter = true;
                Measure(gutter);
            }
        }
        _gutter = gutter;
        Size size = child.DesiredSize;
        return new Size(size.Width + (gutter ? PopupSlider.Thickness : 0), Math.Min(size.Height, availableSize.Height));

        void Measure(bool withGutter) =>
            child.Measure(
                new Size(availableSize.Width - (withGutter ? PopupSlider.Thickness : 0), double.PositiveInfinity)
            );
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { } child)
        {
            double extent = child.DesiredSize.Height;
            _offset = Math.Clamp(_offset, 0, Math.Max(0, extent - finalSize.Height));
            double width = finalSize.Width - (_gutter ? PopupSlider.Thickness : 0);
            child.Arrange(new Rect(0, -_offset, Math.Max(0, width), extent));
            if (_scrollTo?.TranslatePoint(default, child) is { } part && _scrollTo is { } scrolled)
            {
                _scrollTo = null;
                double bottom = part.Y + scrolled.Bounds.Height - finalSize.Height;
                double offset = Math.Clamp(
                    Math.Min(part.Y, Math.Max(_offset, bottom)),
                    0,
                    Math.Max(0, extent - finalSize.Height)
                );
                if (offset != _offset)
                {
                    _offset = offset;
                    child.Arrange(new Rect(0, -_offset, Math.Max(0, width), extent));
                }
            }
            _slider.Show(finalSize.Height, extent, _offset);
        }
        _slider.Arrange(new Rect(finalSize));
        return finalSize;
    }

    public override void Render(DrawingContext context) =>
        // Transparent: the wheel scrolls wherever the pointer is.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!_slider.CanScroll)
            return;
        ScrollTo(_offset - e.Delta.Y * Step);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || !_slider.CanScroll)
            return;
        double? delta = e.Key switch
        {
            Key.Down => Step,
            Key.Up => -Step,
            Key.PageDown => Bounds.Height,
            Key.PageUp => -Bounds.Height,
            Key.End => double.PositiveInfinity,
            Key.Home => double.NegativeInfinity,
            _ => null,
        };
        if (delta is not { } by)
            return;
        ScrollBy(by);
        e.Handled = true;
    }

    /// <summary>Scrolls by <paramref name="delta"/> pixels, down where positive, as far as the content goes.</summary>
    public void ScrollBy(double delta) => ScrollTo(_offset + delta);

    private void ScrollTo(double offset)
    {
        _offset = offset;
        InvalidateArrange();
    }
}

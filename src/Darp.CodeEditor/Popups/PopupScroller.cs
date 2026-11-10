using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// Scrolls a popup's content up and down, as VS Code's widgets do: a thin slider in the color theme's
/// <c>scrollbarSlider.*</c> colors, in a gutter of its own while the content is taller than the scroller, so that it
/// never covers the text. Content wraps to the width left; nothing scrolls across. The wheel scrolls, the slider drags,
/// a click beside it pages, and while it has the keyboard, the arrows, Page Up, Page Down, Home and End scroll.
/// </summary>
internal sealed class PopupScroller : Control
{
    /// <summary>The width of the slider and its gutter, as VS Code's hover.</summary>
    public const double SliderWidth = 10;

    /// <summary>How far one notch of the wheel and one arrow key scroll.</summary>
    private const double Step = 40;

    private const double MinSliderLength = 20;

    private Control? _child;
    private double _offset;
    private double _extent;
    private bool _sliderHover;

    /// <summary>Where a drag of the slider started: the pointer's y and the offset then; null while none runs.</summary>
    private (double Pointer, double Offset)? _drag;

    public PopupScroller()
    {
        ClipToBounds = true;
        // The app's focus look would draw over the content: the popup shows the keyboard by scrolling.
        FocusAdorner = null;
    }

    /// <summary>What scrolls. Replacing it scrolls back to the top.</summary>
    public Control? Child
    {
        get => _child;
        set
        {
            if (_child == value)
                return;
            if (_child is not null)
            {
                VisualChildren.Remove(_child);
                LogicalChildren.Remove(_child);
            }
            _child = value;
            _offset = 0;
            if (value is not null)
            {
                LogicalChildren.Add(value);
                VisualChildren.Add(value);
            }
            InvalidateMeasure();
        }
    }

    /// <summary>The slider's colors: at rest, under the pointer, and while dragged.</summary>
    public (IBrush Normal, IBrush Hover, IBrush Active)? SliderBrushes
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    }

    private bool CanScroll => _extent > Bounds.Height + 0.5;

    /// <summary>Scrolls by <paramref name="delta"/> pixels, down when positive, within the content.</summary>
    public void ScrollBy(double delta) => ScrollTo(_offset + delta);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_child is not { } child)
            return default;
        child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        Size size = child.DesiredSize;
        if (size.Height > availableSize.Height && availableSize.Width > SliderWidth)
        {
            // Taller than the room: the slider takes its gutter, and the content wraps to what is left.
            child.Measure(new Size(availableSize.Width - SliderWidth, double.PositiveInfinity));
            size = child.DesiredSize.WithWidth(child.DesiredSize.Width + SliderWidth);
        }
        return new Size(size.Width, Math.Min(size.Height, availableSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_child is not { } child)
            return finalSize;
        double width = child.DesiredSize.Height > finalSize.Height ? finalSize.Width - SliderWidth : finalSize.Width;
        // Measured again for the final width, as a parent may arrange it narrower or wider than it measured.
        _extent = child.DesiredSize.Height;
        _offset = Math.Clamp(_offset, 0, Math.Max(0, _extent - finalSize.Height));
        child.Arrange(new Rect(0, -_offset, Math.Max(0, width), _extent));
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        // Transparent: the wheel and the slider's gutter take the pointer everywhere.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (!CanScroll || SliderBrushes is not var (normal, hover, active))
            return;
        Rect slider = SliderRect();
        context.FillRectangle(
            _drag is not null ? active
                : _sliderHover ? hover
                : normal,
            slider
        );
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!CanScroll)
            return;
        ScrollBy(-e.Delta.Y * Step);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Point point = e.GetPosition(this);
        if (
            !CanScroll
            || point.X < Bounds.Width - SliderWidth
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
        )
            return;
        Rect slider = SliderRect();
        if (point.Y < slider.Top)
            ScrollBy(-Bounds.Height);
        else if (point.Y > slider.Bottom)
            ScrollBy(Bounds.Height);
        else
        {
            _drag = (point.Y, _offset);
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point point = e.GetPosition(this);
        if (_drag is var (start, offset))
        {
            double track = Bounds.Height - SliderRect().Height;
            if (track > 0)
                ScrollTo(offset + (point.Y - start) / track * (_extent - Bounds.Height));
            return;
        }
        bool over = CanScroll && SliderRect().Contains(point);
        if (over != _sliderHover)
        {
            _sliderHover = over;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null)
            return;
        _drag = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_sliderHover)
        {
            _sliderHover = false;
            InvalidateVisual();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyModifiers != KeyModifiers.None)
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
        if (delta is { } d)
        {
            ScrollTo(double.IsInfinity(d) ? (d > 0 ? _extent : 0) : _offset + d);
            e.Handled = true;
        }
    }

    private void ScrollTo(double offset)
    {
        double clamped = Math.Clamp(offset, 0, Math.Max(0, _extent - Bounds.Height));
        if (clamped == _offset)
            return;
        _offset = clamped;
        InvalidateArrange();
        InvalidateVisual();
    }

    private Rect SliderRect()
    {
        double height = Bounds.Height;
        double length = Math.Max(MinSliderLength, height * height / Math.Max(_extent, 1));
        double top = (height - length) * (_offset / Math.Max(1, _extent - height));
        return new Rect(Bounds.Width - SliderWidth, top, SliderWidth, length);
    }
}

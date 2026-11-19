using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The slider of a popup that scrolls, as VS Code's widgets draw theirs: a thin bar at the popup's right edge in the
/// color theme's <c>scrollbarSlider.*</c> colors, which shows where the view is in the content while it is taller than
/// the view. Dragging the bar scrolls, and a click beside it pages. Its owner scrolls, in units of its own, such as
/// pixels or rows.
/// </summary>
internal sealed class PopupSlider : Control
{
    /// <summary>The slider's width, as VS Code's widgets have it.</summary>
    public const double Thickness = 10;

    private const double MinLength = 20;

    private double _viewport;
    private double _extent;
    private double _offset;
    private bool _pointerOver;

    /// <summary>Where a drag started: the pointer's y and the offset then; null while none runs.</summary>
    private (double Pointer, double Offset)? _drag;

    public PopupSlider()
    {
        Width = Thickness;
        HorizontalAlignment = HorizontalAlignment.Right;
    }

    /// <summary>The bar's colors: at rest, under the pointer, and while dragged.</summary>
    public (IBrush Normal, IBrush Hover, IBrush Active)? BarBrushes
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    }

    /// <summary>Raised when the user drags the bar or pages, with the offset to scroll to.</summary>
    public event Action<double>? Scrolled;

    /// <summary>Whether the content is taller than the view, so that the bar shows.</summary>
    public bool CanScroll => _extent > _viewport;

    /// <summary>Shows a view of <paramref name="viewport"/> at <paramref name="offset"/> in <paramref name="extent"/>.</summary>
    public void Show(double viewport, double extent, double offset)
    {
        (_viewport, _extent, _offset) = (viewport, extent, offset);
        IsHitTestVisible = CanScroll;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (!CanScroll || BarBrushes is not var (normal, hover, active))
            return;
        // Transparent beside the bar: a click there pages.
        context.FillRectangle(Avalonia.Media.Brushes.Transparent, new Rect(Bounds.Size));
        context.FillRectangle(
            _drag is not null ? active
                : _pointerOver ? hover
                : normal,
            Bar()
        );
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // Handled, so that the press neither takes the focus nor reaches the content.
        e.Handled = true;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Rect bar = Bar();
        double y = e.GetPosition(this).Y;
        if (y < bar.Top)
            Scrolled?.Invoke(_offset - _viewport);
        else if (y > bar.Bottom)
            Scrolled?.Invoke(_offset + _viewport);
        else
        {
            _drag = (y, _offset);
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        double y = e.GetPosition(this).Y;
        if (_drag is var (start, offset))
        {
            double track = Bounds.Height - Bar().Height;
            if (track > 0)
                Scrolled?.Invoke(offset + (y - start) / track * (_extent - _viewport));
            return;
        }
        bool over = Bar().Contains(e.GetPosition(this));
        if (over != _pointerOver)
        {
            _pointerOver = over;
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
        _pointerOver = false;
        InvalidateVisual();
    }

    /// <summary>The bar: as long as the view is of the content, and as far down as the view is in it.</summary>
    private Rect Bar()
    {
        double height = Bounds.Height;
        double length = Math.Min(height, Math.Max(MinLength, height * _viewport / _extent));
        double top = (height - length) * Math.Clamp(_offset / (_extent - _viewport), 0, 1);
        return new Rect(0, top, Thickness, length);
    }
}

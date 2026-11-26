using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Darp.CodeEditor.Popups;

// The controls inside the editor's popups. A derived control has a style key of its own, so that an app's styles for
// text blocks, panels, borders and scroll bars don't reach the popups. CodeEditorStyles holds the themes of those
// with a template.

/// <summary>Text in a popup.</summary>
internal sealed class PopupText : TextBlock;

/// <summary>Text in a popup that the user can select and copy, as in a hover: clicking it gives it the keyboard.</summary>
internal sealed class PopupSelectableText : SelectableTextBlock
{
    private static readonly Cursor s_beam = new(StandardCursorType.Ibeam);

    public PopupSelectableText(IBrush selection)
    {
        Cursor = s_beam;
        SelectionBrush = selection;
    }
}

/// <summary>Blocks of a popup, one below the other.</summary>
internal sealed class PopupStack : StackPanel;

/// <summary>A row of a popup, docked.</summary>
internal sealed class PopupDock : DockPanel;

/// <summary>A box around a part of a popup, such as code on a background of its own.</summary>
internal sealed class PopupBox : Border;

/// <summary>A line between a popup's parts.</summary>
internal sealed class PopupLine : Border
{
    public PopupLine() => Height = 1;
}

/// <summary>
/// A button in a popup: a chevron that points up or down, in the color that <see cref="Paint"/> gives it, on
/// <c>list.hoverBackground</c> under the pointer. A click on it is handled, so that the popup takes no focus: the
/// editor keeps the keyboard.
/// </summary>
internal sealed class PopupButton : Control
{
    private const double Size = 16;

    private static readonly Geometry s_up = Geometry.Parse("M4.5,10 L8,6.5 L11.5,10");
    private static readonly Geometry s_down = Geometry.Parse("M4.5,6.5 L8,10 L11.5,6.5");
    private static readonly Cursor s_hand = new(StandardCursorType.Hand);

    private readonly Geometry _chevron;
    private IPen? _pen;
    private IBrush? _hover;

    /// <param name="up">Whether the chevron points up; down otherwise.</param>
    public PopupButton(bool up)
    {
        _chevron = up ? s_up : s_down;
        Width = Size;
        Height = Size;
        Cursor = s_hand;
    }

    /// <summary>Raised when the user clicks the button.</summary>
    public event Action? Clicked;

    public void Paint(IBrush foreground, IBrush hover)
    {
        _pen = new ImmutablePen(foreground.ToImmutable(), 1.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        _hover = hover;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        // Transparent at rest, as a click anywhere on it counts.
        context.DrawRectangle(IsPointerOver ? _hover : Brushes.Transparent, null, new Rect(Bounds.Size), 3, 3);
        context.DrawGeometry(null, _pen, _chevron);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPointerOverProperty)
            InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            Clicked?.Invoke();
    }
}

/// <summary>
/// Scrolls a popup's content up and down. Its bar takes a gutter of its own while the content is taller than the view,
/// so that it never covers the text, and the content wraps to the width left.
/// </summary>
internal sealed class PopupScrollViewer : ScrollViewer;

/// <summary>The bar of a <see cref="PopupScrollViewer"/>, in the color theme's <c>scrollbarSlider.*</c> colors.</summary>
internal sealed class PopupScrollBar : ScrollBar;

/// <summary>The part of a <see cref="PopupScrollBar"/> that is dragged.</summary>
internal sealed class PopupThumb : Thumb;

/// <summary>The part of a <see cref="PopupScrollBar"/> beside its thumb: a click there pages.</summary>
internal sealed class PopupTrackButton : RepeatButton;

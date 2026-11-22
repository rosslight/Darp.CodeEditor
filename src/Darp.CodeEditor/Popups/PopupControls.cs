using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

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

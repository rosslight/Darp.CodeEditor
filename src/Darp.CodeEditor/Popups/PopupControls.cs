using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Darp.CodeEditor.Popups;

// The text and panels inside the editor's popups. Each has a style key of its own, so that an app's styles for text
// blocks, panels and borders don't reach the popups; and as styles for derived types, such as :is(TextBlock), still
// would, each sets its typography and layout itself, which no style overrides. Text also gets its font and foreground
// set, from the popup's look: prose, as HoverView.Prose makes it, the app's font and the popup's foreground.

/// <summary>Text in a popup.</summary>
internal sealed class PopupText : TextBlock
{
    public PopupText() => Reset(this);

    protected override Type StyleKeyOverride => typeof(PopupText);

    /// <summary>Sets the typography and layout that an app's styles would otherwise set.</summary>
    public static void Reset(TextBlock text)
    {
        text.FontWeight = FontWeight.Normal;
        text.FontStyle = FontStyle.Normal;
        text.FontStretch = FontStretch.Normal;
        text.TextDecorations = null;
        text.LineHeight = double.NaN;
        text.LetterSpacing = 0;
        text.Margin = default;
        text.Padding = default;
    }
}

/// <summary>
/// Text in a popup that the user can select and copy with Ctrl+C, as in a hover: clicking it gives it the keyboard.
/// </summary>
internal sealed class PopupSelectableText : SelectableTextBlock
{
    private static readonly Cursor s_beam = new(StandardCursorType.Ibeam);

    public PopupSelectableText(IBrush selection)
    {
        PopupText.Reset(this);
        Cursor = s_beam;
        SelectionBrush = selection;
    }

    protected override Type StyleKeyOverride => typeof(PopupSelectableText);
}

/// <summary>Blocks of a popup, one below the other.</summary>
internal sealed class PopupStack : StackPanel
{
    public PopupStack()
    {
        Spacing = 0;
        Margin = default;
    }

    protected override Type StyleKeyOverride => typeof(PopupStack);
}

/// <summary>A row of a popup, docked.</summary>
internal sealed class PopupDock : DockPanel
{
    public PopupDock()
    {
        LastChildFill = true;
        Margin = default;
    }

    protected override Type StyleKeyOverride => typeof(PopupDock);
}

/// <summary>A box around a part of a popup, such as signature help's active signature on a background of its own.</summary>
internal sealed class PopupBox : Border
{
    public PopupBox()
    {
        Margin = default;
        BorderThickness = default;
    }

    protected override Type StyleKeyOverride => typeof(PopupBox);
}

/// <summary>A line between a popup's parts.</summary>
internal sealed class PopupLine : Border
{
    public PopupLine()
    {
        Margin = default;
        BorderThickness = default;
    }

    protected override Type StyleKeyOverride => typeof(PopupLine);
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Darp.CodeEditor.Popups;

// The text and panels inside the editor's popups. Each has a style key of its own, so that an app's styles for text
// blocks, panels and borders don't reach the popups; and as styles for derived types, such as :is(TextBlock), still
// would, each sets its typography and layout itself, which no style overrides. The popup sets the rest, its font and
// foreground, where the text inherits them.

/// <summary>Text in a popup.</summary>
internal sealed class PopupText : TextBlock
{
    public PopupText()
    {
        FontWeight = FontWeight.Normal;
        FontStyle = FontStyle.Normal;
        FontStretch = FontStretch.Normal;
        TextDecorations = null;
        LineHeight = double.NaN;
        LetterSpacing = 0;
        Margin = default;
        Padding = default;
    }

    protected override Type StyleKeyOverride => typeof(PopupText);
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

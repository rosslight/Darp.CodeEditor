using Avalonia.Controls;

namespace Darp.CodeEditor.Popups;

// The text and panels inside the editor's popups. Each has a style key of its own, so that an app's styles for text
// blocks, panels and borders never reach the popups: they look as the editor sets them.

/// <summary>Text in a popup.</summary>
internal sealed class PopupText : TextBlock
{
    protected override Type StyleKeyOverride => typeof(PopupText);
}

/// <summary>Blocks of a popup, one below the other.</summary>
internal sealed class PopupStack : StackPanel
{
    protected override Type StyleKeyOverride => typeof(PopupStack);
}

/// <summary>A row of a popup, docked.</summary>
internal sealed class PopupDock : DockPanel
{
    protected override Type StyleKeyOverride => typeof(PopupDock);
}

/// <summary>A line between a popup's parts.</summary>
internal sealed class PopupLine : Border
{
    protected override Type StyleKeyOverride => typeof(PopupLine);
}

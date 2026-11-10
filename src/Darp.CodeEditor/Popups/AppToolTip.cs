using Avalonia.Controls;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The app's tooltip, shown open, as the look of the hover and of signature help: its font, padding, corners and colors.
/// </summary>
internal sealed class AppToolTip : ToolTip
{
    public AppToolTip() => PseudoClasses.Set(":open", true);

    protected override Type StyleKeyOverride => typeof(ToolTip);
}

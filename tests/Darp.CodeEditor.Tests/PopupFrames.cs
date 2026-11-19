using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Tests;

/// <summary>Finds the editor's popups in a window's overlay layer.</summary>
internal static class PopupFrames
{
    /// <summary>The shown popup named <paramref name="name"/>, such as "Hover"; null while none shows.</summary>
    public static PopupFrame? Find(EditorWindow window, string name) =>
        OverlayLayer
            .GetOverlayLayer(window.Window)
            ?.GetVisualDescendants()
            .OfType<PopupFrame>()
            .FirstOrDefault(frame => frame.Name == name && frame.IsEffectivelyVisible);

    /// <summary>What a popup with a scroller shows.</summary>
    public static Avalonia.Controls.Control Content(PopupFrame frame) =>
        frame.GetVisualDescendants().OfType<PopupScroller>().First().Child
        ?? throw new InvalidOperationException("The popup shows nothing.");
}

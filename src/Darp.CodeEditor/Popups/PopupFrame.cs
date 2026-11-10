using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The outside of an editor popup, as VS Code draws its editor widgets: a background, a 1-pixel border, small corners
/// and a shadow, all from the editor's color theme. It is the editor's own control with its values set locally, so an
/// app's styles for borders, flyouts or tooltips never reach it.
/// </summary>
internal class PopupFrame : Border
{
    /// <summary>The corners' radius, as VS Code's widgets.</summary>
    public const double Radius = 3;

    public PopupFrame()
    {
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(Radius);
    }

    /// <summary>Paints the frame, and sets the foreground that its text inherits.</summary>
    public void Paint(IBrush background, IBrush border, IBrush foreground, BoxShadows shadow)
    {
        Background = background;
        BorderBrush = border;
        BoxShadow = shadow;
        TextElement.SetForeground(this, foreground);
    }
}

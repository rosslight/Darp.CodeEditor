using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The outside of an editor popup, as VS Code draws its editor widgets: a background, a 1-pixel border, small corners
/// and a shadow, in the colors that <see cref="Paint"/> gives it. Its type is its own and its values are set on it, so
/// that an app's styles for borders, flyouts and tooltips never reach it.
/// </summary>
internal sealed class PopupFrame : Border
{
    /// <summary>The most width of a popup of text, before it wraps.</summary>
    public const double MaxTextWidth = 320;

    /// <summary>The corners' radius, as VS Code's widgets.</summary>
    private const double Radius = 3;

    public PopupFrame()
    {
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(Radius);
        // Not what the window around passes down: the popup's text is as the editor sets it.
        TextElement.SetFontWeight(this, FontWeight.Normal);
        TextElement.SetFontStyle(this, FontStyle.Normal);
    }

    /// <summary>
    /// Paints the frame, gives its text <paramref name="foreground"/>, and gives the scroll bars and list rows inside it
    /// their colors, which their themes read as resources.
    /// </summary>
    public void Paint(IBrush background, IBrush border, IBrush foreground, PopupColors colors)
    {
        Background = background;
        BorderBrush = border;
        BoxShadow = colors.Shadow;
        TextElement.SetForeground(this, foreground);
        Resources["CodeEditorPopupSlider"] = colors.Slider.Normal;
        Resources["CodeEditorPopupSliderHover"] = colors.Slider.Hover;
        Resources["CodeEditorPopupSliderActive"] = colors.Slider.Active;
        Resources["CodeEditorPopupRowHover"] = colors.ListHoverBackground;
        Resources["CodeEditorPopupRowSelected"] = colors.SuggestSelectedBackground;
        Resources["CodeEditorPopupRowSelectedText"] = colors.SuggestSelectedForeground;
    }
}

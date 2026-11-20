using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Themes.Simple;
using Sample.Localization;

namespace Sample;

/// <summary>
/// A window in one of Avalonia's own themes, Fluent or Simple, light or dark, with an editor of the app's current
/// document: to check the look of the editor and its popups in each. The main window keeps FluentAvalonia, which its
/// controls need; this window holds its theme in its own styles, which its popups take too, as they show in its overlay
/// layer.
/// </summary>
public sealed partial class ThemeWindow : Window
{
    public ThemeWindow() => InitializeComponent();

    /// <summary>
    /// Opens a theme window that shows what <paramref name="owner"/> shows, its data context, and closes with it.
    /// </summary>
    /// <param name="owner">The main window.</param>
    /// <param name="fluent">Whether the theme is Fluent; else it is Simple.</param>
    /// <param name="variant">Light or dark.</param>
    public static void Open(Window owner, bool fluent, ThemeVariant variant)
    {
        var window = new ThemeWindow { DataContext = owner.DataContext, RequestedThemeVariant = variant };
        Styles theme = fluent ? new FluentTheme() : new SimpleTheme();
        window.Styles.Add(theme);
        // Fluent follows the platform's colors, whose subscription would keep the closed window alive. The theme leaves
        // as the window closes, also with its owner, while the platform's settings are still there to unsubscribe from.
        window.Closing += (_, _) => window.Styles.Remove(theme);
        // The title follows the app's language. The strings outlive the window, which stops listening as it closes.
        PropertyChangedEventHandler updateTitle = (_, _) => window.Title = Describe(fluent, variant);
        Strings.Current.PropertyChanged += updateTitle;
        window.Closed += (_, _) => Strings.Current.PropertyChanged -= updateTitle;
        window.Title = Describe(fluent, variant);
        window.Show(owner);
    }

    /// <summary>What the window's title and the menu that opens it call it: the theme and the variant.</summary>
    public static string Describe(bool fluent, ThemeVariant variant) =>
        $"{(fluent ? "Fluent" : "Simple")} {(variant == ThemeVariant.Dark ? Strings.Current.ThemeDark : Strings.Current.ThemeLight)}";
}

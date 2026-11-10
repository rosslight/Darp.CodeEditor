using System.ComponentModel;
using System.Globalization;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using Sample.Localization;

namespace Sample.Settings;

/// <summary>
/// The user's choices: the themes and the language on the settings page, and the view's options in the title bar.
/// They last as long as the app runs.
/// </summary>
public sealed partial class AppSettings : ObservableObject
{
    public AppSettings()
    {
        AppThemes =
        [
            new(ThemeVariant.Default, strings => strings.ThemeSystem),
            new(ThemeVariant.Light, strings => strings.ThemeLight),
            new(ThemeVariant.Dark, strings => strings.ThemeDark),
        ];
        EditorThemes =
        [
            new(ThemeVariant.Default, strings => strings.ThemeSameAsApp),
            new(ThemeVariant.Light, strings => strings.ThemeLight),
            new(ThemeVariant.Dark, strings => strings.ThemeDark),
        ];
        AppTheme = AppThemes[0];
        EditorTheme = EditorThemes[0];
    }

    public IReadOnlyList<ThemeChoice> AppThemes { get; }

    /// <summary>The theme variant of the whole app. Editors follow it with Light+ or Dark+.</summary>
    [ObservableProperty]
    public partial ThemeChoice AppTheme { get; set; }

    public IReadOnlyList<ThemeChoice> EditorThemes { get; }

    /// <summary>
    /// The theme variant of the editor alone, through a <c>ThemeVariantScope</c> around it, so that its popups and
    /// search bar match its colors.
    /// </summary>
    [ObservableProperty]
    public partial ThemeChoice EditorTheme { get; set; }

    public IReadOnlyList<CultureInfo> Cultures => Strings.Cultures;

    /// <summary>
    /// The language of the app's own text, and of the UI thread, which the editor's context menu follows.
    /// </summary>
    public CultureInfo Culture
    {
        get => Strings.Default.Culture ?? Strings.Cultures[0];
        set
        {
            Strings.Default.Culture = value;
            CultureInfo.CurrentUICulture = value;
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    public partial bool WordWrap { get; set; }

    [ObservableProperty]
    public partial bool ShowLineNumbers { get; set; } = true;

    public IReadOnlyList<double> FontSizes { get; } = [10, 11, 12, 13, 14, 16, 18, 20, 24];

    [ObservableProperty]
    public partial double FontSize { get; set; } = 14;
}

/// <summary>A theme variant to choose from, with its name in the app's language.</summary>
public sealed class ThemeChoice : INotifyPropertyChanged
{
    private readonly Func<Strings, string> _name;

    public ThemeChoice(ThemeVariant variant, Func<Strings, string> name)
    {
        Variant = variant;
        _name = name;
        // Choices last as long as the app, as the strings do.
        Strings.Default.PropertyChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The variant; <see cref="ThemeVariant.Default"/> follows the system, or the app around an editor.</summary>
    public ThemeVariant Variant { get; }

    public string Name => _name(Strings.Default);
}

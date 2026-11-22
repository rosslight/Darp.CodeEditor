using System.ComponentModel;
using System.Globalization;
using Darp.CodeEditor;

namespace Sample.Localization;

/// <summary>
/// The app's own text, generated from <c>Strings.resx</c> and its translations, in the language the user chose.
/// Bindings to its properties update when the language changes.
/// </summary>
public sealed partial class Strings : INotifyPropertyChanged
{
    private Strings()
    {
        // The strings last as long as the app.
        CultureChanged += (_, culture) =>
        {
            if (culture is not null)
                CultureInfo.CurrentUICulture = culture;
            // An empty name tells bindings that every property changed.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        };
        string language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        Culture = Cultures.FirstOrDefault(culture => culture.TwoLetterISOLanguageName == language) ?? Cultures[0];
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The languages the app speaks.</summary>
    public static IReadOnlyList<CultureInfo> Cultures { get; } = [new("en"), new("de")];

    /// <summary>Where a language server is in its life, in words: the texts <c>ServerRunning</c> and so on.</summary>
    public string Describe(LanguageServerState state) => GetResourceString($"Server{state}");
}

using System.ComponentModel;
using System.Globalization;
using Darp.CodeEditor;

namespace Sample.Localization;

/// <summary>
/// The app's own text. Darp.Utils.ResxSourceGenerator generates its properties from <c>Strings.resx</c>, and a
/// <c>Format</c> method for each text with placeholders; this part adds what bindings need. Bindings to its properties
/// update when <see cref="Strings.Culture"/> changes.
/// </summary>
public sealed partial class Strings : INotifyPropertyChanged
{
    private Strings()
    {
        string language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        Culture = Cultures.FirstOrDefault(culture => culture.TwoLetterISOLanguageName == language) ?? Cultures[0];
        // An empty name tells bindings that every property changed.
        CultureChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The languages the app speaks. <see cref="Strings.Culture"/> starts as the system's, if it is one.</summary>
    public static IReadOnlyList<CultureInfo> Cultures { get; } = [new("en"), new("de")];

    /// <summary>Where a language server is in its life, in words: the texts <c>ServerRunning</c> and so on.</summary>
    public string Describe(LanguageServerState state) => GetResourceString($"Server{state}");

    /// <summary>Fills in a text with placeholders that a binding gave, such as <see cref="CaretPosition"/>.</summary>
    public string Format(string text, params object?[] args) => string.Format(Culture, text, args);
}

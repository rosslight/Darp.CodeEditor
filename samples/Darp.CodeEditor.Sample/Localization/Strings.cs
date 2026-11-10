using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using Darp.CodeEditor;

namespace Sample.Localization;

/// <summary>
/// The app's own text, from <c>Strings.resx</c> and its translations, in the language the user chose. Bindings to its
/// properties update when the language changes.
/// </summary>
public sealed class Strings : INotifyPropertyChanged
{
    private static readonly ResourceManager s_resources = new(typeof(Strings));

    private Strings()
    {
        string language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        Culture = Cultures.FirstOrDefault(culture => culture.TwoLetterISOLanguageName == language) ?? Cultures[0];
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The languages the app speaks.</summary>
    public static IReadOnlyList<CultureInfo> Cultures { get; } = [new("en"), new("de")];

    /// <summary>The app's text.</summary>
    public static Strings Current { get; } = new();

    /// <summary>The language of the text, one of <see cref="Cultures"/>. Defaults to the system's, if the app speaks it.</summary>
    public CultureInfo Culture
    {
        get;
        set
        {
            if (Equals(field, value))
                return;
            field = value;
            CultureInfo.CurrentUICulture = value;
            // An empty name tells bindings that every property changed.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    public string Documents => Get();
    public string New => Get();
    public string Open => Get();
    public string Save => Get();
    public string SaveAs => Get();
    public string Close => Get();
    public string Undo => Get();
    public string Redo => Get();
    public string Find => Get();
    public string View => Get();
    public string Replace => Get();
    public string GoToLine => Get();
    public string Go => Get();
    public string InsertDateTime => Get();
    public string CaretPosition => Get();
    public string IndentSpaces => Get();
    public string IndentTabs => Get();
    public string Untitled => Get();
    public string NoFile => Get();
    public string StateNoFile => Get();
    public string StateUnsaved => Get();
    public string NoServer => Get();
    public string ServerNotInstalled => Get();
    public string CopyPath => Get();
    public string ShowInFolder => Get();
    public string AllFiles => Get();
    public string OpenFailed => Get();
    public string SaveFailed => Get();
    public string OpenInAnother => Get();
    public string Ok => Get();
    public string SaveChanges => Get();
    public string SaveChangesMessage => Get();
    public string DontSave => Get();
    public string Cancel => Get();
    public string Settings => Get();
    public string Appearance => Get();
    public string AppTheme => Get();
    public string AppThemeDescription => Get();
    public string EditorTheme => Get();
    public string EditorThemeDescription => Get();
    public string ThemeSystem => Get();
    public string ThemeSameAsApp => Get();
    public string ThemeLight => Get();
    public string ThemeDark => Get();
    public string Language => Get();
    public string LanguageDescription => Get();
    public string WordWrap => Get();
    public string LineNumbers => Get();
    public string FontSize => Get();
    public string About => Get();
    public string AboutDescription => Get();
    public string SourceCode => Get();

    /// <summary>Where a language server is in its life, in words: the texts <c>ServerRunning</c> and so on.</summary>
    public string Describe(LanguageServerState state) => Get($"Server{state}");

    /// <summary>Fills in a text with placeholders, such as <see cref="Untitled"/>.</summary>
    public string Format(string text, params object?[] args) => string.Format(Culture, text, args);

    private string Get([CallerMemberName] string name = "") => s_resources.GetString(name, Culture) ?? name;
}

namespace Darp.CodeEditor;

/// <summary>
/// The colors of a <see cref="CodeEditor"/>'s text area: a VS Code color theme, with its TextMate token colors and its
/// semantic token colors, so that syntax and language-server colors never clash.
/// </summary>
/// <remarks>
/// <para>
/// Create a theme once and share it between editors; it is immutable and safe to use from any thread. In XAML, create
/// it with <see cref="Source"/>, usually in the app's theme dictionaries:
/// </para>
/// <code><![CDATA[
/// <ResourceDictionary.ThemeDictionaries>
///   <ResourceDictionary x:Key="Dark">
///     <ce:CodeEditorTheme x:Key="CodeEditorColorTheme" Source="avares://MyApp/Themes/brand-dark.json" />
///   </ResourceDictionary>
/// </ResourceDictionary.ThemeDictionaries>
/// ]]></code>
/// <para>
/// A theme loads when an editor first uses it, or earlier with <see cref="PreloadAsync"/>. When its file is missing or
/// invalid, <see cref="PreloadAsync"/> throws, and editors show <see cref="LightPlus"/> or <see cref="DarkPlus"/> by
/// their theme variant.
/// </para>
/// </remarks>
public sealed class CodeEditorTheme
{
    /// <summary>Creates a theme from <see cref="Source"/>, for XAML.</summary>
    public CodeEditorTheme() { }

    /// <summary>VS Code's default dark theme.</summary>
    public static CodeEditorTheme DarkPlus { get; } = new();

    /// <summary>VS Code's default light theme.</summary>
    public static CodeEditorTheme LightPlus { get; } = new();

    /// <summary>
    /// The theme's file, a VS Code color theme: an absolute <c>avares://</c> or <c>file://</c> URI. The themes it
    /// includes are found relative to it.
    /// </summary>
    public Uri? Source { get; init; }

    /// <summary>A VS Code color theme file, such as one from a theme extension.</summary>
    public static CodeEditorTheme FromFile(string path) => new() { Source = new Uri(Path.GetFullPath(path)) };

    /// <summary>Loads the theme off the UI thread. Optional: editors load it on their own otherwise.</summary>
    public Task PreloadAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

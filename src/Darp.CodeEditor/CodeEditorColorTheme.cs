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
///     <ce:CodeEditorColorTheme x:Key="CodeEditorColorTheme" Source="avares://MyApp/Themes/brand-dark.json" />
///   </ResourceDictionary>
/// </ResourceDictionary.ThemeDictionaries>
/// ]]></code>
/// <para>
/// A theme starts loading in the background when it is created; built-in themes when first used. Loading reads
/// <c>avares://</c> files without Avalonia's services, so it works before the app's <c>AppBuilder</c> runs. When its
/// file is missing or invalid, editors show <see cref="LightPlus"/> or <see cref="DarkPlus"/> by their theme variant,
/// and the error goes to Avalonia's log, as binding errors do; apps see it once they set up a log sink, such as
/// <c>LogToTrace()</c>.
/// </para>
/// </remarks>
public sealed class CodeEditorColorTheme
{
    /// <summary>Creates a theme from <see cref="Source"/>, for XAML. Setting the source starts loading it.</summary>
    public CodeEditorColorTheme() { }

    /// <summary>VS Code's default dark theme.</summary>
    public static CodeEditorColorTheme DarkPlus { get; } =
        new() { Source = new Uri("avares://Darp.CodeEditor/Themes/dark_plus.json") };

    /// <summary>VS Code's default light theme.</summary>
    public static CodeEditorColorTheme LightPlus { get; } =
        new() { Source = new Uri("avares://Darp.CodeEditor/Themes/light_plus.json") };

    /// <summary>
    /// The theme's file, a VS Code color theme: an absolute <c>avares://</c> or <c>file://</c> URI. The themes it
    /// includes are found relative to it.
    /// </summary>
    public Uri? Source { get; init; }
}

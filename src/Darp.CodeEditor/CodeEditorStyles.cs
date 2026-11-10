using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Darp.CodeEditor;

/// <summary>
/// The editor's styles. Add them once to the app's styles, after its theme:
/// <code><![CDATA[
/// <Application.Styles>
///   <FluentTheme />
///   <ce:CodeEditorStyles />
/// </Application.Styles>
/// ]]></code>
/// </summary>
/// <remarks>
/// <para>
/// They hold the editor's control theme, AvaloniaEdit's styles, and the <c>CodeEditorColorTheme</c> resource:
/// <see cref="CodeEditorColorTheme.LightPlus"/> for light theme variants and <see cref="CodeEditorColorTheme.DarkPlus"/> for dark
/// ones. Override that resource in the app's theme dictionaries to give every editor other colors.
/// </para>
/// <para>
/// The search bar and scroll bars take their look from the app's theme, whichever it is. Completion, hover and signature
/// help are the editor's own: their colors come from the editor's color theme, so app themes don't restyle them.
/// </para>
/// </remarks>
public sealed partial class CodeEditorStyles : Styles
{
    /// <summary>Creates the styles.</summary>
    public CodeEditorStyles()
    {
        AvaloniaXamlLoader.Load(this);
        Resources.ThemeDictionaries[ThemeVariant.Light] = ColorTheme(CodeEditorColorTheme.LightPlus);
        Resources.ThemeDictionaries[ThemeVariant.Dark] = ColorTheme(CodeEditorColorTheme.DarkPlus);
    }

    private static ResourceDictionary ColorTheme(CodeEditorColorTheme theme) =>
        new() { ["CodeEditorColorTheme"] = theme };
}

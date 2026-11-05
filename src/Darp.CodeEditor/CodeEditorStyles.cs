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
/// <see cref="CodeEditorTheme.LightPlus"/> for light theme variants and <see cref="CodeEditorTheme.DarkPlus"/> for dark
/// ones. Override that resource in the app's theme dictionaries to give every editor other colors.
/// </para>
/// <para>
/// Popups, the search bar and scroll bars take their look from the app's theme, whichever it is: completion, hover and
/// signature help use the app's flyout and tooltip looks, and the search bar uses its text boxes and buttons.
/// </para>
/// </remarks>
public sealed class CodeEditorStyles : Styles
{
    /// <summary>Creates the styles.</summary>
    public CodeEditorStyles() { }
}

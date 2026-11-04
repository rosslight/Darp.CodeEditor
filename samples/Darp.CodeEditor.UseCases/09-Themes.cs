using Darp.CodeEditor;

namespace UseCases.Themes;

// Scenario: editors follow the app's light and dark switch on their own, with VS Code's Light+ and Dark+. An app with
// its own colors loads VS Code theme files instead, once, and gives them to every editor in a style:
//
//   <Style Selector="ce|CodeEditor">
//     <Setter Property="DarkTheme" Value="{x:Static themes:BrandThemes.Dark}" />
//     <Setter Property="LightTheme" Value="{x:Static themes:BrandThemes.Light}" />
//   </Style>
//
// Fonts come from the control's FontFamily and FontSize, as for any Avalonia control.
//
// Questions:
// - Is a pair of themes that follows the theme variant right, or should apps pick one theme themselves?

/// <summary>The app's own editor colors.</summary>
public static class BrandThemes
{
    /// <summary>The dark colors.</summary>
    public static CodeEditorTheme Dark { get; } =
        CodeEditorTheme.FromFile(Path.Combine(AppContext.BaseDirectory, "Themes", "brand-dark.json"));

    /// <summary>The light colors.</summary>
    public static CodeEditorTheme Light { get; } =
        CodeEditorTheme.FromFile(Path.Combine(AppContext.BaseDirectory, "Themes", "brand-light.json"));
}

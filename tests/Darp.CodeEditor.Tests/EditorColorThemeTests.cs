using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Darp.CodeEditor.Tests;

/// <summary>Which colors an editor's text area shows: its color theme, from resources by theme variant.</summary>
[Collection(UiFixture.Collection)]
public sealed class EditorColorThemeTests(UiFixture ui)
{
    private static readonly Color s_lightBackground = Color.Parse("#FFFFFF");
    private static readonly Color s_darkBackground = Color.Parse("#1E1E1E");

    /// <summary>The colors of JSON's text in Light+: default text, property names, strings and numbers.</summary>
    private static readonly Color[] s_lightText =
    [
        .. new[] { "#000000", "#0451A5", "#A31515", "#098658" }.Select(Color.Parse),
    ];

    /// <summary>The same in Dark+.</summary>
    private static readonly Color[] s_darkText =
    [
        .. new[] { "#D4D4D4", "#9CDCFE", "#CE9178", "#B5CEA8" }.Select(Color.Parse),
    ];

    private readonly UiFixture _ui = ui;

    [Fact]
    public Task The_color_theme_comes_from_the_resources_of_the_editors_theme_variant() =>
        _ui.Run(async () =>
        {
            var light = new CodeEditor { Document = CodeDocument.Create(CodeLanguage.Json, "{}") };
            var dark = new CodeEditor { Document = CodeDocument.Create(CodeLanguage.Json, "{}") };
            var content = new StackPanel
            {
                Children =
                {
                    light,
                    new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Dark, Child = dark },
                },
            };
            using var window = new EditorWindow(light, content);

            light.ColorTheme.ShouldBeSameAs(CodeEditorColorTheme.LightPlus);
            dark.ColorTheme.ShouldBeSameAs(CodeEditorColorTheme.DarkPlus);
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return Background(light) == s_lightBackground && Background(dark) == s_darkBackground;
            });
        });

    [Fact]
    public Task A_theme_whose_file_failed_to_load_shows_Light_plus_or_Dark_plus_by_the_theme_variant() =>
        _ui.Run(async () =>
        {
            var broken = new CodeEditorColorTheme { Source = new Uri("avares://Darp.CodeEditor.Tests/missing.json") };
            (await broken.Loaded).ShouldBeNull();
            var light = new CodeEditor { ColorTheme = broken };
            var dark = new CodeEditor { ColorTheme = broken };
            var content = new StackPanel
            {
                Children =
                {
                    light,
                    new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Dark, Child = dark },
                },
            };
            using var window = new EditorWindow(light, content);

            await Wait.UntilAsync(() =>
            {
                window.Render();
                return Background(light) == s_lightBackground && Background(dark) == s_darkBackground;
            });
        });

    [Fact]
    public Task A_new_theme_variant_recolors_text_and_background_in_the_same_frame() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json, Texts.Json(100));
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.RenderedColors().Contains(s_lightText[1]);
            });

            window.Window.RequestedThemeVariant = ThemeVariant.Dark;

            // Each frame shows one theme, with colored text: never the old text on the new background, or no colors.
            await Wait.UntilAsync(() =>
            {
                window.Render();
                IReadOnlySet<Color> text = window.RenderedColors();
                bool dark = Background(window.Editor) == s_darkBackground;
                Color[] theme = dark ? s_darkText : s_lightText;
                text.ShouldBeSubsetOf(theme);
                text.ShouldContain(theme[1]);
                return dark;
            });
        });

    private static Color? Background(CodeEditor editor) =>
        (EditorWindow.PartOf(editor).Background as ISolidColorBrush)?.Color;
}

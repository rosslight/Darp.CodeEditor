using Avalonia.Logging;
using Avalonia.Media;
using Darp.CodeEditor.Loading;
using Shouldly;

namespace Darp.CodeEditor.Tests;

/// <summary>Themes load VS Code's color themes with the themes they include, or fail with an error in the log.</summary>
public sealed class CodeEditorColorThemeTests
{
    public static TheoryData<string, string, string, string> BuiltIn =>
        new()
        {
            // The theme, its editor background (from the included theme), a token color of each file, and its own
            // semantic color, which overrides the included one.
            { nameof(CodeEditorColorTheme.DarkPlus), "#1E1E1E", "#6A9955", "#C586C0" },
            { nameof(CodeEditorColorTheme.LightPlus), "#FFFFFF", "#008000", "#AF00DB" },
        };

    [Theory]
    [MemberData(nameof(BuiltIn))]
    public async Task Built_in_themes_load_with_the_themes_they_include(
        string name,
        string background,
        string comment,
        string newOperator
    )
    {
        CodeEditorColorTheme theme =
            name == nameof(CodeEditorColorTheme.DarkPlus)
                ? CodeEditorColorTheme.DarkPlus
                : CodeEditorColorTheme.LightPlus;

        ColorThemeFile file = (await theme.Loaded).ShouldNotBeNull();

        file.Colors["editor.background"].ShouldBe(Color.Parse(background));
        file.SemanticTokenColors["newOperator"].Foreground.ShouldBe(Color.Parse(newOperator));
        // Comments are colored by a rule of the included theme.
        var textMateTheme = TextMateSharp.Themes.Theme.CreateFromRawTheme(file.TokenRules, null);
        textMateTheme
            .Match(["comment"])
            .ShouldContain(rule =>
                textMateTheme.GetColor(rule.foreground).Equals(comment, StringComparison.OrdinalIgnoreCase)
            );
    }

    [Theory]
    [InlineData("missing.json", null)]
    [InlineData("invalid.json", "{ \"colors\": ")]
    [InlineData("self.json", "{ \"include\": \"./self.json\" }")]
    public async Task A_missing_or_invalid_theme_fails_and_logs_an_error(string fileName, string? content)
    {
        TestLog.Install();
        string directory = Directory.CreateTempSubdirectory().FullName;
        string file = Path.Combine(directory, fileName);
        if (content is not null)
            await File.WriteAllTextAsync(file, content, TestContext.Current.CancellationToken);

        var theme = new CodeEditorColorTheme { Source = new Uri(file) };

        (await theme.Loaded).ShouldBeNull();
        TestLog.EntriesOf(theme).ShouldBe([(LogEventLevel.Error, "CodeEditor")]);
        Directory.Delete(directory, recursive: true);
    }
}

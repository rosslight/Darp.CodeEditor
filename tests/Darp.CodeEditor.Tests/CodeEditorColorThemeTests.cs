using Avalonia.Logging;
using Avalonia.Media;
using Darp.CodeEditor.Loading;

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

        ColorThemeFile? file = await theme.Loaded;

        Assert.NotNull(file);
        Assert.Equal(Color.Parse(background), file.Colors["editor.background"]);
        Assert.Equal(Color.Parse(newOperator), file.SemanticTokenColors["newOperator"].Foreground);
        // Comments are colored by a rule of the included theme.
        var textMateTheme = TextMateSharp.Themes.Theme.CreateFromRawTheme(file.TokenRules, null);
        Assert.Contains(
            textMateTheme.Match(["comment"]),
            rule => textMateTheme.GetColor(rule.foreground).Equals(comment, StringComparison.OrdinalIgnoreCase)
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

        Assert.Null(await theme.Loaded);
        Assert.Equal([(LogEventLevel.Error, "CodeEditor")], TestLog.EntriesOf(theme));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Semantic_styles_set_clear_and_inherit_each_property()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        await File.WriteAllTextAsync(
            Path.Combine(directory, "base.json"),
            """
            { "semanticTokenColors": {
                "variable": { "foreground": "#ff0000", "bold": true },
                "number": { "underline": true } } }
            """,
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            Path.Combine(directory, "theme.json"),
            """
            { "include": "./base.json", "semanticTokenColors": {
                "variable": { "italic": true, "bold": false },
                "keyword": { "fontStyle": "italic" },
                "string": { "fontStyle": "bold\titalic\n underline", "bold": false, "strikethrough": true } } }
            """,
            TestContext.Current.CancellationToken
        );

        var theme = new CodeEditorColorTheme { Source = new Uri(Path.Combine(directory, "theme.json")) };
        ColorThemeFile? file = await theme.Loaded;
        Assert.NotNull(file);
        IReadOnlyDictionary<string, SemanticTokenStyle> styles = file.SemanticTokenColors;

        // The included foreground stays; bold is cleared, italic set, the rest left to TextMate.
        Assert.Equal(new SemanticTokenStyle(Color.Parse("#ff0000"), false, true, null, null), styles["variable"]);
        Assert.Equal(new SemanticTokenStyle(null, null, null, true, null), styles["number"]);
        // fontStyle sets all four styles.
        Assert.Equal(new SemanticTokenStyle(null, false, true, false, false), styles["keyword"]);
        // As in VS Code, any whitespace separates its names, and it wins over the flags.
        Assert.Equal(new SemanticTokenStyle(null, true, true, true, false), styles["string"]);
        Directory.Delete(directory, recursive: true);
    }
}

using Avalonia.Input;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// What the editor indents by itself, by the language's rules and as far as its <see cref="AutoIndent"/> goes: Enter by
/// the rules for Enter, the brackets and the indentation rules.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class AutoIndentTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Theory]
    // A doc comment: Enter after its start continues it, also between its start and its end.
    [InlineData("/**|", "/**\n * |")]
    [InlineData("/**| */", "/**\n * |\n */")]
    [InlineData("/**\n * abc|", "/**\n * abc\n * |")]
    // After its end, the space before it goes.
    [InlineData("/**\n * abc\n */|", "/**\n * abc\n */\n|")]
    // A line after a one-line if outdents what follows.
    [InlineData("if (x)\n    y();|", "if (x)\n    y();\n|")]
    // The expression's flags apply.
    [InlineData("    case 1:|", "    case 1:\n        |")]
    // Brackets apply after the rules.
    [InlineData("    f(|)", "    f(\n        |\n    )")]
    public Task Enter_follows_the_languages_rules_for_Enter(string marked, string expected) =>
        _ui.Run(async () =>
        {
            await Rules.Loaded;
            using EditorWindow window = Show(marked, Rules);

            window.Press(Key.Enter);

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Theory]
    [InlineData("  // alpha |beta", "  // alpha \n  // |beta")]
    // At the comment's end, the rule's afterText doesn't match.
    [InlineData("  // alpha|", "  // alpha\n  |")]
    public Task Enter_in_a_JSON_line_comment_continues_it(string marked, string expected) =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = Show(marked, CodeLanguage.Json);

            window.Press(Key.Enter);

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Theory]
    [InlineData(AutoIndent.None, "    {|", "    {\n|")]
    [InlineData(AutoIndent.Keep, "    {|", "    {\n    |")]
    [InlineData(AutoIndent.Brackets, "    {|", "    {\n        |")]
    [InlineData(AutoIndent.Brackets, "/**|", "/**\n|")]
    [InlineData(AutoIndent.Advanced, "/**|", "/**\n * |")]
    public Task Enter_indents_as_far_as_the_languages_auto_indent_goes(
        AutoIndent autoIndent,
        string marked,
        string expected
    ) =>
        _ui.Run(async () =>
        {
            var language = new CodeLanguage("rules", configuration: Rules.Configuration) { AutoIndent = autoIndent };
            await language.Loaded;
            using EditorWindow window = Show(marked, language);

            window.Press(Key.Enter);

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Theory]
    [InlineData(AutoIndent.Full, "{\n    \"a\": 1,\n}|")]
    [InlineData(AutoIndent.Advanced, "{\n    \"a\": 1,\n    }|")]
    public Task Typing_outdents_a_line_by_the_decrease_pattern_only_with_full_auto_indent(
        AutoIndent autoIndent,
        string expected
    ) =>
        _ui.Run(async () =>
        {
            var language = new CodeLanguage("json", CodeLanguage.Json.Grammar, CodeLanguage.Json.Configuration)
            {
                AutoIndent = autoIndent,
            };
            await language.Loaded;
            using EditorWindow window = Show("{\n    \"a\": 1,\n    |", language);

            window.Type("}");

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Fact]
    public Task YAML_doesnt_indent_by_its_indentation_rules() =>
        _ui.Run(async () =>
        {
            // As VS Code's YAML extension sets editor.autoIndent for the language.
            CodeLanguage.Yaml.AutoIndent.ShouldBe(AutoIndent.Advanced);
            await CodeLanguage.Yaml.Loaded;
            using EditorWindow window = Show("key:|\n", CodeLanguage.Yaml);

            window.Press(Key.Enter);

            MarkedText.Of(window).ShouldBe("key:\n|\n");
        });

    [Theory]
    // A bracket in a string or a comment before the caret doesn't indent the new line.
    [InlineData("{\n  \"a\": \"abc{|\"\n}", "{\n  \"a\": \"abc{\n  |\"\n}")]
    [InlineData("{\n  // a list: [|\n}", "{\n  // a list: [\n  |\n}")]
    // Nor does one in a comment at the line's end, after the text: here the comma keeps the indentation.
    [InlineData("{\n  \"a\": 1, // {|\n}", "{\n  \"a\": 1, // {\n  |\n}")]
    public Task Enter_leaves_out_the_brackets_in_strings_and_comments(string marked, string expected) =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored(marked, CodeLanguage.Json, indentation: 2);

            window.Press(Key.Enter);

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Fact]
    public Task Typing_a_line_that_outdents_leaves_out_the_brackets_in_comments_above() =>
        _ui.Run(async () =>
        {
            // The line above ends with an opening brace in a comment, which would keep the closing bracket indented.
            using EditorWindow window = await ShowColored(
                "{\n  \"a\": [\n    1 // {\n    |",
                CodeLanguage.Json,
                indentation: 2
            );

            window.Type("]");

            MarkedText.Of(window).ShouldBe("{\n  \"a\": [\n    1 // {\n  ]|");
        });

    /// <summary>The rules for Enter of <c>enter.language-configuration.json</c>, with the editor's default auto-indent.</summary>
    private static CodeLanguage Rules { get; } =
        new(
            "rules",
            configuration: new Uri("avares://Darp.CodeEditor.Tests/Languages/enter.language-configuration.json")
        );

    /// <summary>An editor of the marked text, in a document indented by <paramref name="indentation"/> spaces.</summary>
    private static EditorWindow Show(string marked, CodeLanguage language, int indentation = 4)
    {
        EditorWindow window = MarkedText.Show(marked, language);
        window.Editor.Document.ShouldNotBeNull().Indentation = Indentation.Spaces(indentation);
        return window;
    }

    /// <summary>
    /// <see cref="Show"/>, once the grammar colors the text, with more than one color: strings and comments are known
    /// by their tokens.
    /// </summary>
    private static async Task<EditorWindow> ShowColored(string marked, CodeLanguage language, int indentation = 4)
    {
        EditorWindow window = Show(marked, language, indentation);
        await Wait.UntilAsync(() =>
        {
            window.Render();
            return window.RenderedColors().Count > 1;
        });
        return window;
    }
}

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
    public Task YAML_indents_by_its_brackets_and_rules_for_Enter_but_not_by_its_indentation_rules() =>
        _ui.Run(async () =>
        {
            // As VS Code's YAML extension sets editor.autoIndent for the language.
            CodeLanguage.Yaml.AutoIndent.ShouldBe(AutoIndent.Advanced);
            await CodeLanguage.Yaml.Loaded;
            using EditorWindow window = Show("key:|\n", CodeLanguage.Yaml);

            window.Press(Key.Enter);
            MarkedText.Of(window).ShouldBe("key:\n|\n");

            window.Type("a: [");
            window.Press(Key.Enter);
            MarkedText.Of(window).ShouldBe("key:\na: [\n    |\n]\n");
        });

    /// <summary>The rules for Enter of <c>enter.language-configuration.json</c>, with the editor's default auto-indent.</summary>
    private static CodeLanguage Rules { get; } =
        new(
            "rules",
            configuration: new Uri("avares://Darp.CodeEditor.Tests/Languages/enter.language-configuration.json")
        );

    /// <summary>An editor of the marked text, in a document indented by 4 spaces.</summary>
    private static EditorWindow Show(string marked, CodeLanguage language)
    {
        EditorWindow window = MarkedText.Show(marked, language);
        window.Editor.Document.ShouldNotBeNull().Indentation = Indentation.Spaces(4);
        return window;
    }
}

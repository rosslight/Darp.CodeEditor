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

            MarkedText.Of(window).ShouldBe(NewLines.In(expected, start: marked));
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

            MarkedText.Of(window).ShouldBe(NewLines.In(expected, start: marked));
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

            MarkedText.Of(window).ShouldBe(NewLines.In(expected, start: marked));
        });

    [Theory]
    [InlineData(AutoIndent.Full, "begin\n    x\nEnd|")]
    [InlineData(AutoIndent.Advanced, "begin\n    x\n    End|")]
    public Task Typing_outdents_a_line_by_the_decrease_pattern_only_with_full_auto_indent(
        AutoIndent autoIndent,
        string expected
    ) =>
        _ui.Run(async () =>
        {
            // Word brackets, which only the decrease pattern outdents.
            var language = new CodeLanguage(
                "pascal",
                configuration: new Uri("avares://Darp.CodeEditor.Tests/Languages/pascal.language-configuration.json")
            )
            {
                AutoIndent = autoIndent,
            };
            await language.Loaded;
            using EditorWindow window = Show("begin\n    x\n    En|", language);

            window.Type("d");

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

    [Theory]
    // Two levels in, by the indentation rules from the lines above, where Tab alone indents one.
    [InlineData("{\n  \"a\": {\n    \"b\": 1,\n|", "{\n  \"a\": {\n    \"b\": 1,\n    |")]
    // A line of whitespace that is short of it.
    [InlineData("{\n  \"a\": {\n    \"b\": 1,\n | ", "{\n  \"a\": {\n    \"b\": 1,\n    |")]
    // A line that has it already takes whitespace to the next tab stop at the caret, as elsewhere.
    [InlineData("{\n  \"a\": 1,\n   |", "{\n  \"a\": 1,\n    |")]
    // Without rules that indent it, one level.
    [InlineData("{\n  \"a\": 1\n}\n|", "{\n  \"a\": 1\n}\n  |")]
    public Task Tab_on_a_blank_line_goes_to_its_indentation_by_the_indentation_rules(string marked, string expected) =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = Show(marked, CodeLanguage.Json, indentation: 2);

            window.Press(Key.Tab);

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Theory]
    // By what Enter would do after the closest line above with text: here, the rule for Enter after a case.
    [InlineData("    case 1:\n\n|", "    case 1:\n\n        |")]
    [InlineData("    f(\n  |", "    f(\n        |")]
    [InlineData("    x\n|", "    x\n    |")]
    public Task Tab_on_a_blank_line_goes_to_its_indentation_by_the_rules_for_Enter(string marked, string expected) =>
        _ui.Run(async () =>
        {
            await Rules.Loaded;
            using EditorWindow window = Show(marked, Rules);

            window.Press(Key.Tab);

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Theory]
    // A statement that indents only the next line, and the line after that, back at the statement's indentation.
    [InlineData("if (x)|", Key.Enter, "if (x)\n    |")]
    [InlineData("if (x)\n    y();|", Key.Enter, "if (x)\n    y();\n|")]
    [InlineData("    x\n    if (x)\n|", Key.Tab, "    x\n    if (x)\n        |")]
    // A line that gives no indentation: the line above it does.
    [InlineData("    a();\n#region x|", Key.Enter, "    a();\n#region x\n    |")]
    public Task Lines_indent_by_the_patterns_for_the_next_line_only_and_for_lines_without_indentation(
        string marked,
        Key key,
        string expected
    ) =>
        _ui.Run(async () =>
        {
            var language = new CodeLanguage(
                "indent-next",
                configuration: new Uri(
                    "avares://Darp.CodeEditor.Tests/Languages/indent-next.language-configuration.json"
                )
            );
            await language.Loaded;
            using EditorWindow window = Show(marked, language);

            window.Press(key);

            MarkedText.Of(window).ShouldBe(NewLines.In(expected, start: marked));
        });

    [Fact]
    public Task Tab_on_a_blank_line_is_an_undo_step_of_its_own() =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = Show("{\n  \"a\": {\n    \"b\": 1,\n|", CodeLanguage.Json, indentation: 2);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();

            window.Press(Key.Tab);
            window.Type("x");
            document.Undo();
            MarkedText.Of(window).ShouldBe("{\n  \"a\": {\n    \"b\": 1,\n    |");

            document.Undo();
            document.Text.ShouldBe("{\n  \"a\": {\n    \"b\": 1,\n");
        });

    [Theory]
    // A closing bracket typed first on its line goes to the indentation of its opening bracket's line.
    [InlineData("x = (\n    |", ")", "x = (\n)|")]
    [InlineData("    f(a,\n        b\n        |", ")", "    f(a,\n        b\n    )|")]
    [InlineData("{\n    f(a,\n        |", "}", "{\n    f(a,\n}|")]
    // Not after other text on its line, nor when the opening bracket is on its line.
    [InlineData("x = (\n    a|", ")", "x = (\n    a)|")]
    [InlineData("    (a|", ")", "    (a)|")]
    // Not without an opening bracket to match.
    [InlineData("x\n    |", ")", "x\n    )|")]
    public Task A_closing_bracket_typed_first_on_its_line_lines_up_with_its_opening_one(
        string marked,
        string typed,
        string expected
    ) =>
        _ui.Run(async () =>
        {
            await Rules.Loaded;
            using EditorWindow window = Show(marked, Rules);

            window.Type(typed);

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Fact]
    public Task A_closing_bracket_in_a_comment_stays_where_it_is_typed() =>
        _ui.Run(async () =>
        {
            // JSON without its indentation rules, which would outdent the brace before the brackets do.
            var json = new CodeLanguage("json", CodeLanguage.Json.Grammar, CodeLanguage.Json.Configuration)
            {
                AutoIndent = AutoIndent.Advanced,
            };
            using EditorWindow window = await ShowColored("{\n  /*\n  |\n  */\n}", json, indentation: 2);

            window.Type("}");
            MarkedText.Of(window).ShouldBe("{\n  /*\n  }|\n  */\n}");

            window.Editor.Selection = new TextSelection(window.Editor.Document!.Text.Length - 1);
            window.Press(Key.Enter);
            window.Type("  }");
            MarkedText.Of(window).ShouldBe("{\n  /*\n  }\n  */\n\n}|}");
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

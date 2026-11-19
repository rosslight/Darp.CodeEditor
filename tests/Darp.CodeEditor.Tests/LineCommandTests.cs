using Avalonia.Input;
using Darp.CodeEditor.Commands;
using Keys = Avalonia.Input.Key;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The commands on whole lines: moving and copying lines, deleting them, inserting a line, and indenting and outdenting
/// them, each a step of its own for undo.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class LineCommandTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    /// <summary>The keys that copy lines: with Ctrl too on Linux, whose desktops take Shift+Alt+Up and Down.</summary>
    private static KeyModifiers CopyLines =>
        OperatingSystem.IsLinux()
            ? KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt
            : KeyModifiers.Shift | KeyModifiers.Alt;

    /// <summary>A language with the indentation rules and brackets of VS Code's tests of moving lines.</summary>
    private static CodeLanguage IndentRules { get; } =
        new(
            "indent-rules",
            configuration: new Uri("avares://Darp.CodeEditor.Tests/Languages/indent-rules.language-configuration.json")
        );

    /// <summary>A language whose rules for Enter apply to moved lines too.</summary>
    private static CodeLanguage MoveEnter { get; } =
        new(
            "move-enter",
            configuration: new Uri("avares://Darp.CodeEditor.Tests/Languages/move-enter.language-configuration.json")
        );

    [Theory]
    [InlineData("first\nsec|ond\nthird", Keys.Down, "first\nthird\nsec|ond")]
    [InlineData("first\nsec|ond\nthird", Keys.Up, "sec|ond\nfirst\nthird")]
    [InlineData("fi|rst\nsecond", Keys.Up, "fi|rst\nsecond")]
    [InlineData("first\nsec|ond", Keys.Down, "first\nsec|ond")]
    [InlineData("«a\nb\n»c\nd", Keys.Down, "c\n«a\nb\n»d")]
    [InlineData("«a\nb\n»c", Keys.Down, "«a\nb\n»c")]
    [InlineData("a\n«b\nc»\nd", Keys.Up, "«b\nc»\na\nd")]
    [InlineData("a\n|\nb", Keys.Down, "a\nb\n|")]
    [InlineData("a\n|\nb", Keys.Up, "|\na\nb")]
    [InlineData("a\n|\n\nb", Keys.Down, "a\n\n|\nb")]
    public Task Alt_Up_and_Alt_Down_move_the_lines_past_the_line_next_to_them_with_the_selection(
        string marked,
        Keys key,
        string expected
    ) =>
        _ui.Run(() =>
        {
            // A last line that the selection touches only at its start stays, and nothing moves past the text's ends.
            using EditorWindow window = MarkedText.Show(marked, CodeLanguage.PlainText);

            window.Press(key, KeyModifiers.Alt);

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Fact]
    public Task A_selection_made_backwards_stays_backwards_as_its_lines_move() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|first\nsecond line", CodeLanguage.PlainText);
            window.Editor.Selection = new TextSelection(3, 0);

            window.Press(Keys.Down, KeyModifiers.Alt);

            DocumentOf(window).Text.ShouldBe("second line\nfirst");
            window.Editor.Selection.ShouldBe(new TextSelection(15, 12));
        });

    [Fact]
    public Task Moving_lines_is_a_step_of_its_own_that_undo_takes_back_with_the_selection() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("a\n«b»\nc", CodeLanguage.PlainText);
            CodeDocument document = DocumentOf(window);

            window.Type("x");
            window.Press(Keys.Down, KeyModifiers.Alt);
            // At the last line: no change, and no step.
            window.Press(Keys.Down, KeyModifiers.Alt);
            MarkedText.Of(window).ShouldBe("a\nc\nx|");

            document.Undo();
            MarkedText.Of(window).ShouldBe("a\nx|\nc");
            document.Undo();
            MarkedText.Of(window).ShouldBe("a\n«b»\nc");
            document.Redo();
            document.Redo();
            MarkedText.Of(window).ShouldBe("a\nc\nx|");
        });

    [Theory]
    // VS Code's tests of moving lines with indentation rules.
    [InlineData("class X {\n\t|z = 2\n}", Keys.Up, "|z = 2\nclass X {\n}")]
    [InlineData(
        "|const value = 2;\nconst standardLanguageDescriptions = [\n    {\n        diagnosticSource: 'js',\n    }\n];",
        Keys.Down,
        "const standardLanguageDescriptions = [\n    |const value = 2;\n    {\n        diagnosticSource: 'js',\n    }\n];"
    )]
    [InlineData(
        "if (true) {\n    if (false) {\n        «if (1) {\n            console.log('b');\n        }»\n        console.log('a');\n    }\n}",
        Keys.Down,
        "if (true) {\n    if (false) {\n        console.log('a');\n        «if (1) {\n            console.log('b');\n        }»\n    }\n}"
    )]
    // Into a block and out of it.
    [InlineData("{\n}\n|a();", Keys.Up, "{\n    |a();\n}")]
    [InlineData("{\n    |a();\n}", Keys.Down, "{\n}\n|a();")]
    // A closing bracket keeps the indentation of the block it closes, and a line moved above it takes the block's.
    [InlineData("{\n    a();\n    b();\n|}", Keys.Up, "{\n    a();\n|}\n    b();")]
    [InlineData("{\n    a();\n|}\nb();", Keys.Down, "{\n    a();\n    b();\n|}")]
    // Lines that end in an empty line, which takes the indentation too.
    [InlineData("«x\n\n»{\nz", Keys.Down, "{\n    «x\n    \n»z")]
    [InlineData("if (a) {\n}\n«x\n\n»z", Keys.Up, "if (a) {\n    «x\n    \n»}\nz")]
    public Task Moved_lines_take_the_indentation_that_the_language_gives_their_new_place(
        string marked,
        Keys key,
        string expected
    ) =>
        _ui.Run(async () =>
        {
            await IndentRules.Loaded;
            using EditorWindow window = MarkedText.Show(marked, IndentRules);
            DocumentOf(window).Indentation = Indentation.Spaces(4);

            window.Press(key, KeyModifiers.Alt);

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Fact]
    public Task Below_Full_AutoIndent_moved_lines_keep_their_indentation() =>
        _ui.Run(async () =>
        {
            var language = new CodeLanguage(
                "indent-rules-advanced",
                configuration: new Uri(
                    "avares://Darp.CodeEditor.Tests/Languages/indent-rules.language-configuration.json"
                )
            )
            {
                AutoIndent = AutoIndent.Advanced,
            };
            await language.Loaded;
            using EditorWindow window = MarkedText.Show("{\n}\n|a();", language);
            DocumentOf(window).Indentation = Indentation.Spaces(4);

            window.Press(Keys.Up, KeyModifiers.Alt);

            MarkedText.Of(window).ShouldBe("{\n|a();\n}");
        });

    [Fact]
    public Task A_line_moved_down_past_an_opening_bracket_in_JSON_goes_into_its_object() =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = MarkedText.Show(
                "{\n    \"a\": |1,\n    \"b\": {\n        \"c\": 2\n    }\n}",
                CodeLanguage.Json
            );
            DocumentOf(window).Indentation = Indentation.Spaces(4);

            window.Press(Keys.Down, KeyModifiers.Alt);
            MarkedText.Of(window).ShouldBe("{\n    \"b\": {\n        \"a\": |1,\n        \"c\": 2\n    }\n}");

            window.Press(Keys.Up, KeyModifiers.Alt);
            MarkedText.Of(window).ShouldBe("{\n    \"a\": |1,\n    \"b\": {\n        \"c\": 2\n    }\n}");
        });

    [Theory]
    // After a case, by the rule for Enter.
    [InlineData("case 1:\nx();\n|y();", Keys.Up, "case 1:\n    |y();\nx();")]
    // After a doc comment's start, a line with its star keeps the space before it.
    [InlineData("/**\n|x\n * a", Keys.Down, "/**\n * a\n |x")]
    public Task Moved_lines_take_the_indentation_that_the_rules_for_Enter_give_their_new_place(
        string marked,
        Keys key,
        string expected
    ) =>
        _ui.Run(async () =>
        {
            await MoveEnter.Loaded;
            using EditorWindow window = MarkedText.Show(marked, MoveEnter);
            DocumentOf(window).Indentation = Indentation.Spaces(4);

            window.Press(key, KeyModifiers.Alt);

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Fact]
    public Task A_bracket_in_a_comment_gives_lines_moved_below_it_no_indentation() =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = MarkedText.Show("{\n    |\"a\": 1,\n    \"b\": 2 // {\n}", CodeLanguage.Json);
            DocumentOf(window).Indentation = Indentation.Spaces(4);
            // The comment is known by its tokens.
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.RenderedColors().Count > 1;
            });

            window.Press(Keys.Down, KeyModifiers.Alt);

            MarkedText.Of(window).ShouldBe("{\n    \"b\": 2 // {\n    |\"a\": 1,\n}");
        });

    [Theory]
    [InlineData("first\nsec|ond\nthird", false, "first\nsec|ond\nsecond\nthird")]
    [InlineData("first\nsec|ond\nthird", true, "first\nsecond\nsec|ond\nthird")]
    [InlineData("«a\nb»\nc", true, "a\nb\n«a\nb»\nc")]
    [InlineData("«a\nb»\nc", false, "«a\nb»\na\nb\nc")]
    [InlineData("«a\nb\n»c", true, "a\nb\n«a\nb\n»c")]
    [InlineData("a\n|\nb", true, "a\n\n|\nb")]
    [InlineData("a\n|\nb", false, "a\n|\n\nb")]
    [InlineData("ab|", false, "ab|\nab")]
    public Task Copying_lines_puts_a_copy_above_or_below_them_and_the_selection_on_the_lower_one_going_down(
        string marked,
        bool down,
        string expected
    ) =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show(marked, CodeLanguage.PlainText);

            window.Press(down ? Keys.Down : Keys.Up, CopyLines);

            MarkedText.Of(window).ShouldBe(NewLines.In(expected, start: marked));
            DocumentOf(window).Undo();
            MarkedText.Of(window).ShouldBe(marked);
        });

    [Fact]
    public Task A_read_only_editor_moves_and_copies_no_lines() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("a\nb|\nc", CodeLanguage.PlainText);
            window.Editor.IsReadOnly = true;

            window.Press(Keys.Up, KeyModifiers.Alt);
            window.Press(Keys.Down, CopyLines);

            MarkedText.Of(window).ShouldBe("a\nb|\nc");
        });

    [Theory]
    [InlineData("a\nfo|o\nbar\nc", "a\nba|r\nc")]
    [InlineData("a\nbc|", "a|")]
    [InlineData("a\n«b\nc»\nd", "a\nd|")]
    [InlineData("«a\n»b", "|b")]
    [InlineData("ab|c", "|")]
    public Task Ctrl_Shift_K_deletes_the_lines_and_the_caret_keeps_its_column_on_the_line_in_their_place(
        string marked,
        string expected
    ) =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show(marked, CodeLanguage.PlainText);

            window.Press(Keys.K, EditorWindow.Command | KeyModifiers.Shift);

            MarkedText.Of(window).ShouldBe(expected);
            DocumentOf(window).Undo();
            MarkedText.Of(window).ShouldBe(marked);
        });

    [Fact]
    public Task Ctrl_Shift_K_in_an_empty_text_makes_no_step() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|", CodeLanguage.PlainText);

            window.Press(Keys.K, EditorWindow.Command | KeyModifiers.Shift);

            DocumentOf(window).CanUndo.ShouldBeFalse();
        });

    [Theory]
    // VS Code's tests: above the first line the caret stays at the start, on the new line.
    [InlineData("Fi|rst line\nSecond line\nThird line", true, "|\nFirst line\nSecond line\nThird line")]
    [InlineData("First line\nSe|cond line\nThird line", true, "First line\n|\nSecond line\nThird line")]
    [InlineData("Fi|rst line\nSecond line\nThird line", false, "First line\n|\nSecond line\nThird line")]
    [InlineData("First line\nSecond line\nTh|ird line", false, "First line\nSecond line\nThird line\n|")]
    [InlineData("«First» line\nSecond line", false, "First line\n|\nSecond line")]
    public Task Ctrl_Enter_and_Ctrl_Shift_Enter_insert_a_line_below_or_above_the_caret_and_put_the_caret_on_it(
        string marked,
        bool above,
        string expected
    ) =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show(marked, CodeLanguage.PlainText);

            window.Press(Keys.Enter, EditorWindow.Command | (above ? KeyModifiers.Shift : KeyModifiers.None));

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Theory]
    [InlineData("{|\n}", false, "{\n    |\n}")]
    [InlineData("{\n    \"a\": |1\n}", true, "{\n    |\n    \"a\": 1\n}")]
    [InlineData("{\n    \"a\": |1\n}", false, "{\n    \"a\": 1\n    |\n}")]
    public Task An_inserted_line_is_indented_as_Enter_at_the_end_of_the_line_above_indents(
        string marked,
        bool above,
        string expected
    ) =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = MarkedText.Show(marked, CodeLanguage.Json);
            DocumentOf(window).Indentation = Indentation.Spaces(4);

            window.Press(Keys.Enter, EditorWindow.Command | (above ? KeyModifiers.Shift : KeyModifiers.None));

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Theory]
    // VS Code's tests: the line indents without a selection too, and the caret stays at its text.
    [InlineData("f|unction baz() {", false, true, "\tf|unction baz() {")]
    [InlineData("|Some text", false, true, "\t|Some text")]
    [InlineData("    a|b", true, false, "a|b")]
    [InlineData("«a\nb»", false, false, "«    a\n    b»")]
    public Task Ctrl_close_and_open_bracket_indent_and_outdent_the_lines(
        string marked,
        bool outdent,
        bool tabs,
        string expected
    ) =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show(marked, CodeLanguage.PlainText);
            DocumentOf(window).Indentation = tabs ? Indentation.Tabs(4) : Indentation.Spaces(4);

            window.Press(outdent ? Keys.OemOpenBrackets : Keys.OemCloseBrackets, EditorWindow.Command);

            MarkedText.Of(window).ShouldBe(expected);
            DocumentOf(window).Undo();
            MarkedText.Of(window).ShouldBe(marked);
        });

    /// <summary>As a command that does not apply, it leaves its key to the binding before it.</summary>
    [Fact]
    public Task Outdenting_a_line_without_indentation_lets_its_key_go_on() =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = MarkedText.Show("a|b", CodeLanguage.Json);
            string command = OperatingSystem.IsMacOS() ? "cmd" : "ctrl";
            window.Editor.Keymap = new CodeEditorKeymap([
                new CodeEditorKeyBinding($"{command}+[", CodeEditorCommands.CommentLine),
                .. CodeEditorKeymap.Default.Bindings,
            ]);

            window.Press(Keys.OemOpenBrackets, EditorWindow.Command);

            DocumentOf(window).Text.ShouldBe("// ab");
        });

    [Fact]
    public Task A_read_only_editor_deletes_inserts_and_indents_no_lines() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("a\nb|\nc", CodeLanguage.PlainText);
            window.Editor.IsReadOnly = true;

            window.Press(Keys.K, EditorWindow.Command | KeyModifiers.Shift);
            window.Press(Keys.Enter, EditorWindow.Command);
            window.Press(Keys.OemCloseBrackets, EditorWindow.Command);

            MarkedText.Of(window).ShouldBe("a\nb|\nc");
        });

    /// <summary>Linux takes Ctrl+Shift+Alt to copy lines.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void By_default_the_line_commands_have_the_keys_of_each_platform(bool mac, bool linux)
    {
        var keymap = new CodeEditorKeymap(CodeEditorKeymap.LineBindings(mac, linux));
        KeyModifiers command = mac ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyModifiers copy = KeyModifiers.Shift | KeyModifiers.Alt | (linux ? KeyModifiers.Control : KeyModifiers.None);

        Commands(keymap, Keys.Up, KeyModifiers.Alt).ShouldBe([CodeEditorCommands.MoveLinesUp]);
        Commands(keymap, Keys.Down, KeyModifiers.Alt).ShouldBe([CodeEditorCommands.MoveLinesDown]);
        Commands(keymap, Keys.Up, copy).ShouldBe([CodeEditorCommands.CopyLinesUp]);
        Commands(keymap, Keys.Down, copy).ShouldBe([CodeEditorCommands.CopyLinesDown]);
        Commands(keymap, Keys.K, command | KeyModifiers.Shift).ShouldBe([CodeEditorCommands.DeleteLines]);
        Commands(keymap, Keys.Enter, command).ShouldBe([CodeEditorCommands.InsertLineAfter]);
        Commands(keymap, Keys.Enter, command | KeyModifiers.Shift).ShouldBe([CodeEditorCommands.InsertLineBefore]);
        Commands(keymap, Keys.OemCloseBrackets, command).ShouldBe([CodeEditorCommands.IndentLines]);
        Commands(keymap, Keys.OemOpenBrackets, command).ShouldBe([CodeEditorCommands.OutdentLines]);
    }

    internal static IReadOnlyList<string> Commands(CodeEditorKeymap keymap, Keys key, KeyModifiers modifiers) =>
        KeyResolution.Of(keymap, null, KeymapTests.Key(key, modifiers)).Commands;

    private static CodeDocument DocumentOf(EditorWindow window) =>
        window.Editor.Document ?? throw new InvalidOperationException("No document.");
}

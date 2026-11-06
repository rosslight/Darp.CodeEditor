using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Editing;

namespace Darp.CodeEditor.Tests;

/// <summary>Auto-closing and surrounding pairs, as VS Code types them, and how the user undoes them.</summary>
[Collection(UiFixture.Collection)]
public sealed class PairTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task An_opening_bracket_closes_and_typing_its_closing_bracket_steps_over_it() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("{\n    \"a\": |\n}", CodeLanguage.Json);

            window.Type("[");
            MarkedText.Of(window).ShouldBe("{\n    \"a\": [|]\n}");
            window.Type("1]");
            MarkedText.Of(window).ShouldBe("{\n    \"a\": [1]|\n}");
            // The bracket the user typed is no closing bracket of the editor's.
            window.Type("]");
            MarkedText.Of(window).ShouldBe("{\n    \"a\": [1]]|\n}");
        });

    [Fact]
    public Task Undo_removes_the_closing_character_with_the_opening_one() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("{\n    \"a\": |\n}", CodeLanguage.Json);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();

            window.Type("\"");
            document.Text.ShouldBe("{\n    \"a\": \"\"\n}");
            document.Undo();

            document.Text.ShouldBe("{\n    \"a\": \n}");
        });

    [Fact]
    public Task Backspace_deletes_an_empty_pair_only_when_the_editor_closed_it() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("{\n    \"a\": |,\n    \"b\": ()\n}", CodeLanguage.Json);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();

            window.Type("[");
            window.Press(Key.Back);
            document.Text.ShouldBe("{\n    \"a\": ,\n    \"b\": ()\n}");
            document.Undo();
            document.Text.ShouldBe("{\n    \"a\": [],\n    \"b\": ()\n}");

            // Typed by the user: Backspace deletes one character, as anywhere else.
            window.Editor.Selection = new TextSelection(document.CurrentSnapshot.GetOffset(new TextPosition(3, 11)));
            window.Press(Key.Back);
            document.Text.ShouldBe("{\n    \"a\": [],\n    \"b\": )\n}");
        });

    /// <summary>
    /// Where VS Code's rules close a pair, and where not: before a word, a quote after a word, and pairs that the
    /// language does not close in strings or comments, by the tokens of the line, also of the line before.
    /// </summary>
    [Theory]
    [InlineData("[1, |]", "(", "[1, (|)]")]
    [InlineData("[1, |foo]", "(", "[1, (|foo]")]
    [InlineData("[1, don|]", "'", "[1, don'|]")]
    [InlineData("[1, \"a | b\"]", "(", "[1, \"a (| b\"]")]
    [InlineData("[1, // note |\n]", "\"", "[1, // note \"|\n]")]
    [InlineData("[1, // note |\n]", "'", "[1, // note '|'\n]")]
    [InlineData("[1, /* note\n|\n*/ ]", "\"", "[1, /* note\n\"|\n*/ ]")]
    [InlineData("[1, // note\n|\n]", "\"", "[1, // note\n\"|\"\n]")]
    public Task A_pair_closes_where_VS_Code_closes_it(string marked, string typed, string expected) =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored(marked, CodeLanguage.Json);

            window.Type(typed);

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Fact]
    public Task A_pair_of_several_characters_closes_when_its_last_character_is_typed() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("<a b=\"c\">\n|\n</a>", CodeLanguage.Xml);

            window.Type("<!--");

            MarkedText.Of(window).ShouldBe("<a b=\"c\">\n<!--|-->\n</a>");
        });

    [Fact]
    public Task An_opening_character_surrounds_the_selection_in_one_undo_step() =>
        _ui.Run(async () =>
        {
            // JSON has no surrounding pairs of its own: its auto-closing pairs surround, as in VS Code.
            using EditorWindow window = await ShowColored("{\n    \"a\": «value»\n}", CodeLanguage.Json);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();

            window.Type("\"");
            MarkedText.Of(window).ShouldBe("{\n    \"a\": \"«value»\"\n}");

            document.Undo();
            document.Text.ShouldBe("{\n    \"a\": value\n}");
        });

    [Fact]
    public Task In_a_rectangle_selection_pairs_close_step_over_and_delete_on_every_line() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("[1,\n 2,|\n]", CodeLanguage.Json);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            TextArea textArea = EditorWindow.PartOf(window.Editor).TextArea;
            // Empty, after the commas of the first two lines.
            textArea.Selection = new RectangleSelection(
                textArea,
                new TextViewPosition(1, 4),
                new TextViewPosition(2, 4)
            );

            window.Type("[");
            document.Text.ShouldBe("[1,[]\n 2,[]\n]");
            window.Type("3]");
            document.Text.ShouldBe("[1,[3]\n 2,[3]\n]");
            window.Type("[");
            window.Press(Key.Back);
            document.Text.ShouldBe("[1,[3]\n 2,[3]\n]");

            // Each was one step on both lines: Backspace, the second pair, the 3, and the first pair.
            document.Undo();
            document.Text.ShouldBe("[1,[3][]\n 2,[3][]\n]");
            document.Undo();
            document.Undo();
            document.Text.ShouldBe("[1,[]\n 2,[]\n]");
            document.Undo();
            document.Text.ShouldBe("[1,\n 2,\n]");
        });

    [Fact]
    public Task Enter_in_a_rectangle_selection_breaks_the_carets_line_only() =>
        _ui.Run(async () =>
        {
            // AvaloniaEdit has no carets on several lines: the rectangle ends.
            using EditorWindow window = await ShowColored("[1,\n 2,|\n]", CodeLanguage.Json);
            TextArea textArea = EditorWindow.PartOf(window.Editor).TextArea;
            textArea.Selection = new RectangleSelection(
                textArea,
                new TextViewPosition(1, 4),
                new TextViewPosition(2, 4)
            );
            textArea.Caret.Position = new TextViewPosition(2, 4);

            window.Press(Key.Enter);

            MarkedText.Of(window).ShouldBe("[1,\n 2,\n |\n]");
            textArea.Selection.ShouldNotBeOfType<RectangleSelection>();
            textArea.Selection.IsEmpty.ShouldBeTrue();
        });

    [Fact]
    public Task In_a_rectangle_selection_an_opening_character_surrounds_every_line() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("[1, \"a\",\n 2, \"b\"|\n]", CodeLanguage.Json);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            TextArea textArea = EditorWindow.PartOf(window.Editor).TextArea;
            // The two strings.
            textArea.Selection = new RectangleSelection(
                textArea,
                new TextViewPosition(1, 5),
                new TextViewPosition(2, 8)
            );

            window.Type("[");

            document.Text.ShouldBe("[1, [\"a\"],\n 2, [\"b\"]\n]");
            document.Undo();
            document.Text.ShouldBe("[1, \"a\",\n 2, \"b\"\n]");
        });

    [Fact]
    public Task The_helpers_leave_a_read_only_editor_alone() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("{\n    \"a\": «[]»\n}", CodeLanguage.Json);
            window.Editor.IsReadOnly = true;

            window.Type("(");
            window.Press(Key.Enter);
            window.Press(Key.Oem2, EditorWindow.Command);
            window.Editor.Selection = new TextSelection(12);
            window.Press(Key.Back);

            window.Editor.Document.ShouldNotBeNull().Text.ShouldBe("{\n    \"a\": []\n}");
        });

    /// <summary>
    /// Shows the marked text once the grammar colors it, with more than one color: pairs that are not closed in
    /// strings or comments wait for the tokens.
    /// </summary>
    private static async Task<EditorWindow> ShowColored(string marked, CodeLanguage language)
    {
        EditorWindow window = MarkedText.Show(marked, language);
        await Wait.UntilAsync(() =>
        {
            window.Render();
            return window.RenderedColors().Count > 1;
        });
        return window;
    }
}

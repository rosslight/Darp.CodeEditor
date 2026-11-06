using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;

namespace Darp.CodeEditor.Tests;

/// <summary>Indentation in the editor: the document's setting, Tab and Shift+Tab, and new lines.</summary>
[Collection(UiFixture.Collection)]
public sealed class IndentationTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task Every_editor_of_a_document_indents_by_its_indentation_and_follows_a_change_at_once() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json, "ab\n\tx");
            var first = new CodeEditor { Document = document };
            var second = new CodeEditor { Document = document };
            using var window = new EditorWindow(first, new StackPanel { Children = { first, second } });
            first.Selection = new TextSelection(2);

            // Spaces to the next stop of 4.
            window.Press(Key.Tab);
            document.Text.ShouldBe("ab  \n\tx");
            TabWidth(second).ShouldBe(4);

            document.Indentation = Indentation.Tabs(8);
            window.Layout();
            second.Focus();
            second.Selection = new TextSelection(4);
            window.Press(Key.Tab);

            document.Text.ShouldBe("ab  \t\n\tx");
            TabWidth(first).ShouldBe(8);
            TabWidth(second).ShouldBe(8);
        });

    [Fact]
    public Task Tab_and_Shift_Tab_indent_and_outdent_the_selected_lines() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("«a\n  b»\nc", CodeLanguage.Json);

            window.Press(Key.Tab);
            MarkedText.Of(window).ShouldBe("«    a\n      b»\nc");

            window.Press(Key.Tab, KeyModifiers.Shift);
            window.Press(Key.Tab, KeyModifiers.Shift);
            MarkedText.Of(window).ShouldBe("«a\nb»\nc");
        });

    [Theory]
    [InlineData("{\n    \"a\": 1,|\n}", "{\n    \"a\": 1,\n    |\n}")]
    [InlineData("{\n    \"a\": [|\n}", "{\n    \"a\": [\n        |\n}")]
    [InlineData("{\n  \"a\": [|\n}", "{\n  \"a\": [\n    |\n}")]
    [InlineData("{\n    \"a\": {|}\n}", "{\n    \"a\": {\n        |\n    }\n}")]
    [InlineData("{\r\n    \"a\": {|}\r\n}", "{\r\n    \"a\": {\r\n        |\r\n    }\r\n}")]
    public Task Enter_keeps_the_indentation_and_indents_after_and_between_brackets(string marked, string expected) =>
        _ui.Run(async () =>
        {
            // One level more goes to the next stop of the document's indentation; line breaks stay the document's.
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = MarkedText.Show(marked, CodeLanguage.Json);

            window.Press(Key.Enter);

            MarkedText.Of(window).ShouldBe(expected);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            document.Undo();
            document.Text.ShouldBe(MarkedText.Parse(marked).Text);
        });

    [Theory]
    [InlineData("key:|\n", "yaml", "key:\n  |\n")]
    [InlineData("  - item|\n", "yaml", "  - item\n  |\n")]
    [InlineData("BEGIN|\n", "pascal", "BEGIN\n    |\n")]
    [InlineData("begin\n    x|", "pascal", "begin\n    x\n    |")]
    public Task Enter_indents_one_level_more_after_a_line_that_matches_the_increase_pattern(
        string marked,
        string language,
        string expected
    ) =>
        _ui.Run(async () =>
        {
            CodeLanguage codeLanguage = language == "yaml" ? CodeLanguage.Yaml : Pascal;
            await codeLanguage.Loaded;
            using EditorWindow window = MarkedText.Show(marked, codeLanguage);

            window.Press(Key.Enter);

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Fact]
    public Task Typing_a_line_that_matches_the_decrease_pattern_outdents_it_in_the_same_undo_step() =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = MarkedText.Show("{\n    \"a\": 1,\n    |\n}", CodeLanguage.Json);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();

            window.Type("}");
            MarkedText.Of(window).ShouldBe("{\n    \"a\": 1,\n}|\n}");

            document.Undo();
            document.Text.ShouldBe("{\n    \"a\": 1,\n    \n}");
        });

    [Fact]
    public Task The_decrease_pattern_applies_with_its_flags() =>
        _ui.Run(async () =>
        {
            await Pascal.Loaded;
            using EditorWindow window = MarkedText.Show("begin\n    x\n    En|", Pascal);

            window.Type("d");

            MarkedText.Of(window).ShouldBe("begin\n    x\nEnd|");
        });

    /// <summary>A language with word brackets, and indentation patterns that ignore case.</summary>
    private static CodeLanguage Pascal { get; } =
        new(
            "pascal",
            configuration: new Uri("avares://Darp.CodeEditor.Tests/Languages/pascal.language-configuration.json")
        );

    /// <summary>How many spaces wide the editor draws the tab at the start of the document's second line.</summary>
    private static double TabWidth(CodeEditor editor)
    {
        TextView textView = EditorWindow.TextViewOf(editor);
        textView.EnsureVisualLines();
        double x = textView.GetVisualPosition(new TextViewPosition(2, 2), VisualYPosition.LineTop).X;
        return Math.Round(x / textView.WideSpaceWidth, 2);
    }
}

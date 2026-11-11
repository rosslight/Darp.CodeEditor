using Avalonia;
using Avalonia.Input;

namespace Darp.CodeEditor.Tests;

/// <summary>Double-click and deleting by words in the editor, by <see cref="WordRulesTests"/>'s rules, instead of AvaloniaEdit's.</summary>
[Collection(UiFixture.Collection)]
public sealed class WordEditingTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task A_double_click_selects_VS_Codes_word_also_past_the_lines_end() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|a == b.c\nnext", CodeLanguage.Json);

            // Just after the left edge of the second '=': inside the run.
            DoubleClick(window, window.PointAt(3) - new Point(window.TextView.WideSpaceWidth / 2 - 1, 0));
            MarkedText.Of(window).ShouldBe("a «==» b.c\nnext");

            DoubleClick(window, window.PointAt(8) + new Point(40, 0));
            MarkedText.Of(window).ShouldBe("a == b.«c»\nnext");
        });

    [Fact]
    public Task Ctrl_Backspace_deletes_a_lines_whitespace_before_joining_it_to_the_line_before() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("a\n    |", CodeLanguage.Json);

            window.Press(Key.Back, KeyModifiers.Control);
            MarkedText.Of(window).ShouldBe("a\n|");
            window.Press(Key.Back, KeyModifiers.Control);
            MarkedText.Of(window).ShouldBe("a|");
            window.Press(Key.Delete, KeyModifiers.Control);
            MarkedText.Of(window).ShouldBe("a|");
        });

    [Fact]
    public Task Ctrl_D_selects_the_word_at_the_caret_and_deletes_no_line() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("alpha be|ta\ngamma", CodeLanguage.PlainText);

            window.Press(Key.D, EditorWindow.Command);
            MarkedText.Of(window).ShouldBe("alpha «beta»\ngamma");

            // Without carets on several lines, there is no next match to add a caret at.
            window.Press(Key.D, EditorWindow.Command);
            MarkedText.Of(window).ShouldBe("alpha «beta»\ngamma");
        });

    [Theory]
    [InlineData("alpha | beta")]
    [InlineData("a |== b")]
    public Task Ctrl_D_away_from_a_word_changes_nothing(string marked) =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show(marked, CodeLanguage.PlainText);

            window.Press(Key.D, EditorWindow.Command);

            MarkedText.Of(window).ShouldBe(marked);
        });

    [Fact]
    public Task Without_its_binding_Ctrl_D_changes_nothing() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("alpha be|ta\ngamma", CodeLanguage.PlainText);
            window.Editor.Keymap = CodeEditorKeymap.Empty;

            window.Press(Key.D, EditorWindow.Command);

            MarkedText.Of(window).ShouldBe("alpha be|ta\ngamma");
        });

    private static void DoubleClick(EditorWindow window, Point point)
    {
        window.Click(point, MouseButton.Left);
        window.Click(point, MouseButton.Left);
    }
}

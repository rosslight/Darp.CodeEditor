using Avalonia.Input;

namespace Darp.CodeEditor.Tests;

/// <summary>The arrow keys with a selection, as VS Code moves the caret, instead of AvaloniaEdit.</summary>
[Collection(UiFixture.Collection)]
public sealed class CaretMovementTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    private const string Lines = "|abcdef\nabcdef\nabcdef\nabcdef";

    [Theory]
    [InlineData(9, 18)]
    [InlineData(18, 9)]
    public Task Left_and_Right_put_the_caret_at_the_selections_start_or_end_and_go_no_further(int anchor, int caret) =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show(Lines, CodeLanguage.PlainText);

            window.Editor.Selection = new TextSelection(anchor, caret);
            window.Press(Key.Left);
            MarkedText.Of(window).ShouldBe("abcdef\nab|cdef\nabcdef\nabcdef");

            window.Editor.Selection = new TextSelection(anchor, caret);
            window.Press(Key.Right);
            MarkedText.Of(window).ShouldBe("abcdef\nabcdef\nabcd|ef\nabcdef");
        });

    [Theory]
    [InlineData(9, 18)]
    [InlineData(18, 9)]
    public Task Up_and_Down_go_one_line_from_the_selections_start_or_end_in_its_column(int anchor, int caret) =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show(Lines, CodeLanguage.PlainText);

            window.Editor.Selection = new TextSelection(anchor, caret);
            window.Press(Key.Up);
            MarkedText.Of(window).ShouldBe("ab|cdef\nabcdef\nabcdef\nabcdef");

            window.Editor.Selection = new TextSelection(anchor, caret);
            window.Press(Key.Down);
            MarkedText.Of(window).ShouldBe("abcdef\nabcdef\nabcdef\nabcd|ef");
        });

    [Fact]
    public Task With_Shift_the_arrow_keys_extend_the_selection_from_the_caret() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show(Lines, CodeLanguage.PlainText);
            window.Editor.Selection = new TextSelection(9, 18);

            window.Press(Key.Left, KeyModifiers.Shift);
            window.Editor.Selection.ShouldBe(new TextSelection(9, 17));
            window.Press(Key.Up, KeyModifiers.Shift);
            window.Editor.Selection.ShouldBe(new TextSelection(9, 10));
            window.Press(Key.Right, KeyModifiers.Shift);
            window.Editor.Selection.ShouldBe(new TextSelection(9, 11));
            window.Press(Key.Down, KeyModifiers.Shift);
            window.Editor.Selection.ShouldBe(new TextSelection(9, 18));
        });

    [Fact]
    public Task Without_a_selection_the_arrow_keys_move_the_caret_by_one() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("abcdef\nab|cdef\nabcdef", CodeLanguage.PlainText);

            window.Press(Key.Left);
            MarkedText.Of(window).ShouldBe("abcdef\na|bcdef\nabcdef");
            window.Press(Key.Right);
            window.Press(Key.Right);
            MarkedText.Of(window).ShouldBe("abcdef\nabc|def\nabcdef");
            window.Press(Key.Up);
            MarkedText.Of(window).ShouldBe("abc|def\nabcdef\nabcdef");
            window.Press(Key.Down);
            window.Press(Key.Down);
            MarkedText.Of(window).ShouldBe("abcdef\nabcdef\nabc|def");
        });

    [Fact]
    public Task Up_on_the_first_line_and_Down_on_the_last_go_to_the_documents_start_and_end() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("ab|cdef\nabcdef\nabcdef", CodeLanguage.PlainText);

            window.Press(Key.Up);
            MarkedText.Of(window).ShouldBe("|abcdef\nabcdef\nabcdef");

            window.Editor.Selection = new TextSelection(16);
            window.Press(Key.Down);
            MarkedText.Of(window).ShouldBe("abcdef\nabcdef\nabcdef|");
        });

    [Fact]
    public Task After_going_to_the_documents_start_Down_goes_back_to_the_column_the_caret_came_from() =>
        _ui.Run(() =>
        {
            // VS Code keeps the columns that the move left over, as for a short line.
            using EditorWindow window = MarkedText.Show("abc|def\nabcdef\nabcdef", CodeLanguage.PlainText);

            window.Press(Key.Up);
            window.Press(Key.Down);
            MarkedText.Of(window).ShouldBe("abcdef\nabc|def\nabcdef");

            window.Editor.Selection = new TextSelection(17);
            window.Press(Key.Down);
            window.Press(Key.Up);
            MarkedText.Of(window).ShouldBe("abcdef\nabc|def\nabcdef");
        });

    [Fact]
    public Task With_Shift_Up_and_Down_select_to_the_documents_start_and_end() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("ab|cdef\nabcdef", CodeLanguage.PlainText);

            window.Press(Key.Up, KeyModifiers.Shift);
            window.Editor.Selection.ShouldBe(new TextSelection(2, 0));

            window.Editor.Selection = new TextSelection(9);
            window.Press(Key.Down, KeyModifiers.Shift);
            window.Editor.Selection.ShouldBe(new TextSelection(9, 13));
        });

    [Fact]
    public Task Up_with_a_selection_on_the_first_line_goes_to_the_documents_start() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("a«bc»def\nabcdef", CodeLanguage.PlainText);

            window.Press(Key.Up);
            MarkedText.Of(window).ShouldBe("|abcdef\nabcdef");

            window.Editor.Selection = new TextSelection(9, 11);
            window.Press(Key.Down);
            MarkedText.Of(window).ShouldBe("abcdef\nabcdef|");
        });

    [Fact]
    public Task Up_in_a_wrapped_first_line_goes_up_a_row_before_it_goes_to_the_start() =>
        _ui.Run(() =>
        {
            string line = string.Join(' ', Enumerable.Repeat("word", 60));
            using EditorWindow window = MarkedText.Show(line + "|", CodeLanguage.PlainText);
            window.Editor.WordWrap = true;
            window.Layout();

            window.Press(Key.Up);
            window.Editor.Selection.Caret.ShouldBeInRange(1, line.Length - 1);

            window.Editor.Selection = new TextSelection(5);
            window.Press(Key.Down);
            window.Editor.Selection.Caret.ShouldBeInRange(6, line.Length - 1);
        });

    [Theory]
    [InlineData("  |  \nabc", "|    \nabc")]
    [InlineData("    |\nabc", "|    \nabc")]
    [InlineData("|    \nabc", "|    \nabc")]
    [InlineData("  ab|c", "  |abc")]
    [InlineData("  |abc", "|  abc")]
    [InlineData("a|bc", "|abc")]
    public Task Home_goes_to_the_first_character_and_then_to_the_lines_start_and_on_a_blank_line_to_its_start(
        string marked,
        string expected
    ) =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show(marked, CodeLanguage.PlainText);

            window.Press(Key.Home);

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Fact]
    public Task Shift_Home_on_a_blank_line_selects_to_its_start() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("abc\n  |  ", CodeLanguage.PlainText);

            window.Press(Key.Home, KeyModifiers.Shift);

            window.Editor.Selection.ShouldBe(new TextSelection(6, 4));
        });
}

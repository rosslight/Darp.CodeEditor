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
}

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

    private static void DoubleClick(EditorWindow window, Point point)
    {
        window.Click(point, MouseButton.Left);
        window.Click(point, MouseButton.Left);
    }
}

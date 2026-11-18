using Avalonia.Input;

namespace Darp.CodeEditor.Tests;

/// <summary>Ctrl+L selects whole lines, as VS Code's <c>expandLineSelection</c> does.</summary>
[Collection(UiFixture.Collection)]
public sealed class LineSelectionTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Theory]
    [InlineData("a\nb|c\nd", "a\n«bc\n»d")]
    [InlineData("a\nb|c", "a\n«bc»")]
    [InlineData("a«\nb»c\nd", "«a\nbc\n»d")]
    [InlineData("«a\n»b\nc", "«a\nb\n»c")]
    public Task Ctrl_L_selects_the_lines_whole_up_to_the_start_of_the_next_and_again_takes_the_next_line(
        string marked,
        string expected
    ) =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show(marked, CodeLanguage.PlainText);

            window.Press(Key.L, EditorWindow.Command);

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Fact]
    public Task Ctrl_L_selects_in_a_read_only_editor_too() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("a|\nb", CodeLanguage.PlainText);
            window.Editor.IsReadOnly = true;

            window.Press(Key.L, EditorWindow.Command);
            window.Press(Key.L, EditorWindow.Command);

            MarkedText.Of(window).ShouldBe("«a\nb»");
        });
}

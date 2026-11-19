using Avalonia.Input;

namespace Darp.CodeEditor.Tests;

/// <summary>Ctrl+U puts back the selections that the caret left, as VS Code's cursor undo does.</summary>
[Collection(UiFixture.Collection)]
public sealed class CursorUndoTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task Ctrl_U_puts_back_the_selections_that_the_caret_left_and_cursor_redo_the_ones_it_took_back() =>
        _ui.Run(() =>
        {
            using EditorWindow window = Show("|abc\ndef");

            window.Press(Key.Right, KeyModifiers.Shift);
            window.Press(Key.Down);
            window.Press(Key.End);
            MarkedText.Of(window).ShouldBe("abc\ndef|");

            window.Press(Key.U, EditorWindow.Command);
            MarkedText.Of(window).ShouldBe("abc\nd|ef");
            window.Press(Key.U, EditorWindow.Command);
            MarkedText.Of(window).ShouldBe("«a»bc\ndef");
            window.Press(Key.U, EditorWindow.Command);
            MarkedText.Of(window).ShouldBe("|abc\ndef");
            window.Press(Key.U, EditorWindow.Command);
            MarkedText.Of(window).ShouldBe("|abc\ndef");

            window.Press(Key.R, KeyModifiers.Control | KeyModifiers.Alt);
            MarkedText.Of(window).ShouldBe("«a»bc\ndef");
            window.Press(Key.R, KeyModifiers.Control | KeyModifiers.Alt);
            MarkedText.Of(window).ShouldBe("abc\nd|ef");
        });

    [Fact]
    public Task A_change_of_the_text_forgets_the_selections() =>
        _ui.Run(() =>
        {
            using EditorWindow window = Show("|abc");

            window.Press(Key.Right);
            window.Type("x");
            window.Press(Key.U, EditorWindow.Command);

            MarkedText.Of(window).ShouldBe("ax|bc");
        });

    [Fact]
    public Task Cursor_undo_keeps_the_last_50_selections() =>
        _ui.Run(() =>
        {
            using EditorWindow window = Show("|" + new string('a', 60));

            for (int i = 0; i < 60; i++)
                window.Press(Key.Right);
            for (int i = 0; i < 60; i++)
                window.Press(Key.U, EditorWindow.Command);

            window.Editor.Selection.ShouldBe(new TextSelection(10));
        });

    private static EditorWindow Show(string marked)
    {
        EditorWindow window = MarkedText.Show(marked, CodeLanguage.PlainText);
        window.Editor.Keymap = CodeEditorKeymap.Default.With(
            new CodeEditorKeyBinding("ctrl+alt+r", CodeEditorCommands.CursorRedo)
        );
        return window;
    }
}

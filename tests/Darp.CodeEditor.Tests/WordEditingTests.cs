using Avalonia;
using Avalonia.Input;
using Darp.CodeEditor.Commands;
using Keys = Avalonia.Input.Key;

namespace Darp.CodeEditor.Tests;

/// <summary>Double-click, moving and deleting by words, and Ctrl+D, by the words of <see cref="WordRulesTests"/>.</summary>
[Collection(UiFixture.Collection)]
public sealed class WordEditingTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    /// <summary>The platform's key for words: Ctrl, or Alt on macOS.</summary>
    private static KeyModifiers Words => OperatingSystem.IsMacOS() ? KeyModifiers.Alt : KeyModifiers.Control;

    [Fact]
    public Task A_double_click_selects_the_word_also_past_the_lines_end() =>
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

            window.Press(Keys.Back, Words);
            MarkedText.Of(window).ShouldBe("a\n|");
            window.Press(Keys.Back, Words);
            MarkedText.Of(window).ShouldBe("a|");
            window.Press(Keys.Delete, Words);
            MarkedText.Of(window).ShouldBe("a|");
        });

    /// <summary>As Backspace deletes it.</summary>
    [Fact]
    public Task Ctrl_Backspace_deletes_an_empty_pair_that_the_editor_closed_as_a_whole() =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = MarkedText.Show("a |", CodeLanguage.Json);
            window.Type("[");
            MarkedText.Of(window).ShouldBe("a [|]");

            window.Press(Keys.Back, Words);

            MarkedText.Of(window).ShouldBe("a |");
        });

    [Fact]
    public Task Ctrl_D_selects_the_word_at_the_caret_and_deletes_no_line() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("alpha be|ta\ngamma", CodeLanguage.PlainText);

            window.Press(Keys.D, EditorWindow.Command);
            MarkedText.Of(window).ShouldBe("alpha «beta»\ngamma");

            // With one caret, there is no next match to add a caret at.
            window.Press(Keys.D, EditorWindow.Command);
            MarkedText.Of(window).ShouldBe("alpha «beta»\ngamma");
        });

    [Theory]
    [InlineData("alpha | beta")]
    [InlineData("a |== b")]
    public Task Ctrl_D_away_from_a_word_changes_nothing(string marked) =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show(marked, CodeLanguage.PlainText);

            window.Press(Keys.D, EditorWindow.Command);

            MarkedText.Of(window).ShouldBe(marked);
        });

    [Fact]
    public Task Without_its_binding_Ctrl_D_changes_nothing() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("alpha be|ta\ngamma", CodeLanguage.PlainText);
            window.Editor.Keymap = CodeEditorKeymap.Empty;

            window.Press(Keys.D, EditorWindow.Command);

            MarkedText.Of(window).ShouldBe("alpha be|ta\ngamma");
        });

    [Fact]
    public Task Ctrl_Right_and_Ctrl_Left_move_by_words_and_with_Shift_select() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|alpha beta", CodeLanguage.PlainText);

            window.Press(Keys.Right, Words);
            MarkedText.Of(window).ShouldBe("alpha| beta");
            window.Press(Keys.Right, Words | KeyModifiers.Shift);
            window.Editor.Selection.ShouldBe(new TextSelection(5, 10));
            window.Press(Keys.Left, Words);
            MarkedText.Of(window).ShouldBe("alpha |beta");
            window.Press(Keys.Left, Words | KeyModifiers.Shift);
            window.Editor.Selection.ShouldBe(new TextSelection(6, 0));
        });

    [Fact]
    public Task Ctrl_Shift_Right_selects_a_word_without_the_space_after_it() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|alpha beta", CodeLanguage.PlainText);

            window.Press(Keys.Right, Words | KeyModifiers.Shift);

            MarkedText.Of(window).ShouldBe("«alpha» beta");
        });

    [Fact]
    public void By_default_Ctrl_D_selects_the_word_at_the_caret()
    {
        Commands(Keys.D, EditorWindow.Command).ShouldBe([CodeEditorCommands.AddSelectionToNextFindMatch]);
    }

    [Fact]
    public void By_default_Ctrl_Left_and_Ctrl_Right_move_by_words_and_with_Shift_select()
    {
        Commands(Keys.Left, Words).ShouldBe([CodeEditorCommands.CursorWordLeft]);
        Commands(Keys.Left, Words | KeyModifiers.Shift).ShouldBe([CodeEditorCommands.CursorWordLeftSelect]);
        Commands(Keys.Right, Words).ShouldBe([CodeEditorCommands.CursorWordEndRight]);
        Commands(Keys.Right, Words | KeyModifiers.Shift).ShouldBe([CodeEditorCommands.CursorWordEndRightSelect]);
    }

    [Fact]
    public void By_default_Ctrl_Backspace_and_Ctrl_Delete_delete_by_words()
    {
        Commands(Keys.Back, Words).ShouldBe([CodeEditorCommands.DeleteWordLeft]);
        Commands(Keys.Delete, Words).ShouldBe([CodeEditorCommands.DeleteWordRight]);
    }

    private static IReadOnlyList<string> Commands(Keys key, KeyModifiers modifiers) =>
        KeyResolution.Of(CodeEditorKeymap.Default, null, KeymapTests.Key(key, modifiers)).Commands;

    private static void DoubleClick(EditorWindow window, Point point)
    {
        window.Click(point, MouseButton.Left);
        window.Click(point, MouseButton.Left);
    }
}

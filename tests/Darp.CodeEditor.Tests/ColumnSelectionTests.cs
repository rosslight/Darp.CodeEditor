using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit.Editing;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Column selection, as VS Code's: by the <c>cursorColumnSelect</c> commands, on Ctrl+Shift+Alt with the arrows and the
/// page keys except on Linux, and by Shift+Alt+drag from the selection's anchor.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class ColumnSelectionTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    private const KeyModifiers Columns = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt;

    [Theory]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Home)]
    [InlineData(Key.End)]
    public Task Shift_Alt_with_the_arrows_Home_and_End_makes_no_rectangle_as_in_VS_Code(Key key) =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("ab\nc|d\nef", CodeLanguage.PlainText);

            window.Press(key, KeyModifiers.Shift | KeyModifiers.Alt);

            TextAreaOf(window).Selection.ShouldNotBeOfType<RectangleSelection>();
        });

    [Fact]
    public Task The_column_commands_move_the_corner_at_the_caret_by_columns_lines_and_up_to_the_longest_line() =>
        _ui.Run(() =>
        {
            using EditorWindow window = Show("ab\nabcd\nabc", 0);

            Press(window, Key.Right, 5);
            SelectedTexts(window).ShouldBe(["ab"]);
            Press(window, Key.Down, 1);
            Press(window, Key.Right, 5);
            SelectedTexts(window).ShouldBe(["ab", "abcd"]);
            Press(window, Key.Left, 1);
            Press(window, Key.Down, 5);
            SelectedTexts(window).ShouldBe(["ab", "abc", "abc"]);
            Press(window, Key.Up, 2);
            SelectedTexts(window).ShouldBe(["ab"]);
        });

    [Fact]
    public Task Column_selection_starts_at_the_selections_anchor_and_counts_a_tab_to_its_stop() =>
        _ui.Run(() =>
        {
            using EditorWindow window = Show("\tab\n12345678", 1);
            window.Editor.Document.ShouldNotBeNull().Indentation = Indentation.Tabs(4);

            // From after the tab, four columns in.
            Press(window, Key.Down, 1);
            Press(window, Key.Right, 1);
            SelectedTexts(window).ShouldBe(["a", "5"]);
        });

    [Fact]
    public Task Shift_Alt_drag_selects_columns_from_the_selections_anchor_to_the_pointer() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("a|bc\ndef\nghi", CodeLanguage.PlainText);
            const RawInputModifiers shiftAlt = RawInputModifiers.Shift | RawInputModifiers.Alt;

            window.Window.MouseDown(Before(window, 10), MouseButton.Left, shiftAlt);
            Dispatcher.UIThread.RunJobs();
            SelectedTexts(window).ShouldBe(["b", "e", "h"]);
            window.Window.MouseMove(Before(window, 7), shiftAlt | RawInputModifiers.LeftMouseButton);
            window.Window.MouseUp(Before(window, 7), MouseButton.Left, shiftAlt);
            Dispatcher.UIThread.RunJobs();

            SelectedTexts(window).ShouldBe(["bc", "ef"]);
        });

    [Fact]
    public void By_default_the_column_commands_have_VS_Codes_keys_and_none_on_Linux()
    {
        static IEnumerable<(string Keys, string Command)> Keys(bool mac, bool linux) =>
            CodeEditorKeymap.LineAndSelectionBindings(mac, linux).Select(binding => (binding.Keys, binding.Command));

        Keys(mac: false, linux: false)
            .ShouldContain(("ctrl+shift+alt+pagedown", CodeEditorCommands.CursorColumnSelectPageDown));
        Keys(mac: true, linux: false).ShouldContain(("cmd+shift+alt+left", CodeEditorCommands.CursorColumnSelectLeft));
        Keys(mac: false, linux: true).ShouldNotContain(binding => binding.Command.StartsWith("cursorColumnSelect"));
    }

    /// <summary>Where the window shows the start of the character at <paramref name="offset"/>.</summary>
    private static Point Before(EditorWindow window, int offset) =>
        window.PointAt(offset) - new Point(window.TextView.WideSpaceWidth / 2, 0);

    /// <summary>An editor of <paramref name="text"/> with the caret at <paramref name="caret"/>, and the column commands on Windows' keys.</summary>
    private static EditorWindow Show(string text, int caret)
    {
        EditorWindow window = MarkedText.Show(text.Insert(caret, "|"), CodeLanguage.PlainText);
        window.Editor.Keymap = CodeEditorKeymap.Default.With(
            new CodeEditorKeyBinding("ctrl+shift+alt+left", CodeEditorCommands.CursorColumnSelectLeft),
            new CodeEditorKeyBinding("ctrl+shift+alt+right", CodeEditorCommands.CursorColumnSelectRight),
            new CodeEditorKeyBinding("ctrl+shift+alt+up", CodeEditorCommands.CursorColumnSelectUp),
            new CodeEditorKeyBinding("ctrl+shift+alt+down", CodeEditorCommands.CursorColumnSelectDown)
        );
        return window;
    }

    private static void Press(EditorWindow window, Key key, int times)
    {
        for (int i = 0; i < times; i++)
            window.Press(key, Columns);
    }

    private static TextArea TextAreaOf(EditorWindow window) => EditorWindow.PartOf(window.Editor).TextArea;

    /// <summary>The text of each line of the rectangle selection.</summary>
    private static List<string> SelectedTexts(EditorWindow window)
    {
        TextArea textArea = TextAreaOf(window);
        textArea.Selection.ShouldBeOfType<RectangleSelection>();
        return [.. textArea.Selection.Segments.Select(segment => textArea.Document.GetText(segment))];
    }
}

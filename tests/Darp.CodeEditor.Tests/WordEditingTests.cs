using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
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

    /// <summary>The pointer stays put while the view scrolls: the drag's end goes to the word that comes under it.</summary>
    [Fact]
    public Task A_drag_after_a_double_click_follows_the_text_that_scrolls_under_the_pointer() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show(
                "|" + string.Join("\n", Enumerable.Range(1, 200).Select(n => $"line{n} x")),
                CodeLanguage.PlainText
            );
            Point point = window.PointAt(2);
            window.Click(point, MouseButton.Left);
            window.Window.MouseDown(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            window.Editor.Selection.ShouldBe(new TextSelection(0, 5));

            EditorWindow.PartOf(window.Editor).ScrollToVerticalOffset(40 * window.TextView.DefaultLineHeight);
            window.Layout();
            window.Layout();

            DocumentLine line41 = window.TextView.Document.GetLineByNumber(41);
            window.Editor.Selection.ShouldBe(new TextSelection(0, line41.Offset + "line41".Length));
            window.Window.MouseUp(point, MouseButton.Left);
        });

    /// <summary>Below the last line, the pointer is at the text's end, and the drag goes to the end of the last word.</summary>
    [Fact]
    public Task A_drag_after_a_double_click_goes_to_the_texts_end_where_the_last_line_scrolls_above_the_pointer() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show(
                "|" + string.Join("\n", Enumerable.Range(1, 200).Select(n => $"line{n} x")),
                CodeLanguage.PlainText
            );
            TextEditor part = EditorWindow.PartOf(window.Editor);
            double lineHeight = window.TextView.DefaultLineHeight;
            // Line 198 in the view's fifth row.
            part.ScrollToVerticalOffset(193 * lineHeight);
            window.Layout();
            DocumentLine line198 = window.TextView.Document.GetLineByNumber(198);
            Point point = window.PointAt(line198.Offset + 2);
            window.Click(point, MouseButton.Left);
            window.Window.MouseDown(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            window.Editor.Selection.ShouldBe(new TextSelection(line198.Offset, line198.Offset + "line198".Length));

            part.ScrollToVerticalOffset(199 * lineHeight);
            window.Layout();
            window.Layout();

            window.Editor.Selection.ShouldBe(new TextSelection(line198.Offset, window.TextView.Document.TextLength));
            window.Window.MouseUp(point, MouseButton.Left);
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
            await window.TokensKnownAsync();
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

    /// <summary>AvaloniaEdit's keys extend a rectangle by words.</summary>
    [Fact]
    public Task Ctrl_Shift_Right_leaves_a_rectangle_selection_a_rectangle() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|ab cd\nef gh", CodeLanguage.PlainText);
            TextArea textArea = EditorWindow.PartOf(window.Editor).TextArea;
            textArea.Caret.Position = new TextViewPosition(2, 2);
            textArea.Selection = new RectangleSelection(
                textArea,
                new TextViewPosition(1, 1),
                new TextViewPosition(2, 2)
            );

            window.Press(Keys.Right, Words | KeyModifiers.Shift);

            textArea.Selection.ShouldBeOfType<RectangleSelection>();
        });

    [Fact]
    public Task Ctrl_Delete_whose_change_switches_the_document_leaves_the_other_one_alone() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("«x»yz", CodeLanguage.PlainText);
            CodeDocument first = window.Editor.Document.ShouldNotBeNull();
            CodeDocument other = CodeDocument.Create(CodeLanguage.PlainText, "abc");
            first.TextChanged += (_, _) => window.Editor.Document = other;

            window.Press(Keys.Delete, Words);

            first.Text.ShouldBe("yz");
            other.Text.ShouldBe("abc");
        });

    /// <summary>As a command that does not apply, it leaves its key to the binding before it.</summary>
    [Fact]
    public Task Ctrl_Backspace_on_a_read_only_selection_lets_its_key_go_on() =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = MarkedText.Show("a«bc»d", CodeLanguage.Json);
            TextArea textArea = EditorWindow.PartOf(window.Editor).TextArea;
            var protectedText = new TextSegmentReadOnlySectionProvider<TextSegment>(textArea.Document);
            protectedText.Segments.Add(new TextSegment { StartOffset = 1, Length = 2 });
            textArea.ReadOnlySectionProvider = protectedText;
            string words = OperatingSystem.IsMacOS() ? "alt" : "ctrl";
            window.Editor.Keymap = new CodeEditorKeymap([
                new CodeEditorKeyBinding($"{words}+backspace", CodeEditorCommands.CommentLine),
                .. CodeEditorKeymap.Default.Bindings,
            ]);

            window.Press(Keys.Back, Words);

            window.Editor.Document.ShouldNotBeNull().Text.ShouldBe("// abcd");
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

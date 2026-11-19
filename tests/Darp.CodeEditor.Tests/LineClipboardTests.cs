using Avalonia.Input;
using Avalonia.Input.Platform;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The clipboard without a selection: copy and cut take the caret's whole line, which pastes as a whole line above the
/// caret's, as long as <see cref="CodeEditorOptions.EmptySelectionClipboard"/> is on.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class LineClipboardTests(UiFixture ui)
{
    /// <summary>
    /// The clipboard's line break from the documents here, whose line breaks are <c>\n</c> or none: <c>\r\n</c> where
    /// the system's is, and else theirs, the system's either way.
    /// </summary>
    private static readonly string s_lineBreak = NewLines.OfNewDocuments;

    private readonly UiFixture _ui = ui;

    /// <summary>By key, and by the editor's method, as an app's Edit menu and the context menu paste.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Copy_without_a_selection_copies_the_line_which_pastes_above_the_carets_line(bool byKey) =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("al|pha\nbeta", CodeLanguage.PlainText);

            window.Press(Key.C, EditorWindow.Command);
            (await ClipboardText(window)).ShouldBe("alpha" + s_lineBreak);

            window.Editor.Selection = new TextSelection(8);
            await PasteAsync(window, byKey);

            MarkedText.Of(window).ShouldBe("alpha\nalpha\nbe|ta");
        });

    [Fact]
    public Task Copy_on_an_empty_line_copies_a_line_break_which_pastes_as_an_empty_line() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("alpha\n|\nbeta", CodeLanguage.PlainText);

            window.Press(Key.C, EditorWindow.Command);
            (await ClipboardText(window)).ShouldBe(s_lineBreak);

            window.Editor.Selection = new TextSelection(9);
            await PasteAsync(window);

            MarkedText.Of(window).ShouldBe("alpha\n\n\nbe|ta");
        });

    [Fact]
    public Task Cut_without_a_selection_takes_the_line_with_its_line_break_in_one_undo_step() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("al|pha\nbeta", CodeLanguage.PlainText);
            CodeDocument document = DocumentOf(window);

            window.Press(Key.X, EditorWindow.Command);
            MarkedText.Of(window).ShouldBe("|beta");
            (await ClipboardText(window)).ShouldBe("alpha" + s_lineBreak);

            await PasteAsync(window);
            MarkedText.Of(window).ShouldBe("alpha\n|beta");

            document.Undo();
            MarkedText.Of(window).ShouldBe("|beta");
            document.Undo();
            MarkedText.Of(window).ShouldBe("al|pha\nbeta");
        });

    /// <summary>The last line goes with the line break before it, as the only line without one after it.</summary>
    [Theory]
    [InlineData("alpha\nbe|ta", "alpha|", "beta")]
    [InlineData("al|pha", "|", "alpha")]
    public Task Cut_without_a_selection_on_the_last_line_takes_the_line_break_before_it(
        string marked,
        string expected,
        string line
    ) =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show(marked, CodeLanguage.PlainText);

            window.Press(Key.X, EditorWindow.Command);

            MarkedText.Of(window).ShouldBe(expected);
            (await ClipboardText(window)).ShouldBe(line + s_lineBreak);
        });

    [Fact]
    public Task A_copied_line_replaces_a_selection_as_text() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("al|pha\nbeta", CodeLanguage.PlainText);

            window.Press(Key.C, EditorWindow.Command);
            await ClipboardText(window);
            window.Editor.Selection = new TextSelection(6, 8);
            await PasteAsync(window);

            MarkedText.Of(window).ShouldBe("alpha\nalpha\n|ta");
        });

    [Fact]
    public Task Text_from_elsewhere_with_a_line_break_at_its_end_pastes_at_the_caret() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("al|pha\nbeta", CodeLanguage.PlainText);
            // The editor's copied line, which the clipboard no longer holds.
            window.Press(Key.C, EditorWindow.Command);
            await ClipboardText(window);
            window.Editor.Selection = new TextSelection(8);
            await Clipboard(window).SetTextAsync("gamma" + s_lineBreak);

            await PasteAsync(window);

            MarkedText.Of(window).ShouldBe("alpha\nbegamma\n|ta");
        });

    /// <summary>After a lone CR, the line is a line of its own: its LF does not join the CR.</summary>
    [Fact]
    public Task A_copied_empty_line_pastes_as_a_line_of_its_own_after_a_lone_CR() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|\na\rb", CodeLanguage.PlainText);
            window.Press(Key.C, EditorWindow.Command);
            await ClipboardText(window);
            window.Editor.Selection = new TextSelection(3);

            await PasteAsync(window);

            MarkedText.Of(window).ShouldBe("\na\n\n|b");
        });

    /// <summary>Where text can go in at the caret but not at its line's start, the line goes in at the caret.</summary>
    [Fact]
    public Task A_copied_line_pastes_at_the_caret_where_its_lines_start_is_read_only() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|ab\ncd", CodeLanguage.PlainText);
            window.Press(Key.C, EditorWindow.Command);
            await ClipboardText(window);
            TextArea textArea = EditorWindow.PartOf(window.Editor).TextArea;
            var protectedText = new TextSegmentReadOnlySectionProvider<TextSegment>(textArea.Document);
            // Around the second line's start, not around the caret at its end.
            protectedText.Segments.Add(new TextSegment { StartOffset = 2, Length = 2 });
            textArea.ReadOnlySectionProvider = protectedText;
            window.Editor.Selection = new TextSelection(5);

            await PasteAsync(window);

            MarkedText.Of(window).ShouldBe("ab\ncdab\n|");
        });

    /// <summary>The editor shows and edits a long line's first part only: its key goes on, and cuts nothing.</summary>
    [Fact]
    public Task Cut_without_a_selection_leaves_a_line_with_a_hidden_tail() =>
        _ui.Run(async () =>
        {
            string line = new('x', VisibleTextLimit + 500);
            using EditorWindow window = MarkedText.Show("a\n|" + line, CodeLanguage.PlainText);
            await Clipboard(window).SetTextAsync("kept");

            window.Press(Key.X, EditorWindow.Command);

            DocumentOf(window).Text.ShouldBe("a\n" + line);
            (await ClipboardText(window)).ShouldBe("kept");
        });

    [Fact]
    public Task Without_EmptySelectionClipboard_copy_and_cut_need_a_selection_and_lines_paste_as_text() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("al|pha\nbeta", CodeLanguage.PlainText);
            window.Press(Key.C, EditorWindow.Command);
            await ClipboardText(window);
            window.Editor.Options = CodeEditorOptions.Default with { EmptySelectionClipboard = false };

            window.Editor.Selection = new TextSelection(8);
            await PasteAsync(window);
            MarkedText.Of(window).ShouldBe("alpha\nbealpha\n|ta");

            await Clipboard(window).SetTextAsync("kept");
            window.Press(Key.C, EditorWindow.Command);
            window.Press(Key.X, EditorWindow.Command);
            window.Layout();
            MarkedText.Of(window).ShouldBe("alpha\nbealpha\n|ta");
            (await ClipboardText(window)).ShouldBe("kept");
        });

    /// <summary>
    /// Pastes by key, or by the editor's method, and waits for the text to change: the clipboard is read in the
    /// background.
    /// </summary>
    private static Task PasteAsync(EditorWindow window, bool byKey = true)
    {
        CodeDocument document = DocumentOf(window);
        var changed = new TaskCompletionSource();
        document.TextChanged += OnChanged;
        if (byKey)
            window.Press(Key.V, EditorWindow.Command);
        else
            window.Editor.Paste();
        return Wait.ForAsync(changed.Task);

        void OnChanged(object? sender, EventArgs e)
        {
            document.TextChanged -= OnChanged;
            changed.TrySetResult();
        }
    }

    private const int VisibleTextLimit = Darp.CodeEditor.Editing.VisibleText.LineLimit;

    private static Task<string?> ClipboardText(EditorWindow window) => Clipboard(window).TryGetTextAsync();

    private static IClipboard Clipboard(EditorWindow window) =>
        window.Window.Clipboard ?? throw new InvalidOperationException("The window has no clipboard.");

    private static CodeDocument DocumentOf(EditorWindow window) =>
        window.Editor.Document ?? throw new InvalidOperationException("No document.");
}

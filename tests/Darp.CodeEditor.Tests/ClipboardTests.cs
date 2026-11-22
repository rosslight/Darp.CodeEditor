using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Tests;

/// <summary>The clipboard keys and their line breaks.</summary>
[Collection(UiFixture.Collection)]
public sealed class ClipboardTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    /// <summary>
    /// In the document's line breaks, and on Windows in its own, <c>\r\n</c>: from a document with <c>\n</c>, that is
    /// the system's either way.
    /// </summary>
    [Fact]
    public Task Copy_takes_the_documents_line_breaks() =>
        _ui.Run(async () =>
        {
            using (EditorWindow window = MarkedText.Show("«a\r\nb»\r\nc", CodeLanguage.PlainText))
            {
                window.Press(Key.C, EditorWindow.Command);

                (await ClipboardText(window)).ShouldBe("a\r\nb");
            }
            using (EditorWindow window = MarkedText.Show("«a\nb»\nc", CodeLanguage.PlainText))
            {
                window.Press(Key.C, EditorWindow.Command);

                (await ClipboardText(window)).ShouldBe("a" + NewLines.OfNewDocuments + "b");
            }
        });

    [Fact]
    public Task Cut_takes_the_documents_line_breaks_too() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("«a\r\nb»\r\nc", CodeLanguage.PlainText);

            window.Press(Key.X, EditorWindow.Command);

            (await ClipboardText(window)).ShouldBe("a\r\nb");
            MarkedText.Of(window).ShouldBe("|\r\nc");
        });

    /// <summary>Also on a line that ends in another line break than the document's.</summary>
    [Fact]
    public Task Pasted_text_goes_in_with_the_documents_line_breaks() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("a\r\nb\r\n|c\nd", CodeLanguage.PlainText);
            await Clipboard(window).SetTextAsync("x\ny\rz");

            Task pasted = NextChangeAsync(window);
            window.Press(Key.V, EditorWindow.Command);
            await pasted;

            MarkedText.Of(window).ShouldBe("a\r\nb\r\nx\r\ny\r\nz|c\nd");
        });

    [Fact]
    public Task Pasted_tabs_become_spaces_in_a_document_that_indents_with_spaces() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("a\r\n|b", CodeLanguage.PlainText);
            await Clipboard(window).SetTextAsync("\tx\ny");

            Task pasted = NextChangeAsync(window);
            window.Press(Key.V, EditorWindow.Command);
            await pasted;

            MarkedText.Of(window).ShouldBe("a\r\n    x\r\ny|b");
        });

    [Fact]
    public Task Copy_and_cut_raise_the_text_areas_TextCopied() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("«a\r\nb»", CodeLanguage.PlainText);
            TextArea textArea = EditorWindow.PartOf(window.Editor).TextArea;
            var copied = new List<string>();
            textArea.TextCopied += (_, e) => copied.Add(e.Text);

            window.Press(Key.C, EditorWindow.Command);
            window.Press(Key.X, EditorWindow.Command);

            copied.Count.ShouldBe(2);
        });

    /// <summary>
    /// Each row takes a line of the text, and the last row the rest, with the document's line breaks, also on rows that
    /// end in another.
    /// </summary>
    [Fact]
    public Task A_rectangle_takes_the_pasted_lines_with_the_documents_line_breaks() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("a\r\nb\r\nc\r\n12\n3|4", CodeLanguage.PlainText);
            TextArea textArea = EditorWindow.PartOf(window.Editor).TextArea;
            textArea.Selection = new RectangleSelection(
                textArea,
                new TextViewPosition(4, 2),
                new TextViewPosition(5, 3)
            );
            await Clipboard(window).SetTextAsync("x\ny\nz");

            Task pasted = NextChangeAsync(window);
            window.Press(Key.V, EditorWindow.Command);
            await pasted;

            window.Editor.Document.ShouldNotBeNull().Text.ShouldBe("a\r\nb\r\nc\r\n1x\n3y\r\nz");
        });

    /// <summary>Also where the drop lands on a line that ends in another line break than the document's.</summary>
    [Fact]
    public Task Dropped_text_goes_in_with_the_documents_line_breaks() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("a\r\nb\r\n|c\nd", CodeLanguage.PlainText);
            using var data = new DataTransfer();
            var item = new DataTransferItem();
            item.Set(DataFormat.Text, "x\ny");
            data.Add(item);
            Point at = window.PointAt(6) - new Point(window.TextView.WideSpaceWidth / 2, 0);

            EditorWindow
                .PartOf(window.Editor)
                .TextArea.RaiseEvent(
                    new DragEventArgs(DragDrop.DropEvent, data, window.Window, at, KeyModifiers.None)
                    {
                        DragEffects = DragDropEffects.Copy,
                    }
                );

            MarkedText.Of(window).ShouldBe("a\r\nb\r\n«x\r\ny»c\nd");
        });

    /// <summary>A text input of several characters, as from an input method, with a line break in it.</summary>
    [Fact]
    public Task Typed_text_with_line_breaks_goes_in_with_the_documents() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("a\r\nb\r\n|c\nd", CodeLanguage.PlainText);

            window.Window.KeyTextInput("x\ny");
            window.Layout();

            MarkedText.Of(window).ShouldBe("a\r\nb\r\nx\r\ny|c\nd");
        });

    /// <summary>
    /// A platform's clipboard can take a while: what the paste reads goes nowhere once the editor shows another document
    /// or becomes read-only, and leaves both documents as they were.
    /// </summary>
    [Theory]
    [InlineData("another document")]
    [InlineData("read-only")]
    public Task A_paste_that_waits_for_the_clipboard_goes_in_only_where_it_started(string change) =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|a", CodeLanguage.PlainText);
            CodeDocument first = window.Editor.Document.ShouldNotBeNull();
            CodeDocument second = CodeDocument.Create(CodeLanguage.PlainText, "b");
            var held = new HeldText("x");
            await Clipboard(window).SetDataAsync(held);

            window.Press(Key.V, EditorWindow.Command);
            if (change == "read-only")
                window.Editor.IsReadOnly = true;
            else
                window.Editor.Document = second;
            held.Release();
            await EditorWindow.RunPostedJobsAsync();

            first.Text.ShouldBe("a");
            second.Text.ShouldBe("b");
            // No update of either document was left open.
            first.Replace(new TextRange(0, 0), "1");
            second.Replace(new TextRange(0, 0), "2");
            (first.Text, second.Text).ShouldBe(("1a", "2b"));
        });

    /// <summary>As AvaloniaEdit's paste: by key and by the editor's method alike.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Paste_leaves_the_text_alone_where_the_caret_cannot_insert(bool byKey) =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("«abc»def", CodeLanguage.PlainText);
            TextArea textArea = EditorWindow.PartOf(window.Editor).TextArea;
            var protectedText = new TextSegmentReadOnlySectionProvider<TextSegment>(textArea.Document);
            protectedText.Segments.Add(new TextSegment { StartOffset = 2, Length = 3 });
            textArea.ReadOnlySectionProvider = protectedText;
            await Clipboard(window).SetTextAsync("X");

            if (byKey)
                window.Press(Key.V, EditorWindow.Command);
            else
                window.Editor.Paste();
            await EditorWindow.RunPostedJobsAsync();

            window.Editor.Document.ShouldNotBeNull().Text.ShouldBe("abcdef");
        });

    /// <summary>As AvaloniaEdit's paste: not at the start of a long line's hidden tail, by key and by method alike.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Paste_leaves_the_text_alone_at_a_long_lines_hidden_tail(bool byKey) =>
        _ui.Run(async () =>
        {
            string line = new('a', 3_500);
            using var window = new EditorWindow(
                new CodeEditor { Document = CodeDocument.Create(CodeLanguage.PlainText, line) }
            );
            window.Editor.Selection = new TextSelection(2_990, 3_000);
            await Clipboard(window).SetTextAsync("X");

            if (byKey)
                window.Press(Key.V, EditorWindow.Command);
            else
                window.Editor.Paste();
            await EditorWindow.RunPostedJobsAsync();

            window.Editor.Document.ShouldNotBeNull().Text.ShouldBe(line);
        });

    /// <summary>
    /// The editor's check before a paste stands in for AvaloniaEdit's, which is internal: AvaloniaEdit's own paste binding
    /// answers the same for each caret, so that a change of its rule fails here instead of drifting.
    /// </summary>
    [Fact]
    public Task The_check_before_a_paste_answers_as_AvaloniaEdits_paste_binding() =>
        _ui.Run(() =>
        {
            const int longLine = 6;
            var buffer = new TextDocument("short\n" + new string('a', 3_500) + "\nend");
            var textArea = new TextArea { Document = buffer };
            textArea.Options.LongLineLimit = VisibleText.LineLimit;
            var protectedText = new TextSegmentReadOnlySectionProvider<TextSegment>(buffer);
            protectedText.Segments.Add(new TextSegment { StartOffset = 1, Length = 3 });
            textArea.ReadOnlySectionProvider = protectedText;
            int[] carets =
            [
                0,
                1,
                2,
                4,
                longLine,
                longLine + VisibleText.LineLimit - 1,
                longLine + VisibleText.LineLimit,
                longLine + VisibleText.LineLimit + 1,
                longLine + 3_500,
                buffer.TextLength,
            ];
            var answers = new List<bool>();

            foreach (int caret in carets)
            {
                textArea.Caret.Offset = caret;
                bool avaloniaEdit = ApplicationCommands.Paste.CanExecute(null, textArea);
                VisibleText.CanInsert(textArea, caret).ShouldBe(avaloniaEdit, $"at {caret}");
                answers.Add(avaloniaEdit);
            }

            // Both answers occur, so that the comparison means something.
            answers.ShouldContain(true);
            answers.ShouldContain(false);
        });

    /// <summary>Menus ask the command whether it can run.</summary>
    [Fact]
    public Task The_paste_command_can_run_only_where_text_can_go_in() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("a|b", CodeLanguage.PlainText);
            TextArea textArea = EditorWindow.PartOf(window.Editor).TextArea;

            ApplicationCommands.Paste.CanExecute(null, textArea).ShouldBeTrue();
            window.Editor.IsReadOnly = true;
            ApplicationCommands.Paste.CanExecute(null, textArea).ShouldBeFalse();
        });

    /// <summary>A handler of the change that the line breaks make may show another, shorter document.</summary>
    [Fact]
    public Task A_drop_whose_change_switches_the_document_leaves_the_other_one_alone() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("a\r\nb\r\n|c\nd", CodeLanguage.PlainText);
            CodeDocument dropped = window.Editor.Document.ShouldNotBeNull();
            CodeDocument other = CodeDocument.Create(CodeLanguage.PlainText, "z");
            int changes = 0;
            // The drop's insertion, then the change of its line breaks.
            dropped.TextChanged += (_, _) =>
            {
                if (++changes == 2)
                    window.Editor.Document = other;
            };
            using var data = new DataTransfer();
            var item = new DataTransferItem();
            item.Set(DataFormat.Text, "x\ny");
            data.Add(item);
            Point at = window.PointAt(6) - new Point(window.TextView.WideSpaceWidth / 2, 0);

            EditorWindow
                .PartOf(window.Editor)
                .TextArea.RaiseEvent(
                    new DragEventArgs(DragDrop.DropEvent, data, window.Window, at, KeyModifiers.None)
                    {
                        DragEffects = DragDropEffects.Copy,
                    }
                );

            window.Editor.Document.ShouldBe(other);
            MarkedText.Of(window).ShouldBe("|z");
        });

    /// <summary>Completes once the text of the editor's document changes.</summary>
    private static Task NextChangeAsync(EditorWindow window)
    {
        CodeDocument document = window.Editor.Document.ShouldNotBeNull();
        var changed = new TaskCompletionSource();
        document.TextChanged += OnChanged;
        return Wait.ForAsync(changed.Task);

        void OnChanged(object? sender, EventArgs e)
        {
            document.TextChanged -= OnChanged;
            changed.TrySetResult();
        }
    }

    private static IClipboard Clipboard(EditorWindow window) =>
        window.Window.Clipboard ?? throw new InvalidOperationException("The window has no clipboard.");

    private static Task<string?> ClipboardText(EditorWindow window) => Clipboard(window).TryGetTextAsync();
}

using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

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

            window.Press(Key.V, EditorWindow.Command);
            await Wait.UntilAsync(() => window.Editor.Document?.Text != "a\r\nb\r\nc\nd");

            MarkedText.Of(window).ShouldBe("a\r\nb\r\nx\r\ny\r\nz|c\nd");
        });

    [Fact]
    public Task Pasted_tabs_become_spaces_in_a_document_that_indents_with_spaces() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("a\r\n|b", CodeLanguage.PlainText);
            await Clipboard(window).SetTextAsync("\tx\ny");

            window.Press(Key.V, EditorWindow.Command);
            await Wait.UntilAsync(() => window.Editor.Document?.Text != "a\r\nb");

            MarkedText.Of(window).ShouldBe("a\r\n    x\r\ny|b");
        });

    [Fact]
    public Task Copy_and_paste_raise_the_text_areas_events() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("«a\r\nb»", CodeLanguage.PlainText);
            TextArea textArea = EditorWindow.PartOf(window.Editor).TextArea;
            var copied = new List<string>();
            var pasted = new List<string>();
            textArea.TextCopied += (_, e) => copied.Add(e.Text);
            textArea.TextPasted += (_, e) => pasted.Add(e.Text);

            window.Press(Key.C, EditorWindow.Command);
            await ClipboardText(window);
            window.Press(Key.V, EditorWindow.Command);
            await Wait.UntilAsync(() => pasted.Count > 0);

            copied.Count.ShouldBe(1);
            pasted.Count.ShouldBe(1);
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

    private static IClipboard Clipboard(EditorWindow window) =>
        window.Window.Clipboard ?? throw new InvalidOperationException("The window has no clipboard.");

    private static Task<string?> ClipboardText(EditorWindow window) => Clipboard(window).TryGetTextAsync();
}

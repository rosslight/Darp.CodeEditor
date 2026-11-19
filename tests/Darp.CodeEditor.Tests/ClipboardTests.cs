using Avalonia.Input;
using Avalonia.Input.Platform;

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

    private static IClipboard Clipboard(EditorWindow window) =>
        window.Window.Clipboard ?? throw new InvalidOperationException("The window has no clipboard.");

    private static Task<string?> ClipboardText(EditorWindow window) => Clipboard(window).TryGetTextAsync();
}

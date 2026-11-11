using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The clipboard keys as VS Code's: without a selection, copy and cut take the caret's whole line, which pastes as a
/// whole line above the caret's.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class ClipboardTests(UiFixture ui)
{
    private static readonly string s_lineBreak = Environment.NewLine;

    private readonly UiFixture _ui = ui;

    [Fact]
    public Task Copy_without_a_selection_copies_the_line_which_pastes_above_the_carets_line() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("al|pha\nbeta", CodeLanguage.PlainText);

            window.Press(Key.C, EditorWindow.Command);
            (await ClipboardText(window)).ShouldBe("alpha" + s_lineBreak);

            window.Editor.Selection = new TextSelection(8);
            window.Press(Key.V, EditorWindow.Command);
            await Wait.UntilAsync(() => DocumentOf(window).Text != "alpha\nbeta");

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

            window.Press(Key.X, EditorWindow.Command);
            MarkedText.Of(window).ShouldBe("|beta");
            (await ClipboardText(window)).ShouldBe("alpha" + s_lineBreak);

            await PasteAsync(window);
            MarkedText.Of(window).ShouldBe("alpha\n|beta");

            DocumentOf(window).Undo();
            MarkedText.Of(window).ShouldBe("|beta");
            DocumentOf(window).Undo();
            MarkedText.Of(window).ShouldBe("al|pha\nbeta");
        });

    [Theory]
    // The last line goes with the line break before it, the only line without one.
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
            using EditorWindow window = MarkedText.Show("alpha\nbe|ta", CodeLanguage.PlainText);
            await Clipboard(window).SetTextAsync("gamma" + s_lineBreak);

            await PasteAsync(window);

            MarkedText.Of(window).ShouldBe("alpha\nbegamma\n|ta");
        });

    [Fact]
    public Task Without_EmptySelectionClipboard_copy_and_cut_need_a_selection_and_lines_paste_as_text() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("al|pha\nbeta", CodeLanguage.PlainText);
            window.Press(Key.C, EditorWindow.Command);
            await ClipboardText(window);
            window.Editor.EmptySelectionClipboard = false;

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

    /// <summary>Pastes by key, and waits until the text changed: the clipboard is read in the background.</summary>
    private static async Task PasteAsync(EditorWindow window)
    {
        string before = DocumentOf(window).Text;
        window.Press(Key.V, EditorWindow.Command);
        await Wait.UntilAsync(() => DocumentOf(window).Text != before);
    }

    private static Task<string?> ClipboardText(EditorWindow window) => Clipboard(window).TryGetTextAsync();

    private static IClipboard Clipboard(EditorWindow window) =>
        window.Window.Clipboard ?? throw new InvalidOperationException("The window has no clipboard.");

    private static CodeDocument DocumentOf(EditorWindow window) =>
        window.Editor.Document ?? throw new InvalidOperationException("No document.");
}

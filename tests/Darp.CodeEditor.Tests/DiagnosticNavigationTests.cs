using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Going to the next and previous diagnostic, as VS Code's F8 and Shift+F8 do: the caret goes to it, and its message
/// shows in the hover at the caret.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class DiagnosticNavigationTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    private const string Text = "alpha beta gamma delta";

    [Fact]
    public Task F8_and_Shift_F8_go_through_errors_warnings_and_information_with_wraparound_and_show_the_message() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, Text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            SetDiagnostics(document);

            // VS Code's order: the most severe first, then by position; hints are left out.
            (await GoAsync(window, Key.F8)).ShouldBe((17, "delta is wrong"));
            (await GoAsync(window, Key.F8)).ShouldBe((6, "beta looks odd"));
            (await GoAsync(window, Key.F8)).ShouldBe((0, "alpha is noted"));
            (await GoAsync(window, Key.F8)).ShouldBe((17, "delta is wrong"));
            (await GoAsync(window, Key.F8, KeyModifiers.Shift)).ShouldBe((0, "alpha is noted"));
            (await GoAsync(window, Key.F8, KeyModifiers.Shift)).ShouldBe((6, "beta looks odd"));
            // Alt+F8 and Shift+Alt+F8 too.
            (await GoAsync(window, Key.F8, KeyModifiers.Alt)).ShouldBe((0, "alpha is noted"));
            (await GoAsync(window, Key.F8, KeyModifiers.Alt | KeyModifiers.Shift)).ShouldBe((6, "beta looks odd"));
        });

    [Fact]
    public Task In_position_order_the_first_move_starts_at_the_caret_and_keeps_it_inside_its_diagnostic() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, Text);
            using var window = new EditorWindow(
                new CodeEditor { Document = document, DiagnosticOrder = DiagnosticOrder.Position }
            );
            SetDiagnostics(document);
            window.Editor.Selection = new TextSelection(8);

            (await GoAsync(window, Key.F8)).ShouldBe((8, "beta looks odd"));
            (await GoAsync(window, Key.F8)).ShouldBe((17, "delta is wrong"));
            (await GoAsync(window, Key.F8)).ShouldBe((0, "alpha is noted"));

            // A caret moved elsewhere starts again from there: backwards, from before the next one.
            window.Editor.Selection = new TextSelection(12);
            (await GoAsync(window, Key.F8, KeyModifiers.Shift)).ShouldBe((6, "beta looks odd"));
        });

    [Fact]
    public Task A_diagnostic_out_of_view_scrolls_into_view_with_its_message_and_one_at_a_words_end_shows_its_own() =>
        _ui.Run(async () =>
        {
            string text = string.Concat(Enumerable.Range(0, 300).Select(i => $"line{i}\n"));
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            // Empty, at the end of a line's word, as a missing colon.
            int end = text.IndexOf("line250\n", StringComparison.Ordinal) + "line250".Length;
            document.SetDiagnostics([
                new Diagnostic(
                    document.CurrentSnapshot,
                    new TextRange(end, 0),
                    DiagnosticSeverity.Error,
                    null,
                    "':' expected"
                ),
            ]);

            (await GoAsync(window, Key.F8)).ShouldBe((end, "':' expected"));

            window.Editor.CaretPosition.Line.ShouldBe(251);
        });

    [Fact]
    public Task Without_diagnostics_F8_does_nothing() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, Text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Editor.Selection = new TextSelection(3);

            window.Press(Key.F8);

            window.Editor.Selection.ShouldBe(new TextSelection(3));
            Hover(window).ShouldBeNull();
        });

    /// <summary>An information, a warning, a hint and an error, on the words of <see cref="Text"/> in turn.</summary>
    private static void SetDiagnostics(CodeDocument document)
    {
        TextSnapshot snapshot = document.CurrentSnapshot;
        document.SetDiagnostics([
            new Diagnostic(snapshot, new TextRange(0, 5), DiagnosticSeverity.Information, null, "alpha is noted"),
            new Diagnostic(snapshot, new TextRange(6, 4), DiagnosticSeverity.Warning, null, "beta looks odd"),
            new Diagnostic(snapshot, new TextRange(11, 5), DiagnosticSeverity.Hint, null, "gamma could be shorter"),
            new Diagnostic(snapshot, new TextRange(17, 5), DiagnosticSeverity.Error, null, "delta is wrong"),
        ]);
    }

    /// <summary>
    /// Presses <paramref name="key"/>, and waits for the hover with a diagnostic's message: returns the caret then, and
    /// the message.
    /// </summary>
    private static async Task<(int Caret, string Message)> GoAsync(
        EditorWindow window,
        Key key,
        KeyModifiers modifiers = KeyModifiers.None
    )
    {
        string[] messages =
        [
            "alpha is noted",
            "beta looks odd",
            "gamma could be shorter",
            "delta is wrong",
            "':' expected",
        ];
        window.Press(key, modifiers);
        string? message = null;
        await Wait.UntilAsync(() =>
        {
            window.Render();
            message = Hover(window) is { } hover ? Texts(hover).FirstOrDefault(messages.Contains) : null;
            return message is not null;
        });
        window.Editor.Selection.IsEmpty.ShouldBeTrue();
        return (window.Editor.Selection.Caret, message!);
    }

    private static PopupFrame? Hover(EditorWindow window) => PopupFrames.Find(window, "Hover");

    private static IEnumerable<string> Texts(PopupFrame popup) =>
        popup.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Inlines?.Text ?? block.Text ?? "");
}

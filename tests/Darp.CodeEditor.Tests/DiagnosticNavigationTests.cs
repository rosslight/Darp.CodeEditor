using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Going to the next and the previous diagnostic by F8 and Shift+F8: the caret goes to it, and it shows in the hover at
/// the caret with which problem it is of how many, the most severe first.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class DiagnosticNavigationTests(UiFixture ui)
{
    private const string Text = "alpha beta gamma delta";

    private readonly UiFixture _ui = ui;

    [Fact]
    public Task F8_and_Shift_F8_go_through_errors_warnings_and_information_with_wraparound_and_show_the_message() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, Text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            SetDiagnostics(document);

            // The most severe first, then by position; hints are left out.
            (await GoAsync(window, Key.F8)).ShouldBe((17, "delta is wrong"));
            // As VS Code's widget: which problem it is of how many, without the server's hover.
            Texts(Hover(window).ShouldNotBeNull()).ShouldBe(["1 of 3 problems", "delta is wrong"]);
            Hover(window).ShouldNotBeNull().GetVisualDescendants().OfType<SeverityIcon>().ShouldHaveSingleItem();
            (await GoAsync(window, Key.F8)).ShouldBe((6, "beta looks odd"));
            Texts(Hover(window).ShouldNotBeNull()).ShouldContain("2 of 3 problems");
            (await GoAsync(window, Key.F8)).ShouldBe((0, "alpha is noted"));
            (await GoAsync(window, Key.F8)).ShouldBe((17, "delta is wrong"));
            (await GoAsync(window, Key.F8, KeyModifiers.Shift)).ShouldBe((0, "alpha is noted"));
            (await GoAsync(window, Key.F8, KeyModifiers.Shift)).ShouldBe((6, "beta looks odd"));
            // Alt+F8 and Shift+Alt+F8 too.
            (await GoAsync(window, Key.F8, KeyModifiers.Alt)).ShouldBe((0, "alpha is noted"));
            (await GoAsync(window, Key.F8, KeyModifiers.Alt | KeyModifiers.Shift)).ShouldBe((6, "beta looks odd"));

            // In the UI culture's language.
            CultureInfo culture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
                await GoAsync(window, Key.F8);
                Texts(Hover(window).ShouldNotBeNull()).ShouldContain("3 von 3 Problemen");
            }
            finally
            {
                CultureInfo.CurrentUICulture = culture;
            }
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
            window.TextView.VisualLines.ShouldContain(line => line.FirstDocumentLine.LineNumber == 251);
            Texts(Hover(window).ShouldNotBeNull()).First().ShouldBe("1 of 1 problem");
        });

    [Fact]
    public Task New_diagnostics_show_in_the_problem_that_shows_or_close_it_without_one_there() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, Text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            SetDiagnostics(document);
            (await GoAsync(window, Key.F8)).ShouldBe((17, "delta is wrong"));

            // A warning replaces the error at the same range: it shows, as the second of two now.
            TextSnapshot snapshot = document.CurrentSnapshot;
            var beta = new Diagnostic(
                snapshot,
                new TextRange(6, 4),
                DiagnosticSeverity.Warning,
                null,
                "beta looks odd"
            );
            document.SetDiagnostics([
                beta,
                new Diagnostic(snapshot, new TextRange(17, 5), DiagnosticSeverity.Warning, null, "delta looks odd"),
            ]);
            window.Layout();
            PopupFrame hover = Hover(window).ShouldNotBeNull();
            Texts(hover).ShouldBe(["2 of 2 problems", "delta looks odd"]);
            hover.GetVisualDescendants().OfType<SeverityIcon>().Single().Severity.ShouldBe(DiagnosticSeverity.Warning);

            // None is there any more: it closes, as the caret stays.
            document.SetDiagnostics([beta]);
            window.Layout();
            Hover(window).ShouldBeNull();
            window.Editor.Selection.ShouldBe(new TextSelection(17));
        });

    [Fact]
    public Task F8_stops_once_the_app_shows_another_document_as_the_caret_moves() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, Text);
            CodeDocument other = CodeDocument.Create(CodeLanguage.PlainText, "other text");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            SetDiagnostics(document);
            // An app that shows another document as the selection moves.
            window.Editor.PropertyChanged += (_, e) =>
            {
                if (e.Property == CodeEditor.SelectionProperty)
                    window.Editor.Document = other;
            };

            window.Press(Key.F8);
            window.Render();

            window.Editor.Document.ShouldBeSameAs(other);
            Hover(window).ShouldBeNull();
            window.Editor.Selection.ShouldBe(new TextSelection(0));
        });

    [Fact]
    public Task F8_after_Escape_starts_again_from_the_caret() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, Text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            SetDiagnostics(document);
            (await GoAsync(window, Key.F8)).ShouldBe((17, "delta is wrong"));

            window.Press(Key.Escape);
            Hover(window).ShouldBeNull();

            // From the caret, on the error, which holds it; the presses after go on from there.
            (await GoAsync(window, Key.F8)).ShouldBe((17, "delta is wrong"));
            (await GoAsync(window, Key.F8)).ShouldBe((6, "beta looks odd"));
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
        return (window.Editor.Selection.Caret, message ?? "");
    }

    private static PopupFrame? Hover(EditorWindow window) => PopupFrames.Find(window, "Hover");

    private static IEnumerable<string> Texts(PopupFrame popup) =>
        popup.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Inlines?.Text ?? block.Text ?? "");
}

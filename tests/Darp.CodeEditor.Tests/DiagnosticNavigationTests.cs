using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// F8 and Shift+F8: the caret goes to the next and the previous error, warning or information by position, wrapping
/// around, and the problem shows in the hover.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class DiagnosticNavigationTests(UiFixture ui)
{
    private const string Text = "alpha beta gamma delta";

    private readonly UiFixture _ui = ui;

    [Fact]
    public Task F8_and_Shift_F8_go_by_position_with_wraparound_past_hints_and_show_the_problem() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, Text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            SetDiagnostics(document);

            (await GoAsync(window, Key.F8)).ShouldBe((0, "alpha is noted"));
            Texts(Hover(window).ShouldNotBeNull()).ShouldBe(["alpha is noted"]);
            Hover(window).ShouldNotBeNull().GetVisualDescendants().OfType<SeverityIcon>().ShouldHaveSingleItem();
            (await GoAsync(window, Key.F8)).ShouldBe((6, "beta looks odd"));
            (await GoAsync(window, Key.F8)).ShouldBe((17, "delta is wrong"));
            (await GoAsync(window, Key.F8)).ShouldBe((0, "alpha is noted"));
            (await GoAsync(window, Key.F8, KeyModifiers.Shift)).ShouldBe((17, "delta is wrong"));
            (await GoAsync(window, Key.F8, KeyModifiers.Shift)).ShouldBe((6, "beta looks odd"));
        });

    [Fact]
    public Task From_the_caret_F8_goes_to_the_problem_there_or_after_it_and_Shift_F8_before_it() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, Text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            SetDiagnostics(document);

            // Inside "beta".
            window.Editor.Selection = new TextSelection(8);
            (await GoAsync(window, Key.F8)).ShouldBe((6, "beta looks odd"));

            // In "gamma", whose hint is not visited.
            window.Editor.Selection = new TextSelection(12);
            (await GoAsync(window, Key.F8, KeyModifiers.Shift)).ShouldBe((6, "beta looks odd"));
        });

    [Fact]
    public Task Problems_that_start_at_one_place_are_all_visited() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, Text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            TextSnapshot snapshot = document.CurrentSnapshot;
            document.SetDiagnostics([
                new Diagnostic(snapshot, new TextRange(6, 4), DiagnosticSeverity.Error, null, "alpha is noted"),
                new Diagnostic(snapshot, new TextRange(6, 2), DiagnosticSeverity.Warning, null, "beta looks odd"),
            ]);

            string first = (await GoAsync(window, Key.F8)).Message;
            string second = (await GoAsync(window, Key.F8)).Message;

            new[] { first, second }.ShouldBe(["alpha is noted", "beta looks odd"], ignoreOrder: true);
        });

    [Fact]
    public Task A_problem_out_of_view_scrolls_into_view_and_shows() =>
        _ui.Run(async () =>
        {
            string text = string.Concat(Enumerable.Range(0, 300).Select(i => $"line {i}\n"));
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            int place = text.IndexOf("line 250", StringComparison.Ordinal);
            document.SetDiagnostics([
                new Diagnostic(
                    document.CurrentSnapshot,
                    new TextRange(place, 4),
                    DiagnosticSeverity.Error,
                    null,
                    "delta is wrong"
                ),
            ]);

            (await GoAsync(window, Key.F8)).ShouldBe((place, "delta is wrong"));

            window.Layout();
            window.Render();
            Hover(window).ShouldNotBeNull();
            window.PointAt(place).Y.ShouldBeInRange(0, window.Window.ClientSize.Height);
        });

    [Fact]
    public Task F8_after_Escape_starts_again_from_the_caret() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, Text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            SetDiagnostics(document);
            (await GoAsync(window, Key.F8)).ShouldBe((0, "alpha is noted"));

            window.Press(Key.Escape);
            Hover(window).ShouldBeNull();
            window.Editor.Selection = new TextSelection(9);

            (await GoAsync(window, Key.F8)).ShouldBe((6, "beta looks odd"));
        });

    [Fact]
    public Task Without_a_problem_F8_goes_on_to_the_app() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, Text);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            editor.Selection = new TextSelection(3);
            // Only a hint, which F8 does not visit.
            document.SetDiagnostics([
                new Diagnostic(document.CurrentSnapshot, new TextRange(0, 5), DiagnosticSeverity.Hint, null, "hint"),
            ]);
            int ran = 0;
            window.Window.KeyBindings.Add(
                new KeyBinding { Gesture = new KeyGesture(Key.F8), Command = new ActionCommand(() => ran++) }
            );

            window.Press(Key.F8);

            ran.ShouldBe(1);
            editor.Selection.ShouldBe(new TextSelection(3));
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
        string[] messages = ["alpha is noted", "beta looks odd", "gamma could be shorter", "delta is wrong"];
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

    private sealed class ActionCommand(Action run) : System.Windows.Input.ICommand
    {
        private readonly Action _run = run;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _run();
    }
}

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Darp.CodeEditor.LanguageClient;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The occurrences of the symbol at the caret, highlighted as VS Code's <c>editor.occurrencesHighlight</c> does them:
/// the server's reads and writes, or else the word's.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class OccurrenceHighlightTests(UiFixture ui)
{
    private static readonly TimeSpan s_pause = TimeSpan.FromMilliseconds(50);

    /// <summary><c>editor.wordHighlightBackground</c> of the tests' theme.</summary>
    private static readonly Color s_read = Color.Parse("#40C080");

    /// <summary><c>editor.wordHighlightStrongBackground</c> of the tests' theme.</summary>
    private static readonly Color s_write = Color.Parse("#8040C0");

    /// <summary><c>editor.wordHighlightTextBackground</c> of the tests' theme.</summary>
    private static readonly Color s_word = Color.Parse("#C04080");

    private readonly UiFixture _ui = ui;

    [Fact]
    public Task The_servers_occurrences_show_with_reads_and_writes_apart_after_the_caret_moves() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "x = 1\ny = x\nx = y");
            using EditorWindow window = Show(document);
            await CompletionTests.StartServingAsync(server, document);
            window.Editor.OccurrencesHighlight.ShouldBe(OccurrencesHighlight.SingleFile);

            window.Editor.Selection = new TextSelection(1);
            Advance(time, s_pause - TimeSpan.FromMilliseconds(1));
            fake.LinesStartingWith("documentHighlight").ShouldBeEmpty();
            Advance(time, TimeSpan.FromMilliseconds(1));

            await Wait.UntilAsync(() => window.Editor.Occurrences.Count > 0);
            window.Editor.Occurrences.ShouldBe([
                new Occurrence(new TextRange(0, 1), OccurrenceKind.Write),
                new Occurrence(new TextRange(10, 1), OccurrenceKind.Read),
                new Occurrence(new TextRange(12, 1), OccurrenceKind.Write),
            ]);
            await Wait.UntilAsync(() => window.RenderAndCountPixels(s_write, new TextRange(0, 1)) > 0);
            window.RenderAndCountPixels(s_read, new TextRange(10, 1)).ShouldBeGreaterThan(0);
            window.RenderAndCountPixels(s_read, new TextRange(4, 1)).ShouldBe(0);

            // A caret that stays inside an occurrence keeps them, and asks nothing.
            window.Editor.Selection = new TextSelection(12);
            Advance(time, s_pause);
            fake.LinesStartingWith("documentHighlight").Count.ShouldBe(1);
            window.Editor.Occurrences.Count.ShouldBe(3);
        });

    /// <summary>
    /// Typing clears them, and so does the keyboard leaving the editor; a caret that the user moves asks again, as in VS
    /// Code.
    /// </summary>
    [Fact]
    public Task Typing_and_leaving_the_editor_clear_them_and_moving_the_caret_asks_again() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "abc abc");
            var other = new TextBox();
            var editor = new CodeEditor
            {
                Document = document,
                ColorTheme = Theme(),
                Height = 300,
            };
            using var window = new EditorWindow(editor, new StackPanel { Children = { editor, other } });
            await CompletionTests.StartServingAsync(server, document);
            await ShowAtAsync(window, time, 1, 2);

            window.Type("x");
            editor.Occurrences.ShouldBeEmpty();
            Advance(time, s_pause);
            Dispatcher.UIThread.RunJobs();
            editor.Occurrences.ShouldBeEmpty();
            fake.LinesStartingWith("documentHighlight").Count.ShouldBe(1);

            // "axbc abc": the second word.
            window.Press(Key.End);
            Advance(time, s_pause);
            await Wait.UntilAsync(() => editor.Occurrences.Count == 1);

            other.Focus();
            await Wait.UntilAsync(() => editor.Occurrences.Count == 0);
        });

    [Fact]
    public Task Without_an_answer_from_a_server_the_words_occurrences_show() =>
        _ui.Run(async () =>
        {
            // Without a server.
            using EditorWindow plain = Show(CodeDocument.Create(CodeLanguage.PlainText, "word wordy word Word"));
            plain.Editor.Selection = new TextSelection(1);
            await Wait.UntilAsync(() => plain.Editor.Occurrences.Count > 0);
            plain.Editor.Occurrences.ShouldBe([
                new Occurrence(new TextRange(0, 4), OccurrenceKind.Text),
                new Occurrence(new TextRange(11, 4), OccurrenceKind.Text),
            ]);
            await Wait.UntilAsync(() => plain.RenderAndCountPixels(s_word, new TextRange(11, 4)) > 0);

            // A server that answers null; one that answers with none shows none.
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "none none nothing nothing");
            using EditorWindow window = Show(document);
            await CompletionTests.StartServingAsync(server, document);
            await ShowAtAsync(window, time, 1, 2);
            window.Editor.Occurrences.ShouldAllBe(occurrence => occurrence.Kind == OccurrenceKind.Text);
            window.Editor.Selection = new TextSelection(11);
            Advance(time, s_pause);
            await fake.WaitForAsync("documentHighlight", 2);
            await Wait.UntilAsync(() => window.Editor.Occurrences.Count == 0);
        });

    /// <summary>
    /// As in VS Code: a selection inside a word asks too, but not one beyond it or over several lines; the option off
    /// and the delay are kept.
    /// </summary>
    [Fact]
    public Task Selections_the_option_and_the_delay_decide_as_in_VS_Code() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "abc abc\nabc");
            using EditorWindow window = Show(document);
            CodeEditor editor = window.Editor;
            await CompletionTests.StartServingAsync(server, document);

            editor.Selection = new TextSelection(1, 3);
            Advance(time, s_pause);
            await Wait.UntilAsync(() => editor.Occurrences.Count == 3);

            editor.Selection = new TextSelection(4, 7);
            editor.Selection = new TextSelection(1, 5);
            Advance(time, s_pause);
            editor.Occurrences.ShouldBeEmpty();
            editor.Selection = new TextSelection(1, 9);
            Advance(time, s_pause);
            editor.Occurrences.ShouldBeEmpty();
            fake.LinesStartingWith("documentHighlight").Count.ShouldBe(1);

            editor.OccurrencesHighlight = OccurrencesHighlight.Off;
            editor.Selection = new TextSelection(1);
            Advance(time, s_pause);
            Dispatcher.UIThread.RunJobs();
            fake.LinesStartingWith("documentHighlight").Count.ShouldBe(1);

            editor.OccurrencesHighlight = OccurrencesHighlight.SingleFile;
            editor.OccurrencesHighlightDelay = TimeSpan.FromMilliseconds(200);
            int timers = time.TimersCreated;
            editor.Selection = new TextSelection(2);
            Advance(time, s_pause);
            // The answer is in, and waits for the delay, once its timer is armed: the clock stood still meanwhile.
            await Wait.UntilAsync(() => time.HasTimerDueIn(TimeSpan.FromMilliseconds(200), timers));
            editor.Occurrences.ShouldBeEmpty();
            Advance(time, TimeSpan.FromMilliseconds(200));
            editor.Occurrences.Count.ShouldBe(3);
        });

    /// <summary>
    /// A selection that changes without the caret moving, as when only its anchor moves onto another line, no longer
    /// highlights the word at the caret.
    /// </summary>
    [Fact]
    public Task A_selection_whose_anchor_moves_onto_another_line_clears_the_highlights() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "abc abc\nabc");
            using EditorWindow window = Show(document);
            await CompletionTests.StartServingAsync(server, document);
            await ShowAtAsync(window, time, 1, 3);

            window.Editor.Selection = new TextSelection(9, 1);

            window.Editor.CaretPosition.ShouldBe(new TextPosition(1, 2));
            window.Editor.Occurrences.ShouldBeEmpty();
        });

    [Fact]
    public Task F7_and_Shift_F7_go_through_the_occurrences() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "ab ab cd ab");
            using EditorWindow window = Show(document);
            await CompletionTests.StartServingAsync(server, document);
            await ShowAtAsync(window, time, 1, 3);

            window.Press(Key.F7);
            window.Editor.Selection.ShouldBe(new TextSelection(3));
            window.Press(Key.F7);
            window.Editor.Selection.ShouldBe(new TextSelection(9));
            window.Press(Key.F7);
            window.Editor.Selection.ShouldBe(new TextSelection(0));
            window.Press(Key.F7, KeyModifiers.Shift);
            window.Editor.Selection.ShouldBe(new TextSelection(9));
            window.Editor.Occurrences.Count.ShouldBe(3);
        });

    /// <summary>Puts the caret at <paramref name="caret"/>, and waits for the occurrences there.</summary>
    private static async Task ShowAtAsync(EditorWindow window, ManualTime time, int caret, int count)
    {
        window.Editor.Selection = new TextSelection(caret);
        Advance(time, s_pause);
        await Wait.UntilAsync(() => window.Editor.Occurrences.Count == count);
    }

    private static EditorWindow Show(CodeDocument document) =>
        new(new CodeEditor { Document = document, ColorTheme = Theme() });

    /// <summary>Moves the clock on, and runs what the timers due meanwhile posted.</summary>
    private static void Advance(ManualTime time, TimeSpan span)
    {
        time.Advance(span);
        Dispatcher.UIThread.RunJobs();
    }

    private static CodeEditorColorTheme Theme() =>
        new() { Source = new Uri("avares://Darp.CodeEditor.Tests/Themes/highlights.json") };
}

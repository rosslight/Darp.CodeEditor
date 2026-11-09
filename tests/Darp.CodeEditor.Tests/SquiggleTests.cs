using Avalonia.Media;
using Avalonia.Threading;
using Darp.CodeEditor.Diagnostics;

namespace Darp.CodeEditor.Tests;

/// <summary>Diagnostics as squiggles: in the theme's colors, with the text as the user types, until they are replaced.</summary>
[Collection(UiFixture.Collection)]
public sealed class SquiggleTests(UiFixture ui)
{
    /// <summary>The colors of the test theme's marks.</summary>
    private static readonly Color s_error = Color.Parse("#FF8000");

    private static readonly Color s_warning = Color.Parse("#00C0C0");
    private static readonly Color s_information = Color.Parse("#8000FF");
    private static readonly Color s_hint = Color.Parse("#008080");

    /// <summary>VS Code's color of errors in light themes that set none, as Light+.</summary>
    private static readonly Color s_defaultError = Color.Parse("#E51400");

    /// <summary>
    /// How far a squiggle's pixels may be from its color: a wave a pixel thick is drawn smoothed, so few of its pixels
    /// have the color exactly.
    /// </summary>
    private const int Smoothed = 16;

    private readonly UiFixture _ui = ui;

    [Fact]
    public Task Each_severity_shows_in_the_themes_color_under_its_text() =>
        _ui.Run(async () =>
        {
            const string text = "an error, a warning, some information and a hint";
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document, ColorTheme = Marks() });
            (string Word, DiagnosticSeverity Severity, Color Color)[] words =
            [
                ("error", DiagnosticSeverity.Error, s_error),
                ("warning", DiagnosticSeverity.Warning, s_warning),
                ("information", DiagnosticSeverity.Information, s_information),
                ("hint", DiagnosticSeverity.Hint, s_hint),
            ];

            document.SetDiagnostics([.. words.Select(w => Diagnostic(document, Range(text, w.Word), w.Severity))]);

            await Wait.UntilAsync(() => IsUnderEach(window, s_error, Range(text, "error")));
            foreach ((string word, DiagnosticSeverity severity, Color color) in words)
            {
                TextRange range = Range(text, word);
                // A squiggle runs under each character; a hint is three dots under the first two, as in VS Code.
                if (severity == DiagnosticSeverity.Hint)
                {
                    IsUnderEach(window, color, range with { Length = 2 }).ShouldBeTrue(word);
                    window
                        .RenderAndCountPixels(color, TextRange.FromBounds(range.Start + 2, range.End), Smoothed)
                        .ShouldBe(0, word);
                }
                else
                    IsUnderEach(window, color, range).ShouldBeTrue(word);
                // Under its own text only.
                window.RenderAndCountPixels(color, Range(text, "an "), Smoothed).ShouldBe(0, word);
            }
        });

    [Fact]
    public Task A_theme_without_squiggle_colors_shows_VS_Codes_colors() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "an error");
            using var window = new EditorWindow(new CodeEditor { Document = document });

            document.SetDiagnostics([Diagnostic(document, new TextRange(3, 5), DiagnosticSeverity.Error)]);

            await Wait.UntilAsync(() => IsUnderEach(window, s_defaultError, new TextRange(3, 5)));
        });

    [Fact]
    public Task Squiggles_move_with_the_text_and_go_when_the_diagnostics_clear() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "let value = 1");
            using var window = new EditorWindow(new CodeEditor { Document = document, ColorTheme = Marks() });
            // About the text before the edit: the editor maps it to the text as it is when the diagnostics arrive.
            TextSnapshot analyzed = document.CurrentSnapshot;
            window.Type("x");
            document.SetDiagnostics([Diagnostic(document, new TextRange(4, 5), DiagnosticSeverity.Error, analyzed)]);
            await Wait.UntilAsync(() => IsUnderEach(window, s_error, new TextRange(5, 5)));

            // Typed at either edge, text stays outside.
            window.Editor.Selection = new TextSelection(5);
            window.Type("yy");
            window.Editor.Selection = new TextSelection(12);
            window.Type("z");

            // "xlet yyvaluez = 1"
            IsUnderEach(window, s_error, new TextRange(7, 5)).ShouldBeTrue();
            window.RenderAndCountPixels(s_error, new TextRange(0, 7), Smoothed).ShouldBe(0);
            window.RenderAndCountPixels(s_error, new TextRange(12, 5), Smoothed).ShouldBe(0);

            document.SetDiagnostics([]);

            await Wait.UntilAsync(() => window.RenderAndCountPixels(s_error, Smoothed) == 0);
        });

    [Fact]
    public Task Squiggles_stay_on_their_text_through_edits_while_they_arrive_and_while_no_editor_shows_them() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "let value = 1");
            document.SetDiagnostics([Diagnostic(document, new TextRange(4, 5), DiagnosticSeverity.Error)]);
            // Before the UI thread takes the diagnostics in, and after.
            document.Replace(new TextRange(0, 0), "xx");
            Dispatcher.UIThread.RunJobs();
            document.Replace(new TextRange(0, 0), "y");

            using var window = new EditorWindow(new CodeEditor { Document = document, ColorTheme = Marks() });

            // "yxxlet value = 1"
            await Wait.UntilAsync(() => IsUnderEach(window, s_error, new TextRange(7, 5)));
            window.RenderAndCountPixels(s_error, new TextRange(0, 7), Smoothed).ShouldBe(0);
        });

    [Fact]
    public Task Squiggles_of_one_severity_that_overlap_all_show() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "an error here");
            using var window = new EditorWindow(new CodeEditor { Document = document, ColorTheme = Marks() });

            // An empty one inside another, and two on the same text: shapes that cover each other.
            document.SetDiagnostics([
                Diagnostic(document, new TextRange(3, 5), DiagnosticSeverity.Error),
                Diagnostic(document, new TextRange(5, 0), DiagnosticSeverity.Error),
                Diagnostic(document, new TextRange(9, 4), DiagnosticSeverity.Error),
                Diagnostic(document, new TextRange(9, 4), DiagnosticSeverity.Error),
            ]);

            await Wait.UntilAsync(() => IsUnderEach(window, s_error, new TextRange(3, 5)));
            IsUnderEach(window, s_error, new TextRange(9, 4)).ShouldBeTrue();
        });

    [Fact]
    public Task Overlapping_squiggles_cost_a_frame_what_one_squiggle_costs() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "a word here");
            using var window = new EditorWindow(new CodeEditor { Document = document, ColorTheme = Marks() });

            long one = FrameAllocations(window, document, Errors(document, 1));
            long many = FrameAllocations(window, document, Errors(document, 10_000));

            // Their ranges merge before any shape is made; AvaloniaEdit's search still lists each, a few bytes each.
            // Drawn one by one, they took 9.7 MB more.
            (many - one).ShouldBeLessThan(1_000_000);
            IsUnderEach(window, s_error, new TextRange(2, 4)).ShouldBeTrue();
        });

    [Fact]
    public Task Diagnostics_in_a_long_lines_hidden_tail_cost_a_frame_nothing() =>
        _ui.Run(() =>
        {
            // The editor shows the first 3,000 characters of the line, wrapped, and "..." for the rest.
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, new string('x', 20_000));
            using var window = new EditorWindow(
                new CodeEditor
                {
                    Document = document,
                    WordWrap = true,
                    ColorTheme = Marks(),
                },
                height: 900
            );
            IReadOnlyList<Diagnostic> hidden =
            [
                .. Enumerable
                    .Range(4_000, 10_000)
                    .Select(offset => Diagnostic(document, new TextRange(offset, 1), DiagnosticSeverity.Hint)),
            ];

            long none = FrameAllocations(window, document, []);
            long many = FrameAllocations(window, document, hidden);

            // Drawn one by one, at the "..." they all map to, they took about 4 MB.
            (many - none).ShouldBeLessThan(1_000_000);
        });

    [Fact]
    public Task Editors_take_the_10000_most_severe_of_a_documents_diagnostics() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "a word here");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            TextSnapshot snapshot = document.CurrentSnapshot;
            // 100,000 on one word, their severities taking turns from hints to errors: 25,000 errors.
            document.SetDiagnostics([
                .. Enumerable
                    .Range(0, 100_000)
                    .Select(i => new Diagnostic(
                        snapshot,
                        new TextRange(2, 4),
                        (DiagnosticSeverity)(i % 4),
                        null,
                        $"{i}"
                    )),
            ]);
            window.Layout();
            using var squiggles = new SquiggleLayer(EditorWindow.PartOf(editor).TextArea, document);

            IReadOnlyList<Diagnostic> taken = squiggles.At(3, 20_000);

            // The document keeps them all; its editors the first 10,000 errors.
            document.Diagnostics.Count.ShouldBe(100_000);
            taken.Count.ShouldBe(DiagnosticsBatch.MaxShown);
            taken.ShouldAllBe(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            taken[^1].Message.ShouldBe($"{4 * (DiagnosticsBatch.MaxShown - 1) + 3}");
        });

    [Fact]
    public Task Diagnostics_from_a_servers_thread_cost_the_UI_thread_nothing_each() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "a word here");
            IReadOnlyList<Diagnostic> many = Errors(document, 100_000);
            // As a server's arrive: chosen, and put in the text, on its thread.
            Task.Run(() => document.SetDiagnostics(many)).Wait();

            long before = GC.GetAllocatedBytesForCurrentThread();
            Dispatcher.UIThread.RunJobs();
            long taken = GC.GetAllocatedBytesForCurrentThread() - before;

            document.Diagnostics.Count.ShouldBe(100_000);
            document.ShownDiagnostics.Count.ShouldBe(DiagnosticsBatch.MaxShown);
            // Put in the text on this thread, the 10,000 shown took 0.9 MB.
            taken.ShouldBeLessThan(100_000);
        });

    private static IReadOnlyList<Diagnostic> Errors(CodeDocument document, int count) =>
        [
            .. Enumerable
                .Range(0, count)
                .Select(_ => Diagnostic(document, new TextRange(2, 4), DiagnosticSeverity.Error)),
        ];

    /// <summary>
    /// What a frame allocates on the UI thread with <paramref name="diagnostics"/>, the squiggles drawing again in it.
    /// </summary>
    private static long FrameAllocations(
        EditorWindow window,
        CodeDocument document,
        IReadOnlyList<Diagnostic> diagnostics
    )
    {
        document.SetDiagnostics(diagnostics);
        window.Render();
        // Lines built again draw the layers again; the first frame of them also builds what later ones reuse.
        window.TextView.Redraw();
        window.Render();
        window.TextView.Redraw();
        long before = GC.GetAllocatedBytesForCurrentThread();
        window.Render();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Whether a frame shows <paramref name="color"/> under each character of <paramref name="range"/>.</summary>
    private static bool IsUnderEach(EditorWindow window, Color color, TextRange range) =>
        Enumerable
            .Range(range.Start, range.Length)
            .All(offset => window.RenderAndCountPixels(color, new TextRange(offset, 1), Smoothed) > 0);

    private static CodeEditorColorTheme Marks() =>
        new() { Source = new Uri("avares://Darp.CodeEditor.Tests/Themes/marks.json") };

    private static TextRange Range(string text, string word) =>
        new(text.IndexOf(word, StringComparison.Ordinal), word.Length);

    private static Diagnostic Diagnostic(
        CodeDocument document,
        TextRange range,
        DiagnosticSeverity severity,
        TextSnapshot? snapshot = null
    ) => new(snapshot ?? document.CurrentSnapshot, range, severity, null, severity.ToString());
}

using Avalonia.Media;

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

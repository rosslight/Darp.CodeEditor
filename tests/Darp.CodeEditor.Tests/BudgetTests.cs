using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The budgets of the design's section 1.2 that need no language server: highlighted text within 100 ms of creating
/// the editor, and no UI-thread stall over 50 ms. Both are measured once the process has run the editor's code, so
/// that the JIT compiler's first calls do not count; NativeAOT has none.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class BudgetTests(UiFixture ui)
{
    private const int Lines = 5000;

    private static readonly TimeSpan s_highlightingBudget = TimeSpan.FromMilliseconds(100);

    private readonly UiFixture _ui = ui;

    /// <summary>The languages of the highlighting budget.</summary>
    public static TheoryData<string> Languages => ["json", "yaml"];

    [Theory]
    [MemberData(nameof(Languages))]
    public Task Text_shows_syntax_colors_within_100_ms_of_creating_the_editor(string language) =>
        _ui.Run(async () =>
        {
            (CodeLanguage codeLanguage, string text, Color syntax) = Sample(language);
            // As in an app that created its languages and themes at startup.
            await codeLanguage.Loaded;
            await CodeEditorColorTheme.LightPlus.Loaded;
            TimeSpan first = await FirstColoredFrame(codeLanguage, text, syntax);

            // The median of five editors, each of a new document: one delay of a busy machine does not decide it.
            var samples = new List<TimeSpan>();
            for (int i = 0; i < 5; i++)
                samples.Add(await FirstColoredFrame(codeLanguage, text, syntax));
            TimeSpan median = samples.Order().ElementAt(samples.Count / 2);

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{language}: syntax colors {median.TotalMilliseconds:F1} ms after creating the editor, the median of "
                    + $"{string.Join(", ", samples.Select(sample => sample.TotalMilliseconds.ToString("F1")))} ms "
                    + $"(the test's first editor: {first.TotalMilliseconds:F1} ms)"
            );
            median.ShouldBeLessThan(s_highlightingBudget);
        });

    [Fact]
    public Task The_UI_thread_never_stalls_over_50_ms() =>
        _ui.Run(async () =>
        {
            // Once without the probe, so that the JIT compiler's first calls do not count.
            await RunScenarios(probe: null);
            using var probe = new UiStallProbe(Dispatcher.UIThread, TimeSpan.FromMilliseconds(1));

            IReadOnlyList<StallReport> reports = await RunScenarios(probe);

            foreach (StallReport report in reports)
                TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
            reports.ShouldAllBe(report => report.MaxMs < StallReport.FailureMs);
        });

    /// <summary>
    /// Loading, typing and scrolling 5,000 lines of JSON, and typing on lines of 20,000 characters: one that tokenizes
    /// quickly, and one that runs out of time.
    /// </summary>
    private static async Task<IReadOnlyList<StallReport>> RunScenarios(UiStallProbe? probe)
    {
        var reports = new List<StallReport>();
        CodeDocument document = CodeDocument.Create(CodeLanguage.Json, Texts.Json(Lines));

        probe?.BeginPhase("loading 5,000 lines");
        using var window = new EditorWindow(new CodeEditor { Document = document });
        await Wait.UntilAsync(() => window.RenderedColors().Count > 2);
        await Task.Delay(300);
        End();

        probe?.BeginPhase("typing in 5,000 lines");
        window.Editor.Selection = new TextSelection(document.CurrentSnapshot.GetOffset(new TextPosition(20, 3)));
        foreach (char c in "\"typed\": [1, 2, \"three\"], /* comment */ ")
        {
            window.Type(c.ToString());
            await Task.Delay(16);
        }
        await Task.Delay(300);
        End();

        probe?.BeginPhase("scrolling 5,000 lines");
        TextSnapshot text = document.CurrentSnapshot;
        for (int line = 1; line <= Lines; line += 40)
        {
            window.Editor.ScrollTo(new TextRange(text.GetOffset(new TextPosition(line, 1)), 0));
            await Task.Delay(16);
        }
        await Task.Delay(300);
        End();

        // A string tokenizes in about a millisecond; an array takes seconds, so the background stops at its time limit.
        (string Kind, string Value)[] longValues =
        [
            ("string", "\"" + string.Concat(Enumerable.Repeat("0123456789", 2_000)) + "\""),
            ("array", "[" + string.Concat(Enumerable.Repeat("1, ", 6_666)) + "1]"),
        ];
        foreach ((string kind, string value) in longValues)
        {
            probe?.BeginPhase($"typing on a 20,000-character {kind}");
            CodeDocument longLine = CodeDocument.Create(
                CodeLanguage.Json,
                $"{{\n  \"long\": {value},\n  \"after\": 1\n}}"
            );
            window.Editor.Document = longLine;
            await Task.Delay(300);
            window.Editor.Selection = new TextSelection(
                longLine.CurrentSnapshot.GetOffset(new TextPosition(2, 10_000))
            );
            foreach (char c in "1, ")
            {
                window.Type(c.ToString());
                await Task.Delay(16);
            }
            await Task.Delay(300);
            End();
        }
        return reports;

        void End()
        {
            if (probe is not null)
                reports.Add(probe.EndPhase());
        }
    }

    /// <summary>
    /// Creates and shows an editor of a new document of <paramref name="text"/>, renders frames, and returns how long
    /// after creating the editor a frame first showed the <paramref name="syntax"/> color in its pixels.
    /// </summary>
    private static async Task<TimeSpan> FirstColoredFrame(CodeLanguage language, string text, Color syntax)
    {
        CodeDocument document = CodeDocument.Create(language, text);
        long start = Stopwatch.GetTimestamp();
        using var window = new EditorWindow(new CodeEditor { Document = document });
        while (true)
        {
            WriteableBitmap frame = window.Render();
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
            // Some glyphs' worth of pixels: anti-aliasing blends the edges of the rest.
            if (EditorWindow.CountPixels(frame, syntax) > 100)
                return elapsed;
            elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), $"No {language.Id} colors yet.");
            await Task.Delay(1);
        }
    }

    private static (CodeLanguage Language, string Text, Color Syntax) Sample(string language) =>
        language switch
        {
            // Property names, and YAML's keys.
            "json" => (CodeLanguage.Json, Texts.Json(Lines), Color.Parse("#0451A5")),
            _ => (CodeLanguage.Yaml, Texts.Yaml(Lines), Color.Parse("#800000")),
        };
}

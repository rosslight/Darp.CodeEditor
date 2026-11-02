using Avalonia.Controls;
using AvaloniaEdit.Search;
using Darp.CodeEditor.Instrumentation;

namespace Darp.CodeEditor.Samples;

/// <summary>A window that can show an editor for a measurement.</summary>
internal interface IBenchHost
{
    TopLevel TopLevel { get; }

    /// <summary>Shows the editor in place of the current content and returns once it is laid out.</summary>
    Task ShowAsync(CodeEditor editor);
}

/// <summary>Scenarios that need no language service. They run with and without TextMate to attribute stalls.</summary>
internal static class EditorScenarios
{
    private const int TypingDelayMs = 40;

    public static async Task RunHighlightingAsync(IBenchHost host, UiStallProbe probe, BenchReport report)
    {
        string large = SampleDocuments.LargeCSharpFile(10_000);
        string longLine = SampleDocuments.LongLineFile(20_000);
        (string Label, string? LanguageId, bool Background)[] modes =
        [
            ("plain", null, true),
            ("textmate stock (UI thread)", "csharp", false),
            ("textmate background", "csharp", true),
        ];
        foreach ((string label, string? languageId, bool background) in modes)
        {
            bool highlighted = languageId is not null;

            probe.BeginPhase($"{label}: open 10k lines");
            double start = ProcessClock.ElapsedMs;
            var editor = new CodeEditor { UseBackgroundTokenization = background };
            using var timings = new TimingLog(editor);
            editor.LanguageId = languageId;
            editor.Text = large;
            await host.ShowAsync(editor);
            double visible = await Ui.WaitForVisibleAsync(editor, highlighted);
            await Task.Delay(500);
            report.Add(probe.EndPhase());
            report.Add(
                $"  {label}: {editor.Document.LineCount} lines visible{(highlighted ? " and highlighted" : "")} after {visible - start:F0} ms; {timings.Summary()}"
            );

            probe.BeginPhase($"{label}: scroll 10k lines");
            int frames = 0;
            int framesWithUncoloredLines = 0;
            int uncoloredLines = 0;
            for (int line = 1; line <= editor.Document.LineCount; line += 40)
            {
                Task layout = Ui.NextLayoutAsync(editor);
                editor.ScrollToLine(line);
                // Near the end the view no longer scrolls, so there may be no new layout.
                await Task.WhenAny(layout, Task.Delay(100));
                frames++;
                int pending = editor.VisibleLinesWithoutHighlighting;
                await Ui.NextFrameAsync(host.TopLevel);
                uncoloredLines += pending;
                if (pending > 0)
                    framesWithUncoloredLines++;
            }
            await Task.Delay(300);
            report.Add(probe.EndPhase());
            if (background && highlighted)
                report.Add(
                    $"  scrolling a page per frame: {framesWithUncoloredLines} of {frames} frames showed lines without colors ({uncoloredLines} line-frames)"
                );

            probe.BeginPhase($"{label}: 20k-char line");
            editor.Text = longLine;
            editor.Select(editor.Document.GetLineByNumber(2).EndOffset, 0);
            editor.TextArea.Caret.BringCaretToView();
            await Task.Delay(300);
            await Ui.TypeAsync(editor, " /*x*/", TypingDelayMs);
            await Task.Delay(300);
            report.Add(probe.EndPhase());
            report.Add("  " + Ui.TakeKeystrokeSummary());

            probe.BeginPhase($"{label}: typing burst");
            editor.Text = SampleDocuments.CSharpScript;
            editor.Select(editor.Document.TextLength, 0);
            editor.TextArea.Caret.BringCaretToView();
            await Ui.TypeAsync(editor, SampleDocuments.TypingBurst, TypingDelayMs);
            await Task.Delay(300);
            report.Add(probe.EndPhase());
            report.Add("  " + Ui.TakeKeystrokeSummary());
        }
    }

    /// <summary>
    /// Finds, replaces and undoes through the search panel (case-insensitive, its default); leaves the panel open on
    /// the pattern.
    /// </summary>
    public static string CheckSearch(CodeEditor editor, string pattern, string replacement)
    {
        string original = editor.Text;
        SearchPanel panel = editor.SearchPanel;
        panel.Open();
        panel.MatchCase = false;
        panel.SearchPattern = pattern;
        editor.Select(0, 0);
        panel.FindNext();
        bool found = string.Equals(editor.SelectedText, pattern, StringComparison.OrdinalIgnoreCase);
        int matches = Count(original, pattern);

        panel.IsReplaceMode = true;
        panel.ReplacePattern = replacement;
        panel.ReplaceAll();
        bool replaced = Count(editor.Text, pattern) == 0 && Count(editor.Text, replacement) >= matches;
        editor.Document.UndoStack.Undo();
        bool undone = editor.Text == original;

        panel.IsReplaceMode = false;
        panel.SearchPattern = pattern;
        bool passed = found && matches > 0 && replaced && undone;
        return $"search \"{pattern}\": {(passed ? "PASS" : "FAIL")} (found {found}, matches {matches}, replace all {replaced}, undo {undone})";
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        for (
            int i = text.IndexOf(value, StringComparison.OrdinalIgnoreCase);
            i >= 0;
            i = text.IndexOf(value, i + value.Length, StringComparison.OrdinalIgnoreCase)
        )
            count++;
        return count;
    }
}

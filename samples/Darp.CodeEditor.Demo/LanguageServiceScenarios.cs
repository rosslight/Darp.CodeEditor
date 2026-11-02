using Darp.CodeEditor.Instrumentation;
using Darp.CodeEditor.Samples;

namespace Darp.CodeEditor.Demo;

/// <summary>Scenarios with the C# language service: completion latency and a typing burst.</summary>
internal static class LanguageServiceScenarios
{
    private const int TypingDelayMs = 40;
    private static readonly TimeSpan s_completionTimeout = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(MainWindow window, CodeEditor editor, UiStallProbe probe, BenchReport report)
    {
        await window.ShowTabAsync(0);
        using var timings = new TimingLog(editor);

        // One session at a time, so each latency is one request: an identifier start and a member access.
        probe.BeginPhase("C#: completion latency");
        var latencies = new List<(string Trigger, double Ms, int Items)>();
        editor.Select(editor.Document.TextLength, 0);
        for (int i = 0; i < 20; i++)
        {
            (double wordMs, int wordItems) = await TypeAndWaitAsync(editor, timings, "\nD");
            latencies.Add(("word", wordMs, wordItems));
            await Ui.TypeAsync(editor, "evice", TypingDelayMs);
            (double memberMs, int memberItems) = await TypeAndWaitAsync(editor, timings, ".");
            latencies.Add(("member", memberMs, memberItems));
            await Ui.TypeAsync(editor, "ReadTemp();", TypingDelayMs);
        }
        await Task.Delay(500);
        report.Add(probe.EndPhase());
        report.Add("  " + Ui.TakeKeystrokeSummary());
        report.Add($"  first completion: {latencies[0].Ms:F0} ms ({latencies[0].Items} items)");
        foreach (string trigger in new[] { "word", "member" })
        {
            double[] warm = [.. latencies.Skip(2).Where(l => l.Trigger == trigger).Select(l => l.Ms).Order()];
            int items = latencies.Last(l => l.Trigger == trigger).Items;
            report.Add(
                $"  warm completion ({trigger}, {items} items): p50 {Percentile(warm, 0.5):F0} ms, p95 {Percentile(warm, 0.95):F0} ms, max {warm[^1]:F0} ms (n={warm.Length})"
            );
        }
        report.Add($"  timings: {timings.Summary()}");

        // A fast typist does not wait: sessions start, filter, commit and get cancelled continuously.
        var burst = new TimingLog(editor);
        probe.BeginPhase("C#: typing burst with completion");
        await Ui.TypeAsync(editor, SampleDocuments.TypingBurst + SampleDocuments.TypingBurst, TypingDelayMs);
        await Task.Delay(1000);
        report.Add(probe.EndPhase());
        report.Add("  " + Ui.TakeKeystrokeSummary());
        report.Add($"  timings: {burst.Summary()}");
        burst.Dispose();
    }

    private static async Task<(double Ms, int Items)> TypeAndWaitAsync(
        CodeEditor editor,
        TimingLog timings,
        string text
    )
    {
        await Ui.TypeAsync(editor, text[..^1], TypingDelayMs);
        Task<EditorTiming> result = timings.NextAsync(t =>
            t.Name is "completion" or "completion-empty" or "completion-dropped"
        );
        editor.TextArea.PerformTextInput(text[^1].ToString());
        EditorTiming timing = await result.WaitAsync(s_completionTimeout);
        if (timing.Name != "completion")
        {
            int caret = editor.CaretOffset;
            string context = editor.Document.GetText(Math.Max(0, caret - 60), Math.Min(60, caret));
            throw new InvalidOperationException(
                $"Expected a completion list after \"{text}\", got {timing.Name}. Text before the caret: \"{context}\", selection {editor.SelectionLength}"
            );
        }
        return (timing.Duration.TotalMilliseconds, timing.Count);
    }

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[(int)Math.Min(sorted.Length - 1, Math.Ceiling(percentile * sorted.Length) - 1)];
}

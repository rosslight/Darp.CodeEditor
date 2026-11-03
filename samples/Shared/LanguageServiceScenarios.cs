using Darp.CodeEditor.Instrumentation;

namespace Darp.CodeEditor.Samples;

/// <summary>What one round of the completion latency scenario types, in a language with completion.</summary>
/// <param name="WordStart">Ends with the first letter of a word, which asks for a word list.</param>
/// <param name="MemberAccess">Asks for a member list after the word.</param>
internal sealed record CompletionRound(
    string Language,
    string WordStart,
    string WordRest,
    string MemberAccess,
    string LineRest
)
{
    public static CompletionRound CSharp { get; } = new("C#", "\nD", "evice", ".", "ReadTemp();");

    public static CompletionRound Luau { get; } = new("Luau", "\nd", "evice", ":", "readTemperature()");
}

/// <summary>Scenarios with a language service: completion latency and, for C#, a typing burst.</summary>
internal static class LanguageServiceScenarios
{
    private const int TypingDelayMs = 40;
    private static readonly TimeSpan s_completionTimeout = TimeSpan.FromSeconds(10);

    /// <param name="tab">The tab of the C# editor.</param>
    public static async Task RunAsync(
        IBenchHost host,
        int tab,
        CodeEditor editor,
        UiStallProbe probe,
        BenchReport report
    )
    {
        await MeasureCompletionAsync(host, tab, editor, probe, report, CompletionRound.CSharp);

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

    /// <summary>
    /// One completion session at a time, so each latency is one request: a word start and a member access, 20 times.
    /// </summary>
    /// <param name="tab">The tab of the editor.</param>
    public static async Task MeasureCompletionAsync(
        IBenchHost host,
        int tab,
        CodeEditor editor,
        UiStallProbe probe,
        BenchReport report,
        CompletionRound round
    )
    {
        await host.ShowTabAsync(tab);
        using var timings = new TimingLog(editor);
        probe.BeginPhase($"{round.Language}: completion latency");
        var latencies = new List<(string Trigger, double Ms, int Items)>();
        editor.Select(editor.Document.TextLength, 0);
        for (int i = 0; i < 20; i++)
        {
            (double wordMs, int wordItems) = await TypeAndWaitAsync(editor, timings, round.WordStart);
            latencies.Add(("word", wordMs, wordItems));
            await Ui.TypeAsync(editor, round.WordRest, TypingDelayMs);
            (double memberMs, int memberItems) = await TypeAndWaitAsync(editor, timings, round.MemberAccess);
            latencies.Add(("member", memberMs, memberItems));
            await Ui.TypeAsync(editor, round.LineRest, TypingDelayMs);
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

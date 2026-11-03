using System.Runtime;
using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using Darp.CodeEditor.Instrumentation;
using Darp.CodeEditor.Samples;

namespace Darp.CodeEditor.Demo.Aot;

/// <summary>Runs the --smoke and --bench modes.</summary>
internal static class DemoRun
{
    public static async void Start(
        MainWindow window,
        IClassicDesktopStyleApplicationLifetime desktop,
        UiStallProbe? probe,
        DemoOptions options
    )
    {
        var report = new BenchReport(options.OutputPath);
        int exitCode = 0;
        try
        {
            if (probe is null)
            {
                // Scene for a screenshot: highlighting with the Luau grammar and the search panel open.
                await Ui.WaitForVisibleAsync(window.LuauEditor, highlighted: true);
                EditorScenarios.CheckSearch(window.LuauEditor, "reading", "sample");
                return;
            }
            await MeasureAsync(window, probe, options, report);
        }
        catch (Exception exception)
        {
            report.Add($"FAILED: {exception}");
            exitCode = 1;
        }
        report.Save();
        probe?.Dispose();
        if (!options.Hold)
            desktop.Shutdown(exitCode);
    }

    private static async Task MeasureAsync(
        MainWindow window,
        UiStallProbe probe,
        DemoOptions options,
        BenchReport report
    )
    {
        report.Add(
            $"NativeAOT demo (dynamic code supported: {System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}), "
                + $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} cores, "
                + $"server GC {GCSettings.IsServerGC}"
        );
        CodeEditor editor = window.LuauEditor;
        using var timings = new TimingLog(editor);
        using var csharpTimings = new TimingLog(window.CSharpEditor);
        window.CSharpEditor.LanguageServiceFailed += (_, exception) =>
            report.Add($"LANGUAGE SERVICE FAILED: {exception}");
        Task<double> visible = Ui.WaitForVisibleAsync(editor, highlighted: false);
        Task<double> highlighted = Ui.WaitForVisibleAsync(editor, highlighted: true);
        CSharpBench? csharp = window.CSharp is { } service ? new CSharpBench(service, csharpTimings) : null;
        await highlighted;
        report.Add(probe.EndPhase());
        probe.BeginPhase("startup: until C# ready (C# tab in the background) + 500 ms");
        if (csharp is not null)
            await csharp.WaitUntilReadyAsync();
        await Task.Delay(500);
        report.Add(probe.EndPhase());
        double created = window.EditorsCreatedMs;
        report.Add($"  editors created:            {created, 7:F0} ms after process start");
        report.Add(
            $"  editor text visible:        {await visible, 7:F0} ms after process start, {await visible - created:F0} ms after editor creation"
        );
        report.Add(
            $"  editor text highlighted:    {await highlighted, 7:F0} ms after process start, {await highlighted - created:F0} ms after editor creation"
        );
        if (csharp is not null)
        {
            await csharp.ReportAsync(report, created);
            report.Add($"  idle {csharp.DescribeMemory()}");
        }
        report.Add($"  startup timings: {timings.Summary()}");

        report.Add(EditorScenarios.CheckSearch(editor, "reading", "sample"));
        await window.ShowTabAsync(1);
        CodeEditor json = window.JsonEditor;
        using var jsonTimings = new TimingLog(json);
        Task<double> diagnosed = jsonTimings.WhenFirstAsync("diagnostics");
        report.Add(EditorScenarios.CheckSearch(json, "temperature", "heat"));
        await diagnosed.WaitAsync(TimeSpan.FromSeconds(5));
        report.Add($"  JSON diagnostics shown (trailing comma squiggle): {jsonTimings.Summary()}");

        if (!options.Bench)
            return;

        probe.BeginPhase("JSON: typing burst with word completion");
        json.Select(json.Document.GetLineByNumber(4).EndOffset, 0);
        await Ui.TypeAsync(json, "\n    \"serial\": \"sensor-temperature-7\", \"enabled\": false,", 40);
        await Task.Delay(500);
        report.Add(probe.EndPhase());
        report.Add("  " + Ui.TakeKeystrokeSummary());
        report.Add($"  timings: {jsonTimings.Summary()}");

        if (csharp is not null)
        {
            await LanguageServiceScenarios.RunAsync(window, MainWindow.CSharpTab, window.CSharpEditor, probe, report);
            report.Add($"  after typing, {csharp.DescribeMemory()}");
        }
        if (!options.SkipHighlightingBench)
            await EditorScenarios.RunHighlightingAsync(window, probe, report);
    }
}

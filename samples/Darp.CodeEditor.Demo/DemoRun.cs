using System.Runtime;
using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using Darp.CodeEditor.CSharp;
using Darp.CodeEditor.Instrumentation;
using Darp.CodeEditor.Samples;

namespace Darp.CodeEditor.Demo;

/// <summary>Runs the --smoke, --bench and --scene modes.</summary>
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
            if (options.E2eOutput is { } e2eOutput)
            {
                // A C# document without language features: the input latency probe types at the end of line 1.
                await window.ShowTabAsync(0);
                await Ui.WaitForVisibleAsync(window.CSharpEditor, highlighted: true);
                await Task.Delay(500);
                Ui.WriteE2eTarget(window.CSharpEditor, e2eOutput);
                return;
            }
            if (probe is null)
                await ShowSceneAsync(window);
            else
                await MeasureAsync(window, probe, options, report);
        }
        catch (Exception exception)
        {
            report.Add($"FAILED: {exception}");
            exitCode = 1;
        }
        report.Save();
        probe?.Dispose();
        if (!options.Hold && !options.Scene && options.E2eOutput is null)
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
            $"JIT demo, {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, "
                + $"{Environment.ProcessorCount} cores, server GC {GCSettings.IsServerGC}, concurrent GC {GCSettings.LatencyMode}"
        );
        CodeEditor editor = window.CSharpEditor;
        editor.LanguageServiceFailed += (_, exception) => report.Add($"LANGUAGE SERVICE FAILED: {exception}");
        using var timings = new TimingLog(editor);
        // Subscribe to every milestone before the first frame.
        Task<double> visible = Ui.WaitForVisibleAsync(editor, highlighted: false);
        Task<double> highlighted = Ui.WaitForVisibleAsync(editor, highlighted: true);
        Task<int> uncoloredOnFirstFrame = Ui.LinesWithoutColorsOnFirstFrameAsync(editor);
        Task<double> classified = timings.WhenFirstAsync("classification");
        Task<double> diagnosed = timings.WhenFirstAsync("diagnostics");

        await highlighted;
        report.Add(probe.EndPhase());

        probe.BeginPhase("startup: until C# ready + semantic colors");
        CSharpScriptLanguageService? service = await window.CSharpReady;
        double ready = ProcessClock.ElapsedMs;
        if (service is not null)
            await Task.WhenAll(classified, diagnosed);
        await Task.Delay(500);
        report.Add(probe.EndPhase());

        report.Add($"  editor text visible:        {await visible, 7:F0} ms after process start");
        report.Add($"  editor text highlighted:    {await highlighted, 7:F0} ms");
        report.Add($"  lines without colors on the first frame: {await uncoloredOnFirstFrame}");
        if (service is not null)
        {
            report.Add($"  C# service ready:           {ready, 7:F0} ms");
            report.Add($"  semantic colors shown:      {await classified, 7:F0} ms");
            report.Add($"  diagnostics shown:          {await diagnosed, 7:F0} ms");
        }
        report.Add($"  startup timings: {timings.Summary()}");

        await window.ShowTabAsync(2);
        report.Add(EditorScenarios.CheckSearch(window.JsonEditor, "temperature", "heat"));
        await window.ShowTabAsync(0);
        report.Add(EditorScenarios.CheckSearch(editor, "Device", "Sensor"));

        if (!options.Bench)
            return;
        await EditorScenarios.RunThemeSwitchAsync(window, editor, probe, report);
        if (service is not null)
            await LanguageServiceScenarios.RunAsync(window, editor, probe, report);
        if (!options.SkipHighlightingBench)
            await EditorScenarios.RunHighlightingAsync(window, probe, report);
    }

    /// <summary>Opens completion with its description and leaves the window open for a screenshot.</summary>
    private static async Task ShowSceneAsync(MainWindow window)
    {
        if (await window.CSharpReady is null)
            return;
        CodeEditor editor = window.CSharpEditor;
        using var timings = new TimingLog(editor);
        await timings.WhenFirstAsync("diagnostics");
        editor.Select(editor.Document.TextLength, 0);
        editor.ScrollToLine(1);
        Task<double> completion = timings.WhenFirstAsync("completion");
        await Ui.TypeAsync(editor, "Device.", 60);
        await completion;
        await Ui.TypeAsync(editor, "Re", 150);
    }
}

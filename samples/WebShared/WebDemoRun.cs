using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Darp.CodeEditor.Instrumentation;
using Darp.CodeEditor.Samples;
using Darp.CodeEditor.Web;

namespace Darp.CodeEditor.WebDemo;

/// <summary>What differs between the JIT and the NativeAOT WebView demo.</summary>
/// <param name="ServiceReady">Completes with ms after process start when the language service is ready, if any.</param>
/// <param name="SearchChecks">Tabs to check search and replace on, in order.</param>
/// <param name="LanguageServiceScenarios">The --bench scenarios that need a language server.</param>
/// <param name="E2eTab">The tab whose first line --e2e reports: a C# document without a language server.</param>
/// <param name="DiagnosticsTab">A tab whose first diagnostics --smoke waits for, if any.</param>
internal sealed record WebDemoProfile(
    string Name,
    Task<double>? ServiceReady,
    IReadOnlyList<(int Tab, string Pattern, string Replacement)> SearchChecks,
    Func<WebDemoWindow, UiStallProbe, BenchReport, Task>? LanguageServiceScenarios,
    int E2eTab,
    int? DiagnosticsTab = null
);

/// <summary>The startup milestones of one editor; created before its page loads.</summary>
internal sealed class StartupMilestones
{
    public StartupMilestones(WebCodeEditor editor)
    {
        Editor = editor;
        Created = ProcessClock.ElapsedMs;
        editor.WebView.AdapterCreated += (_, _) => AdapterCreated = ProcessClock.ElapsedMs;
        Ready = PageCommands.MilestoneAsync(editor, "ready");
        Visible = PageCommands.MilestoneAsync(editor, "visible");
        Highlighted = PageCommands.MilestoneAsync(editor, "highlighted");
        LspReady = PageCommands.MilestoneAsync(editor, "lspReady");
        Diagnostics = PageCommands.MilestoneAsync(editor, "diagnosticsShown");
        Semantic = PageCommands.MilestoneAsync(editor, "semanticShown");
        PageInfo = PageCommands.NextEventAsync(editor, "pageInfo");
    }

    public WebCodeEditor Editor { get; }
    public double Created { get; }
    public double AdapterCreated { get; private set; }
    public Task<double> Ready { get; }
    public Task<double> Visible { get; }
    public Task<double> Highlighted { get; }
    public Task<double> LspReady { get; }
    public Task<double> Diagnostics { get; }
    public Task<double> Semantic { get; }
    public Task<WebEditorEvent> PageInfo { get; }
}

/// <summary>Runs the --smoke, --bench, --e2e, --close-after and --webview-info modes of the WebView demos.</summary>
internal static class WebDemoRun
{
    public const int TypingDelayMs = 40;

    public static async void Start(
        WebDemoWindow window,
        IClassicDesktopStyleApplicationLifetime desktop,
        UiStallProbe? probe,
        WebDemoOptions options,
        WebDemoProfile profile,
        BenchReport report
    )
    {
        int exitCode = 0;
        try
        {
            if (options.CloseAfterMs is { } closeAfter)
            {
                await Task.Delay(closeAfter);
                Console.WriteLine($"closing the window {ProcessClock.ElapsedMs:F0} ms after process start");
                window.Close();
                return;
            }
            if (options.WebViewInfo)
                await PrintWebViewInfoAsync(window, report);
            else if (options.Demo.E2eOutput is { } e2eOutput)
                await WriteE2eTargetAsync(window, profile, e2eOutput, report);
            else if (probe is not null)
                await MeasureAsync(window, probe, options, profile, report);
            else
                return;
        }
        catch (Exception exception)
        {
            report.Add($"FAILED: {exception}");
            exitCode = 1;
        }
        report.Save();
        probe?.Dispose();
        if (!options.Demo.Hold && options.Demo.E2eOutput is null)
            desktop.Shutdown(exitCode);
    }

    /// <summary>Reports page errors and the Monaco worker start, from every editor of the window.</summary>
    public static void ObservePage(WebCodeEditor editor, BenchReport report)
    {
        editor.Failed += (_, exception) => report.Add($"EDITOR FAILED: {exception}");
        editor.PageEvent += (_, e) =>
        {
            if (e.Type == "error")
                report.Add($"PAGE ERROR: {e.Data.GetProperty("message").GetString()}");
            else if (e.Type == "worker")
                report.Add(
                    $"  web worker running in the page ({e.Data.GetProperty("label").GetString()}) at {ProcessClock.ElapsedMs:F0} ms"
                );
        };
    }

    private static async Task MeasureAsync(
        WebDemoWindow window,
        UiStallProbe probe,
        WebDemoOptions options,
        WebDemoProfile profile,
        BenchReport report
    )
    {
        // Subscribe to every milestone when the first editor is created, before the page can report one.
        var startup = new TaskCompletionSource<StartupMilestones>();
        window.EditorCreated += OnFirstEditor;
        void OnFirstEditor(WebCodeEditor created)
        {
            window.EditorCreated -= OnFirstEditor;
            startup.SetResult(new StartupMilestones(created));
        }
        StartupMilestones milestones = await startup.Task;
        WebCodeEditor editor = milestones.Editor;
        ObservePage(editor, report);
        (Task<double> ready, Task<double> visible, Task<double> highlighted) = (
            milestones.Ready,
            milestones.Visible,
            milestones.Highlighted
        );
        (Task<double> lspReady, Task<double> diagnostics, Task<double> semantic) = (
            milestones.LspReady,
            milestones.Diagnostics,
            milestones.Semantic
        );
        Task<WebEditorEvent> pageInfo = milestones.PageInfo;

        await highlighted.WaitAsync(TimeSpan.FromSeconds(30));
        report.Add(probe.EndPhase());

        double serviceReady = 0;
        if (profile.ServiceReady is { } service)
        {
            probe.BeginPhase("startup: until C# ready + semantic colors");
            serviceReady = await service;
            await Task.WhenAll(lspReady, diagnostics, semantic).WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(500);
        }
        else
        {
            probe.BeginPhase("startup: first frame + 500 ms");
            await Task.Delay(500);
        }
        report.Add(probe.EndPhase());

        report.Add(
            $"WebView demo ({profile.Name}, {options.Editor}), web view {editor.WebView.AdapterInfo}, "
                + $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, "
                + $"{Environment.ProcessorCount} cores, server GC {GCSettings.IsServerGC}"
        );
        JsonElement info = (await pageInfo).Data;
        report.Add(
            $"  page: timer resolution {info.GetProperty("timerResolutionMs").GetDouble():F3} ms (cross-origin isolated "
                + $"{info.GetProperty("crossOriginIsolated").GetBoolean()}), WebGL {info.GetProperty("webgl").GetString()}, "
                + $"device pixel ratio {info.GetProperty("devicePixelRatio").GetDouble()}"
        );
        report.Add($"  page: {info.GetProperty("userAgent").GetString()}");
        report.Add($"  window opened:              {milestones.Created, 7:F0} ms after process start");
        report.Add($"  web view adapter created:   {milestones.AdapterCreated, 7:F0} ms");
        report.Add($"  page script running:        {await ready, 7:F0} ms");
        report.Add($"  editor text visible:        {await visible, 7:F0} ms");
        report.Add($"  editor text highlighted:    {await highlighted, 7:F0} ms");
        if (profile.ServiceReady is not null)
        {
            report.Add($"  C# service ready:           {serviceReady, 7:F0} ms");
            report.Add($"  LSP initialized (painted):  {await lspReady, 7:F0} ms");
            report.Add($"  semantic colors shown:      {await semantic, 7:F0} ms");
            report.Add($"  diagnostics shown:          {await diagnostics, 7:F0} ms");
        }

        foreach ((int tab, string pattern, string replacement) in profile.SearchChecks)
        {
            bool created = window.EditorOf(tab) is null;
            double shownAt = ProcessClock.ElapsedMs;
            WebCodeEditor tabEditor = window.ShowTab(tab);
            Task<double>? diagnosed =
                created && tab == profile.DiagnosticsTab
                    ? PageCommands.MilestoneAsync(tabEditor, "diagnosticsShown")
                    : null;
            if (created)
            {
                ObservePage(tabEditor, report);
                await PageCommands.NextEventAsync(tabEditor, "highlighted").WaitAsync(TimeSpan.FromSeconds(30));
            }
            JsonElement result = await PageCommands.RunAsync(
                tabEditor,
                "search",
                w =>
                {
                    w.WriteString("pattern", pattern);
                    w.WriteString("replacement", replacement);
                }
            );
            foreach (string line in PageCommands.Lines(result))
                report.Add(line);
            if (diagnosed is not null)
                report.Add(
                    $"  diagnostics shown {await diagnosed.WaitAsync(TimeSpan.FromSeconds(10)) - shownAt:F0} ms after the tab was first shown"
                );
        }
        window.ShowTab(0);

        if (!options.Demo.Bench)
            return;
        if (profile.LanguageServiceScenarios is { } scenarios)
            await scenarios(window, probe, report);
        if (!options.Demo.SkipHighlightingBench)
            await RunHighlightingAsync(window, options.Editor, probe, report);
    }

    /// <summary>Runs a page command inside a probe phase and reports the host's and the page's statistics.</summary>
    public static async Task RunPhaseAsync(
        UiStallProbe probe,
        BenchReport report,
        WebCodeEditor editor,
        string phase,
        string command,
        Action<Utf8JsonWriter>? writeArguments = null
    )
    {
        probe.BeginPhase(phase);
        JsonElement result = await PageCommands.RunAsync(
            editor,
            command,
            w =>
            {
                w.WriteString("phase", phase);
                w.WriteNumber("typingDelayMs", TypingDelayMs);
                writeArguments?.Invoke(w);
            }
        );
        report.Add(probe.EndPhase());
        foreach (string line in PageCommands.Lines(result))
            report.Add(line);
    }

    /// <summary>The highlighting scenarios of the AvaloniaEdit demos, in a new editor without a language server.</summary>
    private static async Task RunHighlightingAsync(
        WebDemoWindow window,
        WebEditorKind kind,
        UiStallProbe probe,
        BenchReport report
    )
    {
        string label = kind == WebEditorKind.Monaco ? "monaco" : "codemirror";
        var editor = new WebCodeEditor
        {
            Kind = kind,
            LanguageId = "csharp",
            DocumentUri = "file:///bench.cs",
            EnableMeasurements = true,
        };
        ObservePage(editor, report);
        Task<WebEditorEvent> ready = PageCommands.NextEventAsync(editor, "ready");
        window.ShowBenchEditor(editor);
        await ready.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(300);

        await RunPhaseAsync(
            probe,
            report,
            editor,
            $"{label}: open 10k lines",
            "openLarge",
            w => w.WriteString("text", SampleDocuments.LargeCSharpFile(10_000))
        );
        await RunPhaseAsync(
            probe,
            report,
            editor,
            $"{label}: scroll 10k lines",
            "scroll",
            w => w.WriteNumber("step", 40)
        );
        await RunPhaseAsync(
            probe,
            report,
            editor,
            $"{label}: 20k-char line",
            "typing",
            w =>
            {
                w.WriteString("text", SampleDocuments.LongLineFile(20_000));
                w.WriteNumber("caretLine", 2);
                w.WriteNumber("settleMs", 300);
                w.WriteString("typed", " /*x*/");
            }
        );
        await RunPhaseAsync(
            probe,
            report,
            editor,
            $"{label}: typing burst",
            "typing",
            w =>
            {
                w.WriteString("text", SampleDocuments.CSharpScript);
                w.WriteString("typed", SampleDocuments.TypingBurst);
            }
        );
    }

    /// <summary>
    /// Writes where the end of line 1 is on screen, for the input latency probe (tools/Darp.CodeEditor.Probe), and keeps
    /// the window open.
    /// </summary>
    private static async Task WriteE2eTargetAsync(
        WebDemoWindow window,
        WebDemoProfile profile,
        string output,
        BenchReport report
    )
    {
        await window.FirstEditor;
        WebCodeEditor editor = window.ShowTab(profile.E2eTab);
        ObservePage(editor, report);
        await PageCommands.NextEventAsync(editor, "highlighted").WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(500);
        JsonElement caret = (await PageCommands.RunAsync(editor, "caret", w => w.WriteNumber("line", 1))).GetProperty(
            "caret"
        );
        // Page coordinates are CSS pixels relative to the web view.
        double scale = window.RenderScaling;
        PixelPoint origin = editor.WebView.PointToScreen(new Point(0, 0));
        E2eTarget.Write(
            output,
            origin.X + caret.GetProperty("x").GetDouble() * scale,
            origin.Y + caret.GetProperty("y").GetDouble() * scale,
            caret.GetProperty("height").GetDouble() * scale,
            caret.GetProperty("charWidth").GetDouble() * scale
        );
        report.Add($"e2e target written to {output}");
    }

    /// <summary>Prints the web engine's own report of its rendering backend (webkit://gpu, edge://gpu).</summary>
    private static async Task PrintWebViewInfoAsync(WebDemoWindow window, BenchReport report)
    {
        var view = new NativeWebView();
        var navigated = new TaskCompletionSource();
        view.NavigationCompleted += (_, _) => navigated.TrySetResult();
        view.AdapterCreated += (_, _) =>
            view.Navigate(new Uri(OperatingSystem.IsWindows() ? "edge://gpu" : "webkit://gpu"));
        if (OperatingSystem.IsLinux())
            view.AdapterPreference = [Avalonia.Platform.WebViewAdapterType.WebKitGtk];
        window.Content = view;
        await navigated.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(2000);
        report.Add($"web view: {view.AdapterInfo}");
        report.Add(await view.InvokeScript("document.body.innerText") ?? "(no text)");
        await Dispatcher.UIThread.InvokeAsync(() => { });
    }
}

using Darp.CodeEditor.CSharp;
using Darp.CodeEditor.Demo;
using Darp.CodeEditor.Instrumentation;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Lsp;
using Darp.CodeEditor.Samples;
using Darp.CodeEditor.Web;

namespace Darp.CodeEditor.WebDemo;

/// <summary>
/// The JIT WebView demo: a C# script with Roslyn through the in-process LSP adapter, Luau and JSON with highlighting.
/// Mirrors samples/Darp.CodeEditor.Demo.
/// </summary>
internal sealed class CSharpWebDemo : IDisposable
{
    private readonly Task<CSharpScriptLanguageService?> _service;
    private readonly BenchReport _report;
    private LanguageServer? _server;

    public CSharpWebDemo(WebDemoOptions options, BenchReport report)
    {
        _report = report;
        // The editor is usable right away; the server answers "initialize" once the service finished loading.
        _service = options.Demo.NoLanguageService
            ? Task.FromResult<CSharpScriptLanguageService?>(null)
            : CSharpScriptLanguageService
                .CreateAsync(
                    new CSharpScriptOptions(
                        typeof(ScriptGlobals),
                        [
                            typeof(Enumerable).Assembly,
                            typeof(List<>).Assembly,
                            typeof(System.Text.StringBuilder).Assembly,
                        ],
                        ["System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks"]
                    )
                )
                .ContinueWith(t => (CSharpScriptLanguageService?)t.Result, TaskScheduler.Default);
        Task<double> ready = _service.ContinueWith(_ => ProcessClock.ElapsedMs, TaskScheduler.Default);

        Window = new WebDemoWindow(
            $"Code editor demo (WebView + {options.Editor}, JIT, C# scripting)",
            [
                new("script.csx", () => CreateCSharpEditor(options)),
                new(
                    "script.luau",
                    () => CreateEditor(options, "luau", "file:///script.luau", SampleDocuments.LuauScript)
                ),
                new("config.json", () => CreateEditor(options, "json", "file:///config.json", SampleDocuments.Json)),
            ],
            options.Light
        );
        Profile = new WebDemoProfile(
            "JIT",
            options.Demo.NoLanguageService ? null : ready,
            [(2, "temperature", "heat"), (0, "Device", "Sensor")],
            options.Demo.NoLanguageService ? null : RunLanguageServiceScenariosAsync,
            E2eTab: 0
        );
    }

    public WebDemoWindow Window { get; }

    public WebDemoProfile Profile { get; }

    public void Dispose()
    {
        _server?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        if (_service.IsCompletedSuccessfully)
            _service.Result?.Dispose();
    }

    private WebCodeEditor CreateCSharpEditor(WebDemoOptions options)
    {
        WebCodeEditor editor = CreateEditor(
            options,
            "csharp",
            "file:///script.csx",
            SampleDocuments.CSharpScript,
            lsp: !options.Demo.NoLanguageService
        );
        if (options.Demo.NoLanguageService)
            return editor;
        Task<ILanguageService> service = _service.ContinueWith(
            t => (ILanguageService)(t.Result ?? throw new InvalidOperationException("No C# service.")),
            TaskScheduler.Default
        );
        // The host only relays: the page runs the LSP client, the adapter runs on the thread pool.
        _server = new LanguageServer(service, editor.PostLspMessage);
        _server.RequestFailed += exception => _report.Add($"LANGUAGE SERVICE FAILED: {exception}");
        editor.LspMessageReceived += _server.Receive;
        return editor;
    }

    private static double Median(List<double> values)
    {
        double[] sorted = [.. values.Order()];
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    private static WebCodeEditor CreateEditor(
        WebDemoOptions options,
        string language,
        string uri,
        string text,
        bool lsp = false
    ) =>
        new()
        {
            Kind = options.Editor,
            LanguageId = language,
            DocumentUri = uri,
            InitialText = text,
            UseLanguageServer = lsp,
            EnableMeasurements = options.Measure,
            EnableDevTools = options.DevTools,
            PageQuery = options.PageQuery,
        };

    /// <summary>The scenarios of samples/Darp.CodeEditor.Demo/LanguageServiceScenarios.cs, run in the page.</summary>
    private async Task RunLanguageServiceScenariosAsync(WebDemoWindow window, UiStallProbe probe, BenchReport report)
    {
        WebCodeEditor editor = window.ShowTab(0);
        // Where completion time goes on the host side: the server (Roslyn + JSON) and the relay into the page.
        var serverMs = new List<double>();
        var relay = new List<(int Length, double Ms)>();
        void OnHandled(string method, TimeSpan duration)
        {
            if (method == "textDocument/completion")
                lock (serverMs)
                    serverMs.Add(duration.TotalMilliseconds);
        }
        void OnDelivered(int length, TimeSpan duration)
        {
            if (length > 1000)
                relay.Add((length, duration.TotalMilliseconds));
        }
        _server!.RequestHandled += OnHandled;
        editor.LspMessageDelivered += OnDelivered;
        await WebDemoRun.RunPhaseAsync(
            probe,
            report,
            editor,
            "C#: completion latency",
            "completionLatency",
            w => w.WriteNumber("rounds", 20)
        );
        _server.RequestHandled -= OnHandled;
        editor.LspMessageDelivered -= OnDelivered;
        lock (serverMs)
            report.Add(
                $"  host: server handled completion requests in p50 {Median(serverMs):F1} ms, max {serverMs.Max():F1} ms (n={serverMs.Count})"
            );
        foreach (var group in relay.GroupBy(r => r.Length > 100_000))
            report.Add(
                $"  host: messages over {(group.Key ? "100 KB" : "1 KB")} reached the page's handler in p50 {Median([.. group.Select(r => r.Ms)]):F1} ms "
                    + $"(n={group.Count()}, p50 size {Median([.. group.Select(r => (double)r.Length)]) / 1000:F0} KB)"
            );
        await WebDemoRun.RunPhaseAsync(
            probe,
            report,
            editor,
            "C#: typing burst with completion",
            "typing",
            w =>
            {
                w.WriteString("typed", SampleDocuments.TypingBurst + SampleDocuments.TypingBurst);
                w.WriteNumber("afterMs", 1000);
            }
        );
    }
}

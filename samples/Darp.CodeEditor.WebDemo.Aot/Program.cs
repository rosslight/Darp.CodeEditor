using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Darp.CodeEditor.Demo.Aot;
using Darp.CodeEditor.Instrumentation;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Lsp;
using Darp.CodeEditor.Samples;
using Darp.CodeEditor.Web;

namespace Darp.CodeEditor.WebDemo.Aot;

internal static class Program
{
    public static WebDemoOptions Options { get; private set; } = WebDemoOptions.Parse([]);

    [STAThread]
    public static int Main(string[] args)
    {
        Options = WebDemoOptions.Parse(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

/// <summary>
/// The NativeAOT WebView demo: Luau and C# with highlighting, and JSON with a small language service (word completion,
/// parse errors) served through the in-process LSP adapter. Mirrors samples/Darp.CodeEditor.Demo.Aot.
/// </summary>
internal sealed class App : Application
{
    private LanguageServer? _jsonServer;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            WebDemoOptions options = Program.Options;
            UiStallProbe? probe = options.Measure
                ? new UiStallProbe(Dispatcher.UIThread, TimeSpan.FromMilliseconds(2))
                : null;
            probe?.BeginPhase("startup: until first highlighted frame");
            var report = new BenchReport(options.Demo.OutputPath);
            var window = new WebDemoWindow(
                $"Code editor demo (WebView + {options.Editor}, NativeAOT)",
                [
                    new(
                        "script.luau",
                        () => CreateEditor(options, "luau", "file:///script.luau", SampleDocuments.LuauScript)
                    ),
                    new("config.json", () => CreateJsonEditor(options, report)),
                    new(
                        "script.csx",
                        () => CreateEditor(options, "csharp", "file:///script.csx", SampleDocuments.CSharpScript)
                    ),
                ],
                options.Light
            );
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => _jsonServer?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            var profile = new WebDemoProfile(
                "NativeAOT",
                null,
                [(0, "reading", "sample"), (1, "temperature", "heat")],
                RunJsonScenariosAsync,
                E2eTab: 2,
                DiagnosticsTab: 1
            );
            WebDemoRun.Start(window, desktop, probe, options, profile, report);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private WebCodeEditor CreateJsonEditor(WebDemoOptions options, BenchReport report)
    {
        // A trailing comma, so the parse error shows up as a diagnostic.
        WebCodeEditor editor = CreateEditor(
            options,
            "json",
            "file:///config.json",
            SampleDocuments.Json.Replace("\"file\": null", "\"file\": null,"),
            lsp: true
        );
        _jsonServer = new LanguageServer(
            Task.FromResult<ILanguageService>(new JsonLanguageService()),
            editor.PostLspMessage
        );
        _jsonServer.RequestFailed += exception => report.Add($"LANGUAGE SERVICE FAILED: {exception}");
        editor.LspMessageReceived += _jsonServer.Receive;
        return editor;
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

    private static async Task RunJsonScenariosAsync(WebDemoWindow window, UiStallProbe probe, BenchReport report)
    {
        WebCodeEditor json = window.ShowTab(1);
        await WebDemoRun.RunPhaseAsync(
            probe,
            report,
            json,
            "JSON: typing burst with word completion",
            "typing",
            w =>
            {
                w.WriteNumber("caretLine", 4);
                w.WriteString("typed", "\n    \"serial\": \"sensor-temperature-7\", \"enabled\": false,");
                w.WriteNumber("afterMs", 500);
            }
        );
    }
}

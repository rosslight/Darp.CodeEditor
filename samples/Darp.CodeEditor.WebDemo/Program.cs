using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Darp.CodeEditor.Instrumentation;
using Darp.CodeEditor.Samples;

namespace Darp.CodeEditor.WebDemo;

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

internal sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            WebDemoOptions options = Program.Options;
            // Start probing before the window exists, so startup work on the UI thread is measured too.
            UiStallProbe? probe = options.Measure
                ? new UiStallProbe(Dispatcher.UIThread, TimeSpan.FromMilliseconds(2))
                : null;
            probe?.BeginPhase("startup: until first highlighted frame");
            var report = new BenchReport(options.Demo.OutputPath);
            var host = new CSharpWebDemo(options, report);
            desktop.MainWindow = host.Window;
            desktop.Exit += (_, _) => host.Dispose();
            WebDemoRun.Start(host.Window, desktop, probe, options, host.Profile, report);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

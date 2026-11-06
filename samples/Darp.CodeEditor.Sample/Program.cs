using Avalonia;

namespace Sample;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant code before
    // AppMain is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            // The editor reports languages and color themes that fail to load to Avalonia's log.
            .LogToTrace();
}

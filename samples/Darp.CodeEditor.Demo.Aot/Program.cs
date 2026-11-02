using Avalonia;
using Darp.CodeEditor.Samples;

namespace Darp.CodeEditor.Demo.Aot;

internal static class Program
{
    public static DemoOptions Options { get; private set; } = DemoOptions.Parse([]);

    [STAThread]
    public static int Main(string[] args)
    {
        Options = DemoOptions.Parse(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

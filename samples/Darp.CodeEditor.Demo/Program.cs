using Avalonia;
using Darp.CodeEditor.Samples;

namespace Darp.CodeEditor.Demo;

internal static class Program
{
    public static DemoOptions Options { get; private set; } = DemoOptions.Parse([]);

    [STAThread]
    public static int Main(string[] args)
    {
        Options = DemoOptions.Parse(args);
        // Measurement switch: background GCs only, while memory allows; avoids blocking gen2 collections.
        if (args.Contains("--low-latency-gc"))
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Entry point for the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            // Measurement switch: draw popups inside the window instead of creating native windows.
            .With(
                new X11PlatformOptions { OverlayPopups = Environment.GetCommandLineArgs().Contains("--overlay-popups") }
            )
            .With(
                new Win32PlatformOptions
                {
                    OverlayPopups = Environment.GetCommandLineArgs().Contains("--overlay-popups"),
                }
            )
            .LogToTrace();
}

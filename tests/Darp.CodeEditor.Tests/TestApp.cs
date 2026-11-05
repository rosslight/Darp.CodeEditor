using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Darp.CodeEditor.Tests.TestApp))]

namespace Darp.CodeEditor.Tests;

/// <summary>The application of headless tests. Documents need a UI thread, not controls.</summary>
public sealed class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

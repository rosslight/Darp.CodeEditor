using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Darp.CodeEditor.Tests.TestApp))]

namespace Darp.CodeEditor.Tests;

/// <summary>The headless app that the editor tests run in.</summary>
public sealed class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

    public override void Initialize() => Styles.Add(new FluentTheme());
}

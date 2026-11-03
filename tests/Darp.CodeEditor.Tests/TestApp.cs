using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Darp.CodeEditor.Tests.TestApp))]

namespace Darp.CodeEditor.Tests;

/// <summary>The headless app that the editor tests run in.</summary>
public sealed class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        // The editor's template.
        Styles.Add(
            new StyleInclude(new Uri("avares://Darp.CodeEditor.Tests/"))
            {
                Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
            }
        );
    }
}

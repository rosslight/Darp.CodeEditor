using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The app's main UI thread, for the tests of the UI collection: a headless Avalonia app that they share, with
/// <see cref="TestApp"/>'s styles. Avalonia's xunit integration does not work with xunit's NativeAOT runner, so tests
/// run their bodies on that thread through Avalonia's headless test session.
/// </summary>
public sealed class UiFixture : IAsyncDisposable
{
    /// <summary>The name of the collection whose tests share the fixture.</summary>
    public const string Collection = "UI";

    /// <summary>The editor's own texts, which tests compare, in English on every machine.</summary>
    static UiFixture() => CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

    private readonly HeadlessUnitTestSession _session = HeadlessUnitTestSession.StartNew(
        typeof(TestApp),
        AvaloniaTestIsolationLevel.PerAssembly
    );

    /// <summary>Runs <paramref name="body"/> on the UI thread.</summary>
    public Task Run(Action body) => _session.Dispatch(body, TestContext.Current.CancellationToken);

    /// <summary>Runs <paramref name="body"/> on the UI thread, until the task it returns completes.</summary>
    public Task Run(Func<Task> body) =>
        _session.Dispatch(
            async () =>
            {
                await body();
                return true;
            },
            TestContext.Current.CancellationToken
        );

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}

/// <summary>The tests that run on the UI thread of <see cref="UiFixture"/>.</summary>
[CollectionDefinition(UiFixture.Collection)]
public sealed class UiCollection : ICollectionFixture<UiFixture>;

/// <summary>
/// The tests' app: Fluent and the editor's styles, as an app adds them, and Skia, so that headless windows render
/// pixels.
/// </summary>
public sealed class TestApp : Application
{
    /// <summary>Found by the headless session, which builds the app from it.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new CodeEditorStyles());
    }
}

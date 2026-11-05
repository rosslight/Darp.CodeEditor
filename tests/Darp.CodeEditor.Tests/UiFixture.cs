using Avalonia;
using Avalonia.Headless;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The app's main UI thread, for the tests of the UI collection: a headless Avalonia app that they share. Avalonia's
/// xunit integration does not work with xunit's NativeAOT runner, so tests run their bodies on that thread through
/// Avalonia's headless test session.
/// </summary>
public sealed class UiFixture : IAsyncDisposable
{
    /// <summary>The name of the collection whose tests share the fixture.</summary>
    public const string Collection = "UI";

    private readonly HeadlessUnitTestSession _session = HeadlessUnitTestSession.StartNew(
        typeof(Application),
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

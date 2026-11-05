namespace Darp.CodeEditor.Tests;

internal static class Wait
{
    /// <summary>
    /// Polls <paramref name="condition"/> until it holds, for 30 seconds at most. On the UI thread the dispatcher runs
    /// meanwhile: posted work, layout and rendering.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}

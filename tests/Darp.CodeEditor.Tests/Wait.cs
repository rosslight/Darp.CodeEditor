namespace Darp.CodeEditor.Tests;

internal static class Wait
{
    /// <summary>Cancelled when the test run is cancelled, or after 30 seconds.</summary>
    public static CancellationToken Timeout => Within(TimeSpan.FromSeconds(30));

    /// <summary>Cancelled when the test run is cancelled, or after <paramref name="time"/>.</summary>
    public static CancellationToken Within(TimeSpan time)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(time);
        return timeout.Token;
    }

    public static async Task UntilAsync(Func<bool> condition)
    {
        CancellationToken timeout = Timeout;
        while (!condition())
            await Task.Delay(10, timeout);
    }
}

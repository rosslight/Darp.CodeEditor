namespace Darp.CodeEditor.Tests;

internal static class Wait
{
    /// <summary>Cancelled when the test run is cancelled, or after 30 seconds.</summary>
    public static CancellationToken Timeout
    {
        get
        {
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return timeout.Token;
        }
    }

    public static async Task UntilAsync(Func<bool> condition)
    {
        CancellationToken timeout = Timeout;
        while (!condition())
            await Task.Delay(10, timeout);
    }
}

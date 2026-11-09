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

    /// <summary>
    /// Waits for <paramref name="task"/> for 30 seconds at most: a cap, not a latency, so that a task that never ends
    /// fails the test instead of hanging it.
    /// </summary>
    public static Task<T> ForAsync<T>(Task<T> task) =>
        task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    /// <inheritdoc cref="ForAsync{T}(Task{T})"/>
    public static Task ForAsync(Task task) =>
        task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
}

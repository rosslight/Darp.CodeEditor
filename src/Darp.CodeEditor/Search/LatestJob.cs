using Avalonia.Logging;
using Avalonia.Threading;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Search;

/// <summary>
/// The latest run of some work in the background, such as a search: starting a run cancels the one before it, and only
/// the latest run's result comes back, on the UI thread, unless it was cancelled meanwhile. A failure goes to the log.
/// </summary>
internal sealed class LatestJob<T>
{
    private readonly Dispatcher _dispatcher;
    private readonly string _name;

    /// <summary>The run whose result is still to come; null for none.</summary>
    private CancellationTokenSource? _running;

    public LatestJob(Dispatcher dispatcher, string name)
    {
        _dispatcher = dispatcher;
        _name = name;
    }

    /// <summary>Whether a run is under way, whose result is still to come.</summary>
    public bool IsRunning => _running is not null;

    /// <summary>
    /// Runs <paramref name="work"/> in the background, in place of the run under way, and gives its result to
    /// <paramref name="done"/> on the UI thread.
    /// </summary>
    /// <returns>A task that completes once the result was taken, or dropped, on the UI thread.</returns>
    public Task Start(Func<CancellationToken, T> work, Action<T> done)
    {
        Cancel();
        var running = new CancellationTokenSource();
        _running = running;
        var finished = new TaskCompletionSource();
        // Cancelled, the task ends as cancelled, not as failed.
        Task<T> task = Task.Run(() => work(running.Token), running.Token);
        task.ContinueWith(
            _ =>
                _dispatcher.Post(() =>
                {
                    try
                    {
                        Finish(task, running, done);
                    }
                    finally
                    {
                        finished.SetResult();
                    }
                }),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
        return finished.Task;
    }

    /// <summary>Cancels the run under way: its result never comes.</summary>
    public void Cancel()
    {
        _running?.Cancel();
        _running = null;
    }

    private void Finish(Task<T> task, CancellationTokenSource running, Action<T> done)
    {
        if (task.Exception is { } exception)
            Logger
                .TryGet(LogEventLevel.Error, AssetFiles.LogArea)
                ?.Log(this, "{Work} failed: {Exception}", _name, exception);
        if (_running != running)
            return;
        _running = null;
        if (task.IsCompletedSuccessfully)
            done(task.Result);
    }
}

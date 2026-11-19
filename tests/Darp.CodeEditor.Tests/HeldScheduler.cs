using System.Collections.Concurrent;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Runs the work given to it on the thread pool, or, while it holds, keeps it until the test releases it: for ordering
/// the editor's work off the UI thread, such as ranking completion's items, against what the test does.
/// </summary>
internal sealed class HeldScheduler : TaskScheduler
{
    private readonly ConcurrentQueue<Task> _held = new();
    private readonly ConcurrentQueue<Task> _given = new();
    private volatile bool _holding;

    /// <summary>How many pieces of work wait for <see cref="Release"/>.</summary>
    public int Held => _held.Count;

    /// <summary>Keeps the work given from now on.</summary>
    public void Hold() => _holding = true;

    /// <summary>Runs the work kept, and what comes next, on the thread pool.</summary>
    public void Release()
    {
        _holding = false;
        while (_held.TryDequeue(out Task? task))
            Run(task);
    }

    /// <summary>Runs the work kept, as <see cref="Release"/> does, and completes once it ran.</summary>
    public Task ReleaseAndWaitAsync()
    {
        Task[] kept = [.. _held];
        Release();
        return Task.WhenAll(kept.Select(Ran));
    }

    /// <summary>Completes once the work given so far has run, such as the filters of what a test typed; not while held.</summary>
    public Task RanAsync() => Task.WhenAll(_given.Select(Ran));

    protected override void QueueTask(Task task)
    {
        _given.Enqueue(task);
        if (_holding)
            _held.Enqueue(task);
        else
            Run(task);
    }

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    protected override IEnumerable<Task> GetScheduledTasks() => _held;

    /// <summary>Completes once <paramref name="task"/> ran, also when it failed or was cancelled.</summary>
    private static Task Ran(Task task) => task.ContinueWith(_ => { }, TaskScheduler.Default);

    private void Run(Task task) => ThreadPool.UnsafeQueueUserWorkItem(_ => TryExecuteTask(task), null);
}

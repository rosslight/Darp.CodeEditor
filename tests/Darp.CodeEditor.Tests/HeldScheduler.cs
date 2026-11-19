using System.Collections.Concurrent;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Runs the work given to it on the thread pool, or, while it holds, keeps it until the test releases it: for ordering
/// the editor's work off the UI thread, such as ranking completion's items, against what the test does.
/// </summary>
internal sealed class HeldScheduler : TaskScheduler
{
    private readonly ConcurrentQueue<Task> _held = new();
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

    protected override void QueueTask(Task task)
    {
        if (_holding)
            _held.Enqueue(task);
        else
            Run(task);
    }

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    protected override IEnumerable<Task> GetScheduledTasks() => _held;

    private void Run(Task task) => ThreadPool.UnsafeQueueUserWorkItem(_ => TryExecuteTask(task), null);
}

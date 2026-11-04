namespace Darp.CodeEditor.CSharpScript.Runner;

/// <summary>
/// Collects single items into batches for a handler's batch method: the way to keep up with thousands of items per
/// second, since every call into the runner crosses a process boundary.
/// </summary>
/// <remarks>
/// While a batch is in flight, new items wait for the next one, so batches grow with the load and a lone item goes
/// out at once. Safe to use from any thread.
/// </remarks>
/// <typeparam name="TItem">The item type.</typeparam>
/// <typeparam name="TResult">The result for one item.</typeparam>
public sealed class ScriptBatcher<TItem, TResult> : IAsyncDisposable
{
    /// <summary>Creates a batcher that sends batches to <paramref name="handleBatch"/>.</summary>
    /// <param name="handleBatch">Handles a batch; returns one result per item, in order.</param>
    /// <param name="options">Batch size and queue limits.</param>
    public ScriptBatcher(
        Func<TItem[], CancellationToken, Task<TResult[]>> handleBatch,
        ScriptBatcherOptions? options = null
    ) => throw new NotImplementedException();

    /// <summary>Queues <paramref name="item"/> for the next batch and returns its result.</summary>
    /// <remarks>Waits while the queue is full.</remarks>
    public Task<TResult> SubmitAsync(TItem item, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <summary>Sends the queued items, waits for their results and stops taking new ones.</summary>
    public ValueTask DisposeAsync() => throw new NotImplementedException();
}

/// <summary>Limits of a <see cref="ScriptBatcher{TItem, TResult}"/>.</summary>
public sealed record ScriptBatcherOptions
{
    /// <summary>The most items in one batch. Default: 256.</summary>
    public int MaxBatchSize { get; init; } = 256;

    /// <summary>The most items waiting for a batch; more wait in <c>SubmitAsync</c>. Default: 4096.</summary>
    public int Capacity { get; init; } = 4096;
}

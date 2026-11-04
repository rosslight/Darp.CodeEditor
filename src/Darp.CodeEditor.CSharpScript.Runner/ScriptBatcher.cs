namespace Darp.CodeEditor.CSharpScript.Runner;

/// <summary>
/// Collects single items into batches for a handler's batch method: the way to keep up with thousands of items per
/// second, since every call into the runner crosses a process boundary.
/// </summary>
/// <remarks>
/// <para>
/// While a batch is in flight, new items wait for the next one, so batches grow with the load and a lone item goes
/// out at once. Batches go out one at a time, in the order the items came.
/// </para>
/// <para>
/// When a batch fails, every item in it fails with that exception, and the next batch goes out as usual. A batch
/// whose results are not one per item fails with <see cref="InvalidOperationException"/>.
/// </para>
/// <para>Safe to use from any thread.</para>
/// </remarks>
/// <typeparam name="TItem">The item type.</typeparam>
/// <typeparam name="TResult">The result for one item.</typeparam>
public sealed class ScriptBatcher<TItem, TResult> : IAsyncDisposable
{
    /// <summary>Creates a batcher that sends batches to <paramref name="handleBatch"/>.</summary>
    /// <param name="handleBatch">Handles a batch; returns one result per item, in order.</param>
    /// <param name="options">Batch size and queue limits.</param>
    public ScriptBatcher(Func<TItem[], Task<TResult[]>> handleBatch, ScriptBatcherOptions? options = null) =>
        throw new NotImplementedException();

    /// <summary>Queues <paramref name="item"/> for the next batch and returns its result.</summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">
    /// Stops waiting for room in a full queue. Once queued, the item goes out with its batch, and the token no longer
    /// applies: cancelling one item must not cancel the others in its batch.
    /// </param>
    /// <exception cref="ObjectDisposedException">The batcher was disposed.</exception>
    public Task<TResult> SubmitAsync(TItem item, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <summary>
    /// Stops taking items, sends the queued ones and waits for their batches. A batch that hangs ends with the
    /// handler's call timeout.
    /// </summary>
    public ValueTask DisposeAsync() => throw new NotImplementedException();
}

/// <summary>Limits of a <see cref="ScriptBatcher{TItem, TResult}"/>.</summary>
public sealed record ScriptBatcherOptions
{
    /// <summary>The most items in one batch. Default: 256.</summary>
    public int MaxBatchSize { get; init; } = 256;

    /// <summary>The most items queued for a batch; further submissions wait for room. Default: 4096.</summary>
    public int Capacity { get; init; } = 4096;
}

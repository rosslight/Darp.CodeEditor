using System.Threading.Channels;

namespace Darp.CodeEditor.CSharp.Execution;

public sealed record ScriptBatcherOptions
{
    /// <summary>The most items one batch holds.</summary>
    public int MaxBatchSize { get; init; } = 256;

    /// <summary>How many items may wait. When this many wait, submitting waits for room.</summary>
    public int Capacity { get; init; } = 4096;
}

/// <summary>
/// Sends single items, such as packets, to a script in batches: one call into the runner per batch instead of one per
/// item.
/// </summary>
/// <remarks>
/// One batch is in flight at a time, and items keep their order. Items that arrive meanwhile form the next batch, so a
/// lone item goes out at once and batches grow with the load. When a batch fails, each of its items fails with the
/// batch's exception; later batches still go out. Handle a runner restart in the batch delegate, for example by
/// calling the handler the app evaluated last.
/// </remarks>
public sealed class ScriptBatcher<TItem, TResult> : IAsyncDisposable
{
    private readonly Func<TItem[], Task<TResult[]>> _handleBatch;
    private readonly int _maxBatchSize;
    private readonly Channel<Entry> _queue;
    private readonly Task _sending;

    /// <param name="handleBatch">
    /// Handles a batch and returns one result per item, in order, such as <c>handler.HandleBatchAsync</c>.
    /// </param>
    public ScriptBatcher(Func<TItem[], Task<TResult[]>> handleBatch, ScriptBatcherOptions? options = null)
    {
        options ??= new ScriptBatcherOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Capacity, 1);
        _handleBatch = handleBatch;
        _maxBatchSize = options.MaxBatchSize;
        _queue = Channel.CreateBounded<Entry>(
            new BoundedChannelOptions(options.Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait }
        );
        _sending = Task.Run(SendAsync);
    }

    /// <summary>Queues <paramref name="item"/> and completes with its result once its batch was handled.</summary>
    /// <param name="cancellationToken">Cancels waiting for room in the queue, not the item once it is queued.</param>
    /// <exception cref="ObjectDisposedException">The batcher is disposed.</exception>
    public async Task<TResult> SubmitAsync(TItem item, CancellationToken cancellationToken = default)
    {
        var entry = new Entry(item);
        try
        {
            await _queue.Writer.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
        return await entry.Result.Task.ConfigureAwait(false);
    }

    /// <summary>Handles the items already queued, then stops.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _sending.ConfigureAwait(false);
    }

    private async Task SendAsync()
    {
        var batch = new List<Entry>(_maxBatchSize);
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (batch.Count < _maxBatchSize && _queue.Reader.TryRead(out Entry? entry))
                batch.Add(entry);
            var items = new TItem[batch.Count];
            for (int i = 0; i < items.Length; i++)
                items[i] = batch[i].Item;
            try
            {
                TResult[] results = await _handleBatch(items).ConfigureAwait(false);
                if (results.Length != items.Length)
                {
                    throw new InvalidOperationException(
                        $"The batch handler returned {results.Length} results for {items.Length} items."
                    );
                }
                for (int i = 0; i < results.Length; i++)
                    batch[i].Result.TrySetResult(results[i]);
            }
            catch (Exception exception)
            {
                foreach (Entry entry in batch)
                    entry.Result.TrySetException(exception);
            }
            batch.Clear();
        }
    }

    private sealed class Entry(TItem item)
    {
        public TItem Item { get; } = item;

        public TaskCompletionSource<TResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>A <see cref="ScriptBatcher{TItem, TResult}"/> for batch calls without results.</summary>
public sealed class ScriptBatcher<TItem> : IAsyncDisposable
{
    private readonly ScriptBatcher<TItem, bool> _batcher;

    /// <param name="handleBatch">Handles a batch, such as <c>handler.ProcessAsync</c>.</param>
    public ScriptBatcher(Func<TItem[], Task> handleBatch, ScriptBatcherOptions? options = null)
    {
        _batcher = new ScriptBatcher<TItem, bool>(
            async items =>
            {
                await handleBatch(items).ConfigureAwait(false);
                return new bool[items.Length];
            },
            options
        );
    }

    /// <inheritdoc cref="ScriptBatcher{TItem, TResult}.SubmitAsync"/>
    public Task SubmitAsync(TItem item, CancellationToken cancellationToken = default) =>
        _batcher.SubmitAsync(item, cancellationToken);

    /// <inheritdoc cref="ScriptBatcher{TItem, TResult}.DisposeAsync"/>
    public ValueTask DisposeAsync() => _batcher.DisposeAsync();
}

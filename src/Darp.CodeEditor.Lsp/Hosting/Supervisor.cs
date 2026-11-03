using System.Diagnostics;

namespace Darp.CodeEditor.Lsp.Hosting;

/// <summary>
/// Keeps one session with a helper, such as a language server, running. It starts the session when first asked, and
/// when the session ends or fails to start, starts a new one after a delay that doubles with each failure in a row.
/// </summary>
/// <typeparam name="TSession">A started helper; disposing it ends the helper.</typeparam>
public sealed class Supervisor<TSession> : IAsyncDisposable
    where TSession : class, IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<TSession>> _start;
    private readonly Func<TSession, Task> _completion;
    private readonly SupervisorOptions _options;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource<TSession> _attempt = NewAttempt();
    private TSession? _current;
    private Task? _loop;

    /// <param name="start">Starts a session; cancelled when the supervisor is disposed.</param>
    /// <param name="completion">Completes when a session ended.</param>
    public Supervisor(
        Func<CancellationToken, Task<TSession>> start,
        Func<TSession, Task> completion,
        SupervisorOptions? options = null
    )
    {
        _start = start;
        _completion = completion;
        _options = options ?? new SupervisorOptions();
    }

    /// <summary>The running session, or null while none runs.</summary>
    public TSession? Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    /// <summary>Raised on a thread-pool thread when a session started, before <see cref="StartAsync"/> completes.</summary>
    public event Action<TSession>? Started;

    /// <summary>
    /// Raised on a thread-pool thread when a session ended without the supervisor being disposed, with the session,
    /// or when a start failed, with the exception. A new start follows after the delay.
    /// </summary>
    public event Action<TSession?, Exception?>? Failed;

    /// <summary>Starts a session unless one runs or is starting.</summary>
    /// <returns>
    /// The running session, or the outcome of the start in progress: its session, or the exception it failed with.
    /// Later starts continue in the background.
    /// </returns>
    public Task<TSession> StartAsync()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
            if (_current is { } current)
                return Task.FromResult(current);
            _loop ??= Task.Run(RunAsync);
            return _attempt.Task;
        }
    }

    /// <summary>Stops starting sessions and disposes the running one.</summary>
    public async ValueTask DisposeAsync()
    {
        TSession? current;
        Task? loop;
        lock (_lock)
        {
            if (_stop.IsCancellationRequested)
                return;
            _stop.Cancel();
            current = _current;
            loop = _loop;
        }
        if (current is not null)
            await current.DisposeAsync().ConfigureAwait(false);
        if (loop is not null)
            await loop.ConfigureAwait(false);
        _attempt.TrySetException(new ObjectDisposedException(GetType().Name));
    }

    private async Task RunAsync()
    {
        int failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            TSession session;
            try
            {
                session = await _start(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (_stop.IsCancellationRequested)
                    return;
                NextAttempt().TrySetException(exception);
                Raise(() => Failed?.Invoke(null, exception));
                if (!await DelayAsync(++failures).ConfigureAwait(false))
                    return;
                continue;
            }
            bool stopped;
            lock (_lock)
            {
                stopped = _stop.IsCancellationRequested;
                if (!stopped)
                    _current = session;
            }
            if (stopped)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return;
            }
            long started = Stopwatch.GetTimestamp();
            // Before the start's task completes, so that its awaiters see what Started set up.
            Raise(() => Started?.Invoke(session));
            NextAttempt().TrySetResult(session);
            try
            {
                await _completion(session).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning($"Watching a {typeof(TSession).Name} failed: {exception}");
            }
            lock (_lock)
            {
                _current = null;
                // DisposeAsync disposes the session it found.
                if (_stop.IsCancellationRequested)
                    return;
            }
            Raise(() => Failed?.Invoke(session, null));
            await DisposeQuietlyAsync(session).ConfigureAwait(false);
            failures = Stopwatch.GetElapsedTime(started) >= _options.StableAfter ? 1 : failures + 1;
            if (!await DelayAsync(failures).ConfigureAwait(false))
                return;
        }
    }

    /// <summary>Hands the attempt in progress to the caller and starts a new one for later callers.</summary>
    private TaskCompletionSource<TSession> NextAttempt()
    {
        lock (_lock)
        {
            TaskCompletionSource<TSession> attempt = _attempt;
            _attempt = NewAttempt();
            return attempt;
        }
    }

    /// <returns>False when the supervisor was disposed meanwhile.</returns>
    private async Task<bool> DelayAsync(int failures)
    {
        double factor = Math.Pow(2, Math.Min(failures - 1, 16));
        TimeSpan delay = TimeSpan.FromTicks(
            (long)Math.Min(_options.FirstDelay.Ticks * factor, _options.MaxDelay.Ticks)
        );
        try
        {
            await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task DisposeQuietlyAsync(TSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Disposing a {typeof(TSession).Name} failed: {exception}");
        }
    }

    private static void Raise(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception exception)
        {
            Trace.TraceError($"A handler of the supervisor failed: {exception}");
        }
    }

    private static TaskCompletionSource<TSession> NewAttempt()
    {
        var attempt = new TaskCompletionSource<TSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Nobody may wait for a failed start; its exception is then not unobserved.
        _ = attempt.Task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
        return attempt;
    }
}

/// <summary>How a <see cref="Supervisor{TSession}"/> restarts.</summary>
public sealed record SupervisorOptions
{
    /// <summary>The delay before the first restart after a failure; it doubles with each failure in a row.</summary>
    public TimeSpan FirstDelay { get; init; } = TimeSpan.FromSeconds(0.5);

    /// <summary>The longest delay between restarts.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A session that ran this long counts as healthy: its failure starts the delays over.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromSeconds(30);
}

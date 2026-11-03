using System.Diagnostics;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.LanguageServices;

/// <summary>
/// How long a language server may take before it counts as hung. A server that answered a request gets the
/// timeout; one that answered none yet, such as one that still loads, gets twice that. Each hang doubles the timeout
/// of the next server, up to the longest; a server that answered and kept running for a healthy period brings it
/// back to the first.
/// </summary>
/// <remarks>Thread-safe.</remarks>
internal sealed class HangTimeout
{
    // The longest wait that CancelAfter and WaitAsync take everywhere.
    private static readonly TimeSpan s_limit = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly TimeSpan _first;
    private readonly TimeSpan _longest;
    private readonly TimeSpan _healthyAfter;
    private readonly Lock _lock = new();
    private TimeSpan _timeout;
    private LanguageClient? _answered;
    private long _answeredAt;

    /// <param name="timeout">The timeout; <see cref="Timeout.InfiniteTimeSpan"/> waits forever.</param>
    /// <param name="longest">The longest that hangs raise the timeout to; infinite raises it without bound.</param>
    /// <param name="healthyAfter">How long a server that answered runs until the timeout is back to the first.</param>
    /// <exception cref="ArgumentOutOfRangeException">A timeout is neither positive nor infinite.</exception>
    public HangTimeout(TimeSpan timeout, TimeSpan longest, TimeSpan healthyAfter)
    {
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "The timeout must be positive or infinite."
            );
        }
        if (longest <= TimeSpan.Zero && longest != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(longest),
                longest,
                "The timeout must be positive or infinite."
            );
        }
        _first = timeout;
        _longest =
            timeout == Timeout.InfiniteTimeSpan ? timeout
            : longest == Timeout.InfiniteTimeSpan ? s_limit
            : longest > timeout ? longest
            : timeout;
        _healthyAfter = healthyAfter;
        _timeout = timeout;
    }

    /// <summary>How long <paramref name="client"/> may take now; null stands for a server that is starting.</summary>
    public TimeSpan Of(LanguageClient? client)
    {
        lock (_lock)
        {
            ResetIfHealthy();
            return client is not null && client == _answered ? _timeout : Twice(_timeout);
        }
    }

    /// <summary>Notes that <paramref name="client"/> answered a request.</summary>
    public void Answered(LanguageClient client)
    {
        lock (_lock)
        {
            if (_answered == client)
                return;
            _answered = client;
            _answeredAt = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Notes that <paramref name="client"/> ended; one that <paramref name="hung"/> doubles the timeout.</summary>
    /// <param name="client">The server, or null for one that left <c>initialize</c> unanswered.</param>
    public void Ended(LanguageClient? client, bool hung)
    {
        lock (_lock)
        {
            if (client is not null && _answered == client)
            {
                ResetIfHealthy();
                _answered = null;
            }
            if (hung && _timeout != Timeout.InfiniteTimeSpan)
                _timeout = Twice(_timeout) < _longest ? Twice(_timeout) : _longest;
        }
    }

    private void ResetIfHealthy()
    {
        if (_answered is not null && Stopwatch.GetElapsedTime(_answeredAt) >= _healthyAfter)
            _timeout = _first;
    }

    private static TimeSpan Twice(TimeSpan timeout) =>
        timeout == Timeout.InfiniteTimeSpan ? timeout
        : timeout >= s_limit / 2 ? s_limit
        : timeout * 2;
}

namespace Darp.CodeEditor.Tests;

/// <summary>A clock that moves only when a test advances it, for restart delays and time limits.</summary>
/// <remarks>Timers fire on the thread that advances the clock, in the order they are due.</remarks>
internal sealed class ManualTime : TimeProvider
{
    private static readonly DateTimeOffset s_start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private TimeSpan _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
            return s_start + _now;
    }

    public override long GetTimestamp()
    {
        lock (_lock)
            return _now.Ticks;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock on, and fires the timers due meanwhile.</summary>
    public void Advance(TimeSpan time)
    {
        TimeSpan end;
        lock (_lock)
            end = _now + time;
        while (true)
        {
            ManualTimer? due;
            lock (_lock)
            {
                due = _timers.Where(t => t.Due <= end).MinBy(t => t.Due);
                if (due is null)
                {
                    _now = end;
                    return;
                }
                _now = due.Due;
                if (due.Period == Timeout.InfiniteTimeSpan)
                    _timers.Remove(due);
                else
                    due.Due += due.Period;
            }
            due.Fire();
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTime _time;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualTime time, TimerCallback callback, object? state)
        {
            _time = time;
            _callback = callback;
            _state = state;
        }

        /// <summary>Guarded by the clock's lock.</summary>
        public TimeSpan Due { get; set; }

        /// <summary>Guarded by the clock's lock.</summary>
        public TimeSpan Period { get; private set; }

        public void Fire() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_time._lock)
            {
                _time._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                    return true;
                Due = _time._now + dueTime;
                Period = period;
                _time._timers.Add(this);
            }
            return true;
        }

        public void Dispose()
        {
            lock (_time._lock)
                _time._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

namespace Darp.CodeEditor.Tests;

/// <summary>
/// A clock that moves on by <see cref="Step"/> each time it is read, on any thread: a search on it runs out of its time
/// limit after a known number of looks at it, whatever the machine, and with a step of zero never.
/// </summary>
internal sealed class SteppingTime : TimeProvider
{
    private long _now;
    private long _step;

    public TimeSpan Step
    {
        get => new(Volatile.Read(ref _step));
        set => Volatile.Write(ref _step, value.Ticks);
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Add(ref _now, Volatile.Read(ref _step));
}

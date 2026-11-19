namespace Darp.CodeEditor.Tests;

/// <summary>
/// A clock that moves on by <see cref="Step"/> each time it is read, on any thread: a search on it runs out of its time
/// limit after a known number of looks at it, whatever the machine, and with a step of zero never.
/// </summary>
internal sealed class SteppingTime : TimeProvider
{
    private int _readsOnUiThread;
    private long _now;
    private long _step;

    public TimeSpan Step
    {
        get => new(Volatile.Read(ref _step));
        set => Volatile.Write(ref _step, value.Ticks);
    }

    /// <summary>
    /// The managed id of the UI thread, for <see cref="ReadOnUiThread"/>. Not taken from the dispatcher: the first
    /// thread that asks for it becomes the UI thread, which a test outside the headless session must not be.
    /// </summary>
    public int UiThread { get; init; } = -1;

    /// <summary>
    /// Whether <see cref="UiThread"/> read the clock: the search's work ran there, not in the background.
    /// </summary>
    public bool ReadOnUiThread => Volatile.Read(ref _readsOnUiThread) > 0;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        if (Environment.CurrentManagedThreadId == UiThread)
            Interlocked.Increment(ref _readsOnUiThread);
        return Interlocked.Add(ref _now, Volatile.Read(ref _step));
    }
}

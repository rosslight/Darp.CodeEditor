using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Threading;

namespace Darp.CodeEditor.Instrumentation;

/// <summary>
/// Measures how long the UI thread takes to run a highest-priority callback, and records GC pauses.
/// </summary>
/// <remarks>
/// A background thread posts one callback at a time and waits until it ran, then sleeps for the interval. The latency
/// of a callback is how long the UI thread was busy with other work, so a 100 ms stall shows up as one 100 ms sample.
/// Samples are grouped into named phases; start a phase with <see cref="BeginPhase"/> and read it with
/// <see cref="EndPhase"/>, both on the UI thread. A stall that spans a phase boundary is split between the phases.
/// GC pauses stop the UI thread too, so each stall reports how much of it overlapped a GC pause.
/// </remarks>
public sealed class UiStallProbe : IDisposable
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(1);

    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _interval;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _callbackDone = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<GcPause> _gcPauses = new();
    private long _pendingPostedAt;

    // Only touched on the UI thread.
    private Phase? _phase;

    public UiStallProbe(Dispatcher dispatcher, TimeSpan interval)
    {
        _dispatcher = dispatcher;
        _interval = interval;
        _thread = new Thread(Run) { IsBackground = true, Name = "UI stall probe" };
        _thread.Start();
    }

    /// <summary>Starts collecting samples into a new phase. Must be called on the UI thread.</summary>
    public void BeginPhase(string name)
    {
        Dispatcher.UIThread.VerifyAccess();
        _phase = new Phase(name, _gcPauses.Count);
    }

    /// <summary>Ends the current phase and returns its statistics. Must be called on the UI thread.</summary>
    public StallReport EndPhase()
    {
        Dispatcher.UIThread.VerifyAccess();
        Phase phase = _phase ?? throw new InvalidOperationException("No phase was started.");
        _phase = null;
        // A callback still waiting means the UI thread has been busy up to now: count that part in this phase.
        long pendingPostedAt = Volatile.Read(ref _pendingPostedAt);
        if (pendingPostedAt != 0)
            phase.Add(pendingPostedAt, Stopwatch.GetTimestamp());
        return phase.ToReport([.. _gcPauses.Skip(phase.FirstGcPause)]);
    }

    public void Dispose()
    {
        _stop.Cancel();
        // The UI thread may already have stopped, so the probe thread may never see its last callback run.
        _thread.Join(TimeSpan.FromSeconds(1));
    }

    private void Run()
    {
        CancellationToken token = _stop.Token;
        int lastGcIndex = (int)GC.GetGCMemoryInfo(GCKind.Any).Index;
        while (!token.IsCancellationRequested)
        {
            long posted = Stopwatch.GetTimestamp();
            _callbackDone.Reset();
            Volatile.Write(ref _pendingPostedAt, posted);
            _dispatcher.Post(
                () =>
                {
                    Volatile.Write(ref _pendingPostedAt, 0);
                    _phase?.Add(posted, Stopwatch.GetTimestamp());
                    _callbackDone.Set();
                },
                DispatcherPriority.Send
            );
            try
            {
                // Poll for GC pauses while waiting; GCMemoryInfo only describes the latest GC.
                while (!_callbackDone.Wait(s_pollInterval, token))
                    RecordGcPause(ref lastGcIndex);
                RecordGcPause(ref lastGcIndex);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            token.WaitHandle.WaitOne(_interval);
        }
    }

    private void RecordGcPause(ref int lastGcIndex)
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.Any);
        if (info.Index == lastGcIndex)
            return;
        lastGcIndex = (int)info.Index;
        TimeSpan pause = TimeSpan.Zero;
        foreach (TimeSpan duration in info.PauseDurations)
            pause += duration;
        _gcPauses.Enqueue(
            new GcPause(
                Stopwatch.GetTimestamp(),
                pause.TotalMilliseconds,
                info.Generation,
                info.Compacted,
                info.Concurrent,
                info.HeapSizeBytes
            )
        );
    }

    /// <param name="ObservedAt">Stopwatch timestamp shortly after the GC ended.</param>
    private readonly record struct GcPause(
        long ObservedAt,
        double PauseMs,
        int Generation,
        bool Compacted,
        bool Concurrent,
        long HeapSizeBytes
    );

    private sealed class Phase(string name, int firstGcPause)
    {
        private readonly string _name = name;
        private readonly long _start = Stopwatch.GetTimestamp();
        private readonly List<double> _latencies = [];
        private readonly List<(long Start, long End)> _stalls = [];

        public int FirstGcPause { get; } = firstGcPause;

        /// <summary>Adds the time the UI thread was busy, counting only the part inside this phase.</summary>
        public void Add(long postedAt, long ranAt)
        {
            long start = Math.Max(postedAt, _start);
            double latencyMs = Stopwatch.GetElapsedTime(start, ranAt).TotalMilliseconds;
            _latencies.Add(latencyMs);
            if (latencyMs > StallReport.FrameBudgetMs)
                _stalls.Add((start, ranAt));
        }

        public StallReport ToReport(GcPause[] gcPauses)
        {
            double[] sorted = [.. _latencies];
            Array.Sort(sorted);
            Stall[] stalls =
            [
                .. _stalls.Select(s => new Stall(
                    Stopwatch.GetElapsedTime(_start, s.Start).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(s.Start, s.End).TotalMilliseconds,
                    GcOverlapMs(gcPauses, s.Start, s.End)
                )),
            ];
            return new StallReport(
                _name,
                Stopwatch.GetElapsedTime(_start),
                sorted.Length,
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.99),
                sorted.Length == 0 ? 0 : sorted[^1],
                stalls.Length,
                stalls.Count(s => s.LatencyMs > StallReport.FailureMs),
                gcPauses.Sum(p => p.PauseMs),
                gcPauses.Length,
                gcPauses.Length == 0 ? 0 : gcPauses.Max(p => p.PauseMs),
                gcPauses.Length == 0 ? "" : Describe(gcPauses.MaxBy(p => p.PauseMs)),
                [.. stalls.OrderByDescending(s => s.LatencyMs).Take(10)]
            );
        }

        private static string Describe(GcPause pause) =>
            $"gen{pause.Generation}{(pause.Concurrent ? " background" : " blocking")}{(pause.Compacted ? " compacting" : "")}, heap {pause.HeapSizeBytes / (1024 * 1024)} MB";

        // GC pauses are observed after they end, so their interval is approximated as [observed - pause, observed].
        private static double GcOverlapMs(GcPause[] pauses, long start, long end)
        {
            double overlap = 0;
            foreach (GcPause pause in pauses)
            {
                long pauseTicks = (long)(pause.PauseMs * Stopwatch.Frequency / 1000);
                long pauseStart = pause.ObservedAt - pauseTicks;
                long overlapTicks = Math.Min(end, pause.ObservedAt) - Math.Max(start, pauseStart);
                if (overlapTicks > 0)
                    overlap += overlapTicks * 1000.0 / Stopwatch.Frequency;
            }
            return overlap;
        }

        private static double Percentile(double[] sorted, double percentile) =>
            sorted.Length == 0
                ? 0
                : sorted[(int)Math.Min(sorted.Length - 1, Math.Ceiling(percentile * sorted.Length) - 1)];
    }
}

/// <param name="AtMs">When the stall started, relative to the start of the phase.</param>
/// <param name="GcMs">How much of the stall overlapped GC pauses.</param>
public readonly record struct Stall(double AtMs, double LatencyMs, double GcMs);

public sealed record StallReport(
    string Phase,
    TimeSpan Duration,
    int Samples,
    double P50Ms,
    double P99Ms,
    double MaxMs,
    int StallsOver16Ms,
    int StallsOver50Ms,
    double GcPauseMs,
    int GcCount,
    double MaxGcPauseMs,
    string MaxGcPauseKind,
    IReadOnlyList<Stall> WorstStalls
)
{
    public const double FrameBudgetMs = 16;
    public const double FailureMs = 50;

    public override string ToString() =>
        $"{Phase, -44} {Duration.TotalMilliseconds, 7:F0} ms | n {Samples, 5} | p50 {P50Ms, 5:F2} | p99 {P99Ms, 6:F2} | "
        + $"max {MaxMs, 7:F2} ms | >16ms {StallsOver16Ms, 2} | >50ms {StallsOver50Ms, 2} | "
        + $"GC {GcCount, 3} pauses, {GcPauseMs, 6:F1} ms total, max {MaxGcPauseMs, 5:F1} ms ({MaxGcPauseKind})"
        + (
            WorstStalls.Count == 0
                ? ""
                : $"\n    worst stalls (at ms: length ms, GC overlap ms): {string.Join(", ", WorstStalls.Take(6).Select(s => $"{s.AtMs:F0}: {s.LatencyMs:F0} (gc {s.GcMs:F0})"))}"
        );
}

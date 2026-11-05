using System.Collections.Concurrent;
using System.Threading.Channels;
using Darp.CodeEditor.CSharpScript;

namespace UseCases.ScriptApi;

// The runner side of the use cases' script API: ordinary .NET objects in the runner, with no attributes and no
// copying. Drivers and helpers belong here.
//
// - Session: once per session. It implements the session side of the bridge, holds what the session's scripts share,
//   and owns what outlives a run.
// - ScriptGlobals and ReaderGlobals: once per run, for two kinds of scripts over the same session object. They own
//   what belongs to their run, and end it when the session disposes them at the run's end: triggers stop taking calls
//   and wait for the ones already running, and queued values go out. IScriptGlobals tells the compiler what each
//   needs: the session object and, for ScriptGlobals, the run's input.
//
// Calls from the app and from overlapping scripts reach the session object on any thread, so its state is either
// immutable, published with volatile, or in concurrent collections. Scripts must not hold a lock across a call to the
// app: the app may call back meanwhile.

/// <summary>
/// The session object: the session side of the bridge, and the memory that the session's scripts share. The session
/// creates it once and disposes it at its end.
/// </summary>
public sealed class Session : ISessionSide, IScriptSession<Session, IAppSide>, IDisposable
{
    private readonly ConcurrentDictionary<string, TriggerRegistration> _triggers = new();
    private volatile string _name = "";
    private volatile Func<double, double>? _calculate;

    private Session(IAppSide app) => App = app;

    /// <inheritdoc />
    public static Session Create(IAppSide app) => new(app);

    /// <summary>The app's half of the bridge.</summary>
    public IAppSide App { get; }

    /// <summary>
    /// Messages from one script to another, in order. They never leave the runner. Writers wait while 1,024 are
    /// unread.
    /// </summary>
    public Channel<string> Messages { get; } = Channel.CreateBounded<string>(1024);

    /// <inheritdoc />
    public Task InitializeAsync(string name, CancellationToken cancellationToken)
    {
        _name = name;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(
            $"{_name}: {(_calculate is null ? "no calculation" : "calculation set")}, {_triggers.Count} triggers"
        );

    /// <inheritdoc />
    public Task<double> CalculateAsync(double input, CancellationToken cancellationToken) =>
        Task.FromResult(Calculation()(input));

    /// <inheritdoc />
    public Task<double[]> CalculateManyAsync(double[] inputs, CancellationToken cancellationToken) =>
        Task.FromResult(Array.ConvertAll(inputs, new Converter<double, double>(Calculation())));

    /// <inheritdoc />
    public Task TriggerAsync(string topic, CancellationToken cancellationToken) =>
        _triggers.TryGetValue(topic, out TriggerRegistration? registration)
            ? registration.InvokeAsync(cancellationToken)
            : throw new InvalidOperationException($"No run answers '{topic}'.");

    /// <summary>Sets the calculation that the app calls, until another script replaces it or the session ends.</summary>
    public void SetCalculation(Func<double, double> calculate) => _calculate = calculate;

    /// <summary>
    /// Lets the app trigger <paramref name="topic"/> until the returned registration is disposed. The handler's token
    /// is cancelled when the app cancels the call, when <paramref name="stop"/> is cancelled, or when the
    /// registration closes.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another run answers <paramref name="topic"/>.</exception>
    public IAsyncDisposable RegisterTrigger(string topic, Func<CancellationToken, Task> handler, CancellationToken stop)
    {
        var registration = new TriggerRegistration(_triggers, topic, handler, stop);
        if (!_triggers.TryAdd(topic, registration))
            throw new InvalidOperationException($"Another run answers '{topic}'.");
        return registration;
    }

    /// <summary>Tells the session's scripts that no more messages come.</summary>
    public void Dispose() => Messages.Writer.TryComplete();

    private Func<double, double> Calculation() =>
        _calculate ?? throw new InvalidOperationException("No script has set a calculation yet.");

    /// <summary>
    /// One run's handler for a topic. Disposing removes it, never one that another run registered since, closes it to
    /// new calls, cancels the calls already running and waits for them.
    /// </summary>
    private sealed class TriggerRegistration : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, TriggerRegistration> _triggers;
        private readonly string _topic;
        private readonly Func<CancellationToken, Task> _handler;
        private readonly CancellationTokenSource _closing;
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;
        private bool _closed;

        public TriggerRegistration(
            ConcurrentDictionary<string, TriggerRegistration> triggers,
            string topic,
            Func<CancellationToken, Task> handler,
            CancellationToken stop
        )
        {
            _triggers = triggers;
            _topic = topic;
            _handler = handler;
            _closing = CancellationTokenSource.CreateLinkedTokenSource(stop);
        }

        public async Task InvokeAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_closed)
                    throw new InvalidOperationException($"No run answers '{_topic}'.");
                _running++;
            }
            try
            {
                using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
                await _handler(call.Token);
            }
            finally
            {
                lock (_gate)
                {
                    if (--_running == 0 && _closed)
                        _drained.TrySetResult();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _triggers.TryRemove(KeyValuePair.Create(_topic, this));
            lock (_gate)
            {
                _closed = true;
                if (_running == 0)
                    _drained.TrySetResult();
            }
            await _closing.CancelAsync();
            await _drained.Task;
            _closing.Dispose();
        }
    }
}

/// <summary>What ordinary scripts use without qualification. The session creates one per run.</summary>
public sealed class ScriptGlobals : IScriptGlobals<ScriptGlobals, Session, RunInput>, IAsyncDisposable
{
    private readonly Session _session;
    private readonly ValueSender _values;
    private readonly Lock _gate = new();
    private readonly List<IAsyncDisposable> _registrations = [];
    private bool _ending;

    private ScriptGlobals(Session session, RunInput input, CancellationToken stop)
    {
        _session = session;
        _values = new ValueSender(session.App, input.RunId);
        Input = input;
        Stop = stop;
    }

    /// <inheritdoc />
    public static ScriptGlobals Create(Session session, RunInput input, CancellationToken stop) =>
        new(session, input, stop);

    /// <summary>What the app gave this run.</summary>
    public RunInput Input { get; }

    /// <summary>Cancelled when the app cancels this run, or when the run ends. Pass it to everything that waits.</summary>
    public CancellationToken Stop { get; }

    /// <summary>Messages to and from the session's other scripts.</summary>
    public Channel<string> Messages => _session.Messages;

    /// <summary>Script to app, a call: asks the app and waits for the answer.</summary>
    public Task<string> AskAsync(string question) => _session.App.AskAsync(question, Stop);

    /// <summary>Script to app, a notification, marked with this run's id.</summary>
    public Task NotifyAsync(string message) => _session.App.NotifyAsync(Input.RunId, message, Stop);

    /// <summary>
    /// Script to app, at high rates: queues a value for the app. Values go out in batches, in order, and all of them
    /// have reached the app when the run has ended. Waits while 65,536 are queued.
    /// </summary>
    public ValueTask SendAsync(double value) => _values.WriteAsync(value, Stop);

    /// <summary>
    /// App to script, a callback that the session owns: the app calls it until another script replaces it or the
    /// session ends. It outlives this run, so it must not use this run's globals.
    /// </summary>
    public void SetCalculation(Func<double, double> calculate) => _session.SetCalculation(calculate);

    /// <summary>
    /// App to script, a callback that this run owns: the app can trigger it until this run ends, and the run ends only
    /// after the calls already running. The handler's token is cancelled when the app cancels the call or the run
    /// stops.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another run answers <paramref name="topic"/>.</exception>
    /// <exception cref="ObjectDisposedException">The run is ending.</exception>
    public void OnTrigger(string topic, Func<CancellationToken, Task> handler)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_ending, this);
            _registrations.Add(_session.RegisterTrigger(topic, handler, Stop));
        }
    }

    /// <summary>Keeps the run going, with its triggers, until the app cancels it.</summary>
    public Task WaitUntilStoppedAsync() => Task.Delay(Timeout.Infinite, Stop);

    /// <summary>
    /// Ends this run's triggers, waiting for the calls already running, then sends its last values. The session calls
    /// it when the run's code has finished, before the run's Completion.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        IAsyncDisposable[] registrations;
        lock (_gate)
        {
            _ending = true;
            registrations = [.. _registrations];
        }
        foreach (IAsyncDisposable registration in registrations)
            await registration.DisposeAsync();
        await _values.DisposeAsync();
    }
}

/// <summary>
/// What reader scripts use without qualification: a second kind of script, which only reads the session's messages
/// and sends values on. Its runs take no input. The session creates one per run.
/// </summary>
public sealed class ReaderGlobals : IScriptGlobals<ReaderGlobals, Session>, IAsyncDisposable
{
    private const string RunId = "reader";
    private readonly ValueSender _values;

    private ReaderGlobals(Session session, CancellationToken stop)
    {
        _values = new ValueSender(session.App, RunId);
        Messages = session.Messages.Reader;
        Stop = stop;
    }

    /// <inheritdoc />
    public static ReaderGlobals Create(Session session, CancellationToken stop) => new(session, stop);

    /// <summary>Cancelled when the app cancels this run, or when the run ends.</summary>
    public CancellationToken Stop { get; }

    /// <summary>The messages that the session's other scripts write.</summary>
    public ChannelReader<string> Messages { get; }

    /// <summary>Script to app, at high rates: queues a value for the app, as for ordinary scripts, marked "reader".</summary>
    public ValueTask SendAsync(double value) => _values.WriteAsync(value, Stop);

    /// <summary>Sends this run's last values. The session calls it when the run's code has finished.</summary>
    public ValueTask DisposeAsync() => _values.DisposeAsync();
}

/// <summary>
/// Collects one run's values in the runner and sends them to the app in batches. While a batch is on its way, new
/// values wait for the next one, so batches grow with the load and a lone value goes out at once.
/// </summary>
internal sealed class ValueSender : IAsyncDisposable
{
    private const int MaxBatch = 1024;
    private readonly IAppSide _app;
    private readonly string _runId;
    private readonly Channel<double> _values = Channel.CreateBounded<double>(
        new BoundedChannelOptions(65_536) { SingleReader = true }
    );
    private readonly Task _sending;

    public ValueSender(IAppSide app, string runId)
    {
        _app = app;
        _runId = runId;
        _sending = SendAsync();
    }

    public ValueTask WriteAsync(double value, CancellationToken cancellationToken) =>
        _values.Writer.WriteAsync(value, cancellationToken);

    /// <summary>Sends what is queued, and waits until the app has it.</summary>
    public async ValueTask DisposeAsync()
    {
        _values.Writer.TryComplete();
        await _sending;
    }

    private async Task SendAsync()
    {
        var batch = new List<double>(MaxBatch);
        try
        {
            while (await _values.Reader.WaitToReadAsync())
            {
                while (batch.Count < MaxBatch && _values.Reader.TryRead(out double value))
                    batch.Add(value);
                // No token: queued values go out even after the run's stop token is cancelled. If the app stops
                // receiving, the run ends only when the session is disposed or killed.
                await _app.ReceiveAsync(_runId, [.. batch], CancellationToken.None);
                batch.Clear();
            }
        }
        catch (Exception exception)
        {
            // Writers waiting for room learn that the app no longer receives.
            _values.Writer.TryComplete(exception);
            throw;
        }
    }
}

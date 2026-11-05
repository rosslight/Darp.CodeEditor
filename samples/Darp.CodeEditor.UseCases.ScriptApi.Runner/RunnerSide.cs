using System.Threading.Channels;
using Darp.CodeEditor.CSharpScript.Runner;

namespace UseCases.ScriptApi;

// The runner side of the use cases' script API: ordinary .NET objects in the runner, with no attributes and no
// copying. Drivers and helpers belong here.
//
// - Session: once per session. It implements the session side of the bridge, holds what the session's scripts share,
//   and owns what outlives a run. It also decides which kinds of scripts run in it: it creates the globals of each
//   run, one factory per kind, and gives each kind only what it uses.
// - ScriptGlobals and ReaderGlobals: once per run, for two kinds of scripts. They are ordinary classes: ScriptGlobals
//   gets the session object and the run's input, ReaderGlobals only the messages and the app. They own what belongs
//   to their run, and end it when the session disposes them at the run's end: queued values go out.
// - A callback that a script hands over outlives its run: the calculation stays in the session object until another
//   script replaces it. So it may only use what outlives its run, and the library does not track it.
//
// Calls from the app and from overlapping scripts reach the session object on any thread, so its state is either
// immutable, published with volatile, or in concurrent collections. Scripts must not hold a lock across a call to the
// app: the app may call back meanwhile.
//
// Create, the factories and the disposal are implemented explicitly, and the globals' constructors are internal: the
// public members of the globals are what scripts see, and only the session creates and disposes these objects.

/// <summary>
/// The session object: the session side of the bridge, and the memory that the session's scripts share. The session
/// creates it once and disposes it at its end.
/// </summary>
public sealed class Session
    : ISessionSide,
        IScriptSessionObject<Session, IAppSide>,
        IScriptGlobalsFactory<ScriptGlobals, RunInput>,
        IScriptGlobalsFactory<ReaderGlobals>,
        IDisposable
{
    private volatile string _name = "";
    private volatile Func<double, double>? _calculate;

    private Session(IAppSide app) => App = app;

    /// <inheritdoc />
    static Session IScriptSessionObject<Session, IAppSide>.Create(IAppSide app) => new(app);

    /// <inheritdoc />
    ScriptGlobals IScriptGlobalsFactory<ScriptGlobals, RunInput>.CreateGlobals(
        RunInput input,
        CancellationToken stop
    ) => new(this, input, stop);

    /// <inheritdoc />
    ReaderGlobals IScriptGlobalsFactory<ReaderGlobals>.CreateGlobals(CancellationToken stop) =>
        new(App, Messages.Reader, stop);

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
        Task.FromResult($"{_name}: {(_calculate is null ? "no calculation" : "calculation set")}");

    /// <inheritdoc />
    public Task<double> CalculateAsync(double input, CancellationToken cancellationToken) =>
        Task.FromResult(Calculation()(input));

    /// <inheritdoc />
    public Task<double[]> CalculateManyAsync(double[] inputs, CancellationToken cancellationToken) =>
        Task.FromResult(Array.ConvertAll(inputs, new Converter<double, double>(Calculation())));

    /// <summary>Sets the calculation that the app calls, until another script replaces it or the session ends.</summary>
    public void SetCalculation(Func<double, double> calculate) => _calculate = calculate;

    /// <summary>Tells the session's scripts that no more messages come.</summary>
    void IDisposable.Dispose() => Messages.Writer.TryComplete();

    private Func<double, double> Calculation() =>
        _calculate ?? throw new InvalidOperationException("No script has set a calculation yet.");
}

/// <summary>What ordinary scripts use without qualification. The session creates one per run.</summary>
public sealed class ScriptGlobals : IAsyncDisposable
{
    private readonly Session _session;
    private readonly ValueSender _values;

    internal ScriptGlobals(Session session, RunInput input, CancellationToken stop)
    {
        _session = session;
        _values = new ValueSender(session.App, input.RunId);
        Input = input;
        Stop = stop;
    }

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
    /// session ends. It outlives this run, so it may only use what outlives the run: not these globals, the run's
    /// input, or what they own.
    /// </summary>
    public void SetCalculation(Func<double, double> calculate) => _session.SetCalculation(calculate);

    /// <summary>Sends this run's last values. The session calls it when the run's code has finished.</summary>
    ValueTask IAsyncDisposable.DisposeAsync() => _values.DisposeAsync();
}

/// <summary>
/// What reader scripts use without qualification: a second kind of script, which only reads the session's messages
/// and sends values on. Its runs take no input. The session creates one per run.
/// </summary>
public sealed class ReaderGlobals : IAsyncDisposable
{
    private const string RunId = "reader";
    private readonly ValueSender _values;

    internal ReaderGlobals(IAppSide app, ChannelReader<string> messages, CancellationToken stop)
    {
        _values = new ValueSender(app, RunId);
        Messages = messages;
        Stop = stop;
    }

    /// <summary>Cancelled when the app cancels this run, or when the run ends.</summary>
    public CancellationToken Stop { get; }

    /// <summary>The messages that the session's other scripts write.</summary>
    public ChannelReader<string> Messages { get; }

    /// <summary>Script to app, at high rates: queues a value for the app, as for ordinary scripts, marked "reader".</summary>
    public ValueTask SendAsync(double value) => _values.WriteAsync(value, Stop);

    /// <summary>Sends this run's last values. The session calls it when the run's code has finished.</summary>
    ValueTask IAsyncDisposable.DisposeAsync() => _values.DisposeAsync();
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

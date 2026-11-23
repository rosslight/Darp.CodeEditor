using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;
using Darp.CodeEditor.Lsp;
using PolyType;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace Darp.CodeEditor.CSharpScript.Runner;

/// <summary>
/// The app's end of a session: its process, its connection and its one lifecycle, under one lock, which decides what
/// the session admits. It is <see cref="Lifecycle.Running"/>, then <see cref="Lifecycle.Closing"/> from the moment the
/// app disposes it, or <see cref="Lifecycle.Ended"/> when its process ended first.
/// </summary>
internal sealed class SessionConnection
{
    /// <summary>How many lines wait for a slow <see cref="CSharpScriptSessionOptions.Output"/>; older ones are dropped.</summary>
    private const int MaxQueuedOutput = 10_000;

    /// <summary>How long a new process may take to answer.</summary>
    private static readonly TimeSpan s_startLimit = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the end of standard error is awaited once the process has exited: a child it left behind may hold the
    /// pipe open for as long as it runs. Real time, whatever the options' clock: nothing advances a test's clock here.
    /// </summary>
    private static readonly TimeSpan s_standardErrorLimit = TimeSpan.FromMilliseconds(200);

    private readonly ServerProcess _process;
    private readonly SessionRpc _rpc;
    private readonly IRunnerControl _control;
    private readonly CSharpScriptSessionOptions _options;
    private readonly TaskCompletionSource<CSharpScriptSessionExit> _exit = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private readonly Lock _lock = new();

    /// <summary>The runs that have not ended, by their starts: what closing waits for.</summary>
    private readonly Dictionary<int, Task<CSharpScriptRun>> _runs = [];
    private Lifecycle _lifecycle;
    private CSharpScriptSessionExitReason? _cause;
    private CSharpScriptException? _disposeFailure;
    private Task? _disposal;
    private int _lastRun;

    private enum Lifecycle
    {
        Running,
        Closing,
        Ended,
    }

    private SessionConnection(ServerProcess process, SessionRpc rpc, CSharpScriptSessionOptions options)
    {
        _process = process;
        _rpc = rpc;
        _options = options;
        _control = SessionWire.Proxy<IRunnerControl>(rpc);
    }

    /// <summary>Completes once the process has ended, before the session's runs and calls fail because of it.</summary>
    internal Task<CSharpScriptSessionExit> Exited => _exit.Task;

    /// <summary>Starts the process and creates the session object in it. Tests put gates in the transport.</summary>
    internal static async Task<CSharpScriptSession<TSide, TSession>> OpenAsync<TApp, TSide, TSession>(
        TApp app,
        CSharpScriptSessionOptions options,
        CancellationToken cancellationToken,
        Func<Stream, Stream, IJsonRpcMessageHandler>? createHandler = null
    )
        where TApp : class, IShapeable<TApp>
        where TSide : class, IShapeable<TSide>
        where TSession : class, TSide
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireProxies<TApp, TSide>();
        Channel<string>? output = null;
        if (options.Output is { } receive)
        {
            output = Channel.CreateBounded<string>(
                new BoundedChannelOptions(MaxQueuedOutput)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true,
                }
            );
            _ = DeliverOutputAsync(output.Reader, receive);
        }
        ServerProcess process;
        try
        {
            process = ServerProcess.Start(
                StartInfo<TApp, TSide, TSession>(options),
                output is null ? null : line => output.Writer.TryWrite(line)
            );
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            output?.Writer.TryComplete();
            throw new InvalidOperationException($"The script session's process did not start: {e.Message}", e);
        }

        createHandler ??= SessionWire.Handler<TApp, TSide>;
        var transport = new ExitAwareHandler(createHandler(process.Input, process.Output));
        var rpc = new SessionRpc(transport) { CancelLocallyInvokedMethodsWhenConnectionIsClosed = true };
        TSide remote = SessionWire.Proxy<TSide>(rpc);
        rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<TApp>(), app, null);
        var connection = new SessionConnection(process, rpc, options);
        transport.ConnectionLost = connection.OnConnectionLost;
        rpc.RefusesBridgeCalls = connection.IsClosing;
        _ = connection.PublishExitAsync(output?.Writer);
        rpc.StartListening();
        try
        {
            RunFailure? failure = await connection
                ._control.InitializeAsync()
                .WaitAsync(s_startLimit, options.TimeProvider, cancellationToken)
                .ConfigureAwait(false);
            if (failure is null)
                return new CSharpScriptSession<TSide, TSession>(connection, remote);
            throw rpc.ExceptionFor(failure);
        }
        catch (Exception e)
        {
            await connection.KillAsync().ConfigureAwait(false);
            if (e is OperationCanceledException or CSharpScriptException)
                throw;
            CSharpScriptSessionExit exit = await connection.Exited.ConfigureAwait(false);
            throw new InvalidOperationException(
                $"The script session's process did not answer: {e.Message}\n{exit.StandardError}",
                e
            );
        }
    }

    /// <summary>
    /// Fails in every build when the script API lacks <c>[assembly: ExportRpcContractProxies]</c>. Without it the
    /// generated proxies are internal: a JIT app works, and a NativeAOT app would fail on its first call.
    /// </summary>
    private static void RequireProxies<TApp, TSide>()
    {
        foreach (Type bridge in (Type[])[typeof(TApp), typeof(TSide)])
        {
            if (!bridge.GetCustomAttributes<JsonRpcProxyMappingAttribute>(false).Any(m => m.ProxyClass.IsPublic))
            {
                throw new InvalidOperationException(
                    $"The bridge interface {bridge.Name} has no exported proxy. "
                        + "Add [assembly: ExportRpcContractProxies] to the project that declares it."
                );
            }
        }
    }

    internal static ProcessStartInfo StartInfo<TApp, TSide, TSession>(CSharpScriptSessionOptions options)
    {
        (string fileName, string[] arguments) = ScriptAppFiles.RunnerCommand();
        var info = new ProcessStartInfo(
            fileName,
            [.. arguments, ScriptAppFiles.ScriptApiRoot(), TypeName<TSession>(), TypeName<TApp>(), TypeName<TSide>()]
        );
        info.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        foreach ((string name, string? value) in options.EnvironmentVariables)
        {
            if (value is null)
                info.Environment.Remove(name);
            else
                info.Environment[name] = value;
        }
        return info;
    }

    private static string TypeName<T>() => typeof(T).AssemblyQualifiedName ?? typeof(T).Name;

    /// <summary>Hands lines to the app's callback in order, on a task of its own: it may be as slow as it likes.</summary>
    private static async Task DeliverOutputAsync(ChannelReader<string> lines, Action<string> receive)
    {
        await foreach (string line in lines.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                receive(line);
            }
            // The app's own callback: one line that it can't take must not lose the lines after it.
            catch (Exception) { }
        }
    }

    /// <summary>Starts a run, and returns it once the runner has accepted it.</summary>
    internal Task<CSharpScriptRun> StartRunAsync<TGlobals>(
        CSharpScriptAssembly<TGlobals> script,
        string inputType,
        byte[] input,
        CancellationToken cancellationToken
    )
        where TGlobals : class
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<CSharpScriptRun>(cancellationToken);
        lock (_lock)
        {
            if (_lifecycle == Lifecycle.Closing)
            {
                return Task.FromException<CSharpScriptRun>(
                    new ObjectDisposedException(nameof(CSharpScriptSession), "The session is being disposed.")
                );
            }
            if (_lifecycle == Lifecycle.Ended)
                return Task.FromException<CSharpScriptRun>(new ConnectionLostException("The process has ended."));
            int id = ++_lastRun;
            _rpc.Snapshots[id] = script.Snapshot;
            var request = new RunRequest(
                id,
                typeof(TGlobals).AssemblyQualifiedName ?? typeof(TGlobals).Name,
                inputType,
                input,
                script.Image.Assembly,
                script.Image.EntryType,
                script.Image.EntryMethod
            );
            var start = new TaskCompletionSource<CSharpScriptRun>(TaskCreationOptions.RunContinuationsAsynchronously);
            _runs[id] = start.Task;
            // Registered here, so that disposing waits for it; written outside the lock, as the pipe may be full.
            _ = Task.Run(() => FollowRunAsync(request, start));
            return start.Task;
        }
    }

    /// <summary>Starts a run in the runner and follows it to its end.</summary>
    private async Task FollowRunAsync(RunRequest request, TaskCompletionSource<CSharpScriptRun> start)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _control.BeginAsync(request).ConfigureAwait(false);
            start.SetResult(
                new CSharpScriptRun(completion.Task, limit => StopRunAsync(request.Id, completion.Task, limit))
            );
            RunOutcome outcome = await _control.JoinAsync(request.Id).ConfigureAwait(false);
            // Under the lock, so that a stop's deadline sees the run either ended or still to kill.
            lock (_lock)
            {
                if (outcome.Failures.Length > 0)
                    completion.SetException(outcome.Failures.Select(_rpc.ExceptionFor));
                else if (outcome.Stopped)
                    completion.SetCanceled();
                else
                    completion.SetResult();
            }
        }
        catch (Exception e)
        {
            if (!start.TrySetException(e))
            {
                lock (_lock)
                    completion.SetException(e);
            }
        }
        lock (_lock)
            _runs.Remove(request.Id);
    }

    private async Task StopRunAsync(int id, Task completion, TimeSpan? killSessionAfter)
    {
        if (completion.IsCompleted)
            return;
        _ = RequestStopAsync(id);
        // The deadline is for the run's end, however it ends. The stop's acknowledgement has no part in it.
        Task ended = completion.ContinueWith(static _ => { }, TaskScheduler.Default);
        if (killSessionAfter is { } limit)
        {
            try
            {
                await ended.WaitAsync(limit, _options.TimeProvider).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Kill(CSharpScriptSessionExitReason.StopTimedOut, unlessEnded: completion);
            }
        }
        await ended.ConfigureAwait(false);
    }

    private async Task RequestStopAsync(int id)
    {
        try
        {
            await _control.StopAsync(id).ConfigureAwait(false);
        }
        // The run ends with the session then, and its completion says so.
        catch (ConnectionLostException) { }
    }

    /// <summary>Ends the session in the documented order. Safe to call again.</summary>
    internal Task DisposeAsync()
    {
        lock (_lock)
        {
            _lifecycle = Lifecycle.Closing;
            return _disposal ??= Task.Run(CloseAsync);
        }
    }

    private bool IsClosing()
    {
        lock (_lock)
            return _lifecycle == Lifecycle.Closing;
    }

    private async Task CloseAsync()
    {
        try
        {
            await ShutdownAsync().WaitAsync(_options.DisposeTimeout, _options.TimeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Kill(CSharpScriptSessionExitReason.DisposeTimedOut);
        }
        // The process ended first: Exited says how.
        catch (ConnectionLostException) { }
        await AppCallsReturnedAsync().ConfigureAwait(false);
    }

    private async Task ShutdownAsync()
    {
        Task<CSharpScriptRun>[] starts;
        lock (_lock)
            starts = [.. _runs.Values];
        // The runner may dispatch the close ahead of a begin written before it.
        await ((Task)Task.WhenAll(starts)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        RunFailure? failure = await _control.CloseAsync().ConfigureAwait(false);
        await Task.WhenAll(starts.Where(s => s.IsCompletedSuccessfully).Select(s => s.Result.Completion))
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        lock (_lock)
        {
            _disposeFailure = failure is null ? null : _rpc.ExceptionFor(failure);
            if (!_exit.Task.IsCompleted && _cause is null && !_process.HasExited)
            {
                _cause = CSharpScriptSessionExitReason.Disposed;
                // The runner exits when its connection ends.
                _process.Input.Close();
            }
        }
        await _exit.Task.ConfigureAwait(false);
    }

    /// <summary>Ends the process now, then waits for the app side's calls.</summary>
    internal Task KillAsync()
    {
        Kill(CSharpScriptSessionExitReason.Killed);
        return AppCallsReturnedAsync();
    }

    /// <summary>
    /// Kills the process unless it, or the run that <paramref name="unlessEnded"/> stands for, has ended. An orderly end
    /// still under way gives way to this cause.
    /// </summary>
    private void Kill(CSharpScriptSessionExitReason reason, Task? unlessEnded = null)
    {
        lock (_lock)
        {
            if (_exit.Task.IsCompleted || _process.HasExited || unlessEnded is { IsCompleted: true })
                return;
            if (_cause is null or CSharpScriptSessionExitReason.Disposed)
                _cause = reason;
        }
        _process.Kill();
    }

    private async Task AppCallsReturnedAsync()
    {
        await _exit.Task.ConfigureAwait(false);
        await _rpc.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _rpc.DispatchCompletion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    /// The connection broke: holds the failures back until <see cref="Exited"/> is known, and ends a process that broke
    /// its connection but goes on running.
    /// </summary>
    private Task OnConnectionLost()
    {
        bool orderly;
        lock (_lock)
            orderly = _cause == CSharpScriptSessionExitReason.Disposed;
        if (!orderly)
            Kill(CSharpScriptSessionExitReason.Crashed);
        return _exit.Task;
    }

    private async Task PublishExitAsync(ChannelWriter<string>? output)
    {
        await _process.Exited.ConfigureAwait(false);
        await Task.WhenAny(_process.StandardErrorEnded, Task.Delay(s_standardErrorLimit)).ConfigureAwait(false);
        output?.TryComplete();
        int exitCode = _process.ExitCode;
        lock (_lock)
        {
            if (_lifecycle == Lifecycle.Running)
                _lifecycle = Lifecycle.Ended;
            // An orderly end that fails on its way out was a crash after all.
            CSharpScriptSessionExitReason reason =
                _cause is { } cause && (cause != CSharpScriptSessionExitReason.Disposed || exitCode == 0)
                    ? cause
                    : CSharpScriptSessionExitReason.Crashed;
            _exit.SetResult(new CSharpScriptSessionExit(reason, exitCode, _process.StandardErrorTail, _disposeFailure));
        }
        await _rpc.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _process.Dispose();
    }
}

/// <summary>The app's end of the connection: failures in the runner become the session's exceptions.</summary>
internal sealed class SessionRpc(IJsonRpcMessageHandler handler) : JsonRpc(handler)
{
    /// <summary>Each run's compiled text, for failures in it, also long after the run ended.</summary>
    internal ConcurrentDictionary<int, TextSnapshot> Snapshots { get; } = new();

    /// <summary>Whether new calls on the session side fail. Control calls and replies go on.</summary>
    internal Func<bool> RefusesBridgeCalls { get; set; } = static () => false;

    internal CSharpScriptException ExceptionFor(RunFailure failure) =>
        new(failure, Snapshots.GetValueOrDefault(failure.Run));

    // A proxy cached by the app keeps working after the end: its calls must report the lost connection.
    protected override RequestId CreateNewRequestId() =>
        IsDisposed || Completion.IsCompleted
            ? throw new ConnectionLostException("The session's process has ended.")
            : base.CreateNewRequestId();

    protected override async ValueTask<JsonRpcMessage> DispatchRequestAsync(
        JsonRpcRequest request,
        TargetMethod targetMethod,
        CancellationToken cancellationToken
    )
    {
        // An app callback on the token that throws must not interrupt StreamJsonRpc's shutdown of the pipe.
        using var appCall = new CancellationTokenSource();
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try
            {
                appCall.Cancel();
            }
            catch (AggregateException) { }
        });
        JsonRpcMessage response = await base.DispatchRequestAsync(request, targetMethod, appCall.Token)
            .ConfigureAwait(false);
        // An app side that cancels by itself failed: the script sees a remote failure, not its own cancellation.
        if (
            response is JsonRpcError { Error.Code: JsonRpcErrorCode.RequestCanceled } error
            && !cancellationToken.IsCancellationRequested
        )
        {
            error.Error.Code = JsonRpcErrorCode.InvocationError;
        }
        return response;
    }

    protected override Type GetErrorDetailsDataType(JsonRpcError error) => typeof(RunFailure);

    protected override RemoteRpcException CreateExceptionFromRpcError(JsonRpcRequest request, JsonRpcError response)
    {
        if (response.Error?.Code == SessionWire.SessionClosing)
            return new ConnectionLostException(response.Error.Message);
        return response.Error?.GetData(typeof(RunFailure)) is RunFailure failure
            ? ExceptionFor(failure)
            : base.CreateExceptionFromRpcError(request, response);
    }

    protected override ValueTask SendAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        if (
            message is JsonRpcRequest { IsNotification: false, Method: { } method }
            && !method.StartsWith(SessionWire.ControlPrefix, StringComparison.Ordinal)
            && RefusesBridgeCalls()
        )
        {
            throw new ConnectionLostException("The session is closing.");
        }
        return base.SendAsync(message, cancellationToken);
    }
}

/// <summary>
/// Holds a broken connection's failures back until the session knows why the process ended, since StreamJsonRpc
/// notices a broken pipe before the operating system reports the exit.
/// </summary>
internal sealed class ExitAwareHandler : IJsonRpcMessageHandler, IJsonRpcMessageBufferManager, IDisposable
{
    private readonly IJsonRpcMessageHandler _inner;

    public ExitAwareHandler(IJsonRpcMessageHandler inner) => _inner = inner;

    /// <summary>Called when the connection breaks; completes once the session has published its exit.</summary>
    public Func<Task>? ConnectionLost { get; set; }

    public bool CanRead => _inner.CanRead;

    public bool CanWrite => _inner.CanWrite;

    public IJsonRpcMessageFormatter Formatter => _inner.Formatter;

    public void DeserializationComplete(JsonRpcMessage message) =>
        ((IJsonRpcMessageBufferManager)_inner).DeserializationComplete(message);

    public void Dispose() => ((IDisposable)_inner).Dispose();

    public async ValueTask<JsonRpcMessage?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            JsonRpcMessage? message = await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (message is null && ConnectionLost is { } lost)
                await lost().ConfigureAwait(false);
            return message;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested && ConnectionLost is not null)
        {
            await ConnectionLost().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask WriteAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await _inner.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            if (ConnectionLost is { } lost)
                await lost().ConfigureAwait(false);
            throw new ConnectionLostException("The connection to the session's process was lost while writing.", e);
        }
    }
}

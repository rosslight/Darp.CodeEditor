using System.Collections.Concurrent;
using System.Diagnostics;
using Darp.CodeEditor.CSharp.Execution.Protocol;
using Darp.CodeEditor.CSharp.Hosting;
using PolyType;
using PolyType.Abstractions;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Darp.CodeEditor.CSharp.Execution.Hosting;

/// <summary>One runner process and the connection to it. It ends once; the session then starts another.</summary>
internal sealed class RunnerConnection : IAsyncDisposable
{
    /// <summary>How long a killed runner may take to exit before its exit code is given up on.</summary>
    private static readonly TimeSpan s_exitTimeout = TimeSpan.FromSeconds(2);

    private readonly CSharpScriptRunnerOptions _options;
    private readonly RunnerProcess _process;
    private readonly ShapeProviders _shapes;
    private readonly HostRpc _rpc;
    private readonly IRunnerService _runner;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<CompileResult>> _compiles = new();
    private readonly TaskCompletionSource<ScriptRunnerExitedEventArgs?> _ended = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly CancellationTokenSource _endedToken = new();
    private Deadlines? _deadlines;
    private int _ending;
    private long _nextScriptId;

    private RunnerConnection(CSharpScriptRunnerOptions options, RunnerProcess process)
    {
        _options = options;
        _process = process;
        _shapes = new ShapeProviders();
        foreach (CSharpScriptService service in options.Services)
            _shapes.Add(service.Shapes);
        _rpc = new HostRpc(RunnerRpc.CreateMessageHandler(process.Output, process.Input, _shapes));
        foreach (CSharpScriptService service in options.Services)
        {
            _rpc.AddLocalRpcTarget(
                service.Metadata,
                service.Implementation,
                new JsonRpcTargetOptions
                {
                    MethodNameTransform = CommonMethodNameTransforms.Prepend(
                        RunnerNames.ServicePrefix(service.ServiceType.FullName ?? "")
                    ),
                }
            );
        }
        _rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IRunnerHost>(), new Callbacks(this), null);
        // The generated proxy replaces Attach only on a receiver typed JsonRpc.
        JsonRpc rpc = _rpc;
        _runner = rpc.Attach<IRunnerService>();
        _rpc.Disconnected += (_, _) => End(ScriptRunnerExitReason.Crashed);
        _ = process.Exited.ContinueWith(
            _ => End(ScriptRunnerExitReason.Crashed),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    public int ProcessId => _process.Id;

    public bool IsAlive => Volatile.Read(ref _ending) == 0;

    /// <summary>Completes when the connection ended; with the reason, or null when it was disposed.</summary>
    public Task<ScriptRunnerExitedEventArgs?> Ended => _ended.Task;

    public static async Task<RunnerConnection> StartAsync(
        CSharpScriptRunnerOptions options,
        CancellationToken cancellationToken
    )
    {
        RunnerLocation location = RunnerLocation.Find(options.RunnerDirectory);
        RunnerProcess process = await RunnerProcess
            .StartAsync(location, options.RunnerEnvironmentVariables, options.Output)
            .ConfigureAwait(false);
        var connection = new RunnerConnection(options, process);
        try
        {
            await connection.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<T> EvaluateAsync<T>(string code, CancellationToken cancellationToken)
        where T : class, IShapeable<T>
    {
        _shapes.Add(TypeShapeResolver.Resolve<T>().Provider);
        long id = Interlocked.Increment(ref _nextScriptId);
        var compiled = new TaskCompletionSource<CompileResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _compiles[id] = compiled;
        bool started = false;
        try
        {
            // Ending fails the pending compiles; one added after that would wait for nothing.
            _endedToken.Token.ThrowIfCancellationRequested();
            await _rpc.NotifyAsync(
                    nameof(IRunnerService.Compile),
                    [id, code, RunnerTypeName.Of(typeof(T))],
                    [typeof(long), typeof(string), typeof(RunnerTypeName)]
                )
                .ConfigureAwait(false);
            CompileResult result = await compiled
                .Task.WaitAsync(_options.CompileTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (result.Error is { } error)
                throw new InvalidOperationException(error);
            if (!result.Succeeded)
                throw new ScriptCompilationException(result.Diagnostics);
            started = true;
            return await _rpc.InvokeWithCancellationAsync<T>(
                    nameof(IRunnerService.RunAsync),
                    [id],
                    [typeof(long)],
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_endedToken.IsCancellationRequested)
        {
            throw new ConnectionLostException();
        }
        finally
        {
            _compiles.TryRemove(id, out _);
            if (!started)
                await ReleaseAsync(id).ConfigureAwait(false);
        }
    }

    /// <summary>Kills the runner and what it started, and fails pending calls.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _ending, 1) == 0)
        {
            // Kill rather than close the input: a runner that exits on its own leaves the processes scripts started.
            _process.Kill();
            Stop();
            _ended.TrySetResult(null);
        }
        try
        {
            await _process.Exited.WaitAsync(s_exitTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        await _ended.Task.ConfigureAwait(false);
        _process.Dispose();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _rpc.StartListening();
        string environment = CSharpScriptInitializationOptions.Create(_options.Environment).ToJsonString();
        RunnerTypeName[] services = [.. _options.Services.Select(s => RunnerTypeName.Of(s.ServiceType))];
        // Starting has its own timeout: request deadlines and the watchdog apply from here on.
        try
        {
            await _runner
                .StartAsync(new RunnerStart(environment, services), cancellationToken)
                .WaitAsync(_options.StartTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ScriptException exception)
        {
            throw new InvalidOperationException(
                $"The runner could not load the script environment: {exception.Message}",
                exception
            );
        }
        if (_options.CallTimeout != Timeout.InfiniteTimeSpan)
        {
            _rpc.OutboundRequestTimeout = _options.CallTimeout;
            _deadlines = new Deadlines(
                _options.CallTimeout + _options.CancellationGracePeriod,
                () => End(ScriptRunnerExitReason.Killed)
            );
            _rpc.Deadlines = _deadlines;
        }
    }

    /// <summary>Tells the runner to forget a script. Best effort: it never hides the failure that led here.</summary>
    private async Task ReleaseAsync(long id)
    {
        if (!IsAlive)
            return;
        try
        {
            await _rpc.NotifyAsync(nameof(IRunnerService.Release), [id], [typeof(long)]).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is ConnectionLostException or ObjectDisposedException or OperationCanceledException)
        {
            // The runner is gone, and the script with it.
        }
    }

    /// <summary>Ends the connection unexpectedly: the runner crashed, or it is killed for a missed deadline.</summary>
    private void End(ScriptRunnerExitReason reason)
    {
        if (Interlocked.Exchange(ref _ending, 1) != 0)
            return;
        // Kill first: pending calls then fail with ConnectionLostException, not with a reply that raced the kill.
        _process.Kill();
        Stop();
        _ = ReportEndAsync(reason);
    }

    private async Task ReportEndAsync(ScriptRunnerExitReason reason)
    {
        try
        {
            await _process.Exited.WaitAsync(s_exitTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        string standardError = await _process.GetStandardErrorAsync().ConfigureAwait(false);
        _ended.TrySetResult(new ScriptRunnerExitedEventArgs(reason, _process.ExitCode, standardError));
    }

    private void Stop()
    {
        _deadlines?.Dispose();
        _endedToken.Cancel();
        // Closes the runner's input and fails pending calls with ConnectionLostException.
        _rpc.Dispose();
        foreach (TaskCompletionSource<CompileResult> compile in _compiles.Values)
            compile.TrySetException(new ConnectionLostException());
    }

    private sealed class Callbacks(RunnerConnection connection) : IRunnerHost
    {
        private readonly RunnerConnection _connection = connection;

        public Task CompiledAsync(long id, CompileResult result)
        {
            if (_connection._compiles.TryGetValue(id, out TaskCompletionSource<CompileResult>? compile))
                compile.TrySetResult(result);
            return Task.CompletedTask;
        }
    }

    private sealed class HostRpc(IJsonRpcMessageHandler messageHandler) : JsonRpc(messageHandler)
    {
        public Deadlines? Deadlines { get; set; }

        protected override ValueTask SendAsync(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            if (message is JsonRpcRequest { IsResponseExpected: true } request)
                Deadlines?.Sent(request.RequestId);
            return base.SendAsync(message, cancellationToken);
        }

        protected override void OnResponseReceived(JsonRpcMessage response)
        {
            if (response is IJsonRpcMessageWithId { RequestId: var id })
                Deadlines?.Answered(id);
            base.OnResponseReceived(response);
        }

        protected override RemoteRpcException CreateExceptionFromRpcError(
            JsonRpcRequest request,
            JsonRpcError response
        ) =>
            response.Error is { Code: JsonRpcErrorCode.InvocationError } error
                ? new ScriptException(
                    error.Message ?? "The script failed.",
                    (int)error.Code,
                    error.Data,
                    error.GetData(typeof(CommonErrorData)) as CommonErrorData
                )
                : base.CreateExceptionFromRpcError(request, response);
    }

    /// <summary>
    /// Kills the runner when a request goes unanswered past its deadline plus the grace period. StreamJsonRpc already
    /// failed the call with <see cref="TimeoutException"/> and asked the runner to cancel it at the deadline.
    /// </summary>
    private sealed class Deadlines : IDisposable
    {
        private readonly Dictionary<RequestId, long> _sent = [];
        private readonly TimeSpan _limit;
        private readonly Action _overdue;
        private readonly Timer _timer;

        public Deadlines(TimeSpan limit, Action overdue)
        {
            _limit = limit;
            _overdue = overdue;
            TimeSpan period = TimeSpan.FromMilliseconds(Math.Clamp(limit.TotalMilliseconds / 8, 10, 250));
            _timer = new Timer(_ => Check(), null, period, period);
        }

        public void Sent(RequestId id)
        {
            lock (_sent)
                _sent[id] = Stopwatch.GetTimestamp();
        }

        public void Answered(RequestId id)
        {
            lock (_sent)
                _sent.Remove(id);
        }

        public void Dispose() => _timer.Dispose();

        private void Check()
        {
            lock (_sent)
            {
                if (!_sent.Values.Any(sent => Stopwatch.GetElapsedTime(sent) > _limit))
                    return;
            }
            _overdue();
        }
    }
}

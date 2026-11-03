using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Darp.CodeEditor.CSharp.Execution.Protocol;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.Lsp.Hosting;
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

    /// <summary>How long the runner's standard error may stay open after it exited, held by a process it started.</summary>
    private static readonly TimeSpan s_standardErrorTimeout = TimeSpan.FromSeconds(1);

    private readonly CSharpScriptRunnerOptions _options;
    private readonly ChildProcess _process;
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

    private RunnerConnection(CSharpScriptRunnerOptions options, ChildProcess process)
    {
        _options = options;
        _process = process;
        _shapes = new ShapeProviders();
        foreach (CSharpScriptService service in options.Services)
            _shapes.Add(service.Shapes);
        _rpc = new HostRpc(RunnerRpc.CreateMessageHandler(process.StandardInput, process.StandardOutput, _shapes));
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
        string runner = CSharpHelper.Find(
            Path.GetFullPath(options.RunnerDirectory ?? CSharpHelper.DefaultDirectory("csharp-runner")),
            CSharpHelper.RunnerName,
            "Reference the Darp.CodeEditor.CSharp package from the app project, or set "
                + "CSharpScriptRunnerOptions.RunnerDirectory."
        );
        ProcessStartInfo start = CSharpHelper.CreateStartInfo(
            runner,
            dotNetPath: null,
            options.RunnerEnvironmentVariables
        );
        start.ArgumentList.Add(RunnerNames.ParentProcessIdArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        ChildProcess process = await ChildProcess.StartAsync(start, options.Output).ConfigureAwait(false);
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
            CompileResult result = await CompileAsync(
                    id,
                    code,
                    RunnerTypeName.Of(typeof(T)),
                    compiled.Task,
                    cancellationToken
                )
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
            // Not awaited: when the runner does not read its input, the release waits for nothing.
            if (!started)
                _ = ReleaseAsync(id);
        }
    }

    /// <summary>
    /// Hands the script to the runner and waits for it to compile, both within the compile timeout and the caller's
    /// cancellation. A runner that has not read the script or answered by then makes no progress: it is killed, like a
    /// call past its deadline, also when the caller stopped waiting earlier.
    /// </summary>
    private async Task<CompileResult> CompileAsync(
        long id,
        string code,
        RunnerTypeName handler,
        Task<CompileResult> compiled,
        CancellationToken cancellationToken
    )
    {
        using var compiling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        compiling.CancelAfter(_options.CompileTimeout);
        Task send = _rpc.NotifyAsync(
            nameof(IRunnerService.Compile),
            [id, code, handler],
            [typeof(long), typeof(string), typeof(RunnerTypeName)]
        );
        _ = EndUnlessSentAsync(send, _options.CompileTimeout);
        try
        {
            await send.WaitAsync(compiling.Token).ConfigureAwait(false);
            return await compiled.WaitAsync(compiling.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException) when (!_endedToken.IsCancellationRequested)
        {
            End(ScriptRunnerExitReason.Killed);
            throw new TimeoutException(
                $"The runner did not compile the script within {_options.CompileTimeout}, so it was stopped."
            );
        }
    }

    /// <summary>Kills the runner when it does not read a message within <paramref name="limit"/>.</summary>
    private async Task EndUnlessSentAsync(Task send, TimeSpan limit)
    {
        try
        {
            await send.WaitAsync(limit).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            End(ScriptRunnerExitReason.Killed);
        }
        catch (Exception)
        {
            // Sending failed because the connection ended, which reports itself.
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
        await _process.DisposeAsync().ConfigureAwait(false);
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
                _options.CallTimeout,
                _options.CancellationGracePeriod,
                () => End(ScriptRunnerExitReason.Killed)
            );
            _rpc.Deadlines = _deadlines;
        }
    }

    /// <summary>Tells the runner to forget a script. Best effort: it never fails, it only ends with the runner.</summary>
    private async Task ReleaseAsync(long id)
    {
        if (!IsAlive)
            return;
        try
        {
            await _rpc.NotifyAsync(nameof(IRunnerService.Release), [id], [typeof(long)]).ConfigureAwait(false);
        }
        catch (Exception)
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
        // The pipe closes when the process exits, unless a process the script started still holds it.
        await Task.WhenAny(_process.StandardErrorEnded, Task.Delay(s_standardErrorTimeout)).ConfigureAwait(false);
        int? exitCode = _process.Exited.IsCompletedSuccessfully ? _process.Exited.Result : null;
        _ended.TrySetResult(new ScriptRunnerExitedEventArgs(reason, exitCode, _process.StandardErrorTail));
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

    private sealed class HostRpc : JsonRpc
    {
        public HostRpc(IJsonRpcMessageHandler messageHandler)
            : base(messageHandler) =>
            CancellationStrategy = new GivingUp(this, CancellationStrategy ?? throw new InvalidOperationException());

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
    /// Tells <see cref="HostRpc.Deadlines"/> when StreamJsonRpc gives up on a call: at its deadline, or when its caller
    /// cancels it. StreamJsonRpc has decided how the call fails by then.
    /// </summary>
    private sealed class GivingUp(HostRpc rpc, ICancellationStrategy inner) : ICancellationStrategy
    {
        private readonly HostRpc _rpc = rpc;
        private readonly ICancellationStrategy _inner = inner;

        public void CancelOutboundRequest(RequestId requestId)
        {
            _rpc.Deadlines?.GaveUp(requestId);
            _inner.CancelOutboundRequest(requestId);
        }

        public void OutboundRequestEnded(RequestId requestId) => _inner.OutboundRequestEnded(requestId);

        public void IncomingRequestStarted(RequestId requestId, CancellationTokenSource cancellationTokenSource) =>
            _inner.IncomingRequestStarted(requestId, cancellationTokenSource);

        public void IncomingRequestEnded(RequestId requestId) => _inner.IncomingRequestEnded(requestId);
    }

    /// <summary>
    /// Kills the runner when a request goes unanswered for the grace period after its deadline. StreamJsonRpc already
    /// failed the call with <see cref="TimeoutException"/> and asked the runner to cancel it at the deadline.
    /// </summary>
    /// <remarks>
    /// The grace period starts only once StreamJsonRpc gave up on the call. When the app was starved of CPU across the
    /// deadline, StreamJsonRpc's timeout and the kill are due at once; a kill before the timeout ran would fail the call
    /// with <see cref="ConnectionLostException"/> instead.
    /// </remarks>
    private sealed class Deadlines : IDisposable
    {
        private readonly Dictionary<RequestId, Call> _calls = [];
        private readonly TimeSpan _deadline;
        private readonly TimeSpan _gracePeriod;
        private readonly Action _overdue;
        private readonly Timer _timer;

        public Deadlines(TimeSpan deadline, TimeSpan gracePeriod, Action overdue)
        {
            _deadline = deadline;
            _gracePeriod = gracePeriod;
            _overdue = overdue;
            TimeSpan limit = deadline + gracePeriod;
            TimeSpan period = TimeSpan.FromMilliseconds(Math.Clamp(limit.TotalMilliseconds / 8, 10, 250));
            _timer = new Timer(_ => Check(), null, period, period);
        }

        public void Sent(RequestId id)
        {
            lock (_calls)
                _calls[id] = new Call(Stopwatch.GetTimestamp());
        }

        public void GaveUp(RequestId id)
        {
            lock (_calls)
            {
                if (_calls.TryGetValue(id, out Call? call))
                    call.GaveUpAt ??= Stopwatch.GetTimestamp();
            }
        }

        public void Answered(RequestId id)
        {
            lock (_calls)
                _calls.Remove(id);
        }

        public void Dispose() => _timer.Dispose();

        private void Check()
        {
            lock (_calls)
            {
                long now = Stopwatch.GetTimestamp();
                // A call its caller cancelled early still has until its deadline.
                if (
                    !_calls.Values.Any(call =>
                        call.GaveUpAt is { } gaveUp
                        && Stopwatch.GetElapsedTime(gaveUp, now) > _gracePeriod
                        && Stopwatch.GetElapsedTime(call.SentAt, now) > _deadline + _gracePeriod
                    )
                )
                {
                    return;
                }
            }
            _overdue();
        }

        private sealed class Call(long sentAt)
        {
            public long SentAt { get; } = sentAt;

            /// <summary>When StreamJsonRpc gave up on the call.</summary>
            public long? GaveUpAt { get; set; }
        }
    }
}

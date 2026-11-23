using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Darp.CodeEditor.CSharpScript.Runner;
using PolyType;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

(Stream input, Stream output) = RunnerPipes.TakeConnection();

// Everything of the script API loads through its one manifest. The runner's own copies of the bridge are found first.
var resolver = new AssemblyDependencyResolver(args[0]);
AssemblyLoadContext.Default.Resolving += (context, name) =>
    resolver.ResolveAssemblyToPath(name) is { } path ? context.LoadFromAssemblyPath(path) : null;
AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
    resolver.ResolveUnmanagedDllToPath(name) is { } path ? NativeLibrary.Load(path) : 0;

Type session = Type.GetType(args[1], throwOnError: true)!;
Type app = Type.GetType(args[2], throwOnError: true)!;
Type side = Type.GetType(args[3], throwOnError: true)!;
await (Task)
    typeof(Runner<,,>)
        .MakeGenericType(app, side, session)
        .GetMethod("ListenAsync")!
        .Invoke(null, BindingFlags.DoNotWrapExceptions, null, [output, input], null)!;

// The app has gone or the session has ended. This also ends a runner whose scripts left foreground threads behind.
Environment.Exit(0);

/// <summary>One session in this process: the session object, its runs and the end of the session.</summary>
internal sealed class Runner<TApp, TSide, TSession> : IRunnerControl
    where TApp : class, IShapeable<TApp>
    where TSide : class, IShapeable<TSide>
    where TSession : class, TSide, IScriptSessionObject<TSession, TApp>
{
    private readonly RunnerRpc _rpc;
    private readonly ConcurrentDictionary<int, ActiveRun> _runs = new();
    private TSession? _session;

    private Runner(RunnerRpc rpc) => _rpc = rpc;

    /// <summary>Serves the connection until it ends.</summary>
    public static async Task ListenAsync(Stream output, Stream input)
    {
        using var rpc = new RunnerRpc(SessionWire.Handler<TApp, TSide>(output, input));
        rpc.AllowModificationWhileListening = true;
        rpc.AddLocalRpcTarget(
            RpcTargetMetadata.FromShape<IRunnerControl>(),
            new Runner<TApp, TSide, TSession>(rpc),
            new JsonRpcTargetOptions
            {
                MethodNameTransform = CommonMethodNameTransforms.Prepend(SessionWire.ControlPrefix),
            }
        );
        rpc.StartListening();
        await rpc.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public Task<RunFailure?> InitializeAsync()
    {
        try
        {
            TSession session = TSession.Create(SessionWire.Proxy<TApp>(_rpc));
            _session = session;
            _rpc.SessionType = session.GetType();
            _rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<TSide>(), session, null);
            return Task.FromResult<RunFailure?>(null);
        }
        catch (Exception e)
        {
            return Task.FromResult<RunFailure?>(_rpc.Failure(e));
        }
    }

    public Task BeginAsync(RunRequest request)
    {
        _runs[request.Id] = new ActiveRun(run => ExecuteAsync(request, run));
        return Task.CompletedTask;
    }

    public async Task<RunOutcome> JoinAsync(int id)
    {
        RunOutcome outcome = await _runs[id].End.ConfigureAwait(false);
        _runs.TryRemove(id, out _);
        return outcome;
    }

    public Task StopAsync(int id)
    {
        if (_runs.TryGetValue(id, out ActiveRun? run))
            _ = run.CancelAsync();
        return Task.CompletedTask;
    }

    public async Task<RunFailure?> CloseAsync()
    {
        Task calls = _rpc.RefuseCalls();
        foreach (ActiveRun run in _runs.Values)
            _ = run.CancelAsync();
        await Task.WhenAll(_runs.Values.Select(r => r.End)).ConfigureAwait(false);
        await calls.ConfigureAwait(false);
        try
        {
            await DisposeObjectAsync(_session).ConfigureAwait(false);
            return null;
        }
        catch (Exception e)
        {
            return _rpc.Failure(e);
        }
    }

    /// <summary>The run: its globals, its code, then its stop token and the end of its globals.</summary>
    private async Task<RunOutcome> ExecuteAsync(RunRequest request, ActiveRun run)
    {
        var failures = new List<RunFailure>();
        bool stopped = false;
        object? globals = null;
        try
        {
            globals = CreateGlobals(request, run.Stop.Token);
            // Script assemblies stay loaded for the session, so that callbacks a run left behind keep working.
            Assembly script = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(request.Image));
            _rpc.Scripts[script] = request.Id;
            MethodInfo entry = script
                .GetType(request.EntryType, throwOnError: true)!
                .GetMethod(request.EntryMethod, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            object?[] submission = [globals, null];
            await (
                (Task)entry.Invoke(null, BindingFlags.DoNotWrapExceptions, null, [submission], null)!
            ).ConfigureAwait(false);
        }
        // Only the script's code can end a run as stopped: a factory that throws has failed, whatever it threw.
        catch (OperationCanceledException) when (globals is not null && run.Stop.IsCancellationRequested)
        {
            stopped = true;
        }
        catch (Exception e)
        {
            failures.Add(_rpc.Failure(e));
        }
        // A callback on the stop token that throws, and a disposal that throws, are failures of the run as well.
        await AttemptAsync(run.CancelAsync).ConfigureAwait(false);
        await AttemptAsync(() => DisposeObjectAsync(globals)).ConfigureAwait(false);
        return new RunOutcome(stopped, [.. failures]);

        async Task AttemptAsync(Func<Task> step)
        {
            try
            {
                await step().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                failures.Add(_rpc.Failure(e));
            }
        }
    }

    private object CreateGlobals(RunRequest request, CancellationToken stop)
    {
        Type globals = Type.GetType(request.Globals, throwOnError: true)!;
        Type inputType = Type.GetType(request.InputType, throwOnError: true)!;
        object? input =
            inputType == typeof(ValueTuple)
                ? default(ValueTuple)
                : typeof(Runner<TApp, TSide, TSession>)
                    .GetMethod(nameof(ReadInput), BindingFlags.Static | BindingFlags.NonPublic)!
                    .MakeGenericMethod(inputType)
                    .Invoke(null, BindingFlags.DoNotWrapExceptions, null, [request.Input], null);
        return typeof(IScriptGlobalsFactory<,>)
            .MakeGenericType(globals, inputType)
            .GetMethod("CreateGlobals")!
            .Invoke(_session, BindingFlags.DoNotWrapExceptions, null, [input, stop], null)!;
    }

    private static T? ReadInput<T>(byte[] bytes)
        where T : IShapeable<T> => SessionWire.Serializer.Deserialize<T>(bytes);

    private static async Task DisposeObjectAsync(object? target)
    {
        if (target is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (target is IDisposable disposable)
            disposable.Dispose();
    }

    /// <summary>A run that has started: its stop token and its end.</summary>
    private sealed class ActiveRun
    {
        private readonly Lock _lock = new();
        private Task? _cancelling;

        public ActiveRun(Func<ActiveRun, Task<RunOutcome>> body) => End = Task.Run(() => body(this));

        public CancellationTokenSource Stop { get; } = new();

        public Task<RunOutcome> End { get; }

        /// <summary>Cancels the stop token once; every caller waits for the same callbacks, and sees their failure.</summary>
        public Task CancelAsync()
        {
            lock (_lock)
                return _cancelling ??= Stop.CancelAsync();
        }
    }
}

/// <summary>
/// The runner's end of the connection: it knows calls into the session object from their target, so that a closing
/// session refuses new ones and waits until the replies of those it admitted are written.
/// </summary>
internal sealed class RunnerRpc(IJsonRpcMessageHandler handler) : JsonRpc(handler)
{
    private readonly Lock _lock = new();
    private readonly HashSet<RequestId> _unanswered = [];
    private int _admitted;
    private TaskCompletionSource? _closing;

    /// <summary>The session object's type, once it exists.</summary>
    public Type? SessionType { get; set; }

    /// <summary>The loaded scripts, with the run whose request brought them.</summary>
    public ConcurrentDictionary<Assembly, int> Scripts { get; } = new();

    /// <summary>Refuses calls into the session object. Completes when those admitted have been answered.</summary>
    public Task RefuseCalls()
    {
        lock (_lock)
        {
            _closing ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_admitted == 0)
                _closing.TrySetResult();
            return _closing.Task;
        }
    }

    protected override async ValueTask<JsonRpcMessage> DispatchRequestAsync(
        JsonRpcRequest request,
        TargetMethod targetMethod,
        CancellationToken cancellationToken
    )
    {
        if (targetMethod.TargetObjectType != SessionType)
            return await base.DispatchRequestAsync(request, targetMethod, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            if (_closing is not null)
            {
                return new JsonRpcError
                {
                    RequestId = request.RequestId,
                    Error = new JsonRpcError.ErrorDetail
                    {
                        Code = SessionWire.SessionClosing,
                        Message = "The session is closing.",
                    },
                };
            }
            _admitted++;
            if (!request.IsNotification)
                _unanswered.Add(request.RequestId);
        }
        try
        {
            return await base.DispatchRequestAsync(request, targetMethod, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // A notification gets no reply to wait for.
            if (request.IsNotification)
            {
                lock (_lock)
                    CallAnswered();
            }
        }
    }

    protected override void OnResponseSent(JsonRpcMessage response)
    {
        base.OnResponseSent(response);
        lock (_lock)
        {
            if (response is IJsonRpcMessageWithId reply && _unanswered.Remove(reply.RequestId))
                CallAnswered();
        }
    }

    private void CallAnswered()
    {
        if (--_admitted == 0)
            _closing?.TrySetResult();
    }

    /// <summary>A failure as it crosses to the app, with the innermost frame in a script's own code.</summary>
    public RunFailure Failure(Exception exception)
    {
        StackFrame? frame = new StackTrace(exception, fNeedFileInfo: true)
            .GetFrames()
            .FirstOrDefault(f => f.GetFileLineNumber() > 0 && IsScript(f));
        return new RunFailure(
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            exception.StackTrace,
            frame is null ? 0 : Scripts[frame.GetMethod()!.Module.Assembly],
            frame?.GetFileLineNumber() ?? 0,
            Math.Max(1, frame?.GetFileColumnNumber() ?? 0)
        );

        bool IsScript(StackFrame candidate) =>
            candidate.GetMethod()?.Module.Assembly is { } assembly && Scripts.ContainsKey(assembly);
    }

    protected override JsonRpcError.ErrorDetail CreateErrorDetails(JsonRpcRequest request, Exception exception)
    {
        JsonRpcError.ErrorDetail detail = base.CreateErrorDetails(request, exception);
        detail.Data = Failure(exception);
        return detail;
    }
}

using PolyType;
using StreamJsonRpc;

namespace Darp.CodeEditor.CSharpScript.Runner;

/// <summary>Starts sessions, runner processes in which compiled scripts run, and starts scripts in them.</summary>
public static class CSharpScriptSession
{
    /// <summary>
    /// Starts a runner process and creates the session object in it. Returns once the bridge is ready: the app can
    /// call <see cref="CSharpScriptSession{TSessionSide, TSessionObject}.Remote"/> before any script runs.
    /// </summary>
    /// <typeparam name="TAppSide">
    /// The half of the bridge that the app implements and scripts call: an interface of the script API, marked
    /// <c>[JsonRpcContract]</c> and <c>[GenerateShape]</c>.
    /// </typeparam>
    /// <typeparam name="TSessionSide">
    /// The half of the bridge that the session object implements and the app calls: an interface of the script API,
    /// marked <c>[JsonRpcContract]</c> and <c>[GenerateShape]</c>.
    /// </typeparam>
    /// <typeparam name="TSessionObject">
    /// The session object's type, from an assembly marked <c>CSharpScriptReference</c>. The runner creates one
    /// instance through its <see cref="IScriptSessionObject{TSelf, TAppSide}.Create"/>. Scripts whose globals it
    /// creates, through <see cref="IScriptGlobalsFactory{TGlobals, TInput}"/>, run in the session. Naming the type
    /// here compiles none of its code into a NativeAOT app.
    /// </typeparam>
    /// <param name="appSide">The app's half of the bridge. Calls arrive on any thread, until the session ends.</param>
    /// <param name="cancellationToken">Stops starting. It has no effect once the session is started.</param>
    /// <exception cref="InvalidOperationException">
    /// The bridge's interfaces have no build-time proxies, because the script API lacks
    /// <c>[assembly: ExportRpcContractProxies]</c>; checked in every build, so the mistake shows before a NativeAOT
    /// app fails. Or the process did not start or answer in time; the message includes its standard error.
    /// </exception>
    /// <exception cref="CSharpScriptException">The session object's <c>Create</c> threw. The process is ended.</exception>
    public static Task<CSharpScriptSession<TSessionSide, TSessionObject>> StartAsync<
        TAppSide,
        TSessionSide,
        TSessionObject
    >(TAppSide appSide, CancellationToken cancellationToken = default)
        where TAppSide : class, IShapeable<TAppSide>
        where TSessionSide : class, IShapeable<TSessionSide>
        where TSessionObject : class, TSessionSide, IScriptSessionObject<TSessionObject, TAppSide> =>
        throw new NotImplementedException();

    /// <inheritdoc cref="StartAsync{TAppSide, TSessionSide, TSessionObject}(TAppSide, CancellationToken)"/>
    /// <param name="appSide">The app's half of the bridge. Calls arrive on any thread, until the session ends.</param>
    /// <param name="options">How the process runs and how long disposing may take.</param>
    /// <param name="cancellationToken">Stops starting. It has no effect once the session is started.</param>
    public static Task<CSharpScriptSession<TSessionSide, TSessionObject>> StartAsync<
        TAppSide,
        TSessionSide,
        TSessionObject
    >(TAppSide appSide, CSharpScriptSessionOptions options, CancellationToken cancellationToken = default)
        where TAppSide : class, IShapeable<TAppSide>
        where TSessionSide : class, IShapeable<TSessionSide>
        where TSessionObject : class, TSessionSide, IScriptSessionObject<TSessionObject, TAppSide> =>
        throw new NotImplementedException();

    /// <summary>Starts <paramref name="script"/> in <paramref name="session"/>, for scripts whose runs take no input.</summary>
    /// <inheritdoc cref="StartScriptAsync{TSessionSide, TSessionObject, TGlobals, TInput}(CSharpScriptSession{TSessionSide, TSessionObject}, CSharpScriptAssembly{TGlobals}, TInput, CancellationToken)"/>
    public static Task<CSharpScriptRun> StartScriptAsync<TSessionSide, TSessionObject, TGlobals>(
        this CSharpScriptSession<TSessionSide, TSessionObject> session,
        CSharpScriptAssembly<TGlobals> script,
        CancellationToken cancellationToken = default
    )
        where TSessionSide : class, IShapeable<TSessionSide>
        where TSessionObject : class, TSessionSide, IScriptGlobalsFactory<TGlobals, ValueTuple>
        where TGlobals : class => throw new NotImplementedException();

    /// <summary>
    /// Starts <paramref name="script"/> in <paramref name="session"/> with <paramref name="input"/>. Returns once the
    /// runner has accepted the run, before the globals are created: creating them is part of the run, so a factory
    /// that hangs can be stopped like the script, and one that throws ends the run's
    /// <see cref="CSharpScriptRun.Completion"/>.
    /// </summary>
    /// <remarks>
    /// An extension method, so that the compiler can check that the session object creates these globals from this
    /// input: <typeparamref name="TSessionObject"/> must implement
    /// <see cref="IScriptGlobalsFactory{TGlobals, TInput}"/>. The compiler infers every type argument.
    /// </remarks>
    /// <typeparam name="TSessionSide">The half of the bridge that the session object implements.</typeparam>
    /// <typeparam name="TSessionObject">The session object's type, which creates the globals.</typeparam>
    /// <typeparam name="TGlobals">The globals type of the script's kind.</typeparam>
    /// <typeparam name="TInput">
    /// The run's input, marked <c>[GenerateShape]</c>: data that this run alone gets, such as its identity. It is
    /// copied into the runner.
    /// </typeparam>
    /// <param name="session">The session to run the script in.</param>
    /// <param name="script">The script, compiled by <see cref="CSharpScriptServer"/>.</param>
    /// <param name="input">The run's input, which the session object's factory receives.</param>
    /// <param name="cancellationToken">
    /// Stops starting. Once the runner has accepted the run, the method returns it even if the token is cancelled, so
    /// that no run is left without a handle. Afterwards, use <see cref="CSharpScriptRun.StopAsync"/>.
    /// </param>
    /// <exception cref="ObjectDisposedException">The session is being disposed.</exception>
    /// <exception cref="ConnectionLostException">The session's process has ended.</exception>
    public static Task<CSharpScriptRun> StartScriptAsync<TSessionSide, TSessionObject, TGlobals, TInput>(
        this CSharpScriptSession<TSessionSide, TSessionObject> session,
        CSharpScriptAssembly<TGlobals> script,
        TInput input,
        CancellationToken cancellationToken = default
    )
        where TSessionSide : class, IShapeable<TSessionSide>
        where TSessionObject : class, TSessionSide, IScriptGlobalsFactory<TGlobals, TInput>
        where TGlobals : class
        where TInput : IShapeable<TInput> => throw new NotImplementedException();
}

/// <summary>
/// A session: one runner process, a bridge between the app and its scripts, and one session object that the
/// session's scripts share.
/// </summary>
/// <remarks>
/// <para>
/// The script API defines the bridge as two interfaces, one per direction. The app implements the app side; the
/// session object implements the session side, in the runner, and the app calls it through <see cref="Remote"/>.
/// Setup, such as connecting a device, is the script API's own methods on the session side, which the app calls
/// before it starts scripts. Only the bridge crosses the process boundary: its methods are async and copy their data.
/// The session object, the globals and everything they use are ordinary .NET objects in the runner.
/// </para>
/// <para>
/// Scripts start with <c>StartScriptAsync</c> and may overlap; the session object says which kinds of scripts run in
/// it. They share the session object; calls from the app and
/// from scripts reach it on any thread, and the script API makes it safe for that. Sessions share no objects:
/// scripts that must not take each other down belong in separate sessions.
/// </para>
/// <para>
/// A session ends with <see cref="DisposeAsync"/>, with <see cref="KillAsync"/>, or when its process crashes;
/// <see cref="Exited"/> says how. Disposing goes in this order:
/// </para>
/// <list type="number">
/// <item>New runs throw <see cref="ObjectDisposedException"/>; new calls on <see cref="Remote"/> fail with
/// <see cref="ConnectionLostException"/>.</item>
/// <item>Every run is stopped, and its end awaited: its code, then its globals.</item>
/// <item>Calls on <see cref="Remote"/> that are already running in the session object finish.</item>
/// <item>The session object is disposed. The app side still answers meanwhile.</item>
/// <item>The process ends.</item>
/// <item>Calls on the app side that are still running see their token cancelled, and are awaited.</item>
/// </list>
/// <para>
/// Steps 2 to 5 have <see cref="CSharpScriptSessionOptions.DisposeTimeout"/>; past it, the process is killed. A kill
/// skips to step 5. After a kill or a crash the session cannot be used again: running scripts' runs end with
/// <see cref="ConnectionLostException"/>, and so do calls on <see cref="Remote"/>. The process holds no compiler:
/// <see cref="CSharpScriptServer"/> compiles. It is a child of the app: it ends when the app ends, even when the app is
/// killed.
/// </para>
/// <para>
/// A session keeps every script it ran loaded until it ends, so that callbacks a run left in the session object keep
/// working, and failures in them still point into their script.
/// </para>
/// <para>Safe to use from any thread.</para>
/// </remarks>
/// <typeparam name="TSessionSide">The half of the bridge that the session object implements.</typeparam>
/// <typeparam name="TSessionObject">The session object's type, which creates the globals of the session's runs.</typeparam>
public sealed class CSharpScriptSession<TSessionSide, TSessionObject> : IAsyncDisposable
    where TSessionSide : class, IShapeable<TSessionSide>
    where TSessionObject : class, TSessionSide
{
    private CSharpScriptSession() { }

    /// <summary>
    /// The session side of the bridge: a proxy of the session object. Calls cross into the runner; failures in the
    /// runner arrive as <see cref="CSharpScriptException"/>.
    /// </summary>
    public TSessionSide Remote => throw new NotImplementedException();

    /// <summary>
    /// Completes once the process has ended, for any reason, before the session's runs and calls fail because of it:
    /// the session holds those failures back until it knows the exit, and kills a process whose connection broke but
    /// which does not exit. Completes once, with one reason; see <see cref="CSharpScriptSessionExitReason"/>. Never
    /// faults. After a <see cref="ConnectionLostException"/>, await it to learn why the session ended.
    /// </summary>
    public Task<CSharpScriptSessionExit> Exited => throw new NotImplementedException();

    /// <summary>
    /// Ends the process at once, and with it every script of the session; managed cleanup in the runner does not run.
    /// Then cancels the app side's calls that are still running and waits for them. Completes when the process has
    /// exited and those calls have returned; app code that ignores its token keeps it waiting. Safe to call again,
    /// and cuts a <see cref="DisposeAsync"/> in progress short.
    /// </summary>
    public Task KillAsync() => throw new NotImplementedException();

    /// <summary>
    /// Ends the session in the order described in the remarks, killing the process when the runner takes longer than
    /// <see cref="CSharpScriptSessionOptions.DisposeTimeout"/>. Afterwards the app may drop its app side. Never throws
    /// for failures in the runner, so that <c>await using</c> keeps the app's own exception: runs report theirs in
    /// <see cref="CSharpScriptRun.Completion"/>, and the session object's is in
    /// <see cref="CSharpScriptSessionExit.DisposeException"/>. Safe after a kill or a crash, and to call again.
    /// </summary>
    public ValueTask DisposeAsync() => throw new NotImplementedException();
}

/// <summary>One run of a compiled script in a session.</summary>
/// <remarks>
/// <para>
/// A run lasts as long as its code: creating its globals, the script, and disposing the globals. When the script's
/// code has finished, for any reason, the session cancels the run's stop token, disposes its globals if they are
/// disposable, while the bridge is still up, and then completes <see cref="Completion"/>.
/// </para>
/// <para>
/// The globals end what the run owns when they are disposed, such as sending its queued values. Script code that the
/// script handed to something longer-lived, such as a callback set on the session object, can still be called after
/// the run has ended: the library does not track it. Such a callback may only use what outlives its run: the session
/// object and what it owns, not the run's globals, its input, or what they own. A kill runs no cleanup and delivers
/// nothing still queued.
/// </para>
/// </remarks>
public sealed class CSharpScriptRun
{
    private CSharpScriptRun() { }

    /// <summary>
    /// Completes when the run has ended: its code has finished and its globals are disposed. It completes normally;
    /// cancelled, when the script ended with an <see cref="OperationCanceledException"/>, from any token, after its stop
    /// token was cancelled; with <see cref="CSharpScriptException"/> when creating the globals or the script threw, an
    /// <see cref="OperationCanceledException"/> before the stop token was cancelled included, or when disposing the
    /// globals threw; or with <see cref="ConnectionLostException"/> when the session's process ended first. When both
    /// the script and the disposal throw, it holds both, the script's first: <c>await</c> throws that one, and
    /// <c>Completion.Exception</c> has both. To only stop waiting, use <c>Completion.WaitAsync</c>.
    /// </summary>
    public Task Completion => throw new NotImplementedException();

    /// <summary>
    /// Cancels the run's stop token and waits until the run has ended. Does not throw for how the run ended: observe
    /// <see cref="Completion"/> for that. No effect on a run that has ended.
    /// </summary>
    /// <param name="killSessionAfter">
    /// How long the script may take to stop before the whole session is killed, and with it every other run in it;
    /// <see cref="CSharpScriptSession{TSessionSide, TSessionObject}.Exited"/> then says
    /// <see cref="CSharpScriptSessionExitReason.StopTimedOut"/>. Null, the default: wait as long as the script takes; a
    /// script that ignores its stop token then keeps this waiting until the session ends. When several stops pass a
    /// time, the earliest deadline wins. Measured with <see cref="CSharpScriptSessionOptions.TimeProvider"/>.
    /// </param>
    public Task StopAsync(TimeSpan? killSessionAfter = null) => throw new NotImplementedException();

    /// <summary>
    /// Waits until the run has ended, and stops it when <paramref name="stopToken"/> is cancelled first, as
    /// <see cref="StopAsync"/> does. Throws what <see cref="Completion"/> throws, also after stopping:
    /// <see cref="OperationCanceledException"/> when the script stopped, <see cref="ConnectionLostException"/> when the
    /// session was killed, <see cref="CSharpScriptException"/> when the script failed while stopping, and nothing when
    /// it caught its cancellation and returned.
    /// </summary>
    /// <param name="stopToken">Stops the run. To only stop waiting, use <c>Completion.WaitAsync</c> instead.</param>
    /// <param name="killSessionAfter">
    /// After the token was cancelled, how long the script may take to stop before the whole session is killed. Null,
    /// the default: wait as long as the script takes.
    /// </param>
    public Task RunToEndAsync(CancellationToken stopToken, TimeSpan? killSessionAfter = null) =>
        throw new NotImplementedException();
}

/// <summary>Settings of a <see cref="CSharpScriptSession{TSessionSide, TSessionObject}"/>.</summary>
public sealed record CSharpScriptSessionOptions
{
    /// <summary>
    /// How long the runner's part of <see cref="CSharpScriptSession{TSessionSide, TSessionObject}.DisposeAsync"/> may take
    /// before the process is killed: ending the runs, finishing calls in the session object, disposing it. Default:
    /// 5 s.
    /// </summary>
    public TimeSpan DisposeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Environment variables to set or, with a null value, to remove for the process. Scripts run culture-invariant
    /// unless <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT</c> is <c>0</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Receives each line that the process writes to standard output or standard error, on any thread: what the
    /// session's scripts write to the console, and the runtime's report when the process crashes. Lines carry no sign
    /// of the run that wrote them; use the bridge, with the run's input, for output that belongs to one run.
    /// </summary>
    public Action<string>? Output { get; init; }

    /// <summary>
    /// The clock for <see cref="DisposeTimeout"/> and for the <c>killSessionAfter</c> of stopping runs. Default:
    /// <see cref="TimeProvider.System"/>; tests pass their own.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>How a session's process ended.</summary>
public sealed class CSharpScriptSessionExit
{
    private CSharpScriptSessionExit() { }

    /// <summary>Why the process ended.</summary>
    public CSharpScriptSessionExitReason Reason => throw new NotImplementedException();

    /// <summary>The process's exit code.</summary>
    public int ExitCode => throw new NotImplementedException();

    /// <summary>The last lines of the process's standard error, for crash reports.</summary>
    public string StandardError => throw new NotImplementedException();

    /// <summary>
    /// What disposing the session object threw, during <see cref="CSharpScriptSession{TSessionSide, TSessionObject}.DisposeAsync"/>,
    /// which does not throw it; null otherwise.
    /// </summary>
    public CSharpScriptException? DisposeException => throw new NotImplementedException();
}

/// <summary>Why a session's process ended.</summary>
/// <remarks>
/// When several causes meet, such as a kill during disposal, the first cause the session acted on is the reason. A
/// process that ends by itself before any of them is <see cref="Crashed"/>, also while it is being disposed.
/// </remarks>
public enum CSharpScriptSessionExitReason
{
    /// <summary>The app disposed the session, and the runner ended in time.</summary>
    Disposed,

    /// <summary>
    /// The app called <see cref="CSharpScriptSession{TSessionSide, TSessionObject}.KillAsync"/>, also during
    /// disposal.
    /// </summary>
    Killed,

    /// <summary>
    /// A run did not stop within the <c>killSessionAfter</c> that the app passed to
    /// <see cref="CSharpScriptRun.StopAsync"/> or <see cref="CSharpScriptRun.RunToEndAsync"/>, so the process was
    /// killed.
    /// </summary>
    StopTimedOut,

    /// <summary>
    /// It ended by itself, for example from a stack overflow in a script or an unhandled exception in code a script left
    /// running, before the app ended it.
    /// </summary>
    Crashed,

    /// <summary>
    /// Disposing took longer than <see cref="CSharpScriptSessionOptions.DisposeTimeout"/>, so the process was killed.
    /// </summary>
    DisposeTimedOut,
}

/// <summary>
/// Code in the runner threw: a script, its globals, or the session object during a call from the app.
/// </summary>
/// <remarks>
/// The session's connection turns such failures into this exception through StreamJsonRpc's error hook. It derives
/// from <see cref="RemoteInvocationException"/>, which StreamJsonRpc's proxies throw for failed calls, so code that
/// catches that keeps working.
/// </remarks>
public sealed class CSharpScriptException : RemoteInvocationException
{
    private CSharpScriptException()
        : base(null, 0, (object?)null) { }

    /// <summary>The full name of the exception type that was thrown, such as <c>System.FormatException</c>.</summary>
    public string ScriptExceptionType => throw new NotImplementedException();

    /// <summary>The stack trace in the runner, with script line numbers.</summary>
    public string? ScriptStackTrace => throw new NotImplementedException();

    /// <summary>
    /// Where in the script it was thrown: the innermost frame in a script's own code, in <see cref="ScriptSnapshot"/>.
    /// Null when no frame of a script is on the stack, such as for a failure in the session object itself.
    /// </summary>
    public TextPosition? ScriptPosition => throw new NotImplementedException();

    /// <summary>
    /// The compiled text that <see cref="ScriptPosition"/> points into: the compilation's
    /// <see cref="CSharpScriptCompilation{TGlobals}.Snapshot"/>, also for a callback that an earlier
    /// run left in the session. To show the place in the current text, pass both to
    /// <see cref="CodeDocument.MapToCurrent"/>. Set exactly when <see cref="ScriptPosition"/> is.
    /// </summary>
    public TextSnapshot? ScriptSnapshot => throw new NotImplementedException();
}

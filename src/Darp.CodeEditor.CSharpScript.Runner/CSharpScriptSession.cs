using Microsoft.Extensions.Logging;
using PolyType;
using StreamJsonRpc;

namespace Darp.CodeEditor.CSharpScript.Runner;

/// <summary>Starts sessions: runner processes in which compiled scripts run.</summary>
public static class CSharpScriptSession
{
    /// <summary>
    /// Starts a runner process and creates the session object in it. Returns once the bridge is ready: the app can
    /// call <see cref="CSharpScriptSession{TSessionSide, TSession}.Remote"/> before any script runs.
    /// </summary>
    /// <typeparam name="TAppSide">
    /// The half of the bridge that the app implements and scripts call: an interface of the script API, marked
    /// <c>[JsonRpcContract]</c> and <c>[GenerateShape]</c>.
    /// </typeparam>
    /// <typeparam name="TSessionSide">
    /// The half of the bridge that the session object implements and the app calls: an interface of the script API,
    /// marked <c>[JsonRpcContract]</c> and <c>[GenerateShape]</c>.
    /// </typeparam>
    /// <typeparam name="TSession">
    /// The session object's type, from an assembly marked <c>CSharpScriptReference</c>. The runner creates one
    /// instance, through its public constructor, which may ask for <typeparamref name="TAppSide"/>. The session's
    /// scripts must be compiled for it. Naming the type here compiles none of its code into a NativeAOT app.
    /// </typeparam>
    /// <param name="appSide">The app's half of the bridge. Calls arrive on any thread, until the session ends.</param>
    /// <param name="cancellationToken">Stops starting. It has no effect once the session is started.</param>
    /// <exception cref="ArgumentException"><typeparamref name="TSession"/> has no fitting public constructor.</exception>
    /// <exception cref="ScriptException">
    /// The session object's constructor threw. The process is ended.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The process did not start or did not answer in time. The message includes its standard error.
    /// </exception>
    public static Task<CSharpScriptSession<TSessionSide, TSession>> StartAsync<TAppSide, TSessionSide, TSession>(
        TAppSide appSide,
        CancellationToken cancellationToken = default
    )
        where TAppSide : class, IShapeable<TAppSide>
        where TSessionSide : class, IShapeable<TSessionSide>
        where TSession : class, TSessionSide => throw new NotImplementedException();

    /// <inheritdoc cref="StartAsync{TAppSide, TSessionSide, TSession}(TAppSide, CancellationToken)"/>
    /// <param name="appSide">The app's half of the bridge. Calls arrive on any thread, until the session ends.</param>
    /// <param name="options">How the process runs and how long disposing may take.</param>
    /// <param name="cancellationToken">Stops starting. It has no effect once the session is started.</param>
    public static Task<CSharpScriptSession<TSessionSide, TSession>> StartAsync<TAppSide, TSessionSide, TSession>(
        TAppSide appSide,
        CSharpScriptSessionOptions options,
        CancellationToken cancellationToken = default
    )
        where TAppSide : class, IShapeable<TAppSide>
        where TSessionSide : class, IShapeable<TSessionSide>
        where TSession : class, TSessionSide => throw new NotImplementedException();
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
/// Scripts start with <see cref="StartScriptAsync"/> and may overlap. They share the session object; calls from the
/// app and from scripts reach it on any thread, and the script API makes it safe for that. Sessions share no objects:
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
/// <see cref="CSharpScriptServer"/> compiles.
/// </para>
/// <para>Safe to use from any thread.</para>
/// </remarks>
/// <typeparam name="TSessionSide">The half of the bridge that the session object implements.</typeparam>
/// <typeparam name="TSession">The session object's type, which the session's scripts are compiled for.</typeparam>
public sealed class CSharpScriptSession<TSessionSide, TSession> : IAsyncDisposable
    where TSessionSide : class, IShapeable<TSessionSide>
    where TSession : class, TSessionSide
{
    private CSharpScriptSession() { }

    /// <summary>
    /// The session side of the bridge: a proxy of the session object. Calls cross into the runner; failures in the
    /// runner arrive as <see cref="ScriptException"/>.
    /// </summary>
    public TSessionSide Remote => throw new NotImplementedException();

    /// <summary>
    /// Completes once the process has ended, for any reason, before the session's runs fail because of it. Never
    /// faults. After a <see cref="ConnectionLostException"/>, await it to learn why the session ended.
    /// </summary>
    public Task<ScriptSessionExit> Exited => throw new NotImplementedException();

    /// <summary>
    /// Loads <paramref name="script"/>, creates its globals and starts it, for scripts whose runs take no input.
    /// </summary>
    /// <inheritdoc cref="StartScriptAsync{TInput}(CompiledScript{TSession, TInput}, TInput, CancellationToken)"/>
    public Task<ScriptRun> StartScriptAsync(
        CompiledScript<TSession, ValueTuple> script,
        CancellationToken cancellationToken = default
    ) => throw new NotImplementedException();

    /// <summary>
    /// Loads <paramref name="script"/>, creates its globals with <paramref name="input"/> and starts it. Returns once
    /// the script runs; the returned run says when it has ended.
    /// </summary>
    /// <typeparam name="TInput">
    /// The run's input, marked <c>[GenerateShape]</c>: data that this run alone gets, such as its identity. It is
    /// copied into the runner.
    /// </typeparam>
    /// <param name="script">
    /// The script, compiled by <see cref="CSharpScriptServer"/> for this kind of session and this kind of input, so
    /// that its globals fit; the compiler checks it.
    /// </param>
    /// <param name="input">The run's input, which the globals receive.</param>
    /// <param name="cancellationToken">
    /// Stops starting. Once the runner has accepted the script, the method returns the run even if the token is
    /// cancelled, so that no run is left without a handle. Afterwards, use <see cref="ScriptRun.StopAsync"/>.
    /// </param>
    /// <exception cref="ScriptException">Creating the globals threw.</exception>
    /// <exception cref="ObjectDisposedException">The session is being disposed.</exception>
    /// <exception cref="ConnectionLostException">The session's process has ended.</exception>
    public Task<ScriptRun> StartScriptAsync<TInput>(
        CompiledScript<TSession, TInput> script,
        TInput input,
        CancellationToken cancellationToken = default
    )
        where TInput : IShapeable<TInput> => throw new NotImplementedException();

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
    /// for failures in the runner, which go to <see cref="CSharpScriptSessionOptions.Logger"/>, so that
    /// <c>await using</c> keeps the app's own exception. Safe after a kill or a crash, and to call again.
    /// </summary>
    public ValueTask DisposeAsync() => throw new NotImplementedException();
}

/// <summary>One run of a compiled script in a session.</summary>
/// <remarks>
/// A run lasts as long as its script's code. When the code has finished, for any reason, the session cancels the
/// run's stop token, disposes its globals if they are disposable, while the bridge is still up, and then completes
/// <see cref="Completion"/>. So when <see cref="Completion"/> completes, nothing of the run is left: what its globals
/// registered is gone, and what they sent has reached the app. What must outlive a run belongs to the session object.
/// </remarks>
public sealed class ScriptRun
{
    private ScriptRun() { }

    /// <summary>
    /// Completes when the run has ended: its code has finished and its globals are disposed. It completes normally;
    /// cancelled, when the script ended with <see cref="OperationCanceledException"/> through its stop token; with
    /// <see cref="ScriptException"/> when the script threw, also <see cref="OperationCanceledException"/> from any
    /// other token, or when disposing the globals failed after the script succeeded; or with
    /// <see cref="ConnectionLostException"/> when the session's process ended first. When both the script and the
    /// disposal fail, the script's exception wins, and the disposal's goes to the session's logger.
    /// </summary>
    public Task Completion => throw new NotImplementedException();

    /// <summary>
    /// Cancels the run's stop token and waits until the run has ended. Does not throw for how the run ended; see
    /// <see cref="Completion"/>. No effect on a run that has ended.
    /// </summary>
    /// <param name="killSessionAfter">
    /// How long the script may take to stop before the whole session is killed, and with it every other run in it.
    /// Null, the default: wait as long as the script takes; a script that ignores its stop token then keeps this
    /// waiting until the session ends.
    /// </param>
    public Task StopAsync(TimeSpan? killSessionAfter = null) => throw new NotImplementedException();

    /// <summary>
    /// Waits until the run has ended, and throws what <see cref="Completion"/> throws. When
    /// <paramref name="cancellationToken"/> is cancelled first, stops the run as <see cref="StopAsync"/> does, then
    /// throws <see cref="OperationCanceledException"/>, or the run's failure if it failed while stopping, or
    /// <see cref="ConnectionLostException"/> if the session was killed.
    /// </summary>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <param name="killSessionAfter">
    /// After the token was cancelled, how long the script may take to stop before the whole session is killed. Null,
    /// the default: wait as long as the script takes.
    /// </param>
    public Task WaitAsync(CancellationToken cancellationToken, TimeSpan? killSessionAfter = null) =>
        throw new NotImplementedException();
}

/// <summary>Settings of a <see cref="CSharpScriptSession{TSessionSide, TSession}"/>.</summary>
public sealed record CSharpScriptSessionOptions
{
    /// <summary>
    /// How long the runner's part of <see cref="CSharpScriptSession{TSessionSide, TSession}.DisposeAsync"/> may take before the
    /// process is killed: ending the runs, finishing calls in the session object, disposing it. Default: 5 s.
    /// </summary>
    public TimeSpan DisposeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Environment variables to set or, with a null value, to remove for the process. Scripts run culture-invariant
    /// unless <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT</c> is <c>0</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Receives each line that the session's scripts write to the console, on any thread. Lines carry no sign of the
    /// run that wrote them; use the bridge, with the run's input, for output that belongs to one run.
    /// </summary>
    public Action<string>? Output { get; init; }

    /// <summary>
    /// Receives the session's messages: starts, kills, crashes, failures during disposal and the process's standard
    /// error.
    /// </summary>
    public ILogger? Logger { get; init; }
}

/// <summary>How a session's process ended.</summary>
public sealed class ScriptSessionExit
{
    private ScriptSessionExit() { }

    /// <summary>Why the process ended.</summary>
    public ScriptSessionExitReason Reason => throw new NotImplementedException();

    /// <summary>The process's exit code.</summary>
    public int ExitCode => throw new NotImplementedException();

    /// <summary>The last lines of the process's standard error, for crash reports.</summary>
    public string StandardError => throw new NotImplementedException();
}

/// <summary>Why a session's process ended.</summary>
public enum ScriptSessionExitReason
{
    /// <summary>The app disposed the session, and the runner ended in time.</summary>
    Disposed,

    /// <summary>The app called <see cref="CSharpScriptSession{TSessionSide, TSession}.KillAsync"/>.</summary>
    Killed,

    /// <summary>It crashed, for example from a stack overflow in a script.</summary>
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
public sealed class ScriptException : RemoteInvocationException
{
    private ScriptException()
        : base(null, 0, (object?)null) { }

    /// <summary>The full name of the exception type that was thrown, such as <c>System.FormatException</c>.</summary>
    public string ScriptExceptionType => throw new NotImplementedException();

    /// <summary>The stack trace in the runner, with script line numbers.</summary>
    public string? ScriptStackTrace => throw new NotImplementedException();
}

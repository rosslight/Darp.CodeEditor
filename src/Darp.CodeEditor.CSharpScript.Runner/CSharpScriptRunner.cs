using Microsoft.Extensions.Logging;
using PolyType;
using StreamJsonRpc;

namespace Darp.CodeEditor.CSharpScript.Runner;

/// <summary>
/// Runs compiled scripts in a separate process, so that a script that hangs or crashes cannot take the app down.
/// </summary>
/// <remarks>
/// <para>
/// One runner is one process, and all scripts run in it share it. The process starts with the first run, or with
/// <see cref="StartAsync"/>. After a crash, or a kill after a call took too long, the next run starts a new one; the
/// scripts and handlers of the old process are gone. Use several runners to keep scripts apart. The process holds no
/// compiler: <see cref="CSharpScriptServer"/> compiles.
/// </para>
/// <para>
/// A script's result can be a handler: an interface marked <c>[RpcMarshalable]</c> that the app keeps calling. The app
/// receives a proxy, and every call crosses into the runner. Its methods are async and copy their data. Calls on a
/// handler end in one of these ways:
/// </para>
/// <list type="bullet">
/// <item>its result, or <see cref="ScriptException"/> when the script threw;</item>
/// <item><see cref="OperationCanceledException"/> when the call's own token was cancelled;</item>
/// <item>
/// <see cref="TimeoutException"/> after <see cref="CSharpScriptRunnerOptions.CallTimeout"/>. The script is asked to
/// stop; if it has not stopped after <see cref="CSharpScriptRunnerOptions.CancellationGracePeriod"/>, the process is
/// killed;
/// </item>
/// <item><see cref="ConnectionLostException"/> when the process ended; <see cref="Exited"/> says why.</item>
/// </list>
/// <para>
/// Dispose a handler to release the script's object. Safe to use from any thread.
/// </para>
/// </remarks>
public sealed class CSharpScriptRunner : IAsyncDisposable
{
    /// <summary>Describes the runner. The process does not start yet.</summary>
    public CSharpScriptRunner(CSharpScriptRunnerOptions? options = null) => throw new NotImplementedException();

    /// <summary>
    /// Raised on any thread when the process ends while scripts or handlers were alive in it. Their pending calls fail
    /// with <see cref="ConnectionLostException"/>.
    /// </summary>
    public event EventHandler<ScriptRunnerExitedEventArgs>? Exited;

    /// <summary>
    /// Starts the process now rather than with the first run, so that the first run starts sooner. Starting has its
    /// own time limit, so a process that never comes up fails the start instead of hanging it.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

    /// <summary>Runs <paramref name="script"/> and returns its result: a handler proxy, or a copy of its data.</summary>
    /// <typeparam name="T">
    /// A handler interface marked <c>[RpcMarshalable]</c>, or a data type; either with <c>[GenerateShape]</c>, which
    /// lets NativeAOT apps call and copy it.
    /// </typeparam>
    /// <param name="script">
    /// The script, compiled by <see cref="CSharpScriptServer"/>. The runner creates its environment's globals and passes
    /// them proxies of the runner's <see cref="CSharpScriptRunnerOptions.Services"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops the run until it returns: its pending calls into the app throw, and when it has not ended after
    /// <see cref="CSharpScriptRunnerOptions.CancellationGracePeriod"/>, the process is killed. It has no effect on the
    /// handler a run returned; the handler lives until it is disposed.
    /// </param>
    /// <exception cref="ScriptException">The script threw.</exception>
    /// <exception cref="ConnectionLostException">The process ended before the script did.</exception>
    /// <exception cref="ArgumentException">
    /// The constructor of <paramref name="script"/>'s globals needs a service that the runner lacks.
    /// </exception>
    public Task<T> RunAsync<T>(CompiledScript script, CancellationToken cancellationToken = default)
        where T : class, IShapeable<T> => throw new NotImplementedException();

    /// <summary>Runs <paramref name="script"/> to its end, for scripts that act through the app's services.</summary>
    /// <param name="script">
    /// The script, compiled by <see cref="CSharpScriptServer"/>. The runner creates its environment's globals and passes
    /// them proxies of the runner's <see cref="CSharpScriptRunnerOptions.Services"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops the run: its pending calls into the app throw, and when it has not ended after
    /// <see cref="CSharpScriptRunnerOptions.CancellationGracePeriod"/>, the process is killed.
    /// </param>
    /// <exception cref="ScriptException">The script threw.</exception>
    /// <exception cref="ConnectionLostException">The process ended before the script did.</exception>
    /// <exception cref="ArgumentException">
    /// The constructor of <paramref name="script"/>'s globals needs a service that the runner lacks.
    /// </exception>
    public Task RunAsync(CompiledScript script, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <summary>Ends the process, and with it every script and handler in it.</summary>
    public ValueTask DisposeAsync() => throw new NotImplementedException();
}

/// <summary>Settings of a <see cref="CSharpScriptRunner"/>.</summary>
public sealed record CSharpScriptRunnerOptions
{
    /// <summary>
    /// The app's services that scripts call. The globals' constructor of every run receives a proxy of each service it
    /// asks for, matched by its interface. The runner keeps the implementations until it is disposed.
    /// </summary>
    public IReadOnlyList<CSharpScriptService> Services { get; init; } = [];

    /// <summary>
    /// How long a call into a handler may take before it fails with <see cref="TimeoutException"/>.
    /// <see cref="Timeout.InfiniteTimeSpan"/>: no limit. Runs have no limit; stop them with their cancellation token.
    /// Default: 10 s.
    /// </summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a cancelled or timed-out script may take to stop before the process is killed. Default: 1 s.
    /// </summary>
    public TimeSpan CancellationGracePeriod { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Environment variables to set or, with a null value, to remove for the process. Scripts run culture-invariant
    /// unless <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT</c> is <c>0</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Receives each line that scripts of this runner write to the console, on any thread. Lines carry no sign of the
    /// script that wrote them; use a service for output that belongs to one run.
    /// </summary>
    public Action<string>? Output { get; init; }

    /// <summary>Receives the runner's messages: starts, kills, crashes and its standard error.</summary>
    public ILogger? Logger { get; init; }
}

/// <summary>A service of the app that scripts call: an implementation of a script API interface.</summary>
public sealed class CSharpScriptService
{
    private CSharpScriptService() { }

    /// <summary>Offers <paramref name="implementation"/> to scripts as <typeparamref name="TService"/>.</summary>
    /// <typeparam name="TService">
    /// The interface scripts see, marked <c>[JsonRpcContract]</c> and <c>[GenerateShape]</c> in the script API. Calls
    /// arrive on any thread.
    /// </typeparam>
    /// <exception cref="ArgumentException"><typeparamref name="TService"/> is not an interface.</exception>
    public static CSharpScriptService Create<TService>(TService implementation)
        where TService : class, IShapeable<TService> => throw new NotImplementedException();
}

/// <summary>Raised when a runner's process ends while scripts or handlers were alive in it.</summary>
public sealed class ScriptRunnerExitedEventArgs : EventArgs
{
    private ScriptRunnerExitedEventArgs() { }

    /// <summary>Why the process ended.</summary>
    public ScriptRunnerExitReason Reason => throw new NotImplementedException();

    /// <summary>The last lines of the process's standard error, for crash reports.</summary>
    public string StandardError => throw new NotImplementedException();
}

/// <summary>Why a runner's process ended.</summary>
public enum ScriptRunnerExitReason
{
    /// <summary>It crashed, for example from a stack overflow in a script.</summary>
    Crashed,

    /// <summary>
    /// A call took longer than <see cref="CSharpScriptRunnerOptions.CallTimeout"/> and did not stop in time.
    /// </summary>
    CallTimedOut,

    /// <summary>
    /// A cancelled run did not stop within <see cref="CSharpScriptRunnerOptions.CancellationGracePeriod"/>.
    /// </summary>
    Cancelled,
}

/// <summary>A script threw. Thrown by runs and by calls on handlers.</summary>
/// <remarks>
/// It is a <see cref="RemoteInvocationException"/>, because handler calls go through StreamJsonRpc's proxies, and
/// those can only turn a script's failure into one of these.
/// </remarks>
public sealed class ScriptException : RemoteInvocationException
{
    private ScriptException()
        : base(null, 0, (object?)null) { }

    /// <summary>The full name of the exception type the script threw, such as <c>System.FormatException</c>.</summary>
    public string ScriptExceptionType => throw new NotImplementedException();

    /// <summary>The stack trace in the script, with script line numbers.</summary>
    public string? ScriptStackTrace => throw new NotImplementedException();
}

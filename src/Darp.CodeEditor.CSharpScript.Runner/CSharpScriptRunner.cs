using Microsoft.Extensions.Logging;

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
/// A script's result can be a handler: an interface marked <c>[RpcMarshalable]</c> that the app keeps calling. The
/// app receives a proxy, and every call crosses into the runner. Its methods are async and copy their data. Dispose
/// the proxy to release the script's object.
/// </para>
/// <para>Safe to use from any thread.</para>
/// </remarks>
public sealed class CSharpScriptRunner : IAsyncDisposable
{
    /// <summary>Describes the runner. The process does not start yet.</summary>
    public CSharpScriptRunner(CSharpScriptRunnerOptions? options = null) => throw new NotImplementedException();

    /// <summary>Raised on any thread when the process ends while scripts or handlers were alive in it.</summary>
    public event EventHandler<ScriptRunnerExitedEventArgs>? Exited;

    /// <summary>Starts the process now rather than with the first run, so that the first run starts sooner.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

    /// <summary>Runs <paramref name="script"/> and returns its result: a handler proxy, or a copy of its value.</summary>
    /// <param name="script">The script, compiled by <see cref="CSharpScriptServer"/>.</param>
    /// <param name="services">
    /// The app's services for this run. The runner creates the environment's globals and passes a proxy of each
    /// service to the globals' constructor, matched by type.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops the script: its pending calls into the app throw, and when it has not ended after
    /// <see cref="CSharpScriptRunnerOptions.CancellationGracePeriod"/>, the process is killed.
    /// </param>
    /// <exception cref="ScriptException">The script threw.</exception>
    /// <exception cref="ScriptRunnerExitedException">The process ended before the script did.</exception>
    public Task<T> RunAsync<T>(
        CompiledScript script,
        IReadOnlyList<CSharpScriptService>? services = null,
        CancellationToken cancellationToken = default
    ) => throw new NotImplementedException();

    /// <summary>Runs <paramref name="script"/> to its end, for scripts that act through the app's services.</summary>
    /// <inheritdoc cref="RunAsync{T}" path="/param"/>
    /// <inheritdoc cref="RunAsync{T}" path="/exception"/>
    public Task RunAsync(
        CompiledScript script,
        IReadOnlyList<CSharpScriptService>? services = null,
        CancellationToken cancellationToken = default
    ) => throw new NotImplementedException();

    /// <summary>Ends the process, and with it every script and handler in it.</summary>
    public ValueTask DisposeAsync() => throw new NotImplementedException();
}

/// <summary>Settings of a <see cref="CSharpScriptRunner"/>.</summary>
public sealed record CSharpScriptRunnerOptions
{
    /// <summary>
    /// How long a call into a handler may take. A slower call is cancelled, and the process is killed after
    /// <see cref="CancellationGracePeriod"/>. <see cref="Timeout.InfiniteTimeSpan"/>: no limit. Runs have no limit;
    /// stop them with their cancellation token. Default: 10 s.
    /// </summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a cancelled script or call may take to end before the process is killed. Default: 1 s.</summary>
    public TimeSpan CancellationGracePeriod { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Environment variables to set or, with a null value, to remove for the process. Scripts run culture-invariant
    /// unless <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT</c> is <c>0</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>Receives each line scripts write to the console, on any thread.</summary>
    public Action<string>? Output { get; init; }

    /// <summary>Receives the runner's messages: starts, kills, crashes and its standard error.</summary>
    public ILogger? Logger { get; init; }
}

/// <summary>
/// A service of the app that scripts call: an implementation of an interface marked <c>[JsonRpcContract]</c>.
/// </summary>
public sealed class CSharpScriptService
{
    private CSharpScriptService() { }

    /// <summary>The interface scripts see.</summary>
    public Type ServiceType => throw new NotImplementedException();

    /// <summary>Offers <paramref name="implementation"/> to scripts as <typeparamref name="TService"/>.</summary>
    public static CSharpScriptService Create<TService>(TService implementation)
        where TService : class => throw new NotImplementedException();
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

    /// <summary>A call took longer than <see cref="CSharpScriptRunnerOptions.CallTimeout"/> and did not stop in time.</summary>
    CallTimedOut,

    /// <summary>A cancelled script did not stop within <see cref="CSharpScriptRunnerOptions.CancellationGracePeriod"/>.</summary>
    Cancelled,

    /// <summary>The runner was disposed.</summary>
    Disposed,
}

/// <summary>A script threw. Thrown by runs and by calls on handlers.</summary>
public sealed class ScriptException : Exception
{
    private ScriptException() { }

    /// <summary>The full name of the exception type the script threw, such as <c>System.FormatException</c>.</summary>
    public string ScriptExceptionType => throw new NotImplementedException();

    /// <summary>The stack trace in the script, with script line numbers.</summary>
    public string? ScriptStackTrace => throw new NotImplementedException();
}

/// <summary>The runner's process ended during a run or a call. Thrown by runs and by calls on handlers.</summary>
public sealed class ScriptRunnerExitedException : Exception
{
    private ScriptRunnerExitedException() { }

    /// <summary>Why the process ended.</summary>
    public ScriptRunnerExitReason Reason => throw new NotImplementedException();
}

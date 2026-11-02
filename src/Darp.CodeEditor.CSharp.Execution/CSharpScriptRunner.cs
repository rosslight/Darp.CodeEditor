using Darp.CodeEditor.CSharp.Execution.Hosting;
using PolyType;
using StreamJsonRpc;

namespace Darp.CodeEditor.CSharp.Execution;

/// <summary>
/// Runs C# scripts in a helper process, the runner, and hands their results to the app as typed proxies. Works the same
/// in JIT and NativeAOT apps.
/// </summary>
/// <remarks>
/// <para>
/// A script ends with an object that implements a handler interface of the app's script API assembly; the app keeps
/// calling it. Scripts call back into the app through the services on their globals. Everything crosses the process
/// boundary as copies: handler and service interfaces have async methods only, no properties, no events and no
/// generic methods.
/// </para>
/// <para>
/// Calls fail with <see cref="ScriptException"/> when script code throws, with <see cref="TimeoutException"/> when
/// they pass <see cref="CSharpScriptRunnerOptions.CallTimeout"/>, and with <see cref="ConnectionLostException"/> when
/// the runner ended. When a runner crashes or is killed, <see cref="RunnerExited"/> reports it, its handlers stop
/// working, and the next <see cref="EvaluateAsync{T}"/> starts a new runner.
/// </para>
/// </remarks>
public sealed class CSharpScriptRunner : IAsyncDisposable
{
    private readonly CSharpScriptRunnerOptions _options;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly CancellationTokenSource _disposed = new();
    private RunnerConnection? _connection;

    private CSharpScriptRunner(CSharpScriptRunnerOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Raised on the thread pool when a runner crashed or was killed for a missed deadline. Handlers it returned no
    /// longer work; evaluate the scripts again.
    /// </summary>
    public event EventHandler<ScriptRunnerExitedEventArgs>? RunnerExited;

    /// <summary>The process ID of the current runner, or null while none runs.</summary>
    public int? ProcessId =>
        Volatile.Read(ref _connection) is { IsAlive: true } connection ? connection.ProcessId : null;

    /// <summary>Starts a runner and loads the environment into it.</summary>
    /// <exception cref="FileNotFoundException">The runner is not next to the app.</exception>
    /// <exception cref="RemoteInvocationException">The runner could not load the environment.</exception>
    public static async Task<CSharpScriptRunner> StartAsync(
        CSharpScriptRunnerOptions options,
        CancellationToken cancellationToken = default
    )
    {
        var runner = new CSharpScriptRunner(options);
        try
        {
            await runner.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            return runner;
        }
        catch
        {
            await runner.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Compiles and runs <paramref name="code"/>, and returns the object it ends with.</summary>
    /// <typeparam name="T">
    /// A handler interface of the script API assembly, with <see cref="RpcMarshalableAttribute"/> and
    /// <see cref="GenerateShapeAttribute"/>. Dispose the handler when it is no longer needed.
    /// </typeparam>
    /// <exception cref="ScriptCompilationException">The script does not compile.</exception>
    /// <exception cref="ScriptException">The script threw, or did not return a <typeparamref name="T"/>.</exception>
    /// <exception cref="TimeoutException">Compiling or running took too long.</exception>
    /// <exception cref="ConnectionLostException">The runner ended while it ran the script.</exception>
    public async Task<T> EvaluateAsync<T>(string code, CancellationToken cancellationToken = default)
        where T : class, IShapeable<T>
    {
        RunnerConnection connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.EvaluateAsync<T>(code, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ends the runner. Its handlers stop working.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed.IsCancellationRequested)
            return;
        _disposed.Cancel();
        await _startLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is { } connection)
                await connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task<RunnerConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _connection) is { IsAlive: true } current)
            return current;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        try
        {
            await _startLock.WaitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(CSharpScriptRunner));
        }
        try
        {
            ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
            if (_connection is { IsAlive: true } started)
                return started;
            if (_connection is { } ended)
                await ended.DisposeAsync().ConfigureAwait(false);
            _connection = null;
            RunnerConnection connection = await RunnerConnection
                .StartAsync(_options, cancellation.Token)
                .ConfigureAwait(false);
            _ = ReportExitAsync(connection);
            Volatile.Write(ref _connection, connection);
            return connection;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task ReportExitAsync(RunnerConnection connection)
    {
        if (await connection.Ended.ConfigureAwait(false) is { } exited)
            RunnerExited?.Invoke(this, exited);
    }
}

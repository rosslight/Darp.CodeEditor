using System.Collections.Concurrent;
using System.Text.Json;
using Darp.CodeEditor.CSharp.Execution.Protocol;
using Darp.CodeEditor.CSharp.Hosting;
using Microsoft.CodeAnalysis.Scripting;
using StreamJsonRpc;

namespace Darp.CodeEditor.CSharp.Runner;

/// <summary>Compiles and runs the host's scripts in one environment.</summary>
internal sealed class ScriptRunnerService(JsonRpc rpc, ShapeProviders shapes, IRunnerHost host) : IRunnerService
{
    private readonly JsonRpc _rpc = rpc;
    private readonly ShapeProviders _shapes = shapes;
    private readonly IRunnerHost _host = host;
    private readonly ConcurrentDictionary<long, PendingScript> _scripts = new();
    private RunnerEnvironment? _environment;

    public Task<int> StartAsync(RunnerStart start, CancellationToken cancellationToken)
    {
        if (_environment is not null)
            throw new InvalidOperationException("The runner already has an environment.");
        using JsonDocument options = JsonDocument.Parse(start.Environment);
        CSharpScriptEnvironment environment =
            CSharpScriptInitializationOptions.Read(options.RootElement)
            ?? throw new ArgumentException("The start request has no environment.", nameof(start));
        _environment = RunnerEnvironment.Load(environment, start.Services, _rpc, _shapes);
        // Compiling once makes the first real compile much faster; the app typically starts the runner ahead of time.
        _ = Task.Run(_environment.WarmUp, CancellationToken.None);
        return Task.FromResult(Environment.ProcessId);
    }

    public Task CompileAsync(long id, string code, RunnerTypeName handler)
    {
        RunnerEnvironment environment = GetEnvironment();
        var pending = new PendingScript();
        if (!_scripts.TryAdd(id, pending))
            throw new ArgumentException($"Script {id} already exists.", nameof(id));
        _ = CompileAndReportAsync(environment, id, code, handler, pending);
        return Task.CompletedTask;
    }

    public async Task<RunnerValue> RunAsync(long id, CancellationToken cancellationToken)
    {
        RunnerEnvironment environment = GetEnvironment();
        if (!_scripts.TryRemove(id, out PendingScript? pending) || pending.Compiled is not { } compiled)
            throw new InvalidOperationException($"Script {id} is not compiled.");
        ScriptState<object> state = await compiled
            .Script.RunAsync(environment.CreateGlobals(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        object? value = state.ReturnValue;
        if (!compiled.HandlerType.IsInstanceOfType(value))
        {
            throw new InvalidOperationException(
                $"The script returned {(value is null ? "null" : $"a {value.GetType()}")}, not a {compiled.HandlerType}. "
                    + $"End the script with an expression of type {compiled.HandlerType.Name}."
            );
        }
        return new RunnerValue(value, compiled.HandlerType);
    }

    public Task ReleaseAsync(long id)
    {
        if (_scripts.TryRemove(id, out PendingScript? pending))
            pending.Cancellation.Cancel();
        return Task.CompletedTask;
    }

    private async Task CompileAndReportAsync(
        RunnerEnvironment environment,
        long id,
        string code,
        RunnerTypeName handler,
        PendingScript pending
    )
    {
        CompileResult result;
        try
        {
            CancellationToken cancellationToken = pending.Cancellation.Token;
            (CompileResult compileResult, CompiledScript? compiled) = await Task.Run(
                    () => environment.Compile(code, handler, cancellationToken),
                    cancellationToken
                )
                .ConfigureAwait(false);
            pending.Compiled = compiled;
            result = compileResult;
        }
        catch (OperationCanceledException) when (pending.Cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            result = new CompileResult([], exception.Message);
        }
        try
        {
            await _host.CompiledAsync(id, result).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ConnectionLostException or ObjectDisposedException)
        {
            // The host is gone; the runner ends with the connection.
        }
    }

    private RunnerEnvironment GetEnvironment() =>
        _environment ?? throw new InvalidOperationException("The runner has no environment yet.");

    private sealed class PendingScript
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public CompiledScript? Compiled { get; set; }
    }
}

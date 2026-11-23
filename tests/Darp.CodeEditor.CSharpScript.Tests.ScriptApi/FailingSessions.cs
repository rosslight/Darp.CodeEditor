using Darp.CodeEditor.CSharpScript.Runner;
using PolyType;
using StreamJsonRpc;
using UseCases.ScriptApi;

namespace ScriptTests.Api;

/// <summary>A session side that does nothing, for session objects that never get to answer.</summary>
public abstract class NoSessionSide : ISessionSide
{
    /// <inheritdoc />
    public Task InitializeAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<string> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult("");

    /// <inheritdoc />
    public Task<double> CalculateAsync(double input, CancellationToken cancellationToken) => Task.FromResult(input);

    /// <inheritdoc />
    public Task<double[]> CalculateManyAsync(double[] inputs, CancellationToken cancellationToken) =>
        Task.FromResult(inputs);
}

/// <summary>A session object whose <c>Create</c> throws.</summary>
public sealed class ThrowingSession : NoSessionSide, IScriptSessionObject<ThrowingSession, IAppSide>
{
    static ThrowingSession IScriptSessionObject<ThrowingSession, IAppSide>.Create(IAppSide app) =>
        throw new FormatException("The session object failed.");
}

/// <summary>A session object whose <c>Create</c> throws its own cancellation.</summary>
public sealed class CancellingSession : NoSessionSide, IScriptSessionObject<CancellingSession, IAppSide>
{
    static CancellingSession IScriptSessionObject<CancellingSession, IAppSide>.Create(IAppSide app) =>
        throw new OperationCanceledException("The session object gave up.");
}

/// <summary>A bridge in an assembly without <c>[assembly: ExportRpcContractProxies]</c>.</summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IUnexportedSide
{
    /// <summary>A call that never happens.</summary>
    Task PingAsync(CancellationToken cancellationToken);
}

/// <summary>A session object on that bridge.</summary>
public sealed class UnexportedSession : IUnexportedSide, IScriptSessionObject<UnexportedSession, IUnexportedSide>
{
    static UnexportedSession IScriptSessionObject<UnexportedSession, IUnexportedSide>.Create(IUnexportedSide app) =>
        new();

    /// <inheritdoc />
    public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

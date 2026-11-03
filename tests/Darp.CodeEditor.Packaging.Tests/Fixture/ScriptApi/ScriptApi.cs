using PolyType;
using StreamJsonRpc;

[assembly: ExportRpcContractProxies]

namespace ScriptApi;

/// <summary>What scripts use without qualification.</summary>
public sealed class Globals(ICalculator calculator)
{
    /// <summary>The app, which scripts call back into.</summary>
    public ICalculator Calculator { get; } = calculator;
}

/// <summary>A host service: scripts call it, the app implements it.</summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ICalculator
{
    Task<int> AddAsync(int a, int b, CancellationToken cancellationToken = default);
}

/// <summary>What scripts return; the app keeps calling it.</summary>
[RpcMarshalable, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IHandler : IDisposable
{
    Task<int> HandleAsync(int value, CancellationToken cancellationToken = default);

    /// <summary>Where the runner's .NET runtime is: the directory of System.Private.CoreLib.</summary>
    Task<string> GetRuntimeDirectoryAsync(CancellationToken cancellationToken = default);
}

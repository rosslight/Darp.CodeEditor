using PolyType;
using StreamJsonRpc;

[assembly: ExportRpcContractProxies]

namespace Darp.CodeEditor.CSharp.Execution.TestApi;

/// <summary>What test scripts use without qualification.</summary>
public sealed class TestGlobals
{
    private readonly ITestHost _host;

    public TestGlobals(ITestHost host)
    {
        _host = host;
        // A private dependency, two levels deep: the runner must find it although scripts never see it.
        Origin = TestDependency.TestDependency.Describe();
    }

    /// <summary>The app, which scripts call back into.</summary>
    public ITestHost Host => _host;

    /// <summary>What the private dependency reported.</summary>
    public string Origin { get; }
}

/// <summary>A host service: scripts call it, the app implements it.</summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ITestHost
{
    Task<int> AddAsync(int a, int b, CancellationToken cancellationToken = default);

    Task LogAsync(string message, CancellationToken cancellationToken = default);
}

/// <summary>What test scripts return; the app keeps calling it.</summary>
[RpcMarshalable, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ITestHandler : IDisposable
{
    Task<int> HandleAsync(Sample sample, CancellationToken cancellationToken = default);

    Task<int[]> HandleBatchAsync(Sample[] samples, CancellationToken cancellationToken = default);
}

/// <summary>A small packet of data, copied to the runner with each call.</summary>
[GenerateShape]
public sealed partial record Sample(int Channel, byte[] Payload);

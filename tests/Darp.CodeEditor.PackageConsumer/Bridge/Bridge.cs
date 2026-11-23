using PolyType;
using StreamJsonRpc;

[assembly: ExportRpcContractProxies]

namespace ConsumerApi;

/// <summary>The app's half: scripts and the session object call it.</summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IConsumerApp
{
    /// <summary>Asks the app, and waits for its answer.</summary>
    Task<string> AskAsync(string question, CancellationToken cancellationToken);
}

/// <summary>The session's half: the app calls it.</summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IConsumerSession
{
    /// <summary>Asks the app in turn, while the app waits for this call.</summary>
    Task<string> AskBackAsync(string question, CancellationToken cancellationToken);

    /// <summary>The id of the runner's process.</summary>
    Task<int> GetProcessIdAsync(CancellationToken cancellationToken);
}

/// <summary>What one run gets from the app.</summary>
/// <param name="Name">The run's name.</param>
[GenerateShape]
public sealed partial record ConsumerInput(string Name);

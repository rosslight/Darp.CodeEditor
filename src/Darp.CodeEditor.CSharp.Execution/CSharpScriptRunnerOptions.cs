using Darp.CodeEditor.CSharp.Hosting;
using PolyType;
using PolyType.Abstractions;
using StreamJsonRpc;

namespace Darp.CodeEditor.CSharp.Execution;

public sealed record CSharpScriptRunnerOptions
{
    /// <summary>
    /// The environment scripts compile in. Use the same description for the editor, so the runner reports the
    /// diagnostics the editor shows.
    /// </summary>
    /// <remarks>
    /// The globals type is created in the runner for each script. Its public constructor takes the host services it
    /// needs, by their interface types; a constructor without parameters works too.
    /// </remarks>
    public required CSharpScriptEnvironment Environment { get; init; }

    /// <summary>App objects scripts call through the globals. Calls arrive on the thread pool, possibly concurrently.</summary>
    public IReadOnlyList<CSharpScriptService> Services { get; init; } = [];

    /// <summary>
    /// How long a call into the runner may take: running a script and every call on its handler. When it passes, the
    /// call fails with <see cref="TimeoutException"/> and the runner is asked to cancel it.
    /// <see cref="Timeout.InfiniteTimeSpan"/> turns deadlines off.
    /// </summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a call may keep running after its deadline before the runner is killed. A script that ignores
    /// cancellation, such as a busy loop, is stopped this way.
    /// </summary>
    public TimeSpan CancellationGracePeriod { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long handing a script to the runner and compiling it may take. Compiling runs no script code and has no call
    /// deadline. A runner that misses this timeout is stuck, for example because it no longer reads its input, and is
    /// killed like one that misses a call deadline.
    /// </summary>
    public TimeSpan CompileTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long starting a runner and loading the environment may take.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Environment variables for the runner. It inherits the app's environment without the variables that start with
    /// <c>DOTNET_</c>, <c>COMPlus_</c> or <c>CORECLR_</c>, such as startup hooks, profilers and runtime settings meant for
    /// the app; set those here when the runner needs them, for example to trace it.
    /// </summary>
    public IReadOnlyDictionary<string, string> RunnerEnvironmentVariables { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// The directory of the runner. Defaults to <c>helpers/csharp-runner</c> next to the app, where the
    /// <c>Darp.CodeEditor.CSharp</c> package puts it: a published app has the runner's app host, which runs on the .NET
    /// runtime in <c>helpers/dotnet</c>; a build output has its assembly, which runs with <c>dotnet</c>.
    /// </summary>
    public string? RunnerDirectory { get; init; }

    /// <summary>
    /// Receives each line the runner writes to its console, including what scripts write with
    /// <see cref="Console.WriteLine()"/>. Lines longer than 4096 characters are cut and end with "…". Called on the
    /// thread pool; exceptions it throws are traced and ignored.
    /// </summary>
    public Action<string>? Output { get; init; }
}

/// <summary>An app object that scripts call through an RPC interface on the globals.</summary>
public sealed class CSharpScriptService
{
    private CSharpScriptService(
        Type serviceType,
        object implementation,
        RpcTargetMetadata metadata,
        ITypeShapeProvider shapes
    )
    {
        ServiceType = serviceType;
        Implementation = implementation;
        Metadata = metadata;
        Shapes = shapes;
    }

    /// <summary>The interface scripts see, which a constructor parameter of the globals type names.</summary>
    public Type ServiceType { get; }

    internal object Implementation { get; }

    internal RpcTargetMetadata Metadata { get; }

    internal ITypeShapeProvider Shapes { get; }

    /// <summary>Exposes <paramref name="implementation"/> to scripts as <typeparamref name="TService"/>.</summary>
    /// <typeparam name="TService">
    /// An interface of the script API assembly with <see cref="JsonRpcContractAttribute"/> and
    /// <see cref="GenerateShapeAttribute"/>.
    /// </typeparam>
    public static CSharpScriptService Create<TService>(TService implementation)
        where TService : class, IShapeable<TService>
    {
        ArgumentNullException.ThrowIfNull(implementation);
        if (!typeof(TService).IsInterface)
            throw new ArgumentException($"{typeof(TService)} is not an interface.", nameof(TService));
        return new CSharpScriptService(
            typeof(TService),
            implementation,
            RpcTargetMetadata.FromShape<TService>(),
            TypeShapeResolver.Resolve<TService>().Provider
        );
    }
}

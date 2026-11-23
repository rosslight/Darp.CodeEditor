using System.Diagnostics.CodeAnalysis;
using Nerdbank.MessagePack;
using PolyType;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

[assembly: ExportRpcContractProxies]

namespace Darp.CodeEditor.CSharpScript.Runner;

/// <summary>What the app asks of the runner itself, beside the script API's own bridge on the same connection.</summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IRunnerControl
{
    /// <summary>
    /// Creates the session object. What <c>Create</c> threw comes back as data: a thrown
    /// <see cref="OperationCanceledException"/> would otherwise arrive as a cancelled call.
    /// </summary>
    Task<RunFailure?> InitializeAsync();

    /// <summary>Accepts a run and starts it; returns before the globals are created.</summary>
    Task BeginAsync(RunRequest request);

    /// <summary>Waits for the end of an accepted run.</summary>
    Task<RunOutcome> JoinAsync(int id);

    /// <summary>Cancels a run's stop token.</summary>
    Task StopAsync(int id);

    /// <summary>Refuses new calls, ends every run, finishes the calls admitted, and disposes the session object.</summary>
    Task<RunFailure?> CloseAsync();
}

/// <summary>One run, with the compiled script and its input by value.</summary>
internal sealed record RunRequest(
    int Id,
    string Globals,
    string InputType,
    byte[] Input,
    byte[] Image,
    string EntryType,
    string EntryMethod
);

/// <summary>A failure in the runner; <paramref name="Run"/> is 0 when no frame of a script is on its stack.</summary>
internal sealed record RunFailure(string Type, string Message, string? Stack, int Run, int Line, int Column);

/// <summary>How a run ended.</summary>
internal sealed record RunOutcome(bool Stopped, RunFailure[] Failures);

/// <summary>The bridge's shapes, with the runner's own control contract.</summary>
internal sealed class BridgeShapes : ITypeShapeProvider
{
    private readonly ITypeShapeProvider[] _providers;

    public BridgeShapes(params ITypeShapeProvider[] providers) =>
        _providers = [ControlShapes<IRunnerControl>(), .. providers];

    private static ITypeShapeProvider ControlShapes<T>()
        where T : IShapeable<T> => T.GetTypeShape().Provider;

    public ITypeShape? GetTypeShape(Type type) =>
        _providers.Select(p => p.GetTypeShape(type)).FirstOrDefault(s => s is not null);
}

/// <summary>The session's connection, as both ends set it up.</summary>
internal static class SessionWire
{
    /// <summary>Control methods carry it, so that they can't collide with a script API's method.</summary>
    internal const string ControlPrefix = "darp/runner/";

    /// <summary>The runner refused a call, because the session is closing.</summary>
    internal const JsonRpcErrorCode SessionClosing = (JsonRpcErrorCode)(-32042);

    internal static readonly MessagePackSerializer Serializer = new();

    internal static IJsonRpcMessageHandler Handler<TApp, TSide>(Stream output, Stream input)
        where TApp : class, IShapeable<TApp>
        where TSide : class, IShapeable<TSide> =>
        new LengthHeaderMessageHandler(
            output,
            input,
            new NerdbankMessagePackFormatter
            {
                TypeShapeProvider = new BridgeShapes(TApp.GetTypeShape().Provider, TSide.GetTypeShape().Provider),
            }
        );

    /// <summary>A generated proxy of <typeparamref name="T"/>: the only kind that works in a NativeAOT app.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Only generated proxies are allowed here.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Only generated proxies are allowed here.")]
    internal static T Proxy<T>(JsonRpc rpc)
        where T : class =>
        (T)
            rpc.Attach(
                typeof(T),
                new JsonRpcProxyOptions
                {
                    ProxySource = JsonRpcProxyOptions.ProxyImplementation.AlwaysSourceGenerated,
                    MethodNameTransform =
                        typeof(T) == typeof(IRunnerControl)
                            ? CommonMethodNameTransforms.Prepend(ControlPrefix)
                            : static name => name,
                }
            );
}

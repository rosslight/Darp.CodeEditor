using System.Collections.Immutable;
using PolyType;
using StreamJsonRpc;

namespace Darp.CodeEditor.CSharp.Execution.Protocol;

/// <summary>The wire format both ends use: MessagePack messages with a length header.</summary>
internal static class RunnerRpc
{
    public static IJsonRpcMessageHandler CreateMessageHandler(Stream output, Stream input, ShapeProviders shapes) =>
        new LengthHeaderMessageHandler(output, input, new NerdbankMessagePackFormatter { TypeShapeProvider = shapes });
}

/// <summary>
/// The type shapes of everything that crosses the connection: the protocol's and those of the script API assemblies,
/// which become known while the connection runs.
/// </summary>
/// <remarks>
/// Generated shapes reach the types used by an API type, so one provider per script API assembly is enough.
/// </remarks>
internal sealed class ShapeProviders : ITypeShapeProvider
{
    private ImmutableArray<ITypeShapeProvider> _providers = [ProtocolShapes.GeneratedTypeShapeProvider];

    public void Add(ITypeShapeProvider provider)
    {
        ImmutableInterlocked.Update(
            ref _providers,
            static (providers, provider) => providers.Contains(provider) ? providers : providers.Add(provider),
            provider
        );
    }

    public ITypeShape? GetTypeShape(Type type)
    {
        foreach (ITypeShapeProvider provider in _providers)
        {
            if (provider.GetTypeShape(type) is { } shape)
                return shape;
        }
        return null;
    }
}

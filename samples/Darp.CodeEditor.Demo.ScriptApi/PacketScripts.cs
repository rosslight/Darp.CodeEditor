using PolyType;
using StreamJsonRpc;

[assembly: ExportRpcContractProxies]

namespace Darp.CodeEditor.Demo.Scripting;

/// <summary>What packet scripts use without qualification.</summary>
public sealed class PacketScriptGlobals(IDeviceApp app)
{
    private readonly IDeviceApp _app = app;

    /// <summary>The app the script runs for: device readings and the output log.</summary>
    public IDeviceApp App => _app;
}

/// <summary>What packet scripts can ask the app. Each call crosses into the app's process.</summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IDeviceApp
{
    /// <summary>Reads the current temperature of the device in degrees Celsius.</summary>
    Task<double> ReadTemperatureAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes a line to the app's output.</summary>
    Task LogAsync(string message, CancellationToken cancellationToken = default);
}

/// <summary>Turns raw packets into readings. A packet script returns one; the app calls it for every packet.</summary>
[RpcMarshalable, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IPacketHandler : IDisposable
{
    /// <summary>Decodes one packet.</summary>
    Task<Reading> HandleAsync(Packet packet, CancellationToken cancellationToken = default);

    /// <summary>Decodes many packets in one call: the way to keep up with thousands of packets per second.</summary>
    Task<Reading[]> HandleBatchAsync(Packet[] packets, CancellationToken cancellationToken = default);
}

/// <summary>A raw packet from the device.</summary>
/// <param name="Channel">The sensor channel.</param>
/// <param name="Payload">The packet's bytes.</param>
[GenerateShape]
public sealed partial record Packet(int Channel, byte[] Payload);

/// <summary>A decoded value.</summary>
/// <param name="Channel">The sensor channel.</param>
/// <param name="Value">The value in <paramref name="Unit"/>.</param>
/// <param name="Unit">The unit, such as "°C".</param>
[GenerateShape]
public sealed partial record Reading(int Channel, double Value, string Unit);

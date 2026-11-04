using PolyType;
using StreamJsonRpc;

// NativeAOT apps cannot generate proxies at run time, so StreamJsonRpc generates them at build time.
[assembly: ExportRpcContractProxies]

namespace UseCases.ScriptApi;

// The script API of the use cases: what scripts see, and what the app and the scripts exchange. It is a separate
// assembly because NativeAOT apps ship it as a DLL next to the app: the language server reads it and the runner
// loads it. Two kinds of scripts use it: packet scripts return a handler the app keeps calling; sequence scripts run
// from start to end and act through the app's services.

/// <summary>What packet scripts use without qualification.</summary>
public sealed class PacketScriptGlobals(IOutputLog log)
{
    /// <summary>The app's output log.</summary>
    public IOutputLog Log { get; } = log;
}

/// <summary>What sequence scripts use without qualification.</summary>
public sealed class SequenceScriptGlobals(IStation station, IOutputLog log)
{
    /// <summary>The station the sequence runs on.</summary>
    public IStation Station { get; } = station;

    /// <summary>The app's output log.</summary>
    public IOutputLog Log { get; } = log;
}

/// <summary>The app's output log. Each call crosses into the app's process.</summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IOutputLog
{
    /// <summary>Writes a line to the app's output.</summary>
    Task WriteAsync(string message, CancellationToken cancellationToken = default);
}

/// <summary>A test station's outputs and measurements. Each call crosses into the app's process.</summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IStation
{
    /// <summary>Switches an output channel on or off.</summary>
    Task SetOutputAsync(int channel, bool on, CancellationToken cancellationToken = default);

    /// <summary>Measures the voltage at an input channel, in volts.</summary>
    Task<double> MeasureAsync(int channel, CancellationToken cancellationToken = default);
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

/// <summary>A raw packet from a device.</summary>
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

using Darp.CodeEditor.CSharpScript.Runner;
using UseCases.ScriptApi;

namespace UseCases.HighFrequency;

// Scenario: a device sends thousands of packets per second, and the packet script's handler decodes them. A call per
// packet crosses the process boundary every time (about 30 µs per call in the prototype, so about 30,000 calls per
// second at best); the batcher sends them in batches instead (about 1.5 million packets per second in the prototype).
//
// Questions:
// - Is a batch method on the script API (HandleBatchAsync) acceptable, or should batching be invisible to scripts?
// - Do packets need an order guarantee across batches? The batcher keeps it.

/// <summary>Decodes the packets of one device with a packet script's handler.</summary>
public sealed class PacketPipeline(IPacketHandler handler) : IAsyncDisposable
{
    private readonly ScriptBatcher<Packet, Reading> _batcher = new(packets => handler.HandleBatchAsync(packets));

    /// <summary>
    /// Decodes one packet; called for every packet the device sends. The token only stops waiting for room in the
    /// queue.
    /// </summary>
    public Task<Reading> DecodeAsync(Packet packet, CancellationToken cancellationToken) =>
        _batcher.SubmitAsync(packet, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _batcher.DisposeAsync();
        handler.Dispose();
    }
}

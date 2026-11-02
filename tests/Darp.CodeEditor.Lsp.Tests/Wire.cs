using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darp.CodeEditor.Lsp.Tests;

/// <summary>The far end of an in-memory connection, which writes raw bytes and reads framed JSON.</summary>
internal sealed class Wire
{
    private readonly Pipe _toEndpoint = new();
    private readonly Pipe _fromEndpoint = new();

    /// <summary>The input of the endpoint under test.</summary>
    public Stream Input => _toEndpoint.Reader.AsStream();

    /// <summary>The output of the endpoint under test.</summary>
    public Stream Output => _fromEndpoint.Writer.AsStream();

    /// <summary>The far end as streams, for a second <see cref="JsonRpcConnection"/>.</summary>
    public (Stream Input, Stream Output) Peer => (_fromEndpoint.Reader.AsStream(), _toEndpoint.Writer.AsStream());

    /// <summary>Cancelled when the test run is cancelled, or after 30 seconds.</summary>
    public static CancellationToken Timeout
    {
        get
        {
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return timeout.Token;
        }
    }

    public async Task SendRawAsync(params IEnumerable<byte[]> chunks)
    {
        foreach (byte[] chunk in chunks)
        {
            await _toEndpoint.Writer.WriteAsync(chunk, Timeout);
            await _toEndpoint.Writer.FlushAsync(Timeout);
        }
    }

    public Task SendRawAsync(string text) => SendRawAsync(Encoding.UTF8.GetBytes(text));

    public Task SendAsync(string json) => SendRawAsync(Frame(json));

    public Task RequestAsync(int id, string method, string parameters = "null") =>
        SendAsync($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""");

    public Task NotifyAsync(string method, string parameters = "null") =>
        SendAsync($$"""{"jsonrpc":"2.0","method":"{{method}}","params":{{parameters}}}""");

    public Task CancelAsync(int id) => NotifyAsync("$/cancelRequest", $$"""{"id":{{id}}}""");

    /// <summary>Ends the input of the endpoint.</summary>
    public void CloseInput() => _toEndpoint.Writer.Complete();

    public static byte[] Frame(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        return [.. Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body];
    }

    /// <summary>Reads the next message the endpoint wrote.</summary>
    public async Task<JsonObject> ReceiveAsync()
    {
        PipeReader reader = _fromEndpoint.Reader;
        while (true)
        {
            ReadResult read = await reader.ReadAsync(Timeout);
            if (TryReadFrame(read.Buffer, out SequencePosition consumed, out JsonObject? message))
            {
                reader.AdvanceTo(consumed);
                return message;
            }
            if (read.IsCompleted)
                throw new EndOfStreamException("The endpoint closed its output.");
            reader.AdvanceTo(read.Buffer.Start, read.Buffer.End);
        }
    }

    /// <summary>Reads messages until one has <paramref name="id"/>, and returns it.</summary>
    public async Task<JsonObject> ReceiveResponseAsync(int id)
    {
        while (true)
        {
            JsonObject message = await ReceiveAsync();
            if (message["method"] is null && (int?)message["id"] == id)
                return message;
        }
    }

    private static bool TryReadFrame(
        ReadOnlySequence<byte> buffer,
        out SequencePosition consumed,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JsonObject? message
    )
    {
        consumed = buffer.Start;
        message = null;
        string text = Encoding.ASCII.GetString(buffer.Slice(0, Math.Min(buffer.Length, 256)));
        int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
            return false;
        string header = text[..headerEnd];
        int length = int.Parse(header["Content-Length: ".Length..], CultureInfo.InvariantCulture);
        long headerBytes = headerEnd + 4;
        if (buffer.Length < headerBytes + length)
            return false;
        ReadOnlySequence<byte> body = buffer.Slice(headerBytes, length);
        message = JsonNode.Parse(Encoding.UTF8.GetString(body))?.AsObject() ?? throw new InvalidDataException();
        consumed = body.End;
        return true;
    }
}

internal static class JsonNodeExtensions
{
    public static int? ErrorCode(this JsonObject response) => (int?)response["error"]?["code"];
}

/// <summary>Request and notification types for tests, with untyped JSON.</summary>
internal static class TestMethods
{
    public static RequestType<JsonElement, JsonElement> Request(string method) =>
        new(method, Protocol.LspJsonContext.Default.JsonElement, Protocol.LspJsonContext.Default.JsonElement);

    public static NotificationType<JsonElement> Notification(string method) =>
        new(method, Protocol.LspJsonContext.Default.JsonElement);

    public static JsonElement Json(string json) =>
        JsonSerializer.Deserialize(json, Protocol.LspJsonContext.Default.JsonElement);
}

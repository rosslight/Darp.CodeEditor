using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Lsp.Hosting;
using Darp.CodeEditor.Lsp.Testing;

namespace Darp.CodeEditor.Tests.Lsp;

/// <summary>
/// Starts toy servers in this process, one per start of a <see cref="LspLanguageService"/>, over in-memory pipes. It
/// records what the client sends, and can crash the running server.
/// </summary>
internal sealed class ToyServers
{
    private readonly List<JsonObject> _sent = [];
    private TaskCompletionSource _writes = Open();
    private ToyServer? _current;
    private int _starts;

    public int Starts => Volatile.Read(ref _starts);

    /// <summary>Every message the client sent, to all servers so far.</summary>
    public IReadOnlyList<JsonObject> Sent
    {
        get
        {
            lock (_sent)
                return [.. _sent];
        }
    }

    /// <summary>A service of these servers, which restart after 10 ms.</summary>
    public LspLanguageService CreateService() =>
        new(
            new LspLanguageServiceOptions(StartAsync, "toy")
            {
                Settings = item => item.Section == "toy" ? ToyServer.ToJson("""{"greeting":"Hi"}""") : null,
                // The toy's words and numbers, colored as locals and constants.
                TokenTypes = new Dictionary<string, ClassificationKind?>
                {
                    ["word"] = ClassificationKind.Local,
                    ["number"] = ClassificationKind.Constant,
                },
                Restart = new SupervisorOptions { FirstDelay = TimeSpan.FromMilliseconds(10) },
            }
        );

    /// <summary>The messages of a method the client sent.</summary>
    public IReadOnlyList<JsonObject> SentOf(string method) => [.. Sent.Where(m => (string?)m["method"] == method)];

    /// <summary>Holds what the client writes until <see cref="UnblockWrites"/>, as a server that reads nothing.</summary>
    public void BlockWrites() => _writes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void UnblockWrites() => _writes.TrySetResult();

    /// <summary>Ends the running server as a crash would: its connection closes without shutdown.</summary>
    public Task CrashAsync() =>
        (_current ?? throw new InvalidOperationException("No server runs.")).Server.DisposeAsync().AsTask();

    private Task<LanguageServerTransport> StartAsync(CancellationToken cancellationToken)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var server = new ToyServer(toServer.Reader.AsStream(), toClient.Writer.AsStream());
        Task<int> run = server.Server.RunAsync();
        _current = server;
        Interlocked.Increment(ref _starts);
        var output = new RecordingStream(toServer.Writer.AsStream(), Record, () => _writes.Task);
        return Task.FromResult(
            new LanguageServerTransport(
                toClient.Reader.AsStream(),
                output,
                run,
                async () =>
                {
                    await server.Server.DisposeAsync();
                    await toServer.Writer.CompleteAsync();
                    await toClient.Writer.CompleteAsync();
                }
            )
        );
    }

    private static TaskCompletionSource Open()
    {
        var open = new TaskCompletionSource();
        open.SetResult();
        return open;
    }

    private void Record(JsonObject message)
    {
        lock (_sent)
            _sent.Add(message);
    }

    /// <summary>Passes writes on, and reads the LSP messages in them.</summary>
    private sealed class RecordingStream(Stream inner, Action<JsonObject> record, Func<Task> gate) : Stream
    {
        private readonly Stream _inner = inner;
        private readonly Action<JsonObject> _record = record;
        private readonly Func<Task> _gate = gate;
        private readonly ArrayBufferWriter<byte> _buffer = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Record(buffer);
            _inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            Record(buffer.Span);
            await _gate().WaitAsync(cancellationToken);
            await _inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        private void Record(ReadOnlySpan<byte> bytes)
        {
            lock (_buffer)
            {
                _buffer.Write(bytes);
                while (TryReadMessage(out JsonObject? message, out int length))
                {
                    _record(message);
                    byte[] rest = _buffer.WrittenSpan[length..].ToArray();
                    _buffer.ResetWrittenCount();
                    _buffer.Write(rest);
                }
            }
        }

        private bool TryReadMessage(
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JsonObject? message,
            out int length
        )
        {
            message = null;
            length = 0;
            ReadOnlySpan<byte> written = _buffer.WrittenSpan;
            int headerEnd = written.IndexOf("\r\n\r\n"u8);
            if (headerEnd < 0)
                return false;
            string header = Encoding.ASCII.GetString(written[..headerEnd]);
            int bodyLength = int.Parse(header["Content-Length: ".Length..], CultureInfo.InvariantCulture);
            if (written.Length < headerEnd + 4 + bodyLength)
                return false;
            message = JsonNode.Parse(written.Slice(headerEnd + 4, bodyLength))?.AsObject() ?? new JsonObject();
            length = headerEnd + 4 + bodyLength;
            return true;
        }
    }
}

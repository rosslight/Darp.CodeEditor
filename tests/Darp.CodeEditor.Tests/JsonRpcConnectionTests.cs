using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.Tests;

/// <summary>JSON-RPC with LSP's framing: reading what a peer writes, and requests and cancellations both ways.</summary>
public sealed class JsonRpcConnectionTests
{
    [Fact]
    public async Task Messages_split_across_writes_and_merged_into_one_are_read_alike()
    {
        var notifications = new ConcurrentQueue<string>();
        await using var peer = new Peer(notifications: (method, _) => notifications.Enqueue(method));
        byte[] first = Peer.Frame("""{"jsonrpc":"2.0","method":"first"}""");

        // Split in the header and in the body.
        await peer.WriteAsync(first[..5]);
        await peer.WriteAsync(first[5..30]);
        await peer.WriteAsync(first[30..]);
        await peer.WriteAsync([
            .. Peer.Frame("""{"jsonrpc":"2.0","method":"second"}"""),
            .. Peer.Frame("""{"jsonrpc":"2.0","method":"third","params":{"text":"ä😀"}}"""),
        ]);

        await Wait.UntilAsync(() => notifications.Count == 3);
        notifications.ShouldBe(["first", "second", "third"]);
    }

    [Fact]
    public async Task A_message_over_the_size_limit_ends_the_connection()
    {
        await using var peer = new Peer(maxMessageLength: 100);
        Task<JsonElement> request = peer.Connection.SendRequestAsync(
            "waiting",
            null,
            TestContext.Current.CancellationToken
        );

        await peer.WriteAsync(Peer.Frame($$"""{"jsonrpc":"2.0","method":"big","params":"{{new string('x', 100)}}"}"""));

        (await peer.Connection.Ended).ShouldBeOfType<InvalidDataException>().Message.ShouldContain("limit");
        await Should.ThrowAsync<IOException>(request);
    }

    [Theory]
    [InlineData("{oops", JsonRpcErrorCodes.ParseError)]
    [InlineData("[1, 2]", JsonRpcErrorCodes.InvalidRequest)]
    [InlineData("""{"jsonrpc":"2.0","params":{}}""", JsonRpcErrorCodes.InvalidRequest)]
    public async Task An_unreadable_message_is_answered_with_an_error_and_reading_goes_on(string message, int code)
    {
        var notifications = new ConcurrentQueue<string>();
        await using var peer = new Peer(notifications: (method, _) => notifications.Enqueue(method));

        await peer.WriteAsync(Peer.Frame(message));
        await peer.WriteAsync(Peer.Frame("""{"jsonrpc":"2.0","method":"after"}"""));

        JsonNode response = await peer.ReadAsync();
        response["id"].ShouldBeNull();
        ((int?)response["error"]?["code"]).ShouldBe(code);
        await Wait.UntilAsync(() => notifications.Contains("after"));
    }

    [Fact]
    public async Task Cancelling_a_written_request_ends_the_call_and_sends_cancelRequest()
    {
        await using var peer = new Peer();
        using var cancellation = new CancellationTokenSource();
        Task<JsonElement> call = peer.Connection.SendRequestAsync("slow", null, cancellation.Token);
        JsonNode request = await peer.ReadAsync();

        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(call);
        JsonNode cancel = await peer.ReadAsync();
        ((string?)cancel["method"]).ShouldBe("$/cancelRequest");
        ((int?)cancel["params"]?["id"]).ShouldBe((int?)request["id"]);
        // The late response is ignored, and the connection goes on.
        await peer.WriteAsync(Peer.Frame($$"""{"jsonrpc":"2.0","id":{{request["id"]}},"result":1}"""));
        Task<JsonElement> next = peer.Connection.SendRequestAsync("next", null, TestContext.Current.CancellationToken);
        JsonNode nextRequest = await peer.ReadAsync();
        await peer.WriteAsync(Peer.Frame($$"""{"jsonrpc":"2.0","id":{{nextRequest["id"]}},"result":2}"""));
        (await next).GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task A_request_cancelled_before_it_was_written_is_left_out()
    {
        // The peer reads nothing yet, so the writer waits on the first message, which fills the pipe.
        await using var peer = new Peer(pauseWriterThreshold: 1024);
        string large = new('x', 4096);
        peer.Connection.Notify("large", writer => writer.WriteStringValue(large));
        using var cancellation = new CancellationTokenSource();
        Task<JsonElement> call = peer.Connection.SendRequestAsync("never", null, cancellation.Token);
        peer.Connection.Notify("after", null);

        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(call);

        ((string?)(await peer.ReadAsync())["method"]).ShouldBe("large");
        ((string?)(await peer.ReadAsync())["method"]).ShouldBe("after");
    }

    [Fact]
    public async Task A_request_that_the_peer_cancels_is_answered_RequestCancelled()
    {
        await using var peer = new Peer(
            requests: async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            }
        );

        await peer.WriteAsync(Peer.Frame("""{"jsonrpc":"2.0","id":"a","method":"slow"}"""));
        await peer.WriteAsync(Peer.Frame("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":"a"}}"""));

        JsonNode response = await peer.ReadAsync();
        ((string?)response["id"]).ShouldBe("a");
        ((int?)response["error"]?["code"]).ShouldBe(JsonRpcErrorCodes.RequestCancelled);
    }

    [Fact]
    public async Task Responses_and_cancellations_get_through_while_the_peers_requests_run()
    {
        await using var peer = new Peer(
            requests: async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            }
        );
        Task<JsonElement> call = peer.Connection.SendRequestAsync("mine", null, TestContext.Current.CancellationToken);
        JsonNode request = await peer.ReadAsync();

        // More requests than reading once let run before it waited for one of them.
        for (int i = 0; i < 20; i++)
            await peer.WriteAsync(Peer.Frame($$"""{"jsonrpc":"2.0","id":{{i}},"method":"slow"}"""));
        await peer.WriteAsync(Peer.Frame($$$"""{"jsonrpc":"2.0","id":{{{request["id"]}}},"result":"answered"}"""));
        for (int i = 0; i < 20; i++)
        {
            await peer.WriteAsync(
                Peer.Frame($$$"""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":{{{i}}}}}""")
            );
        }

        (await Wait.ForAsync(call)).GetString().ShouldBe("answered");
        var cancelled = new List<int?>();
        for (int i = 0; i < 20; i++)
        {
            JsonNode response = await peer.ReadAsync();
            ((int?)response["error"]?["code"]).ShouldBe(JsonRpcErrorCodes.RequestCancelled);
            cancelled.Add((int?)response["id"]);
        }
        cancelled.ShouldBe(Enumerable.Range(0, 20).Select(i => (int?)i), ignoreOrder: true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_peer_that_sends_without_reading_the_answers_is_disconnected(bool requests)
    {
        // The peer reads nothing, so the answers to its requests, or to its invalid messages, wait to be written.
        await using var peer = new Peer(
            requests: (_, _, _) => Task.FromResult<JsonNode?>(null),
            pauseWriterThreshold: 64,
            maxWaitingMessages: 10
        );

        for (int i = 0; i < 100 && !peer.Connection.Ended.IsCompleted; i++)
        {
            string message = requests ? $$"""{"jsonrpc":"2.0","id":{{i}},"method":"quick"}""" : "{oops";
            await peer.WriteAsync(Peer.Frame(message));
        }

        (await Wait.ForAsync(peer.Connection.Ended))
            .ShouldBeOfType<IOException>()
            .Message.ShouldContain("does not read");
    }

    [Theory]
    [InlineData("""{"code":"bad","message":"failure"}""", JsonRpcErrorCodes.InternalError, "failure")]
    [InlineData("""{"code":-32601,"message":7}""", JsonRpcErrorCodes.MethodNotFound, "The request failed.")]
    [InlineData("\"failure\"", JsonRpcErrorCodes.InternalError, "The request failed.")]
    public async Task An_error_answer_with_parts_of_another_JSON_kind_still_fails_its_call(
        string error,
        int code,
        string message
    )
    {
        await using var peer = new Peer();
        Task<JsonElement> call = peer.Connection.SendRequestAsync("asked", null, TestContext.Current.CancellationToken);
        JsonNode request = await peer.ReadAsync();

        await peer.WriteAsync(Peer.Frame($$$"""{"jsonrpc":"2.0","id":{{{request["id"]}}},"error":{{{error}}}}"""));

        JsonRpcException exception = await Should.ThrowAsync<JsonRpcException>(Wait.ForAsync(call));
        exception.Code.ShouldBe(code);
        exception.Message.ShouldBe(message);
        peer.Connection.Ended.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task An_answer_to_an_id_this_end_never_used_is_ignored()
    {
        await using var peer = new Peer();
        Task<JsonElement> call = peer.Connection.SendRequestAsync("asked", null, TestContext.Current.CancellationToken);
        JsonNode request = await peer.ReadAsync();

        await peer.WriteAsync(Peer.Frame("""{"jsonrpc":"2.0","id":"not ours","result":1}"""));
        await peer.WriteAsync(Peer.Frame($$"""{"jsonrpc":"2.0","id":{{request["id"]}},"result":2}"""));

        (await Wait.ForAsync(call)).GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task The_peers_requests_are_cancelled_when_the_connection_ends()
    {
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        await using var peer = new Peer(
            requests: async (_, _, cancellationToken) =>
            {
                started.TrySetResult();
                using (cancellationToken.Register(() => cancelled.TrySetResult()))
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            }
        );
        await peer.WriteAsync(Peer.Frame("""{"jsonrpc":"2.0","id":1,"method":"slow"}"""));
        await Wait.ForAsync(started.Task);

        await peer.Connection.DisposeAsync();

        await Wait.ForAsync(cancelled.Task);
    }

    [Fact]
    public async Task A_peers_error_response_fails_the_call_with_its_code()
    {
        await using var peer = new Peer();
        Task<JsonElement> call = peer.Connection.SendRequestAsync(
            "refused",
            null,
            TestContext.Current.CancellationToken
        );
        JsonNode request = await peer.ReadAsync();

        await peer.WriteAsync(
            Peer.Frame($$$"""{"jsonrpc":"2.0","id":{{{request["id"]}}},"error":{"code":-32601,"message":"no"}}""")
        );

        JsonRpcException exception = await Should.ThrowAsync<JsonRpcException>(call);
        exception.Code.ShouldBe(JsonRpcErrorCodes.MethodNotFound);
        exception.Message.ShouldBe("no");
    }

    [Fact]
    public async Task A_message_that_cannot_be_built_fails_only_its_own_call()
    {
        await using var peer = new Peer();

        await Should.ThrowAsync<InvalidOperationException>(
            peer.Connection.SendRequestAsync(
                "broken",
                _ => throw new InvalidOperationException("Not serializable."),
                TestContext.Current.CancellationToken
            )
        );

        Task<JsonElement> next = peer.Connection.SendRequestAsync("next", null, TestContext.Current.CancellationToken);
        JsonNode request = await peer.ReadAsync();
        ((string?)request["method"]).ShouldBe("next");
        await peer.WriteAsync(Peer.Frame($$"""{"jsonrpc":"2.0","id":{{request["id"]}},"result":"ok"}"""));
        (await next).GetString().ShouldBe("ok");
    }

    [Fact]
    public async Task A_failed_write_ends_the_connection_and_fails_its_calls()
    {
        var input = new Pipe();
        var connection = new JsonRpcConnection(
            input.Reader.AsStream(),
            new BrokenStream(),
            (_, _, _) => Task.FromResult<JsonNode?>(null),
            (_, _) => { }
        );
        connection.Start();

        await Should.ThrowAsync<IOException>(
            connection.SendRequestAsync("lost", null, TestContext.Current.CancellationToken)
        );

        (await connection.Ended).ShouldBeOfType<IOException>();
        await Should.ThrowAsync<IOException>(
            connection.SendRequestAsync("after", null, TestContext.Current.CancellationToken)
        );
        await connection.DisposeAsync();
    }

    /// <summary>The other end of a connection, over pipes: it writes raw bytes and reads whole messages.</summary>
    private sealed class Peer : IAsyncDisposable
    {
        private readonly Pipe _toConnection = new();
        private readonly Pipe _fromConnection;

        public Peer(
            RequestHandler? requests = null,
            NotificationHandler? notifications = null,
            int maxMessageLength = JsonRpcConnection.DefaultMaxMessageLength,
            long pauseWriterThreshold = 65536,
            int maxWaitingMessages = JsonRpcConnection.DefaultMaxWaitingMessages
        )
        {
            _fromConnection = new Pipe(
                new PipeOptions(
                    pauseWriterThreshold: pauseWriterThreshold,
                    resumeWriterThreshold: pauseWriterThreshold / 2
                )
            );
            Connection = new JsonRpcConnection(
                _toConnection.Reader.AsStream(),
                _fromConnection.Writer.AsStream(),
                requests ?? ((method, _, _) => throw new JsonRpcException(JsonRpcErrorCodes.MethodNotFound, method)),
                notifications ?? ((_, _) => { }),
                maxMessageLength,
                maxWaitingMessages
            );
            Connection.Start();
        }

        public JsonRpcConnection Connection { get; }

        public static byte[] Frame(string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            return [.. Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body];
        }

        public async Task WriteAsync(byte[] bytes)
        {
            await _toConnection.Writer.WriteAsync(bytes, TestContext.Current.CancellationToken);
            // Lets the connection read this part before the next one arrives.
            await Task.Yield();
        }

        /// <summary>Reads the next message that the connection wrote.</summary>
        public async Task<JsonNode> ReadAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            PipeReader reader = _fromConnection.Reader;
            while (true)
            {
                ReadResult result = await reader.ReadAsync(timeout.Token);
                ReadOnlySequence<byte> buffer = result.Buffer;
                var sequence = new SequenceReader<byte>(buffer);
                if (
                    sequence.TryReadTo(out ReadOnlySequence<byte> header, "\r\n\r\n"u8)
                    && int.Parse(Encoding.ASCII.GetString(header).Split(':')[1]) is var length
                    && sequence.Remaining >= length
                )
                {
                    JsonNode message =
                        JsonNode.Parse(buffer.Slice(sequence.Position, length).ToArray())
                        ?? throw new InvalidDataException("A null message.");
                    reader.AdvanceTo(buffer.GetPosition(length, sequence.Position));
                    return message;
                }
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _toConnection.Writer.CompleteAsync();
            await _fromConnection.Reader.CompleteAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>A stream whose writes fail, as a pipe to a process that ended.</summary>
    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Broken pipe.");
    }
}

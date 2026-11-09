using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Darp.CodeEditor.Lsp;

/// <summary>Answers a request of the peer, on the thread pool. The returned node is the response's result.</summary>
/// <remarks>
/// Throw <see cref="JsonRpcException"/> to answer with an error, such as <see cref="JsonRpcErrorCodes.MethodNotFound"/>;
/// <see cref="JsonException"/> answers <see cref="JsonRpcErrorCodes.InvalidParams"/>, and other exceptions
/// <see cref="JsonRpcErrorCodes.InternalError"/>. A request that the peer cancelled answers
/// <see cref="JsonRpcErrorCodes.RequestCancelled"/>, whatever the handler returns.
/// </remarks>
internal delegate Task<JsonNode?> RequestHandler(
    string method,
    JsonElement parameters,
    CancellationToken cancellationToken
);

/// <summary>Handles a notification of the peer, on the read loop, so it returns quickly.</summary>
/// <remarks>A notification whose parameters fail with <see cref="JsonException"/> is ignored: it gets no answer.</remarks>
internal delegate void NotificationHandler(string method, JsonElement parameters);

/// <summary>
/// One end of a JSON-RPC 2.0 connection, with the <c>Content-Length</c> framing of the Language Server Protocol: the
/// client's end of a language server's standard output and input, or the server's end of its own.
/// </summary>
/// <remarks>
/// <para>
/// A read loop reads the peer's messages one at a time and never waits for a handler, so responses and cancellations
/// always get through. It completes the responses to requests, cancels the peer's requests on <c>$/cancelRequest</c>,
/// passes notifications to their handler, and starts each request's handler on the thread pool. An invalid message is
/// answered with an error and the connection goes on. A message over the size limit, or broken framing, ends the
/// connection: skipping it would lose a request or a response without notice.
/// </para>
/// <para>
/// A write loop writes the queued messages in order, each built when its turn comes, off the sender's thread. A message
/// that cannot be built fails alone. A write that fails ends the connection.
/// </para>
/// <para>
/// The write queue has no capacity of its own: what waits in it is bounded by who puts it there. Each request has its
/// caller, and each <c>$/cancelRequest</c> a written request. The peer's requests, and its messages that get an error
/// answer, count as unanswered until their answer is written; a peer with more unanswered than the limit sends faster
/// than it reads, and the connection ends. Senders whose messages could pile up behind a peer that stops reading, such
/// as a document's changes, queue one <see cref="OutgoingMessage"/> that takes what is new when it is built.
/// </para>
/// <para>The connection does not own the streams: their owner closes them, for example by ending the process.</para>
/// </remarks>
internal sealed class JsonRpcConnection : IAsyncDisposable
{
    /// <summary>The largest message the peer may send, in bytes; a larger one ends the connection.</summary>
    public const int DefaultMaxMessageLength = 64 * 1024 * 1024;

    /// <summary>
    /// How many of the peer's messages may wait for their answer to be written, running or queued, before the connection
    /// ends.
    /// </summary>
    public const int DefaultMaxUnansweredRequests = 1_000;

    /// <summary>A header is a line or two, such as <c>Content-Length: 1234</c>.</summary>
    private const int MaxHeaderLength = 1024;

    /// <summary>The output is no HTML page: relaxed escaping keeps text readable and short.</summary>
    private static readonly JsonWriterOptions s_writerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly RequestHandler _handleRequest;
    private readonly NotificationHandler _handleNotification;
    private readonly int _maxMessageLength;
    private readonly int _maxUnansweredRequests;

    private readonly Channel<OutgoingMessage> _queue = Channel.CreateUnbounded<OutgoingMessage>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    /// <summary>Cancelled when the connection ends, to stop both loops.</summary>
    private readonly CancellationTokenSource _stop = new();

    private readonly TaskCompletionSource<Exception?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Lock _lock = new();

    /// <summary>Guarded by <see cref="_lock"/>: the requests sent and not answered yet, by id.</summary>
    private readonly Dictionary<int, Request> _requests = [];

    /// <summary>
    /// Guarded by <see cref="_lock"/>: the peer's requests whose handlers run, by the JSON text of their id. Their
    /// sources have no timer and no parent, so they need no disposal.
    /// </summary>
    private readonly Dictionary<string, CancellationTokenSource> _peerRequests = [];

    /// <summary>Guarded by <see cref="_lock"/>.</summary>
    private int _lastId;

    /// <summary>Guarded by <see cref="_lock"/>: the peer's messages whose answers are not written yet.</summary>
    private int _unansweredRequests;

    /// <summary>Guarded by <see cref="_lock"/>.</summary>
    private bool _isEnded;

    private Task _reading = Task.CompletedTask;
    private Task _writing = Task.CompletedTask;

    /// <param name="input">The peer's output, such as a language server's standard output.</param>
    /// <param name="output">The peer's input, such as a language server's standard input.</param>
    /// <param name="handleRequest">Answers the peer's requests.</param>
    /// <param name="handleNotification">Handles the peer's notifications.</param>
    /// <param name="maxMessageLength">The largest message the peer may send, in bytes.</param>
    /// <param name="maxUnansweredRequests">How many of the peer's messages may wait for their answer.</param>
    public JsonRpcConnection(
        Stream input,
        Stream output,
        RequestHandler handleRequest,
        NotificationHandler handleNotification,
        int maxMessageLength = DefaultMaxMessageLength,
        int maxUnansweredRequests = DefaultMaxUnansweredRequests
    )
    {
        _input = input;
        _output = output;
        _handleRequest = handleRequest;
        _handleNotification = handleNotification;
        _maxMessageLength = maxMessageLength;
        _maxUnansweredRequests = maxUnansweredRequests;
    }

    /// <summary>
    /// Completes once the connection ended: with null when the input ended or the connection was disposed, otherwise
    /// with why reading or writing failed.
    /// </summary>
    public Task<Exception?> Ended => _ended.Task;

    /// <summary>Starts reading and writing.</summary>
    public void Start()
    {
        _reading = Task.Run(ReadAsync);
        _writing = Task.Run(WriteAsync);
    }

    /// <summary>Sends a request and returns its result, or <c>default</c> when the result is missing.</summary>
    /// <remarks>
    /// Cancelling <paramref name="cancellationToken"/> ends the call at once. A request that was not written yet is
    /// then left out; a written one is followed by <c>$/cancelRequest</c>, and its late response is ignored.
    /// </remarks>
    /// <param name="method">The method, such as <c>initialize</c>.</param>
    /// <param name="writeParams">Writes the params, when the request is written; null for none.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="JsonRpcException">The peer answered with an error.</exception>
    /// <exception cref="IOException">The connection ended before the response arrived.</exception>
    public async Task<JsonElement> SendRequestAsync(
        string method,
        Action<Utf8JsonWriter>? writeParams,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new Request(this, method, writeParams);
        lock (_lock)
        {
            if (_isEnded)
                throw new IOException("The connection ended.");
            request.Id = ++_lastId;
            _requests.Add(request.Id, request);
        }
        Send(request);
        using (cancellationToken.Register(() => Cancel(request, cancellationToken)))
            return await request.Response.Task.ConfigureAwait(false);
    }

    /// <summary>Sends a notification. One that cannot be built is dropped; it has no caller to fail.</summary>
    /// <param name="method">The method, such as <c>initialized</c>.</param>
    /// <param name="writeParams">Writes the params, when the notification is written; null for none.</param>
    public void Notify(string method, Action<Utf8JsonWriter>? writeParams) =>
        Send(new Notification(method, writeParams));

    /// <summary>Queues a message that its sender builds when its turn comes.</summary>
    public void Send(OutgoingMessage message)
    {
        if (!_queue.Writer.TryWrite(message))
            message.Fail(new IOException("The connection ended."));
    }

    /// <summary>Ends the connection and waits for both loops.</summary>
    /// <remarks>
    /// A write that blocks on a peer that does not read, and that its stream does not cancel, ends only when the
    /// stream's owner closes it, for example by ending the process. Handlers of the peer's requests are cancelled, not
    /// awaited.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        End(null);
        await Task.WhenAll(_reading, _writing).ConfigureAwait(false);
        _stop.Dispose();
    }

    /// <summary>Ends the connection once: fails the requests, cancels the peer's requests and stops both loops.</summary>
    private void End(Exception? reason)
    {
        List<Request> requests;
        List<CancellationTokenSource> peerRequests;
        lock (_lock)
        {
            if (_isEnded)
                return;
            _isEnded = true;
            requests = [.. _requests.Values];
            _requests.Clear();
            peerRequests = [.. _peerRequests.Values];
        }
        _queue.Writer.TryComplete();
        _stop.Cancel();
        var ended = new IOException("The connection ended.", reason);
        foreach (Request request in requests)
            request.Response.TrySetException(ended);
        foreach (CancellationTokenSource peerRequest in peerRequests)
            peerRequest.Cancel();
        _ended.TrySetResult(reason);
    }

    private void Cancel(Request request, CancellationToken cancellationToken)
    {
        bool isWritten;
        lock (_lock)
        {
            if (!_requests.Remove(request.Id))
                return;
            request.IsCancelled = true;
            isWritten = request.IsWritten;
        }
        request.Response.TrySetCanceled(cancellationToken);
        if (isWritten)
        {
            int id = request.Id;
            Notify(
                "$/cancelRequest",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", id);
                    writer.WriteEndObject();
                }
            );
        }
    }

    private async Task ReadAsync()
    {
        Exception? reason = null;
        PipeReader reader = PipeReader.Create(_input, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            while (await ReadMessageAsync(reader, _stop.Token).ConfigureAwait(false) is { } message)
                Dispatch(message);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        // The loop's end: whatever stopped it ends the connection, and the owner learns why.
        catch (Exception e)
        {
            reason = e;
        }
        await reader.CompleteAsync().ConfigureAwait(false);
        End(reason);
    }

    /// <summary>Reads one message's body.</summary>
    /// <returns>The body, or null when the input ended between messages.</returns>
    /// <exception cref="InvalidDataException">The framing is broken, or the message is over the size limit.</exception>
    private async Task<byte[]?> ReadMessageAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        int length;
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (TryReadHeader(buffer, out SequencePosition bodyStart, out length))
            {
                reader.AdvanceTo(bodyStart);
                break;
            }
            if (buffer.Length > MaxHeaderLength)
                throw new InvalidDataException($"A message header is longer than {MaxHeaderLength} bytes.");
            if (result.IsCompleted)
            {
                if (buffer.IsEmpty)
                    return null;
                throw new InvalidDataException("The input ended inside a message header.");
            }
            reader.AdvanceTo(buffer.Start, buffer.End);
        }

        if (length > _maxMessageLength)
        {
            throw new InvalidDataException(
                $"A message of {length} bytes is over the limit of {_maxMessageLength} bytes."
            );
        }
        ReadResult body = await reader.ReadAtLeastAsync(length, cancellationToken).ConfigureAwait(false);
        if (body.Buffer.Length < length)
            throw new InvalidDataException("The input ended inside a message.");
        byte[] message = body.Buffer.Slice(0, length).ToArray();
        reader.AdvanceTo(body.Buffer.GetPosition(length));
        return message;
    }

    /// <summary>Reads a header up to its empty line, and the length of the body that follows.</summary>
    /// <returns>False when the header is not complete yet.</returns>
    /// <exception cref="InvalidDataException">The header has no valid <c>Content-Length</c>.</exception>
    private static bool TryReadHeader(ReadOnlySequence<byte> buffer, out SequencePosition bodyStart, out int length)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> header, "\r\n\r\n"u8))
        {
            (bodyStart, length) = (default, 0);
            return false;
        }
        if (header.Length > MaxHeaderLength)
            throw new InvalidDataException($"A message header is longer than {MaxHeaderLength} bytes.");
        bodyStart = reader.Position;
        // Other fields, such as Content-Type, don't matter: the body is UTF-8 JSON.
        foreach (string field in Encoding.ASCII.GetString(header).Split("\r\n"))
        {
            int colon = field.IndexOf(':');
            if (
                colon > 0
                && field.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(
                    field.AsSpan(colon + 1).Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out length
                )
            )
            {
                return true;
            }
        }
        throw new InvalidDataException("A message header has no valid Content-Length.");
    }

    /// <summary>Routes a message: responses and cancellations at once, notifications to their handler, requests to theirs.</summary>
    private void Dispatch(byte[] body)
    {
        JsonElement message;
        try
        {
            message = JsonElement.Parse(body);
        }
        catch (JsonException e)
        {
            Answer(new Response(null, JsonRpcErrorCodes.ParseError, $"The message is not valid JSON: {e.Message}"));
            return;
        }
        if (message.ValueKind != JsonValueKind.Object)
        {
            Answer(new Response(null, JsonRpcErrorCodes.InvalidRequest, "A message must be a JSON object."));
            return;
        }

        JsonElement? id =
            message.TryGetProperty("id", out JsonElement value)
            && value.ValueKind is JsonValueKind.Number or JsonValueKind.String
                ? value
                : null;
        if (!message.TryGetProperty("method", out JsonElement methodElement))
        {
            // A response, or an error response to something this end sent that the peer could not read.
            if (id is { } responseId)
                Complete(responseId, message);
            else if (!message.TryGetProperty("error", out _))
                Answer(new Response(null, JsonRpcErrorCodes.InvalidRequest, "A message needs a method or an id."));
            return;
        }

        string? method = null;
        try
        {
            method = methodElement.ValueKind == JsonValueKind.String ? methodElement.GetString() : null;
        }
        // An escaped lone surrogate does not decode.
        catch (InvalidOperationException) { }
        if (method is null)
        {
            Answer(new Response(id, JsonRpcErrorCodes.InvalidRequest, "A method must be a string."));
            return;
        }
        JsonElement parameters = message.TryGetProperty("params", out JsonElement p) ? p : default;
        if (id is { } requestId)
            StartRequest(requestId, method, parameters);
        else
            HandleNotification(method, parameters);
    }

    private void Complete(JsonElement id, JsonElement message)
    {
        Request? request;
        lock (_lock)
        {
            // Ids are this end's numbers; another id answers nothing this end waits for, such as a cancelled request.
            if (!id.TryGetInt32(out int number) || !_requests.Remove(number, out request))
                return;
        }
        if (message.TryGetProperty("error", out JsonElement error))
        {
            int code =
                error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out JsonElement c)
                && c.TryGetInt32(out int n)
                    ? n
                    : JsonRpcErrorCodes.InternalError;
            string text =
                error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out JsonElement m)
                && m.ValueKind == JsonValueKind.String
                    ? m.ToString()
                    : "The request failed.";
            request.Response.TrySetException(new JsonRpcException(code, text));
        }
        else
        {
            request.Response.TrySetResult(message.TryGetProperty("result", out JsonElement result) ? result : default);
        }
    }

    private void HandleNotification(string method, JsonElement parameters)
    {
        if (method == "$/cancelRequest")
        {
            CancellationTokenSource? peerRequest = null;
            if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("id", out JsonElement id))
            {
                lock (_lock)
                    _peerRequests.TryGetValue(id.GetRawText(), out peerRequest);
            }
            peerRequest?.Cancel();
            return;
        }
        try
        {
            _handleNotification(method, parameters);
        }
        // Invalid params: a notification gets no answer.
        catch (JsonException) { }
    }

    private void StartRequest(JsonElement id, string method, JsonElement parameters)
    {
        if (!CountUnanswered())
            return;
        string key = id.GetRawText();
        var cancellation = new CancellationTokenSource();
        lock (_lock)
            _peerRequests[key] = cancellation;
        // On the thread pool: a handler that blocks, such as the app's settings, does not stop reading.
        _ = Task.Run(() => RunRequestAsync(id, key, method, parameters, cancellation));
    }

    /// <summary>Queues the answer to a message that cannot be handled.</summary>
    private void Answer(Response response)
    {
        if (CountUnanswered())
            Send(response);
    }

    /// <summary>
    /// Counts a message of the peer as unanswered until its answer is written. A peer with too many sends faster than
    /// it reads: the connection ends.
    /// </summary>
    /// <returns>False when the connection ended.</returns>
    private bool CountUnanswered()
    {
        lock (_lock)
        {
            if (_isEnded)
                return false;
            if (++_unansweredRequests <= _maxUnansweredRequests)
                return true;
        }
        End(
            new IOException(
                $"The peer sent more than {_maxUnansweredRequests} messages that wait for an answer without reading the answers."
            )
        );
        return false;
    }

    /// <summary>Runs a handler of the peer's request and queues its response. Never fails.</summary>
    private async Task RunRequestAsync(
        JsonElement id,
        string key,
        string method,
        JsonElement parameters,
        CancellationTokenSource cancellation
    )
    {
        Response response;
        try
        {
            JsonNode? result = await _handleRequest(method, parameters, cancellation.Token).ConfigureAwait(false);
            response = cancellation.IsCancellationRequested
                ? new Response(id, JsonRpcErrorCodes.RequestCancelled, "The request was cancelled.")
                : new Response(id, result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            response = new Response(id, JsonRpcErrorCodes.RequestCancelled, "The request was cancelled.");
        }
        catch (JsonRpcException e)
        {
            response = new Response(id, e.Code, e.Message);
        }
        catch (JsonException e)
        {
            response = new Response(id, JsonRpcErrorCodes.InvalidParams, e.Message);
        }
        // The handler's failure becomes the peer's error; nothing else learns of it.
        catch (Exception e)
        {
            response = new Response(id, JsonRpcErrorCodes.InternalError, e.Message);
        }
        lock (_lock)
            _peerRequests.Remove(key);
        Send(response);
    }

    private async Task WriteAsync()
    {
        Exception? reason = null;
        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            await foreach (OutgoingMessage message in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                buffer.ResetWrittenCount();
                if (TryBuild(message, buffer))
                {
                    await WriteFrameAsync(buffer.WrittenMemory, _stop.Token).ConfigureAwait(false);
                    message.Written();
                }
                if (message is Response)
                {
                    lock (_lock)
                        _unansweredRequests--;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        // A failed write may have left part of a frame on the stream: nothing after it would be read right.
        catch (Exception e)
        {
            reason = e;
        }
        End(reason);
        while (_queue.Reader.TryRead(out OutgoingMessage? message))
            message.Fail(new IOException("The connection ended before the message was written."));
    }

    /// <summary>Builds a message into <paramref name="buffer"/>.</summary>
    /// <returns>False when there is nothing to write: the message wrote nothing, or failed alone.</returns>
    private static bool TryBuild(OutgoingMessage message, ArrayBufferWriter<byte> buffer)
    {
        try
        {
            using var writer = new Utf8JsonWriter(buffer, s_writerOptions);
            if (!message.Write(writer))
                return false;
            writer.Flush();
            return true;
        }
        // Only this message fails; nothing of it reached the stream.
        catch (Exception e)
        {
            message.Fail(e);
            return false;
        }
    }

    private async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await _output.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class Request : OutgoingMessage
    {
        private readonly JsonRpcConnection _connection;
        private readonly string _method;
        private readonly Action<Utf8JsonWriter>? _writeParams;

        public Request(JsonRpcConnection connection, string method, Action<Utf8JsonWriter>? writeParams)
        {
            _connection = connection;
            _method = method;
            _writeParams = writeParams;
        }

        /// <summary>Set before the request is queued.</summary>
        public int Id { get; set; }

        public TaskCompletionSource<JsonElement> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Guarded by the connection's lock: the write loop took it, so a cancellation must follow it.</summary>
        public bool IsWritten { get; set; }

        /// <summary>Guarded by the connection's lock: its caller cancelled it before it was written.</summary>
        public bool IsCancelled { get; set; }

        public override bool Write(Utf8JsonWriter writer)
        {
            lock (_connection._lock)
            {
                if (IsCancelled)
                    return false;
                IsWritten = true;
            }
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", Id);
            writer.WriteString("method", _method);
            WriteParams(writer, _writeParams);
            writer.WriteEndObject();
            return true;
        }

        public override void Fail(Exception exception)
        {
            lock (_connection._lock)
                _connection._requests.Remove(Id);
            Response.TrySetException(exception);
        }
    }

    private sealed class Notification : OutgoingMessage
    {
        private readonly string _method;
        private readonly Action<Utf8JsonWriter>? _writeParams;

        public Notification(string method, Action<Utf8JsonWriter>? writeParams)
        {
            _method = method;
            _writeParams = writeParams;
        }

        public override bool Write(Utf8JsonWriter writer)
        {
            WriteNotification(writer, _method, _writeParams);
            return true;
        }

        public override void Fail(Exception exception) { }
    }

    /// <summary>The answer to one of the peer's requests, or to a message that could not be read (with a null id).</summary>
    private sealed class Response : OutgoingMessage
    {
        private readonly JsonElement? _id;
        private readonly JsonNode? _result;
        private readonly (int Code, string Message)? _error;

        public Response(JsonElement? id, JsonNode? result)
        {
            _id = id;
            _result = result;
        }

        public Response(JsonElement? id, int code, string message)
        {
            _id = id;
            _error = (code, message);
        }

        public override bool Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (_id is { } id)
                id.WriteTo(writer);
            else
                writer.WriteNullValue();
            if (_error is var (code, message))
            {
                writer.WriteStartObject("error");
                writer.WriteNumber("code", code);
                writer.WriteString("message", message);
                writer.WriteEndObject();
            }
            else
            {
                writer.WritePropertyName("result");
                if (_result is null)
                    writer.WriteNullValue();
                else
                    _result.WriteTo(writer);
            }
            writer.WriteEndObject();
            return true;
        }

        /// <summary>The peer learns nothing when the connection ended first.</summary>
        public override void Fail(Exception exception) { }
    }
}

/// <summary>A message that waits in a connection's write queue, and is built when its turn comes.</summary>
internal abstract class OutgoingMessage
{
    /// <summary>Writes the message, or nothing when it is no longer needed. Called on the write loop, once per send.</summary>
    /// <returns>Whether it wrote a message.</returns>
    public abstract bool Write(Utf8JsonWriter writer);

    /// <summary>The message was not written: <see cref="Write"/> threw, or the connection ended first.</summary>
    public abstract void Fail(Exception exception);

    /// <summary>The message is on the stream, after <see cref="Write"/> wrote it. Called on the write loop.</summary>
    public virtual void Written() { }

    /// <summary>Writes a notification: its method and, when there are any, its params.</summary>
    protected static void WriteNotification(Utf8JsonWriter writer, string method, Action<Utf8JsonWriter>? writeParams)
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WriteString("method", method);
        WriteParams(writer, writeParams);
        writer.WriteEndObject();
    }

    private protected static void WriteParams(Utf8JsonWriter writer, Action<Utf8JsonWriter>? writeParams)
    {
        if (writeParams is null)
            return;
        writer.WritePropertyName("params");
        writeParams(writer);
    }
}

/// <summary>An error response: the peer's, from a request, or this end's, from a handler.</summary>
internal sealed class JsonRpcException(int code, string message) : Exception(message)
{
    /// <summary>The error's code, such as <see cref="JsonRpcErrorCodes.MethodNotFound"/>.</summary>
    public int Code { get; } = code;
}

/// <summary>The error codes of JSON-RPC and of the Language Server Protocol that the library uses.</summary>
internal static class JsonRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int RequestCancelled = -32800;
}

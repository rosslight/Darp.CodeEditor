using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using System.Threading.Channels;
using Darp.CodeEditor.Lsp.Protocol;

namespace Darp.CodeEditor.Lsp;

/// <summary>
/// One end of a JSON-RPC 2.0 connection over a stream pair with LSP's <c>Content-Length</c> framing. It sends requests
/// and notifications, and dispatches the peer's requests and notifications to handlers.
/// </summary>
/// <remarks>
/// <para>
/// One loop reads the input. It completes responses and applies <c>$/cancelRequest</c> as they arrive, and creates the
/// cancellation token of each request on receipt, so a request can be cancelled while it is still queued. Requests and
/// notifications are dispatched one at a time in arrival order, as their <see cref="DispatchMode"/> says.
/// </para>
/// <para>
/// One loop writes the output, from construction until disposal. Sending only queues a message: the write loop
/// serializes and writes the messages in the order they were sent, so callers never wait for serialization or a slow
/// peer. Params are serialized when the message is written, so they must not change after they were sent.
/// </para>
/// <para>
/// A request answers with <see cref="ErrorCodes.RequestCancelled"/> when its token was cancelled by the time its handler
/// returned, or when the handler throws <see cref="OperationCanceledException"/>. A handler throws
/// <see cref="JsonRpcException"/> to answer with another error, such as <see cref="ErrorCodes.ContentModified"/> for a
/// stale request. Other exceptions answer with <see cref="ErrorCodes.InternalError"/>.
/// </para>
/// <para>
/// Methods without params or result use <see cref="JsonElement"/>: <c>default</c> leaves params out and answers null.
/// The connection does not own the streams.
/// </para>
/// </remarks>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    /// <summary>The output is no HTML page; relaxed escaping keeps non-ASCII text readable and short.</summary>
    private static readonly JsonWriterOptions s_writerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly MessageReader _reader;
    private readonly Stream _output;
    private readonly Action<string> _log;
    private readonly Dictionary<string, Registration<IncomingRequest>> _requestHandlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Registration<CancellationToken>> _notificationHandlers = new(
        StringComparer.Ordinal
    );
    private readonly ConcurrentDictionary<RequestId, IncomingRequest> _incoming = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _outgoing = new();
    private readonly Channel<Incoming> _queue = Channel.CreateUnbounded<Incoming>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
    );
    private readonly Channel<OutgoingMessage> _sends = Channel.CreateUnbounded<OutgoingMessage>(
        new UnboundedChannelOptions { SingleReader = true }
    );
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _disposed = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<Task> _running = [];
    private readonly Task _writeLoop;
    private Task? _loops;
    private long _lastId;

    /// <param name="log">Receives protocol errors and handler failures; the default drops them.</param>
    /// <param name="limits">How much the peer may send; <see cref="JsonRpcLimits"/>' defaults when null.</param>
    public JsonRpcConnection(Stream input, Stream output, Action<string>? log = null, JsonRpcLimits? limits = null)
    {
        _log = log ?? (static _ => { });
        _reader = new MessageReader(input, limits ?? new JsonRpcLimits(), _log);
        _output = output;
        _writeLoop = Task.Run(WriteLoopAsync);
    }

    /// <summary>Completes when the connection ended: the input ended, it was disposed, or a server received <c>exit</c>.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Decides in dispatch order whether a request or notification of a method is admitted.</summary>
    /// <remarks>A rejected request is answered with the returned error; a rejected notification is dropped.</remarks>
    internal Func<string, JsonRpcException?>? Filter { get; init; }

    /// <summary>Handles requests of <paramref name="type"/>. Handlers are registered before <see cref="Start"/>.</summary>
    /// <remarks>
    /// A <see cref="DispatchMode.Concurrent"/> handler must read the state it works on, such as the document text,
    /// before its first incomplete <c>await</c>: later messages, such as <c>textDocument/didChange</c>, are dispatched
    /// from then on. A <see cref="DispatchMode.Serial"/> handler holds up all later messages, so it must not await a
    /// request to the peer that the peer can only answer after this connection dispatched a request of the peer.
    /// </remarks>
    public void OnRequest<TParams, TResult>(
        RequestType<TParams, TResult> type,
        Func<TParams, CancellationToken, Task<TResult?>> handler,
        DispatchMode mode = DispatchMode.Concurrent
    ) => OnRequest(type, handler, mode, answered: null);

    /// <param name="answered">
    /// Runs when the handler's result became the answer, before the next message is dispatched for a
    /// <see cref="DispatchMode.Serial"/> handler.
    /// </param>
    internal void OnRequest<TParams, TResult>(
        RequestType<TParams, TResult> type,
        Func<TParams, CancellationToken, Task<TResult?>> handler,
        DispatchMode mode,
        Action? answered
    ) =>
        Register(
            _requestHandlers,
            type.Method,
            mode,
            async (request, parameters) =>
            {
                ReadOnlyMemory<byte> response;
                try
                {
                    TResult? result = await handler(ReadParams(parameters, type.Params), request.Cancellation.Token)
                        .ConfigureAwait(false);
                    if (Finish(request))
                    {
                        response = Build(request.Id, null, "result", result, type.Result);
                        answered?.Invoke();
                    }
                    else
                        response = Cancelled(request.Id);
                }
                catch (Exception exception)
                {
                    response = Finish(request) ? Error(request.Id, ToError(request, exception)) : Cancelled(request.Id);
                }
                Send(response);
            }
        );

    /// <summary>Handles notifications of <paramref name="type"/>. Handlers are registered before <see cref="Start"/>.</summary>
    /// <param name="handler">Receives a token that is cancelled when the connection ends.</param>
    /// <remarks>
    /// A <see cref="DispatchMode.Serial"/> handler holds up all later messages, so it must not await a request to the
    /// peer that the peer can only answer after this connection dispatched a request of the peer.
    /// </remarks>
    public void OnNotification<TParams>(
        NotificationType<TParams> type,
        Func<TParams, CancellationToken, Task> handler,
        DispatchMode mode = DispatchMode.Serial
    ) =>
        Register(
            _notificationHandlers,
            type.Method,
            mode,
            async (cancellationToken, parameters) =>
            {
                try
                {
                    await handler(ReadParams(parameters, type.Params), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    _log($"Handling {type.Method} failed: {exception}");
                }
            }
        );

    /// <summary>Starts reading and dispatching messages.</summary>
    public void Start()
    {
        ThrowIfStarted();
        _loops = Task.WhenAll(Task.Run(ReadLoopAsync), Task.Run(DispatchLoopAsync));
    }

    /// <summary>Sends a request and returns its result.</summary>
    /// <remarks>
    /// Cancelling <paramref name="cancellationToken"/> ends the call right away, even while the output is blocked. A
    /// request that was not written yet is then left out; otherwise <c>$/cancelRequest</c> follows it, and the late
    /// response is ignored.
    /// </remarks>
    /// <exception cref="JsonRpcException">The peer answered with an error.</exception>
    /// <exception cref="IOException">The connection ended before the response arrived.</exception>
    public async Task<TResult?> SendRequestAsync<TParams, TResult>(
        RequestType<TParams, TResult> type,
        TParams parameters,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        long id = Interlocked.Increment(ref _lastId);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _outgoing[id] = response;
        try
        {
            var request = new OutgoingMessage(() =>
                Build(new RequestId(id), type.Method, "params", parameters, type.Params)
            )
            {
                RequestId = id,
                Abandoned = cancellationToken,
            };
            // Stop refuses messages before it fails the requests it finds, so a request is either refused or failed.
            if (!_sends.Writer.TryWrite(request))
                throw Closed();
            JsonElement result;
            using (cancellationToken.UnsafeRegister(_ => CancelOutgoing(id, request, cancellationToken), null))
                result = await response.Task.ConfigureAwait(false);
            return result.Deserialize(type.Result);
        }
        finally
        {
            _outgoing.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a notification.</summary>
    /// <returns>A task that completes when the notification was written.</returns>
    /// <exception cref="IOException">The connection ended before the notification was written.</exception>
    public Task SendNotificationAsync<TParams>(NotificationType<TParams> type, TParams parameters)
    {
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notification = new OutgoingMessage(() => Build(null, type.Method, "params", parameters, type.Params))
        {
            Written = written,
        };
        return _sends.Writer.TryWrite(notification) ? written.Task : Task.FromException(Closed());
    }

    /// <summary>Ends the connection and waits until running handlers returned.</summary>
    /// <remarks>Messages that were not written yet are dropped; await a notification's task to make sure it went out.</remarks>
    public async ValueTask DisposeAsync()
    {
        Stop();
        _disposed.Cancel();
        if (_loops is not null)
            await _loops.ConfigureAwait(false);
        await _writeLoop.ConfigureAwait(false);
        Task[] running;
        lock (_running)
            running = [.. _running];
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops reading and dispatching, cancels incoming requests and fails outgoing ones. Messages that were already
    /// sent are still written, until the connection is disposed.
    /// </summary>
    internal void Stop()
    {
        if (_stop.IsCancellationRequested)
            return;
        _stop.Cancel();
        _queue.Writer.TryComplete();
        _sends.Writer.TryComplete();
        foreach (IncomingRequest request in _incoming.Values)
            Observe(request.Cancellation.CancelAsync(), "Cancelling a request");
        foreach (long id in _outgoing.Keys)
        {
            if (_outgoing.TryRemove(id, out TaskCompletionSource<JsonElement>? response))
                response.TrySetException(Closed());
        }
        _completion.TrySetResult();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _reader.ReadAsync(_stop.Token).ConfigureAwait(false) is { } body)
            {
                try
                {
                    ReadOnlyMemory<byte> error = Receive(body.Span);
                    if (!error.IsEmpty)
                        Send(error);
                }
                catch (Exception exception)
                {
                    _log($"Handling a message failed: {exception}");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _log($"Reading messages failed: {exception}");
        }
        finally
        {
            Stop();
        }
    }

    /// <summary>Routes a message: responses and cancellations right away, requests and notifications to the queue.</summary>
    /// <returns>The error response to an invalid message, otherwise nothing.</returns>
    private ReadOnlyMemory<byte> Receive(ReadOnlySpan<byte> body)
    {
        JsonElement message;
        string? name;
        bool hasId;
        RequestId? id;
        try
        {
            // JSON text is UTF-8. The reader checks the structure, but the encoding only of the strings it decodes.
            if (!Utf8.IsValid(body))
                throw new JsonException("The message is not valid UTF-8.");
            var reader = new Utf8JsonReader(body);
            // Copies the message, so that it stays valid when the read buffer is reused.
            message = JsonElement.ParseValue(ref reader);
            if (reader.Read())
                throw new JsonException("The message continues after its JSON value.");
            if (message.ValueKind != JsonValueKind.Object)
            {
                var invalid = new JsonRpcException(ErrorCodes.InvalidRequest, "A message must be a JSON object.");
                return Error(null, invalid);
            }
            name =
                message.TryGetProperty("method", out JsonElement method) && method.ValueKind == JsonValueKind.String
                    ? method.GetString()
                    : null;
            hasId = message.TryGetProperty("id", out JsonElement idElement);
            id = RequestId.TryRead(idElement, out RequestId value) ? value : null;
        }
        // Strings with unpaired surrogate escapes fail to decode with InvalidOperationException.
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            _log($"Received a message that is not JSON: {exception.Message}");
            return Error(null, new JsonRpcException(ErrorCodes.ParseError, "The message is not valid JSON."));
        }
        if (name is null)
            return ReceiveResponse(id, message);
        JsonElement? parameters = message.TryGetProperty("params", out JsonElement element) ? element : null;
        if (!hasId)
        {
            if (name == "$/cancelRequest")
                CancelIncoming(parameters);
            else
                _queue.Writer.TryWrite(new Incoming(name, parameters, null));
            return default;
        }
        if (id is not { } requestId)
        {
            return Error(
                null,
                new JsonRpcException(ErrorCodes.InvalidRequest, "A request id must be a number or string.")
            );
        }
        var request = new IncomingRequest(requestId, name);
        if (!_incoming.TryAdd(requestId, request))
        {
            return Error(
                requestId,
                new JsonRpcException(ErrorCodes.InvalidRequest, $"Request {requestId} is running.")
            );
        }
        _queue.Writer.TryWrite(new Incoming(name, parameters, request));
        return default;
    }

    private ReadOnlyMemory<byte> ReceiveResponse(RequestId? id, JsonElement message)
    {
        bool failed = message.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object;
        if (!failed && !message.TryGetProperty("result", out _))
        {
            var invalid = new JsonRpcException(
                ErrorCodes.InvalidRequest,
                "A message needs a method, a result or an error."
            );
            return Error(id, invalid);
        }
        if (id is null)
            _log($"Received a response without a request id: {message.GetRawText()}");
        // An unknown id belongs to a request whose caller cancelled it.
        else if (
            id.Value.Text is null
            && _outgoing.TryRemove(id.Value.Number, out TaskCompletionSource<JsonElement>? call)
        )
        {
            if (failed)
                call.TrySetException(ReadError(error));
            else
                call.TrySetResult(message.GetProperty("result"));
        }
        return default;
    }

    private static JsonRpcException ReadError(JsonElement error)
    {
        try
        {
            ResponseError value =
                error.Deserialize(InternalJsonContext.Default.ResponseError) ?? throw new JsonException();
            return new JsonRpcException(value.Code, value.Message, value.Data);
        }
        catch (JsonException)
        {
            return new JsonRpcException(ErrorCodes.InternalError, error.GetRawText());
        }
    }

    private void CancelIncoming(JsonElement? parameters)
    {
        if (
            parameters is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty("id", out JsonElement idElement)
            && RequestId.TryRead(idElement, out RequestId id)
            && _incoming.TryGetValue(id, out IncomingRequest? request)
        )
        {
            // Asynchronously, so that continuations of the handler do not run on the read loop.
            Observe(request.Cancellation.CancelAsync(), "Cancelling a request");
        }
    }

    /// <summary>Ends a call whose caller cancelled it, and queues its <c>$/cancelRequest</c> behind the request.</summary>
    private void CancelOutgoing(long id, OutgoingMessage request, CancellationToken cancellationToken)
    {
        if (!_outgoing.TryRemove(id, out TaskCompletionSource<JsonElement>? response))
            return;
        response.TrySetCanceled(cancellationToken);
        _sends.Writer.TryWrite(
            new OutgoingMessage(() =>
                Build(null, LspMethods.CancelRequest.Method, "params", new CancelParams(new RequestId(id)), Cancel)
            )
            {
                Cancels = request,
            }
        );
    }

    private static JsonTypeInfo<CancelParams> Cancel => LspJsonContext.Default.CancelParams;

    private async Task DispatchLoopAsync()
    {
        try
        {
            await foreach (Incoming message in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                (Task handled, DispatchMode mode) = Dispatch(message);
                if (mode == DispatchMode.Serial)
                    await handled.ConfigureAwait(false);
                else
                    Track(handled);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    /// <returns>A task that never faults, and whether the next message waits for it.</returns>
    private (Task Handled, DispatchMode Mode) Dispatch(Incoming message)
    {
        JsonRpcException? rejection = Filter?.Invoke(message.Method);
        if (message.Request is not { } request)
        {
            if (rejection is not null)
                _log($"Dropped {message.Method}: {rejection.Message}");
            else if (_notificationHandlers.TryGetValue(message.Method, out Registration<CancellationToken> handler))
                return (handler.Handle(_stop.Token, message.Params), handler.Mode);
            else if (!message.Method.StartsWith("$/", StringComparison.Ordinal))
                _log($"No handler for the notification {message.Method}.");
            return (Task.CompletedTask, DispatchMode.Serial);
        }
        if (
            rejection is null
            && _requestHandlers.TryGetValue(message.Method, out Registration<IncomingRequest> registration)
        )
        {
            // A request cancelled while it was queued does not run.
            if (!request.Cancellation.IsCancellationRequested)
                return (registration.Handle(request, message.Params), registration.Mode);
        }
        rejection ??= new JsonRpcException(ErrorCodes.MethodNotFound, $"Unknown method {message.Method}.");
        Send(Finish(request) ? Error(request.Id, rejection) : Cancelled(request.Id));
        return (Task.CompletedTask, DispatchMode.Serial);
    }

    private void Register<TContext>(
        Dictionary<string, Registration<TContext>> handlers,
        string method,
        DispatchMode mode,
        Func<TContext, JsonElement?, Task> handle
    )
    {
        ThrowIfStarted();
        handlers.Add(method, new Registration<TContext>(handle, mode));
    }

    private void Track(Task task)
    {
        if (task.IsCompleted)
            return;
        lock (_running)
            _running.Add(task);
        task.ContinueWith(
            completed =>
            {
                lock (_running)
                    _running.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private static T ReadParams<T>(JsonElement? parameters, JsonTypeInfo<T> typeInfo)
    {
        T? value;
        try
        {
            value = parameters is { } element ? element.Deserialize(typeInfo) : default;
        }
        catch (JsonException exception)
        {
            throw new JsonRpcException(ErrorCodes.InvalidParams, exception.Message);
        }
        return value is T present
            ? present
            : throw new JsonRpcException(ErrorCodes.InvalidParams, "Params are missing.");
    }

    /// <summary>Ends a request that is being answered.</summary>
    /// <returns>
    /// False when the request was cancelled; it then answers <see cref="ErrorCodes.RequestCancelled"/>. Nothing
    /// cancels the request after this returned true.
    /// </returns>
    private bool Finish(IncomingRequest request)
    {
        _incoming.TryRemove(request.Id, out _);
        return !request.Cancellation.IsCancellationRequested;
    }

    private static ReadOnlyMemory<byte> Cancelled(RequestId id) =>
        Error(id, new JsonRpcException(ErrorCodes.RequestCancelled, "The request was cancelled."));

    private JsonRpcException ToError(IncomingRequest request, Exception exception)
    {
        switch (exception)
        {
            case JsonRpcException error:
                return error;
            case OperationCanceledException:
                return new JsonRpcException(ErrorCodes.RequestCancelled, "The request was cancelled.");
            default:
                _log($"Handling {request.Method} failed: {exception}");
                return new JsonRpcException(ErrorCodes.InternalError, exception.Message);
        }
    }

    private static ReadOnlyMemory<byte> Error(RequestId? id, JsonRpcException error) =>
        Build(
            id,
            null,
            "error",
            new ResponseError(error.Code, error.Message, error.ErrorData),
            InternalJsonContext.Default.ResponseError
        );

    /// <summary>Builds a request (id and method), a notification (method) or a response (id or null, no method).</summary>
    /// <param name="member">Params, which are left out when there are none; or the result or error of a response.</param>
    private static ReadOnlyMemory<byte> Build<T>(
        RequestId? id,
        string? method,
        string member,
        T? value,
        JsonTypeInfo<T> typeInfo
    )
    {
        var body = new ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(body, s_writerOptions))
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            if (id is { } requestId)
            {
                json.WritePropertyName("id");
                requestId.WriteTo(json);
            }
            else if (method is null)
                json.WriteNull("id");
            if (method is not null)
                json.WriteString("method", method);
            // A default JSON element stands for no value.
            if (value is not (null or JsonElement { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null }))
            {
                json.WritePropertyName(member);
                JsonSerializer.Serialize(json, value, typeInfo);
            }
            else if (method is null)
                json.WriteNull(member);
            json.WriteEndObject();
        }
        return body.WrittenMemory;
    }

    /// <summary>Queues a response that was already built; it is dropped once the connection stopped.</summary>
    private void Send(ReadOnlyMemory<byte> response) => _sends.Writer.TryWrite(new OutgoingMessage(() => response));

    /// <summary>Builds and writes the queued messages in order, until the connection stopped and they are written.</summary>
    private async Task WriteLoopAsync()
    {
        await foreach (OutgoingMessage message in _sends.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // A request that its caller cancelled before it went out is left out, and then its $/cancelRequest too.
            if (message.Abandoned.IsCancellationRequested || message.Cancels is { WasWritten: false })
                continue;
            try
            {
                // After dispose, the remaining messages fail without being built or touching the stream.
                _disposed.Token.ThrowIfCancellationRequested();
                await WriteFrameAsync(message.Build()).ConfigureAwait(false);
                message.WasWritten = true;
                message.Written?.TrySetResult();
            }
            catch (Exception exception)
            {
                Exception failure = _disposed.IsCancellationRequested ? Closed() : exception;
                message.Written?.TrySetException(failure);
                if (message.RequestId is { } id && _outgoing.TryRemove(id, out TaskCompletionSource<JsonElement>? call))
                    call.TrySetException(failure);
                if (!_disposed.IsCancellationRequested)
                    _log($"Sending a message failed: {exception}");
            }
        }
    }

    /// <remarks>
    /// A write is never cancelled halfway, except on dispose: then a write that the stream does not cancel, such as to a
    /// console stream, is abandoned.
    /// </remarks>
    private async Task WriteFrameAsync(ReadOnlyMemory<byte> body)
    {
        byte[] header = Encoding.ASCII.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n\r\n")
        );
        CancellationToken disposed = _disposed.Token;
        await _output.WriteAsync(header, disposed).AsTask().WaitAsync(disposed).ConfigureAwait(false);
        await _output.WriteAsync(body, disposed).AsTask().WaitAsync(disposed).ConfigureAwait(false);
        await _output.FlushAsync(disposed).WaitAsync(disposed).ConfigureAwait(false);
    }

    /// <summary>Logs failures of work that nobody awaits.</summary>
    private void Observe(Task task, string what) =>
        task.ContinueWith(
            failed => _log($"{what} failed: {failed.Exception?.GetBaseException()}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

    private void ThrowIfStarted()
    {
        if (_loops is not null)
            throw new InvalidOperationException("The connection was already started.");
    }

    private static IOException Closed() => new("The JSON-RPC connection is closed.");

    /// <param name="Handle">Deserializes the params and runs the handler; the returned task never faults.</param>
    private readonly record struct Registration<TContext>(Func<TContext, JsonElement?, Task> Handle, DispatchMode Mode);

    /// <param name="Request">Null for a notification.</param>
    private readonly record struct Incoming(string Method, JsonElement? Params, IncomingRequest? Request);

    private sealed class IncomingRequest
    {
        public IncomingRequest(RequestId id, string method)
        {
            Id = id;
            Method = method;
        }

        public RequestId Id { get; }

        public string Method { get; }

        /// <summary>Created when the request is read, so that a cancel for a queued request works.</summary>
        public CancellationTokenSource Cancellation { get; } = new();
    }

    /// <summary>A message for the write loop.</summary>
    private sealed class OutgoingMessage
    {
        public OutgoingMessage(Func<ReadOnlyMemory<byte>> build) => Build = build;

        /// <summary>Serializes the message, on the write loop.</summary>
        public Func<ReadOnlyMemory<byte>> Build { get; }

        /// <summary>For a notification: completes when it was written, and faults when it could not be.</summary>
        public TaskCompletionSource? Written { get; init; }

        /// <summary>For a request: its id, whose call fails when the request cannot be written.</summary>
        public long? RequestId { get; init; }

        /// <summary>For a request: its caller's token. A request that was cancelled before it was written is left out.</summary>
        public CancellationToken Abandoned { get; init; }

        /// <summary>For <c>$/cancelRequest</c>: the request it cancels. It is left out when that request was not written.</summary>
        public OutgoingMessage? Cancels { get; init; }

        /// <summary>Set by the write loop, which alone reads it.</summary>
        public bool WasWritten { get; set; }
    }
}

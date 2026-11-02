using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Darp.CodeEditor.LspTesting;

/// <summary>A small LSP client that drives a server process over stdio, for tests and benchmarks.</summary>
internal sealed class LspClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<LspResponse>> _pending = new();
    private readonly Channel<JsonObject> _notifications = Channel.CreateUnbounded<JsonObject>();
    private readonly StringBuilder _standardError = new();
    private readonly Task _reader;
    private int _lastId;

    private LspClient(Process process)
    {
        _process = process;
        _input = process.StandardInput.BaseStream;
        process.ErrorDataReceived += (_, e) =>
        {
            lock (_standardError)
                _standardError.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();
        _reader = Task.Run(ReadMessagesAsync);
    }

    public Process Process => _process;

    /// <summary><see cref="Stopwatch.GetTimestamp"/> right before the process started.</summary>
    public long StartTimestamp { get; private init; }

    public string StandardError
    {
        get
        {
            lock (_standardError)
                return _standardError.ToString();
        }
    }

    /// <summary>Starts <c>dotnet &lt;serverDll&gt; &lt;arguments&gt;</c>.</summary>
    public static LspClient Start(string serverDll, params IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(serverDll);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        long start = Stopwatch.GetTimestamp();
        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("The server did not start.");
        return new LspClient(process) { StartTimestamp = start };
    }

    /// <summary>Sends a request; <see cref="PendingRequest.Response"/> completes with the response.</summary>
    public PendingRequest Send(string method, JsonNode? parameters)
    {
        int id = Interlocked.Increment(ref _lastId);
        var response = new TaskCompletionSource<LspResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = response;
        _ = WriteAsync(
            new JsonObject
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            }
        );
        return new PendingRequest(id, response.Task);
    }

    /// <summary>Sends a request and returns its result.</summary>
    /// <exception cref="LspException">The server answered with an error.</exception>
    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, TimeSpan? timeout = null)
    {
        LspResponse response = await Send(method, parameters)
            .Response.WaitAsync(timeout ?? TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);
        return response.Error is { } error ? throw new LspException(error.Code, error.Message) : response.Result;
    }

    public Task NotifyAsync(string method, JsonNode? parameters) =>
        WriteAsync(new JsonObject { ["method"] = method, ["params"] = parameters });

    public Task CancelAsync(int id) => NotifyAsync("$/cancelRequest", new JsonObject { ["id"] = id });

    /// <summary>Waits for the next notification of <paramref name="method"/> that matches; skips the others.</summary>
    public async Task<JsonObject> WaitForNotificationAsync(
        string method,
        Func<JsonObject, bool>? predicate = null,
        TimeSpan? timeout = null
    )
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            JsonObject notification = await _notifications.Reader.ReadAsync(cancellation.Token).ConfigureAwait(false);
            if ((string?)notification["method"] == method && (predicate?.Invoke(notification) ?? true))
                return notification;
        }
    }

    /// <summary>Sends <c>shutdown</c> and <c>exit</c> and returns the exit code.</summary>
    public async Task<int> ShutdownAsync()
    {
        await RequestAsync("shutdown", null).ConfigureAwait(false);
        await NotifyAsync("exit", null).ConfigureAwait(false);
        return await WaitForExitAsync().ConfigureAwait(false);
    }

    public async Task<int> WaitForExitAsync()
    {
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
            _process.Kill();
        await _process.WaitForExitAsync().ConfigureAwait(false);
        await _reader.ConfigureAwait(false);
        _process.Dispose();
    }

    private async Task WriteAsync(JsonObject message)
    {
        message["jsonrpc"] = "2.0";
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message);
        byte[] header = Encoding.ASCII.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n\r\n")
        );
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _input.WriteAsync(header).ConfigureAwait(false);
            await _input.WriteAsync(body).ConfigureAwait(false);
            await _input.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadMessagesAsync()
    {
        var output = new BufferedStream(_process.StandardOutput.BaseStream, 1 << 16);
        try
        {
            while (await ReadMessageAsync(output).ConfigureAwait(false) is ({ } message, int length))
            {
                if (message["method"] is null && message["id"] is { } id)
                {
                    if (_pending.TryRemove(id.GetValue<int>(), out TaskCompletionSource<LspResponse>? response))
                        response.SetResult(ToResponse(message, length));
                }
                else if (message["id"] is not null)
                {
                    // A request from the server, such as workspace/configuration: answer with null.
                    await WriteAsync(new JsonObject { ["id"] = message["id"]?.DeepClone(), ["result"] = null })
                        .ConfigureAwait(false);
                }
                else
                {
                    await _notifications.Writer.WriteAsync(message).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            foreach (TaskCompletionSource<LspResponse> pending in _pending.Values)
                pending.TrySetException(new EndOfStreamException("The server closed its output."));
            _notifications.Writer.TryComplete();
        }
    }

    private static LspResponse ToResponse(JsonObject message, int length) =>
        message["error"] is JsonObject error
            ? new LspResponse(null, new LspError((int?)error["code"] ?? 0, (string?)error["message"] ?? ""), length)
            : new LspResponse(message["result"], null, length);

    private static async Task<(JsonObject Message, int Length)?> ReadMessageAsync(Stream stream)
    {
        int contentLength = -1;
        while (true)
        {
            string? line = await ReadHeaderLineAsync(stream).ConfigureAwait(false);
            if (line is null)
                return null;
            if (line.Length == 0)
                break;
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);
        }
        byte[] body = new byte[contentLength];
        await stream.ReadExactlyAsync(body).ConfigureAwait(false);
        JsonObject message =
            JsonNode.Parse(body)?.AsObject() ?? throw new InvalidDataException("The server sent a null message.");
        return (message, contentLength);
    }

    private static async Task<string?> ReadHeaderLineAsync(Stream stream)
    {
        var line = new StringBuilder();
        byte[] buffer = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(buffer).ConfigureAwait(false) == 0)
                return null;
            if (buffer[0] == '\n')
                return line.ToString().TrimEnd('\r');
            line.Append((char)buffer[0]);
        }
    }
}

/// <param name="Id">The request id, for <c>$/cancelRequest</c>.</param>
internal sealed record PendingRequest(int Id, Task<LspResponse> Response);

/// <param name="ContentLength">The size of the response message in bytes.</param>
internal sealed record LspResponse(JsonNode? Result, LspError? Error, int ContentLength);

internal sealed record LspError(int Code, string Message);

internal sealed class LspException(int code, string message) : Exception($"{message} ({code})")
{
    public int Code { get; } = code;
}

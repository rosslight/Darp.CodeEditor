using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Darp.CodeEditor.Lsp;

/// <summary>A request method with the JSON contracts of its parameters and its result.</summary>
public sealed record RequestType<TParams, TResult>(
    string Method,
    JsonTypeInfo<TParams> Params,
    JsonTypeInfo<TResult> Result
);

/// <summary>A notification method with the JSON contract of its parameters.</summary>
public sealed record NotificationType<TParams>(string Method, JsonTypeInfo<TParams> Params);

/// <summary>How a handler runs relative to the messages that arrive after it.</summary>
public enum DispatchMode
{
    /// <summary>
    /// Runs to completion before the next message is dispatched. For messages that change state, such as
    /// <c>textDocument/didChange</c>.
    /// </summary>
    Serial,

    /// <summary>
    /// Runs on the dispatch loop until its first incomplete <c>await</c>, then alongside later messages. It therefore
    /// sees the state that all earlier messages left behind.
    /// </summary>
    Concurrent,
}

/// <summary>The id of a JSON-RPC request: a number or a string.</summary>
[JsonConverter(typeof(RequestIdConverter))]
public readonly record struct RequestId
{
    public RequestId(long number) => Number = number;

    public RequestId(string text) => Text = text;

    /// <summary>The id, when <see cref="Text"/> is null.</summary>
    public long Number { get; }

    /// <summary>The id when it is a string; otherwise null.</summary>
    public string? Text { get; }

    public override string ToString() => Text ?? Number.ToString(CultureInfo.InvariantCulture);

    internal static bool TryRead(JsonElement element, out RequestId id)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long number))
            id = new RequestId(number);
        else if (element.ValueKind == JsonValueKind.String)
            id = new RequestId(element.GetString() ?? "");
        else
        {
            id = default;
            return false;
        }
        return true;
    }

    internal void WriteTo(Utf8JsonWriter writer)
    {
        if (Text is null)
            writer.WriteNumberValue(Number);
        else
            writer.WriteStringValue(Text);
    }
}

internal sealed class RequestIdConverter : JsonConverter<RequestId>
{
    public override RequestId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt64(out long number) => new RequestId(number),
            JsonTokenType.String => new RequestId(reader.GetString() ?? ""),
            _ => throw new JsonException("A request id must be an integer or a string."),
        };

    public override void Write(Utf8JsonWriter writer, RequestId value, JsonSerializerOptions options) =>
        value.WriteTo(writer);
}

/// <summary>Error codes of JSON-RPC and the Language Server Protocol.</summary>
public static class ErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>A request arrived before a successful <c>initialize</c>.</summary>
    public const int ServerNotInitialized = -32002;

    /// <summary>The request was cancelled.</summary>
    public const int RequestCancelled = -32800;

    /// <summary>The document changed, so the result would be stale. Clients drop such responses silently.</summary>
    public const int ContentModified = -32801;

    /// <summary>The request was valid but failed; the message says why.</summary>
    public const int RequestFailed = -32803;
}

/// <summary>
/// A JSON-RPC error. Handlers throw it to answer with that error; sent requests throw it when the peer answered with
/// an error.
/// </summary>
public sealed class JsonRpcException : Exception
{
    public JsonRpcException(int code, string message, JsonElement? errorData = null)
        : base(message)
    {
        Code = code;
        ErrorData = errorData;
    }

    /// <summary>One of <see cref="ErrorCodes"/>, or a code of the server.</summary>
    public int Code { get; }

    /// <summary>The error's <c>data</c> member, if any.</summary>
    public JsonElement? ErrorData { get; }
}

/// <summary>The error member of a JSON-RPC response.</summary>
internal sealed record ResponseError(int Code, string Message, JsonElement? Data = null);

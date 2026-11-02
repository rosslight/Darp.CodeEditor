using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace Darp.CodeEditor.Lsp.Tests;

/// <summary>The wire format: framing, malformed input and the shape of responses.</summary>
public sealed class FramingTests : IAsyncLifetime
{
    private readonly Wire _wire = new();
    private readonly List<string> _log = [];
    private readonly JsonRpcConnection _connection;

    public FramingTests()
    {
        _connection = new JsonRpcConnection(
            _wire.Input,
            _wire.Output,
            message =>
            {
                lock (_log)
                    _log.Add(message);
            }
        );
        _connection.OnRequest(TestMethods.Request("echo"), (p, _) => Task.FromResult<JsonElement>(p));
        _connection.OnRequest(
            new RequestType<JsonElement, JsonElement?>(
                "nothing",
                Protocol.LspJsonContext.Default.JsonElement,
                Protocol.LspJsonContext.Default.NullableJsonElement
            ),
            (_, _) => Task.FromResult<JsonElement?>(null)
        );
        _connection.OnRequest(
            new RequestType<Protocol.HoverParams, JsonElement>(
                "typed",
                Protocol.LspJsonContext.Default.HoverParams,
                Protocol.LspJsonContext.Default.JsonElement
            ),
            (_, _) => Task.FromResult(TestMethods.Json("true"))
        );
        _connection.Start();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    [Fact]
    public async Task Reads_a_message_that_arrives_in_pieces()
    {
        byte[] frame = Wire.Frame("""{"jsonrpc":"2.0","id":1,"method":"echo","params":{"text":"split"}}""");
        // Splits inside the header name, inside the header end and inside the body.
        int[] cuts = [3, 17, 19, 20, 40];
        var chunks = new List<byte[]>();
        int start = 0;
        foreach (int cut in cuts.Append(frame.Length))
        {
            chunks.Add(frame[start..cut]);
            start = cut;
        }
        await _wire.SendRawAsync(chunks);

        JsonObject response = await _wire.ReceiveAsync();

        ((string?)response["result"]?["text"]).ShouldBe("split");
    }

    [Fact]
    public async Task Reads_several_messages_from_one_write()
    {
        await _wire.SendRawAsync([
            .. Wire.Frame("""{"jsonrpc":"2.0","id":1,"method":"echo","params":[1]}"""),
            .. Wire.Frame("""{"jsonrpc":"2.0","id":2,"method":"echo","params":[2]}"""),
        ]);

        int?[] ids = [(int?)(await _wire.ReceiveAsync())["id"], (int?)(await _wire.ReceiveAsync())["id"]];

        ids.Order().ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Round_trips_a_large_message_with_non_ascii_text()
    {
        // Content-Length counts UTF-8 bytes; the text has 1, 2 and 4 byte characters.
        string text = string.Concat(Enumerable.Repeat("aü😀", 1_000_000));
        await _wire.RequestAsync(1, "echo", JsonSerializer.Serialize(new JsonObject { ["text"] = text }));

        JsonObject response = await _wire.ReceiveAsync();

        ((string?)response["result"]?["text"]).ShouldBe(text);
    }

    [Fact]
    public async Task Header_names_ignore_case_and_other_headers_are_ignored()
    {
        byte[] body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"echo","params":[]}""");
        await _wire.SendRawAsync([
            .. Encoding.ASCII.GetBytes(
                $"content-length:{body.Length}\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n"
            ),
            .. body,
        ]);

        JsonObject response = await _wire.ReceiveAsync();

        ((int?)response["id"]).ShouldBe(1);
    }

    [Fact]
    public async Task A_body_that_is_not_json_answers_parse_error_and_reading_continues()
    {
        await _wire.SendAsync("{not json");
        await _wire.RequestAsync(2, "echo", "[]");

        JsonObject error = await _wire.ReceiveAsync();
        JsonObject next = await _wire.ReceiveAsync();

        error.ErrorCode().ShouldBe(ErrorCodes.ParseError);
        error["id"].ShouldBeNull();
        ((int?)next["id"]).ShouldBe(2);
    }

    [Theory]
    [InlineData("Content-Length: abc\r\n\r\n")]
    [InlineData("Content-Length: -5\r\n\r\n")]
    [InlineData("no header at all\r\n\r\n")]
    // A header without length is skipped together with its body.
    [InlineData("Content-Lnegth: 2\r\n\r\n{}")]
    public async Task A_malformed_header_is_skipped_and_reading_continues(string malformed)
    {
        await _wire.SendRawAsync(malformed);
        await _wire.RequestAsync(1, "echo", "[]");

        JsonObject response = await _wire.ReceiveAsync();

        ((int?)response["id"]).ShouldBe(1);
        lock (_log)
            _log.ShouldContain(m => m.StartsWith("Skipped a malformed message header", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("""{"jsonrpc":"2.0"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":{},"method":"echo"}""")]
    public async Task Messages_that_are_no_requests_answer_invalid_request(string message)
    {
        await _wire.SendAsync(message);

        JsonObject response = await _wire.ReceiveAsync();

        response.ErrorCode().ShouldBe(ErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task A_null_result_is_written_as_result_null()
    {
        await _wire.RequestAsync(1, "nothing");

        JsonObject response = await _wire.ReceiveAsync();

        response.ContainsKey("result").ShouldBeTrue();
        response["result"].ShouldBeNull();
        response.ContainsKey("error").ShouldBeFalse();
    }

    [Fact]
    public async Task String_request_ids_are_answered_with_the_same_id()
    {
        await _wire.SendAsync("""{"jsonrpc":"2.0","id":"abc","method":"echo","params":[]}""");

        JsonObject response = await _wire.ReceiveAsync();

        ((string?)response["id"]).ShouldBe("abc");
    }

    [Fact]
    public async Task Unknown_methods_answer_method_not_found_and_bad_params_invalid_params()
    {
        await _wire.RequestAsync(1, "unknown");
        await _wire.RequestAsync(2, "typed", """{"textDocument":5}""");
        await _wire.RequestAsync(3, "typed");

        JsonObject unknown = await _wire.ReceiveResponseAsync(1);
        JsonObject wrongShape = await _wire.ReceiveResponseAsync(2);
        JsonObject missing = await _wire.ReceiveResponseAsync(3);

        unknown.ErrorCode().ShouldBe(ErrorCodes.MethodNotFound);
        wrongShape.ErrorCode().ShouldBe(ErrorCodes.InvalidParams);
        missing.ErrorCode().ShouldBe(ErrorCodes.InvalidParams);
    }
}

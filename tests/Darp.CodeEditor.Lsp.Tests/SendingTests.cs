using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Shouldly;

namespace Darp.CodeEditor.Lsp.Tests;

/// <summary>The write path: sending queues, the write loop serializes and writes in order, cancellation never waits.</summary>
public sealed class SendingTests : IAsyncLifetime
{
    private static readonly TimeSpan s_prompt = TimeSpan.FromSeconds(5);

    private readonly Wire _wire = new();
    private readonly GatedStream _output;
    private readonly JsonRpcConnection _connection;

    public SendingTests()
    {
        _output = new GatedStream(_wire.Output);
        _connection = new JsonRpcConnection(_wire.Input, _output);
        _connection.Start();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _output.Resume();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Cancelling_ends_the_call_while_the_output_is_blocked_and_the_cancel_follows_the_whole_request()
    {
        // The request's header is written, its body blocks.
        _output.PauseAt(1);
        using var cancellation = new CancellationTokenSource();
        Task<JsonElement> call = _connection.SendRequestAsync(
            TestMethods.Request("slow"),
            TestMethods.Json("[1]"),
            cancellation.Token
        );
        await _output.Paused.Task.WaitAsync(s_prompt, TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            call.WaitAsync(s_prompt, TestContext.Current.CancellationToken)
        );
        _output.Resume();
        JsonObject request = await _wire.ReceiveAsync();
        JsonObject cancel = await _wire.ReceiveAsync();
        ((string?)request["method"]).ShouldBe("slow");
        ((string?)cancel["method"]).ShouldBe("$/cancelRequest");
        ((int?)cancel["params"]?["id"]).ShouldBe((int?)request["id"]);
    }

    [Fact]
    public async Task A_request_cancelled_before_it_went_out_is_left_out_with_its_cancel()
    {
        _output.PauseAt(0);
        Task first = _connection.SendNotificationAsync(TestMethods.Notification("first"), default);
        await _output.Paused.Task.WaitAsync(s_prompt, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        Task<JsonElement> call = _connection.SendRequestAsync(
            TestMethods.Request("never"),
            default,
            cancellation.Token
        );

        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            call.WaitAsync(s_prompt, TestContext.Current.CancellationToken)
        );
        Task last = _connection.SendNotificationAsync(TestMethods.Notification("last"), default);
        _output.Resume();
        await Task.WhenAll(first, last).WaitAsync(s_prompt, TestContext.Current.CancellationToken);
        ((string?)(await _wire.ReceiveAsync())["method"]).ShouldBe("first");
        ((string?)(await _wire.ReceiveAsync())["method"]).ShouldBe("last");
    }

    [Fact]
    public async Task Sending_returns_before_the_params_are_serialized_and_keeps_the_order()
    {
        var converter = new BlockingConverter();
        var slow = new NotificationType<string>(
            "slow",
            (JsonTypeInfo<string>)
                new JsonSerializerOptions
                {
                    Converters = { converter },
                    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
                }.GetTypeInfo(typeof(string))
        );
        try
        {
            // Off the test thread, so that a send that serializes on its caller fails the test instead of hanging it.
            Task sent = await Task
                .Factory.StartNew(
                    () => _connection.SendNotificationAsync(slow, "large document"),
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    TaskScheduler.Default
                )
                .WaitAsync(s_prompt, TestContext.Current.CancellationToken);
            Task next = _connection.SendNotificationAsync(TestMethods.Notification("next"), default);
            await converter.Entered.Task.WaitAsync(s_prompt, TestContext.Current.CancellationToken);

            sent.IsCompleted.ShouldBeFalse();
            converter.Release.SetResult();
            await Task.WhenAll(sent, next).WaitAsync(s_prompt, TestContext.Current.CancellationToken);
            ((string?)(await _wire.ReceiveAsync())["params"]).ShouldBe("large document");
            ((string?)(await _wire.ReceiveAsync())["method"]).ShouldBe("next");
        }
        finally
        {
            converter.Release.TrySetResult();
        }
    }

    /// <summary>Serializes a string only once released, like a large document that takes long to serialize.</summary>
    private sealed class BlockingConverter : JsonConverter<string>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() ?? "";

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            Entered.TrySetResult();
            Release.Task.Wait();
            writer.WriteStringValue(value);
        }
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace Darp.CodeEditor.Lsp.Tests;

/// <summary>Cancellation in both directions, before, during and after a request runs.</summary>
public sealed class CancellationTests : IAsyncLifetime
{
    private readonly Wire _wire = new();
    private readonly JsonRpcConnection _connection;
    private readonly TaskCompletionSource<CancellationToken> _started = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _workRuns;

    public CancellationTests()
    {
        _connection = new JsonRpcConnection(_wire.Input, _wire.Output);
        // Holds up the dispatch of later messages until the far end answers its request.
        _connection.OnNotification(
            TestMethods.Notification("block"),
            async (_, token) => await _connection.SendRequestAsync(TestMethods.Request("unblock"), default, token)
        );
        _connection.OnRequest(
            TestMethods.Request("work"),
            (_, _) =>
            {
                Interlocked.Increment(ref _workRuns);
                return Task.FromResult(TestMethods.Json("\"done\""));
            }
        );
        _connection.OnRequest(
            TestMethods.Request("wait"),
            async (_, token) =>
            {
                _started.SetResult(token);
                await Task.Delay(Timeout.Infinite, token);
                return default;
            }
        );
        // Ignores its token, like a handler that is busy in code that cannot be cancelled.
        _connection.OnRequest(
            TestMethods.Request("ignore"),
            async (_, token) =>
            {
                _started.SetResult(token);
                await _release.Task;
                return TestMethods.Json("\"full result\"");
            }
        );
        _connection.Start();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    [Fact]
    public async Task A_request_cancelled_while_queued_answers_request_cancelled_and_never_runs()
    {
        await _wire.NotifyAsync("block");
        JsonObject unblock = await _wire.ReceiveAsync();
        // Read in this order: the request is queued behind "block", then cancelled, then "block" ends.
        await _wire.RequestAsync(1, "work");
        await _wire.CancelAsync(1);
        await _wire.SendAsync($$"""{"jsonrpc":"2.0","id":{{unblock["id"]}},"result":null}""");

        JsonObject response = await _wire.ReceiveResponseAsync(1);

        response.ErrorCode().ShouldBe(ErrorCodes.RequestCancelled);
        _workRuns.ShouldBe(0);
    }

    [Fact]
    public async Task A_request_cancelled_while_running_answers_request_cancelled()
    {
        await _wire.RequestAsync(1, "wait");
        await _started.Task.WaitAsync(Wire.Timeout);
        await _wire.CancelAsync(1);

        JsonObject response = await _wire.ReceiveResponseAsync(1);

        response.ErrorCode().ShouldBe(ErrorCodes.RequestCancelled);
    }

    [Fact]
    public async Task A_handler_that_returns_a_result_after_the_cancel_still_answers_request_cancelled()
    {
        await _wire.RequestAsync(1, "ignore");
        CancellationToken token = await _started.Task.WaitAsync(Wire.Timeout);
        var cancelled = new TaskCompletionSource();
        token.Register(cancelled.SetResult);
        await _wire.CancelAsync(1);
        await cancelled.Task.WaitAsync(Wire.Timeout);
        _release.SetResult();

        JsonObject response = await _wire.ReceiveResponseAsync(1);

        response.ErrorCode().ShouldBe(ErrorCodes.RequestCancelled);
        response.ContainsKey("result").ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancel_with_a_string_id_or_an_unknown_id_cancels_nothing()
    {
        await _wire.RequestAsync(1, "ignore");
        CancellationToken token = await _started.Task.WaitAsync(Wire.Timeout);
        await _wire.NotifyAsync("$/cancelRequest", """{"id":"1"}""");
        await _wire.CancelAsync(999);
        // The read loop handles messages in order, so the cancels took effect once this response was read.
        Task<JsonElement> barrier = _connection.SendRequestAsync(TestMethods.Request("barrier"), default, Wire.Timeout);
        JsonObject request = await _wire.ReceiveAsync();
        await _wire.SendAsync($$"""{"jsonrpc":"2.0","id":{{request["id"]}},"result":null}""");
        await barrier;

        token.IsCancellationRequested.ShouldBeFalse();
        _release.SetResult();
        ((string?)(await _wire.ReceiveResponseAsync(1))["result"]).ShouldBe("full result");
    }

    [Fact]
    public async Task A_cancel_after_the_response_changes_nothing()
    {
        await _wire.RequestAsync(1, "work");
        JsonObject first = await _wire.ReceiveResponseAsync(1);
        await _wire.CancelAsync(1);
        await _wire.RequestAsync(2, "work");

        JsonObject next = await _wire.ReceiveAsync();

        ((string?)first["result"]).ShouldBe("done");
        ((int?)next["id"]).ShouldBe(2);
        ((string?)next["result"]).ShouldBe("done");
    }

    [Fact]
    public async Task Cancelling_an_outgoing_request_sends_cancel_request_and_ignores_the_late_response()
    {
        using var cancellation = new CancellationTokenSource();
        Task<JsonElement> call = _connection.SendRequestAsync(TestMethods.Request("slow"), default, cancellation.Token);
        JsonObject request = await _wire.ReceiveAsync();

        await cancellation.CancelAsync();
        JsonObject cancel = await _wire.ReceiveAsync();
        await _wire.SendAsync($$"""{"jsonrpc":"2.0","id":{{request["id"]}},"result":"late"}""");

        await Should.ThrowAsync<OperationCanceledException>(call);
        ((string?)cancel["method"]).ShouldBe("$/cancelRequest");
        ((int?)cancel["params"]?["id"]).ShouldBe((int?)request["id"]);
        // The connection still works after the late response.
        await _wire.RequestAsync(7, "work");
        ((int?)(await _wire.ReceiveAsync())["id"]).ShouldBe(7);
    }

    [Fact]
    public async Task Ending_the_input_cancels_running_requests_and_fails_outgoing_ones()
    {
        Task<JsonElement> call = _connection.SendRequestAsync(TestMethods.Request("never"), default, Wire.Timeout);
        await _wire.RequestAsync(1, "wait");
        CancellationToken token = await _started.Task.WaitAsync(Wire.Timeout);

        _wire.CloseInput();

        await Should.ThrowAsync<IOException>(call);
        await _connection.Completion.WaitAsync(Wire.Timeout);
        token.IsCancellationRequested.ShouldBeTrue();
    }
}

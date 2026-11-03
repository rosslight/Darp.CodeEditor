using System.Collections.Concurrent;
using System.Text.Json;
using Shouldly;

namespace Darp.CodeEditor.Lsp.Tests;

/// <summary>Ordering of messages, both endpoint roles and error answers, between two connections.</summary>
public sealed class DispatchTests : IAsyncLifetime
{
    private readonly Wire _wire = new();
    private readonly JsonRpcConnection _server;
    private readonly JsonRpcConnection _client;

    public DispatchTests()
    {
        _server = new JsonRpcConnection(_wire.Input, _wire.Output);
        (Stream input, Stream output) = _wire.Peer;
        _client = new JsonRpcConnection(input, output);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Changes_apply_in_arrival_order_and_before_later_requests()
    {
        var applied = new List<int>();
        _server.OnNotification(
            TestMethods.Notification("change"),
            async (p, _) =>
            {
                // Takes a while, so a request that did not wait for it would see too few changes.
                await Task.Delay(Random.Shared.Next(5));
                applied.Add(p.GetInt32());
            }
        );
        _server.OnRequest(
            TestMethods.Request("count"),
            (_, _) => Task.FromResult(TestMethods.Json($"{applied.Count}"))
        );
        Start();

        var counts = new List<Task<JsonElement>>();
        for (int i = 1; i <= 20; i++)
        {
            await _client.SendNotificationAsync(TestMethods.Notification("change"), TestMethods.Json($"{i}"));
            counts.Add(_client.SendRequestAsync(TestMethods.Request("count"), default, Wire.Timeout));
        }

        (await Task.WhenAll(counts)).Select(c => c.GetInt32()).ShouldBe(Enumerable.Range(1, 20));
        applied.ShouldBe(Enumerable.Range(1, 20));
    }

    [Fact]
    public async Task Requests_run_concurrently_and_a_slow_request_does_not_hold_up_changes()
    {
        var secondStarted = new TaskCompletionSource();
        var changed = new TaskCompletionSource();
        _server.OnRequest(
            TestMethods.Request("first"),
            async (_, _) =>
            {
                // Finishes only after a later request and a later change ran.
                await Task.WhenAll(secondStarted.Task, changed.Task);
                return TestMethods.Json("1");
            }
        );
        _server.OnRequest(
            TestMethods.Request("second"),
            (_, _) =>
            {
                secondStarted.SetResult();
                return Task.FromResult(TestMethods.Json("2"));
            }
        );
        _server.OnNotification(
            TestMethods.Notification("change"),
            (_, _) =>
            {
                changed.SetResult();
                return Task.CompletedTask;
            }
        );
        Start();

        Task<JsonElement> first = _client.SendRequestAsync(TestMethods.Request("first"), default, Wire.Timeout);
        Task<JsonElement> second = _client.SendRequestAsync(TestMethods.Request("second"), default, Wire.Timeout);
        await _client.SendNotificationAsync(TestMethods.Notification("change"), default);

        (await first).GetInt32().ShouldBe(1);
        (await second).GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task A_serial_request_holds_up_the_messages_after_it()
    {
        var events = new ConcurrentQueue<string>();
        _server.OnRequest(
            TestMethods.Request("exclusive"),
            async (_, _) =>
            {
                await Task.Delay(50);
                events.Enqueue("request");
                return default;
            },
            DispatchMode.Serial
        );
        _server.OnRequest(
            TestMethods.Request("after"),
            (_, _) =>
            {
                events.Enqueue("after");
                return Task.FromResult(default(JsonElement));
            }
        );
        Start();

        Task<JsonElement> exclusive = _client.SendRequestAsync(TestMethods.Request("exclusive"), default, Wire.Timeout);
        await _client.SendRequestAsync(TestMethods.Request("after"), default, Wire.Timeout);
        await exclusive;

        events.ShouldBe(["request", "after"]);
    }

    [Fact]
    public async Task Both_ends_send_requests_and_responses_find_their_callers()
    {
        _client.OnRequest(
            TestMethods.Request("workspace/configuration"),
            (p, _) => Task.FromResult(TestMethods.Json($"\"config of {p.GetString()}\""))
        );
        _server.OnRequest(
            TestMethods.Request("echoLater"),
            async (p, token) =>
            {
                await Task.Delay(30 - (5 * p.GetInt32()), token);
                // A server-to-client request from inside a request handler.
                JsonElement config = await _server.SendRequestAsync(
                    TestMethods.Request("workspace/configuration"),
                    TestMethods.Json($"\"{p.GetInt32()}\""),
                    token
                );
                return config;
            }
        );
        Start();

        JsonElement[] results = await Task.WhenAll(
            Enumerable
                .Range(1, 5)
                .Select(i =>
                    _client.SendRequestAsync(TestMethods.Request("echoLater"), TestMethods.Json($"{i}"), Wire.Timeout)
                )
        );

        results.Select(r => r.GetString()).ShouldBe(Enumerable.Range(1, 5).Select(i => $"config of {i}"));
    }

    [Fact]
    public async Task Handler_errors_reach_the_caller_with_their_codes()
    {
        _server.OnRequest(
            TestMethods.Request("stale"),
            (_, _) => throw new JsonRpcException(ErrorCodes.ContentModified, "The document changed.")
        );
        _server.OnRequest(
            TestMethods.Request("failed"),
            (_, _) =>
                throw new JsonRpcException(
                    ErrorCodes.RequestFailed,
                    "No such file.",
                    TestMethods.Json("{\"file\":\"a\"}")
                )
        );
        _server.OnRequest(TestMethods.Request("crash"), (_, _) => throw new InvalidOperationException("Bug."));
        Start();

        JsonRpcException stale = await Should.ThrowAsync<JsonRpcException>(Send("stale"));
        JsonRpcException failed = await Should.ThrowAsync<JsonRpcException>(Send("failed"));
        JsonRpcException crash = await Should.ThrowAsync<JsonRpcException>(Send("crash"));
        JsonRpcException unknown = await Should.ThrowAsync<JsonRpcException>(Send("unknown"));

        stale.Code.ShouldBe(ErrorCodes.ContentModified);
        (failed.Code, failed.Message, failed.ErrorData?.GetProperty("file").GetString()).ShouldBe(
            (ErrorCodes.RequestFailed, "No such file.", "a")
        );
        (crash.Code, crash.Message).ShouldBe((ErrorCodes.InternalError, "Bug."));
        unknown.Code.ShouldBe(ErrorCodes.MethodNotFound);
    }

    private Task<JsonElement> Send(string method) =>
        _client.SendRequestAsync(TestMethods.Request(method), default, Wire.Timeout);

    private void Start()
    {
        _server.Start();
        _client.Start();
    }
}

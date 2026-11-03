using System.Text.Json.Nodes;
using Darp.CodeEditor.Lsp.Protocol;
using Shouldly;

namespace Darp.CodeEditor.Lsp.Tests;

/// <summary>The server lifecycle: initialize, shutdown and exit, and what is answered around them.</summary>
public sealed class LifecycleTests : IAsyncLifetime
{
    private const string Initialize = """{"capabilities":{}}""";
    private const string FailingInitialize = """{"capabilities":{},"initializationOptions":{"fail":true}}""";
    private const string Hover = """{"textDocument":{"uri":"file:///a"},"position":{"line":0,"character":0}}""";

    private readonly Wire _wire = new();
    private readonly LanguageServer _server;
    private readonly Task<int> _run;
    private int _opened;
    private bool _shutdownRan;

    public LifecycleTests()
    {
        _server = new LanguageServer(
            _wire.Input,
            _wire.Output,
            async (p, token) =>
            {
                // Slow, so that messages sent right after initialize queue behind it.
                await Task.Delay(20, token);
                return p.InitializationOptions is { } options && options.TryGetProperty("fail", out _)
                    ? throw new JsonRpcException(ErrorCodes.RequestFailed, "Bad options.")
                    : new InitializeResult(new ServerCapabilities(HoverProvider: true));
            },
            () =>
            {
                _shutdownRan = true;
                return Task.CompletedTask;
            }
        );
        _server.Connection.OnNotification(
            LspMethods.DidOpen,
            (_, _) =>
            {
                Interlocked.Increment(ref _opened);
                return Task.CompletedTask;
            }
        );
        _server.Connection.OnRequest(
            LspMethods.Hover,
            (_, _) => Task.FromResult<Hover?>(new Hover(new MarkupContent(MarkupKind.PlainText, "hi")))
        );
        _run = _server.RunAsync();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    [Fact]
    public async Task Before_initialize_requests_answer_server_not_initialized_and_notifications_are_dropped()
    {
        await _wire.NotifyAsync("textDocument/didOpen", Open);
        await _wire.RequestAsync(1, "textDocument/hover", Hover);
        await _wire.RequestAsync(2, "unknown/method");

        (await _wire.ReceiveResponseAsync(1)).ErrorCode().ShouldBe(ErrorCodes.ServerNotInitialized);
        (await _wire.ReceiveResponseAsync(2)).ErrorCode().ShouldBe(ErrorCodes.ServerNotInitialized);
        _opened.ShouldBe(0);
    }

    [Fact]
    public async Task Messages_right_after_initialize_see_the_initialized_server()
    {
        await _wire.RequestAsync(1, "initialize", Initialize);
        await _wire.NotifyAsync("textDocument/didOpen", Open);
        await _wire.RequestAsync(2, "textDocument/hover", Hover);

        JsonObject initialized = await _wire.ReceiveResponseAsync(1);
        JsonObject hover = await _wire.ReceiveResponseAsync(2);

        ((bool?)initialized["result"]?["capabilities"]?["hoverProvider"]).ShouldBe(true);
        ((string?)hover["result"]?["contents"]?["value"]).ShouldBe("hi");
        _opened.ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_initialize_does_not_count_and_can_be_repeated()
    {
        await _wire.RequestAsync(1, "initialize", FailingInitialize);
        await _wire.RequestAsync(2, "textDocument/hover", Hover);
        await _wire.RequestAsync(3, "initialize", Initialize);
        await _wire.RequestAsync(4, "textDocument/hover", Hover);

        (await _wire.ReceiveResponseAsync(1)).ErrorCode().ShouldBe(ErrorCodes.RequestFailed);
        (await _wire.ReceiveResponseAsync(2)).ErrorCode().ShouldBe(ErrorCodes.ServerNotInitialized);
        (await _wire.ReceiveResponseAsync(3))["error"].ShouldBeNull();
        (await _wire.ReceiveResponseAsync(4))["error"].ShouldBeNull();
    }

    [Fact]
    public async Task A_cancelled_initialize_does_not_count_even_when_the_initializer_returned()
    {
        var wire = new Wire();
        var started = new TaskCompletionSource<CancellationToken>();
        var release = new TaskCompletionSource();
        await using var server = new LanguageServer(
            wire.Input,
            wire.Output,
            async (_, token) =>
            {
                // Ignores its token, so it returns a result after the cancel.
                started.SetResult(token);
                await release.Task;
                return new InitializeResult(new ServerCapabilities());
            }
        );
        Task<int> run = server.RunAsync();
        await wire.RequestAsync(1, "initialize", Initialize);
        CancellationToken token = await started.Task.WaitAsync(Wire.Timeout);
        var cancelled = new TaskCompletionSource();
        using CancellationTokenRegistration registration = token.Register(cancelled.SetResult);
        await wire.CancelAsync(1);
        await cancelled.Task.WaitAsync(Wire.Timeout);
        // Queued behind initialize, which runs serially, so it sees the outcome.
        await wire.RequestAsync(2, "unknown/method");
        release.SetResult();

        (await wire.ReceiveResponseAsync(1)).ErrorCode().ShouldBe(ErrorCodes.RequestCancelled);
        (await wire.ReceiveResponseAsync(2)).ErrorCode().ShouldBe(ErrorCodes.ServerNotInitialized);
        run.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task A_second_initialize_answers_invalid_request()
    {
        await _wire.RequestAsync(1, "initialize", Initialize);
        await _wire.RequestAsync(2, "initialize", Initialize);

        (await _wire.ReceiveResponseAsync(2)).ErrorCode().ShouldBe(ErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task After_shutdown_requests_answer_invalid_request_and_exit_ends_with_code_0()
    {
        await _wire.RequestAsync(1, "initialize", Initialize);
        await _wire.RequestAsync(2, "shutdown");
        await _wire.RequestAsync(3, "textDocument/hover", Hover);
        await _wire.RequestAsync(4, "shutdown");

        JsonObject shutdown = await _wire.ReceiveResponseAsync(2);
        (await _wire.ReceiveResponseAsync(3)).ErrorCode().ShouldBe(ErrorCodes.InvalidRequest);
        (await _wire.ReceiveResponseAsync(4)).ErrorCode().ShouldBe(ErrorCodes.InvalidRequest);
        await _wire.NotifyAsync("exit");

        shutdown.ContainsKey("result").ShouldBeTrue();
        _shutdownRan.ShouldBeTrue();
        (await _run.WaitAsync(Wire.Timeout)).ShouldBe(0);
    }

    [Fact]
    public async Task Exit_without_shutdown_ends_with_code_1()
    {
        await _wire.RequestAsync(1, "initialize", Initialize);
        await _wire.NotifyAsync("exit");

        (await _run.WaitAsync(Wire.Timeout)).ShouldBe(1);
    }

    [Fact]
    public async Task The_end_of_the_input_ends_with_code_1()
    {
        await _wire.RequestAsync(1, "initialize", Initialize);
        await _wire.RequestAsync(2, "shutdown");
        await _wire.ReceiveResponseAsync(2);

        _wire.CloseInput();

        (await _run.WaitAsync(Wire.Timeout)).ShouldBe(1);
    }

    private static string Open => """{"textDocument":{"uri":"file:///a","languageId":"text","version":1,"text":""}}""";
}

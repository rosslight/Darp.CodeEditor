using System.Text.Json;
using System.Threading.Channels;
using Darp.CodeEditor.Lsp.Hosting;
using Darp.CodeEditor.Lsp.Protocol;
using Darp.CodeEditor.Lsp.Testing;
using Shouldly;

namespace Darp.CodeEditor.Lsp.Tests;

/// <summary>The client lifecycle against the toy server, and restarts by the supervisor.</summary>
public sealed class LanguageClientTests
{
    [Fact]
    public async Task The_client_initializes_answers_the_settings_request_and_shuts_the_server_down()
    {
        var wire = new Wire();
        var toy = new ToyServer(wire.Input, wire.Output);
        Task<int> run = toy.Server.RunAsync();
        (Stream input, Stream output) = wire.Peer;
        var transport = new LanguageServerTransport(input, output, run, toy.Server.DisposeAsync);
        var options = new LanguageClientOptions(ToyClient.InitializeParams)
        {
            Settings = item => item.Section == "toy" ? ToyServer.ToJson("""{"greeting":"Hi"}""") : null,
        };

        LanguageClient client = await LanguageClient.StartAsync(transport, options, Wire.Timeout);
        await client.Connection.SendNotificationAsync(
            LspMethods.DidOpen,
            new DidOpenTextDocumentParams(new TextDocumentItem(ToyClient.Uri, "toy", 1, "alpha"))
        );
        Hover? hover = await client.Connection.SendRequestAsync(
            LspMethods.Hover,
            new HoverParams(new TextDocumentIdentifier(ToyClient.Uri), new Position(0, 1)),
            Wire.Timeout
        );
        await client.DisposeAsync();

        client.ServerInfo.ShouldBe(new ServerInfo("toy", "1.0"));
        hover.ShouldNotBeNull().Contents.Value.ShouldBe("Hi, **alpha**");
        (await run.WaitAsync(Wire.Timeout)).ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_start_fails_the_caller_and_is_retried()
    {
        int starts = 0;
        var sessions = Channel.CreateUnbounded<FakeSession>();
        await using var supervisor = new Supervisor<FakeSession>(
            _ =>
            {
                if (Interlocked.Increment(ref starts) == 1)
                    throw new IOException("The server is missing.");
                var session = new FakeSession();
                sessions.Writer.TryWrite(session);
                return Task.FromResult(session);
            },
            session => session.Ended.Task,
            Fast
        );

        await Should.ThrowAsync<IOException>(supervisor.StartAsync());
        FakeSession retried = await sessions.Reader.ReadAsync(Wire.Timeout);

        (await supervisor.StartAsync().WaitAsync(Wire.Timeout)).ShouldBe(retried);
    }

    [Fact]
    public async Task A_session_that_ends_is_disposed_and_replaced_and_disposal_ends_the_last_one()
    {
        var sessions = Channel.CreateUnbounded<FakeSession>();
        var failed = new List<FakeSession?>();
        var supervisor = new Supervisor<FakeSession>(
            _ =>
            {
                var session = new FakeSession();
                sessions.Writer.TryWrite(session);
                return Task.FromResult(session);
            },
            session => session.Ended.Task,
            Fast
        );
        supervisor.Failed += (session, _) =>
        {
            lock (failed)
                failed.Add(session);
        };

        FakeSession first = await supervisor.StartAsync().WaitAsync(Wire.Timeout);
        (await sessions.Reader.ReadAsync(Wire.Timeout)).ShouldBe(first);
        first.Ended.SetResult();
        FakeSession second = await sessions.Reader.ReadAsync(Wire.Timeout);
        second.ShouldNotBe(first);
        await supervisor.DisposeAsync();

        first.Disposed.ShouldBeTrue();
        second.Disposed.ShouldBeTrue();
        lock (failed)
            failed.ShouldBe([first]);
        sessions.Reader.TryRead(out _).ShouldBeFalse();
    }

    private static SupervisorOptions Fast { get; } = new() { FirstDelay = TimeSpan.FromMilliseconds(10) };

    private sealed class FakeSession : IAsyncDisposable
    {
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Ended.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}

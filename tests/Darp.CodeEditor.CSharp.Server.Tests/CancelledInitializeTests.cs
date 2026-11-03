using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.Lsp.Protocol;
using Darp.CodeEditor.LspTesting;
using Shouldly;

namespace Darp.CodeEditor.CSharp.Server.Tests;

/// <summary>An <c>initialize</c> the client cancelled after the server did its work, then one that succeeds.</summary>
public sealed class CancelledInitializeTests
{
    [Fact]
    public async Task A_cancelled_initialize_reports_nothing_after_a_retry_succeeded()
    {
        await using var server = new InProcessServer();
        CSharpScriptEnvironment missingGlobals = TestEnvironments.Device with
        {
            Globals = new CSharpScriptGlobals(typeof(InProcessServer).Assembly.Location, "Missing.Globals"),
        };

        await InitializeTwiceAsync(server, InProcessServer.InitializeParams(missingGlobals));
        // Diagnostics need the retry's environment and a compilation: they come after the cancelled environment
        // would have failed to load.
        await server.Client.SendNotificationAsync(
            LspMethods.DidOpen,
            new DidOpenTextDocumentParams(new TextDocumentItem("file:///scripts/a.csx", "csharp", 1, "Log(1);"))
        );
        PublishDiagnosticsParams published = await server.Diagnostics.ReadAsync(InProcessServer.Timeout);

        published.Diagnostics.ShouldBeEmpty();
        server.Messages.TryRead(out string? message).ShouldBeFalse(message);
    }

    [Fact]
    public async Task A_cancelled_initialize_does_not_watch_its_client_process()
    {
        // Stand in for client processes: ones the test can end.
        await using LspClient cancelledClient = TestServer.Start();
        await using LspClient client = TestServer.Start();
        int cancelledId = cancelledClient.Process.Id;
        int clientId = client.Process.Id;
        await using var server = new InProcessServer();

        await InitializeTwiceAsync(server, InProcessServer.InitializeParams(TestEnvironments.Device, cancelledId), clientId);
        cancelledClient.Process.Kill();
        await cancelledClient.Process.WaitForExitAsync(InProcessServer.Timeout);
        client.Process.Kill();
        int exited = await server.ExitedClients.ReadAsync(InProcessServer.Timeout);

        exited.ShouldBe(clientId);
    }

    /// <summary>
    /// Sends <paramref name="cancelled"/>, cancels it once the server did its work, then initializes with the device
    /// environment and sends <c>initialized</c>.
    /// </summary>
    private static async Task InitializeTwiceAsync(
        InProcessServer server,
        InitializeParams cancelled,
        int? processId = null
    )
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(InProcessServer.Timeout);
        Task first = server.Client.SendRequestAsync(LspMethods.Initialize, cancelled, cancellation.Token);
        await server.FirstInitializeHeld.WaitAsync(InProcessServer.Timeout);
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(first);

        InitializeResult? result = await server.Client.SendRequestAsync(
            LspMethods.Initialize,
            InProcessServer.InitializeParams(TestEnvironments.Device, processId),
            InProcessServer.Timeout
        );
        await server.Client.SendNotificationAsync(LspMethods.Initialized, new InitializedParams());

        result.ShouldNotBeNull().Capabilities.HoverProvider.ShouldBe(true);
    }
}

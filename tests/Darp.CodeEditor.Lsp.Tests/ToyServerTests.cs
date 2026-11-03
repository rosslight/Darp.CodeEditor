using Darp.CodeEditor.Lsp.Protocol;
using Darp.CodeEditor.Lsp.Testing;
using Shouldly;

namespace Darp.CodeEditor.Lsp.Tests;

/// <summary>A toy server and a client on this library, talking over in-memory pipes.</summary>
public sealed class ToyServerTests : IAsyncLifetime
{
    private const string Text = "alpha error\nslow stale add(1, 2)\n";

    private readonly Wire _wire = new();
    private readonly ToyServer _server;
    private readonly Task<int> _run;
    private readonly ToyClient _client;
    private InitializeResult? _initialized;

    public ToyServerTests()
    {
        _server = new ToyServer(_wire.Input, _wire.Output);
        _run = _server.Server.RunAsync();
        (Stream input, Stream output) = _wire.Peer;
        _client = new ToyClient(input, output, "Hi");
    }

    private static CancellationToken Timeout => Wire.Timeout;

    public async ValueTask InitializeAsync()
    {
        _initialized = await _client.InitializeAsync(Timeout);
        await _client.OpenAsync(Text);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.Server.DisposeAsync();
    }

    [Fact]
    public void Initialize_announces_the_capabilities()
    {
        InitializeResult result = _initialized.ShouldNotBeNull();

        result.ServerInfo.ShouldBe(new ServerInfo("toy", "1.0"));
        result.Capabilities.TextDocumentSync.ShouldNotBeNull().Change.ShouldBe(TextDocumentSyncKind.Incremental);
        result.Capabilities.SemanticTokensProvider.ShouldNotBeNull().Legend.TokenTypes.ShouldBe(["word", "number"]);
    }

    [Fact]
    public async Task Opening_and_changing_a_document_publishes_diagnostics_for_each_version()
    {
        PublishDiagnosticsParams opened = await _client.DiagnosticsAsync(1, Timeout);
        // Inserts "error " at the start of the second line, in UTF-16 positions.
        await _client.ChangeAsync(2, new DocumentRange(new Position(1, 0), new Position(1, 0)), "error ");
        PublishDiagnosticsParams changed = await _client.DiagnosticsAsync(2, Timeout);

        opened.Uri.ShouldBe(ToyClient.Uri);
        opened
            .Diagnostics.Select(d => (d.Range, d.Code))
            .ShouldBe([(new DocumentRange(new(0, 6), new(0, 11)), "TOY1")]);
        changed.Diagnostics.Select(d => d.Range.Start).ShouldBe([new Position(0, 6), new Position(1, 0)]);
        _client.Logs.ShouldContain($"Opened {ToyClient.Uri}");
    }

    [Fact]
    public async Task Completion_returns_insert_and_replace_edits_and_resolve_keeps_the_data()
    {
        CompletionList? list = await _client.CompletionAsync(new Position(0, 2), Timeout);
        CompletionItem alpha = list.ShouldNotBeNull().Items[0];
        CompletionItem? resolved = await _client.Connection.SendRequestAsync(
            LspMethods.CompletionResolve,
            alpha,
            Timeout
        );

        list.IsIncomplete.ShouldBeTrue();
        alpha.TextEdit.ShouldBe(
            new CompletionTextEdit(
                "alpha",
                new DocumentRange(new(0, 0), new(0, 2)),
                new DocumentRange(new(0, 0), new(0, 5))
            )
        );
        alpha.CommitCharacters.ShouldBe(["."]);
        list.Items[1].AdditionalTextEdits.ShouldNotBeNull().Single().NewText.ShouldBe("-- uses beta\n");
        resolved.ShouldNotBeNull().Detail.ShouldBe("resolved 1");
        resolved.Data.ShouldNotBeNull().GetRawText().ShouldBe("""{"id":1,"nested":[true,null]}""");
    }

    [Fact]
    public async Task Hover_uses_the_setting_the_server_asked_the_client_for()
    {
        Hover? hover = await _client.HoverAsync(new Position(0, 1), Timeout);

        hover.ShouldBe(
            new Hover(new MarkupContent(MarkupKind.Markdown, "Hi, **alpha**"), new DocumentRange(new(0, 0), new(0, 5)))
        );
    }

    [Fact]
    public async Task Signature_help_gives_parameter_offsets_and_the_active_parameter()
    {
        SignatureHelp? help = await _client.SignatureHelpAsync(new Position(1, 18), Timeout);

        help.ShouldNotBeNull().ActiveParameter.ShouldBe(1);
        help.Signatures.Single()
            .Parameters.ShouldNotBeNull()
            .Select(p => p.Label)
            .ShouldBe([new ParameterLabel(4, 9), new ParameterLabel(11, 16)]);
    }

    [Fact]
    public async Task Semantic_tokens_classify_the_words_of_the_range()
    {
        SemanticTokens? tokens = await _client.SemanticTokensAsync(new DocumentRange(new(1, 11), new(1, 20)), Timeout);

        // "add" on line 1, "1" four characters later, "2" three characters after that.
        tokens.ShouldNotBeNull().Data.ShouldBe([1, 11, 3, 0, 0, 0, 4, 1, 1, 0, 0, 3, 1, 1, 0]);
    }

    [Fact]
    public async Task Cancelling_a_slow_request_ends_it_right_away()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(_client.HoverAsync(new Position(1, 1), cancellation.Token));
        // The server answered the cancel and still works.
        (await _client.HoverAsync(new Position(0, 1), Timeout)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_request_made_stale_by_a_change_answers_content_modified()
    {
        Task<Hover?> stale = _client.HoverAsync(new Position(1, 7), Timeout);
        await _client.ChangeAsync(2, new DocumentRange(new(0, 0), new(0, 0)), "x");

        JsonRpcException exception = await Should.ThrowAsync<JsonRpcException>(stale);

        exception.Code.ShouldBe(ErrorCodes.ContentModified);
    }

    [Fact]
    public async Task Shutdown_and_exit_end_the_server_with_code_0()
    {
        await _client.ShutdownAsync(Timeout);

        (await _run.WaitAsync(Timeout)).ShouldBe(0);
    }
}

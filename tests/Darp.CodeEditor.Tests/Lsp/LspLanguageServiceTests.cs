using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Darp.CodeEditor.LanguageServices;
using Shouldly;

namespace Darp.CodeEditor.Tests.Lsp;

/// <summary>The LSP client against the toy server of the LSP layer's tests.</summary>
public sealed class LspLanguageServiceTests : IAsyncLifetime
{
    private const string Text = "alpha error\nslow stale add(1, 2)\n";

    private readonly ToyServers _servers = new();
    private readonly LspLanguageService _service;
    private readonly Channel<DiagnosticsChangedEventArgs> _diagnostics =
        Channel.CreateUnbounded<DiagnosticsChangedEventArgs>();

    public LspLanguageServiceTests()
    {
        _service = _servers.CreateService();
        _service.DiagnosticsChanged += (_, e) => _diagnostics.Writer.TryWrite(e);
    }

    private static CancellationToken Timeout => Wait.Timeout;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _service.DisposeAsync();

    [Fact]
    public async Task Documents_opened_before_the_server_started_reach_it_at_their_current_version()
    {
        _service.OpenDocument(new DocumentSnapshot("a", 1, "alpha error\n"));
        // Inserts "beta " before "error".
        _service.ChangeDocument(new DocumentChange("a", 2, new TextChange(6, 0, "beta ")));
        await _service.PrewarmAsync(Timeout);

        QuickInfo? info = await _service.GetQuickInfoAsync(new DocumentVersion("a", 2), 7, Timeout);

        info.ShouldNotBeNull().Range.ShouldBe(new TextRange(6, 4));
        info.Content.ShouldBe([new MarkupBlock(MarkupBlockKind.Paragraph, "Hi, beta")]);
        JsonObject opened = _servers.SentOf("textDocument/didOpen").Single();
        ((int?)opened["params"]?["textDocument"]?["version"]).ShouldBe(2);
    }

    [Fact]
    public async Task Changes_go_out_incrementally_before_the_requests_made_after_them()
    {
        await _service.PrewarmAsync(Timeout);
        _service.OpenDocument(new DocumentSnapshot("a", 1, Text));
        // Replaces "slow" on the second line with "quick".
        _service.ChangeDocument(new DocumentChange("a", 2, new TextChange(12, 4, "quick")));

        QuickInfo? info = await _service.GetQuickInfoAsync(new DocumentVersion("a", 2), 13, Timeout);

        info.ShouldNotBeNull().Text.ShouldBe("Hi, quick");
        info.Range.ShouldBe(new TextRange(12, 5));
        JsonNode change = _servers.SentOf("textDocument/didChange").Single()["params"]!;
        change["contentChanges"]!
            .ToJsonString()
            .ShouldBe(
                """[{"text":"quick","range":{"start":{"line":1,"character":0},"end":{"line":1,"character":4}}}]"""
            );
    }

    [Fact]
    public async Task Diagnostics_arrive_per_version_as_offsets()
    {
        await _service.PrewarmAsync(Timeout);
        _service.OpenDocument(new DocumentSnapshot("a", 1, "alpha error\n"));
        DiagnosticsChangedEventArgs opened = await _diagnostics.Reader.ReadAsync(Timeout);
        _service.ChangeDocument(new DocumentChange("a", 2, new TextChange(0, 0, "error ")));
        DiagnosticsChangedEventArgs changed = await _diagnostics.Reader.ReadAsync(Timeout);

        (opened.DocumentId, opened.Version).ShouldBe(("a", 1));
        opened.Diagnostics.ShouldBe([
            new DiagnosticInfo(new TextRange(6, 5), DiagnosticSeverity.Error, "TOY1", "Found an error."),
        ]);
        changed.Version.ShouldBe(2);
        changed.Diagnostics.Select(d => d.Range).ShouldBe([new TextRange(0, 5), new TextRange(12, 5)]);
    }

    [Fact]
    public async Task Cancelling_a_request_ends_it_and_tells_the_server()
    {
        await _service.PrewarmAsync(Timeout);
        _service.OpenDocument(new DocumentSnapshot("a", 1, Text));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(
            _service.GetQuickInfoAsync(new DocumentVersion("a", 1), 13, cancellation.Token)
        );

        long hover = (long)_servers.SentOf("textDocument/hover").Single()["id"]!;
        await Wait.UntilAsync(() => _servers.SentOf("$/cancelRequest").Count == 1);
        ((long?)_servers.SentOf("$/cancelRequest").Single()["params"]?["id"]).ShouldBe(hover);
        (await _service.GetQuickInfoAsync(new DocumentVersion("a", 1), 1, Timeout)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_request_for_an_older_version_or_one_the_server_calls_stale_answers_nothing()
    {
        await _service.PrewarmAsync(Timeout);
        _service.OpenDocument(new DocumentSnapshot("a", 1, Text));
        // The toy answers ContentModified for "stale" once the document changes.
        Task<QuickInfo?> stale = _service.GetQuickInfoAsync(new DocumentVersion("a", 1), 18, Timeout);
        _service.ChangeDocument(new DocumentChange("a", 2, new TextChange(0, 0, "x")));

        (await stale).ShouldBeNull();
        (await _service.GetQuickInfoAsync(new DocumentVersion("a", 1), 1, Timeout)).ShouldBeNull();
    }

    [Fact]
    public async Task Completion_maps_edits_commit_characters_and_incompleteness_and_sends_the_lsp_trigger_kind()
    {
        await _service.PrewarmAsync(Timeout);
        _service.OpenDocument(new DocumentSnapshot("a", 1, Text));

        CompletionList? list = await _service.GetCompletionsAsync(
            new DocumentVersion("a", 1),
            2,
            new CompletionTrigger(CompletionTriggerKind.Insertion, 'a'),
            Timeout
        );
        await _service.GetCompletionsAsync(
            new DocumentVersion("a", 1),
            2,
            new CompletionTrigger(CompletionTriggerKind.IncompleteList, 'l'),
            Timeout
        );
        await _service.GetCompletionsAsync(
            new DocumentVersion("a", 1),
            2,
            new CompletionTrigger(CompletionTriggerKind.TriggerCharacter, '.'),
            Timeout
        );

        list.ShouldNotBeNull().IsIncomplete.ShouldBeTrue();
        list.ApplicableRange.ShouldBe(new TextRange(0, 5));
        CompletionItem alpha = list.Items[0];
        CompletionItem beta = list.Items[1];
        (alpha.DisplayText, alpha.InsertText, alpha.Kind).ShouldBe(("alpha", "alpha", CompletionItemKind.Local));
        alpha.CommitCharacters.ShouldBe(['.']);
        (beta.InsertText, beta.Kind).ShouldBe(("beta()", CompletionItemKind.Method));
        beta.AdditionalEdits.ShouldBe([new TextEdit(new TextRange(0, 0), "-- uses beta\n")]);
        // Typing a letter is Invoked; only the server's trigger characters are TriggerCharacter.
        _servers
            .SentOf("textDocument/completion")
            .Select(m =>
                ((int?)m["params"]?["context"]?["triggerKind"], (string?)m["params"]?["context"]?["triggerCharacter"])
            )
            .ShouldBe([(1, null), (3, null), (2, ".")]);
    }

    [Fact]
    public async Task Resolve_loads_the_description_with_the_items_data()
    {
        await _service.PrewarmAsync(Timeout);
        _service.OpenDocument(new DocumentSnapshot("a", 1, Text));
        CompletionList list = (
            await _service.GetCompletionsAsync(
                new DocumentVersion("a", 1),
                2,
                new CompletionTrigger(CompletionTriggerKind.Invoke),
                Timeout
            )
        ).ShouldNotBeNull();

        CompletionItemDetails? details = await _service.ResolveCompletionItemAsync(
            new CompletionItemReference("a", list.ResultId, 0),
            Timeout
        );

        details
            .ShouldNotBeNull()
            .Description.ShouldBe([
                new MarkupBlock(MarkupBlockKind.Code, "resolved 1"),
                new MarkupBlock(MarkupBlockKind.Paragraph, "alpha"),
            ]);
        _servers.SentOf("completionItem/resolve").Single()["params"]!["data"]!
            .ToJsonString()
            .ShouldBe("""{"id":1,"nested":[true,null]}""");
    }

    [Fact]
    public async Task Semantic_tokens_of_the_requested_range_are_classified_by_the_servers_legend()
    {
        await _service.PrewarmAsync(Timeout);
        _service.OpenDocument(new DocumentSnapshot("a", 1, Text));

        IReadOnlyList<ClassifiedRange> classified = await _service.GetClassificationsAsync(
            new DocumentVersion("a", 1),
            TextRange.FromBounds(23, 32),
            Timeout
        );

        // "add", "1" and "2" on the second line.
        classified.ShouldBe([
            new ClassifiedRange(new TextRange(23, 3), ClassificationKind.Local),
            new ClassifiedRange(new TextRange(27, 1), ClassificationKind.Constant),
            new ClassifiedRange(new TextRange(30, 1), ClassificationKind.Constant),
        ]);
    }

    [Fact]
    public async Task A_crashed_server_restarts_gets_the_documents_again_and_serves_again()
    {
        await _service.PrewarmAsync(Timeout);
        _service.OpenDocument(new DocumentSnapshot("a", 1, "alpha error\n"));
        (await _diagnostics.Reader.ReadAsync(Timeout)).Diagnostics.Count.ShouldBe(1);
        var states = Channel.CreateUnbounded<LanguageServerState>();
        _service.StateChanged += (_, _) => states.Writer.TryWrite(_service.State);
        var invalidated = new TaskCompletionSource();
        _service.Invalidated += (_, _) => invalidated.TrySetResult();

        await _servers.CrashAsync();

        (await states.Reader.ReadAsync(Timeout)).ShouldBe(LanguageServerState.Restarting);
        // The crash clears the diagnostics; the new server publishes them again.
        (await _diagnostics.Reader.ReadAsync(Timeout)).Diagnostics.ShouldBeEmpty();
        (await states.Reader.ReadAsync(Timeout)).ShouldBe(LanguageServerState.Running);
        (await _diagnostics.Reader.ReadAsync(Timeout)).Diagnostics.Count.ShouldBe(1);
        await invalidated.Task.WaitAsync(Timeout);
        _servers.Starts.ShouldBe(2);
        _servers.SentOf("textDocument/didOpen").Count.ShouldBe(2);
        (await _service.GetQuickInfoAsync(new DocumentVersion("a", 1), 1, Timeout))
            .ShouldNotBeNull()
            .Text.ShouldBe("Hi, alpha");
    }

    [Fact]
    public async Task Editor_calls_return_at_once_while_the_server_reads_nothing_and_queued_edits_go_out_as_one()
    {
        await _service.PrewarmAsync(Timeout);
        _servers.BlockWrites();
        string large = new('x', 1_000_000);

        var calls = Stopwatch.StartNew();
        _service.OpenDocument(new DocumentSnapshot("a", 1, large + "\n"));
        for (int i = 0; i < 200; i++)
            _service.ChangeDocument(new DocumentChange("a", i + 2, new TextChange(i, 0, "y")));
        calls.Stop();
        _servers.UnblockWrites();
        QuickInfo? info = await _service.GetQuickInfoAsync(new DocumentVersion("a", 201), 0, Timeout);

        calls.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(500));
        // The edits queued up behind the blocked didOpen: they went out in one didChange.
        _servers.SentOf("textDocument/didChange").Single()["params"]!["contentChanges"]!
            .AsArray()
            .Count.ShouldBe(200);
        info.ShouldNotBeNull().Range.ShouldBe(new TextRange(0, 1_000_200));
    }
}

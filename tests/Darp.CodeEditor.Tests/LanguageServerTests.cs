using System.Text.Json;
using System.Threading.Channels;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Lsp;
using Shouldly;

namespace Darp.CodeEditor.Tests;

/// <summary>The LSP adapter's contract with clients: text sync, completion shapes, diagnostics and cancellation.</summary>
public sealed class LanguageServerTests : IAsyncDisposable
{
    private const string Uri = "file:///test.csx";

    private readonly FakeService _service = new();
    private readonly Channel<JsonElement> _sent = Channel.CreateUnbounded<JsonElement>();
    private readonly LanguageServer _server;
    private int _nextId;

    public LanguageServerTests()
    {
        _server = new LanguageServer(
            Task.FromResult<ILanguageService>(_service),
            m => _sent.Writer.TryWrite(JsonDocument.Parse(m).RootElement)
        );
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    [Fact]
    public async Task Incremental_changes_reach_the_service_as_full_text()
    {
        await InitializeAsync(itemDefaults: true);
        Notify(
            "textDocument/didOpen",
            $$$"""{"textDocument":{"uri":"{{{Uri}}}","languageId":"csharp","version":1,"text":"abc\ndef"}}"""
        );
        Notify(
            "textDocument/didChange",
            $$$"""{"textDocument":{"uri":"{{{Uri}}}","version":2},"contentChanges":[{"range":{"start":{"line":1,"character":1},"end":{"line":1,"character":2}},"text":"XY"}]}"""
        );

        await RequestAsync(
            "textDocument/completion",
            $$$"""{"textDocument":{"uri":"{{{Uri}}}"},"position":{"line":1,"character":4}}"""
        );

        _service.LastCompletion.ShouldBe((new DocumentSnapshot(Uri, 2, "abc\ndXYf"), 8));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Completion_items_carry_their_edit_in_the_shape_the_client_supports(bool itemDefaults)
    {
        await InitializeAsync(itemDefaults);
        Notify(
            "textDocument/didOpen",
            $$$"""{"textDocument":{"uri":"{{{Uri}}}","languageId":"csharp","version":1,"text":"x = Dev"}}"""
        );

        JsonElement result = (
            await RequestAsync(
                "textDocument/completion",
                $$$"""{"textDocument":{"uri":"{{{Uri}}}"},"position":{"line":0,"character":7}}"""
            )
        ).GetProperty("result");

        JsonElement item = result.GetProperty("items")[0];
        item.GetProperty("label").GetString().ShouldBe("Device");
        const string range = """{"start":{"line":0,"character":4},"end":{"line":0,"character":7}}""";
        if (itemDefaults)
        {
            result.GetProperty("itemDefaults").GetProperty("editRange").GetRawText().ShouldBe(range);
            item.GetProperty("textEditText").GetString().ShouldBe("Device");
        }
        else
        {
            item.GetProperty("textEdit").GetProperty("range").GetRawText().ShouldBe(range);
            item.GetProperty("textEdit").GetProperty("newText").GetString().ShouldBe("Device");
        }
    }

    [Fact]
    public async Task Diagnostics_are_published_once_for_the_latest_version()
    {
        await InitializeAsync(itemDefaults: true);
        Notify(
            "textDocument/didOpen",
            $$$"""{"textDocument":{"uri":"{{{Uri}}}","languageId":"csharp","version":1,"text":"a"}}"""
        );
        Notify(
            "textDocument/didChange",
            $$$"""{"textDocument":{"uri":"{{{Uri}}}","version":2},"contentChanges":[{"text":"ab"}]}"""
        );
        Notify(
            "textDocument/didChange",
            $$$"""{"textDocument":{"uri":"{{{Uri}}}","version":3},"contentChanges":[{"text":"abc"}]}"""
        );

        JsonElement published = await NextAsync(m =>
            m.TryGetProperty("method", out JsonElement method)
            && method.GetString() == "textDocument/publishDiagnostics"
        );

        published.GetProperty("params").GetProperty("version").GetInt32().ShouldBe(3);
        _service.DiagnosedVersions.ShouldBe([3]);
    }

    [Fact]
    public async Task A_cancelled_request_is_answered_with_RequestCancelled()
    {
        await InitializeAsync(itemDefaults: true);
        Notify(
            "textDocument/didOpen",
            $$$"""{"textDocument":{"uri":"{{{Uri}}}","languageId":"csharp","version":1,"text":"slow"}}"""
        );
        _service.BlockCompletion = true;

        Task<JsonElement> response = RequestAsync(
            "textDocument/completion",
            $$$"""{"textDocument":{"uri":"{{{Uri}}}"},"position":{"line":0,"character":4}}"""
        );
        Notify("$/cancelRequest", $$$"""{"id":{{{_nextId}}}}""");

        (await response).GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32800);
    }

    private Task<JsonElement> InitializeAsync(bool itemDefaults) =>
        RequestAsync(
            "initialize",
            itemDefaults
                ? """{"capabilities":{"textDocument":{"completion":{"completionList":{"itemDefaults":["editRange","commitCharacters"]}}}}}"""
                : """{"capabilities":{}}"""
        );

    private void Notify(string method, string parameters) =>
        _server.Receive($$$"""{"jsonrpc":"2.0","method":"{{{method}}}","params":{{{parameters}}}}""");

    private Task<JsonElement> RequestAsync(string method, string parameters)
    {
        int id = ++_nextId;
        _server.Receive($$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"{{{method}}}","params":{{{parameters}}}}""");
        return NextAsync(m => m.TryGetProperty("id", out JsonElement value) && value.GetInt32() == id);
    }

    private async Task<JsonElement> NextAsync(Func<JsonElement, bool> match)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            JsonElement message = await _sent.Reader.ReadAsync(timeout.Token);
            if (match(message))
                return message;
        }
    }

    private sealed class FakeService : ILanguageService
    {
        public (DocumentSnapshot Document, int Position)? LastCompletion { get; private set; }

        public List<int> DiagnosedVersions { get; } = [];

        public bool BlockCompletion { get; set; }

        public LanguageServiceCapabilities Capabilities { get; } = new(['.'], [';'], ['(']);

        public async Task<CompletionList?> GetCompletionsAsync(
            DocumentSnapshot document,
            int position,
            CompletionTrigger trigger,
            CancellationToken cancellationToken
        )
        {
            if (BlockCompletion)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            LastCompletion = (document, position);
            int start = document.Text.LastIndexOf(' ') + 1;
            return new CompletionList(
                1,
                TextRange.FromBounds(start, position),
                [new CompletionItem("Device", "Device", "Device", CompletionItemKind.Property)]
            );
        }

        public Task<string?> GetCompletionDescriptionAsync(
            CompletionItemReference item,
            CancellationToken cancellationToken
        ) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(
            DocumentSnapshot document,
            CancellationToken cancellationToken
        )
        {
            lock (DiagnosedVersions)
                DiagnosedVersions.Add(document.Version);
            return Task.FromResult<IReadOnlyList<DiagnosticInfo>>([]);
        }

        public Task<IReadOnlyList<ClassifiedRange>> GetClassificationsAsync(
            DocumentSnapshot document,
            TextRange range,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<ClassifiedRange>>([]);

        public Task<QuickInfo?> GetQuickInfoAsync(
            DocumentSnapshot document,
            int position,
            CancellationToken cancellationToken
        ) => Task.FromResult<QuickInfo?>(null);

        public Task<SignatureHelp?> GetSignatureHelpAsync(
            DocumentSnapshot document,
            int position,
            CancellationToken cancellationToken
        ) => Task.FromResult<SignatureHelp?>(null);
    }
}

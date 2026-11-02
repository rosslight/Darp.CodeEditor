using System.Text.Json;
using System.Text.Json.Nodes;
using Darp.CodeEditor.Lsp.Protocol;
using Shouldly;

namespace Darp.CodeEditor.Lsp.Tests;

/// <summary>The JSON of the protocol types: union forms, verbatim data and unknown members.</summary>
public sealed class ProtocolTests
{
    private static LspJsonContext Json => LspJsonContext.Default;

    [Fact]
    public void A_completion_result_is_read_as_a_list_or_as_an_array_of_items()
    {
        CompletionList? list = JsonSerializer.Deserialize(
            """{"isIncomplete":true,"items":[{"label":"a"}],"unknown":1}""",
            Json.CompletionList
        );
        CompletionList? array = JsonSerializer.Deserialize("""[{"label":"b"}]""", Json.CompletionList);

        list.ShouldNotBeNull();
        array.ShouldNotBeNull();
        (list.IsIncomplete, list.Items.Single().Label).ShouldBe((true, "a"));
        (array.IsIncomplete, array.Items.Single().Label).ShouldBe((false, "b"));
    }

    [Fact]
    public void Completion_items_keep_their_data_and_unknown_members_and_add_nothing()
    {
        const string sent = """
            {"label":"print","kind":3,"documentation":"Prints.","data":{"id":[1,null,"x"]},
             "command":{"title":"","command":"editor.action.triggerParameterHints"}}
            """;

        CompletionItem item = JsonSerializer.Deserialize(sent, Json.CompletionItem) ?? throw new InvalidDataException();
        JsonObject written = Write(item, Json.CompletionItem);

        item.Documentation.ShouldBe(new MarkupContent(MarkupKind.PlainText, "Prints."));
        written["data"].ShouldNotBeNull().ToJsonString().ShouldBe("""{"id":[1,null,"x"]}""");
        ((string?)written["command"]?["command"]).ShouldBe("editor.action.triggerParameterHints");
        written.Select(p => p.Key).Order().ShouldBe(["command", "data", "documentation", "kind", "label"]);
    }

    [Fact]
    public void Completion_edits_read_and_write_both_forms()
    {
        var range = new DocumentRange(new Position(0, 1), new Position(0, 3));
        var insert = new DocumentRange(new Position(0, 1), new Position(0, 2));
        const string textEdit =
            """{"newText":"ab","range":{"start":{"line":0,"character":1},"end":{"line":0,"character":3}}}""";
        const string insertReplace = """
            {"newText":"ab","insert":{"start":{"line":0,"character":1},"end":{"line":0,"character":2}},
             "replace":{"start":{"line":0,"character":1},"end":{"line":0,"character":3}}}
            """;

        CompletionTextEdit plain = Read($$"""{"label":"a","textEdit":{{textEdit}}}""").TextEdit ?? default;
        CompletionTextEdit split = Read($$"""{"label":"a","textEdit":{{insertReplace}}}""").TextEdit ?? default;

        plain.ShouldBe(new CompletionTextEdit("ab", range));
        split.ShouldBe(new CompletionTextEdit("ab", insert, range));
        Write(new CompletionItem("a", TextEdit: plain), Json.CompletionItem)["textEdit"]
            .ShouldNotBeNull()
            .ToJsonString()
            .ShouldBe(textEdit);
        Write(new CompletionItem("a", TextEdit: split), Json.CompletionItem)["textEdit"]
            .ShouldNotBeNull()
            .AsObject()
            .Select(p => p.Key)
            .ShouldBe(["newText", "insert", "replace"]);
    }

    [Fact]
    public void The_default_edit_range_reads_a_range_or_insert_and_replace_ranges()
    {
        CompletionList? plain = JsonSerializer.Deserialize(
            """{"isIncomplete":false,"itemDefaults":{"editRange":{"start":{"line":1,"character":0},"end":{"line":1,"character":4}}},"items":[]}""",
            Json.CompletionList
        );
        CompletionList? split = JsonSerializer.Deserialize(
            """
            {"isIncomplete":false,"items":[],"itemDefaults":{"editRange":{
              "insert":{"start":{"line":1,"character":0},"end":{"line":1,"character":2}},
              "replace":{"start":{"line":1,"character":0},"end":{"line":1,"character":4}}}}}
            """,
            Json.CompletionList
        );

        plain
            .ShouldNotBeNull()
            .ItemDefaults.ShouldNotBeNull()
            .EditRange.ShouldBe(new CompletionEditRange(new DocumentRange(new(1, 0), new(1, 4))));
        split
            .ShouldNotBeNull()
            .ItemDefaults.ShouldNotBeNull()
            .EditRange.ShouldBe(
                new CompletionEditRange(
                    new DocumentRange(new(1, 0), new(1, 2)),
                    new DocumentRange(new(1, 0), new(1, 4))
                )
            );
    }

    [Theory]
    [InlineData("""{"kind":"plaintext","value":"x"}""", "plaintext")]
    [InlineData("\"**x**\"", "markdown")]
    public void Hover_contents_are_read_from_markup_content_or_a_plain_string(string contents, string kind)
    {
        Hover? hover = JsonSerializer.Deserialize($$"""{"contents":{{contents}}}""", Json.Hover);

        hover.ShouldNotBeNull().Contents.Kind.ShouldBe(kind);
    }

    [Fact]
    public void Parameter_labels_round_trip_as_text_or_offsets()
    {
        const string sent = """{"signatures":[{"label":"f(a, b)","parameters":[{"label":"a"},{"label":[5,6]}]}]}""";

        SignatureHelp help = JsonSerializer.Deserialize(sent, Json.SignatureHelp) ?? throw new InvalidDataException();

        help.Signatures[0]
            .Parameters.ShouldNotBeNull()
            .Select(p => p.Label)
            .ShouldBe([new ParameterLabel("a"), new ParameterLabel(5, 6)]);
        JsonSerializer.Serialize(help, Json.SignatureHelp).ShouldBe(sent);
    }

    [Fact]
    public void Server_capabilities_accept_booleans_options_and_sync_kinds()
    {
        const string sent = """
            {"capabilities":{"textDocumentSync":2,"hoverProvider":{"workDoneProgress":true},
             "semanticTokensProvider":{"legend":{"tokenTypes":["type"],"tokenModifiers":[]},"range":true,"full":{"delta":true}},
             "unknownProvider":true}}
            """;

        ServerCapabilities capabilities = (
            JsonSerializer.Deserialize(sent, Json.InitializeResult) ?? throw new InvalidDataException()
        ).Capabilities;

        capabilities.TextDocumentSync.ShouldBe(
            new TextDocumentSyncOptions(OpenClose: true, Change: TextDocumentSyncKind.Incremental)
        );
        capabilities.HoverProvider.ShouldBe(true);
        SemanticTokensOptions semanticTokens = capabilities.SemanticTokensProvider.ShouldNotBeNull();
        (semanticTokens.Range, semanticTokens.Full).ShouldBe((true, true));
    }

    [Fact]
    public void Diagnostic_codes_are_read_from_numbers_and_strings()
    {
        PublishDiagnosticsParams? diagnostics = JsonSerializer.Deserialize(
            """
            {"uri":"file:///a%20b.lua","version":3,"diagnostics":[
              {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"message":"m","code":1001},
              {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"message":"m","code":"CS0103"}]}
            """,
            Json.PublishDiagnosticsParams
        );

        diagnostics.ShouldNotBeNull().Uri.ShouldBe("file:///a%20b.lua");
        diagnostics.Diagnostics.Select(d => d.Code).ShouldBe(["1001", "CS0103"]);
    }

    [Fact]
    public void Requests_a_server_sends_its_client_read_as_luau_lsp_sends_them()
    {
        ConfigurationParams? configuration = JsonSerializer.Deserialize(
            """{"items":[{"section":"luau-lsp"},{"scopeUri":"file:///a.luau","section":"files"}]}""",
            Json.ConfigurationParams
        );
        RegistrationParams? registration = JsonSerializer.Deserialize(
            """{"registrations":[{"id":"1","method":"workspace/didChangeWatchedFiles","registerOptions":{"watchers":[]}}]}""",
            Json.RegistrationParams
        );
        UnregistrationParams? unregistration = JsonSerializer.Deserialize(
            """{"unregisterations":[{"id":"1","method":"workspace/didChangeWatchedFiles"}]}""",
            Json.UnregistrationParams
        );
        WorkDoneProgressCreateParams? progress = JsonSerializer.Deserialize(
            """{"token":"luau-lsp/indexing"}""",
            Json.WorkDoneProgressCreateParams
        );

        configuration
            .ShouldNotBeNull()
            .Items.ShouldBe([
                new ConfigurationItem(Section: "luau-lsp"),
                new ConfigurationItem("file:///a.luau", "files"),
            ]);
        registration.ShouldNotBeNull().Registrations.Single().Method.ShouldBe("workspace/didChangeWatchedFiles");
        unregistration.ShouldNotBeNull().Unregistrations.Single().Id.ShouldBe("1");
        progress.ShouldNotBeNull().Token.GetString().ShouldBe("luau-lsp/indexing");
    }

    [Fact]
    public void A_configuration_answer_has_one_value_per_item_and_writes_missing_ones_as_null()
    {
        IReadOnlyList<JsonElement?> settings = [null, TestMethods.Json("""{"greeting":"Hi"}""")];

        JsonSerializer
            .Serialize(settings, Json.IReadOnlyListNullableJsonElement)
            .ShouldBe("""[null,{"greeting":"Hi"}]""");
    }

    [Fact]
    public void Members_that_LSP_requires_must_be_present_and_not_null()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize("""{"kind":1}""", Json.CompletionItem));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize("""{"label":null}""", Json.CompletionItem));
        Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize("""{"textDocument":{"uri":"file:///a"}}""", Json.HoverParams)
        );
    }

    private static CompletionItem Read(string json) =>
        JsonSerializer.Deserialize(json, Json.CompletionItem) ?? throw new InvalidDataException();

    private static JsonObject Write<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonNode.Parse(JsonSerializer.Serialize(value, typeInfo))?.AsObject() ?? throw new InvalidDataException();
}

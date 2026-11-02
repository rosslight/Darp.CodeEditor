using System.Text.Json.Nodes;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.CSharp.Scripting;
using Darp.CodeEditor.LspTesting;
using Darp.CodeEditor.Samples;
using Microsoft.CodeAnalysis;
using Shouldly;

namespace Darp.CodeEditor.CSharp.Server.Tests;

/// <summary>Drives the real server process over stdio, as an editor does.</summary>
public sealed class LspLifecycleTests
{
    [Fact]
    public async Task Initialize_announces_the_features_and_shutdown_then_exit_ends_with_code_0()
    {
        await using LspClient client = TestServer.Start();

        JsonNode? result = await client.RequestAsync(
            "initialize",
            TestServer.InitializeParams(TestEnvironments.Device)
        );
        await client.NotifyAsync("initialized", new JsonObject());

        JsonNode capabilities = result.Get("capabilities");
        capabilities.GetInt("textDocumentSync", "change").ShouldBe(2); // incremental
        capabilities.Get("completionProvider", "resolveProvider").GetValue<bool>().ShouldBeTrue();
        capabilities.Get("hoverProvider").GetValue<bool>().ShouldBeTrue();
        capabilities
            .GetArray("signatureHelpProvider", "triggerCharacters")
            .Select(c => c?.GetValue<string>())
            .ShouldBe(["(", ","]);
        capabilities.Get("semanticTokensProvider", "range").GetValue<bool>().ShouldBeTrue();
        result.GetString("serverInfo", "name").ShouldBe("Darp.CodeEditor.CSharp.Server");
        (await client.ShutdownAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Requests_before_initialize_fail_and_exit_without_shutdown_ends_with_code_1()
    {
        await using LspClient client = TestServer.Start();

        LspException error = await Should.ThrowAsync<LspException>(() =>
            client.RequestAsync("textDocument/hover", TestServer.AtEnd("file:///a.csx", ""))
        );
        await client.NotifyAsync("exit", null);

        error.Code.ShouldBe(-32002); // ServerNotInitialized
        (await client.WaitForExitAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task The_server_exits_when_the_client_process_exits_while_its_input_stays_open()
    {
        // Stands in for the client process: one the test can end while it keeps the server's input open itself.
        await using LspClient standIn = TestServer.Start();
        await using LspClient client = TestServer.Start();
        JsonObject parameters = TestServer.InitializeParams(TestEnvironments.Device);
        parameters["processId"] = standIn.Process.Id;
        await client.RequestAsync("initialize", parameters);
        await client.NotifyAsync("initialized", new JsonObject());

        standIn.Process.Kill();

        (await client.WaitForExitAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_globals_type_that_does_not_exist_is_reported_to_the_user()
    {
        await using LspClient client = await TestServer.StartAsync(
            TestEnvironments.Device with
            {
                Globals = new CSharpScriptGlobals(typeof(TestServer).Assembly.Location, "Missing.Globals"),
            }
        );

        JsonObject message = await client.WaitForNotificationAsync("window/showMessage");

        message.GetInt("params", "type").ShouldBe(1); // Error
        message.GetString("params", "message").ShouldContain("Missing.Globals");
    }

    [Fact]
    public async Task Initialize_fails_for_an_unknown_environment_version()
    {
        await using LspClient client = TestServer.Start();
        JsonObject parameters = TestServer.InitializeParams(TestEnvironments.Device);
        parameters["initializationOptions"] = new JsonObject { ["csharpScript"] = new JsonObject { ["version"] = 2 } };

        LspException error = await Should.ThrowAsync<LspException>(() => client.RequestAsync("initialize", parameters));

        error.Message.ShouldContain("\"version\": 1");
    }
}

public sealed class LspCompletionTests
{
    private const string Uri = "file:///scripts/completion.csx";

    [Fact]
    public async Task Member_completion_lists_globals_members_and_resolves_their_documentation()
    {
        await using LspClient client = await TestServer.StartAsync();
        const string text = "var t = Device.";
        await client.OpenAsync(Uri, text);

        JsonObject parameters = TestServer.AtEnd(Uri, text);
        parameters["context"] = new JsonObject { ["triggerKind"] = 2, ["triggerCharacter"] = "." };
        JsonNode? list = await client.RequestAsync("textDocument/completion", parameters);
        JsonNode item = Item(list, "ReadTemperature");
        JsonNode? resolved = await client.RequestAsync("completionItem/resolve", item.DeepClone());

        item.GetInt("kind").ShouldBe(2); // Method
        list.Get("isIncomplete").GetValue<bool>().ShouldBeFalse();
        list.Get("itemDefaults", "editRange")
            .ToJsonString()
            .ShouldBe(TestServer.Range(text, text.Length, text.Length).ToJsonString());
        resolved.GetString("documentation", "kind").ShouldBe("markdown");
        resolved
            .GetString("documentation", "value")
            .ShouldBe(
                "```csharp\ndouble Darp.CodeEditor.CSharp.TestGlobals.TestDevice.ReadTemperature()\n```\n\nReads the current temperature in degrees Celsius."
            );
    }

    [Fact]
    public async Task Word_completion_offers_types_of_other_namespaces_and_adds_their_using_on_resolve()
    {
        await using LspClient client = await TestServer.StartAsync();
        const string text = "var x = 1;\nvar s = new StringB";
        await client.OpenAsync(Uri, text);

        JsonNode? list = await client.RequestAsync("textDocument/completion", TestServer.AtEnd(Uri, text));
        JsonNode item = Item(list, "StringBuilder");
        JsonNode? resolved = await client.RequestAsync("completionItem/resolve", item.DeepClone());

        list.Get("itemDefaults", "editRange")
            .ToJsonString()
            .ShouldBe(TestServer.Range(text, text.Length - "StringB".Length, text.Length).ToJsonString());
        item.GetString("labelDetails", "description").ShouldBe("System.Text");
        JsonNode edit = resolved.GetArray("additionalTextEdits").ShouldHaveSingleItem().ShouldNotBeNull();
        edit.GetString("newText").ShouldBe("using System.Text;\n\n");
        edit.Get("range").ToJsonString().ShouldBe(TestServer.Range(text, 0, 0).ToJsonString());
    }

    [Fact]
    public async Task Long_word_lists_keep_the_best_matches_and_ask_for_more_letters()
    {
        await using LspClient client = await TestServer.StartAsync();
        const string text = "var x = 1;\nD";
        await client.OpenAsync(Uri, text);

        JsonNode? first = await client.RequestAsync("textDocument/completion", TestServer.AtEnd(Uri, text));
        await client.ChangeAsync(Uri, 2, TestServer.Range(text, text.Length, text.Length), "ev");
        JsonObject again = TestServer.AtEnd(Uri, text + "ev");
        again["context"] = new JsonObject { ["triggerKind"] = 3 }; // TriggerForIncompleteCompletions
        JsonNode? second = await client.RequestAsync("textDocument/completion", again);

        // The list has over 1,000 items, so the server keeps those that match "D", at most 1,000, and the client
        // asks again as the word grows.
        first.Get("isIncomplete").GetValue<bool>().ShouldBeTrue();
        first.GetArray("items").Count.ShouldBeLessThanOrEqualTo(1000);
        // Kept items match as the editor's own filter matches: prefix, word parts ("DebuggerVisualizer") or substring.
        Labels(first).ShouldAllBe(label => CompletionFilter.Score(label, "D") > 0);
        Labels(second).Length.ShouldBeLessThan(Labels(first).Length);
        Labels(second).ShouldAllBe(label => CompletionFilter.Score(label, "Dev") > 0);
        Labels(second).ShouldContain("Device");
    }

    [Fact]
    public async Task Resolving_an_item_keeps_the_edits_of_its_list_after_another_document_completed()
    {
        // Roslyn shares the items of types from other namespaces between lists and moves them to the newest list.
        await using LspClient client = await TestServer.StartAsync();
        const string first = "var s = new StringB";
        const string second =
            "// A longer script, so its list is further into the text.\nvar x = 1;\nvar t = new StringB";
        await client.OpenAsync(Uri, first);
        await client.OpenAsync("file:///scripts/other.csx", second);

        JsonNode? list = await client.RequestAsync("textDocument/completion", TestServer.AtEnd(Uri, first));
        await client.RequestAsync("textDocument/completion", TestServer.AtEnd("file:///scripts/other.csx", second));
        JsonNode? resolved = await client.RequestAsync(
            "completionItem/resolve",
            Item(list, "StringBuilder").DeepClone()
        );

        JsonNode edit = resolved.GetArray("additionalTextEdits").ShouldHaveSingleItem().ShouldNotBeNull();
        edit.GetString("newText").ShouldStartWith("using System.Text;");
        edit.Get("range").ToJsonString().ShouldBe(TestServer.Range(first, 0, 0).ToJsonString());
    }

    [Fact]
    public async Task Snippets_expand_on_resolve_for_clients_that_resolve_text_edits()
    {
        const string text = "var x = 1;\nfo";
        JsonObject capabilities = TestServer.FullCapabilities();
        capabilities
            .GetArray("textDocument", "completion", "completionItem", "resolveSupport", "properties")
            .Add("textEdit");
        await using LspClient resolving = await TestServer.StartAsync(capabilities: capabilities);
        await using LspClient other = await TestServer.StartAsync();
        await resolving.OpenAsync(Uri, text);
        await other.OpenAsync(Uri, text);

        JsonNode? list = await resolving.RequestAsync("textDocument/completion", TestServer.AtEnd(Uri, text));
        JsonNode? otherList = await other.RequestAsync("textDocument/completion", TestServer.AtEnd(Uri, text));
        JsonNode snippet = list.GetArray("items")
            .Single(i => i.GetString("label") == "for" && i.GetInt("kind") == 15)
            .ShouldNotBeNull();
        JsonNode? resolved = await resolving.RequestAsync("completionItem/resolve", snippet.DeepClone());

        string newText = resolved.GetString("textEdit", "newText");
        resolved.GetInt("insertTextFormat").ShouldBe(2); // Snippet
        newText.ShouldStartWith("for (int i = 0; i < length; i++)");
        newText.ShouldContain("$0");
        resolved
            .Get("textEdit", "range")
            .ToJsonString()
            .ShouldBe(TestServer.Range(text, text.Length - 2, text.Length).ToJsonString());
        otherList.GetArray("items").Select(i => i.GetInt("kind")).ShouldNotContain(15);
    }

    [Fact]
    public async Task Clients_without_item_defaults_get_a_text_edit_per_item()
    {
        JsonObject capabilities = TestServer.FullCapabilities();
        capabilities.Get("textDocument", "completion").AsObject().Remove("completionList").ShouldBeTrue();
        await using LspClient client = await TestServer.StartAsync(capabilities: capabilities);
        const string text = "var t = Device.Rea";
        await client.OpenAsync(Uri, text);

        JsonNode? list = await client.RequestAsync("textDocument/completion", TestServer.AtEnd(Uri, text));

        list.ShouldNotBeNull()["itemDefaults"].ShouldBeNull();
        JsonNode item = Item(list, "ReadAsync");
        item.GetString("textEdit", "newText").ShouldBe("ReadAsync");
        item.Get("textEdit", "range")
            .ToJsonString()
            .ShouldBe(TestServer.Range(text, text.Length - 3, text.Length).ToJsonString());
    }

    /// <summary>The texts the client filters the items by.</summary>
    private static string[] Labels(JsonNode? list) => [.. list.GetArray("items").Select(FilterText)];

    /// <summary>The text the client filters an item by: its filter text, or else its label.</summary>
    private static string FilterText(JsonNode? item) =>
        item.ShouldNotBeNull()["filterText"]?.GetValue<string>() ?? item.GetString("label");

    private static JsonNode Item(JsonNode? list, string label) =>
        list.GetArray("items").Single(i => i.GetString("label") == label).ShouldNotBeNull();
}

public sealed class LspLanguageFeatureTests
{
    private const string Uri = "file:///scripts/features.csx";

    [Fact]
    public async Task Hover_shows_the_signature_as_csharp_and_then_the_documentation()
    {
        await using LspClient client = await TestServer.StartAsync();
        const string text = "var t = Device.ReadTemperature();";
        await client.OpenAsync(Uri, text);

        JsonNode? hover = await client.RequestAsync(
            "textDocument/hover",
            TestServer.At(Uri, text, "ReadTemperature", 2)
        );

        hover.GetString("contents", "kind").ShouldBe("markdown");
        hover
            .GetString("contents", "value")
            .ShouldBe(
                "```csharp\ndouble Darp.CodeEditor.CSharp.TestGlobals.TestDevice.ReadTemperature()\n```\n\nReads the current temperature in degrees Celsius."
            );
        int start = text.IndexOf("ReadTemperature", StringComparison.Ordinal);
        hover
            .Get("range")
            .ToJsonString()
            .ShouldBe(TestServer.Range(text, start, start + "ReadTemperature".Length).ToJsonString());
    }

    [Fact]
    public async Task Signature_help_selects_the_overload_that_fits_the_typed_arguments()
    {
        await using LspClient client = await TestServer.StartAsync();
        const string text = "Log(\"{0}\", 1, ";
        await client.OpenAsync(Uri, text);

        JsonNode? help = await client.RequestAsync("textDocument/signatureHelp", TestServer.AtEnd(Uri, text));

        help.GetArray("signatures").Count.ShouldBe(3);
        JsonNode active = help.Get("signatures", help.GetInt("activeSignature"));
        active.GetString("label").ShouldBe("void DeviceGlobals.Log(string format, params object[] args)");
        help.GetInt("activeParameter").ShouldBe(1); // the params array takes every further argument
        active.GetString("documentation", "value").ShouldBe("Writes a formatted message to the script log.");
    }

    [Fact]
    public async Task Signature_help_follows_named_arguments()
    {
        await using LspClient client = await TestServer.StartAsync();
        const string text = "var block = await Device.ReadAsync(length: ";
        await client.OpenAsync(Uri, text);

        JsonNode? help = await client.RequestAsync("textDocument/signatureHelp", TestServer.AtEnd(Uri, text));

        JsonNode signature = help.GetArray("signatures").ShouldHaveSingleItem().ShouldNotBeNull();
        string label = signature.GetString("label");
        label.ShouldBe("Task<byte[]> TestDevice.ReadAsync(int register, int length)");
        signature.GetInt("activeParameter").ShouldBe(1);
        JsonNode parameter = signature.Get("parameters", 1);
        label[parameter.GetInt("label", 0)..parameter.GetInt("label", 1)].ShouldBe("int length");
        parameter.GetString("documentation", "value").ShouldBe("How many bytes to read.");
    }

    [Fact]
    public async Task Semantic_tokens_classify_the_requested_range()
    {
        await using LspClient client = TestServer.Start();
        JsonNode? initialized = await client.RequestAsync(
            "initialize",
            TestServer.InitializeParams(TestEnvironments.Device)
        );
        await client.NotifyAsync("initialized", new JsonObject());
        const string text = "var t = Device.ReadTemperature(); // comment\nLog(\"x\");";
        await client.OpenAsync(Uri, text);

        JsonObject parameters = new()
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri },
            ["range"] = TestServer.Range(text, 0, text.IndexOf('\n', StringComparison.Ordinal)),
        };
        JsonNode? tokens = await client.RequestAsync("textDocument/semanticTokens/range", parameters);

        string[] legend =
        [
            .. initialized
                .GetArray("capabilities", "semanticTokensProvider", "legend", "tokenTypes")
                .Select(t => t.ShouldNotBeNull().GetValue<string>()),
        ];
        uint[] data = [.. tokens.GetArray("data").Select(n => n.ShouldNotBeNull().GetValue<uint>())];
        var decoded = new List<(string Text, string Type)>();
        int line = 0;
        int character = 0;
        for (int i = 0; i < data.Length; i += 5)
        {
            line += (int)data[i];
            character = data[i] == 0 ? character + (int)data[i + 1] : (int)data[i + 1];
            string lineText = text.Split('\n')[line];
            decoded.Add((lineText.Substring(character, (int)data[i + 2]), legend[data[i + 3]]));
        }
        decoded.ShouldBe([
            ("var", "keyword"),
            ("t", "field"), // top-level script variables are fields of the script class
            ("Device", "property"),
            ("ReadTemperature", "method"),
            ("// comment", "comment"),
        ]);
    }
}

public sealed class LspDocumentSyncTests
{
    private const string DeviceHover = "```csharp\nDarp.CodeEditor.CSharp.TestGlobals.TestDevice Device { get; }";

    [Fact]
    public async Task Diagnostics_are_published_for_the_newest_version_after_incremental_changes()
    {
        const string uri = "file:///scripts/sync.csx";
        await using LspClient client = await TestServer.StartAsync();
        const string v1 = "int x = \"one\";\n";

        await client.OpenAsync(uri, v1);
        // Fix the error, then append a line: two incremental changes before the diagnostics delay ends.
        int quote = v1.IndexOf('"', StringComparison.Ordinal);
        await client.ChangeAsync(uri, 2, TestServer.Range(v1, quote, quote + "\"one\"".Length), "1");
        const string v2 = "int x = 1;\n";
        await client.ChangeAsync(uri, 3, TestServer.Range(v2, v2.Length, v2.Length), "Log(x);\n");
        JsonObject first = await client.WaitForNotificationAsync("textDocument/publishDiagnostics");

        first.GetInt("params", "version").ShouldBe(3);
        first.GetArray("params", "diagnostics").ShouldBeEmpty();

        // Break the second line: "Log(x);" becomes "Log(x, );".
        const string v3 = "int x = 1;\nLog(x);\n";
        int argumentEnd = v3.IndexOf("x)", StringComparison.Ordinal) + 1;
        await client.ChangeAsync(uri, 4, TestServer.Range(v3, argumentEnd, argumentEnd), ", ");
        JsonObject second = await client.WaitForNotificationAsync("textDocument/publishDiagnostics");

        second.GetInt("params", "version").ShouldBe(4);
        JsonNode diagnostic = second.GetArray("params", "diagnostics").ShouldHaveSingleItem().ShouldNotBeNull();
        diagnostic.GetString("code").ShouldBe("CS1525");
        diagnostic
            .Get("range", "start")
            .ToJsonString()
            .ShouldBe(new JsonObject { ["line"] = 1, ["character"] = 7 }.ToJsonString());
    }

    [Fact]
    public async Task Changes_for_the_current_or_an_older_version_change_nothing_and_cancel_nothing()
    {
        const string uri = "file:///scripts/versions.csx";
        await using LspClient client = await TestServer.StartAsync();
        const string text = "var t = Device.ReadTemperature();";
        await client.OpenAsync(uri, text, version: 2);

        // The environment is still loading, so the hover is pending while the changes arrive.
        PendingRequest hover = client.Send("textDocument/hover", TestServer.At(uri, text, "Device"));
        await client.ChangeAsync(uri, 2, TestServer.Range(text, 0, 0), "// the same version\n");
        await client.ChangeAsync(uri, 1, TestServer.Range(text, 0, 0), "// an older version\n");
        LspResponse response = await hover.Response;
        JsonNode? again = await client.RequestAsync("textDocument/hover", TestServer.At(uri, text, "Device"));
        JsonObject published = await client.WaitForNotificationAsync("textDocument/publishDiagnostics");

        response.Error.ShouldBeNull();
        response.Result.GetString("contents", "value").ShouldStartWith(DeviceHover);
        // The text is unchanged: the same position still shows Device, and diagnostics are for version 2.
        again.GetString("contents", "value").ShouldStartWith(DeviceHover);
        published.GetInt("params", "version").ShouldBe(2);
    }

    [Fact]
    public async Task Closing_a_document_cancels_its_pending_completion()
    {
        const string uri = "file:///scripts/closed.csx";
        await using LspClient client = await TestServer.StartAsync();
        const string text = "var t = Device.";
        await client.OpenAsync(uri, text);

        // The environment is still loading, so the completion is pending when the document closes.
        PendingRequest completion = client.Send("textDocument/completion", TestServer.AtEnd(uri, text));
        await client.NotifyAsync(
            "textDocument/didClose",
            new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }
        );
        LspResponse response = await completion.Response;

        response.Error.ShouldNotBeNull().Code.ShouldBe(-32800); // RequestCancelled
    }

    [Fact]
    public async Task Diagnostics_of_the_sample_match_the_host_compilation_and_closing_clears_them()
    {
        // Escaped characters must come back as the client sent them.
        const string uri = "file:///c%3A/scripts/my%20sample.csx";
        await using LspClient client = await TestServer.StartAsync(TestEnvironments.Sample);
        string text = SampleDocuments.CSharpScript;

        await client.OpenAsync(uri, text);
        JsonObject published = await client.WaitForNotificationAsync("textDocument/publishDiagnostics");
        await client.NotifyAsync(
            "textDocument/didClose",
            new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }
        );
        JsonObject cleared = await client.WaitForNotificationAsync("textDocument/publishDiagnostics");

        ResolvedScriptEnvironment environment = ResolvedScriptEnvironment.Resolve(TestEnvironments.Sample);
        string[] expected =
        [
            .. environment
                .CreateScript(text, typeof(Darp.CodeEditor.Demo.ScriptGlobals), environment.CreateScriptOptions())
                .GetCompilation()
                .GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(d => d.Severity != DiagnosticSeverity.Hidden)
                .Select(d =>
                    $"{d.Id} {TestServer.Range(text, d.Location.SourceSpan.Start, d.Location.SourceSpan.End).ToJsonString()}"
                ),
        ];
        published.GetString("params", "uri").ShouldBe(uri);
        published
            .GetArray("params", "diagnostics")
            .Select(d => $"{d.GetString("code")} {d.Get("range").ToJsonString()}")
            .ShouldBe(expected);
        expected.ShouldNotBeEmpty();
        cleared.GetString("params", "uri").ShouldBe(uri);
        cleared.GetArray("params", "diagnostics").ShouldBeEmpty();
    }

    [Fact(
        Skip = "EmmyLua.LanguageServer.Framework 0.9.2 reads every $/cancelRequest id as 0 (CancelParams.Id has no "
            + "setter), so no request is cancelled. Enable it when the server moves to Darp.CodeEditor.Lsp."
    )]
    public async Task Cancelled_requests_answer_with_request_cancelled()
    {
        const string uri = "file:///scripts/cancel.csx";
        // Right after initialize the environment is still loading, so the request is still pending when the cancel arrives.
        await using LspClient client = await TestServer.StartAsync();
        const string text = "var t = Device.ReadTemperature();";
        await client.OpenAsync(uri, text);

        PendingRequest hover = client.Send("textDocument/hover", TestServer.At(uri, text, "Device"));
        await client.CancelAsync(hover.Id);
        LspResponse response = await hover.Response;

        response.Error.ShouldNotBeNull().Code.ShouldBe(-32800); // RequestCancelled
    }

    [Fact]
    public async Task A_new_version_cancels_pending_requests_for_the_old_one()
    {
        const string uri = "file:///scripts/stale.csx";
        await using LspClient client = await TestServer.StartAsync();
        const string text = "var t = Device.ReadTemperature();";
        await client.OpenAsync(uri, text);

        PendingRequest hover = client.Send("textDocument/hover", TestServer.At(uri, text, "Device"));
        await client.ChangeAsync(uri, 2, TestServer.Range(text, 0, 0), "// edited\n");
        LspResponse response = await hover.Response;
        JsonNode? fresh = await client.RequestAsync(
            "textDocument/hover",
            TestServer.At(uri, "// edited\n" + text, "Device")
        );

        response.Error.ShouldNotBeNull().Code.ShouldBe(-32800); // RequestCancelled
        fresh.GetString("contents", "value").ShouldStartWith(DeviceHover);
    }
}

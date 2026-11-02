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

        JsonNode capabilities = result?["capabilities"] ?? throw new InvalidOperationException("No capabilities.");
        ((int?)capabilities["textDocumentSync"]?["change"]).ShouldBe(2); // incremental
        ((bool?)capabilities["completionProvider"]?["resolveProvider"]).ShouldBe(true);
        ((bool?)capabilities["hoverProvider"]).ShouldBe(true);
        capabilities["signatureHelpProvider"]
            ?["triggerCharacters"]?.AsArray()
            .Select(c => (string?)c)
            .ShouldBe(["(", ","]);
        ((bool?)capabilities["semanticTokensProvider"]?["range"]).ShouldBe(true);
        ((string?)result?["serverInfo"]?["name"]).ShouldBe("Darp.CodeEditor.CSharp.Server");
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
        JsonNode list =
            await client.RequestAsync("textDocument/completion", parameters) ?? throw new InvalidOperationException();
        JsonNode item =
            list["items"]?.AsArray().Single(i => (string?)i?["label"] == "ReadTemperature")
            ?? throw new InvalidOperationException();
        JsonNode resolved =
            await client.RequestAsync("completionItem/resolve", item.DeepClone())
            ?? throw new InvalidOperationException();

        ((int?)item["kind"]).ShouldBe(2); // Method
        list["itemDefaults"]
            ?["editRange"]?.ToJsonString()
            .ShouldBe(TestServer.Range(text, text.Length, text.Length).ToJsonString());
        ((string?)resolved["documentation"]?["kind"]).ShouldBe("markdown");
        ((string?)resolved["documentation"]?["value"]).ShouldBe(
            "```csharp\ndouble Darp.CodeEditor.CSharp.TestGlobals.TestDevice.ReadTemperature()\n```\n\nReads the current temperature in degrees Celsius."
        );
    }

    [Fact]
    public async Task Word_completion_offers_types_of_other_namespaces_and_adds_their_using_on_resolve()
    {
        await using LspClient client = await TestServer.StartAsync();
        const string text = "var x = 1;\nvar s = new StringB";
        await client.OpenAsync(Uri, text);

        JsonNode list =
            await client.RequestAsync("textDocument/completion", TestServer.AtEnd(Uri, text))
            ?? throw new InvalidOperationException();
        JsonNode item =
            list["items"]?.AsArray().Single(i => (string?)i?["label"] == "StringBuilder")
            ?? throw new InvalidOperationException();
        JsonNode resolved =
            await client.RequestAsync("completionItem/resolve", item.DeepClone())
            ?? throw new InvalidOperationException();

        list["itemDefaults"]
            ?["editRange"]?.ToJsonString()
            .ShouldBe(TestServer.Range(text, text.Length - "StringB".Length, text.Length).ToJsonString());
        ((string?)item["labelDetails"]?["description"]).ShouldBe("System.Text");
        JsonNode edit =
            resolved["additionalTextEdits"]?.AsArray().ShouldHaveSingleItem() ?? throw new InvalidOperationException();
        ((string?)edit["newText"]).ShouldBe("using System.Text;\n\n");
        edit["range"]?.ToJsonString().ShouldBe(TestServer.Range(text, 0, 0).ToJsonString());
    }

    [Fact]
    public async Task Clients_without_item_defaults_get_a_text_edit_per_item()
    {
        JsonObject capabilities = TestServer.FullCapabilities();
        capabilities["textDocument"]?["completion"]?.AsObject().Remove("completionList");
        await using LspClient client = await TestServer.StartAsync(capabilities: capabilities);
        const string text = "var t = Device.Rea";
        await client.OpenAsync(Uri, text);

        JsonNode list =
            await client.RequestAsync("textDocument/completion", TestServer.AtEnd(Uri, text))
            ?? throw new InvalidOperationException();

        list["itemDefaults"].ShouldBeNull();
        JsonNode item =
            list["items"]?.AsArray().Single(i => (string?)i?["label"] == "ReadAsync")
            ?? throw new InvalidOperationException();
        ((string?)item["textEdit"]?["newText"]).ShouldBe("ReadAsync");
        item["textEdit"]
            ?["range"]?.ToJsonString()
            .ShouldBe(TestServer.Range(text, text.Length - 3, text.Length).ToJsonString());
    }
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

        JsonNode hover =
            await client.RequestAsync("textDocument/hover", TestServer.At(Uri, text, "ReadTemperature", 2))
            ?? throw new InvalidOperationException();

        ((string?)hover["contents"]?["kind"]).ShouldBe("markdown");
        ((string?)hover["contents"]?["value"]).ShouldBe(
            "```csharp\ndouble Darp.CodeEditor.CSharp.TestGlobals.TestDevice.ReadTemperature()\n```\n\nReads the current temperature in degrees Celsius."
        );
        int start = text.IndexOf("ReadTemperature", StringComparison.Ordinal);
        hover["range"]
            ?.ToJsonString()
            .ShouldBe(TestServer.Range(text, start, start + "ReadTemperature".Length).ToJsonString());
    }

    [Fact]
    public async Task Signature_help_selects_the_overload_that_fits_the_typed_arguments()
    {
        await using LspClient client = await TestServer.StartAsync();
        const string text = "Log(\"{0}\", 1, ";
        await client.OpenAsync(Uri, text);

        JsonNode help =
            await client.RequestAsync("textDocument/signatureHelp", TestServer.AtEnd(Uri, text))
            ?? throw new InvalidOperationException();

        JsonArray signatures = help["signatures"]?.AsArray() ?? throw new InvalidOperationException();
        signatures.Count.ShouldBe(3);
        JsonNode active = signatures[(int?)help["activeSignature"] ?? -1] ?? throw new InvalidOperationException();
        ((string?)active["label"]).ShouldBe("void DeviceGlobals.Log(string format, params object[] args)");
        ((int?)help["activeParameter"]).ShouldBe(1); // the params array takes every further argument
        ((string?)active["documentation"]?["value"]).ShouldBe("Writes a formatted message to the script log.");
    }

    [Fact]
    public async Task Signature_help_follows_named_arguments()
    {
        await using LspClient client = await TestServer.StartAsync();
        const string text = "var block = await Device.ReadAsync(length: ";
        await client.OpenAsync(Uri, text);

        JsonNode help =
            await client.RequestAsync("textDocument/signatureHelp", TestServer.AtEnd(Uri, text))
            ?? throw new InvalidOperationException();

        JsonNode signature =
            help["signatures"]?.AsArray().ShouldHaveSingleItem() ?? throw new InvalidOperationException();
        string label = (string?)signature["label"] ?? "";
        label.ShouldBe("Task<byte[]> TestDevice.ReadAsync(int register, int length)");
        ((int?)signature["activeParameter"]).ShouldBe(1);
        JsonNode parameter = signature["parameters"]?[1] ?? throw new InvalidOperationException();
        int start = (int?)parameter["label"]?[0] ?? -1;
        int end = (int?)parameter["label"]?[1] ?? -1;
        label[start..end].ShouldBe("int length");
        ((string?)parameter["documentation"]?["value"]).ShouldBe("How many bytes to read.");
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
        JsonNode tokens =
            await client.RequestAsync("textDocument/semanticTokens/range", parameters)
            ?? throw new InvalidOperationException();

        string[] legend =
        [
            .. initialized
                ?["capabilities"]?["semanticTokensProvider"]?["legend"]?["tokenTypes"]?.AsArray()
                .Select(t => (string?)t ?? "")
                ?? [],
        ];
        uint[] data = [.. tokens["data"]?.AsArray().Select(n => (uint?)n ?? 0) ?? []];
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

        ((int?)first["params"]?["version"]).ShouldBe(3);
        first["params"]?["diagnostics"]?.AsArray().ShouldBeEmpty();

        // Break the second line: "Log(x);" becomes "Log(x, );".
        const string v3 = "int x = 1;\nLog(x);\n";
        int argumentEnd = v3.IndexOf("x)", StringComparison.Ordinal) + 1;
        await client.ChangeAsync(uri, 4, TestServer.Range(v3, argumentEnd, argumentEnd), ", ");
        JsonObject second = await client.WaitForNotificationAsync("textDocument/publishDiagnostics");

        ((int?)second["params"]?["version"]).ShouldBe(4);
        JsonNode diagnostic =
            second["params"]?["diagnostics"]?.AsArray().ShouldHaveSingleItem() ?? throw new InvalidOperationException();
        ((string?)diagnostic["code"]).ShouldBe("CS1525");
        diagnostic["range"]
            ?["start"]?.ToJsonString()
            .ShouldBe(new JsonObject { ["line"] = 1, ["character"] = 7 }.ToJsonString());
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
        ((string?)published["params"]?["uri"]).ShouldBe(uri);
        published["params"]
            ?["diagnostics"]?.AsArray()
            .Select(d => $"{(string?)d?["code"]} {d?["range"]?.ToJsonString()}")
            .ShouldBe(expected);
        expected.ShouldNotBeEmpty();
        ((string?)cleared["params"]?["uri"]).ShouldBe(uri);
        cleared["params"]?["diagnostics"]?.AsArray().ShouldBeEmpty();
    }

    [Fact]
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

        response.Error?.Code.ShouldBe(-32800); // RequestCancelled
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

        response.Error?.Code.ShouldBe(-32800);
        // The new version is served.
        JsonNode? fresh = await client.RequestAsync(
            "textDocument/hover",
            TestServer.At(uri, "// edited\n" + text, "Device")
        );
        ((string?)fresh?["contents"]?["value"]).ShouldStartWith(
            "```csharp\nDarp.CodeEditor.CSharp.TestGlobals.TestDevice Device { get; }"
        );
    }
}

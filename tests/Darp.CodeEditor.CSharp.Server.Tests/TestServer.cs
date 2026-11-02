using System.Text.Json.Nodes;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.LspTesting;
using Shouldly;

namespace Darp.CodeEditor.CSharp.Server.Tests;

/// <summary>Starts the real server process and talks to it like an editor would.</summary>
internal static class TestServer
{
    /// <summary>The server's build output is copied next to the tests.</summary>
    public static string ServerPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Darp.CodeEditor.CSharp.Server.dll");

    /// <summary>A client that supports the optional features the server uses.</summary>
    public static JsonObject FullCapabilities() =>
        JsonNode
            .Parse(
                """
                {
                  "textDocument": {
                    "completion": {
                      "completionItem": {
                        "snippetSupport": true,
                        "commitCharactersSupport": true,
                        "documentationFormat": ["markdown", "plaintext"],
                        "labelDetailsSupport": true,
                        "resolveSupport": { "properties": ["documentation", "detail", "additionalTextEdits"] }
                      },
                      "completionList": { "itemDefaults": ["commitCharacters", "editRange", "data"] }
                    },
                    "hover": { "contentFormat": ["markdown", "plaintext"] },
                    "signatureHelp": {
                      "signatureInformation": {
                        "documentationFormat": ["markdown", "plaintext"],
                        "parameterInformation": { "labelOffsetSupport": true },
                        "activeParameterSupport": true
                      }
                    }
                  }
                }
                """
            )
            ?.AsObject()
        ?? throw new InvalidOperationException();

    /// <summary>Starts the server and completes <c>initialize</c> and <c>initialized</c>.</summary>
    public static async Task<LspClient> StartAsync(
        CSharpScriptEnvironment? environment = null,
        JsonObject? capabilities = null
    )
    {
        LspClient client = Start();
        await client.RequestAsync("initialize", InitializeParams(environment ?? TestEnvironments.Device, capabilities));
        await client.NotifyAsync("initialized", new JsonObject());
        return client;
    }

    /// <summary>Starts the server process only; diagnostics are published 50 ms after the last change.</summary>
    public static LspClient Start() => LspClient.Start(ServerPath, "--diagnostics-delay", "50");

    public static JsonObject InitializeParams(CSharpScriptEnvironment environment, JsonObject? capabilities = null) =>
        new()
        {
            ["processId"] = Environment.ProcessId,
            ["rootUri"] = null,
            ["capabilities"] = capabilities ?? FullCapabilities(),
            ["initializationOptions"] = CSharpScriptInitializationOptions.Create(environment),
        };

    public static Task OpenAsync(this LspClient client, string uri, string text, int version = 1) =>
        client.NotifyAsync(
            "textDocument/didOpen",
            new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = uri,
                    ["languageId"] = "csharp",
                    ["version"] = version,
                    ["text"] = text,
                },
            }
        );

    /// <summary>Replaces <paramref name="range"/> of the document with <paramref name="text"/>.</summary>
    public static Task ChangeAsync(this LspClient client, string uri, int version, JsonObject range, string text) =>
        client.NotifyAsync(
            "textDocument/didChange",
            new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
                ["contentChanges"] = new JsonArray(new JsonObject { ["range"] = range, ["text"] = text }),
            }
        );

    /// <summary>Parameters for a request at the position of <paramref name="marker"/> in <paramref name="text"/>.</summary>
    /// <param name="offset">Moves the position within or after the marker.</param>
    public static JsonObject At(string uri, string text, string marker, int offset = 0)
    {
        int index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            throw new ArgumentException($"\"{marker}\" is not in the text.", nameof(marker));
        return new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = Position(text, index + offset),
        };
    }

    public static JsonObject AtEnd(string uri, string text) =>
        new()
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = Position(text, text.Length),
        };

    public static JsonObject Position(string text, int offset)
    {
        int line = text.AsSpan(0, offset).Count('\n');
        int character = offset - (text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1);
        if (offset == 0)
            character = 0;
        return new JsonObject { ["line"] = line, ["character"] = character };
    }

    public static JsonObject Range(string text, int start, int end) =>
        new() { ["start"] = Position(text, start), ["end"] = Position(text, end) };

    /// <summary>
    /// The value at <paramref name="path"/>, property names and array indexes; fails the test when a part is missing,
    /// so assertions on the value cannot pass without running.
    /// </summary>
    public static JsonNode Get(this JsonNode? node, params object[] path)
    {
        JsonNode current = node ?? throw new ShouldAssertException("The JSON value is null.");
        foreach (object step in path)
        {
            JsonNode? next = step switch
            {
                string name => current[name],
                int index => current.AsArray().Count > index ? current[index] : null,
                _ => throw new ArgumentException($"{step} is neither a property name nor an index.", nameof(path)),
            };
            current = next ?? throw new ShouldAssertException($"{current.ToJsonString()} has no {step}.");
        }
        return current;
    }

    public static string GetString(this JsonNode? node, params object[] path) => node.Get(path).GetValue<string>();

    public static int GetInt(this JsonNode? node, params object[] path) => node.Get(path).GetValue<int>();

    public static JsonArray GetArray(this JsonNode? node, params object[] path) => node.Get(path).AsArray();
}

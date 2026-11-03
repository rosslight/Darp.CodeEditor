using System.Text.Json.Nodes;
using Shouldly;

namespace Darp.CodeEditor.Luau.Tests;

/// <summary>How an environment becomes luau-lsp's arguments, settings and document URIs.</summary>
public sealed class LuauEnvironmentTests
{
    [Fact]
    public void Arguments_load_the_definitions_and_documentation_and_set_the_mode()
    {
        string api = Path.Combine(AppContext.BaseDirectory, "TestApi");
        LuauEnvironment environment = LuauSession.Environment(LuauMode.Strict) with
        {
            Definitions = [new LuauDefinitions("@test", Path.Combine(api, "test.d.luau"))],
        };

        List<string> arguments = LuauLanguageServer.CreateArguments(environment, ["--no-flags-enabled"]);

        arguments.ShouldBe([
            "lsp",
            "--stdio",
            "--definitions",
            $"@test={Path.Combine(api, "test.d.luau")}",
            "--docs",
            Path.Combine(api, "test.docs.json"),
            "--base-luaurc",
            Path.Combine(AppContext.BaseDirectory, "helpers", "luau-lsp", "luaurc", "strict.luaurc"),
            "--no-flags-enabled",
        ]);
    }

    [Fact]
    public void A_missing_definitions_file_fails_the_start_with_its_path()
    {
        LuauEnvironment environment = LuauSession.Environment() with
        {
            Definitions = [new LuauDefinitions("test", "/missing/api.d.luau")],
        };

        FileNotFoundException exception = Should.Throw<FileNotFoundException>(() =>
            LuauLanguageServer.CreateArguments(environment, [])
        );

        exception.FileName.ShouldBe(Path.GetFullPath("/missing/api.d.luau"));
    }

    [Fact]
    public void Settings_select_the_standard_platform_and_merge_the_host_settings_over_them()
    {
        LuauEnvironment environment = new()
        {
            Settings = new JsonObject
            {
                ["completion"] = new JsonObject { ["autocompleteEnd"] = true },
                ["types"] = new JsonObject { ["disabledGlobals"] = new JsonArray("os.exit") },
            },
        };

        JsonObject settings = LuauLanguageServer.CreateSettings(environment, indexWorkspace: false);

        JsonNode
            .DeepEquals(
                settings,
                JsonNode.Parse(
                    """
                    {
                      "platform": { "type": "standard" },
                      "completion": { "addParentheses": false, "autocompleteEnd": true },
                      "index": { "enabled": false },
                      "types": { "disabledGlobals": ["os.exit"] }
                    }
                    """
                )
            )
            .ShouldBeTrue(settings.ToJsonString());
    }

    [Fact]
    public void Document_ids_are_placed_in_the_workspace()
    {
        string root = Path.Combine(Path.GetTempPath(), "scripts");
        string outside = OperatingSystem.IsWindows() ? @"D:\other\b.luau" : "/other/b.luau";

        LuauLanguageServer
            .ToDocumentUri(root, "a.luau")
            .ShouldBe(LuauLanguageServer.ToFileUri(Path.Combine(root, "a.luau")));
        LuauLanguageServer
            .ToDocumentUri(root, Path.Combine(root, "lib", "a.luau"))
            .ShouldBe(LuauLanguageServer.ToFileUri(Path.Combine(root, "lib", "a.luau")));
        LuauLanguageServer
            .ToDocumentUri(root, outside)
            .ShouldBe(
                LuauLanguageServer.ToFileUri(
                    Path.Combine(root, OperatingSystem.IsWindows() ? @"D\other\b.luau" : "other/b.luau")
                )
            );
    }

    [Theory]
    [InlineData("/home/user/My Scripts (1)/a+b.luau", "file:///home/user/My%20Scripts%20%281%29/a%2Bb.luau")]
    [InlineData("/home/user/Skripte/Grüße.luau", "file:///home/user/Skripte/Gr%C3%BC%C3%9Fe.luau")]
    // A backslash is part of a Unix file name.
    [InlineData(@"/home/user/a\b.luau", "file:///home/user/a%5Cb.luau")]
    public void Unix_paths_are_written_as_luau_lsp_writes_them(string path, string uri) =>
        LuauLanguageServer.ToFileUri(path, windows: false).ShouldBe(uri);

    [Theory]
    [InlineData(@"C:\Users\user\Scripts\main.luau", "file:///c%3A/Users/user/Scripts/main.luau")]
    [InlineData(@"\\server\share\main.luau", "file://server/share/main.luau")]
    public void Windows_paths_are_written_as_luau_lsp_writes_them(string path, string uri) =>
        LuauLanguageServer.ToFileUri(path, windows: true).ShouldBe(uri);
}

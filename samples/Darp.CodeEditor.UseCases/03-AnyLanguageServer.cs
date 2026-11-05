using System.Text.Json.Nodes;
using Darp.CodeEditor;

namespace UseCases.AnyLanguage;

// Scenario: the app edits YAML configuration files with a third-party language server it ships, such as
// yaml-language-server. Any server that speaks LSP over stdio plugs in the same way; the editor knows nothing about
// YAML beyond its built-in grammar.
//
// - The server needs settings: it asks for them with workspace/configuration, and `Settings` answers.
// - Documents carry their file path, so the server can find a schema or configuration next to them.
// - Servers speak LSP over stdio; other transports come when a server needs one.
// - Each app ships the servers it uses; the library bundles none.

/// <summary>YAML editing with yaml-language-server.</summary>
public sealed class YamlEditing : IAsyncDisposable
{
    private readonly LanguageServer _server;

    /// <summary>Describes the server; it starts with the first document it creates.</summary>
    public YamlEditing()
    {
        _server = new LanguageServer(
            new LanguageServerOptions
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "tools", "yaml-language-server"),
                Arguments = ["--stdio"],
                Settings = request =>
                    request.Section == "yaml"
                        ? new JsonObject
                        {
                            ["validate"] = true,
                            ["schemaStore"] = new JsonObject { ["enable"] = false },
                        }
                        : null,
            }
        );
    }

    /// <summary>Opens a YAML file in a document that the server serves.</summary>
    public async Task<CodeDocument> OpenAsync(string path, CancellationToken cancellationToken) =>
        _server.CreateDocument(CodeLanguage.Yaml, await File.ReadAllTextAsync(path, cancellationToken), path);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

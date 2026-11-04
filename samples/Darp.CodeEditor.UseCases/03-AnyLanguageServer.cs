using System.Text.Json.Nodes;
using Darp.CodeEditor;

namespace UseCases.AnyLanguage;

// Scenario: the app edits YAML configuration files with a third-party language server it ships, such as
// yaml-language-server. Any server that speaks LSP over stdio plugs in the same way; the editor knows nothing about
// YAML beyond the bundled grammar.
//
// - The server needs settings: it asks for them with workspace/configuration, and `Settings` answers.
// - Documents carry their file path, so the server can find a schema or configuration next to them.
//
// Questions:
// - Do some servers need more than a command line, initialization options and settings answers?
// - Should the editor bundle third-party servers, or does each app ship the ones it uses?

/// <summary>YAML editing with yaml-language-server.</summary>
public sealed class YamlEditing : IAsyncDisposable
{
    private readonly LanguageServer _server;

    /// <summary>Describes the server; it starts with the first YAML document.</summary>
    public YamlEditing()
    {
        _server = new LanguageServer(
            new LanguageServerOptions
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "tools", "yaml-language-server"),
                Arguments = ["--stdio"],
                Settings = section =>
                    section == "yaml"
                        ? new JsonObject
                        {
                            ["validate"] = true,
                            ["schemaStore"] = new JsonObject { ["enable"] = false },
                        }
                        : null,
            }
        );
        Yaml = CodeLanguage.Bundled("yaml", _server);
    }

    /// <summary>The language of YAML documents.</summary>
    public CodeLanguage Yaml { get; }

    /// <summary>Opens a YAML file in a document.</summary>
    public async Task<CodeDocument> OpenAsync(string path, CancellationToken cancellationToken) =>
        new(Yaml, await File.ReadAllTextAsync(path, cancellationToken), path);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

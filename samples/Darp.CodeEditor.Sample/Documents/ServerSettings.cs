using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darp.CodeEditor;

namespace Sample.Documents;

/// <summary>
/// The app's settings for its language servers, in sections as VS Code's settings are: the JSON schemas of the
/// examples. They are files next to the app, which the user can open, edit and save; the servers read them by their
/// file URIs.
/// </summary>
/// <remarks>
/// <para>
/// Servers associate a schema with documents by their file names: here the examples' names, so that a file opened
/// with the same name gets the schema too. VS Code's JSON server reads its settings only when the client sends all of
/// them; the YAML server asks for its section, <c>yaml</c>. Other sections, such as Pyright's <c>python</c>, have
/// no settings: the servers' defaults apply.
/// </para>
/// <para>
/// Both servers forget their schemas whenever the settings change, and load them again. VS Code's JSON server takes a
/// schema's text from its settings as well, so the open text of a schema goes there, and edits apply before they are
/// saved. The YAML server takes only the schemas' URIs and reads the files, so its schema applies once saved.
/// </para>
/// </remarks>
internal sealed class ServerSettings
{
    private static readonly string s_folder = Path.Combine(AppContext.BaseDirectory, "Schemas");

    /// <summary>The schema of the Config example, for VS Code's JSON server.</summary>
    public static string ConfigSchema { get; } = Path.Combine(s_folder, "config.schema.json");

    /// <summary>The schema of the Pipeline example, for the YAML server.</summary>
    public static string PipelineSchema { get; } = Path.Combine(s_folder, "pipeline.schema.json");

    /// <summary>The schemas, for the navigation pane.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [ConfigSchema, PipelineSchema];

    /// <summary>
    /// The text of each schema that is open, by its path, as of the last <see cref="Update"/>: the last text that was
    /// valid JSON. Replaced on the UI thread, read on the servers' threads.
    /// </summary>
    private volatile ImmutableDictionary<string, string> _openSchemas = ImmutableDictionary<string, string>.Empty;

    /// <summary>Whether <paramref name="path"/> is one of the <see cref="Schemas"/>.</summary>
    public static bool IsSchema(string? path) => path is not null && Schemas.Contains(path);

    /// <summary>
    /// Takes the text of the schemas among <paramref name="documents"/>, on the UI thread. A schema whose text is not
    /// valid JSON, as while the user types, keeps the text it had; one that is not open any more is read from its file.
    /// </summary>
    public void Update(IEnumerable<OpenDocument> documents)
    {
        ImmutableDictionary<string, string> open = ImmutableDictionary<string, string>.Empty;
        foreach (OpenDocument document in documents)
        {
            if (document.Document.FilePath is not { } path || !IsSchema(path))
                continue;
            string text = document.Document.CurrentSnapshot.GetText();
            if (IsJson(text))
                open = open.SetItem(path, text);
            else if (_openSchemas.TryGetValue(path, out string? valid))
                open = open.SetItem(path, valid);
        }
        _openSchemas = open;
    }

    /// <summary>The settings of <paramref name="request"/>'s section, or all of them; called on any thread.</summary>
    public JsonNode? Get(LanguageServerSettingsRequest request) =>
        request.Section switch
        {
            null => new JsonObject { ["json"] = Json(), ["yaml"] = Yaml() },
            "json" => Json(),
            "yaml" => Yaml(),
            _ => null,
        };

    /// <summary>
    /// VS Code's JSON server: validation, which is off unless a setting turns it on, and the schemas, with the text of
    /// those that are open.
    /// </summary>
    private JsonObject Json()
    {
        var config = new JsonObject { ["fileMatch"] = new JsonArray("Config.json"), ["url"] = Uri(ConfigSchema) };
        if (_openSchemas.TryGetValue(ConfigSchema, out string? text))
            config["schema"] = JsonNode.Parse(text);
        return new JsonObject
        {
            ["validate"] = new JsonObject { ["enable"] = true },
            ["schemas"] = new JsonArray(config),
        };
    }

    /// <summary>
    /// The YAML server: each schema's URI, with globs of the files it is for. The server matches a glob anywhere in a
    /// path, but not across folders whose names start with a dot, so the example's own path is one of them.
    /// </summary>
    private static JsonObject Yaml() =>
        new()
        {
            ["schemas"] = new JsonObject
            {
                [Uri(PipelineSchema)] = new JsonArray(
                    "Pipeline.yaml",
                    Path.Combine(AppContext.BaseDirectory, "Examples", "Pipeline.yaml").Replace('\\', '/')
                ),
            },
        };

    private static string Uri(string path) => new Uri(path).AbsoluteUri;

    private static bool IsJson(string text)
    {
        try
        {
            using JsonDocument _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

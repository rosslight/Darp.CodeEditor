using System.Text.Json.Nodes;
using Darp.CodeEditor;

namespace Sample.Documents;

/// <summary>
/// The app's settings for its language servers, in sections as VS Code's settings are: the JSON schemas of the
/// examples, which the app ships next to it, so that the servers read them by their file URIs.
/// </summary>
/// <remarks>
/// Servers associate a schema with documents by their file names: here the examples' names, so that a file opened
/// with the same name gets the schema too. VS Code's JSON server reads its settings only when the client sends all of
/// them; the YAML server asks for its section, <c>yaml</c>. Other sections, such as Pyright's <c>python</c>, have
/// no settings: the servers' defaults apply.
/// </remarks>
internal static class ServerSettings
{
    private static readonly string s_configSchema = SchemaUri("config.schema.json");
    private static readonly string s_pipelineSchema = SchemaUri("pipeline.schema.json");

    /// <summary>The settings of <paramref name="request"/>'s section, or all of them; called on any thread.</summary>
    public static JsonNode? Get(LanguageServerSettingsRequest request) =>
        request.Section switch
        {
            null => new JsonObject { ["json"] = Json(), ["yaml"] = Yaml() },
            "json" => Json(),
            "yaml" => Yaml(),
            _ => null,
        };

    /// <summary>VS Code's JSON server: validation, which is off unless a setting turns it on, and the schemas.</summary>
    private static JsonObject Json() =>
        new()
        {
            ["validate"] = new JsonObject { ["enable"] = true },
            ["schemas"] = new JsonArray(
                new JsonObject { ["fileMatch"] = new JsonArray("Config.json"), ["url"] = s_configSchema }
            ),
        };

    /// <summary>The YAML server: each schema's URI, with a glob of the files it is for.</summary>
    private static JsonObject Yaml() => new() { ["schemas"] = new JsonObject { [s_pipelineSchema] = "Pipeline.yaml" } };

    private static string SchemaUri(string fileName) =>
        new Uri(Path.Combine(AppContext.BaseDirectory, "Schemas", fileName)).AbsoluteUri;
}

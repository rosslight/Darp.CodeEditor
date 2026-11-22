using System.Text.Json.Nodes;
using Darp.CodeEditor;

namespace Sample.Documents;

/// <summary>
/// The app's settings for its language servers, in sections as VS Code's settings are: the JSON schema of the YAML
/// example, which the app ships next to it, so that the server reads it by its file URI.
/// </summary>
/// <remarks>
/// The YAML server associates a schema with documents by their file names: here the example's name, so that a file
/// opened with the same name gets the schema too. VS Code's JSON server reads its settings only when the client sends
/// all of them; the YAML server asks for its section, <c>yaml</c>. Other sections, such as Pyright's <c>python</c>,
/// have no settings: the servers' defaults apply.
/// </remarks>
internal static class ServerSettings
{
    /// <summary>The file of the Pipeline example's schema, next to the app.</summary>
    public static string PipelineSchema { get; } =
        Path.Combine(AppContext.BaseDirectory, "Schemas", "Pipeline.schema.json");

    /// <summary>The settings of <paramref name="request"/>'s section, or all of them; called on any thread.</summary>
    public static JsonNode? Get(LanguageServerSettingsRequest request) =>
        request.Section switch
        {
            null => new JsonObject { ["json"] = Json(), ["yaml"] = Yaml() },
            "json" => Json(),
            "yaml" => Yaml(),
            _ => null,
        };

    /// <summary>VS Code's JSON server: validation, which is off unless a setting turns it on.</summary>
    private static JsonObject Json() => new() { ["validate"] = new JsonObject { ["enable"] = true } };

    /// <summary>The YAML server: each schema's URI, with a glob of the files it is for.</summary>
    private static JsonObject Yaml() =>
        new() { ["schemas"] = new JsonObject { [new Uri(PipelineSchema).AbsoluteUri] = "Pipeline.yaml" } };
}

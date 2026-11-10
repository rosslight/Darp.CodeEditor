using System.Text.Json;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.LanguageClient;

/// <summary>Reads the optional parts of a server's answers: a part of another JSON type than the protocol says counts as missing.</summary>
internal static class ServerJson
{
    public static string? String(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number)
            ? number
            : null;

    public static bool IsTrue(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    /// <summary>A <c>documentation</c>: plain text, or <c>MarkupContent</c> in Markdown or plain text; null without any.</summary>
    public static HoverText? Documentation(JsonElement parent)
    {
        if (!parent.TryGetProperty("documentation", out JsonElement documentation))
            return null;
        HoverText? text = documentation.ValueKind switch
        {
            JsonValueKind.String => new HoverText(documentation.GetString() ?? "", IsMarkdown: false),
            JsonValueKind.Object when String(documentation, "value") is { } value => new HoverText(
                value,
                String(documentation, "kind") == "markdown"
            ),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text?.Text) ? null : text;
    }

    /// <summary>A required range.</summary>
    /// <exception cref="JsonException">It is not a range.</exception>
    public static LspRange Range(JsonElement value) =>
        value.Deserialize(LspJsonContext.Default.LspRange) ?? throw new JsonException("A range is null.");
}

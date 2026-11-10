using System.Text.Json;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.LanguageClient;

/// <summary>
/// The features that a server offers for the editors, of those the client implements, from its answer to
/// <c>initialize</c>. A capability of another JSON type than the protocol says counts as not given.
/// </summary>
/// <param name="Hover">Whether it answers <c>textDocument/hover</c>.</param>
/// <param name="SemanticTokens">How it answers semantic tokens; null when it does not.</param>
internal sealed record ServerFeatures(bool Hover, SemanticTokensProvider? SemanticTokens)
{
    public static ServerFeatures Read(ServerCapabilities capabilities) =>
        new(
            capabilities.HoverProvider is { ValueKind: JsonValueKind.True or JsonValueKind.Object },
            SemanticTokensProvider.Read(capabilities.SemanticTokensProvider)
        );
}

/// <summary>A server's semantic tokens: what their numbers name, and the requests it answers.</summary>
/// <param name="Legend">The token types and modifiers that its tokens refer to by index.</param>
/// <param name="Range">Whether it answers <c>textDocument/semanticTokens/range</c>.</param>
/// <param name="Full">Whether it answers <c>textDocument/semanticTokens/full</c>.</param>
internal sealed record SemanticTokensProvider(SemanticTokensLegend Legend, bool Range, bool Full)
{
    /// <summary>The provider of <c>semanticTokensProvider</c>; null without a legend or a request it answers.</summary>
    public static SemanticTokensProvider? Read(JsonElement? capability)
    {
        if (
            capability is not { ValueKind: JsonValueKind.Object } options
            || !options.TryGetProperty("legend", out JsonElement legend)
            || legend.ValueKind != JsonValueKind.Object
            || Strings(legend, "tokenTypes") is not { } types
            || Strings(legend, "tokenModifiers") is not { } modifiers
        )
        {
            return null;
        }
        bool range = Offered(options, "range");
        bool full = Offered(options, "full");
        return range || full
            ? new SemanticTokensProvider(new SemanticTokensLegend(types, modifiers), range, full)
            : null;
    }

    /// <summary>A request is offered with true, or with an object of options.</summary>
    private static bool Offered(JsonElement options, string request) =>
        options.TryGetProperty(request, out JsonElement value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.Object;

    private static string[]? Strings(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return null;
        var strings = new string[array.GetArrayLength()];
        int i = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return null;
            strings[i++] = item.GetString() ?? "";
        }
        return strings;
    }
}

/// <summary>The names of a server's token types and modifiers: a token's type is an index, its modifiers bits.</summary>
internal sealed record SemanticTokensLegend(string[] TokenTypes, string[] TokenModifiers);

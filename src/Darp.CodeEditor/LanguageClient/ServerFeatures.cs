using System.Text;
using System.Text.Json;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.LanguageClient;

/// <summary>
/// The features that a server offers for the editors, of those the client implements, from its answer to
/// <c>initialize</c>. A capability of another JSON type than the protocol says counts as not given.
/// </summary>
/// <param name="Hover">Whether it answers <c>textDocument/hover</c>.</param>
/// <param name="SemanticTokens">How it answers semantic tokens; null when it does not.</param>
/// <param name="Completion">How it answers completion; null when it does not.</param>
/// <param name="SignatureHelp">When it answers signature help; null when it does not.</param>
internal sealed record ServerFeatures(
    bool Hover,
    SemanticTokensProvider? SemanticTokens,
    CompletionProvider? Completion,
    SignatureHelpProvider? SignatureHelp
)
{
    public static ServerFeatures Read(ServerCapabilities capabilities) =>
        new(
            capabilities.HoverProvider is { ValueKind: JsonValueKind.True or JsonValueKind.Object },
            SemanticTokensProvider.Read(capabilities.SemanticTokensProvider),
            CompletionProvider.Read(capabilities.CompletionProvider),
            SignatureHelpProvider.Read(capabilities.SignatureHelpProvider)
        );

    /// <summary>
    /// The single characters of a list of strings, as typing checks them; longer strings, and lists of another JSON type,
    /// count as none.
    /// </summary>
    internal static string Characters(JsonElement options, string name)
    {
        if (!options.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return "";
        var characters = new StringBuilder();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is [char c])
                characters.Append(c);
        }
        return characters.ToString();
    }
}

/// <summary>A server's completion.</summary>
/// <param name="TriggerCharacters">The characters that ask for completion as they are typed.</param>
/// <param name="CommitCharacters">The characters that commit an item that names none of its own.</param>
/// <param name="Resolves">Whether it answers <c>completionItem/resolve</c>.</param>
internal sealed record CompletionProvider(string TriggerCharacters, string CommitCharacters, bool Resolves)
{
    /// <summary>The provider of <c>completionProvider</c>, an object of options; null for anything else.</summary>
    public static CompletionProvider? Read(JsonElement? capability) =>
        capability is { ValueKind: JsonValueKind.Object } options
            ? new CompletionProvider(
                ServerFeatures.Characters(options, "triggerCharacters"),
                ServerFeatures.Characters(options, "allCommitCharacters"),
                options.TryGetProperty("resolveProvider", out JsonElement resolve)
                    && resolve.ValueKind == JsonValueKind.True
            )
            : null;
}

/// <summary>A server's signature help.</summary>
/// <param name="TriggerCharacters">The characters that ask for signature help as they are typed.</param>
/// <param name="RetriggerCharacters">The characters that ask again while signature help shows.</param>
internal sealed record SignatureHelpProvider(string TriggerCharacters, string RetriggerCharacters)
{
    /// <summary>The provider of <c>signatureHelpProvider</c>, an object of options; null for anything else.</summary>
    public static SignatureHelpProvider? Read(JsonElement? capability) =>
        capability is { ValueKind: JsonValueKind.Object } options
            ? new SignatureHelpProvider(
                ServerFeatures.Characters(options, "triggerCharacters"),
                ServerFeatures.Characters(options, "retriggerCharacters")
            )
            : null;
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

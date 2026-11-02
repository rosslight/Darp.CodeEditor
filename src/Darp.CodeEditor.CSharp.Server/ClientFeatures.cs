using EmmyLua.LanguageServer.Framework.Protocol.Capabilities.Client.ClientCapabilities;
using EmmyLua.LanguageServer.Framework.Protocol.Model.Markup;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary>The optional protocol features the client declared in <c>initialize</c> that change the server's answers.</summary>
internal sealed record ClientFeatures(
    bool CompletionDefaultEditRange,
    bool CompletionDefaultCommitCharacters,
    bool CompletionCommitCharacters,
    bool CompletionLabelDetails,
    bool CompletionSnippets,
    bool CompletionResolvesAdditionalTextEdits,
    bool CompletionMarkdown,
    bool HoverMarkdown,
    bool SignatureLabelOffsets,
    bool SignatureActiveParameter,
    bool SignatureMarkdown
)
{
    public static ClientFeatures From(ClientCapabilities capabilities)
    {
        var completion = capabilities.TextDocument?.Completion;
        var completionItem = completion?.CompletionItem;
        List<string> itemDefaults = completion?.CompletionList?.ItemDefaults ?? [];
        var signature = capabilities.TextDocument?.SignatureHelp?.SignatureInformation;
        return new ClientFeatures(
            CompletionDefaultEditRange: itemDefaults.Contains("editRange"),
            CompletionDefaultCommitCharacters: itemDefaults.Contains("commitCharacters"),
            CompletionCommitCharacters: completionItem?.CommitCharactersSupport == true,
            CompletionLabelDetails: completionItem?.LabelDetailsSupport == true,
            CompletionSnippets: completionItem?.SnippetSupport == true,
            CompletionResolvesAdditionalTextEdits: completionItem?.ResolveSupport?.Properties?.Contains(
                "additionalTextEdits"
            ) == true,
            CompletionMarkdown: completionItem?.DocumentationFormat?.Contains(MarkupKind.Markdown.Value) == true,
            HoverMarkdown: capabilities.TextDocument?.Hover?.ContentFormat?.Contains(MarkupKind.Markdown) == true,
            SignatureLabelOffsets: signature?.ParameterInformation?.LabelOffsetSupport == true,
            SignatureActiveParameter: signature?.ActiveParameterSupport == true,
            SignatureMarkdown: signature?.DocumentationFormat?.Contains(MarkupKind.Markdown) == true
        );
    }
}

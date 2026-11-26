using System.Text;
using Darp.CodeEditor.Lsp;
using Microsoft.CodeAnalysis.QuickInfo;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>Hover from Roslyn's quick info: the symbol as code, then its documentation in Markdown.</summary>
internal static class ScriptHover
{
    /// <summary>The hover at <paramref name="position"/>; null where there is nothing to say.</summary>
    public static async Task<LspHover?> GetAsync(
        ScriptSnapshot script,
        LspPosition position,
        CancellationToken cancellationToken
    )
    {
        if (QuickInfoService.GetService(script.Document) is not { } service)
            return null;
        int offset = script.Lines.ToOffset(position);
        QuickInfoItem? info = await service
            .GetQuickInfoAsync(script.Document, offset, cancellationToken)
            .ConfigureAwait(false);
        if (info is null || info.Sections.IsEmpty)
            return null;
        var markdown = new StringBuilder();
        foreach (QuickInfoSection section in info.Sections)
        {
            if (markdown.Length > 0)
                markdown.Append("\n\n");
            if (section.Kind == QuickInfoSectionKinds.Description)
                markdown.Append("```csharp\n").Append(section.Text).Append("\n```");
            else
                markdown.Append(Markdown.Of(section.TaggedParts));
        }
        return new LspHover(
            new LspMarkupContent("markdown", markdown.ToString()),
            new LspRange(script.Lines.ToPosition(info.Span.Start), script.Lines.ToPosition(info.Span.End))
        );
    }
}

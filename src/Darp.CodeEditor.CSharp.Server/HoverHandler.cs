using Darp.CodeEditor.Lsp;
using Darp.CodeEditor.Lsp.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.QuickInfo;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary><c>textDocument/hover</c> from Roslyn's quick info: the signature as C# code, then the documentation.</summary>
internal static class HoverHandler
{
    public static void Register(JsonRpcConnection connection, ScriptSession session) =>
        connection.OnRequest(LspMethods.Hover, (request, token) => HandleAsync(session, request, token));

    private static async Task<Hover?> HandleAsync(ScriptSession session, HoverParams request, CancellationToken token)
    {
        if (!session.TryGetSnapshot(request.TextDocument.Uri, out DocumentSnapshot? snapshot))
            return null;
        int position = snapshot.Lines.GetOffset(request.Position);
        bool markdown = session.Client.HoverMarkdown;
        // The snapshot is taken; the rest runs on the thread pool while later messages are dispatched.
        await Task.Yield();
        return await StaleWork
            .RunAsync(
                token,
                snapshot.Changed,
                async cancellationToken =>
                    await GetHoverAsync(
                            snapshot.Owner.GetDocument(
                                await session.Workspace.WaitAsync(cancellationToken).ConfigureAwait(false),
                                snapshot
                            ),
                            snapshot.Lines,
                            position,
                            markdown,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
            )
            .ConfigureAwait(false);
    }

    public static async Task<Hover?> GetHoverAsync(
        Document document,
        LineMap lines,
        int position,
        bool markdown,
        CancellationToken cancellationToken
    )
    {
        if (QuickInfoService.GetService(document) is not { } service)
            return null;
        QuickInfoItem? item = await service
            .GetQuickInfoAsync(document, position, cancellationToken)
            .ConfigureAwait(false);
        if (item is null || item.Sections.IsEmpty)
            return null;
        string? signature = item.Sections.FirstOrDefault(s => s.Kind == QuickInfoSectionKinds.Description)?.Text;
        IEnumerable<string> paragraphs = item
            .Sections.Where(s => s.Kind != QuickInfoSectionKinds.Description)
            .Select(s => Markup.ToText(s.TaggedParts));
        return new Hover(Markup.Create(signature, paragraphs, markdown), lines.GetRange(item.Span));
    }
}

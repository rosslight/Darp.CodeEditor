using EmmyLua.LanguageServer.Framework.Protocol.Capabilities.Client.ClientCapabilities;
using EmmyLua.LanguageServer.Framework.Protocol.Capabilities.Server;
using EmmyLua.LanguageServer.Framework.Protocol.Message.Hover;
using EmmyLua.LanguageServer.Framework.Server.Handler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.QuickInfo;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary><c>textDocument/hover</c> from Roslyn's quick info: the signature as C# code, then the documentation.</summary>
internal sealed class HoverHandler : HoverHandlerBase
{
    private readonly ScriptSession _session;

    public HoverHandler(ScriptSession session) => _session = session;

    public override void RegisterCapability(
        ServerCapabilities serverCapabilities,
        ClientCapabilities clientCapabilities
    ) => serverCapabilities.HoverProvider = true;

    protected override Task<HoverResponse?> Handle(HoverParams request, CancellationToken token)
    {
        if (!_session.TryGetSnapshot(request.TextDocument.Uri, out DocumentSnapshot? snapshot))
            return Task.FromResult<HoverResponse?>(null);
        int position = snapshot.Lines.GetOffset(request.Position);
        ScriptSession session = _session;
        return Task.Run(
            async () =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, snapshot.Changed);
                ScriptWorkspace workspace = await session.Workspace.WaitAsync(cancellation.Token).ConfigureAwait(false);
                return await GetHoverAsync(
                        snapshot.Owner.GetDocument(workspace, snapshot),
                        snapshot.Lines,
                        position,
                        session.Client.HoverMarkdown,
                        cancellation.Token
                    )
                    .ConfigureAwait(false);
            },
            token
        );
    }

    public static async Task<HoverResponse?> GetHoverAsync(
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
        return new HoverResponse
        {
            Contents = Markup.Create(signature, paragraphs, markdown),
            Range = lines.GetRange(item.Span),
        };
    }
}

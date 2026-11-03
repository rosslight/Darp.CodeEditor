using EmmyLua.LanguageServer.Framework.Protocol.Capabilities.Client.ClientCapabilities;
using EmmyLua.LanguageServer.Framework.Protocol.Capabilities.Server;
using EmmyLua.LanguageServer.Framework.Protocol.Capabilities.Server.Options;
using EmmyLua.LanguageServer.Framework.Protocol.Message.TextDocument;
using EmmyLua.LanguageServer.Framework.Protocol.Model.TextEdit;
using EmmyLua.LanguageServer.Framework.Server.Handler;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary>Keeps the open documents in sync with the client, with incremental changes.</summary>
internal sealed class TextDocumentSyncHandler : TextDocumentHandlerBase
{
    private readonly ScriptSession _session;

    public TextDocumentSyncHandler(ScriptSession session) => _session = session;

    public override void RegisterCapability(
        ServerCapabilities serverCapabilities,
        ClientCapabilities clientCapabilities
    ) =>
        serverCapabilities.TextDocumentSync = new TextDocumentSyncOptions
        {
            OpenClose = true,
            Change = TextDocumentSyncKind.Incremental,
        };

    protected override Task Handle(DidOpenTextDocumentParams request, CancellationToken token)
    {
        _session.Open(request.TextDocument.Uri, request.TextDocument.Version, request.TextDocument.Text);
        return Task.CompletedTask;
    }

    protected override Task Handle(DidChangeTextDocumentParams request, CancellationToken token)
    {
        _session.Change(request.TextDocument.Uri, request.TextDocument.Version, request.ContentChanges);
        return Task.CompletedTask;
    }

    protected override Task Handle(DidCloseTextDocumentParams request, CancellationToken token)
    {
        _session.Close(request.TextDocument.Uri);
        return Task.CompletedTask;
    }

    protected override Task Handle(WillSaveTextDocumentParams request, CancellationToken token) => Task.CompletedTask;

    protected override Task<List<TextEdit>?> HandleRequest(
        WillSaveTextDocumentParams request,
        CancellationToken token
    ) => Task.FromResult<List<TextEdit>?>(null);
}

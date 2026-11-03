using System.Collections.Concurrent;
using System.Threading.Channels;
using Darp.CodeEditor.Lsp.Protocol;

namespace Darp.CodeEditor.Lsp.Testing;

/// <summary>The client end for <see cref="ToyServer"/>: one document, typed requests, and what the server sent.</summary>
internal sealed class ToyClient : IAsyncDisposable
{
    /// <summary>Escaped, to check that the server keeps URIs as sent.</summary>
    public const string Uri = "file:///toy/a%20b.txt";

    private readonly Channel<PublishDiagnosticsParams> _diagnostics =
        Channel.CreateUnbounded<PublishDiagnosticsParams>();

    public ToyClient(Stream input, Stream output, string greeting)
    {
        Connection = new JsonRpcConnection(input, output);
        Connection.OnRequest(
            ToyServer.Configuration,
            (_, _) => Task.FromResult(ToyServer.ToJson($$"""[{"greeting":"{{greeting}}"}]"""))
        );
        Connection.OnNotification(
            LspMethods.PublishDiagnostics,
            (p, token) => _diagnostics.Writer.WriteAsync(p, token).AsTask()
        );
        Connection.OnNotification(
            LspMethods.LogMessage,
            (p, _) =>
            {
                Logs.Enqueue(p.Message);
                return Task.CompletedTask;
            }
        );
        Connection.Start();
    }

    public JsonRpcConnection Connection { get; }

    public ConcurrentQueue<string> Logs { get; } = new();

    public static InitializeParams InitializeParams { get; } =
        new(
            new ClientCapabilities
            {
                General = new GeneralClientCapabilities { PositionEncodings = ["utf-16"] },
                TextDocument = new TextDocumentClientCapabilities
                {
                    Completion = new CompletionClientCapabilities
                    {
                        CompletionItem = new CompletionItemClientCapabilities
                        {
                            InsertReplaceSupport = true,
                            ResolveSupport = new CompletionResolveSupport(["detail", "documentation"]),
                        },
                    },
                    SignatureHelp = new SignatureHelpClientCapabilities
                    {
                        SignatureInformation = new SignatureInformationClientCapabilities
                        {
                            ParameterInformation = new ParameterInformationClientCapabilities(LabelOffsetSupport: true),
                        },
                    },
                },
            }
        )
        {
            ProcessId = Environment.ProcessId,
            ClientInfo = new ClientInfo("toy client"),
        };

    public async Task<InitializeResult?> InitializeAsync(CancellationToken cancellationToken)
    {
        InitializeResult? result = await Connection.SendRequestAsync(
            LspMethods.Initialize,
            InitializeParams,
            cancellationToken
        );
        await Connection.SendNotificationAsync(LspMethods.Initialized, new InitializedParams());
        return result;
    }

    public Task OpenAsync(string text) =>
        Connection.SendNotificationAsync(
            LspMethods.DidOpen,
            new DidOpenTextDocumentParams(new TextDocumentItem(Uri, "toy", 1, text))
        );

    public Task ChangeAsync(int version, DocumentRange range, string text) =>
        Connection.SendNotificationAsync(
            LspMethods.DidChange,
            new DidChangeTextDocumentParams(
                new VersionedTextDocumentIdentifier(Uri, version),
                [new TextDocumentContentChangeEvent(text, range)]
            )
        );

    public Task<CompletionList?> CompletionAsync(Position position, CancellationToken cancellationToken) =>
        Connection.SendRequestAsync(
            LspMethods.Completion,
            new CompletionParams(new TextDocumentIdentifier(Uri), position),
            cancellationToken
        );

    public Task<Hover?> HoverAsync(Position position, CancellationToken cancellationToken) =>
        Connection.SendRequestAsync(
            LspMethods.Hover,
            new HoverParams(new TextDocumentIdentifier(Uri), position),
            cancellationToken
        );

    public Task<SignatureHelp?> SignatureHelpAsync(Position position, CancellationToken cancellationToken) =>
        Connection.SendRequestAsync(
            LspMethods.SignatureHelp,
            new SignatureHelpParams(new TextDocumentIdentifier(Uri), position),
            cancellationToken
        );

    public Task<SemanticTokens?> SemanticTokensAsync(DocumentRange range, CancellationToken cancellationToken) =>
        Connection.SendRequestAsync(
            LspMethods.SemanticTokensRange,
            new SemanticTokensRangeParams(new TextDocumentIdentifier(Uri), range),
            cancellationToken
        );

    /// <summary>Waits for diagnostics of <paramref name="version"/>, skipping older ones.</summary>
    public async Task<PublishDiagnosticsParams> DiagnosticsAsync(int? version, CancellationToken cancellationToken)
    {
        while (true)
        {
            PublishDiagnosticsParams diagnostics = await _diagnostics.Reader.ReadAsync(cancellationToken);
            if (diagnostics.Version == version)
                return diagnostics;
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await Connection.SendRequestAsync(LspMethods.Shutdown, default, cancellationToken);
        await Connection.SendNotificationAsync(LspMethods.Exit, default);
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

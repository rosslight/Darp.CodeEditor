using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Darp.CodeEditor.CSharpScript.Protocol;
using Darp.CodeEditor.Lsp;
using Microsoft.CodeAnalysis.Text;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>
/// The C# script language server: it keeps the open scripts in sync and answers for the version each request arrived
/// after. Everything here runs on the connection's read loop, so the scripts need no lock; the work of each answer
/// runs on the thread pool, on an immutable <see cref="ScriptSnapshot"/>.
/// </summary>
internal sealed class ScriptServer
{
    private static readonly string s_capabilities = $$"""
        {
          "capabilities": {
            "textDocumentSync": {{TextDocumentSyncKind.Incremental}},
            "hoverProvider": true,
            "completionProvider": { "resolveProvider": true, "triggerCharacters": ["."] },
            "signatureHelpProvider": { "triggerCharacters": ["(", ",", "[", "<", "{"], "retriggerCharacters": [")", "]", ">", "}"] },
            "semanticTokensProvider": {
              "legend": {
                "tokenTypes": {{JsonSerializer.Serialize(ScriptSemanticTokens.TokenTypes)}},
                "tokenModifiers": {{JsonSerializer.Serialize(ScriptSemanticTokens.TokenModifiers)}}
              },
              "range": true
            }
          }
        }
        """;

    // Diagnostics wait for a pause in typing, so that they don't compete with the completion a keystroke asks for.
    private static readonly TimeSpan s_typingPause = TimeSpan.FromMilliseconds(100);
    private readonly JsonRpcConnection _rpc;
    private readonly Dictionary<string, OpenScript> _scripts = [];

    /// <summary>The loaded environments, by their JSON: environments with equal settings share one.</summary>
    private readonly Dictionary<string, ScriptContext> _contexts = [];

    private int _lastCompletionList;

    public ScriptServer(Stream input, Stream output) =>
        _rpc = new JsonRpcConnection(input, output, HandleRequest, HandleNotification, startRequestsInOrder: true);

    /// <summary>Serves until the input ends.</summary>
    /// <returns>The process's exit code.</returns>
    public async Task<int> RunAsync()
    {
        _rpc.Start();
        return await _rpc.Ended.ConfigureAwait(false) is null ? 0 : 1;
    }

    private Task<JsonNode?> HandleRequest(string method, JsonElement parameters, CancellationToken cancellationToken) =>
        method switch
        {
            "initialize" => Task.FromResult(JsonNode.Parse(s_capabilities)),
            "shutdown" => Task.FromResult<JsonNode?>(null),
            "textDocument/hover" => Answer(
                parameters,
                LspJsonContext.Default.TextDocumentPositionParams,
                p => p.TextDocument,
                LspJsonContext.Default.LspHover,
                (script, p, token) => ScriptHover.GetAsync(script, p.Position, token),
                cancellationToken
            ),
            "textDocument/semanticTokens/range" => Answer(
                parameters,
                LspJsonContext.Default.SemanticTokensRangeParams,
                p => p.TextDocument,
                LspJsonContext.Default.LspSemanticTokens,
                (script, p, token) => ScriptSemanticTokens.GetAsync(script, p.Range, token),
                cancellationToken
            ),
            "textDocument/signatureHelp" => Answer(
                parameters,
                LspJsonContext.Default.SignatureHelpParams,
                p => p.TextDocument,
                LspJsonContext.Default.LspSignatureHelp,
                (script, p, token) => ScriptSignatureHelp.GetAsync(script, p, token),
                cancellationToken
            ),
            "textDocument/completion" => Complete(parameters, cancellationToken),
            "completionItem/resolve" => Resolve(parameters, cancellationToken),
            ScriptCompileParams.Method => Compile(parameters, cancellationToken),
            _ => throw new JsonRpcException(JsonRpcErrorCodes.MethodNotFound, $"Unknown method '{method}'."),
        };

    /// <summary>
    /// Takes the version of the script now, in the order of the messages, and answers on the thread pool. A script
    /// closed before the request has no answer.
    /// </summary>
    private Task<JsonNode?> Answer<TParams, TResult>(
        JsonElement json,
        JsonTypeInfo<TParams> paramsType,
        Func<TParams, TextDocumentIdentifier> document,
        JsonTypeInfo<TResult> resultType,
        Func<ScriptSnapshot, TParams, CancellationToken, Task<TResult?>> answer,
        CancellationToken cancellationToken
    )
        where TResult : class
    {
        TParams parameters = Read(json, paramsType);
        if (!_scripts.TryGetValue(document(parameters).Uri, out OpenScript? script))
            return Task.FromResult<JsonNode?>(null);
        ScriptSnapshot snapshot = script.Snapshot;
        return Serialized(
            Task.Run(() => answer(snapshot, parameters, cancellationToken), cancellationToken),
            resultType
        );
    }

    private Task<JsonNode?> Complete(JsonElement json, CancellationToken cancellationToken)
    {
        CompletionParams parameters = Read(json, LspJsonContext.Default.CompletionParams);
        if (!_scripts.TryGetValue(parameters.TextDocument.Uri, out OpenScript? script))
            return Task.FromResult<JsonNode?>(null);
        ScriptCompletions completions = script.Complete(parameters, ++_lastCompletionList, cancellationToken);
        return Serialized(completions.Items, LspJsonContext.Default.LspCompletionList);
    }

    private Task<JsonNode?> Resolve(JsonElement json, CancellationToken cancellationToken)
    {
        LspCompletionItem item = Read(json, LspJsonContext.Default.LspCompletionItem);
        Task<LspCompletionItem?> resolved =
            item.Data is JsonArray { Count: 2 } data
            && _scripts.Values.Select(script => script.Completions).FirstOrDefault(c => c?.Id == (int?)data[0])
                is { } list
            && (int?)data[1] is { } index
                ? Task.Run(() => list.ResolveAsync(item, index, cancellationToken), cancellationToken)
                : Task.FromResult<LspCompletionItem?>(item);
        return Serialized(resolved, LspJsonContext.Default.LspCompletionItem);
    }

    /// <summary>
    /// Compiles on the thread pool. An open script with this text in this environment lends its snapshot, whose
    /// compilation the editor's diagnostics already built; other text, as a version the document has left behind, opens
    /// on its own and takes no part in the document's life.
    /// </summary>
    private Task<JsonNode?> Compile(JsonElement json, CancellationToken cancellationToken)
    {
        ScriptCompileParams parameters = Read(json, ScriptProtocolJson.Default.ScriptCompileParams);
        ScriptContext context = ContextOf(parameters.Environment);
        SourceText text = SourceText.From(parameters.Text);
        ScriptSnapshot snapshot =
            _scripts
                .Values.Where(open => open.Context == context && open.Snapshot.Text.ContentEquals(text))
                .Select(open => open.Snapshot)
                .FirstOrDefault()
            ?? context.Open(parameters.Text, version: 0);
        return Task.Run(
            async () =>
                JsonSerializer.SerializeToNode(
                    await ScriptCompiler.CompileAsync(snapshot, cancellationToken).ConfigureAwait(false),
                    ScriptProtocolJson.Default.ScriptCompileResult
                ),
            cancellationToken
        );
    }

    private static async Task<JsonNode?> Serialized<T>(Task<T?> answer, JsonTypeInfo<T> type)
        where T : class =>
        await answer.ConfigureAwait(false) is { } value ? JsonSerializer.SerializeToNode(value, type) : null;

    private void HandleNotification(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "textDocument/didOpen":
                Open(Read(parameters, LspJsonContext.Default.DidOpenTextDocumentParams).TextDocument);
                break;
            case "textDocument/didChange":
                Change(Read(parameters, LspJsonContext.Default.DidChangeTextDocumentParams));
                break;
            case "textDocument/didClose":
                string uri = Read(parameters, LspJsonContext.Default.DidCloseTextDocumentParams).TextDocument.Uri;
                if (_scripts.Remove(uri, out OpenScript? closed))
                    closed.StopDiagnostics();
                break;
            case "exit":
                Environment.Exit(0);
                break;
        }
    }

    private static T Read<T>(JsonElement json, JsonTypeInfo<T> type) =>
        json.Deserialize(type) ?? throw new JsonException("The params are missing.");

    private void Open(TextDocumentItem document)
    {
        ScriptEnvironmentData environment =
            document.Data?.Deserialize(ScriptProtocolJson.Default.ScriptEnvironmentData)
            ?? throw new JsonException("A script needs its environment.");
        ScriptContext context = ContextOf(environment);
        var script = new OpenScript(document.Uri, context, context.Open(document.Text, document.Version));
        _scripts[document.Uri] = script;
        Analyze(script, TimeSpan.Zero);
    }

    /// <summary>The loaded environment with these settings, loading it the first time.</summary>
    private ScriptContext ContextOf(ScriptEnvironmentData environment)
    {
        string key = JsonSerializer.Serialize(environment, ScriptProtocolJson.Default.ScriptEnvironmentData);
        if (!_contexts.TryGetValue(key, out ScriptContext? context))
            _contexts[key] = context = new ScriptContext(environment);
        return context;
    }

    private void Change(DidChangeTextDocumentParams change)
    {
        if (!_scripts.TryGetValue(change.TextDocument.Uri, out OpenScript? script))
            return;
        script.Snapshot = script.Snapshot.WithChanges(change.ContentChanges, change.TextDocument.Version);
        Analyze(script, s_typingPause);
    }

    /// <summary>
    /// Publishes the diagnostics of the script's current version, in place of the work for the version before it, which
    /// then publishes nothing.
    /// </summary>
    private void Analyze(OpenScript script, TimeSpan delay)
    {
        CancellationToken cancellationToken = script.RestartDiagnostics();
        ScriptSnapshot snapshot = script.Snapshot;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<LspDiagnostic> diagnostics = await ScriptDiagnostics
                        .GetAsync(snapshot, cancellationToken)
                        .ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    var published = new PublishDiagnosticsParams(script.Uri, diagnostics, snapshot.Version);
                    _rpc.Notify(
                        "textDocument/publishDiagnostics",
                        writer =>
                            JsonSerializer.Serialize(writer, published, LspJsonContext.Default.PublishDiagnosticsParams)
                    );
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                // The end of this background work: its failure reaches the app's log through standard error.
                catch (Exception e)
                {
                    await Console.Error.WriteLineAsync($"Diagnostics failed: {e}").ConfigureAwait(false);
                }
            },
            CancellationToken.None
        );
    }
}

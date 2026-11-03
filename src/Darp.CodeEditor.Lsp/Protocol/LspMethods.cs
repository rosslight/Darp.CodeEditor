using System.Text.Json;

namespace Darp.CodeEditor.Lsp.Protocol;

/// <summary>The LSP methods of this library with their JSON contracts, for both ends of a connection.</summary>
public static class LspMethods
{
    public static RequestType<InitializeParams, InitializeResult> Initialize { get; } =
        new("initialize", Json.InitializeParams, Json.InitializeResult);

    public static NotificationType<InitializedParams> Initialized { get; } = new("initialized", Json.InitializedParams);

    /// <summary>Has no params and answers null.</summary>
    public static RequestType<JsonElement, JsonElement?> Shutdown { get; } =
        new("shutdown", Json.JsonElement, Json.NullableJsonElement);

    /// <summary>Has no params.</summary>
    public static NotificationType<JsonElement> Exit { get; } = new("exit", Json.JsonElement);

    public static NotificationType<DidOpenTextDocumentParams> DidOpen { get; } =
        new("textDocument/didOpen", Json.DidOpenTextDocumentParams);

    public static NotificationType<DidChangeTextDocumentParams> DidChange { get; } =
        new("textDocument/didChange", Json.DidChangeTextDocumentParams);

    public static NotificationType<DidCloseTextDocumentParams> DidClose { get; } =
        new("textDocument/didClose", Json.DidCloseTextDocumentParams);

    /// <summary>Answers null when nothing applies.</summary>
    public static RequestType<CompletionParams, CompletionList> Completion { get; } =
        new("textDocument/completion", Json.CompletionParams, Json.CompletionList);

    public static RequestType<CompletionItem, CompletionItem> CompletionResolve { get; } =
        new("completionItem/resolve", Json.CompletionItem, Json.CompletionItem);

    /// <summary>Answers null when there is nothing to show.</summary>
    public static RequestType<HoverParams, Hover> Hover { get; } =
        new("textDocument/hover", Json.HoverParams, Json.Hover);

    /// <summary>Answers null when no signature applies.</summary>
    public static RequestType<SignatureHelpParams, SignatureHelp> SignatureHelp { get; } =
        new("textDocument/signatureHelp", Json.SignatureHelpParams, Json.SignatureHelp);

    public static RequestType<SemanticTokensRangeParams, SemanticTokens> SemanticTokensRange { get; } =
        new("textDocument/semanticTokens/range", Json.SemanticTokensRangeParams, Json.SemanticTokens);

    public static NotificationType<PublishDiagnosticsParams> PublishDiagnostics { get; } =
        new("textDocument/publishDiagnostics", Json.PublishDiagnosticsParams);

    public static NotificationType<LogMessageParams> LogMessage { get; } =
        new("window/logMessage", Json.LogMessageParams);

    public static NotificationType<LogMessageParams> ShowMessage { get; } =
        new("window/showMessage", Json.LogMessageParams);

    public static NotificationType<CancelParams> CancelRequest { get; } = new("$/cancelRequest", Json.CancelParams);

    private static LspJsonContext Json => LspJsonContext.Default;
}

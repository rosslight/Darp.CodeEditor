using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darp.CodeEditor.Lsp.Protocol;

/// <param name="InitializationOptions">Options of the server, kept as sent.</param>
public sealed record InitializeParams(
    ClientCapabilities Capabilities,
    int? ProcessId = null,
    ClientInfo? ClientInfo = null,
    string? Locale = null,
    string? RootUri = null,
    IReadOnlyList<WorkspaceFolder>? WorkspaceFolders = null,
    JsonElement? InitializationOptions = null,
    string? Trace = null
);

public sealed record ClientInfo(string Name, string? Version = null);

public sealed record WorkspaceFolder(string Uri, string Name);

public sealed record InitializeResult(ServerCapabilities Capabilities, ServerInfo? ServerInfo = null);

public sealed record ServerInfo(string Name, string? Version = null);

/// <summary>The client capabilities that the editor and its servers use.</summary>
public sealed record ClientCapabilities(
    GeneralClientCapabilities? General = null,
    TextDocumentClientCapabilities? TextDocument = null,
    WindowClientCapabilities? Window = null,
    WorkspaceClientCapabilities? Workspace = null,
    JsonElement? Experimental = null
);

/// <param name="PositionEncodings">Such as <c>utf-16</c>, the encoding of <see cref="Position.Character"/> here.</param>
public sealed record GeneralClientCapabilities(IReadOnlyList<string>? PositionEncodings = null);

public sealed record WindowClientCapabilities(bool? WorkDoneProgress = null);

public sealed record WorkspaceClientCapabilities(bool? Configuration = null, bool? WorkspaceFolders = null);

public sealed record TextDocumentClientCapabilities(
    CompletionClientCapabilities? Completion = null,
    HoverClientCapabilities? Hover = null,
    SignatureHelpClientCapabilities? SignatureHelp = null,
    PublishDiagnosticsClientCapabilities? PublishDiagnostics = null,
    SemanticTokensClientCapabilities? SemanticTokens = null
);

public sealed record CompletionClientCapabilities(
    CompletionItemClientCapabilities? CompletionItem = null,
    bool? ContextSupport = null,
    CompletionListClientCapabilities? CompletionList = null
);

public sealed record CompletionItemClientCapabilities(
    bool? SnippetSupport = null,
    bool? CommitCharactersSupport = null,
    IReadOnlyList<string>? DocumentationFormat = null,
    bool? DeprecatedSupport = null,
    bool? PreselectSupport = null,
    bool? InsertReplaceSupport = null,
    CompletionResolveSupport? ResolveSupport = null,
    bool? LabelDetailsSupport = null
);

/// <param name="Properties">The item properties that <c>completionItem/resolve</c> may fill in.</param>
public sealed record CompletionResolveSupport(IReadOnlyList<string> Properties);

/// <param name="ItemDefaults">The <see cref="CompletionItemDefaults"/> properties the client supports.</param>
public sealed record CompletionListClientCapabilities(IReadOnlyList<string>? ItemDefaults = null);

public sealed record HoverClientCapabilities(IReadOnlyList<string>? ContentFormat = null);

public sealed record SignatureHelpClientCapabilities(
    SignatureInformationClientCapabilities? SignatureInformation = null,
    bool? ContextSupport = null
);

public sealed record SignatureInformationClientCapabilities(
    IReadOnlyList<string>? DocumentationFormat = null,
    ParameterInformationClientCapabilities? ParameterInformation = null,
    bool? ActiveParameterSupport = null
);

public sealed record ParameterInformationClientCapabilities(bool? LabelOffsetSupport = null);

public sealed record PublishDiagnosticsClientCapabilities(
    bool? VersionSupport = null,
    DiagnosticTagSupport? TagSupport = null
);

public sealed record DiagnosticTagSupport(IReadOnlyList<DiagnosticTag> ValueSet);

/// <param name="Formats">Such as <c>relative</c>, the only format LSP defines.</param>
public sealed record SemanticTokensClientCapabilities(
    SemanticTokensRequests Requests,
    IReadOnlyList<string> TokenTypes,
    IReadOnlyList<string> TokenModifiers,
    IReadOnlyList<string> Formats,
    bool? MultilineTokenSupport = null,
    bool? OverlappingTokenSupport = null
);

public sealed record SemanticTokensRequests(
    [property: JsonConverter(typeof(BooleanOrOptionsConverter))] bool? Range = null,
    [property: JsonConverter(typeof(BooleanOrOptionsConverter))] bool? Full = null
);

/// <summary>The server capabilities that the editor and its servers use.</summary>
/// <param name="PositionEncoding">The encoding of <see cref="Position.Character"/>; <c>utf-16</c> when left out.</param>
public sealed record ServerCapabilities(
    string? PositionEncoding = null,
    [property: JsonConverter(typeof(TextDocumentSyncConverter))] TextDocumentSyncOptions? TextDocumentSync = null,
    CompletionOptions? CompletionProvider = null,
    [property: JsonConverter(typeof(BooleanOrOptionsConverter))] bool? HoverProvider = null,
    SignatureHelpOptions? SignatureHelpProvider = null,
    SemanticTokensOptions? SemanticTokensProvider = null,
    JsonElement? Experimental = null
);

public sealed record TextDocumentSyncOptions(bool? OpenClose = null, TextDocumentSyncKind? Change = null);

public enum TextDocumentSyncKind
{
    None = 0,
    Full = 1,
    Incremental = 2,
}

public sealed record CompletionOptions(
    IReadOnlyList<string>? TriggerCharacters = null,
    IReadOnlyList<string>? AllCommitCharacters = null,
    bool? ResolveProvider = null
);

public sealed record SignatureHelpOptions(
    IReadOnlyList<string>? TriggerCharacters = null,
    IReadOnlyList<string>? RetriggerCharacters = null
);

public sealed record SemanticTokensOptions(
    SemanticTokensLegend Legend,
    [property: JsonConverter(typeof(BooleanOrOptionsConverter))] bool? Range = null,
    [property: JsonConverter(typeof(BooleanOrOptionsConverter))] bool? Full = null
);

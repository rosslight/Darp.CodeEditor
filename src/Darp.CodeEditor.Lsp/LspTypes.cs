using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darp.CodeEditor.Lsp;

// The subset of the Language Server Protocol 3.17 the adapter speaks. Positions are UTF-16 code units, like .NET strings.

internal readonly record struct Position(int Line, int Character);

internal readonly record struct Range(Position Start, Position End);

internal sealed record TextDocumentIdentifier(string Uri);

internal sealed record VersionedTextDocumentIdentifier(string Uri, int Version);

internal sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);

internal sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);

/// <param name="Range">The replaced range; null means <paramref name="Text"/> is the whole new document.</param>
internal sealed record TextDocumentContentChangeEvent(Range? Range, string Text);

internal sealed record DidChangeTextDocumentParams(
    VersionedTextDocumentIdentifier TextDocument,
    TextDocumentContentChangeEvent[] ContentChanges
);

internal sealed record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);

internal sealed record CompletionContext(int TriggerKind, string? TriggerCharacter);

internal sealed record TextDocumentPositionParams(
    TextDocumentIdentifier TextDocument,
    Position Position,
    CompletionContext? Context
);

internal sealed record SemanticTokensParams(TextDocumentIdentifier TextDocument, Range? Range);

internal sealed record CancelParams(JsonElement Id);

/// <summary>The client capabilities the adapter adapts to.</summary>
internal sealed record InitializeParams(ClientCapabilities? Capabilities);

internal sealed record ClientCapabilities(TextDocumentClientCapabilities? TextDocument);

internal sealed record TextDocumentClientCapabilities(CompletionClientCapabilities? Completion);

internal sealed record CompletionClientCapabilities(CompletionListCapabilities? CompletionList);

internal sealed record CompletionListCapabilities(string[]? ItemDefaults);

internal sealed record InitializeResult(ServerCapabilities Capabilities, ServerInfo ServerInfo);

internal sealed record ServerInfo(string Name);

internal sealed record ServerCapabilities(
    string PositionEncoding,
    TextDocumentSyncOptions TextDocumentSync,
    CompletionOptions CompletionProvider,
    bool HoverProvider,
    SignatureHelpOptions SignatureHelpProvider,
    SemanticTokensOptions SemanticTokensProvider
);

/// <param name="Change">2: incremental.</param>
internal sealed record TextDocumentSyncOptions(bool OpenClose, int Change);

internal sealed record CompletionOptions(string[] TriggerCharacters, bool ResolveProvider);

internal sealed record SignatureHelpOptions(string[] TriggerCharacters, string[] RetriggerCharacters);

internal sealed record SemanticTokensOptions(SemanticTokensLegend Legend, bool Range, bool Full);

internal sealed record SemanticTokensLegend(string[] TokenTypes, string[] TokenModifiers);

internal sealed record CompletionListResult(
    bool IsIncomplete,
    CompletionItemDefaults? ItemDefaults,
    LspCompletionItem[] Items
);

internal sealed record CompletionItemDefaults(Range EditRange, string[] CommitCharacters);

/// <param name="Data">Identifies the item for completionItem/resolve.</param>
internal sealed record LspCompletionItem(
    string Label,
    int Kind,
    string? Detail,
    string SortText,
    string FilterText,
    string? TextEditText,
    TextEdit? TextEdit,
    MarkupContent? Documentation,
    CompletionItemData Data
);

internal sealed record CompletionItemData(string Uri, long ResultId, int Index);

internal sealed record TextEdit(Range Range, string NewText);

internal sealed record MarkupContent(string Kind, string Value)
{
    public static MarkupContent PlainText(string value) => new("plaintext", value);
}

internal sealed record Hover(MarkupContent Contents, Range Range);

internal sealed record SignatureHelpResult(SignatureInformation[] Signatures, int ActiveSignature, int ActiveParameter);

internal sealed record SignatureInformation(
    string Label,
    MarkupContent? Documentation,
    ParameterInformation[] Parameters
);

/// <param name="Label">Start and end offset of the parameter in the signature label.</param>
internal sealed record ParameterInformation(int[] Label, MarkupContent? Documentation);

internal sealed record PublishDiagnosticsParams(string Uri, int Version, LspDiagnostic[] Diagnostics);

/// <param name="Severity">1 error, 2 warning, 3 information.</param>
internal sealed record LspDiagnostic(Range Range, int Severity, string Code, string Source, string Message);

internal sealed record SemanticTokens(int[] Data);

internal sealed record ResponseError(int Code, string Message);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(DidOpenTextDocumentParams))]
[JsonSerializable(typeof(DidChangeTextDocumentParams))]
[JsonSerializable(typeof(DidCloseTextDocumentParams))]
[JsonSerializable(typeof(TextDocumentPositionParams))]
[JsonSerializable(typeof(SemanticTokensParams))]
[JsonSerializable(typeof(CancelParams))]
[JsonSerializable(typeof(CompletionListResult))]
[JsonSerializable(typeof(LspCompletionItem))]
[JsonSerializable(typeof(Hover))]
[JsonSerializable(typeof(SignatureHelpResult))]
[JsonSerializable(typeof(PublishDiagnosticsParams))]
[JsonSerializable(typeof(SemanticTokens))]
[JsonSerializable(typeof(ResponseError))]
internal sealed partial class LspJsonContext : JsonSerializerContext;

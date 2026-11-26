using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Darp.CodeEditor.Lsp;

// The part of the Language Server Protocol 3.17 that the library uses. Names follow the specification; positions,
// ranges and diagnostics carry an Lsp prefix to stay apart from the editor's own types. Optional members have defaults:
// a missing required member makes the message invalid.

/// <summary>A position in a document: a line and a character, both from 0, in UTF-16 code units.</summary>
internal sealed record LspPosition(int Line, int Character);

/// <summary>A range in a document, from <see cref="Start"/> up to <see cref="End"/>.</summary>
internal sealed record LspRange(LspPosition Start, LspPosition End);

internal sealed record WorkspaceFolder(string Uri, string Name);

internal sealed record InitializeParams(
    int ProcessId,
    // Required by the protocol, even when null.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RootUri,
    IReadOnlyList<WorkspaceFolder>? WorkspaceFolders,
    JsonNode? InitializationOptions,
    JsonNode Capabilities
);

internal sealed record InitializeResult(ServerCapabilities Capabilities);

/// <param name="TextDocumentSync">A <c>TextDocumentSyncKind</c> number, or an object with a <c>change</c> kind.</param>
/// <param name="HoverProvider">True, or an object of options.</param>
/// <param name="SemanticTokensProvider">An object with a <c>legend</c>, and <c>range</c> and <c>full</c> when offered.</param>
/// <param name="CompletionProvider">An object with trigger and commit characters, and whether items resolve.</param>
/// <param name="SignatureHelpProvider">An object with trigger and retrigger characters.</param>
/// <param name="DocumentHighlightProvider">True, or an object of options.</param>
internal sealed record ServerCapabilities(
    JsonElement? TextDocumentSync = null,
    JsonElement? HoverProvider = null,
    JsonElement? SemanticTokensProvider = null,
    JsonElement? CompletionProvider = null,
    JsonElement? SignatureHelpProvider = null,
    JsonElement? DocumentHighlightProvider = null
);

/// <param name="Uri">The document's URI.</param>
/// <param name="LanguageId">The language's identifier.</param>
/// <param name="Version">The version of the text.</param>
/// <param name="Text">The text.</param>
/// <param name="Data">What the server needs beyond the text, in its own format; not in the protocol, so null for most.</param>
internal sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text, JsonNode? Data = null);

internal sealed record TextDocumentIdentifier(string Uri);

internal sealed record VersionedTextDocumentIdentifier(string Uri, int Version);

internal sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);

/// <summary>A change of a document's text: <see cref="Text"/> replaces <see cref="Range"/>, or the whole text without one.</summary>
internal sealed record TextDocumentContentChangeEvent(string Text, LspRange? Range = null);

internal sealed record DidChangeTextDocumentParams(
    VersionedTextDocumentIdentifier TextDocument,
    IReadOnlyList<TextDocumentContentChangeEvent> ContentChanges
);

internal sealed record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);

internal sealed record PublishDiagnosticsParams(
    string Uri,
    IReadOnlyList<LspDiagnostic> Diagnostics,
    int? Version = null
);

/// <summary>A server's diagnostic.</summary>
/// <param name="Range">The range it is about.</param>
/// <param name="Message">The text for users.</param>
/// <param name="Severity">1 for errors, 2 warnings, 3 information, 4 hints; null leaves it to the client.</param>
/// <param name="Code">A number or a string, read as text.</param>
/// <param name="Source">What reported it, such as a linter's name.</param>
/// <param name="CodeDescription">The web page that explains the code.</param>
internal sealed record LspDiagnostic(
    LspRange Range,
    string Message,
    int? Severity = null,
    [property: JsonConverter(typeof(DiagnosticCodeConverter))] string? Code = null,
    string? Source = null,
    LspCodeDescription? CodeDescription = null
);

/// <summary>A diagnostic's <c>codeDescription</c>.</summary>
/// <param name="Href">The address of the page.</param>
internal sealed record LspCodeDescription(string Href);

/// <summary>The params of <c>textDocument/hover</c> and <c>textDocument/documentHighlight</c>.</summary>
internal sealed record TextDocumentPositionParams(TextDocumentIdentifier TextDocument, LspPosition Position);

/// <summary>The params of <c>textDocument/semanticTokens/full</c>.</summary>
internal sealed record SemanticTokensParams(TextDocumentIdentifier TextDocument);

/// <summary>The params of <c>textDocument/semanticTokens/range</c>.</summary>
internal sealed record SemanticTokensRangeParams(TextDocumentIdentifier TextDocument, LspRange Range);

/// <summary>The params of <c>textDocument/completion</c>.</summary>
internal sealed record CompletionParams(
    TextDocumentIdentifier TextDocument,
    LspPosition Position,
    CompletionContext Context
);

/// <summary>How completion was asked for.</summary>
/// <param name="TriggerKind">One of <see cref="CompletionTriggerKind"/>.</param>
/// <param name="TriggerCharacter">The character typed, for a trigger character.</param>
internal sealed record CompletionContext(int TriggerKind, string? TriggerCharacter = null);

/// <summary>The params of <c>textDocument/signatureHelp</c>.</summary>
internal sealed record SignatureHelpParams(
    TextDocumentIdentifier TextDocument,
    LspPosition Position,
    SignatureHelpContext Context
);

/// <summary>How signature help was asked for.</summary>
/// <param name="TriggerKind">One of <see cref="SignatureHelpTriggerKind"/>.</param>
/// <param name="IsRetrigger">Whether signature help showed when it was asked for.</param>
/// <param name="TriggerCharacter">The character typed, for a trigger character.</param>
/// <param name="ActiveSignatureHelp">The signature help that showed, with the signature the user chose.</param>
internal sealed record SignatureHelpContext(
    int TriggerKind,
    bool IsRetrigger,
    string? TriggerCharacter = null,
    JsonNode? ActiveSignatureHelp = null
);

internal sealed record ConfigurationParams(IReadOnlyList<ConfigurationItem> Items);

internal sealed record ConfigurationItem(string? ScopeUri = null, string? Section = null);

/// <param name="Settings">All settings; some servers fail on null.</param>
internal sealed record DidChangeConfigurationParams(JsonNode Settings);

/// <summary>Text with its format, <c>markdown</c> or <c>plaintext</c>.</summary>
internal sealed record LspMarkupContent(string Kind, string Value);

/// <summary>The answer to <c>textDocument/hover</c>.</summary>
internal sealed record LspHover(LspMarkupContent Contents, LspRange? Range = null);

/// <summary>An edit of a document's text.</summary>
internal sealed record LspTextEdit(LspRange Range, string NewText);

/// <summary>A completion item, as a server answers it and as <c>completionItem/resolve</c> gets it back.</summary>
/// <param name="Label">What the list shows.</param>
/// <param name="Kind">A <c>CompletionItemKind</c> number.</param>
/// <param name="TextEdit">What committing it changes.</param>
/// <param name="Data">The server's own data, which comes back to resolve the item.</param>
/// <param name="SortText">What orders the item before anything is typed; null for its label.</param>
/// <param name="FilterText">What typing is matched against; null for its label.</param>
/// <param name="Preselect">Whether the item is selected first.</param>
/// <param name="Detail">Its signature, once resolved.</param>
/// <param name="Documentation">Its documentation, once resolved.</param>
/// <param name="Tags">Its <c>CompletionItemTag</c> numbers, such as <see cref="CompletionItemTag.Deprecated"/>.</param>
internal sealed record LspCompletionItem(
    string Label,
    int Kind,
    LspTextEdit TextEdit,
    JsonNode? Data = null,
    string? SortText = null,
    string? FilterText = null,
    bool? Preselect = null,
    string? Detail = null,
    LspMarkupContent? Documentation = null,
    IReadOnlyList<int>? Tags = null
);

/// <summary>The answer to <c>textDocument/completion</c>.</summary>
internal sealed record LspCompletionList(bool IsIncomplete, IReadOnlyList<LspCompletionItem> Items);

/// <summary>A parameter of a signature.</summary>
/// <param name="Label">Where it starts and ends in the signature's label.</param>
/// <param name="Documentation">What it is for; null for nothing.</param>
internal sealed record LspParameterInformation(IReadOnlyList<int> Label, LspMarkupContent? Documentation = null);

/// <summary>A signature of a call.</summary>
/// <param name="Label">The signature.</param>
/// <param name="Parameters">Its parameters.</param>
/// <param name="ActiveParameter">The parameter at the caret; -1 for none.</param>
/// <param name="Documentation">What it does; null for nothing.</param>
internal sealed record LspSignatureInformation(
    string Label,
    IReadOnlyList<LspParameterInformation> Parameters,
    int ActiveParameter,
    LspMarkupContent? Documentation = null
);

/// <summary>The answer to <c>textDocument/signatureHelp</c>.</summary>
/// <param name="Signatures">The signatures of the call.</param>
/// <param name="ActiveSignature">The one that shows.</param>
/// <param name="Data">
/// The server's own data, outside the protocol: the client sends the answer back when it asks again.
/// </param>
internal sealed record LspSignatureHelp(
    IReadOnlyList<LspSignatureInformation> Signatures,
    int ActiveSignature,
    JsonNode? Data = null
);

/// <summary>An occurrence of a symbol, in the answer to <c>textDocument/documentHighlight</c>.</summary>
/// <param name="Range">Where it is.</param>
/// <param name="Kind">How it uses the symbol, one of <see cref="DocumentHighlightKind"/>.</param>
internal sealed record LspDocumentHighlight(LspRange Range, int Kind);

/// <summary>The answer to <c>textDocument/semanticTokens/range</c>, in the protocol's relative format.</summary>
internal sealed record LspSemanticTokens(IReadOnlyList<int> Data);

/// <summary>Reads a diagnostic's code, a number or a string, as text.</summary>
internal sealed class DiagnosticCodeConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number ? Encoding.UTF8.GetString(reader.ValueSpan) : reader.GetString();

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>The kinds of <c>TextDocumentSyncKind</c>.</summary>
internal static class TextDocumentSyncKind
{
    public const int None = 0;
    public const int Full = 1;
    public const int Incremental = 2;
}

/// <summary>The kinds of <c>CompletionTriggerKind</c>.</summary>
internal static class CompletionTriggerKind
{
    public const int Invoked = 1;
    public const int TriggerCharacter = 2;
    public const int TriggerForIncompleteCompletions = 3;
}

/// <summary>The tags of <c>CompletionItemTag</c>.</summary>
internal static class CompletionItemTag
{
    /// <summary>The item is deprecated: it shows struck through.</summary>
    public const int Deprecated = 1;
}

/// <summary>The kinds of <c>DocumentHighlightKind</c>.</summary>
internal static class DocumentHighlightKind
{
    /// <summary>An occurrence that neither reads nor writes, such as a declaration.</summary>
    public const int Text = 1;

    public const int Read = 2;
    public const int Write = 3;
}

/// <summary>The kinds of <c>SignatureHelpTriggerKind</c>.</summary>
internal static class SignatureHelpTriggerKind
{
    public const int Invoked = 1;
    public const int TriggerCharacter = 2;
    public const int ContentChange = 3;
}

/// <summary>The source-generated serialization of the protocol's types, for NativeAOT.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true
)]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(DidOpenTextDocumentParams))]
[JsonSerializable(typeof(DidChangeTextDocumentParams))]
[JsonSerializable(typeof(DidCloseTextDocumentParams))]
[JsonSerializable(typeof(PublishDiagnosticsParams))]
[JsonSerializable(typeof(TextDocumentPositionParams))]
[JsonSerializable(typeof(SemanticTokensParams))]
[JsonSerializable(typeof(SemanticTokensRangeParams))]
[JsonSerializable(typeof(CompletionParams))]
[JsonSerializable(typeof(SignatureHelpParams))]
[JsonSerializable(typeof(ConfigurationParams))]
[JsonSerializable(typeof(DidChangeConfigurationParams))]
[JsonSerializable(typeof(LspHover))]
[JsonSerializable(typeof(LspCompletionList))]
[JsonSerializable(typeof(LspCompletionItem))]
[JsonSerializable(typeof(LspSignatureHelp))]
[JsonSerializable(typeof(LspSemanticTokens))]
[JsonSerializable(typeof(LspDocumentHighlight[]))]
internal sealed partial class LspJsonContext : JsonSerializerContext;

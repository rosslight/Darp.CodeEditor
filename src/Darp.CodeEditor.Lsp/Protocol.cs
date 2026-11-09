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
internal sealed record ServerCapabilities(
    JsonElement? TextDocumentSync = null,
    JsonElement? HoverProvider = null,
    JsonElement? SemanticTokensProvider = null
);

internal sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);

internal sealed record TextDocumentIdentifier(string Uri);

internal sealed record VersionedTextDocumentIdentifier(string Uri, int Version);

internal sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);

/// <summary>A change of a document's text: <see cref="Text"/> replaces <see cref="Range"/>, or the whole text without one.</summary>
internal sealed record TextDocumentContentChangeEvent(LspRange? Range, string Text);

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
internal sealed record LspDiagnostic(
    LspRange Range,
    string Message,
    int? Severity = null,
    [property: JsonConverter(typeof(DiagnosticCodeConverter))] string? Code = null
);

/// <summary>The params of <c>textDocument/hover</c>.</summary>
internal sealed record TextDocumentPositionParams(TextDocumentIdentifier TextDocument, LspPosition Position);

/// <summary>The params of <c>textDocument/semanticTokens/full</c>.</summary>
internal sealed record SemanticTokensParams(TextDocumentIdentifier TextDocument);

/// <summary>The params of <c>textDocument/semanticTokens/range</c>.</summary>
internal sealed record SemanticTokensRangeParams(TextDocumentIdentifier TextDocument, LspRange Range);

internal sealed record ConfigurationParams(IReadOnlyList<ConfigurationItem> Items);

internal sealed record ConfigurationItem(string? ScopeUri = null, string? Section = null);

/// <param name="Settings">All settings; some servers fail on null.</param>
internal sealed record DidChangeConfigurationParams(JsonNode Settings);

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
[JsonSerializable(typeof(ConfigurationParams))]
[JsonSerializable(typeof(DidChangeConfigurationParams))]
internal sealed partial class LspJsonContext : JsonSerializerContext;

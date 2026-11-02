using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darp.CodeEditor.Lsp.Protocol;

/// <summary>Source-generated JSON contracts of the protocol types.</summary>
/// <remarks>
/// Unknown members are ignored. Members that LSP requires are required, and null only where the types say so.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true
)]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(InitializedParams))]
[JsonSerializable(typeof(DidOpenTextDocumentParams))]
[JsonSerializable(typeof(DidChangeTextDocumentParams))]
[JsonSerializable(typeof(DidCloseTextDocumentParams))]
[JsonSerializable(typeof(CompletionParams))]
[JsonSerializable(typeof(CompletionList))]
[JsonSerializable(typeof(CompletionItemDefaults))]
[JsonSerializable(typeof(IReadOnlyList<CompletionItem>))]
[JsonSerializable(typeof(CompletionItem))]
[JsonSerializable(typeof(HoverParams))]
[JsonSerializable(typeof(Hover))]
[JsonSerializable(typeof(SignatureHelpParams))]
[JsonSerializable(typeof(SignatureHelp))]
[JsonSerializable(typeof(PublishDiagnosticsParams))]
[JsonSerializable(typeof(SemanticTokensRangeParams))]
[JsonSerializable(typeof(SemanticTokens))]
[JsonSerializable(typeof(CancelParams))]
[JsonSerializable(typeof(LogMessageParams))]
[JsonSerializable(typeof(ConfigurationParams))]
[JsonSerializable(typeof(IReadOnlyList<JsonElement?>))]
[JsonSerializable(typeof(RegistrationParams))]
[JsonSerializable(typeof(UnregistrationParams))]
[JsonSerializable(typeof(WorkDoneProgressCreateParams))]
[JsonSerializable(typeof(TextDocumentSyncOptions))]
[JsonSerializable(typeof(DocumentRange))]
[JsonSerializable(typeof(MarkupContent))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonElement?))]
public sealed partial class LspJsonContext : JsonSerializerContext;

/// <summary>JSON contracts that only this library uses.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true
)]
[JsonSerializable(typeof(EditShape))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(ResponseError))]
internal sealed partial class InternalJsonContext : JsonSerializerContext;

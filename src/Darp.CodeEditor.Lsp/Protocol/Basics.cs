using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darp.CodeEditor.Lsp.Protocol;

/// <summary>A position in a document. <see cref="Character"/> counts UTF-16 code units, as C# strings do.</summary>
public readonly record struct Position(int Line, int Character);

/// <summary>A range in a document, with an exclusive <see cref="End"/>. LSP calls it <c>Range</c>.</summary>
public readonly record struct DocumentRange(Position Start, Position End);

public sealed record TextEdit(DocumentRange Range, string NewText);

/// <param name="Uri">The document URI, kept exactly as the client sent it.</param>
public sealed record TextDocumentIdentifier(string Uri);

/// <summary>Formatted text, <see cref="MarkupKind.PlainText"/> or <see cref="MarkupKind.Markdown"/>.</summary>
public sealed record MarkupContent(string Kind, string Value);

public static class MarkupKind
{
    public const string PlainText = "plaintext";
    public const string Markdown = "markdown";
}

/// <summary>Parameters of <c>initialized</c>.</summary>
public sealed record InitializedParams;

/// <summary>Parameters of <c>$/cancelRequest</c>.</summary>
public sealed record CancelParams(RequestId Id);

/// <summary>Parameters of <c>window/logMessage</c> and of <c>window/showMessage</c>.</summary>
public sealed record LogMessageParams(MessageType Type, string Message);

public enum MessageType
{
    Error = 1,
    Warning = 2,
    Info = 3,
    Log = 4,
    Debug = 5,
}

/// <param name="Uri">The document URI, as the client sent it in <c>textDocument/didOpen</c>.</param>
/// <param name="Version">The document version the diagnostics were computed for.</param>
public sealed record PublishDiagnosticsParams(string Uri, IReadOnlyList<Diagnostic> Diagnostics, int? Version = null);

/// <param name="Code">The diagnostic's code; a numeric code is read as its digits.</param>
public sealed record Diagnostic(
    DocumentRange Range,
    string Message,
    DiagnosticSeverity? Severity = null,
    [property: JsonConverter(typeof(StringOrNumberConverter))] string? Code = null,
    string? Source = null,
    IReadOnlyList<DiagnosticTag>? Tags = null,
    JsonElement? Data = null
);

public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

public enum DiagnosticTag
{
    Unnecessary = 1,
    Deprecated = 2,
}

public sealed record SemanticTokensRangeParams(TextDocumentIdentifier TextDocument, DocumentRange Range);

/// <summary>Parameters of <c>textDocument/semanticTokens/full</c>, for servers that classify only whole documents.</summary>
public sealed record SemanticTokensParams(TextDocumentIdentifier TextDocument);

/// <param name="Data">
/// Five integers per token: line and start character relative to the previous token, length, index into
/// <see cref="SemanticTokensLegend.TokenTypes"/>, and modifier bits.
/// </param>
public sealed record SemanticTokens(int[] Data, string? ResultId = null);

public sealed record SemanticTokensLegend(IReadOnlyList<string> TokenTypes, IReadOnlyList<string> TokenModifiers);

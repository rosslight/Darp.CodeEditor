using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darp.CodeEditor.Lsp.Protocol;

public sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);

public sealed record VersionedTextDocumentIdentifier(string Uri, int Version);

public sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);

/// <param name="ContentChanges">In order; the range of each change refers to the text after the previous ones.</param>
public sealed record DidChangeTextDocumentParams(
    VersionedTextDocumentIdentifier TextDocument,
    IReadOnlyList<TextDocumentContentChangeEvent> ContentChanges
);

/// <param name="Range">The replaced range; null replaces the whole text.</param>
public sealed record TextDocumentContentChangeEvent(string Text, DocumentRange? Range = null);

public sealed record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);

public sealed record CompletionParams(
    TextDocumentIdentifier TextDocument,
    Position Position,
    CompletionContext? Context = null
);

public sealed record CompletionContext(CompletionTriggerKind TriggerKind, string? TriggerCharacter = null);

public enum CompletionTriggerKind
{
    Invoked = 1,
    TriggerCharacter = 2,
    TriggerForIncompleteCompletions = 3,
}

/// <summary>The result of <c>textDocument/completion</c>; also reads the other form of the result, an array of items.</summary>
/// <param name="IsIncomplete">Whether typing further should request a new list instead of filtering this one.</param>
[JsonConverter(typeof(CompletionListConverter))]
public sealed record CompletionList(
    IReadOnlyList<CompletionItem> Items,
    bool IsIncomplete = false,
    CompletionItemDefaults? ItemDefaults = null
);

/// <summary>Values for the items of a <see cref="CompletionList"/> that leave them out.</summary>
/// <param name="EditRange">The range of items without <see cref="CompletionItem.TextEdit"/>.</param>
public sealed record CompletionItemDefaults(
    IReadOnlyList<string>? CommitCharacters = null,
    CompletionEditRange? EditRange = null,
    InsertTextFormat? InsertTextFormat = null,
    JsonElement? Data = null
);

/// <param name="TextEditText">The new text for <see cref="CompletionItemDefaults.EditRange"/>.</param>
/// <param name="Data">Data of the server for <c>completionItem/resolve</c>, kept verbatim.</param>
public sealed record CompletionItem(
    string Label,
    CompletionItemLabelDetails? LabelDetails = null,
    CompletionItemKind? Kind = null,
    string? Detail = null,
    [property: JsonConverter(typeof(MarkupContentConverter))] MarkupContent? Documentation = null,
    bool? Deprecated = null,
    bool? Preselect = null,
    string? SortText = null,
    string? FilterText = null,
    string? InsertText = null,
    InsertTextFormat? InsertTextFormat = null,
    CompletionTextEdit? TextEdit = null,
    string? TextEditText = null,
    IReadOnlyList<TextEdit>? AdditionalTextEdits = null,
    IReadOnlyList<string>? CommitCharacters = null,
    JsonElement? Data = null
)
{
    /// <summary>Members this type does not model, such as <c>command</c>, so that resolving sends them back.</summary>
    /// <remarks>Settable, because the serializer cannot fill extension data through the constructor.</remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed record CompletionItemLabelDetails(string? Detail = null, string? Description = null);

/// <summary>
/// The edit of a completion item: LSP's <c>TextEdit</c> when <see cref="Insert"/> and <see cref="Replace"/> are equal,
/// otherwise an <c>InsertReplaceEdit</c>.
/// </summary>
/// <param name="Insert">The range to replace when the item is inserted, usually the word up to the caret.</param>
/// <param name="Replace">The range to replace when the item replaces the word, usually the whole word.</param>
[JsonConverter(typeof(CompletionTextEditConverter))]
public readonly record struct CompletionTextEdit(string NewText, DocumentRange Insert, DocumentRange Replace)
{
    public CompletionTextEdit(string newText, DocumentRange range)
        : this(newText, range, range) { }
}

/// <summary>
/// <see cref="CompletionItemDefaults.EditRange"/>: LSP's <c>Range</c> when <see cref="Insert"/> and
/// <see cref="Replace"/> are equal, otherwise <c>{ insert, replace }</c>.
/// </summary>
[JsonConverter(typeof(CompletionEditRangeConverter))]
public readonly record struct CompletionEditRange(DocumentRange Insert, DocumentRange Replace)
{
    public CompletionEditRange(DocumentRange range)
        : this(range, range) { }
}

public enum CompletionItemKind
{
    Text = 1,
    Method = 2,
    Function = 3,
    Constructor = 4,
    Field = 5,
    Variable = 6,
    Class = 7,
    Interface = 8,
    Module = 9,
    Property = 10,
    Unit = 11,
    Value = 12,
    Enum = 13,
    Keyword = 14,
    Snippet = 15,
    Color = 16,
    File = 17,
    Reference = 18,
    Folder = 19,
    EnumMember = 20,
    Constant = 21,
    Struct = 22,
    Event = 23,
    Operator = 24,
    TypeParameter = 25,
}

public enum InsertTextFormat
{
    PlainText = 1,
    Snippet = 2,
}

public sealed record HoverParams(TextDocumentIdentifier TextDocument, Position Position);

/// <param name="Contents">Also read from a plain string, a marked string in Markdown; arrays of them are not read.</param>
public sealed record Hover(
    [property: JsonConverter(typeof(HoverContentsConverter))] MarkupContent Contents,
    DocumentRange? Range = null
);

public sealed record SignatureHelpParams(
    TextDocumentIdentifier TextDocument,
    Position Position,
    SignatureHelpContext? Context = null
);

public sealed record SignatureHelpContext(
    SignatureHelpTriggerKind TriggerKind,
    bool IsRetrigger,
    string? TriggerCharacter = null,
    SignatureHelp? ActiveSignatureHelp = null
);

public enum SignatureHelpTriggerKind
{
    Invoked = 1,
    TriggerCharacter = 2,
    ContentChange = 3,
}

public sealed record SignatureHelp(
    IReadOnlyList<SignatureInformation> Signatures,
    int? ActiveSignature = null,
    int? ActiveParameter = null
);

public sealed record SignatureInformation(
    string Label,
    [property: JsonConverter(typeof(MarkupContentConverter))] MarkupContent? Documentation = null,
    IReadOnlyList<ParameterInformation>? Parameters = null,
    int? ActiveParameter = null
);

public sealed record ParameterInformation(
    ParameterLabel Label,
    [property: JsonConverter(typeof(MarkupContentConverter))] MarkupContent? Documentation = null
);

/// <summary>
/// A parameter's label: its text, or its range in <see cref="SignatureInformation.Label"/> as UTF-16 offsets with an
/// exclusive end.
/// </summary>
[JsonConverter(typeof(ParameterLabelConverter))]
public readonly record struct ParameterLabel
{
    public ParameterLabel(string text) => Text = text;

    public ParameterLabel(int start, int end)
    {
        Start = start;
        End = end;
    }

    /// <summary>The label, or null when the label is given by <see cref="Start"/> and <see cref="End"/>.</summary>
    public string? Text { get; }

    public int Start { get; }

    public int End { get; }
}

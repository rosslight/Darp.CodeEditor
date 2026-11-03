using System.Text;

namespace Darp.CodeEditor.LanguageServices;

/// <summary>The full text of a document at one version.</summary>
/// <param name="DocumentId">Identifies the document across versions.</param>
/// <param name="Version">Increases with every change of the document.</param>
public sealed record DocumentSnapshot(string DocumentId, int Version, string Text);

/// <summary>One version of a document, which a request is made for.</summary>
public readonly record struct DocumentVersion(string DocumentId, int Version);

/// <summary>A change of a document to <see cref="Version"/>, one higher than the version it changes.</summary>
public sealed record DocumentChange(string DocumentId, int Version, TextChange Change);

/// <summary>Replaces <see cref="RemovedLength"/> characters at <see cref="Offset"/> of the previous version.</summary>
public readonly record struct TextChange(int Offset, int RemovedLength, string InsertedText);

/// <summary>A range of characters, as offsets into the document text.</summary>
public readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;

    public static TextRange FromBounds(int start, int end) => new(start, end - start);
}

/// <summary>Replaces a range of a document version with new text.</summary>
public sealed record TextEdit(TextRange Range, string NewText);

/// <param name="CompletionTriggerCharacters">Characters that open completion after they are typed, such as '.'.</param>
/// <param name="CompletionCommitCharacters">
/// Characters that commit the selected completion item before they are inserted, unless the item has its own.
/// </param>
/// <param name="SignatureHelpTriggerCharacters">Characters that open or update signature help, such as '(' and ','.</param>
public sealed record LanguageServiceCapabilities(
    IReadOnlyList<char> CompletionTriggerCharacters,
    IReadOnlyList<char> CompletionCommitCharacters,
    IReadOnlyList<char> SignatureHelpTriggerCharacters
)
{
    public static LanguageServiceCapabilities None { get; } = new([], [], []);
}

/// <param name="Version">The document version of <see cref="Diagnostics"/>; the editor drops older ones.</param>
public sealed class DiagnosticsChangedEventArgs(
    string documentId,
    int version,
    IReadOnlyList<DiagnosticInfo> diagnostics
) : EventArgs
{
    public string DocumentId { get; } = documentId;

    public int Version { get; } = version;

    public IReadOnlyList<DiagnosticInfo> Diagnostics { get; } = diagnostics;
}

public enum CompletionTriggerKind
{
    /// <summary>The user asked for completion explicitly, for example with Ctrl+Space.</summary>
    Invoke,

    /// <summary>The user typed the first character of a word.</summary>
    Insertion,

    /// <summary>The user typed one of <see cref="LanguageServiceCapabilities.CompletionTriggerCharacters"/>.</summary>
    TriggerCharacter,

    /// <summary>The user typed on in a word whose list was incomplete (<see cref="CompletionList.IsIncomplete"/>).</summary>
    IncompleteList,
}

public readonly record struct CompletionTrigger(CompletionTriggerKind Kind, char Character = '\0');

/// <param name="ResultId">Identifies this list in <see cref="CompletionItemReference"/>.</param>
/// <param name="ApplicableRange">The text a committed item replaces, usually the word being typed.</param>
/// <param name="Items">Sorted in the order the editor shows them.</param>
/// <param name="IsIncomplete">
/// The list holds only some of the items; the editor asks again as the user types on in the same word.
/// </param>
public sealed record CompletionList(
    long ResultId,
    TextRange ApplicableRange,
    IReadOnlyList<CompletionItem> Items,
    bool IsIncomplete = false
);

/// <param name="DisplayText">The text shown in the list.</param>
/// <param name="FilterText">The text matched against what the user typed.</param>
/// <param name="InsertText">The text that replaces <see cref="CompletionList.ApplicableRange"/> on commit.</param>
/// <param name="InlineDescription">Short extra text shown next to the item, if any.</param>
/// <param name="Range">The text this item replaces, when it differs from the list's applicable range.</param>
/// <param name="CommitCharacters">
/// The characters that commit this item, or null for <see cref="LanguageServiceCapabilities.CompletionCommitCharacters"/>.
/// </param>
/// <param name="AdditionalEdits">
/// Edits elsewhere in the document that go with the item; <see cref="CompletionItemDetails"/> can add them later.
/// </param>
public sealed record CompletionItem(
    string DisplayText,
    string FilterText,
    string InsertText,
    CompletionItemKind Kind,
    string? InlineDescription = null,
    TextRange? Range = null,
    IReadOnlyList<char>? CommitCharacters = null,
    IReadOnlyList<TextEdit>? AdditionalEdits = null
);

/// <summary>Points to one item of a completion list, so its details can be loaded lazily.</summary>
public readonly record struct CompletionItemReference(string DocumentId, long ResultId, int Index);

/// <param name="Description">What the item is: its signature as code and its documentation.</param>
/// <param name="AdditionalEdits">
/// Edits elsewhere in the document, in the coordinates of the list's version, which the editor applies with the item;
/// null keeps those of the item.
/// </param>
public sealed record CompletionItemDetails(
    IReadOnlyList<MarkupBlock> Description,
    IReadOnlyList<TextEdit>? AdditionalEdits = null
);

public enum CompletionItemKind
{
    Text,
    Keyword,
    Snippet,
    Namespace,
    Class,
    Struct,
    Interface,
    Enum,
    EnumMember,
    Delegate,
    TypeParameter,
    Method,
    ExtensionMethod,
    Property,
    Field,
    Event,
    Constant,
    Local,
    Parameter,
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record DiagnosticInfo(TextRange Range, DiagnosticSeverity Severity, string Id, string Message);

/// <summary>Semantic classes the editor can color on top of syntax highlighting.</summary>
public enum ClassificationKind
{
    Namespace,
    Class,
    Struct,
    Interface,
    Enum,
    Delegate,
    TypeParameter,
    Method,
    ExtensionMethod,
    Property,
    Event,
    Field,
    Constant,
    EnumMember,
    Local,
    Parameter,
    Label,
}

public readonly record struct ClassifiedRange(TextRange Range, ClassificationKind Kind);

public enum MarkupBlockKind
{
    /// <summary>Running text; line breaks are kept.</summary>
    Paragraph,

    /// <summary>Code, such as a signature, shown in the editor's font.</summary>
    Code,
}

/// <summary>A paragraph or a code block of documentation, as language services format it.</summary>
/// <param name="Language">The language of a code block, if the service named it.</param>
public sealed record MarkupBlock(MarkupBlockKind Kind, string Text, string? Language = null)
{
    /// <summary>The blocks as plain text, separated by blank lines.</summary>
    public static string ToPlainText(IEnumerable<MarkupBlock> blocks)
    {
        var builder = new StringBuilder();
        foreach (MarkupBlock block in blocks)
        {
            if (builder.Length > 0)
                builder.Append("\n\n");
            builder.Append(block.Text);
        }
        return builder.ToString();
    }
}

/// <param name="Range">The text the information is about.</param>
/// <param name="Content">The signature as code and the documentation as paragraphs.</param>
public sealed record QuickInfo(TextRange Range, IReadOnlyList<MarkupBlock> Content)
{
    /// <summary>The content as plain text.</summary>
    public string Text => MarkupBlock.ToPlainText(Content);
}

public enum SignatureHelpTriggerKind
{
    /// <summary>The user asked for signature help explicitly.</summary>
    Invoke,

    /// <summary>The user typed one of <see cref="LanguageServiceCapabilities.SignatureHelpTriggerCharacters"/>.</summary>
    TriggerCharacter,

    /// <summary>The text changed while signature help was shown or requested.</summary>
    ContentChange,
}

/// <param name="IsRetrigger">Signature help is already shown and updates.</param>
public readonly record struct SignatureHelpTrigger(
    SignatureHelpTriggerKind Kind,
    char Character = '\0',
    bool IsRetrigger = false
);

/// <param name="ApplicableRange">The argument list the signatures belong to.</param>
/// <param name="ActiveParameter">The parameter being typed, for signatures that do not say.</param>
public sealed record SignatureHelp(
    IReadOnlyList<SignatureInfo> Signatures,
    int ActiveSignature,
    int ActiveParameter,
    TextRange ApplicableRange
);

/// <param name="ActiveParameter">The parameter being typed in this signature, or null for the help's.</param>
public sealed record SignatureInfo(
    string Label,
    string? Documentation,
    IReadOnlyList<ParameterInfo> Parameters,
    int? ActiveParameter = null
);

/// <param name="LabelRange">Where <see cref="Label"/> is in the signature's label, if known.</param>
public sealed record ParameterInfo(string Label, string? Documentation, TextRange? LabelRange = null);

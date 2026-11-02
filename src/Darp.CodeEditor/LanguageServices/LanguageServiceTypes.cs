namespace Darp.CodeEditor.LanguageServices;

/// <summary>The full text of a document at one version.</summary>
/// <param name="DocumentId">Identifies the document across versions.</param>
/// <param name="Version">Increases with every change of the document.</param>
public sealed record DocumentSnapshot(string DocumentId, int Version, string Text);

/// <summary>A range of characters, as offsets into the document text.</summary>
public readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;

    public static TextRange FromBounds(int start, int end) => new(start, end - start);
}

/// <param name="CompletionTriggerCharacters">Characters that open completion after they are typed, such as '.'.</param>
/// <param name="CompletionCommitCharacters">Characters that commit the selected completion item before they are inserted.</param>
/// <param name="SignatureHelpTriggerCharacters">Characters that open or update signature help, such as '(' and ','.</param>
public sealed record LanguageServiceCapabilities(
    IReadOnlyList<char> CompletionTriggerCharacters,
    IReadOnlyList<char> CompletionCommitCharacters,
    IReadOnlyList<char> SignatureHelpTriggerCharacters
);

public enum CompletionTriggerKind
{
    /// <summary>The user asked for completion explicitly, for example with Ctrl+Space.</summary>
    Invoke,

    /// <summary>The user typed the first character of a word.</summary>
    Insertion,

    /// <summary>The user typed one of <see cref="LanguageServiceCapabilities.CompletionTriggerCharacters"/>.</summary>
    TriggerCharacter,
}

public readonly record struct CompletionTrigger(CompletionTriggerKind Kind, char Character = '\0');

/// <param name="ResultId">Identifies this list in <see cref="CompletionItemReference"/>.</param>
/// <param name="ApplicableRange">The text the committed item replaces, usually the word being typed.</param>
/// <param name="Items">Sorted in the order the editor shows them.</param>
public sealed record CompletionList(long ResultId, TextRange ApplicableRange, IReadOnlyList<CompletionItem> Items);

/// <param name="DisplayText">The text shown in the list.</param>
/// <param name="FilterText">The text matched against what the user typed.</param>
/// <param name="InsertText">The text that replaces <see cref="CompletionList.ApplicableRange"/> on commit.</param>
/// <param name="InlineDescription">Short extra text shown next to the item, if any.</param>
public sealed record CompletionItem(
    string DisplayText,
    string FilterText,
    string InsertText,
    CompletionItemKind Kind,
    string? InlineDescription = null
);

/// <summary>Points to one item of a completion list, so its description can be loaded lazily.</summary>
public readonly record struct CompletionItemReference(string DocumentId, long ResultId, int Index);

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

/// <param name="Range">The text the information is about.</param>
/// <param name="Text">Plain text, possibly over several lines.</param>
public sealed record QuickInfo(TextRange Range, string Text);

/// <param name="ApplicableRange">The argument list the signatures belong to.</param>
public sealed record SignatureHelp(
    IReadOnlyList<SignatureInfo> Signatures,
    int ActiveSignature,
    int ActiveParameter,
    TextRange ApplicableRange
);

public sealed record SignatureInfo(string Label, string? Documentation, IReadOnlyList<ParameterInfo> Parameters);

public sealed record ParameterInfo(string Label, string? Documentation);

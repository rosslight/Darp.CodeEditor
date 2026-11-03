using System.Text.Json;
using Darp.CodeEditor.LanguageServices;

namespace Darp.CodeEditor.Demo.Aot;

/// <summary>
/// A small in-process language service, to show the contract with the completion and squiggle UI: completes words
/// that occur in the document and reports JSON parse errors. It keeps its own copy of each document; JSON documents
/// are small, so it changes the copy right away and does the rest on the thread pool.
/// </summary>
internal sealed class JsonLanguageService : ILanguageService
{
    private static readonly string[] s_keywords = ["true", "false", "null"];
    private static readonly IReadOnlyList<MarkupBlock> s_description =
    [
        new MarkupBlock(MarkupBlockKind.Paragraph, "A word from this document"),
    ];

    private readonly Lock _lock = new();
    private readonly Dictionary<string, DocumentSnapshot> _documents = new(StringComparer.Ordinal);
    private long _nextResultId;

    public LanguageServiceCapabilities Capabilities { get; } = new(['"'], [], []);

    public event EventHandler? Invalidated
    {
        add { }
        remove { }
    }

    public event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;

    public void OpenDocument(DocumentSnapshot document)
    {
        lock (_lock)
            _documents[document.DocumentId] = document;
        PublishDiagnostics(document);
    }

    public void ChangeDocument(DocumentChange change)
    {
        DocumentSnapshot changed;
        lock (_lock)
        {
            if (!_documents.TryGetValue(change.DocumentId, out DocumentSnapshot? document))
                return;
            TextChange edit = change.Change;
            string text = string.Concat(
                document.Text.AsSpan(0, edit.Offset),
                edit.InsertedText,
                document.Text.AsSpan(edit.Offset + edit.RemovedLength)
            );
            changed = new DocumentSnapshot(change.DocumentId, change.Version, text);
            _documents[change.DocumentId] = changed;
        }
        PublishDiagnostics(changed);
    }

    public void CloseDocument(string documentId)
    {
        lock (_lock)
            _documents.Remove(documentId);
    }

    public Task<CompletionList?> GetCompletionsAsync(
        DocumentVersion document,
        int position,
        CompletionTrigger trigger,
        CancellationToken cancellationToken
    ) =>
        TryGet(document) is { } snapshot
            ? Task.Run(() => Complete(snapshot.Text, position), cancellationToken)
            : Task.FromResult<CompletionList?>(null);

    public Task<CompletionItemDetails?> ResolveCompletionItemAsync(
        CompletionItemReference item,
        CancellationToken cancellationToken
    ) => Task.FromResult<CompletionItemDetails?>(new CompletionItemDetails(s_description));

    public Task<IReadOnlyList<ClassifiedRange>> GetClassificationsAsync(
        DocumentVersion document,
        TextRange range,
        CancellationToken cancellationToken
    ) => Task.FromResult<IReadOnlyList<ClassifiedRange>>([]);

    public Task<QuickInfo?> GetQuickInfoAsync(
        DocumentVersion document,
        int position,
        CancellationToken cancellationToken
    ) => Task.FromResult<QuickInfo?>(null);

    public Task<SignatureHelp?> GetSignatureHelpAsync(
        DocumentVersion document,
        int position,
        SignatureHelpTrigger trigger,
        CancellationToken cancellationToken
    ) => Task.FromResult<SignatureHelp?>(null);

    private DocumentSnapshot? TryGet(DocumentVersion document)
    {
        lock (_lock)
        {
            return
                _documents.TryGetValue(document.DocumentId, out DocumentSnapshot? snapshot)
                && snapshot.Version == document.Version
                ? snapshot
                : null;
        }
    }

    private CompletionList? Complete(string text, int position)
    {
        int start = position;
        while (start > 0 && IsWordPart(text[start - 1]))
            start--;
        var words = new SortedSet<string>(s_keywords, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < text.Length; )
        {
            if (!IsWordPart(text[i]))
            {
                i++;
                continue;
            }
            int wordStart = i;
            while (i < text.Length && IsWordPart(text[i]))
                i++;
            // The word being typed is not a suggestion.
            if (wordStart != start && !char.IsDigit(text[wordStart]))
                words.Add(text[wordStart..i]);
        }
        CompletionItem[] items =
        [
            .. words.Select(w => new CompletionItem(
                w,
                w,
                w,
                s_keywords.Contains(w) ? CompletionItemKind.Keyword : CompletionItemKind.Text
            )),
        ];
        return new CompletionList(
            Interlocked.Increment(ref _nextResultId),
            TextRange.FromBounds(start, position),
            items
        );
    }

    private void PublishDiagnostics(DocumentSnapshot document) =>
        _ = Task.Run(() =>
            DiagnosticsChanged?.Invoke(
                this,
                new DiagnosticsChangedEventArgs(document.DocumentId, document.Version, Parse(document.Text))
            )
        );

    private static IReadOnlyList<DiagnosticInfo> Parse(string text)
    {
        try
        {
            using JsonDocument _ = JsonDocument.Parse(text);
            return [];
        }
        catch (JsonException exception)
        {
            // The parser reports a line and a byte position; for ASCII text the byte position is the column.
            int offset = OffsetOf(text, (int)(exception.LineNumber ?? 0), (int)(exception.BytePositionInLine ?? 0));
            var range = new TextRange(Math.Max(0, Math.Min(offset, text.Length - 1)), 1);
            return [new DiagnosticInfo(range, DiagnosticSeverity.Error, "JSON", exception.Message)];
        }
    }

    private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int OffsetOf(string text, int line, int column)
    {
        int offset = 0;
        for (int i = 0; i < line && offset >= 0; i++)
            offset = text.IndexOf('\n', offset) + 1;
        return Math.Max(0, offset) + column;
    }
}

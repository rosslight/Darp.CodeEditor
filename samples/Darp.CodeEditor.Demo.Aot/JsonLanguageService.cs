using System.Text.Json;
using Darp.CodeEditor.LanguageServices;

namespace Darp.CodeEditor.Demo.Aot;

/// <summary>
/// A language service without Roslyn, to show the contract and the completion and squiggle UI in a NativeAOT app:
/// completes words that occur in the document and reports JSON parse errors.
/// </summary>
internal sealed class JsonLanguageService : ILanguageService
{
    private static readonly string[] s_keywords = ["true", "false", "null"];
    private long _nextResultId;

    public LanguageServiceCapabilities Capabilities { get; } = new(['"'], [], []);

    public Task<CompletionList?> GetCompletionsAsync(
        DocumentSnapshot document,
        int position,
        CompletionTrigger trigger,
        CancellationToken cancellationToken
    )
    {
        string text = document.Text;
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
        return Task.FromResult<CompletionList?>(
            new CompletionList(Interlocked.Increment(ref _nextResultId), TextRange.FromBounds(start, position), items)
        );
    }

    public Task<string?> GetCompletionDescriptionAsync(
        CompletionItemReference item,
        CancellationToken cancellationToken
    ) => Task.FromResult<string?>("A word from this document");

    public Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(
        DocumentSnapshot document,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using JsonDocument _ = JsonDocument.Parse(document.Text);
            return Task.FromResult<IReadOnlyList<DiagnosticInfo>>([]);
        }
        catch (JsonException exception)
        {
            // The parser reports a line and a byte position; for ASCII text the byte position is the column.
            int offset = OffsetOf(
                document.Text,
                (int)(exception.LineNumber ?? 0),
                (int)(exception.BytePositionInLine ?? 0)
            );
            var range = new TextRange(Math.Max(0, Math.Min(offset, document.Text.Length - 1)), 1);
            return Task.FromResult<IReadOnlyList<DiagnosticInfo>>([
                new DiagnosticInfo(range, DiagnosticSeverity.Error, "JSON", exception.Message),
            ]);
        }
    }

    public Task<IReadOnlyList<ClassifiedRange>> GetClassificationsAsync(
        DocumentSnapshot document,
        TextRange range,
        CancellationToken cancellationToken
    ) => Task.FromResult<IReadOnlyList<ClassifiedRange>>([]);

    public Task<QuickInfo?> GetQuickInfoAsync(
        DocumentSnapshot document,
        int position,
        CancellationToken cancellationToken
    ) => Task.FromResult<QuickInfo?>(null);

    public Task<SignatureHelp?> GetSignatureHelpAsync(
        DocumentSnapshot document,
        int position,
        CancellationToken cancellationToken
    ) => Task.FromResult<SignatureHelp?>(null);

    private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int OffsetOf(string text, int line, int column)
    {
        int offset = 0;
        for (int i = 0; i < line && offset >= 0; i++)
            offset = text.IndexOf('\n', offset) + 1;
        return Math.Max(0, offset) + column;
    }
}

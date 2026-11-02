using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary>The newest completion list of each open document, for <c>completionItem/resolve</c>.</summary>
internal sealed class CompletionResults
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CompletionResult> _latest = new(StringComparer.Ordinal);
    private long _lastId;

    public long NextId()
    {
        lock (_gate)
            return ++_lastId;
    }

    /// <summary>Keeps <paramref name="result"/> as the newest list of <paramref name="document"/>.</summary>
    /// <returns>False when the document closed, or opened again, since the list was requested; the list is dropped.</returns>
    public bool Set(OpenDocument document, CompletionResult result)
    {
        lock (_gate)
        {
            // Closing cancels the document, then removes its list under this lock, so a request that finishes after
            // the close cannot bring its list back.
            if (document.Closed.IsCancellationRequested)
                return false;
            // A slow request that finishes late must not replace a newer list.
            if (!_latest.TryGetValue(document.Key, out CompletionResult? existing) || existing.Id < result.Id)
                _latest[document.Key] = result;
            return true;
        }
    }

    /// <summary>Forgets the list of a document that closed. Call it after <see cref="OpenDocument.Close"/>.</summary>
    public void Remove(string documentKey)
    {
        lock (_gate)
            _latest.Remove(documentKey);
    }

    public bool TryGetLatest(string documentKey, [NotNullWhen(true)] out CompletionResult? result)
    {
        lock (_gate)
            return _latest.TryGetValue(documentKey, out result);
    }

    public bool TryGet(long id, [NotNullWhen(true)] out CompletionResult? result)
    {
        lock (_gate)
        {
            result = _latest.Values.FirstOrDefault(r => r.Id == id);
            return result is not null;
        }
    }
}

/// <param name="Position">Where the list was requested.</param>
/// <param name="Span">The list's span: the text the items replace.</param>
/// <param name="Items">The Roslyn items, at the index each LSP item's data names.</param>
internal sealed record CompletionResult(
    long Id,
    Document Document,
    LineMap Lines,
    int Position,
    CompletionTrigger Trigger,
    TextSpan Span,
    ImmutableArray<RoslynCompletionItem> Items
);

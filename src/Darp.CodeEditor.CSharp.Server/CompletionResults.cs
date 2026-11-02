using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
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

    public void Set(string documentKey, CompletionResult result)
    {
        lock (_gate)
        {
            // A slow request that finishes late must not replace a newer list.
            if (!_latest.TryGetValue(documentKey, out CompletionResult? existing) || existing.Id < result.Id)
                _latest[documentKey] = result;
        }
    }

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

/// <param name="Items">The Roslyn items, at the index each LSP item's data names.</param>
internal sealed record CompletionResult(
    long Id,
    Document Document,
    LineMap Lines,
    ImmutableArray<RoslynCompletionItem> Items
);

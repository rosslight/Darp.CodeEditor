using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// Ranks completion's items for the text typed for each, with <see cref="CompletionMatcher"/>, VS Code's matching: a
/// whole list off the UI thread, one ranking after the other, whatever its length, and a single item on it. The UI
/// thread reads no text for a list: each item's typed text is read from a snapshot, as far as the matcher reads it.
/// </summary>
internal sealed class CompletionRanking
{
    /// <summary>Ranks lists off the UI thread; made the first time a list is ranked, as it takes about 150 KB.</summary>
    private CompletionMatcher? _listMatcher;

    /// <summary>Checks a single item on the UI thread; made the first time one is checked.</summary>
    private CompletionMatcher? _itemMatcher;

    /// <summary>
    /// The ranking started last: the next one runs after it, so that the list matcher serves one thread at a time.
    /// </summary>
    private Task _ranking = Task.CompletedTask;

    /// <summary>
    /// Ranks <paramref name="items"/> off the UI thread, after the ranking before, for the text of
    /// <paramref name="snapshot"/> up to <paramref name="caret"/>; null once cancelled.
    /// </summary>
    public Task<RankedItems?> RankAsync(
        IReadOnlyList<CompletionItem> items,
        TextSnapshot snapshot,
        int caret,
        CancellationToken cancellationToken
    )
    {
        Task<RankedItems?> ranking = _ranking.ContinueWith(
            _ => Rank(items, snapshot, caret, cancellationToken),
            CancellationToken.None,
            TaskContinuationOptions.DenyChildAttach,
            TaskScheduler.Default
        );
        _ranking = ranking;
        return ranking;
    }

    /// <summary>
    /// Whether <paramref name="item"/> matches the text typed for it in <paramref name="snapshot"/> up to
    /// <paramref name="caret"/>, checked on the UI thread for that item alone.
    /// </summary>
    public bool Matches(CompletionItem item, TextSnapshot snapshot, int caret) =>
        (_itemMatcher ??= new CompletionMatcher()).Match(
            Typed(snapshot, item.Edit.Range.Start, caret),
            item.Label,
            item.FilterText,
            item.FilterTextIsLabel
        )
            is not null;

    /// <summary>
    /// Off the UI thread, after the ranking before: the items that match the text typed for each, from where its edit
    /// starts up to <paramref name="caret"/>, best first, and the one to select; null once cancelled. Of each typed
    /// text only what the matcher reads is read, however far before the caret the edit starts.
    /// </summary>
    private RankedItems? Rank(
        IReadOnlyList<CompletionItem> items,
        TextSnapshot snapshot,
        int caret,
        CancellationToken cancellationToken
    )
    {
        if (cancellationToken.IsCancellationRequested)
            return null;
        var typed = new Dictionary<int, string>();
        var candidates = new CompletionCandidate[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            CompletionItem item = items[i];
            int start = item.Edit.Range.Start;
            if (!typed.TryGetValue(start, out string? text))
                typed[start] = text = Typed(snapshot, start, caret);
            candidates[i] = new CompletionCandidate(text, item.Label, item.FilterText, item.FilterTextIsLabel);
        }
        List<RankedCompletion> ranked;
        try
        {
            ranked = (_listMatcher ??= new CompletionMatcher()).Rank(candidates, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        // As VS Code: the first item, or the first that the server preselects among those that match best.
        int preselected = ranked.FindIndex(r => r.Score == ranked[0].Score && items[r.Index].IsPreselected);
        return new RankedItems(
            [.. ranked.Select(r => new ShownItem(items[r.Index], r.Highlights))],
            Math.Max(0, preselected)
        );
    }

    /// <summary>
    /// The text typed from <paramref name="start"/> up to <paramref name="caret"/>, as far as the matcher reads it: an
    /// edit that starts far before the caret, also in a line's collapsed tail, costs no more than one near it, and
    /// matches by its first characters, as in VS Code. When they are all spaces and tabs, nothing counts as typed;
    /// VS Code reads on, and leaves the item out if anything else follows.
    /// </summary>
    private static string Typed(TextSnapshot snapshot, int start, int caret) =>
        snapshot.GetText(TextRange.FromBounds(start, Math.Min(caret, start + CompletionMatcher.MaxLength)));
}

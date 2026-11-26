using Avalonia.Logging;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// The selected item's details beside completion's list: its detail, as code, and its documentation, read off the UI
/// thread as the hover reads its Markdown. The server resolves the selected item, so that what it adds shows once it
/// answers, if the item is still selected; the resolve of an item no longer selected is cancelled while it is under
/// way. The user hides and shows the details (<see cref="ToggleDetails"/>); hidden, the selected row shows the detail.
/// </summary>
internal sealed class CompletionResolver
{
    private readonly CompletionView _view;
    private readonly BackgroundTextMateHighlighter _highlighter;

    /// <summary>The resolves of the items, under way or done, until completion ends.</summary>
    private readonly Dictionary<CompletionItem, ItemResolve> _resolves = new(ReferenceEqualityComparer.Instance);

    /// <summary>The selected item; null while no list shows.</summary>
    private CompletionItem? _selected;

    /// <summary>The item whose details show, or are about to; null for none.</summary>
    private CompletionItem? _detailsOf;

    /// <summary>Cancels the reading of the details that are about to show.</summary>
    private CancellationTokenSource? _reading;

    /// <summary>Whether the details show beside the list; the user toggles them.</summary>
    private bool _showsDetails = true;

    public CompletionResolver(CompletionView view, BackgroundTextMateHighlighter highlighter)
    {
        _view = view;
        _highlighter = highlighter;
    }

    /// <summary>
    /// <paramref name="item"/> is selected: its details show, and <paramref name="list"/>'s server resolves it unless it
    /// did; null when no list shows it.
    /// </summary>
    public async void Select(CompletionItem item, CompletionList? list)
    {
        _selected = item;
        // Whether or not this item still needs its own, the resolve of another one is no longer needed.
        foreach (
            (CompletionItem other, ItemResolve running) in _resolves
                .Where(r => !ReferenceEquals(r.Key, item) && !r.Value.Task.IsCompleted)
                .ToList()
        )
        {
            running.Cancellation.Cancel();
            _resolves.Remove(other);
        }
        ShowDetails(ResolvedOf(item) ?? item);
        if (list is not { Resolves: true } || _resolves.ContainsKey(item))
            return;
        var cancellation = new CancellationTokenSource();
        Task<CompletionItem?> resolving = list.ResolveAsync(item, cancellation.Token);
        _resolves[item] = new ItemResolve(resolving, cancellation);
        try
        {
            if (await resolving is { } resolved && ReferenceEquals(_selected, item))
                ShowDetails(resolved);
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Error, AssetFiles.LogArea)?.Log(this, "Resolving failed: {Exception}", e);
        }
    }

    /// <summary>The item as the server resolved it; null while it did not.</summary>
    public CompletionItem? ResolvedOf(CompletionItem item) =>
        _resolves.TryGetValue(item, out ItemResolve? resolve) && resolve.Task.IsCompletedSuccessfully
            ? resolve.Task.Result
            : null;

    /// <summary>
    /// The resolve of <paramref name="item"/>, under way or done, or a new one by <paramref name="list"/>'s server, taken
    /// out of the resolver, so that ending completion does not cancel it.
    /// </summary>
    public ItemResolve Take(CompletionItem item, CompletionList list)
    {
        if (_resolves.Remove(item, out ItemResolve? resolve))
            return resolve;
        var cancellation = new CancellationTokenSource();
        return new ItemResolve(list.ResolveAsync(item, cancellation.Token), cancellation);
    }

    /// <summary>Shows or hides the selected item's details.</summary>
    public void ToggleDetails()
    {
        _showsDetails = !_showsDetails;
        _detailsOf = null;
        if (_selected is { } item)
            ShowDetails(ResolvedOf(item) ?? item);
    }

    /// <summary>Hides the details while no list shows; the resolves go on.</summary>
    public void Hide()
    {
        _selected = null;
        _detailsOf = null;
        _reading?.Cancel();
        _reading = null;
        _view.ShowDetails(null);
    }

    /// <summary>Hides the details, and cancels the resolves under way, as completion ends.</summary>
    public void Clear()
    {
        Hide();
        foreach (ItemResolve resolve in _resolves.Values)
            resolve.Cancellation.Cancel();
        _resolves.Clear();
    }

    /// <summary>
    /// Shows <paramref name="item"/>'s details beside the list; nothing when it has neither a detail nor documentation,
    /// or the user hid them. Typing that keeps the item selected keeps them.
    /// </summary>
    private async void ShowDetails(CompletionItem item)
    {
        if (ReferenceEquals(_detailsOf, item))
            return;
        _detailsOf = item;
        _reading?.Cancel();
        _reading = null;
        if (!_showsDetails || (item.Detail is null && item.Documentation is null))
        {
            _view.ShowDetails(null);
            return;
        }
        var cancellation = new CancellationTokenSource();
        _reading = cancellation;
        TextMateGrammar? grammar = _highlighter.Grammar;
        try
        {
            IReadOnlyList<HoverBlock> blocks = await Task.Run(
                () => DetailBlocks(item, grammar, cancellation.Token),
                cancellation.Token
            );
            if (!cancellation.IsCancellationRequested)
                _view.ShowDetails(HoverView.Create(blocks, _view.Look));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e)
        {
            Logger
                .TryGet(LogEventLevel.Error, AssetFiles.LogArea)
                ?.Log(this, "Showing an item's details failed: {Exception}", e);
        }
    }

    /// <summary>
    /// The detail, as code, and the documentation, as much of both as a hover shows: one budget for both, cut before
    /// anything reads them, so that a server's long detail or documentation costs the UI thread no more than the
    /// largest hover.
    /// </summary>
    private static IReadOnlyList<HoverBlock> DetailBlocks(
        CompletionItem item,
        TextMateGrammar? grammar,
        CancellationToken cancellationToken
    )
    {
        var budget = new HoverBudget();
        var blocks = new List<HoverBlock>();
        if (item.Detail is { } detail && budget.Take(detail) is { } kept)
            blocks.AddRange(budget.Fit([HoverBlocks.Highlight(kept, grammar, cancellationToken)]));
        if (item.Documentation is { } documentation)
            blocks.AddRange(HoverBlocks.Of(new HoverInfo([documentation], null), grammar, budget, cancellationToken));
        return blocks;
    }
}

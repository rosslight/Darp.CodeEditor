using Avalonia.Logging;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.Completion;

/// <summary>What completion's filter asks of the completion that owns it, when a filter's result comes.</summary>
internal interface ICompletionOwner
{
    /// <summary>Whether the user asked by key: when nothing matches, the list freezes or says so.</summary>
    bool ByKey { get; }

    /// <summary>Whether a newer list is asked for, which may have what the one that shows lacks.</summary>
    bool Asking { get; }

    /// <summary>Ends completion: nothing matches a list opened by typing, and no newer list comes.</summary>
    void End();

    /// <summary>Commits the item at <paramref name="index"/> of the list, for an accept that waited for the filter.</summary>
    void Commit(int index, bool replace);
}

/// <summary>
/// Filters completion's answer as the user types, and shows the result. Each change of the text or the caret filters
/// anew off the UI thread (<see cref="CompletionRanking"/>), the filter under way stops, and the list shows the newest
/// result, the previous one meanwhile. Typing on ranks the items that show, as VS Code refilters, and anything else,
/// such as Backspace, all of the answer's: once typing shortened a long list, it tolerates a typo. When nothing matches, a list opened by typing ends, unless more may come, as
/// from a list asked for again: it freezes meanwhile. One asked for by key freezes, or says "No suggestions.". An
/// accept that the current text's filter has to decide commits what it selects once it has run, unless the text or
/// the caret changes before. It owns what the list shows until completion ends, which <see cref="Hide"/> forgets.
/// </summary>
internal sealed class CompletionFilter
{
    private readonly CompletionView _view;
    private readonly CompletionRanking _ranking;
    private readonly CompletionResolver _resolver;
    private readonly TextArea _textArea;
    private readonly CodeDocument _document;
    private readonly Func<int, TextRange> _wordBefore;
    private readonly ICompletionOwner _owner;

    /// <summary>The filter under way; null for none.</summary>
    private FilterRun? _filtering;

    /// <summary>What the items that show were filtered for; null while none show.</summary>
    private FilterKey? _shown;

    /// <summary>What the frozen list's text is: its items show as they were, none of them matching; null otherwise.</summary>
    private FilterKey? _frozenAt;

    /// <summary>
    /// The accept that waits for the filter of its text, whether it replaces, and what its key goes on to when that
    /// filter finds nothing; null for none. A filter of other text or another caret drops it.
    /// </summary>
    private (FilterKey At, bool Replace, Action? GoOn)? _waiting;

    /// <param name="view">The list.</param>
    /// <param name="ranking">Ranks the items.</param>
    /// <param name="resolver">Shows the selected item's details.</param>
    /// <param name="textArea">The text area, for the caret.</param>
    /// <param name="document">The document, for its text.</param>
    /// <param name="wordBefore">The word before an offset.</param>
    /// <param name="owner">The completion that the filter ends or commits for.</param>
    public CompletionFilter(
        CompletionView view,
        CompletionRanking ranking,
        CompletionResolver resolver,
        TextArea textArea,
        CodeDocument document,
        Func<int, TextRange> wordBefore,
        ICompletionOwner owner
    )
    {
        _view = view;
        _ranking = ranking;
        _resolver = resolver;
        _textArea = textArea;
        _document = document;
        _wordBefore = wordBefore;
        _owner = owner;
    }

    /// <summary>The items that show, in order.</summary>
    public IReadOnlyList<ShownItem> Items { get; private set; } = [];

    /// <summary>The selected item's index in <see cref="Items"/>.</summary>
    public int Selected { get; private set; }

    /// <summary>Whether the list shows items, frozen or not.</summary>
    public bool IsShowing => _shown is not null;

    /// <summary>Whether the list shows its items frozen: none of them matches the text.</summary>
    public bool IsFrozen => _frozenAt is not null;

    /// <summary>Whether the list says "No suggestions." instead of showing items.</summary>
    public bool ShowsNoMatch { get; private set; }

    /// <summary>The answer whose items show; null while none show.</summary>
    public CompletionAnswer? ShownAnswer => _shown?.Answer;

    /// <summary>
    /// Filters <paramref name="answer"/> for the text up to the caret, off the UI thread, unless that filter runs or
    /// shows already: the filter under way stops, and the list shows the new one's result once it has run.
    /// </summary>
    public void Run(CompletionAnswer answer)
    {
        // While the request is not stale, its offsets up to where it was asked are the current text's.
        if (answer.Request.IsStale || _textArea.Caret.Offset < answer.Request.Offset)
        {
            // The answer no longer fits: the newer request's shows.
            Hide();
            return;
        }
        FilterKey key = CurrentKey(answer);
        if (_filtering?.Key == key || (_shown == key && _filtering is null))
            return;
        _filtering?.Cancellation.Cancel();
        // An accept that waits goes on waiting for a newer list's filter of the same text and caret, as for a list
        // asked for again while it waited; a change of either drops it.
        if (_waiting is { At: var at } waiting && at != key)
            _waiting = (at.Version, at.Caret) == (key.Version, key.Caret) ? waiting with { At = key } : null;
        var filtering = new FilterRun(key, new CancellationTokenSource());
        _filtering = filtering;
        // The UI thread reads no text: each item's typed text is read off it, from the snapshot of the key's version.
        FilterAsync(
            filtering,
            RanksShown(key) ? [.. Items.Select(shown => shown.Item)] : answer.List.Items,
            _document.CurrentSnapshot
        );
    }

    /// <summary>
    /// Commits what the filter of the current text selects once it has run, for accepting, unless the text or the
    /// caret changes before; starts that filter if none runs. When that filter finds nothing to commit, the key that
    /// accepted goes on, by <paramref name="goOn"/>.
    /// </summary>
    public void CommitWhenFiltered(CompletionAnswer answer, bool replace, Action? goOn)
    {
        _waiting = (CurrentKey(answer), replace, goOn);
        Run(answer);
    }

    /// <summary>
    /// Takes what the key of an accept that waits for the filter of the current text and caret goes on to, as when a
    /// newer answer ends completion with nothing to commit; null when none waits.
    /// </summary>
    public Action? TakeWaitingKey()
    {
        if (
            _waiting is not { } waiting
            || (waiting.At.Version, waiting.At.Caret) != (_document.CurrentSnapshot.Version, _textArea.Caret.Offset)
        )
            return null;
        _waiting = null;
        return waiting.GoOn;
    }

    /// <summary>
    /// Whether the item at <paramref name="index"/> may be committed now: the list shows the filter of the current
    /// text of <paramref name="answer"/>, the newest answer, frozen or not, or the item matches it, which is checked
    /// for that item alone.
    /// </summary>
    public bool CanCommit(int index, CompletionAnswer? answer)
    {
        if (_shown is not { } shown)
            return false;
        if (answer is not null && shown == CurrentKey(answer))
            return true;
        if (_frozenAt is { } frozen && frozen == CurrentKey(frozen.Answer))
            return true;
        CompletionItem item = Items[index].Item;
        int caret = _textArea.Caret.Offset;
        if (shown.Answer.Request.IsStale || caret < shown.Answer.Request.Offset)
            return false;
        int ranked = RanksShown(CurrentKey(shown.Answer)) ? Items.Count : shown.Answer.List.Items.Count;
        return _ranking.Matches(item, _document.CurrentSnapshot, caret, ranked <= CompletionMatcher.MaxGracefulItems);
    }

    public void Select(int index)
    {
        Selected = index;
        _view.Select(index);
        ResolveSelected();
    }

    /// <summary>
    /// Hides the list, as completion waits for an answer or ends: the filter under way stops, and an accept that waits
    /// for it is dropped.
    /// </summary>
    public void Hide()
    {
        ShowsNoMatch = false;
        _frozenAt = null;
        _filtering?.Cancellation.Cancel();
        _filtering = null;
        _waiting = null;
        _shown = null;
        Items = [];
        _resolver.Hide();
        _view.Hide();
    }

    /// <summary>
    /// Shows a filter's result, of <paramref name="items"/>, unless a newer filter replaced it or the list was hidden.
    /// </summary>
    private async void FilterAsync(FilterRun filtering, IReadOnlyList<CompletionItem> items, TextSnapshot snapshot)
    {
        try
        {
            RankedItems? filtered = await _ranking.RankAsync(
                items,
                snapshot,
                filtering.Key.Caret,
                filtering.Cancellation.Token
            );
            if (filtered is null || _filtering != filtering)
                return;
            _filtering = null;
            // It counts only for the text and the caret it was filtered for: the change that outdated it filters anew.
            if (filtering.Key == CurrentKey(filtering.Key.Answer))
                Show(filtering.Key, filtered);
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Error, AssetFiles.LogArea)?.Log(this, "Filtering failed: {Exception}", e);
        }
    }

    /// <summary>
    /// Shows a filter's items. When none match, a list opened by typing ends, unless more may come; one asked for by key
    /// freezes, or says that nothing matches.
    /// </summary>
    private void Show(FilterKey key, RankedItems filtered)
    {
        if (filtered.Items.Count == 0)
        {
            Action? goOn = _waiting is { } waiting && waiting.At == key ? waiting.GoOn : null;
            // An incomplete list may have more for what is typed, and a newer request, as for a trigger character typed
            // while the list showed, brings a list of its own: its answer shows, or ends completion when it has nothing.
            bool byKey = _owner.ByKey;
            bool more = key.Answer.List.IsIncomplete || _owner.Asking;
            if (byKey && _shown?.Answer != key.Answer)
            {
                Hide();
                ShowsNoMatch = true;
                _view.ShowNoSuggestions(key.Answer.Request.Word.Start);
            }
            // Meanwhile, as when asked for by key, the list keeps its rows, frozen, while the caret is in a word.
            else if (IsShowing && (byKey || more) && _wordBefore(key.Caret).Length > 0)
            {
                Freeze(key);
                return;
            }
            else if (!byKey && more)
                Hide();
            else
                _owner.End();
            // The key that waited for this filter goes on as it would have without the list: in VS Code, which
            // filters as the user types, the list is gone before such a key.
            goOn?.Invoke();
            return;
        }
        _frozenAt = null;
        ShowsNoMatch = false;
        Items = filtered.Items;
        Selected = filtered.Selected;
        _shown = key;
        if (_waiting is { } accept && accept.At == key)
        {
            _owner.Commit(Selected, accept.Replace);
            return;
        }
        _view.Show(Items, Selected, key.Answer.Request.Word.Start);
        // Placing it may have ended completion, as its line is out of view.
        if (IsShowing)
            ResolveSelected();
    }

    /// <summary>
    /// The list keeps showing its items for the text that <paramref name="key"/> is for, which none of them matches, as
    /// VS Code's frozen list: without their matched characters in bold. An accept that waited for this filter commits
    /// the selected item over what was typed now.
    /// </summary>
    private void Freeze(FilterKey key)
    {
        bool wasFrozen = _frozenAt is not null;
        _frozenAt = key;
        if (_waiting is { } accept && accept.At == key)
            _owner.Commit(Selected, accept.Replace);
        else if (!wasFrozen)
            _view.Show(
                [.. Items.Select(shown => shown with { Highlights = [] })],
                Selected,
                key.Answer.Request.Word.Start
            );
    }

    /// <summary>
    /// Whether a filter for <paramref name="key"/> ranks the items that show, rather than all of the answer's: the caret
    /// went on past where they were filtered for, as when typing on.
    /// </summary>
    private bool RanksShown(FilterKey key) =>
        _shown is { } shown && shown.Answer == key.Answer && key.Caret > shown.Caret;

    /// <summary>What filtering <paramref name="answer"/> now would filter for.</summary>
    private FilterKey CurrentKey(CompletionAnswer answer) =>
        new(answer, _document.CurrentSnapshot.Version, _textArea.Caret.Offset);

    private void ResolveSelected() => _resolver.Select(Items[Selected].Item, _shown?.Answer.List);
}

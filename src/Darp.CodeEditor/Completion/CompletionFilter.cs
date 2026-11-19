using Avalonia.Input;
using Avalonia.Logging;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// Filters a completion session's answer as the user types, and shows the result. Each change of the text or the caret
/// filters anew off the UI thread (<see cref="CompletionRanking"/>), the filter under way stops, and the list shows the
/// newest result, the previous one meanwhile. When nothing matches, a list opened by typing ends, unless more may
/// come; one asked for by key freezes, or says "No suggestions.". An accept that the current text's filter has to
/// decide commits what it selects once it has run, unless the text or the caret changes before.
/// </summary>
internal sealed class CompletionFilter
{
    private readonly CompletionView _view;
    private readonly CompletionRanking _ranking;
    private readonly CompletionResolver _resolver;
    private readonly TextArea _textArea;
    private readonly CodeDocument _document;
    private readonly Func<int, TextRange> _wordBefore;
    private readonly Action _close;
    private readonly Action _update;
    private readonly Action<CompletionSession, int, bool> _commit;

    /// <param name="view">The list.</param>
    /// <param name="ranking">Ranks the items.</param>
    /// <param name="resolver">Shows the selected item's details.</param>
    /// <param name="textArea">The text area, for the caret.</param>
    /// <param name="document">The document, for its text.</param>
    /// <param name="wordBefore">The word before an offset.</param>
    /// <param name="close">Ends completion.</param>
    /// <param name="update">Follows the caret again, as after a change.</param>
    /// <param name="commit">Commits the item at an index of the session's list, replacing or not.</param>
    public CompletionFilter(
        CompletionView view,
        CompletionRanking ranking,
        CompletionResolver resolver,
        TextArea textArea,
        CodeDocument document,
        Func<int, TextRange> wordBefore,
        Action close,
        Action update,
        Action<CompletionSession, int, bool> commit
    )
    {
        _view = view;
        _ranking = ranking;
        _resolver = resolver;
        _textArea = textArea;
        _document = document;
        _wordBefore = wordBefore;
        _close = close;
        _update = update;
        _commit = commit;
    }

    /// <summary>
    /// Filters <paramref name="answer"/> for the text up to the caret, off the UI thread, unless that filter runs or
    /// shows already: the filter under way stops, and the list shows the new one's result once it has run.
    /// </summary>
    public void Run(CompletionSession session, CompletionAnswer answer, int caret)
    {
        // While the request is not stale, its offsets up to where it was asked are the current text's.
        if (answer.Request.IsStale || caret < answer.Request.Offset)
        {
            // The answer no longer fits: the newer request's shows.
            Hide(session);
            return;
        }
        FilterKey key = CurrentKey(answer);
        if (session.Filtering?.Key == key || (session.Shown == key && session.Filtering is null))
            return;
        session.Filtering?.Cancellation.Cancel();
        // An accept that waits goes on waiting for a newer list's filter of the same text and caret, as for a list
        // asked for again while it waited; a change of either drops it.
        if (session.CommitAt is { } waiting && waiting != key)
            session.CommitAt = (waiting.Version, waiting.Caret) == (key.Version, key.Caret) ? key : null;
        var filtering = new FilterRun(key, new CancellationTokenSource());
        session.Filtering = filtering;
        // The UI thread reads no text: each item's typed text is read off it, from the snapshot of the key's version.
        FilterAsync(session, filtering, _document.CurrentSnapshot);
    }

    /// <summary>Shows a filter's result, unless completion ended, or a newer filter replaced it.</summary>
    private async void FilterAsync(CompletionSession session, FilterRun filtering, TextSnapshot snapshot)
    {
        try
        {
            CancellationToken cancellationToken = filtering.Cancellation.Token;
            RankedItems? filtered = await _ranking.RankAsync(
                filtering.Key.Answer.List.Items,
                snapshot,
                filtering.Key.Caret,
                cancellationToken
            );
            if (filtered is null || session.HasEnded || session.Filtering != filtering)
                return;
            session.Filtering = null;
            // It counts only for the text and the caret it was filtered for: a change since outdates it, also one
            // that left the caret where it was.
            if (filtering.Key != CurrentKey(filtering.Key.Answer))
            {
                _update();
                return;
            }
            Show(session, filtering.Key, filtered);
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
    private void Show(CompletionSession session, FilterKey key, RankedItems filtered)
    {
        if (filtered.Items.Count == 0)
        {
            Action? goOn = session.CommitAt == key ? session.CommitGoOn : null;
            // An incomplete list may have more for what is typed, and a newer request, as for a trigger character typed
            // while the list showed, brings a list of its own: its answer shows, or ends completion when it has nothing.
            if (!session.ByKey)
            {
                if (key.Answer.List.IsIncomplete || session.Pending is not null)
                    Hide(session);
                else
                    _close();
            }
            else if (session.Shown?.Answer != key.Answer)
            {
                Hide(session);
                session.ShowsNoMatch = true;
                _view.ShowNoSuggestions(key.Answer.Request.Word.Start);
            }
            else if (_wordBefore(key.Caret).Length == 0)
                _close();
            else
            {
                Freeze(session, key);
                return;
            }
            // The key that waited for this filter goes on as it would have without the list: in VS Code, which
            // filters as the user types, the list is gone before such a key.
            goOn?.Invoke();
            return;
        }
        session.FrozenAt = null;
        session.ShowsNoMatch = false;
        session.Items = filtered.Items;
        session.Selected = filtered.Selected;
        session.Shown = key;
        if (session.CommitAt == key)
        {
            _commit(session, session.Selected, session.CommitReplaces);
            return;
        }
        int wordStart = key.Answer.Request.Word.Start;
        _view.Show(session.Items, session.Selected, wordStart);
        // Placing it may have ended completion, as its line is out of view.
        if (!session.HasEnded)
            ResolveSelected(session);
    }

    /// <summary>
    /// The list keeps showing its items for the text that <paramref name="key"/> is for, which none of them matches, as
    /// VS Code's frozen list: without their matched characters in bold. An accept that waited for this filter commits
    /// the selected item over what was typed now.
    /// </summary>
    private void Freeze(CompletionSession session, FilterKey key)
    {
        bool wasFrozen = session.FrozenAt is not null;
        session.FrozenAt = key;
        if (session.CommitAt == key)
            _commit(session, session.Selected, session.CommitReplaces);
        else if (!wasFrozen)
        {
            ShownItem[] items = [.. session.Items.Select(shown => shown with { Highlights = [] })];
            _view.Show(items, session.Selected, key.Answer.Request.Word.Start);
        }
    }

    /// <summary>
    /// Whether the item at <paramref name="index"/> may be committed now: the list shows the filter of the current
    /// text, frozen or not, or the item matches it, which is checked for that item alone.
    /// </summary>
    public bool CanCommit(CompletionSession session, int index)
    {
        if (session.Shown is not { } shown)
            return false;
        if (session.Answer is { } answer && shown == CurrentKey(answer))
            return true;
        if (session.FrozenAt is { } frozen && frozen == CurrentKey(frozen.Answer))
            return true;
        CompletionItem item = session.Items[index].Item;
        int caret = _textArea.Caret.Offset;
        if (shown.Answer.Request.IsStale || caret < shown.Answer.Request.Offset)
            return false;
        return _ranking.Matches(item, _document.CurrentSnapshot, caret);
    }

    /// <summary>
    /// Commits what the filter of the current text selects once it has run, for accepting, unless the text or
    /// the caret changes before; starts that filter if none runs. When that filter finds nothing to commit, the key
    /// that accepted goes on, by <paramref name="goOn"/>.
    /// </summary>
    public void CommitWhenFiltered(CompletionSession session, bool replace, Action? goOn)
    {
        if (session.Answer is not { } answer)
            return;
        session.CommitAt = CurrentKey(answer);
        session.CommitReplaces = replace;
        session.CommitGoOn = goOn;
        Run(session, answer, _textArea.Caret.Offset);
    }

    /// <summary>What filtering <paramref name="answer"/> now would filter for.</summary>
    private FilterKey CurrentKey(CompletionAnswer answer) =>
        new(answer, _document.CurrentSnapshot.Version, _textArea.Caret.Offset);

    public void Select(CompletionSession session, int index)
    {
        session.Selected = index;
        _view.Select(index);
        ResolveSelected(session);
    }

    private void ResolveSelected(CompletionSession session) =>
        _resolver.Select(session.Items[session.Selected].Item, session.Shown?.Answer.List);

    /// <summary>Hides the list while completion waits for an answer; a filter of the old one under way stops.</summary>
    public void Hide(CompletionSession session)
    {
        session.ShowsNoMatch = false;
        session.FrozenAt = null;
        session.Filtering?.Cancellation.Cancel();
        session.Filtering = null;
        session.CommitAt = null;
        session.Shown = null;
        session.Items = [];
        _resolver.Hide();
        _view.Hide();
    }
}

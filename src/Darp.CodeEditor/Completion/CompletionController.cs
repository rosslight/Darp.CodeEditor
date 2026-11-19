using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Logging;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;
using Darp.CodeEditor.Lsp;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// Completion in one editor's view of a document, as VS Code behaves by default: a list of the server's items below
/// the word being typed, which the typed text filters and ranks, and which commits the selected item into the text.
/// </summary>
/// <remarks>
/// <para>
/// Completion opens on Ctrl+Space, also on macOS, on the server's trigger characters, and when typing starts a word
/// outside a comment. Until the server answers, nothing shows; what is typed meanwhile filters its answer. The list
/// is the server's for where it was asked: typing on filters and ranks it on the client, with the matching of
/// <see cref="CompletionMatcher"/>, and selects the first item, or the one the server preselects among the best. The
/// matching runs off the UI thread, whatever the list's length: each change cancels the filter under way, and the list
/// shows the newest result, the previous one meanwhile. A list
/// that the server marks incomplete is asked for again as the user types on, and shows meanwhile. Moving back before
/// where completion was asked, and any edit before it, as by another editor of the document, asks again while a word
/// is left, as VS Code does when the caret moves back: the list's positions no longer fit the text.
/// </para>
/// <para>
/// When nothing matches what is typed, a list opened by typing ends. One asked for by key stays, as VS Code's does:
/// frozen, as its items showed last, without their matched characters in bold, while the caret is in a word; or, when
/// none of the answer's items showed, as when nothing matched where it was asked, it says "No suggestions.", and so
/// does an empty answer, until the next key or caret move.
/// </para>
/// <para>
/// Up, Down, Page Up and Page Down select among the rows that show, while there is more than one item; with one, they
/// go on to the text, as in VS Code. The keymap's accept commands, a click or one of the selected item's commit
/// characters commit it: its edit, or its text over the word, and its additional edits, such as an import, in one step
/// that the user undoes. <see cref="Accept"/> inserts, over the word up to the caret, or replaces, over the rest of the
/// word too; a click and a commit character insert. While the filter of the current text still runs, they commit the
/// item that shows if it matches the current text, checked on the UI thread for that item alone; otherwise accepting
/// commits what that filter selects once it has run, unless the text, by any editor or code, or the caret changes
/// before, and when nothing matches then, the key that accepted goes on, as Enter to a new line; a commit character goes
/// in as text, as it does when nothing matches: deciding later would hold back or reorder what the user types. A frozen
/// list commits by accepting and by a click, over what was typed, but not by commit characters. A read-only editor
/// neither completes nor commits. The server resolves the selected item, so that its detail and documentation show
/// beside the list, unless the user hid them (<see cref="ToggleDetails"/>): the selected row shows its detail then. An
/// item whose additional edits are still to come when it is committed gets them when they come, in its step, if that
/// step is still the last one: otherwise they are dropped, as the user moved on.
/// </para>
/// <para>
/// <see cref="Close"/> ends completion, for every reason it ends: Escape, the caret leaving the line or moving back
/// past the word, a selection, the editor becoming read-only, the list scrolling out of view, the editor losing the
/// keyboard focus, a server that starts or stops serving the document, new colors, the list running out of matches, and
/// the editor no longer showing the document. Ending it hides "No suggestions." too, and cancels the request still
/// under way with <c>$/cancelRequest</c>, and so does a newer request; a late answer is dropped.
/// </para>
/// </remarks>
internal sealed class CompletionController : IEditorFeature
{
    private readonly TemplatedControl _editor;
    private readonly TextArea _textArea;
    private readonly TextView _textView;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly SnippetController _snippets;
    private readonly CompletionView _view;
    private PopupColors _colors = PopupColors.Default;
    private SymbolIconColors? _icons;

    /// <summary>Completion from its first request until it ends; null while there is none.</summary>
    private CompletionSession? _session;

    /// <summary>Whether "No suggestions." shows for an empty answer to a request by key, with no completion open.</summary>
    private bool _showsEmpty;

    /// <summary>Whether the selected item's details show beside the list; the user toggles them.</summary>
    private bool _showsDetails = true;

    /// <summary>Cancels the resolve of the item committed last, whose additional edits are still to come.</summary>
    private CancellationTokenSource? _lateEdits;

    private readonly CompletionRanking _ranking = new();

    public CompletionController(
        TemplatedControl editor,
        TextArea textArea,
        CodeDocument document,
        BackgroundTextMateHighlighter highlighter,
        SnippetController snippets
    )
    {
        _editor = editor;
        _textArea = textArea;
        _textView = textArea.TextView;
        _buffer = textArea.Document;
        _document = document;
        _highlighter = highlighter;
        _snippets = snippets;
        _view = new CompletionView(editor);
        _view.Clicked += OnClicked;
        _buffer.Changing += OnBufferChanging;
        _buffer.TextChanged += OnBufferTextChanged;
        _textArea.TextEntered += OnTextEntered;
        _textArea.Caret.PositionChanged += OnCaretPositionChanged;
        _document.LanguageFeaturesChanged += OnClosingChange;
        _view.LeftView += OnClosingChange;
    }

    /// <summary>The popups' colors, from the color theme once it is loaded. New colors end completion.</summary>
    public PopupColors Colors
    {
        set
        {
            if (value == _colors)
                return;
            _colors = value;
            Close();
        }
    }

    /// <summary>The colors of the icons, once the color theme is loaded. New colors end completion.</summary>
    public SymbolIconColors? Icons
    {
        set
        {
            if (value == _icons)
                return;
            _icons = value;
            Close();
        }
    }

    public void AddCommands(CommandTable commands)
    {
        const CommandNeeds needs = CommandNeeds.TextFocus | CommandNeeds.Writable;
        commands.Add(CodeEditorCommands.TriggerSuggest, Trigger, needs);
        commands.Add(CodeEditorCommands.ToggleSuggestionDetails, ToggleDetails);
        commands.Add(CodeEditorCommands.AcceptSelectedSuggestion, key => Accept(replace: false, key), needs);
        commands.Add(CodeEditorCommands.AcceptAlternativeSelectedSuggestion, key => Accept(replace: true, key), needs);
    }

    /// <summary>Asks for completion, where the server completes and the user can type.</summary>
    private bool Trigger()
    {
        if (
            _document.Features?.Completion is null
            || !_textArea.ReadOnlySectionProvider.CanInsert(_textArea.Caret.Offset)
        )
            return false;
        Ask(new CompletionTrigger(CompletionTriggerKind.Invoked), byKey: true);
        return true;
    }

    /// <summary>
    /// Commits the selected item while the list shows: inserting, its text goes over the word up to the caret, and the
    /// rest of the word stays after it; replacing, it goes over the rest of the word too, or as far as the item's
    /// replace range reaches.
    /// </summary>
    /// <param name="replace">Whether to replace.</param>
    /// <param name="key">The key that accepts, which goes on if the filter it waits for finds nothing.</param>
    /// <returns>Whether the list showed: otherwise the key goes on, as to a new line or an indent.</returns>
    private bool Accept(bool replace, PressedKey? key)
    {
        if (_session is not { IsShowing: true } session)
            return false;
        if (CanCommit(session, session.Selected))
            Commit(session, session.Selected, replace);
        else
            CommitWhenFiltered(session, replace, key);
        return true;
    }

    /// <summary>Shows or hides the selected item's details while the list shows.</summary>
    /// <returns>Whether the list showed.</returns>
    private bool ToggleDetails()
    {
        if (_session is not { IsShowing: true } session)
            return false;
        _showsDetails = !_showsDetails;
        session.DetailsOf = null;
        ResolveSelected(session);
        return true;
    }

    /// <summary>
    /// The keys of completion's list while it shows more than one item, from the editor's key handler while the text area
    /// has the keyboard: those that select. Any key ends "No suggestions." of an empty answer.
    /// </summary>
    /// <returns>Whether the key was completion's.</returns>
    public bool OnKeyDown(KeyEventArgs e)
    {
        if (_session is null)
            HideEmpty();
        if (_session is not { IsShowing: true, Items.Count: > 1 } session || e.KeyModifiers != KeyModifiers.None)
            return false;
        int count = session.Items.Count;
        int page = Math.Max(1, _view.List.VisibleRows - 1);
        switch (e.Key)
        {
            case Key.Down:
                Select(session, (session.Selected + 1) % count);
                return true;
            case Key.Up:
                Select(session, (session.Selected + count - 1) % count);
                return true;
            case Key.PageDown:
                Select(session, Math.Min(count - 1, session.Selected + page));
                return true;
            case Key.PageUp:
                Select(session, Math.Max(0, session.Selected - page));
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Text the user types, before it goes in: one of the selected item's commit characters commits it, and then goes in
    /// after it, if the item is known to match the current text. A frozen list commits nothing so, as in VS Code.
    /// </summary>
    public void OnTextInput(TextInputEventArgs e)
    {
        if (
            _session is { IsShowing: true, FrozenAt: null } session
            && e.Text is [char typed]
            && session.Items[session.Selected].Item.CommitCharacters.Contains(typed)
            && CanCommit(session, session.Selected)
        )
        {
            Commit(session, session.Selected);
        }
    }

    /// <summary>
    /// Ends completion: closes the list, cancels the server's requests still under way, and drops what they would show.
    /// </summary>
    /// <returns>Whether the list or a message showed.</returns>
    public bool Close()
    {
        bool showedEmpty = HideEmpty();
        if (_session is not { } session)
            return showedEmpty;
        _session = null;
        session.Pending?.Cancellation.Cancel();
        session.Filtering?.Cancellation.Cancel();
        foreach (ItemResolve resolve in session.Resolves.Values)
            resolve.Cancellation.Cancel();
        session.Details?.Cancel();
        _view.Hide();
        _view.ShowDetails(null);
        return session.IsShowing || session.ShowsNoMatch;
    }

    public void Dispose()
    {
        Close();
        _lateEdits?.Cancel();
        _view.Clicked -= OnClicked;
        _buffer.Changing -= OnBufferChanging;
        _buffer.TextChanged -= OnBufferTextChanged;
        _textArea.TextEntered -= OnTextEntered;
        _textArea.Caret.PositionChanged -= OnCaretPositionChanged;
        _document.LanguageFeaturesChanged -= OnClosingChange;
        _view.LeftView -= OnClosingChange;
    }

    /// <summary>
    /// Asks the server for completion at the caret. While completion is open, the request replaces the one under way,
    /// and the items that show stay until it answers. Asked <paramref name="byKey"/>, it says when nothing matches.
    /// </summary>
    private void Ask(CompletionTrigger trigger, bool byKey = false)
    {
        int caret = _textArea.Caret.Offset;
        if (!_textArea.ReadOnlySectionProvider.CanInsert(caret))
            return;
        var request = new CompletionRequest(_document.CurrentSnapshot, WordBefore(caret));
        HideEmpty();
        CompletionSession session = _session ??= new CompletionSession();
        session.ByKey |= byKey;
        session.Pending?.Cancellation.Cancel();
        session.Pending = request;
        AskAsync(session, request, trigger);
    }

    /// <summary>Shows the server's answer, unless completion ended or asked again meanwhile.</summary>
    private async void AskAsync(CompletionSession session, CompletionRequest request, CompletionTrigger trigger)
    {
        try
        {
            CompletionList? list = await _document.CompletionAsync(
                request.Snapshot,
                request.Word,
                trigger,
                request.Cancellation.Token
            );
            if (_session != session || session.Pending != request)
                return;
            session.Pending = null;
            if (list is null || list.Items.Count == 0)
            {
                // Asked by key, the user learns that there is nothing; once a list showed, it says that nothing
                // matches. Otherwise a complete list without items ends completion.
                if (session is { ByKey: true, Answer: null })
                {
                    Close();
                    _showsEmpty = true;
                    ShowMessage(request.Word.Start);
                    return;
                }
                if (!session.ByKey && list is not { IsIncomplete: true })
                {
                    Close();
                    return;
                }
                list ??= new CompletionList([], isIncomplete: false, resolve: null);
            }
            session.Answer = new CompletionAnswer(list, request);
            Update();
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Error, AssetFiles.LogArea)?.Log(this, "Completion failed: {Exception}", e);
        }
    }

    /// <summary>Follows the caret: filters the list with what is typed, asks again, or ends completion.</summary>
    private void Update()
    {
        if (_session is not { } session)
        {
            // "No suggestions." of an empty answer shows until the caret moves.
            HideEmpty();
            return;
        }
        int caret = _textArea.Caret.Offset;
        CompletionRequest latest = session.Latest;
        if (
            !_textArea.Selection.IsEmpty
            || (!latest.IsStale && _buffer.GetLineByOffset(caret) != _buffer.GetLineByOffset(latest.Offset))
        )
        {
            Close();
            return;
        }
        if (latest.IsStale || caret < latest.Offset)
        {
            // Back before where completion was asked, or the text before it changed: the list does not fit.
            if (WordBefore(caret).Length == 0)
            {
                Close();
                return;
            }
            Hide(session);
            Ask(new CompletionTrigger(CompletionTriggerKind.Invoked));
            return;
        }
        if (session.Answer is not { } answer)
            return;
        if (answer.List.IsIncomplete && caret > latest.Offset && WordBefore(caret).Length > 0)
            Ask(new CompletionTrigger(CompletionTriggerKind.TriggerForIncompleteCompletions));
        Filter(session, answer, caret);
    }

    /// <summary>
    /// Filters <paramref name="answer"/> for the text up to the caret, off the UI thread, unless that filter runs or
    /// shows already: the filter under way stops, and the list shows the new one's result once it has run.
    /// </summary>
    private void Filter(CompletionSession session, CompletionAnswer answer, int caret)
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
        if (session.CommitAt != key)
            session.CommitAt = null;
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
            if (filtered is null || _session != session || session.Filtering != filtering)
                return;
            session.Filtering = null;
            // It counts only for the text and the caret it was filtered for: a change since outdates it, also one
            // that left the caret where it was.
            if (filtering.Key != CurrentKey(filtering.Key.Answer))
            {
                Update();
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
            PressedKey? waiting = session.CommitAt == key ? session.CommitKey : null;
            // An incomplete list may have more for what is typed, and a newer request, as for a trigger character typed
            // while the list showed, brings a list of its own: its answer shows, or ends completion when it has nothing.
            if (!session.ByKey)
            {
                if (key.Answer.List.IsIncomplete || session.Pending is not null)
                    Hide(session);
                else
                    Close();
            }
            else if (session.Shown?.Answer != key.Answer)
            {
                Hide(session);
                session.ShowsNoMatch = true;
                ShowMessage(key.Answer.Request.Word.Start);
            }
            else if (WordBefore(key.Caret).Length == 0)
                Close();
            else
            {
                Freeze(session, key);
                return;
            }
            if (waiting is { } pressed)
                Replay(pressed);
            return;
        }
        session.FrozenAt = null;
        session.ShowsNoMatch = false;
        session.Items = filtered.Items;
        session.Selected = filtered.Selected;
        session.Shown = key;
        if (session.CommitAt == key)
        {
            Commit(session, session.Selected, session.CommitReplaces);
            return;
        }
        int wordStart = key.Answer.Request.Word.Start;
        _view.Show(session.Items, session.Selected, Look(), _icons, _textView.DefaultLineHeight);
        _view.Open(_textView, () => TextViewBoxes.CharacterAt(_textView, Math.Min(wordStart, _buffer.TextLength)));
        // Placing it may have ended completion, as its line is out of view.
        if (_session == session)
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
            Commit(session, session.Selected, session.CommitReplaces);
        else if (!wasFrozen)
        {
            ShownItem[] items = [.. session.Items.Select(shown => shown with { Highlights = [] })];
            _view.Show(items, session.Selected, Look(), _icons, _textView.DefaultLineHeight);
        }
    }

    /// <summary>Shows "No suggestions." at the word that starts at <paramref name="wordStart"/>.</summary>
    private void ShowMessage(int wordStart)
    {
        _view.ShowMessage("No suggestions.", Look(), _textView.DefaultLineHeight);
        _view.Open(_textView, () => TextViewBoxes.CharacterAt(_textView, Math.Min(wordStart, _buffer.TextLength)));
    }

    /// <summary>Hides "No suggestions." of an empty answer, if it shows.</summary>
    /// <returns>Whether it showed.</returns>
    private bool HideEmpty()
    {
        if (!_showsEmpty)
            return false;
        _showsEmpty = false;
        _view.Hide();
        return true;
    }

    /// <summary>
    /// Presses <paramref name="key"/> again, now that the list no longer takes it: an accept that waited for a filter
    /// that found nothing goes on as it would have without the list, as Enter to a new line. In VS Code, which filters
    /// as the user types, the list is gone before such a key.
    /// </summary>
    private void Replay(PressedKey key) =>
        _textArea.RaiseEvent(
            new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Source = _textArea,
                Key = key.Key,
                KeyModifiers = key.Modifiers,
                KeySymbol = key.Symbol,
            }
        );

    /// <summary>
    /// Whether the item at <paramref name="index"/> may be committed now: the list shows the filter of the current
    /// text, frozen or not, or the item matches it, which is checked for that item alone.
    /// </summary>
    private bool CanCommit(CompletionSession session, int index)
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
    /// Commits what the filter of the current text selects once it has run, for <see cref="Accept"/>, unless the text or
    /// the caret changes before; starts that filter if none runs. When that filter finds nothing to commit,
    /// <paramref name="key"/>, the key that accepted, does what it does without a list.
    /// </summary>
    private void CommitWhenFiltered(CompletionSession session, bool replace, PressedKey? key)
    {
        if (session.Answer is not { } answer)
            return;
        session.CommitAt = CurrentKey(answer);
        session.CommitReplaces = replace;
        session.CommitKey = key;
        Filter(session, answer, _textArea.Caret.Offset);
    }

    /// <summary>What filtering <paramref name="answer"/> now would filter for.</summary>
    private FilterKey CurrentKey(CompletionAnswer answer) =>
        new(answer, _document.CurrentSnapshot.Version, _textArea.Caret.Offset);

    private void Select(CompletionSession session, int index)
    {
        session.Selected = index;
        _view.Select(index);
        ResolveSelected(session);
    }

    /// <summary>Hides the list while completion waits for an answer; a filter of the old one under way stops.</summary>
    private void Hide(CompletionSession session)
    {
        session.ShowsNoMatch = false;
        session.FrozenAt = null;
        session.Filtering?.Cancellation.Cancel();
        session.Filtering = null;
        session.CommitAt = null;
        session.Shown = null;
        session.Items = [];
        session.DetailsOf = null;
        session.Details?.Cancel();
        _view.Hide();
        _view.ShowDetails(null);
    }

    /// <summary>
    /// Shows the selected item's details, and asks the server to resolve it, unless it did: the resolved details
    /// replace them once it answers, if the item is still selected. The resolve of an item no longer selected is
    /// cancelled while it is under way.
    /// </summary>
    private async void ResolveSelected(CompletionSession session)
    {
        CompletionItem item = session.Items[session.Selected].Item;
        // Whether or not this item still needs its own, the resolve of another one is no longer needed.
        foreach (
            (CompletionItem other, ItemResolve running) in session
                .Resolves.Where(r => !ReferenceEquals(r.Key, item) && !r.Value.Task.IsCompleted)
                .ToList()
        )
        {
            running.Cancellation.Cancel();
            session.Resolves.Remove(other);
        }
        ShowDetails(session, session.ResolvedOf(item) ?? item);
        if (session.Shown?.Answer is not { List.Resolves: true } answer || session.Resolves.ContainsKey(item))
            return;
        var cancellation = new CancellationTokenSource();
        Task<CompletionItem?> resolving = answer.List.ResolveAsync(item, cancellation.Token);
        session.Resolves[item] = new ItemResolve(resolving, cancellation);
        try
        {
            if (
                await resolving is { } resolved
                && _session == session
                && session.IsShowing
                && ReferenceEquals(session.Items[session.Selected].Item, item)
            )
            {
                ShowDetails(session, resolved);
            }
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Error, AssetFiles.LogArea)?.Log(this, "Resolving failed: {Exception}", e);
        }
    }

    /// <summary>
    /// Shows <paramref name="item"/>'s detail, as code, and its documentation beside the list, read off the UI thread
    /// as the hover reads its Markdown; nothing when it has neither. Typing that keeps the item selected keeps them.
    /// </summary>
    private async void ShowDetails(CompletionSession session, CompletionItem item)
    {
        if (ReferenceEquals(session.DetailsOf, item))
            return;
        session.DetailsOf = item;
        session.Details?.Cancel();
        session.Details = null;
        if (!_showsDetails || (item.Detail is null && item.Documentation is null))
        {
            _view.ShowDetails(null);
            return;
        }
        var cancellation = new CancellationTokenSource();
        session.Details = cancellation;
        TextMateGrammar? grammar = _highlighter.Grammar;
        try
        {
            IReadOnlyList<HoverBlock> blocks = await Task.Run(
                () => DetailBlocks(item, grammar, cancellation.Token),
                cancellation.Token
            );
            if (_session != session || cancellation.IsCancellationRequested || !session.IsShowing)
                return;
            _view.ShowDetails(HoverView.Create(blocks, Look()));
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
        {
            // Lines end at \r\n, \n or \r, as everywhere in the library.
            string[] lines = kept.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            blocks.AddRange(budget.Fit([HoverBlocks.Highlight(lines, grammar, cancellationToken)]));
        }
        if (item.Documentation is { } documentation)
            blocks.AddRange(HoverBlocks.Of(new HoverInfo([documentation], null), grammar, budget, cancellationToken));
        return blocks;
    }

    /// <summary>
    /// Commits an item: its edit, from where it starts to where its insert range, or its replace range for
    /// <paramref name="replace"/>, ends as the edits since completion was asked moved it, or to the caret where that is
    /// further, and its additional edits, in one step that the user undoes; the caret goes after the item's text. A
    /// snippet goes in as its text, with its variables' values for the text before the commit, and its first stop is
    /// selected. Additional edits that overlap others are left out, and stay plain text. Additional edits that a resolve
    /// still under way brings come later.
    /// </summary>
    private void Commit(CompletionSession session, int index, bool replace = false)
    {
        if (session.Shown?.Answer is not { } answer)
            return;
        if (!_textArea.ReadOnlySectionProvider.CanInsert(_textArea.Caret.Offset))
        {
            Close();
            return;
        }
        CompletionItem selected = session.Items[index].Item;
        CompletionItem item = session.ResolvedOf(selected) ?? selected;
        CompletionRequest request = answer.Request;
        int caret = _textArea.Caret.Offset;
        // Text typed at the caret goes in the range, and once the rest of the word is deleted, also by another editor or
        // code, the text after it stays out.
        TextRange since = TextRange.FromBounds(request.Offset, replace ? item.ReplaceRange.End : item.Edit.Range.End);
        int end = _document.MapToCurrent(request.Snapshot, since).End;
        var range = TextRange.FromBounds(
            item.Edit.Range.Start,
            Math.Min(_buffer.GetLineByOffset(caret).EndOffset, Math.Max(caret, end))
        );
        RenderedSnippet? snippet = item.Snippet is { } source ? _snippets.Render(source, range.Start) : null;
        var main = new CompletionEdit(range, snippet?.Text ?? InLineBreaks(item.Edit.NewText));
        ItemResolve? late = null;
        if (item.AdditionalEdits is null && answer.List.Resolves)
        {
            // Taken out of the session, so that ending it does not cancel it.
            if (!session.Resolves.Remove(selected, out late))
            {
                var cancellation = new CancellationTokenSource();
                late = new ItemResolve(answer.List.ResolveAsync(selected, cancellation.Token), cancellation);
            }
        }
        Close();
        var step = new object();
        var mark = new SnippetMark();
        List<CompletionEdit> edits = WithoutOverlaps([main], Mapped(request.Snapshot, item.AdditionalEdits ?? []));
        Apply(step, continued: false, edits, snippet is null ? null : mark);
        // The item's text moved by the additional edits before it.
        int start =
            main.Range.Start
            + edits.Skip(1).Where(e => e.Range.End <= main.Range.Start).Sum(e => e.NewText.Length - e.Range.Length);
        _textArea.ClearSelection();
        _textArea.Caret.Offset = start + main.NewText.Length;
        _textArea.Caret.BringCaretToView();
        if (snippet is not null)
            _snippets.Start(snippet, start, mark);
        if (late is not null)
            ApplyLateEdits(late, step, request.Snapshot);
    }

    /// <summary>
    /// Applies the additional edits that a committed item's resolve brings, in the commit's undo step, if that step is
    /// still the last one; drops them otherwise.
    /// </summary>
    private async void ApplyLateEdits(ItemResolve late, object step, TextSnapshot snapshot)
    {
        _lateEdits?.Cancel();
        _lateEdits = late.Cancellation;
        try
        {
            CompletionItem? item = await late.Task;
            if (
                late.Cancellation.IsCancellationRequested
                || item?.AdditionalEdits is not { Count: > 0 } additional
                || _buffer.UndoStack.LastGroupDescriptor != step
                || !_textArea.ReadOnlySectionProvider.CanInsert(_textArea.Caret.Offset)
            )
            {
                return;
            }
            Apply(step, continued: true, WithoutOverlaps([], Mapped(snapshot, additional)), mark: null);
        }
        catch (Exception e)
        {
            Logger
                .TryGet(LogEventLevel.Error, AssetFiles.LogArea)
                ?.Log(this, "Applying an item's additional edits failed: {Exception}", e);
        }
        finally
        {
            if (_lateEdits == late.Cancellation)
                _lateEdits = null;
        }
    }

    /// <summary>
    /// Applies edits, from the last to the first, in the undo step <paramref name="step"/>: a new one, or the last one
    /// continued. A snippet's <paramref name="mark"/> goes in first, so that undoing the step reaches it last, once
    /// the text is back.
    /// </summary>
    private void Apply(object step, bool continued, List<CompletionEdit> edits, SnippetMark? mark)
    {
        UndoStack undo = _buffer.UndoStack;
        if (continued)
            undo.StartContinuedUndoGroup(step);
        else
            undo.StartUndoGroup(step);
        try
        {
            if (mark is not null)
                undo.Push(mark);
            using (_buffer.RunUpdate())
            {
                foreach (CompletionEdit edit in edits.OrderByDescending(e => e.Range.Start))
                    _buffer.Replace(edit.Range.Start, edit.Range.Length, edit.NewText);
            }
        }
        finally
        {
            undo.EndUndoGroup();
        }
    }

    /// <summary>
    /// Edits in the text of <paramref name="snapshot"/>, moved to the current text, with the document's line breaks.
    /// </summary>
    private IEnumerable<CompletionEdit> Mapped(TextSnapshot snapshot, IEnumerable<CompletionEdit> edits) =>
        edits.Select(edit => new CompletionEdit(
            _document.MapToCurrent(snapshot, edit.Range),
            InLineBreaks(edit.NewText)
        ));

    private string InLineBreaks(string text) => TextUtilities.NormalizeNewLines(text, _document.LineBreak);

    /// <summary><paramref name="first"/> and the edits that overlap none before them.</summary>
    private static List<CompletionEdit> WithoutOverlaps(List<CompletionEdit> first, IEnumerable<CompletionEdit> more)
    {
        foreach (CompletionEdit edit in more)
        {
            if (first.TrueForAll(other => edit.Range.End <= other.Range.Start || edit.Range.Start >= other.Range.End))
                first.Add(edit);
        }
        return first;
    }

    private void OnClicked(int index)
    {
        if (
            _session is { IsShowing: true } session
            && index >= 0
            && index < session.Items.Count
            && CanCommit(session, index)
        )
        {
            Commit(session, index);
        }
    }

    /// <summary>Opens completion on the server's trigger characters, and when typing starts a word.</summary>
    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: > 0 } text || _document.Features?.Completion is not { } provider)
            return;
        char typed = text[^1];
        if (provider.TriggerCharacters.Contains(typed))
            Ask(new CompletionTrigger(CompletionTriggerKind.TriggerCharacter, typed));
        else if (_session is null && StartsWord(typed))
            Ask(new CompletionTrigger(CompletionTriggerKind.Invoked));
    }

    /// <summary>
    /// Whether the character just typed starts a word outside a comment: it is the whole word before the caret, and no
    /// word goes on after the caret. VS Code asks there too in strings, as its JSON and YAML settings say.
    /// </summary>
    private bool StartsWord(char typed)
    {
        int caret = _textArea.Caret.Offset;
        if (!WordCharacters.IsWordCharacter(typed) || WordBefore(caret).Length != 1)
            return false;
        if (
            caret < _buffer.GetLineByOffset(caret).EndOffset
            && WordCharacters.IsWordCharacter(_buffer.GetCharAt(caret))
        )
            return false;
        return _highlighter.ContextAt(caret) is not { Before: TokenType.Comment };
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e) => Update();

    /// <summary>A part of a change before where completion was asked makes the request stale.</summary>
    private void OnBufferChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (_session is not { } session)
            return;
        foreach (CompletionRequest? request in (CompletionRequest?[])[session.Answer?.Request, session.Pending])
        {
            if (request is { IsStale: false } && e.Offset < request.Offset)
                request.IsStale = true;
        }
    }

    /// <summary>
    /// After any change of the text, by this editor, another editor of the document or code: follows it, also when it
    /// left the caret where it was. The filter under way, and a commit that waits for it, are for older text.
    /// </summary>
    private void OnBufferTextChanged(object? sender, EventArgs e) => Update();

    private void OnClosingChange(object? sender, EventArgs e) => Close();

    /// <summary>
    /// The word before <paramref name="caret"/>, by VS Code's word characters, which ends at the caret. It reaches
    /// back at most <see cref="HoverController.MaxWordReach"/> characters, as a hovered word does: a line of millions
    /// of word characters costs no more.
    /// </summary>
    private TextRange WordBefore(int caret)
    {
        DocumentLine line = _buffer.GetLineByOffset(caret);
        int first = Math.Max(line.Offset, caret - HoverController.MaxWordReach);
        int start = caret;
        while (start > first && WordCharacters.IsWordCharacter(_buffer.GetCharAt(start - 1)))
            start--;
        return TextRange.FromBounds(start, caret);
    }

    private PopupLook Look() => new(_editor.FontFamily, _editor.FontSize, _colors);
}

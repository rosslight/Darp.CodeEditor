using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
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
/// Completion opens on Ctrl+Space, also on macOS, on the server's trigger characters, and, as VS Code's quick
/// suggestions, on a word character typed outside a comment at a word's end or as its first character; Backspace opens
/// nothing. Until the server answers, nothing shows; what is typed meanwhile filters its answer. The list is the
/// server's for where it was asked: typing on filters and ranks it on the client, with the matching of
/// <see cref="CompletionMatcher"/>, and selects the first item, or the one the server preselects among the best. As in
/// VS Code, typing on ranks the items that show, and anything else, such as Backspace, all of the answer's items; up
/// to 2,000 ranked items tolerate a typo. The matching runs off the UI thread, whatever the list's length: each change
/// cancels the filter under way, and the list shows the newest result, the previous one meanwhile. A list that the
/// server marks incomplete is asked for again as the user types on, and shows meanwhile. Moving back before
/// where completion was asked, and any edit before it, as by another editor of the document, asks again while a word
/// is left, as VS Code does when the caret moves back: the list's positions no longer fit the text.
/// </para>
/// <para>
/// When nothing matches what is typed, a list opened by typing ends. One asked for by key stays as VS Code's does:
/// frozen, as the answer's items showed last, without their matched characters in bold, while the caret is in a word;
/// accepting or a click commits the selected item over what was typed, and its commit characters go in as text. It
/// says "No suggestions." when none of the answer's items showed, as when nothing matched where it was asked.
/// </para>
/// <para>
/// Up, Down, Page Up and Page Down select, and the keymap's accept commands, a click or one of the selected item's
/// commit characters commit it: its edit, or its text over the word, and its additional edits, such as an import, in
/// one step that the user undoes. <see cref="Accept"/> inserts, over the word up to the caret, or replaces, over the
/// rest of the word too; a click and a commit character insert. While the filter of the current text still runs, they
/// commit the item that shows if it matches the current text, checked on the UI thread for that item alone; otherwise
/// accepting commits what that filter selects once it has run, unless the text, by any editor or code, or the caret
/// changes before, and a commit character goes in as text, as it does when nothing matches: deciding later would hold
/// back or reorder what the user types. A read-only
/// editor neither completes nor commits. The server resolves the selected item, so that its detail and documentation
/// show beside the list. An item whose additional edits are still to come when it is committed gets them when they
/// come, in its step, if that step is still the last one: otherwise they are dropped, as the user moved on.
/// </para>
/// <para>
/// <see cref="Close"/> ends completion, for every reason it ends: Escape, the caret leaving the line or moving back
/// past the word, a click or drag that moves the caret, a selection, the editor becoming read-only, the list scrolling
/// out of view, the editor losing the keyboard focus, a server that starts or stops serving the document, new colors,
/// the list running out of matches as above, and the editor no longer showing the document. Ending it cancels the
/// request still under way with <c>$/cancelRequest</c>, and so does a newer request; a late answer is dropped.
/// </para>
/// </remarks>
internal sealed class CompletionController : IDisposable
{
    private readonly CodeEditor _editor;
    private readonly TextArea _textArea;
    private readonly TextView _textView;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly SnippetController _snippets;
    private readonly CompletionView _view;
    private TextAreaColors? _colors;
    private SymbolIconColors? _icons;

    /// <summary>Whether the selected item's details show beside the list: for the app's run, as VS Code keeps it.</summary>
    private static bool s_showsDetails = true;

    /// <summary>Completion from its first request until it ends; null while there is none.</summary>
    private Session? _session;

    /// <summary>
    /// The message that shows at the word instead of a list after the user asked by key: "Loading…" while the server
    /// takes long, or "No suggestions." once it answered with none; null while none shows.
    /// </summary>
    private string? _message;

    /// <summary>The wait after asking by key before "Loading…" shows; null while none runs.</summary>
    private ITimer? _loading;

    /// <summary>
    /// The word, up to the caret, that a request opened by typing found nothing for; null for none. Typing on in it
    /// asks no more: a server that answers nothing there, as Pyright does in strings, would be asked again on every
    /// key. Anything else, such as Backspace, a caret move or another editor's edit, ends it, so that typing after it
    /// asks again.
    /// </summary>
    private TextRange? _quiet;

    /// <summary>Cancels the resolve of the item committed last, whose additional edits are still to come.</summary>
    private CancellationTokenSource? _lateEdits;

    /// <summary>
    /// Ranks lists off the UI thread, for one filter at a time; made the first time a list is filtered, as it takes
    /// about 150 KB.
    /// </summary>
    private CompletionMatcher? _filterMatcher;

    /// <summary>Checks a single item on the UI thread; made the first time one is checked.</summary>
    private CompletionMatcher? _itemMatcher;

    /// <summary>
    /// The filter given <see cref="_filterMatcher"/> last: the next one runs after it, so that the matcher serves one
    /// thread at a time.
    /// </summary>
    private Task _matching = Task.CompletedTask;

    public CompletionController(
        CodeEditor editor,
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

    /// <summary>The colors of the color theme, for code in the details, once it is loaded. New colors end completion.</summary>
    public TextAreaColors? Colors
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

    /// <summary>
    /// The keys of completion's list while it shows, from the editor's key handler while the text area has the
    /// keyboard: the keys that select, while it shows more than one item. With one, they go on to the text, as in VS
    /// Code, and a caret that leaves the line ends completion. The keys that commit are the keymap's, for
    /// <see cref="Accept"/>.
    /// </summary>
    /// <returns>Whether the key was completion's.</returns>
    public bool OnKeyDown(KeyEventArgs e)
    {
        // "No suggestions." shows until the next key.
        if (_session is null)
            HideMessage();
        if (_session is not { IsShowing: true, Items.Count: > 1 } session || e.KeyModifiers != KeyModifiers.None)
            return false;
        int count = session.Items.Count;
        int page = _view.List.VisibleRows - 1;
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
    /// Commits the selected item while the list shows, for the keymap's accept commands: inserting, its text goes over
    /// the word up to the caret, and the rest of the word stays after it; replacing, it goes over the rest of the word
    /// too, or as far as the item's replace range reaches.
    /// </summary>
    /// <returns>Whether the list showed: otherwise the key goes on, as to a new line or an indent.</returns>
    public bool Accept(bool replace)
    {
        if (_session is not { IsShowing: true } session)
            return false;
        if (CanCommit(session, session.Selected))
            Commit(session, session.Selected, replace);
        else
            CommitWhenFiltered(session, replace);
        return true;
    }

    /// <summary>Asks for completion at the caret, as the user asks for it by key.</summary>
    public void Trigger() => Ask(new CompletionTrigger(CompletionTriggerKind.Invoked), explicitly: true);

    /// <summary>
    /// Shows or hides the selected item's details while the list shows; the choice holds for the app's run, as VS Code
    /// remembers it.
    /// </summary>
    /// <returns>Whether the list showed.</returns>
    public bool ToggleDetails()
    {
        if (_session is not { IsShowing: true } session)
            return false;
        s_showsDetails = !s_showsDetails;
        session.DetailsOf = null;
        ResolveSelected(session);
        return true;
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
    /// <returns>Whether the list showed.</returns>
    public bool Close()
    {
        StopLoading();
        bool messageShowed = HideMessage();
        if (_session is not { } session)
            return messageShowed;
        _session = null;
        session.Pending?.Cancellation.Cancel();
        session.Filtering?.Cancellation.Cancel();
        foreach (Resolve resolve in session.Resolves.Values)
            resolve.Cancellation.Cancel();
        session.Details?.Cancel();
        _view.Hide();
        _view.ShowDetails(null);
        return session.IsShowing || session.ShowsNoMatch || messageShowed;
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
    /// and the items that show stay until it answers.
    /// </summary>
    private void Ask(CompletionTrigger trigger, bool explicitly = false)
    {
        int caret = _textArea.Caret.Offset;
        if (!_textArea.ReadOnlySectionProvider.CanInsert(caret))
            return;
        var request = new Request(_document.CurrentSnapshot, WordBefore(caret));
        _quiet = null;
        HideMessage();
        Session session = _session ??= new Session();
        session.IsExplicit |= explicitly;
        if (explicitly && !session.IsShowing)
            StartLoading(session, request);
        session.Pending?.Cancellation.Cancel();
        session.Pending = request;
        AskAsync(session, request, trigger);
    }

    /// <summary>Shows the server's answer, unless completion ended or asked again meanwhile.</summary>
    private async void AskAsync(Session session, Request request, CompletionTrigger trigger)
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
            StopLoading();
            // Nothing here, as in a comment or a string, where some servers answer an empty list that is incomplete:
            // asking again on each key would never end. Asked by key, the user learns so; otherwise nothing shows,
            // and typing on in the word asks no more.
            if (list is null || list.Items.Count == 0)
            {
                if (!session.IsExplicit || session.Answer is null)
                {
                    bool explicitly = session.IsExplicit;
                    Close();
                    if (explicitly)
                        ShowMessage("No suggestions.", request);
                    else if (
                        !request.IsStale
                        && WordBefore(_textArea.Caret.Offset) is { Length: > 0 } word
                        && word.Start == request.Word.Start
                    )
                    {
                        _quiet = word;
                    }
                    return;
                }
                list ??= new CompletionList([], isIncomplete: false, resolve: null);
            }
            session.Answer = new Answer(list, request);
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
            // "No suggestions." shows until the caret moves.
            HideMessage();
            return;
        }
        int caret = _textArea.Caret.Offset;
        Request latest = session.Latest;
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
    private void Filter(Session session, Answer answer, int caret)
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
        var filtering = new Filtering(key, Refiltered(session, key), new CancellationTokenSource());
        session.Filtering = filtering;
        // The UI thread reads no text: each item's typed text is read off it, from the snapshot of the key's version.
        FilterAsync(session, filtering, _document.CurrentSnapshot);
    }

    /// <summary>
    /// The items that show, when a filter for <paramref name="key"/> ranks them instead of all of the answer's items,
    /// as VS Code refilters: they were filtered for the same answer with the caret further back, as typing on leaves
    /// them. Null otherwise, as after Backspace.
    /// </summary>
    private static List<ShownItem>? Refiltered(Session session, FilterKey key) =>
        session.Shown is { } shown && shown.Answer == key.Answer && key.Caret > shown.Caret ? session.Items : null;

    /// <summary>Shows a filter's result, unless completion ended, or a newer filter replaced it.</summary>
    private async void FilterAsync(Session session, Filtering filtering, TextSnapshot snapshot)
    {
        try
        {
            CancellationToken cancellationToken = filtering.Cancellation.Token;
            Task<Filtered?> ranking = _matching.ContinueWith(
                _ =>
                    Rank(
                        filtering.Shown?.ConvertAll(shown => shown.Item) ?? filtering.Key.Answer.List.Items,
                        snapshot,
                        filtering.Key.Caret,
                        cancellationToken
                    ),
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default
            );
            _matching = ranking;
            Filtered? filtered = await ranking;
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
    /// Off the UI thread, after the filter before: the items that match the text typed for each, from where its edit
    /// starts up to <paramref name="caret"/>, best first, and the one to select; null once cancelled. Of each typed
    /// text only what the matcher reads is read, however far before the caret the edit starts. Up to
    /// <see cref="CompletionMatcher.MaxGracefulItems"/> items match gracefully, tolerating a typo.
    /// </summary>
    private Filtered? Rank(
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
            ranked = (_filterMatcher ??= new CompletionMatcher()).Rank(candidates, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        // As VS Code: the first item, or the first that the server preselects among those that match best.
        int preselected = ranked.FindIndex(r => r.Score == ranked[0].Score && items[r.Index].IsPreselected);
        return new Filtered(
            [.. ranked.Select(r => new ShownItem(items[r.Index], r.Highlights))],
            Math.Max(0, preselected)
        );
    }

    /// <summary>Shows a filter's items, or ends completion when none match a complete list.</summary>
    private void Show(Session session, FilterKey key, Filtered filtered)
    {
        if (filtered.Items.Count == 0)
        {
            // As in VS Code: asked by key, the items of the answer that showed last stay, frozen, while the caret is in
            // a word; when none of its items showed, the list says that nothing matches. Otherwise, an incomplete list
            // may have more for what is typed, and a newer request, as for a trigger character typed while the list
            // showed, brings a list of its own: its answer shows, or ends completion when it has nothing.
            if (!session.IsExplicit)
            {
                if (key.Answer.List.IsIncomplete || session.Pending is not null)
                    Hide(session);
                else
                    Close();
            }
            else if (session.Shown?.Answer != key.Answer)
                ShowNoMatch(session, key);
            else if (WordBefore(key.Caret).Length == 0)
                Close();
            else
                Freeze(session, key);
            return;
        }
        session.ShowsNoMatch = false;
        session.FrozenAt = null;
        StopLoading();
        _message = null;
        session.Items = filtered.Items;
        session.Selected = filtered.Selected;
        session.Shown = key;
        if (session.CommitAt == key)
        {
            Commit(session, session.Selected, session.CommitReplaces);
            return;
        }
        int wordStart = key.Answer.Request.Word.Start;
        Func<Rect> place = () => TextViewBoxes.CharacterAt(_textView, Math.Min(wordStart, _buffer.TextLength));
        session.Place = place;
        _view.Show(session.Items, session.Selected, Look(), _icons, _textView.DefaultLineHeight);
        _view.Open(_textView, place);
        // Placing it may have ended completion, as its line is out of view.
        if (_session == session)
            ResolveSelected(session);
    }

    /// <summary>
    /// The list keeps showing its items for the text that <paramref name="key"/> is for, which none of them matches,
    /// as VS Code's frozen list: without their matched characters in bold, and without committing on their commit
    /// characters. The keys that accept, and a click, commit the selected item over what was typed; an accept that
    /// waited for this filter does so now.
    /// </summary>
    private void Freeze(Session session, FilterKey key)
    {
        bool wasFrozen = session.FrozenAt is not null;
        session.FrozenAt = key;
        if (session.CommitAt == key)
        {
            Commit(session, session.Selected, session.CommitReplaces);
            return;
        }
        if (!wasFrozen)
        {
            _view.Show(
                [.. session.Items.Select(shown => shown with { Highlights = [] })],
                session.Selected,
                Look(),
                _icons,
                _textView.DefaultLineHeight
            );
        }
    }

    /// <summary>
    /// The list, at the word, says that nothing matches what is typed: the items that showed are gone, and the keys
    /// that accept go to the text again.
    /// </summary>
    private void ShowNoMatch(Session session, FilterKey key)
    {
        StopLoading();
        session.Filtering?.Cancellation.Cancel();
        session.Filtering = null;
        session.CommitAt = null;
        session.FrozenAt = null;
        session.Shown = null;
        session.Items = [];
        session.DetailsOf = null;
        session.Details?.Cancel();
        session.ShowsNoMatch = true;
        int wordStart = key.Answer.Request.Word.Start;
        _view.ShowMessage("No suggestions.", Look(), _textView.DefaultLineHeight, narrow: false);
        _view.Open(_textView, () => TextViewBoxes.CharacterAt(_textView, Math.Min(wordStart, _buffer.TextLength)));
    }

    /// <summary>Shows <paramref name="message"/> at the word of <paramref name="request"/>, as VS Code does after a request by key.</summary>
    private void ShowMessage(string message, Request request)
    {
        _message = message;
        int wordStart = request.Word.Start;
        _view.ShowMessage(message, Look(), _textView.DefaultLineHeight, narrow: true);
        _view.Open(_textView, () => TextViewBoxes.CharacterAt(_textView, Math.Min(wordStart, _buffer.TextLength)));
    }

    /// <summary>Hides the message, if it shows on its own.</summary>
    /// <returns>Whether it showed.</returns>
    private bool HideMessage()
    {
        if (_message is null)
            return false;
        _message = null;
        if (_session is not { IsShowing: true } and not { ShowsNoMatch: true })
            _view.Hide();
        return true;
    }

    /// <summary>"Loading…" shows once a request by key takes longer than 50 ms, VS Code's wait, until the list shows.</summary>
    private void StartLoading(Session session, Request request)
    {
        StopLoading();
        ITimer? loading = null;
        loading = _document.TimeProvider.CreateTimer(
            _ =>
                _editor.Dispatcher.Post(() =>
                {
                    if (_loading != loading || _session != session || session.IsShowing || session.ShowsNoMatch)
                        return;
                    StopLoading();
                    ShowMessage("Loading…", request);
                }),
            null,
            TimeSpan.FromMilliseconds(50),
            Timeout.InfiniteTimeSpan
        );
        _loading = loading;
    }

    private void StopLoading()
    {
        _loading?.Dispose();
        _loading = null;
    }

    /// <summary>
    /// Whether the item at <paramref name="index"/> may be committed now: the list shows the filter of the current
    /// text, or the item matches it, which is checked for that item alone, as gracefully as the filter matches.
    /// </summary>
    private bool CanCommit(Session session, int index)
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
        int ranked = Refiltered(session, CurrentKey(shown.Answer))?.Count ?? shown.Answer.List.Items.Count;
        return (_itemMatcher ??= new CompletionMatcher()).Match(
            Typed(_document.CurrentSnapshot, item.Edit.Range.Start, caret),
            item.Label,
            item.FilterText,
            item.FilterTextIsLabel,
            graceful: ranked <= CompletionMatcher.MaxGracefulItems
        )
            is not null;
    }

    /// <summary>
    /// Commits what the filter of the current text selects once it has run, for <see cref="Accept"/>, unless the text
    /// or the caret changes before; starts that filter if none runs.
    /// </summary>
    private void CommitWhenFiltered(Session session, bool replace)
    {
        if (session.Answer is not { } answer)
            return;
        session.CommitAt = CurrentKey(answer);
        session.CommitReplaces = replace;
        Filter(session, answer, _textArea.Caret.Offset);
    }

    /// <summary>
    /// The text typed from <paramref name="start"/> up to <paramref name="caret"/>, as far as the matcher reads it: an
    /// edit that starts far before the caret, also in a line's collapsed tail, costs no more than one near it, and
    /// matches by its first characters, as in VS Code. When they are all spaces and tabs, nothing counts as typed;
    /// VS Code reads on, and leaves the item out if anything else follows.
    /// </summary>
    private static string Typed(TextSnapshot snapshot, int start, int caret) =>
        snapshot.GetText(TextRange.FromBounds(start, Math.Min(caret, start + CompletionMatcher.MaxLength)));

    /// <summary>What filtering <paramref name="answer"/> now would filter for.</summary>
    private FilterKey CurrentKey(Answer answer) =>
        new(answer, _document.CurrentSnapshot.Version, _textArea.Caret.Offset);

    private void Select(Session session, int index)
    {
        session.Selected = index;
        _view.Select(index);
        ResolveSelected(session);
    }

    /// <summary>Hides the list while completion waits for an answer; a filter of the old one under way stops.</summary>
    private void Hide(Session session)
    {
        session.ShowsNoMatch = false;
        session.Filtering?.Cancellation.Cancel();
        session.Filtering = null;
        session.CommitAt = null;
        session.FrozenAt = null;
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
    private async void ResolveSelected(Session session)
    {
        CompletionItem item = session.Items[session.Selected].Item;
        // Whether or not this item still needs its own, the resolve of another one is no longer needed.
        foreach (
            (CompletionItem other, Resolve running) in session
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
        session.Resolves[item] = new Resolve(resolving, cancellation);
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
    private async void ShowDetails(Session session, CompletionItem item)
    {
        if (ReferenceEquals(session.DetailsOf, item))
            return;
        session.DetailsOf = item;
        session.Details?.Cancel();
        session.Details = null;
        if (!s_showsDetails || (item.Detail is null && item.Documentation is null))
        {
            _view.ShowDetails(null);
            return;
        }
        var cancellation = new CancellationTokenSource();
        session.Details = cancellation;
        TextMateGrammar? grammar = _highlighter.Grammar;
        IBrush dimmed = (_colors?.Popups ?? PopupColors.Default).Description;
        try
        {
            IReadOnlyList<HoverBlock> blocks = await Task.Run(
                () => DetailBlocks(item, grammar, dimmed, cancellation.Token),
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
    /// The detail, as plain text in the editor's font, dimmed, as VS Code shows it: a server's detail is any text, such as
    /// "Auto-import", not always code. Then the documentation. As much of both as a hover shows: one budget for both,
    /// cut before anything reads them, so that a server's long detail or documentation costs the UI thread no more than
    /// the largest hover.
    /// </summary>
    private static IReadOnlyList<HoverBlock> DetailBlocks(
        CompletionItem item,
        TextMateGrammar? grammar,
        IBrush dimmed,
        CancellationToken cancellationToken
    )
    {
        var budget = new HoverBudget();
        var blocks = new List<HoverBlock>();
        if (item.Detail is { } detail && budget.Take(detail) is { } kept)
        {
            // Lines end at \r\n, \n or \r, as everywhere in the library.
            blocks.AddRange(
                budget.Fit([new HoverCode([new CodeRun(kept.Replace("\r\n", "\n").Replace('\r', '\n'), dimmed)])])
            );
        }
        if (item.Documentation is { } documentation)
            blocks.AddRange(HoverBlocks.Of(new HoverInfo([documentation], null), grammar, budget, cancellationToken));
        return blocks;
    }

    /// <summary>
    /// Commits an item: its edit, from where it starts to as far past the caret as its insert range, or its replace
    /// range for <paramref name="replace"/>, reached past where completion was asked, and its additional edits, in one
    /// step that the user undoes; the caret goes after the item's text. A snippet goes in as its text, with its
    /// variables' values for the text before the commit, and its first stop is selected. Additional edits that overlap
    /// others are left out, and stay plain text. Additional edits that a resolve still under way brings come later.
    /// </summary>
    private void Commit(Session session, int index, bool replace = false)
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
        Request request = answer.Request;
        int caret = _textArea.Caret.Offset;
        int end = replace ? item.ReplaceRange.End : item.Edit.Range.End;
        var range = TextRange.FromBounds(
            item.Edit.Range.Start,
            Math.Min(_buffer.GetLineByOffset(caret).EndOffset, caret + (end - request.Offset))
        );
        RenderedSnippet? snippet = item.Snippet is { } source ? _snippets.Render(source, range.Start) : null;
        var main = new CompletionEdit(range, snippet?.Text ?? item.Edit.NewText);
        Resolve? late = null;
        if (item.AdditionalEdits is null && answer.List.Resolves)
        {
            // Taken out of the session, so that ending it does not cancel it.
            if (!session.Resolves.Remove(selected, out late))
            {
                var cancellation = new CancellationTokenSource();
                late = new Resolve(answer.List.ResolveAsync(selected, cancellation.Token), cancellation);
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
    private async void ApplyLateEdits(Resolve late, object step, TextSnapshot snapshot)
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

    /// <summary>Edits in the text of <paramref name="snapshot"/>, moved to the current text.</summary>
    private IEnumerable<CompletionEdit> Mapped(TextSnapshot snapshot, IEnumerable<CompletionEdit> edits) =>
        edits.Select(edit => edit with { Range = _document.MapToCurrent(snapshot, edit.Range) });

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

    /// <summary>
    /// Opens completion on the server's trigger characters, and on a word character typed while completion is closed.
    /// </summary>
    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: > 0 } text || _document.Features?.Completion is not { } provider)
            return;
        char typed = text[^1];
        if (provider.TriggerCharacters.Contains(typed))
            Ask(new CompletionTrigger(CompletionTriggerKind.TriggerCharacter, typed));
        else if (_session is null && OpensByTyping(typed))
            Ask(new CompletionTrigger(CompletionTriggerKind.Invoked));
    }

    /// <summary>
    /// Whether the character just typed opens completion, as VS Code's quick suggestions decide: it is a word
    /// character that ends a word, or that is the first character of a word, and the word is no number. Not in a
    /// comment; VS Code asks there too in strings, as its JSON and YAML settings say. Not while typing on in a word
    /// that a request found nothing for.
    /// </summary>
    private bool OpensByTyping(char typed)
    {
        int caret = _textArea.Caret.Offset;
        TextRange before = WordBefore(caret);
        if (!WordCharacters.IsWordCharacter(typed) || before.Length == 0)
            return false;
        if (_quiet is { } quiet && quiet.Start == before.Start && quiet.End == caret)
            return false;
        TextRange word = TextRange.FromBounds(before.Start, WordAfter(caret));
        if (word.End != caret && before.Length != 1)
            return false;
        if (IsNumber(_buffer.GetText(word.Start, word.Length)))
            return false;
        return _highlighter.ContextAt(caret) is not { Before: TokenType.Comment };
    }

    /// <summary>
    /// Whether <paramref name="word"/> reads as a number, as VS Code tells by JavaScript's <c>Number</c>: decimal
    /// digits, with an exponent, or hexadecimal, binary or octal ones after their prefix. A word holds no sign or dot.
    /// </summary>
    private static bool IsNumber(string word)
    {
        if (word == "Infinity")
            return true;
        if (word.Length > 2 && word[0] == '0' && char.ToLowerInvariant(word[1]) is 'x' or 'b' or 'o')
        {
            Func<char, bool> isDigit = char.ToLowerInvariant(word[1]) switch
            {
                'x' => char.IsAsciiHexDigit,
                'b' => c => c is '0' or '1',
                _ => c => c is >= '0' and <= '7',
            };
            return word.Skip(2).All(isDigit);
        }
        int i = 0;
        while (i < word.Length && char.IsAsciiDigit(word[i]))
            i++;
        if (i == 0 || i == word.Length)
            return i > 0;
        if (char.ToLowerInvariant(word[i]) != 'e')
            return false;
        int exponent = ++i;
        while (i < word.Length && char.IsAsciiDigit(word[i]))
            i++;
        return i == word.Length && i > exponent;
    }

    /// <summary>
    /// A caret moved by the keyboard filters, asks again or ends completion; one moved by the pointer, as by a click,
    /// ends it, as in VS Code.
    /// </summary>
    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_quiet is { } quiet && _textArea.Caret.Offset != quiet.End)
            _quiet = null;
        if (_editor.PointerMovesCaret)
            Close();
        else
            Update();
    }

    /// <summary>
    /// Typing on after <see cref="_quiet"/>'s word goes on with it; any other change ends it. A part of a change before
    /// where completion was asked makes the request stale.
    /// </summary>
    private void OnBufferChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (_quiet is { } quiet)
        {
            _quiet =
                e.RemovalLength == 0 && e.Offset == quiet.End
                    ? quiet with
                    {
                        Length = quiet.Length + e.InsertionLength,
                    }
                    : null;
        }
        if (_session is not { } session)
            return;
        foreach (Request? request in (Request?[])[session.Answer?.Request, session.Pending])
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

    /// <summary>
    /// Where the word that goes on after <paramref name="caret"/> ends, reaching as far as <see cref="WordBefore"/>
    /// does; the caret itself when no word goes on.
    /// </summary>
    private int WordAfter(int caret)
    {
        int last = Math.Min(_buffer.GetLineByOffset(caret).EndOffset, caret + HoverController.MaxWordReach);
        int end = caret;
        while (end < last && WordCharacters.IsWordCharacter(_buffer.GetCharAt(end)))
            end++;
        return end;
    }

    private PopupLook Look() => new(_editor.FontFamily, _editor.FontSize, _colors?.Popups ?? PopupColors.Default);

    /// <summary>A request for completion.</summary>
    /// <param name="snapshot">The text it was asked for.</param>
    /// <param name="word">The word before the caret then: what items without an edit replace.</param>
    private sealed class Request(TextSnapshot snapshot, TextRange word)
    {
        public TextSnapshot Snapshot { get; } = snapshot;

        public TextRange Word { get; } = word;

        /// <summary>
        /// Where the caret was, in <see cref="Snapshot"/>; also in the current text, while the request is not stale.
        /// </summary>
        public int Offset => Word.End;

        /// <summary>Cancels the request.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>
        /// Whether the text before <see cref="Offset"/> changed since: then the answer's positions no longer fit the
        /// current text.
        /// </summary>
        public bool IsStale { get; set; }
    }

    /// <summary>A server's list, with the request it answered.</summary>
    private sealed record Answer(CompletionList List, Request Request);

    /// <summary>An item's resolve, under way or done.</summary>
    private sealed record Resolve(Task<CompletionItem?> Task, CancellationTokenSource Cancellation);

    /// <summary>What a list is filtered for: an answer, at a version of the text and a caret.</summary>
    private readonly record struct FilterKey(Answer Answer, int Version, int Caret);

    /// <summary>A filter under way.</summary>
    /// <param name="Key">What it filters for.</param>
    /// <param name="Shown">The items that showed, which it ranks; null to rank all of the answer's items.</param>
    /// <param name="Cancellation">Stops it.</param>
    private sealed record Filtering(FilterKey Key, List<ShownItem>? Shown, CancellationTokenSource Cancellation);

    /// <summary>A filter's result: the items that match, in order, and the one to select.</summary>
    private sealed record Filtered(List<ShownItem> Items, int Selected);

    /// <summary>Completion from its first request until it ends.</summary>
    private sealed class Session
    {
        /// <summary>Whether the user asked for completion by key, rather than typing opening it.</summary>
        public bool IsExplicit { get; set; }

        /// <summary>Whether the list shows that nothing matches what is typed.</summary>
        public bool ShowsNoMatch { get; set; }

        /// <summary>
        /// The filter that found no match while the items of <see cref="Shown"/> stay, frozen; null while the list is
        /// not frozen.
        /// </summary>
        public FilterKey? FrozenAt { get; set; }

        /// <summary>The answer whose items show; null until the first one.</summary>
        public Answer? Answer { get; set; }

        /// <summary>The request under way; null for none.</summary>
        public Request? Pending { get; set; }

        /// <summary>The request asked last: the one under way, or the one whose answer shows.</summary>
        public Request Latest =>
            Pending ?? Answer?.Request ?? throw new InvalidOperationException("Completion was not asked for.");

        /// <summary>Whether the list shows.</summary>
        public bool IsShowing => Shown is not null;

        /// <summary>What the items that show were filtered for; null while none show.</summary>
        public FilterKey? Shown { get; set; }

        /// <summary>The filter under way; null for none.</summary>
        public Filtering? Filtering { get; set; }

        /// <summary>
        /// The filter whose selection <see cref="Accept"/> commits once it has run; null for none. A filter for
        /// anything else drops it.
        /// </summary>
        public FilterKey? CommitAt { get; set; }

        /// <summary>Whether the commit that waits for <see cref="CommitAt"/> replaces the rest of the word.</summary>
        public bool CommitReplaces { get; set; }

        /// <summary>The items that show, in order.</summary>
        public List<ShownItem> Items { get; set; } = [];

        /// <summary>The selected item's index in <see cref="Items"/>.</summary>
        public int Selected { get; set; }

        /// <summary>Where the list is: the box of the first character of the word it completes.</summary>
        public Func<Rect>? Place { get; set; }

        /// <summary>The resolves of the items, under way or done.</summary>
        public Dictionary<CompletionItem, Resolve> Resolves { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>The item whose details show, or are about to; null for none.</summary>
        public CompletionItem? DetailsOf { get; set; }

        /// <summary>Cancels the reading of the details that are about to show.</summary>
        public CancellationTokenSource? Details { get; set; }

        /// <summary>The item as the server resolved it; null while it did not.</summary>
        public CompletionItem? ResolvedOf(CompletionItem item) =>
            Resolves.TryGetValue(item, out Resolve? resolve) && resolve.Task.IsCompletedSuccessfully
                ? resolve.Task.Result
                : null;
    }
}

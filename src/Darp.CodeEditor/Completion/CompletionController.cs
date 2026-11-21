using Avalonia.Input;
using Avalonia.Logging;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
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
/// Completion opens on Ctrl+Space, also on macOS, on the server's trigger characters, and on a word that typing ends or
/// starts, where the language's quick suggestions say (<see cref="QuickSuggest"/>). Until the server answers, nothing
/// shows; what is typed meanwhile filters its answer, and so does what is typed while the list shows
/// (<see cref="CompletionFilter"/>). Opened by typing, an answer with nothing ends completion, and typing on in its word
/// opens nothing. A list that the server marks incomplete is asked for again as the user types on, and shows meanwhile.
/// Moving back before where completion was asked, and any edit before it, as by another editor of the document, asks
/// again while a word is left, as VS Code does when the caret moves back: the list's positions no longer fit the text.
/// Asked for by key, an empty answer says "No suggestions." until the next key or caret move.
/// </para>
/// <para>
/// Up, Down, Page Up and Page Down select among the rows that show, while there is more than one item; with one, they
/// go on to the text, as in VS Code. The accept commands, a click or one of the selected item's commit characters commit
/// it (<see cref="CompletionCommit"/>): accepting inserts or replaces, a click and a commit character insert, and a
/// commit character then goes in. A frozen list commits by accepting and by a click, but not by commit characters. A
/// read-only editor neither completes nor commits.
/// </para>
/// <para>
/// <see cref="Close"/> ends completion, for every reason it ends: Escape, the caret leaving the line or moving back past
/// the word, a selection, a command that moves the caret, the editor becoming read-only, the list scrolling out of view,
/// the text losing the keyboard focus, also to the search bar, which ends an accept that waits for the filter, a server
/// that starts or stops serving the document, new colors, the list running out of matches, and the editor no longer
/// showing the document. Ending it hides "No suggestions." too, and cancels the request still under way with
/// <c>$/cancelRequest</c>, and so does a newer request; a late answer is dropped.
/// </para>
/// </remarks>
internal sealed class CompletionController : IEditorFeature, ICompletionOwner
{
    private readonly TextArea _textArea;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly CompletionView _view;
    private readonly CompletionCommit _commit;
    private readonly CompletionResolver _resolver;
    private readonly CompletionFilter _filter;
    private readonly QuickSuggest _quickSuggest;

    /// <summary>Does the normal action of a key whose accept found nothing to commit.</summary>
    private readonly Action<KeyGesture> _performKey;

    /// <summary>Completion from its first request until it ends; null while there is none.</summary>
    private CompletionSession? _session;

    /// <summary>The view's commands, for the key that runs an accept.</summary>
    private CommandTable? _commands;

    public CompletionController(
        CodeEditor editor,
        TextArea textArea,
        CodeDocument document,
        BackgroundTextMateHighlighter highlighter,
        SnippetController snippets,
        TextChanges changes,
        TaskScheduler background,
        Action<KeyGesture> performKey
    )
    {
        _textArea = textArea;
        _buffer = textArea.Document;
        _document = document;
        _performKey = performKey;
        _view = new CompletionView(editor, textArea.TextView);
        _commit = new CompletionCommit(textArea, document, snippets, changes);
        _resolver = new CompletionResolver(_view, highlighter);
        _filter = new CompletionFilter(
            _view,
            new CompletionRanking(background),
            _resolver,
            textArea,
            document,
            WordBefore,
            this
        );
        _quickSuggest = new QuickSuggest(textArea, document, highlighter, WordBefore);
        _view.Clicked += OnClicked;
        _buffer.Changing += OnBufferChanging;
        _buffer.TextChanged += OnBufferTextChanged;
        _textArea.TextEntered += OnTextEntered;
        _textArea.Caret.PositionChanged += OnCaretPositionChanged;
        _textArea.LostFocus += OnClosingChange;
        _document.LanguageFeaturesChanged += OnClosingChange;
        _view.LeftView += OnClosingChange;
    }

    /// <summary>Whether the list shows items, frozen or not.</summary>
    private bool IsShowing => _session is not null && _filter.IsShowing;

    /// <summary>
    /// The popups' and the icons' colors, from the color theme once it is loaded. New colors end completion.
    /// </summary>
    public void SetColors(PopupColors colors, SymbolIconColors? icons)
    {
        if (colors == _view.Colors && icons == _view.Icons)
            return;
        _view.Colors = colors;
        _view.Icons = icons;
        Close();
    }

    public void AddCommands(CommandTable commands)
    {
        _commands = commands;
        const CommandNeeds needs = CommandNeeds.TextFocus | CommandNeeds.Writable;
        commands.Add(
            CodeEditorCommands.TriggerSuggest,
            () => Ask(new CompletionTrigger(CompletionTriggerKind.Invoked), byKey: true),
            needs,
            () =>
                _document.Features?.Completion is not null
                && _textArea.ReadOnlySectionProvider.CanInsert(_textArea.Caret.Offset)
        );
        commands.Add(
            CodeEditorCommands.ToggleSuggestionDetails,
            _resolver.ToggleDetails,
            CommandNeeds.TextFocus,
            () => IsShowing
        );
        commands.Add(CodeEditorCommands.AcceptSelectedSuggestion, () => Accept(replace: false), needs, () => IsShowing);
        commands.Add(
            CodeEditorCommands.AcceptAlternativeSelectedSuggestion,
            () => Accept(replace: true),
            needs,
            () => IsShowing
        );
    }

    /// <summary>
    /// Commits the selected item while the list shows: inserting, its text goes over the word up to the caret, and the
    /// rest of the word stays after it; replacing, it goes over the rest of the word too, or as far as the item's
    /// replace range reaches. While the filter of the current text still has to decide, that filter commits, and a key
    /// that finds nothing to commit does its normal action then.
    /// </summary>
    private void Accept(bool replace)
    {
        if (_filter.CanCommit(_filter.Selected, _session?.Answer))
            Commit(_filter.Selected, replace);
        else if (_session?.Answer is { } answer)
            _filter.CommitWhenFiltered(answer, replace, _commands?.Gesture);
    }

    /// <summary>
    /// The keys of completion's list while it shows more than one item, from the editor's key handler while the text area
    /// has the keyboard: those that select. Any key ends "No suggestions." of an empty answer.
    /// </summary>
    /// <returns>Whether the key was completion's.</returns>
    public bool OnKeyDown(KeyEventArgs e)
    {
        if (_session is null)
            _view.HideNoSuggestions();
        return _session is not null && e.KeyModifiers == KeyModifiers.None && _filter.SelectByKey(e.Key);
    }

    /// <summary>
    /// Text the user types, before it goes in: one of the selected item's commit characters commits it, and then goes in
    /// after it, if the item is known to match the current text. A frozen list commits nothing so, as in VS Code.
    /// </summary>
    public void OnTextInput(TextInputEventArgs e)
    {
        if (
            _session is { } session
            && _filter is { IsShowing: true, IsFrozen: false }
            && e.Text is [char typed]
            && _filter.Items[_filter.Selected].Item.CommitCharacters.Contains(typed)
            && _filter.CanCommit(_filter.Selected, session.Answer)
        )
        {
            Commit(_filter.Selected);
        }
    }

    /// <summary>Ends "No suggestions." of an empty answer, as any key does.</summary>
    public void HideNoSuggestions() => _view.HideNoSuggestions();

    /// <summary>
    /// Ends completion: closes the list, cancels the server's requests still under way, and drops what they would show.
    /// </summary>
    /// <returns>Whether the list or a message showed.</returns>
    public bool Close()
    {
        bool showedEmpty = _view.HideNoSuggestions();
        if (_session is not { } session)
            return showedEmpty;
        bool showed = _filter.IsShowing || _filter.ShowsNoMatch;
        _session = null;
        session.Pending?.Cancellation.Cancel();
        _filter.Hide();
        _resolver.Clear();
        return showed;
    }

    public void Dispose()
    {
        Close();
        _commit.Dispose();
        _quickSuggest.Dispose();
        _view.Clicked -= OnClicked;
        _buffer.Changing -= OnBufferChanging;
        _buffer.TextChanged -= OnBufferTextChanged;
        _textArea.TextEntered -= OnTextEntered;
        _textArea.Caret.PositionChanged -= OnCaretPositionChanged;
        _textArea.LostFocus -= OnClosingChange;
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
        _view.HideNoSuggestions();
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
                    _view.ShowNoSuggestions(request.Word.Start);
                    return;
                }
                if (!session.ByKey)
                {
                    // An accept that waits for the text and caret that this answer is for does its key's normal
                    // action: there is nothing to commit from, as when the list that showed matched nothing. Some
                    // servers answer nothing, incomplete, in strings and comments: asking again on each key there
                    // would never end.
                    KeyGesture? pressed = _filter.TakeWaitingKey();
                    Close();
                    _quickSuggest.Quiet(request);
                    if (pressed is { } key)
                        _performKey(key);
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
            _view.HideNoSuggestions();
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
            _filter.Hide();
            Ask(new CompletionTrigger(CompletionTriggerKind.Invoked));
            return;
        }
        if (session.Answer is not { } answer)
            return;
        if (answer.List.IsIncomplete && caret > latest.Offset && WordBefore(caret).Length > 0)
            Ask(new CompletionTrigger(CompletionTriggerKind.TriggerForIncompleteCompletions));
        _filter.Run(answer);
    }

    /// <summary>
    /// Commits the item at <paramref name="index"/> of the list, as it was resolved, with
    /// <see cref="CompletionCommit"/>: completion ends first. The resolve of an item whose additional edits are still to
    /// come is taken out of the resolver, so that ending completion does not cancel it.
    /// </summary>
    private void Commit(int index, bool replace = false)
    {
        if (_filter.ShownAnswer is not { } answer)
            return;
        CompletionItem selected = _filter.Items[index].Item;
        CompletionItem item = _resolver.ResolvedOf(selected) ?? selected;
        ItemResolve? late =
            item.AdditionalEdits is null && answer.List.Resolves ? _resolver.Take(selected, answer.List) : null;
        Close();
        _commit.Commit(item, answer, replace, late);
    }

    /// <summary>
    /// A click on a row commits its item where a commit may: also on a frozen list while the filter of what was typed
    /// last still runs, as the frozen list's rows show what the click commits.
    /// </summary>
    private void OnClicked(int index)
    {
        if (
            _session is { } session
            && _filter.IsShowing
            && index >= 0
            && index < _filter.Items.Count
            && (_filter.IsFrozen || _filter.CanCommit(index, session.Answer))
        )
        {
            Commit(index);
        }
    }

    bool ICompletionOwner.ByKey => _session?.ByKey == true;

    bool ICompletionOwner.Asking => _session?.Pending is not null;

    void ICompletionOwner.End() => Close();

    void ICompletionOwner.Commit(int index, bool replace) => Commit(index, replace);

    void ICompletionOwner.PerformKey(KeyGesture key) => _performKey(key);

    /// <summary>Opens completion on the server's trigger characters, and where typing opens it.</summary>
    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: > 0 } text || _document.Features?.Completion is not { } provider)
            return;
        char typed = text[^1];
        if (provider.TriggerCharacters.Contains(typed))
            Ask(new CompletionTrigger(CompletionTriggerKind.TriggerCharacter, typed));
        else if (_session is null && _quickSuggest.Opens(typed))
            Ask(new CompletionTrigger(CompletionTriggerKind.Invoked));
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

    private TextRange WordBefore(int caret) => CompletionRequest.WordBefore(_buffer, caret);
}

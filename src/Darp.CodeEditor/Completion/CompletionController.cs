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
/// outside a comment. Until the server answers, nothing shows; what is typed meanwhile filters its answer, and so does
/// what is typed while the list shows (<see cref="CompletionFilter"/>). A list that the server marks incomplete is
/// asked for again as the user types on, and shows meanwhile. Moving back before where completion was asked, and any
/// edit before it, as by another editor of the document, asks again while a word is left, as VS Code does when the
/// caret moves back: the list's positions no longer fit the text. Asked for by key, an empty answer says "No
/// suggestions." until the next key or caret move.
/// </para>
/// <para>
/// Up, Down, Page Up and Page Down select among the rows that show, while there is more than one item; with one, they
/// go on to the text, as in VS Code. The keymap's accept commands, a click or one of the selected item's commit
/// characters commit it (<see cref="CompletionCommit"/>): accepting inserts or replaces, a click and a commit character
/// insert, and a commit character then goes in. A frozen list commits by accepting and by a click, but not by commit
/// characters. A read-only editor neither completes nor commits.
/// </para>
/// <para>
/// <see cref="Close"/> ends completion, for every reason it ends: Escape, the caret leaving the line or moving back
/// past the word, a selection, the editor becoming read-only, the list scrolling out of view, the editor losing the
/// keyboard focus, a server that starts or stops serving the document, new colors, the list running out of matches, and
/// the editor no longer showing the document. Ending it hides "No suggestions." too, and cancels the request still
/// under way with <c>$/cancelRequest</c>, and so does a newer request; a late answer is dropped.
/// </para>
/// </remarks>
internal sealed class CompletionController : IEditorFeature, ICompletionOwner
{
    private readonly TextArea _textArea;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly CompletionView _view;

    /// <summary>Completion from its first request until it ends; null while there is none.</summary>
    private CompletionSession? _session;

    /// <summary>Whether "No suggestions." shows for an empty answer to a request by key, with no completion open.</summary>
    private bool _showsEmpty;

    private readonly CompletionCommit _commit;
    private readonly CompletionResolver _resolver;
    private readonly CompletionFilter _filter;

    public CompletionController(
        TemplatedControl editor,
        TextArea textArea,
        CodeDocument document,
        BackgroundTextMateHighlighter highlighter,
        SnippetController snippets,
        TaskScheduler background
    )
    {
        _textArea = textArea;
        _buffer = textArea.Document;
        _document = document;
        _highlighter = highlighter;
        _view = new CompletionView(editor, textArea.TextView);
        _commit = new CompletionCommit(textArea, document, snippets);
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
            if (value == _view.Colors)
                return;
            _view.Colors = value;
            Close();
        }
    }

    /// <summary>The colors of the icons, once the color theme is loaded. New colors end completion.</summary>
    public SymbolIconColors? Icons
    {
        set
        {
            if (value == _view.Icons)
                return;
            _view.Icons = value;
            Close();
        }
    }

    public void AddCommands(CommandTable commands)
    {
        const CommandNeeds needs = CommandNeeds.TextFocus | CommandNeeds.Writable;
        commands.Add(CodeEditorCommands.TriggerSuggest, Trigger, needs);
        commands.Add(CodeEditorCommands.ToggleSuggestionDetails, ToggleDetails);
        commands.Add(CodeEditorCommands.AcceptSelectedSuggestion, goOn => Accept(replace: false, goOn), needs);
        commands.Add(
            CodeEditorCommands.AcceptAlternativeSelectedSuggestion,
            goOn => Accept(replace: true, goOn),
            needs
        );
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
    /// <param name="goOn">
    /// What the key that accepts goes on to, as to its next binding or a new line, when the filter it waits for finds
    /// nothing; null when no key accepts.
    /// </param>
    /// <returns>Whether the list showed: otherwise the key goes on, as to a new line or an indent.</returns>
    private bool Accept(bool replace, Action? goOn)
    {
        if (_session is not { } session || !_filter.IsShowing)
            return false;
        if (_filter.CanCommit(_filter.Selected, session.Answer))
            Commit(_filter.Selected, replace);
        else if (session.Answer is { } answer)
            _filter.CommitWhenFiltered(answer, replace, goOn);
        return true;
    }

    /// <summary>Shows or hides the selected item's details while the list shows.</summary>
    /// <returns>Whether the list showed.</returns>
    private bool ToggleDetails()
    {
        if (_session is null || !_filter.IsShowing)
            return false;
        _resolver.ToggleDetails();
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
        if (_session is null || !_filter.IsShowing || _filter.Items.Count < 2 || e.KeyModifiers != KeyModifiers.None)
            return false;
        int count = _filter.Items.Count;
        int selected = _filter.Selected;
        CompletionListBox list = _view.List;
        switch (e.Key)
        {
            case Key.Down:
                _filter.Select((selected + 1) % count);
                return true;
            case Key.Up:
                _filter.Select((selected + count - 1) % count);
                return true;
            case Key.PageDown:
                _filter.Select(CompletionPaging.Down(selected, list.FirstRow, list.VisibleRows, count));
                return true;
            case Key.PageUp:
                _filter.Select(CompletionPaging.Up(selected, list.FirstRow, list.VisibleRows));
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

    /// <summary>
    /// Ends completion: closes the list, cancels the server's requests still under way, and drops what they would show.
    /// </summary>
    /// <returns>Whether the list or a message showed.</returns>
    public bool Close()
    {
        bool showedEmpty = HideEmpty();
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
                    _view.ShowNoSuggestions(request.Word.Start);
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
    /// Commits the item at <paramref name="index"/> of the list, as it was resolved, with
    /// <see cref="CompletionCommit"/>: completion ends first. The resolve of an item whose additional edits are still to
    /// come is taken out of the resolver, so that ending completion does not cancel it.
    /// </summary>
    private void Commit(int index, bool replace = false)
    {
        if (_filter.ShownAnswer is not { } answer)
            return;
        if (!_textArea.ReadOnlySectionProvider.CanInsert(_textArea.Caret.Offset))
        {
            Close();
            return;
        }
        CompletionItem selected = _filter.Items[index].Item;
        CompletionItem item = _resolver.ResolvedOf(selected) ?? selected;
        ItemResolve? late =
            item.AdditionalEdits is null && answer.List.Resolves ? _resolver.Take(selected, answer.List) : null;
        Close();
        _commit.Commit(item, answer.Request, replace, late);
    }

    private void OnClicked(int index)
    {
        if (
            _session is { } session
            && _filter.IsShowing
            && index >= 0
            && index < _filter.Items.Count
            && _filter.CanCommit(index, session.Answer)
        )
        {
            Commit(index);
        }
    }

    bool ICompletionOwner.ByKey => _session?.ByKey == true;

    bool ICompletionOwner.Asking => _session?.Pending is not null;

    void ICompletionOwner.End() => Close();

    void ICompletionOwner.Commit(int index, bool replace) => Commit(index, replace);

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
}

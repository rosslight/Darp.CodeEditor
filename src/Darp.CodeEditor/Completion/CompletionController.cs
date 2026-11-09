using Avalonia.Input;
using Avalonia.Logging;
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
/// Completion opens on Ctrl+Space, also on macOS, on the server's trigger characters, and when typing starts a word
/// outside a comment. Until the server answers, nothing shows; what is typed meanwhile filters its answer. The list
/// is the server's for where it was asked: typing on filters and ranks it on the client, with the matching of
/// <see cref="CompletionMatcher"/>, and selects the first item, or the one the server preselects among the best. A list
/// that the server marks incomplete is asked for again as the user types on, and shows meanwhile. Moving back before
/// where completion was asked asks again while a word is left, as VS Code does.
/// </para>
/// <para>
/// Up, Down, Page Up and Page Down select, and Enter, Tab, a click or one of the selected item's commit characters
/// commit it: its edit, or its text over the word, and its additional edits, such as an import, in one step that the
/// user undoes. The server resolves the selected item, so that its detail and documentation show beside the list. An
/// item whose additional edits are still to come when it is committed gets them when they come, in its step, if that
/// step is still the last one: otherwise they are dropped, as the user moved on.
/// </para>
/// <para>
/// <see cref="Close"/> ends completion, for every reason it ends: Escape, the caret leaving the line or moving back past
/// the word, a selection, the list scrolling out of view, the editor losing the keyboard focus, a server that starts or
/// stops serving the document, new colors, the list running out of matches, and the editor no longer showing the
/// document. Ending it cancels the request still under way with <c>$/cancelRequest</c>, and so does a newer request; a
/// late answer is dropped.
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
    private readonly CompletionMatcher _matcher = new();
    private readonly CompletionView _view = new();
    private readonly EditorPopup _popup;
    private TextAreaColors? _colors;
    private SymbolIconColors? _icons;

    /// <summary>Completion from its first request until it ends; null while there is none.</summary>
    private Session? _session;

    /// <summary>Cancels the resolve of the item committed last, whose additional edits are still to come.</summary>
    private CancellationTokenSource? _lateEdits;

    public CompletionController(
        CodeEditor editor,
        TextArea textArea,
        CodeDocument document,
        BackgroundTextMateHighlighter highlighter
    )
    {
        _editor = editor;
        _textArea = textArea;
        _textView = textArea.TextView;
        _buffer = textArea.Document;
        _document = document;
        _highlighter = highlighter;
        _popup = new EditorPopup(editor, _view.Root, below: true);
        _view.Clicked += OnClicked;
        _textArea.TextEntered += OnTextEntered;
        _textArea.Caret.PositionChanged += OnCaretPositionChanged;
        _document.LanguageFeaturesChanged += OnClosingChange;
        _popup.LeftView += OnClosingChange;
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
    /// The keys of completion, from the editor's key handler while the text area has the keyboard: Ctrl+Space, and
    /// while the list shows, the keys that select and commit.
    /// </summary>
    /// <returns>Whether the key was completion's.</returns>
    public bool OnKeyDown(KeyEventArgs e)
    {
        // Also on macOS, where Cmd takes Ctrl's place for other keys: Cmd+Space is the system's search.
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            Ask(new CompletionTrigger(CompletionTriggerKind.Invoked));
            return true;
        }
        if (_session is not { IsShowing: true } session || e.KeyModifiers != KeyModifiers.None)
            return false;
        int count = session.Items.Count;
        int page = CompletionView.VisibleItems - 1;
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
            case Key.Enter or Key.Tab:
                Commit(session, session.Selected);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Text the user types, before it goes in: one of the selected item's commit characters commits it, and then goes in
    /// after it.
    /// </summary>
    public void OnTextInput(TextInputEventArgs e)
    {
        if (
            _session is { IsShowing: true } session
            && e.Text is [char typed]
            && session.Items[session.Selected].Item.CommitCharacters.Contains(typed)
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
        if (_session is not { } session)
            return false;
        _session = null;
        session.Pending?.Cancellation.Cancel();
        foreach (Resolve resolve in session.Resolves.Values)
            resolve.Cancellation.Cancel();
        session.Details?.Cancel();
        _popup.Hide();
        _view.ShowDetails(null);
        return session.IsShowing;
    }

    public void Dispose()
    {
        Close();
        _lateEdits?.Cancel();
        _view.Clicked -= OnClicked;
        _textArea.TextEntered -= OnTextEntered;
        _textArea.Caret.PositionChanged -= OnCaretPositionChanged;
        _document.LanguageFeaturesChanged -= OnClosingChange;
        _popup.LeftView -= OnClosingChange;
    }

    /// <summary>
    /// Asks the server for completion at the caret. While completion is open, the request replaces the one under way,
    /// and the items that show stay until it answers.
    /// </summary>
    private void Ask(CompletionTrigger trigger)
    {
        int caret = _textArea.Caret.Offset;
        if (!_textArea.ReadOnlySectionProvider.CanInsert(caret))
            return;
        TextLocation location = _buffer.GetLocation(caret);
        var request = new Request(
            _document.CurrentSnapshot,
            WordBefore(caret),
            location.Line,
            location.Column,
            new CancellationTokenSource()
        );
        Session session = _session ??= new Session();
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
            if (list is null || (list.Items.Count == 0 && !list.IsIncomplete))
            {
                Close();
                return;
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
            return;
        int caret = _textArea.Caret.Offset;
        TextLocation at = _buffer.GetLocation(caret);
        Request latest = session.Latest;
        if (at.Line != latest.Line || !_textArea.Selection.IsEmpty)
        {
            Close();
            return;
        }
        if (at.Column < latest.Column)
        {
            // Back before where completion was asked: the list is for more text than there is.
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
        if (answer.List.IsIncomplete && at.Column > latest.Column && WordBefore(caret).Length > 0)
            Ask(new CompletionTrigger(CompletionTriggerKind.TriggerForIncompleteCompletions));
        Filter(session, answer, caret);
    }

    /// <summary>
    /// Shows the items of <paramref name="answer"/> that match the text typed for each, from where its edit starts up
    /// to the caret, best first.
    /// </summary>
    private void Filter(Session session, Answer answer, int caret)
    {
        Request request = answer.Request;
        DocumentLine line = _buffer.GetLineByOffset(caret);
        // Where completion was asked, in the current text: the text before it on its line has not changed.
        int asked = line.Offset + request.Column - 1;
        if (caret < asked)
        {
            // Before the answer that shows was asked for: the newer request's answer shows.
            Hide(session);
            return;
        }
        IReadOnlyList<CompletionItem> items = answer.List.Items;
        var typed = new Dictionary<int, string>();
        var candidates = new CompletionCandidate[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            CompletionItem item = items[i];
            int start = asked - (request.Offset - item.Edit.Range.Start);
            if (!typed.TryGetValue(start, out string? text))
                typed[start] = text = _buffer.GetText(start, caret - start);
            candidates[i] = new CompletionCandidate(text, item.Label, item.FilterText);
        }
        List<RankedCompletion> ranked = _matcher.Rank(candidates);
        if (ranked.Count == 0)
        {
            // An incomplete list may have more for what is typed: its next answer shows.
            if (answer.List.IsIncomplete)
                Hide(session);
            else
                Close();
            return;
        }
        session.Items = [.. ranked.Select(r => new ShownItem(items[r.Index], r.Highlights))];
        // As VS Code: the first item, or the first that the server preselects among those that match best.
        int preselected = ranked.FindIndex(r => r.Score == ranked[0].Score && items[r.Index].IsPreselected);
        session.Selected = preselected > 0 ? preselected : 0;
        int wordStart = asked - (request.Offset - request.Word.Start);
        session.Place = () => TextViewBoxes.CharacterAt(_textView, Math.Min(wordStart, _buffer.TextLength));
        session.IsShowing = true;
        _view.Show(session.Items, session.Selected, Look(), _icons, Math.Ceiling(_textView.DefaultLineHeight) + 4);
        _popup.Show(_textView, session.Place);
        ResolveSelected(session);
    }

    private void Select(Session session, int index)
    {
        session.Selected = index;
        _view.Select(index);
        ResolveSelected(session);
    }

    /// <summary>Hides the list while completion waits for an answer.</summary>
    private void Hide(Session session)
    {
        session.IsShowing = false;
        session.Items = [];
        session.Details?.Cancel();
        _popup.Hide();
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
        ShowDetails(session, session.ResolvedOf(item) ?? item);
        if (session.Answer is not { List.Resolves: true } answer || session.Resolves.ContainsKey(item))
            return;
        foreach (
            (CompletionItem other, Resolve running) in session.Resolves.Where(r => !r.Value.Task.IsCompleted).ToList()
        )
        {
            running.Cancellation.Cancel();
            session.Resolves.Remove(other);
        }
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
    /// as the hover reads its Markdown; nothing when it has neither.
    /// </summary>
    private async void ShowDetails(Session session, CompletionItem item)
    {
        session.Details?.Cancel();
        session.Details = null;
        if (item.Detail is null && item.Documentation is null)
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
            if (_session != session || cancellation.IsCancellationRequested || session.Place is not { } place)
                return;
            _view.ShowDetails(HoverView.Create(blocks, Look()));
            // The popup is wider now: placed again, so that it stays inside the window.
            _popup.Show(_textView, place);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e)
        {
            Logger
                .TryGet(LogEventLevel.Error, AssetFiles.LogArea)
                ?.Log(this, "Showing an item's details failed: {Exception}", e);
        }
    }

    private static IReadOnlyList<HoverBlock> DetailBlocks(
        CompletionItem item,
        TextMateGrammar? grammar,
        CancellationToken cancellationToken
    )
    {
        var blocks = new List<HoverBlock>();
        if (item.Detail is { } detail)
            blocks.Add(HoverBlocks.Highlight(detail.Split('\n'), grammar, cancellationToken));
        if (item.Documentation is { } documentation)
        {
            blocks.AddRange(
                HoverBlocks.Of(new HoverInfo([documentation], null), grammar, new HoverBudget(), cancellationToken)
            );
        }
        return blocks;
    }

    /// <summary>
    /// Commits an item: its edit, from where it starts to as far past the caret as it reached past where completion was
    /// asked, and its additional edits, in one step that the user undoes; the caret goes after the item's text.
    /// Additional edits that overlap others are left out. Additional edits that a resolve still under way brings come
    /// later.
    /// </summary>
    private void Commit(Session session, int index)
    {
        if (session.Answer is not { } answer)
            return;
        CompletionItem selected = session.Items[index].Item;
        CompletionItem item = session.ResolvedOf(selected) ?? selected;
        Request request = answer.Request;
        int caret = _textArea.Caret.Offset;
        DocumentLine line = _buffer.GetLineByOffset(caret);
        int asked = line.Offset + request.Column - 1;
        var main = new CompletionEdit(
            TextRange.FromBounds(
                asked - (request.Offset - item.Edit.Range.Start),
                Math.Min(line.EndOffset, caret + (item.Edit.Range.End - request.Offset))
            ),
            item.Edit.NewText
        );
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
        List<CompletionEdit> edits = WithoutOverlaps([main], Mapped(request.Snapshot, item.AdditionalEdits ?? []));
        Apply(step, continued: false, edits);
        // The item's text moved by the additional edits before it.
        int shift = edits
            .Skip(1)
            .Where(e => e.Range.End <= main.Range.Start)
            .Sum(e => e.NewText.Length - e.Range.Length);
        _textArea.ClearSelection();
        _textArea.Caret.Offset = main.Range.Start + shift + main.NewText.Length;
        _textArea.Caret.BringCaretToView();
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
            )
            {
                return;
            }
            Apply(step, continued: true, WithoutOverlaps([], Mapped(snapshot, additional)));
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
    /// continued.
    /// </summary>
    private void Apply(object step, bool continued, List<CompletionEdit> edits)
    {
        UndoStack undo = _buffer.UndoStack;
        if (continued)
            undo.StartContinuedUndoGroup(step);
        else
            undo.StartUndoGroup(step);
        try
        {
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
        if (_session is { IsShowing: true } session && index >= 0 && index < session.Items.Count)
            Commit(session, index);
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

    private void OnClosingChange(object? sender, EventArgs e) => Close();

    /// <summary>The word before <paramref name="caret"/>, by VS Code's word characters, which ends at the caret.</summary>
    private TextRange WordBefore(int caret)
    {
        DocumentLine line = _buffer.GetLineByOffset(caret);
        int start = caret;
        while (start > line.Offset && WordCharacters.IsWordCharacter(_buffer.GetCharAt(start - 1)))
            start--;
        return TextRange.FromBounds(start, caret);
    }

    private CodeLook Look() => new(_editor.FontFamily, _editor.FontSize, _colors?.Background, _colors?.Foreground);

    /// <summary>A request for completion.</summary>
    /// <param name="Snapshot">The text it was asked for.</param>
    /// <param name="Word">The word before the caret then: what items without an edit replace.</param>
    /// <param name="Line">The caret's line then.</param>
    /// <param name="Column">The caret's column then.</param>
    /// <param name="Cancellation">Cancels the request.</param>
    private sealed record Request(
        TextSnapshot Snapshot,
        TextRange Word,
        int Line,
        int Column,
        CancellationTokenSource Cancellation
    )
    {
        /// <summary>The caret's offset in <see cref="Snapshot"/>.</summary>
        public int Offset => Word.End;
    }

    /// <summary>A server's list, with the request it answered.</summary>
    private sealed record Answer(CompletionList List, Request Request);

    /// <summary>An item's resolve, under way or done.</summary>
    private sealed record Resolve(Task<CompletionItem?> Task, CancellationTokenSource Cancellation);

    /// <summary>Completion from its first request until it ends.</summary>
    private sealed class Session
    {
        /// <summary>The answer whose items show; null until the first one.</summary>
        public Answer? Answer { get; set; }

        /// <summary>The request under way; null for none.</summary>
        public Request? Pending { get; set; }

        /// <summary>The request asked last: the one under way, or the one whose answer shows.</summary>
        public Request Latest =>
            Pending ?? Answer?.Request ?? throw new InvalidOperationException("Completion was not asked for.");

        /// <summary>Whether the list shows.</summary>
        public bool IsShowing { get; set; }

        /// <summary>The items that show, in order.</summary>
        public List<ShownItem> Items { get; set; } = [];

        /// <summary>The selected item's index in <see cref="Items"/>.</summary>
        public int Selected { get; set; }

        /// <summary>Where the list is: the box of the first character of the word it completes.</summary>
        public Func<Avalonia.Rect>? Place { get; set; }

        /// <summary>The resolves of the items, under way or done.</summary>
        public Dictionary<CompletionItem, Resolve> Resolves { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Cancels the reading of the details that are about to show.</summary>
        public CancellationTokenSource? Details { get; set; }

        /// <summary>The item as the server resolved it; null while it did not.</summary>
        public CompletionItem? ResolvedOf(CompletionItem item) =>
            Resolves.TryGetValue(item, out Resolve? resolve) && resolve.Task.IsCompletedSuccessfully
                ? resolve.Task.Result
                : null;
    }
}

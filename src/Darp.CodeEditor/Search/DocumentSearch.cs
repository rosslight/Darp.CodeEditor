using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using EditorSelection = AvaloniaEdit.Editing.Selection;

namespace Darp.CodeEditor.Search;

/// <summary>
/// The search of the document that one editor shows, for the editor's bar, as VS Code's find model
/// (<c>findModel.ts</c>): the matches of the bar's query, found in the background and highlighted, going from one to
/// the next, also beyond those highlighted, and replacing them; only in the lines of Find in Selection while it is on.
/// It starts from VS Code's start position, the caret as the bar opened or as the user last moved it, and goes round
/// past the last match as its <c>editor.find.loop</c> does by default.
/// </summary>
internal sealed class DocumentSearch : IDisposable
{
    private readonly EditorContext _context;
    private readonly TextDocument _buffer;
    private readonly TextChanges _changes;
    private readonly TimeProvider _clock;

    private readonly SearchBar _bar;
    private readonly SearchMatchLayer _matchLayer;
    private readonly SearchScopeLayer _scopeLayer;
    private readonly LatestJob<SearchResult> _search;
    private readonly LatestJob<(SearchMatch? Match, SearchOutcome Outcome)> _seek;
    private readonly LatestJob<(CombinedEdit? Edit, SearchOutcome Outcome)> _replaceAll;
    private SearchResult? _result;

    /// <summary>
    /// The ranges that Find in Selection searches, from anchor to anchor, which grow with text typed at either edge, as
    /// VS Code's do; null while it is off. It stays on until the bar closes.
    /// </summary>
    private List<(TextAnchor Start, TextAnchor End)>? _scope;

    /// <summary>
    /// Where to move once the search under way has its result. A newer search that takes over keeps it: it replaces
    /// the search, not what the search was for.
    /// </summary>
    private SearchMove _move;

    /// <summary>
    /// Where a search moves from as the query, its options or the scope change: the caret as the bar opened, as the
    /// user last moved it, or after the text last changed; not where a match was selected.
    /// </summary>
    private int _from;

    /// <summary>Whether a match is being selected, which leaves <see cref="_from"/> where it is.</summary>
    private bool _selecting;

    /// <summary>Whether the view ended, as a handler of the selection can end it by showing another document.</summary>
    private bool _ended;

    public DocumentSearch(EditorContext context, TextChanges changes, SearchBar bar, TextAreaColors? colors)
    {
        _context = context;
        _buffer = context.Area.Document;
        _changes = changes;
        _clock = context.Document.TimeProvider;
        _bar = bar;
        _matchLayer = new SearchMatchLayer(Area) { Colors = colors };
        _scopeLayer = new SearchScopeLayer(Area.TextView) { Colors = colors };
        Dispatcher dispatcher = Area.Dispatcher;
        _search = new(dispatcher, "The search");
        _seek = new(dispatcher, "The search for the next match");
        _replaceAll = new(dispatcher, "Replace All");
        _buffer.Changed += OnBufferChanged;
        _buffer.TextChanged += OnTextChanged;
        Area.SelectionChanged += OnSelectionChanged;
        Area.Caret.PositionChanged += OnCaretMoved;
        bar.Search = this;
        StartFromCaret();
        Search(SearchMove.None);
    }

    /// <summary>
    /// The latest work in the background: a search, a search for a match beyond those kept, or Replace All's. It
    /// completes once its result was taken, or dropped, on the UI thread.
    /// </summary>
    public Task LatestSearch { get; private set; } = Task.CompletedTask;

    /// <summary>The colors of the color theme, once it is loaded.</summary>
    public TextAreaColors? Colors
    {
        set
        {
            _matchLayer.Colors = value;
            _scopeLayer.Colors = value;
        }
    }

    /// <summary>Whether Find in Selection is on.</summary>
    public bool IsInSelection => _scope is not null;

    private TextArea Area => _context.Area;

    /// <summary>Starts from the caret, as the bar opens.</summary>
    public void StartFromCaret() => _from = Area.Caret.Offset;

    /// <summary>The user changed the query or clicked a toggle: the search moves to its first match.</summary>
    public void OnQueryChanged() => Search(SearchMove.First);

    /// <summary>The user changed the replacement or Preserve Case: the matches get theirs.</summary>
    public void OnReplacementChanged() => Search(SearchMove.None);

    /// <summary>
    /// Ends the search, and Find in Selection, as the bar closes; the keyboard goes back to the text.
    /// </summary>
    public void OnClosed()
    {
        SetScope(null);
        Search(SearchMove.None);
        Area.Focus();
    }

    /// <summary>
    /// Turns Find in Selection on for the selected lines, as Alt+L and the bar's toggle do, or off, and selects the
    /// first match from where the search started. Without a selection it stays off.
    /// </summary>
    public void FindInSelection(bool on)
    {
        List<TextRange>? scope = on ? FindScope.OfSelection(_buffer, Area.Selection.Segments) : null;
        if (scope is null && _scope is null)
            _bar.FindInSelection = false;
        else
        {
            SetScope(scope);
            Search(SearchMove.First);
        }
    }

    /// <summary>
    /// Searches only the selected lines, if any, as the automatic Find in Selection does as the bar opens, without
    /// searching yet.
    /// </summary>
    /// <returns>Whether something is selected, and the scope changed.</returns>
    public bool ScopeToSelection()
    {
        if (FindScope.OfSelection(_buffer, Area.Selection.Segments) is not { } scope)
            return false;
        SetScope(scope);
        return true;
    }

    /// <summary>
    /// Starts a search of the current text in the background, in place of the one under way, if the bar is open and
    /// has a query; it moves the selection as <paramref name="move"/> says once its result is in, or as the search it
    /// replaces was to.
    /// </summary>
    public void Search(SearchMove move)
    {
        CancelWork();
        if (move != SearchMove.None)
            _move = move;
        _scopeLayer.Scope = _bar.IsOpen ? Scope() : null;
        if (!_bar.IsOpen || _bar.Query is not { } query)
        {
            _move = SearchMove.None;
            Show(null);
            UpdateStatus();
            return;
        }
        SearchReplacement? replacement = _bar.Replacement;
        List<TextRange>? scope = Scope();
        TextSnapshot snapshot = _context.Document.CurrentSnapshot;
        LatestSearch = _search.Start(
            cancellation => TextSearch.Find(snapshot, query, replacement, scope, cancellation, _clock),
            OnSearched
        );
        UpdateStatus();
    }

    /// <summary>
    /// Selects a match as <paramref name="move"/> says, once the search under way has its result: past the last match,
    /// round to the first, and before the first, round to the last. Where the match may be beyond those that the result
    /// keeps, it searches the text for it in the background: every match stays within reach.
    /// </summary>
    public void Go(SearchMove move)
    {
        if (move == SearchMove.None)
            return;
        if (_search.IsRunning || _result is not { } result)
        {
            _move = move;
            return;
        }
        if (result.Matches is not { FirstSegment: { } first, LastSegment: { } last } matches)
            return;
        TextRange selection = _changes.Selection.Range;
        bool capped = result.Outcome == SearchOutcome.TooManyMatches;
        SearchQuery query = result.Query;
        if (move == SearchMove.Previous)
        {
            SearchMatch? after = matches.FindFirstSegmentWithStartAfter(selection.Start);
            if (after is not null && matches.GetPreviousSegment(after) is { } previous)
                Select(previous);
            else if (!capped)
                Select(last);
            else
            {
                // The last match before the selection, which may be beyond the last kept, or the last of all.
                int before = after is null ? selection.Start : int.MaxValue;
                int from = last.StartOffset;
                Seek(
                    result,
                    move,
                    (text, scope, c) => TextSearch.FindPrevious(text, query, scope, from, before, c, _clock)
                );
            }
            return;
        }
        // The first match from where the search started, or after the selection; an empty match at the caret is the
        // current one.
        int start = move == SearchMove.First ? _from : selection.End;
        bool skipEmpty = move == SearchMove.Next && selection.Length == 0;
        SearchMatch? next = matches.FindFirstSegmentWithStartAfter(start);
        if (skipEmpty && next is { Length: 0 } && next.StartOffset == start)
            next = matches.GetNextSegment(next);
        if (next is not null)
            Select(next);
        else if (capped)
            Seek(
                result,
                move,
                (text, scope, c) => TextSearch.FindNext(text, query, null, scope, start, skipEmpty, c, _clock)
            );
        else
            Select(first);
    }

    /// <summary>
    /// Replaces the selected match, as VS Code does: the next match from the selection's start, beyond the matches
    /// that the result keeps found again in the background, is replaced where it is the selection, and selected
    /// otherwise; past the last match, the first is selected. The search that the change starts goes on from the end of
    /// the replacement.
    /// </summary>
    public void ReplaceOne()
    {
        if (CurrentResult() is not { Replacement: { } replacement } result)
            return;
        TextRange selection = _changes.Selection.Range;
        if (result.Matches.FindFirstSegmentWithStartAfter(selection.Start) is { } next)
            ReplaceOrSelect(next);
        else if (result.Outcome == SearchOutcome.TooManyMatches)
            Seek(
                result,
                SearchMove.Next,
                (text, scope, c) =>
                    TextSearch.FindNext(text, result.Query, replacement, scope, selection.Start, false, c, _clock),
                ReplaceOrSelect
            );
        else if (result.Matches.FirstSegment is { } first)
            Select(first);

        void ReplaceOrSelect(SearchMatch match)
        {
            if (match.StartOffset == selection.Start && match.Length == selection.Length)
                Replace(match);
            else
                Select(match);
        }
    }

    /// <summary>
    /// Replaces every match in one change, one undo step: all of them, also beyond the matches that the result keeps,
    /// found again and combined into that change in the background, and replaced unless something changed meanwhile.
    /// Never only some: a search that ran out of time replaces nothing.
    /// </summary>
    public void ReplaceAll()
    {
        if (
            CurrentResult()
            is not {
                Outcome: SearchOutcome.Complete or SearchOutcome.TooManyMatches,
                Replacement: { } replacement
            } result
        )
            return;
        TextSnapshot snapshot = _context.Document.CurrentSnapshot;
        List<TextRange>? scope = Scope();
        LatestSearch = _replaceAll.Start(
            cancellation => TextSearch.ReplaceAll(snapshot, result.Query, replacement, scope, cancellation, _clock),
            replaced =>
            {
                // A change of the text, the query, the replacement or the scope searches again and cancels this.
                if (CurrentResult() != result)
                    return;
                if (replaced.Edit is { } edit)
                    _changes.Run(EditStep.Own, () => _changes.Apply(edit));
                else if (replaced.Outcome == SearchOutcome.OutOfTime)
                    _bar.ShowTimedOut();
            }
        );
    }

    /// <summary>Shows in the bar the matches, the one selected, and what its buttons can do.</summary>
    public void UpdateStatus()
    {
        if (_ended)
            return;
        int? index = null;
        if (_result is { } result && CurrentMatch(result) is { } current)
        {
            index = 0;
            for (
                SearchMatch? match = result.Matches.FirstSegment;
                match != current;
                match = result.Matches.GetNextSegment(match)
            )
                index++;
        }
        _bar.ShowResult(
            _result,
            index,
            canReplaceAll: CurrentResult()
                is { Outcome: SearchOutcome.Complete or SearchOutcome.TooManyMatches, Matches.Count: > 0 },
            canFindInSelection: _scope is not null || !Area.Selection.IsEmpty
        );
    }

    public void Dispose()
    {
        _ended = true;
        CancelWork();
        _buffer.Changed -= OnBufferChanged;
        _buffer.TextChanged -= OnTextChanged;
        Area.SelectionChanged -= OnSelectionChanged;
        Area.Caret.PositionChanged -= OnCaretMoved;
        _matchLayer.Dispose();
        _scopeLayer.Dispose();
        // Find in Selection ends with the document.
        _bar.FindInSelection = false;
        _bar.ShowResult(null, null, canReplaceAll: false, canFindInSelection: false);
        _bar.Search = null;
    }

    /// <summary>Searches only <paramref name="ranges"/> from now on, or everywhere for null.</summary>
    private void SetScope(List<TextRange>? ranges)
    {
        _scope = ranges
            ?.Select(range =>
                (
                    Anchor(range.Start, AnchorMovementType.BeforeInsertion),
                    Anchor(range.End, AnchorMovementType.AfterInsertion)
                )
            )
            .ToList();
        _bar.FindInSelection = _scope is not null;

        TextAnchor Anchor(int offset, AnchorMovementType movement)
        {
            TextAnchor anchor = _buffer.CreateAnchor(offset);
            anchor.MovementType = movement;
            anchor.SurviveDeletion = true;
            return anchor;
        }
    }

    /// <summary>The ranges that Find in Selection searches, as they are now; null while it is off.</summary>
    private List<TextRange>? Scope() =>
        _scope
            ?.Select(range => TextRange.FromBounds(range.Start.Offset, Math.Max(range.Start.Offset, range.End.Offset)))
            .ToList();

    /// <summary>Shows a search's result, and moves as the search was to.</summary>
    private void OnSearched(SearchResult result)
    {
        Show(result);
        SearchMove move = _move;
        _move = SearchMove.None;
        Go(move);
        UpdateStatus();
    }

    private void Show(SearchResult? result)
    {
        _result = result;
        _matchLayer.Matches = result?.Matches;
    }

    /// <summary>
    /// Selects the match that <paramref name="find"/> finds in the background, or gives it to <paramref name="found"/>,
    /// unless the result or the selection changed meanwhile; without one, going forward, the result's first match.
    /// </summary>
    private void Seek(
        SearchResult result,
        SearchMove move,
        Func<TextSnapshot, List<TextRange>?, CancellationToken, (SearchMatch? Match, SearchOutcome Outcome)> find,
        Action<SearchMatch>? found = null
    )
    {
        TextSnapshot snapshot = _context.Document.CurrentSnapshot;
        List<TextRange>? scope = Scope();
        TextRange selection = _changes.Selection.Range;
        LatestSearch = _seek.Start(
            cancellation => find(snapshot, scope, cancellation),
            sought =>
            {
                if (_result != result || _changes.Selection.Range != selection)
                    return;
                // Out of time, a match found is only the last one reached, as going back finds.
                if (sought.Outcome == SearchOutcome.OutOfTime)
                    _bar.ShowTimedOut();
                else if (sought.Match is { } match)
                    (found ?? Select)(match);
                else if (move != SearchMove.Previous && result.Matches.FirstSegment is { } first)
                    Select(first);
            }
        );
    }

    /// <summary>Replaces <paramref name="match"/>; the search that the change starts goes on from its end.</summary>
    private void Replace(SearchMatch match)
    {
        int end = match.StartOffset + match.Replacement.Length;
        if (!Apply([new TextEdit(match.StartOffset, match.Length, match.Replacement)]))
            return;
        _from = end;
        _move = SearchMove.First;
    }

    private void Select(SearchMatch match)
    {
        _selecting = true;
        Area.Selection = EditorSelection.Create(Area, match.StartOffset, match.EndOffset);
        if (!_ended)
            Area.Caret.Offset = match.EndOffset;
        _selecting = false;
        if (_ended)
            return;
        _context.Reveal(new TextRange(match.StartOffset, match.Length));
        // Its number, now that the selection is all set.
        UpdateStatus();
    }

    /// <summary>Makes <paramref name="edits"/> as one change of the text, in an undo step of its own.</summary>
    /// <returns>Whether they went in.</returns>
    private bool Apply(IReadOnlyList<TextEdit> edits)
    {
        bool applied = false;
        _changes.Run(EditStep.Own, () => applied = _changes.Apply(edits));
        return applied;
    }

    /// <summary>
    /// The result to replace with: one for the text as it is, the query and the replacement as they are, in an editor
    /// that may change the text. Null while a search is under way.
    /// </summary>
    private SearchResult? CurrentResult() =>
        _result is { Replacement: { } replacement } result
        && !_search.IsRunning
        && !Area.IsReadOnly
        && _bar.Replacement == replacement
        && result.Version == _context.Document.CurrentSnapshot.Version
            ? result
            : null;

    /// <summary>The match that is selected; null for none.</summary>
    private SearchMatch? CurrentMatch(SearchResult result)
    {
        TextRange selection = _changes.Selection.Range;
        return
            result.Matches.FindFirstSegmentWithStartAfter(selection.Start) is { } match
            && match.StartOffset == selection.Start
            && match.Length == selection.Length
            ? match
            : null;
    }

    private void CancelWork()
    {
        _search.Cancel();
        _seek.Cancel();
        _replaceAll.Cancel();
    }

    /// <summary>Keeps the matches on their text while the text changes.</summary>
    private void OnBufferChanged(object? sender, DocumentChangeEventArgs e) => _result?.Matches.UpdateOffsets(e);

    private void OnTextChanged(object? sender, EventArgs e)
    {
        StartFromCaret();
        if (_bar.IsOpen)
            Search(SearchMove.None);
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        OnCaretMoved(sender, e);
        if (_bar.IsOpen)
            UpdateStatus();
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        if (!_selecting)
            StartFromCaret();
    }
}

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;
using Darp.CodeEditor.Localization;

namespace Darp.CodeEditor.Search;

/// <summary>
/// The editor's search and replace bar, at the top right of the text: the app's text boxes, toggle buttons and
/// buttons, in the look of its flyouts and in its font, not the editor's.
/// </summary>
/// <remarks>
/// <para>
/// It searches as the user types, in the background, and again after each change of the text: the UI thread only
/// shows the matches, which follow the edits until the next result replaces them.
/// </para>
/// <para>
/// A search has a time limit and a limit on the matches it keeps (<see cref="TextSearch"/>), and the bar says so when
/// it hits one. Replace All acts only on a complete result for the current text, query and replacement: never on a
/// partial one.
/// </para>
/// </remarks>
internal sealed class SearchBar : UserControl
{
    private static readonly Geometry s_previous = Geometry.Parse("M8,2 L14,9 L10,9 L10,14 L6,14 L6,9 L2,9 Z");
    private static readonly Geometry s_next = Geometry.Parse("M8,14 L14,7 L10,7 L10,2 L6,2 L6,7 L2,7 Z");
    private static readonly Geometry s_close = Geometry.Parse(
        "M3,2 L8,7 L13,2 L14,3 L9,8 L14,13 L13,14 L8,9 L3,14 L2,13 L7,8 L2,3 Z"
    );

    /// <summary>Lines in a box, as VS Code's icon of Find in Selection.</summary>
    private static readonly Geometry s_selection = Geometry.Parse(
        "F0 M2,2 L14,2 L14,14 L2,14 Z M3,3 L3,13 L13,13 L13,3 Z M5,5 L11,5 L11,6 L5,6 Z M5,7.5 L11,7.5 L11,8.5 L5,8.5 Z "
            + "M5,10 L9,10 L9,11 L5,11 Z"
    );

    private readonly TextBox _find = QueryBox();
    private readonly ToggleButton _matchCase = new() { Content = "Aa" };
    private readonly ToggleButton _wholeWord = new() { Content = "ab" };
    private readonly ToggleButton _regex = new() { Content = ".*" };
    private readonly TextBlock _count = new() { MinWidth = 80, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _previous;
    private readonly Button _next;
    private readonly ToggleButton _findInSelection = new() { Content = Icon(s_selection) };
    private readonly Button _close;
    private readonly TextBox _replace = QueryBox();
    private readonly ToggleButton _preserveCase = new() { Content = "AB" };
    private readonly Button _replaceOne = new();
    private readonly Button _replaceAll = new();
    private readonly StackPanel _replaceRow;
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };

    /// <summary>Whether the bar shows the replace row, unless the editor is read-only.</summary>
    private bool _replaceMode;

    /// <summary>The editor and what it shows, while it shows a document.</summary>
    private Shown? _shown;

    private TextAreaColors? _colors;

    /// <summary>The result of the last search, for the text and the query of then.</summary>
    private SearchResult? _result;

    /// <summary>The search under way in the background; null for none.</summary>
    private CancellationTokenSource? _searching;

    /// <summary>
    /// The search for the match to select beyond those that the result keeps, under way in the background; null for
    /// none.
    /// </summary>
    private CancellationTokenSource? _seeking;

    /// <summary>
    /// Where to move once the search under way has its result. A newer search that takes over keeps it: it replaces
    /// the search, not what the search was for.
    /// </summary>
    private Move _move;

    /// <summary>
    /// Where a search moves from as the query, its options or the scope change, as VS Code's start position: the caret
    /// as the bar opened, as the user last moved it, or after the text last changed; not where the bar selected a match.
    /// </summary>
    private int _from;

    /// <summary>Whether the bar selects a match, which leaves <see cref="_from"/> where it is.</summary>
    private bool _selecting;

    /// <summary>The histories of the query and the replacement while the editor has none: the bar's own.</summary>
    private readonly SearchHistory _findHistory = new();

    private readonly SearchHistory _replaceHistory = new();

    /// <summary>Where Up and Down are in the query's history.</summary>
    private readonly SearchHistoryCursor _findCursor = new();

    /// <summary>Where Up and Down are in the replacement's history.</summary>
    private readonly SearchHistoryCursor _replaceCursor = new();

    /// <summary>The pause after which the query and the replacement go into their histories; null for none.</summary>
    private ITimer? _historyPause;

    /// <summary>
    /// The ranges that Find in Selection searches, which follow the edits; null while it is off. It is on for the
    /// document shown until the bar closes, as in VS Code.
    /// </summary>
    private TextSegmentCollection<TextSegment>? _scope;

    /// <summary>The bindings to the app's font where the editor is, while the bar is in the visual tree.</summary>
    private IDisposable[] _font = [];

    public SearchBar()
    {
        IsVisible = false;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        // Clear of the vertical scroll bar.
        Margin = new Thickness(0, 0, 24, 0);
        _previous = IconButton(s_previous, FindPrevious);
        _next = IconButton(s_next, FindNext);
        _close = IconButton(s_close, Close);
        ShowTexts();
        _replaceRow = Row(_replace, _preserveCase, _replaceOne, _replaceAll);
        _replaceRow.IsVisible = false;
        // About the width of the rows, which a longer message wraps to.
        _message.MaxWidth = 520;
        Content = new FlyoutPresenter
        {
            // Not cut to a flyout's width.
            MaxWidth = double.PositiveInfinity,
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    Row(_find, _matchCase, _wholeWord, _regex, _count, _previous, _next, _findInSelection, _close),
                    _replaceRow,
                    _message,
                },
            },
        };
        // Raised as soon as the text changed; TextChanged comes later, posted.
        _find.TextChanging += (_, _) => OnQueryChanged();
        _matchCase.IsCheckedChanged += (_, _) => OnQueryChanged();
        _wholeWord.IsCheckedChanged += (_, _) => OnQueryChanged();
        _regex.IsCheckedChanged += (_, _) => OnQueryChanged();
        _replace.TextChanging += (_, _) => OnReplacementChanged();
        _preserveCase.IsCheckedChanged += (_, _) => OnReplacementChanged();
        _replaceOne.Click += (_, _) => ReplaceOne();
        _replaceAll.Click += (_, _) => ReplaceAll();
        _findInSelection.Click += (_, _) => OnFindInSelectionClicked();
        _find.GotFocus += (_, _) => FollowTheSelection();
        _replace.GotFocus += (_, _) => FollowTheSelection();
        // As in VS Code, a box with a history says so while it has the keyboard.
        foreach (TextBox box in (TextBox[])[_find, _replace])
        {
            box.GotFocus += (_, _) => ShowHistoryHint(box);
            box.LostFocus += (_, _) => ShowHistoryHint(box);
            box.TextChanged += (_, _) => ShowHistoryHint(box);
        }
        // Ahead of the text boxes' own keys: Enter goes to the next match rather than into the text.
        _find.AddHandler(KeyDownEvent, OnFindKeyDown, RoutingStrategies.Tunnel);
        _replace.AddHandler(KeyDownEvent, OnReplaceKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Which text box takes the keyboard as the bar opens.</summary>
    private enum KeyboardTarget
    {
        None,
        Query,
        Replacement,
    }

    private enum Move
    {
        None,

        /// <summary>To the first match from where the search for a new query started.</summary>
        First,

        Next,
        Previous,
    }

    /// <summary>Whether the bar is open.</summary>
    public bool IsOpen => IsVisible;

    /// <summary>The query, also while the bar is closed: the next search is for it.</summary>
    public string Query => _find.Text ?? "";

    /// <summary>Whether the query has the keyboard.</summary>
    public bool IsQueryFocused => _find.IsKeyboardFocusWithin;

    /// <summary>Whether Match Case is on, also while the bar is closed.</summary>
    public bool MatchCase => _matchCase.IsChecked == true;

    /// <summary>Whether Match Whole Word is on, also while the bar is closed.</summary>
    public bool WholeWord => _wholeWord.IsChecked == true;

    /// <summary>Whether Use Regular Expression is on, also while the bar is closed.</summary>
    public bool UseRegex => _regex.IsChecked == true;

    /// <summary>Raised when the bar opens or closes, or its query or options change, as VS Code's find state.</summary>
    public event EventHandler? StateChanged;

    /// <summary>The colors of the color theme, once it is loaded.</summary>
    public TextAreaColors? Colors
    {
        set
        {
            _colors = value;
            _shown?.Layer.Colors = value;
            _shown?.ScopeLayer.Colors = value?.Highlights;
        }
    }

    internal TextBox FindBox => _find;

    internal TextBox ReplaceBox => _replace;

    internal ToggleButton MatchCaseToggle => _matchCase;

    internal ToggleButton RegexToggle => _regex;

    internal ToggleButton PreserveCaseToggle => _preserveCase;

    internal ToggleButton FindInSelectionToggle => _findInSelection;

    internal Button ReplaceAllButton => _replaceAll;

    /// <summary>The number of matches, and which one is selected.</summary>
    internal string? Count => _count.Text;

    /// <summary>The tip of the number of matches.</summary>
    internal object? CountTip => ToolTip.GetTip(_count);

    /// <summary>Why the result is partial, or the regular expression is invalid; null for neither.</summary>
    internal string? Message => _message.IsVisible ? _message.Text : null;

    /// <summary>Searches the document that <paramref name="editor"/> shows now, until <see cref="Detach"/>.</summary>
    public void Attach(CodeEditor editor, TextArea textArea, CodeDocument document)
    {
        var shown = new Shown(
            editor,
            textArea,
            document,
            textArea.Document,
            new SearchMatchLayer(textArea) { Colors = _colors },
            new SearchScopeLayer(textArea.TextView) { Colors = _colors?.Highlights }
        );
        _shown = shown;
        shown.Buffer.Changed += OnBufferChanged;
        shown.Buffer.TextChanged += OnTextChanged;
        textArea.SelectionChanged += OnSelectionChanged;
        textArea.Caret.PositionChanged += OnCaretPositionChanged;
        _from = Caret();
        ShowReplaceRow();
        Search(Move.None);
    }

    /// <summary>Stops searching the document the editor showed: no more searches, and no highlights.</summary>
    public void Detach()
    {
        if (_shown is not { } shown)
            return;
        CancelSearch();
        _move = Move.None;
        _historyPause?.Dispose();
        _historyPause = null;
        shown.Buffer.Changed -= OnBufferChanged;
        shown.Buffer.TextChanged -= OnTextChanged;
        shown.TextArea.SelectionChanged -= OnSelectionChanged;
        shown.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
        shown.Layer.Dispose();
        shown.ScopeLayer.Dispose();
        _shown = null;
        _result = null;
        // As in VS Code, Find in Selection ends with the document.
        _scope = null;
        _findInSelection.IsChecked = false;
        UpdateStatus();
    }

    /// <summary>
    /// Opens the bar, with its replace row if <paramref name="replace"/>, and otherwise with the row as it was in an
    /// open bar, as in VS Code. <paramref name="text"/> from the document, if given, becomes the query, escaped while
    /// the bar uses a regular expression, and leaves the selection where it is. The keyboard goes into the query, or
    /// into the replacement if <paramref name="focusReplacement"/> and the row shows.
    /// </summary>
    public void Open(bool replace, string? text, bool focusReplacement = false) =>
        Open(replace, text, focusReplacement ? KeyboardTarget.Replacement : KeyboardTarget.Query, Move.None);

    /// <summary>
    /// Selects the next match, opening the bar if it is closed, without taking the keyboard; for
    /// <paramref name="text"/> from the document as the new query, if given.
    /// </summary>
    public void FindNext(string? text) => Go(Move.Next, text);

    /// <summary>
    /// Selects the previous match, opening the bar if it is closed, without taking the keyboard; for
    /// <paramref name="text"/> from the document as the new query, if given.
    /// </summary>
    public void FindPrevious(string? text) => Go(Move.Previous, text);

    /// <summary>
    /// Turns Match Case on or off, as its key does, also while the bar is closed, for the next search. Unlike a click
    /// on the toggle, the key leaves the selection where it is, as in VS Code.
    /// </summary>
    public void ToggleMatchCase() => Toggle(_matchCase);

    /// <summary>Turns Match Whole Word on or off, as <see cref="ToggleMatchCase"/> does Match Case.</summary>
    public void ToggleWholeWord() => Toggle(_wholeWord);

    /// <summary>Turns Use Regular Expression on or off, as <see cref="ToggleMatchCase"/> does Match Case.</summary>
    public void ToggleRegex() => Toggle(_regex);

    /// <summary>Turns Preserve Case on or off, also while the replace row is hidden, as in VS Code.</summary>
    public void TogglePreserveCase() => Toggle(_preserveCase);

    /// <summary>
    /// Turns Find in Selection on for the selection, as its key does, or off, and selects the first match from the
    /// selection's start; also while the bar is closed, for the next search, as in VS Code. Without a selection, it
    /// stays off.
    /// </summary>
    public void ToggleFindInSelection()
    {
        if (_scope is not null)
            SetScope(null, Move.First);
        else if (SelectionScope(trimEnd: true) is { } scope)
            SetScope(scope, Move.First);
    }

    /// <summary>
    /// Searches only the selection, as VS Code's automatic Find in Selection does when the bar opens, without moving
    /// the selection; nothing changes without a selection.
    /// </summary>
    public void FindInSelection()
    {
        if (SelectionScope(trimEnd: false) is { } scope)
            SetScope(scope, Move.None);
    }

    /// <summary>Closes the bar, ends Find in Selection, as in VS Code, and gives the keyboard back to the text.</summary>
    public void Close()
    {
        PauseForHistory();
        IsVisible = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
        CancelSearch();
        _move = Move.None;
        _result = null;
        _scope = null;
        _findInSelection.IsChecked = false;
        _shown?.Layer.Matches = null;
        _shown?.ScopeLayer.Scope = null;
        UpdateStatus();
        _shown?.TextArea.Focus();
    }

    /// <summary>
    /// Shows the replace row in replace mode, and searches again for what replaces the matches. An editor that became
    /// read-only ends replace mode, as in VS Code: the row stays hidden once the editor can be edited again.
    /// </summary>
    public void UpdateReplaceRow()
    {
        if (_shown?.Editor.IsReadOnly == true)
            _replaceMode = false;
        if (ShowReplaceRow())
            Search(Move.None);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The editor's font is a monospace one; the bar takes the app's, where the editor is.
        if (this.FindAncestorOfType<CodeEditor>()?.GetVisualParent() is AvaloniaObject around)
        {
            _font =
            [
                Bind(FontFamilyProperty, around.GetObservable(TextElement.FontFamilyProperty)),
                Bind(FontSizeProperty, around.GetObservable(TextElement.FontSizeProperty)),
            ];
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        foreach (IDisposable binding in _font)
            binding.Dispose();
        _font = [];
    }

    private void Open(bool replace, string? text, KeyboardTarget focus, Move move)
    {
        bool wasOpen = IsOpen;
        if (!wasOpen)
        {
            ShowTexts();
            PauseForHistory();
        }
        // As VS Code starts its search again, also in an open bar.
        _from = Caret();
        _replaceMode = replace || (wasOpen && _replaceMode);
        bool rowChanged = ShowReplaceRow();
        IsVisible = true;
        _shown?.ScopeLayer.Scope = _scope;
        if (!wasOpen)
            StateChanged?.Invoke(this, EventArgs.Empty);
        string? query = text is not null && _regex.IsChecked == true ? TextSearch.Escape(text) : text;
        if (query is not null && query != _find.Text)
        {
            // Unlike a typed query, it leaves the selection where it is, as in VS Code; the next or previous match from
            // there if asked.
            _find.Text = query;
            _move = move;
        }
        else if (!wasOpen || rowChanged)
            Search(move);
        else if (move != Move.None)
            Go(move, null);
        if (focus != KeyboardTarget.None)
        {
            // A bar shown for the first time has no text box in the visual tree until it is laid out.
            UpdateLayout();
            TextBox box = focus == KeyboardTarget.Replacement && _replaceRow.IsVisible ? _replace : _find;
            box.Focus();
            box.SelectAll();
        }
    }

    /// <summary>
    /// Shows the replace row in replace mode, unless the editor is read-only.
    /// </summary>
    /// <returns>Whether that changed, and with it whether searches find what replaces the matches.</returns>
    private bool ShowReplaceRow()
    {
        bool visible = _replaceMode && _shown?.Editor.IsReadOnly == false;
        if (visible == _replaceRow.IsVisible)
            return false;
        _replaceRow.IsVisible = visible;
        return true;
    }

    private void Go(Move move, string? text)
    {
        if (!IsOpen || text is not null)
        {
            Open(false, text, KeyboardTarget.None, move);
            return;
        }
        if (_searching is null && _result is not null)
            Perform(move, _result);
        else
            _move = move;
    }

    private void OnQueryChanged()
    {
        PauseForHistory();
        Search(Move.First);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnReplacementChanged()
    {
        PauseForHistory();
        Search(Move.None);
    }

    /// <summary>
    /// Puts the query and the replacement into their histories once nothing changes for half a second, as VS Code
    /// does after a change of what it searches for, of its options, or as its bar opens or closes.
    /// </summary>
    private void PauseForHistory()
    {
        if (_shown is not { } shown)
            return;
        _historyPause?.Dispose();
        ITimer? pause = null;
        pause = shown.Document.TimeProvider.CreateTimer(
            _ =>
                Dispatcher.Post(() =>
                {
                    if (_historyPause != pause)
                        return;
                    _historyPause = null;
                    pause?.Dispose();
                    RecordHistory();
                }),
            null,
            TimeSpan.FromMilliseconds(500),
            Timeout.InfiniteTimeSpan
        );
        _historyPause = pause;
    }

    /// <summary>Adds the query and the replacement to their histories, unless empty or the entries shown.</summary>
    private void RecordHistory()
    {
        if (_shown is null)
            return;
        _findCursor.Add(FindHistory, _find.Text ?? "");
        _replaceCursor.Add(ReplaceHistory, _replace.Text ?? "");
    }

    /// <summary>The query's history: the editor's, or the bar's own.</summary>
    private SearchHistory FindHistory => _shown?.Editor.FindHistory ?? _findHistory;

    /// <summary>The replacement's history: the editor's, or the bar's own.</summary>
    private SearchHistory ReplaceHistory => _shown?.Editor.ReplaceHistory ?? _replaceHistory;

    /// <summary>
    /// Shows the entry before or after in the history of the text box that has the keyboard, as Up and Down do in VS
    /// Code: in a text of several lines, Up only from its start and Down only from its end.
    /// </summary>
    /// <returns>Whether a text box of the bar had the keyboard there.</returns>
    public bool ShowHistory(bool previous)
    {
        TextBox? box =
            _find.IsFocused ? _find
            : _replace.IsFocused && _replaceRow.IsVisible ? _replace
            : null;
        if (box is null)
            return false;
        string text = box.Text ?? "";
        int start = Math.Min(box.SelectionStart, box.SelectionEnd);
        int end = Math.Max(box.SelectionStart, box.SelectionEnd);
        if (text.Contains('\n') && (previous ? start > 0 : end < text.Length))
            return false;
        (SearchHistory history, SearchHistoryCursor cursor) =
            box == _find ? (FindHistory, _findCursor) : (ReplaceHistory, _replaceCursor);
        string? shown = previous ? cursor.Previous(history, text) : cursor.Next(history, text);
        if (shown is not null && shown != text)
        {
            box.Text = shown;
            box.CaretIndex = shown.Length;
        }
        return true;
    }

    /// <summary>
    /// The box's placeholder, with VS Code's hint at Up and Down while it has the keyboard and a history to go through,
    /// and the keymap binds them.
    /// </summary>
    private void ShowHistoryHint(TextBox box)
    {
        string placeholder = box == _find ? Strings.Default.Find : Strings.Default.Replace;
        bool hint =
            box.IsFocused
            && (box == _find ? FindHistory : ReplaceHistory).Entries.Count > 0
            && _shown?.Editor.Keymap is { } keymap
            && keymap.GesturesOf(CodeEditorCommands.HistoryShowPrevious)
                is [{ Key: Key.Up, KeyModifiers: KeyModifiers.None }]
            && keymap.GesturesOf(CodeEditorCommands.HistoryShowNext)
                is [{ Key: Key.Down, KeyModifiers: KeyModifiers.None }];
        box.PlaceholderText = hint
            ? string.Format(CultureInfo.CurrentCulture, Strings.Default.HistoryHint, placeholder, "\u21C5")
            : placeholder;
    }

    /// <summary>
    /// Starts a search of the current text in the background, in place of the one under way, if the bar is open and
    /// has a query; it moves the selection as <paramref name="move"/> says once its result is in, or as the search it
    /// replaces was to.
    /// </summary>
    private void Search(Move move)
    {
        CancelSearch();
        if (move != Move.None)
            _move = move;
        if (!IsOpen || _shown is not { } shown || _find.Text is not { Length: > 0 } text)
        {
            _move = Move.None;
            _result = null;
            _shown?.Layer.Matches = null;
            UpdateStatus();
            return;
        }
        var query = new SearchQuery(
            // A text box takes line breaks as they are pasted.
            text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'),
            _matchCase.IsChecked == true,
            _wholeWord.IsChecked == true,
            _regex.IsChecked == true
        );
        SearchReplacement? replacement = _replaceRow.IsVisible ? Replacement() : null;
        IReadOnlyList<TextRange>? scopes = Scopes();
        TextSnapshot snapshot = shown.Document.CurrentSnapshot;
        var searching = new CancellationTokenSource();
        _searching = searching;
        // Cancelled, the task ends as cancelled, not as failed.
        Task<SearchResult> search = Task.Run(
            () => TextSearch.Find(snapshot, query, replacement, scopes, searching.Token),
            searching.Token
        );
        search.ContinueWith(
            _ => Dispatcher.Post(() => OnSearched(search, searching)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
        UpdateStatus();
    }

    /// <summary>
    /// Shows the result of a search, unless a newer search, closing the bar or the editor's document replaced it.
    /// Its failure goes to the log either way.
    /// </summary>
    private void OnSearched(Task<SearchResult> search, CancellationTokenSource searching)
    {
        if (search.Exception is { } exception)
        {
            Logger
                .TryGet(LogEventLevel.Error, AssetFiles.LogArea)
                ?.Log(this, "The search failed: {Exception}", exception);
        }
        if (_searching != searching)
            return;
        _searching = null;
        if (search.Exception is not null || _shown is not { } shown)
            return;
        SearchResult result = search.Result;
        _result = result;
        shown.Layer.Matches = result.Matches;
        Move move = _move;
        _move = Move.None;
        Perform(move, result);
        UpdateStatus();
    }

    private void CancelSearch()
    {
        _searching?.Cancel();
        _searching = null;
        _seeking?.Cancel();
        _seeking = null;
    }

    /// <summary>
    /// Selects a match as <paramref name="move"/> says, and scrolls it into view. Where the match may be beyond those
    /// that the result keeps, it searches the text for it in the background, as VS Code does beyond the matches it
    /// highlights: every match stays within reach.
    /// </summary>
    private void Perform(Move move, SearchResult result)
    {
        if (_shown is not { } shown || result.Matches is not { Count: > 0, LastSegment: { } last } matches)
            return;
        TextRange selection = shown.Editor.Selection.Range;
        bool capped = result.Outcome == SearchOutcome.TooManyMatches;
        switch (move)
        {
            case Move.First when matches.FindFirstSegmentWithStartAfter(_from) is { } first:
                Select(first);
                break;
            case Move.First when capped:
                int from = _from;
                Seek(
                    result,
                    selection,
                    wrapToFirst: true,
                    (snapshot, scopes, cancellation) =>
                        TextSearch.FindNext(snapshot, result.Query, scopes, from, skipEmpty: false, cancellation)
                );
                break;
            case Move.Next when After(selection) is { } next:
                Select(next);
                break;
            case Move.Next when capped:
                Seek(
                    result,
                    selection,
                    wrapToFirst: true,
                    (snapshot, scopes, cancellation) =>
                        TextSearch.FindNext(
                            snapshot,
                            result.Query,
                            scopes,
                            selection.End,
                            skipEmpty: selection.Length == 0,
                            cancellation
                        )
                );
                break;
            case Move.Previous
                when matches.FindFirstSegmentWithStartAfter(selection.Start) is { } after
                    && matches.GetPreviousSegment(after) is { } previous:
                Select(previous);
                break;
            case Move.Previous when capped:
                // The last match before the selection, which may be beyond the last kept, or the last of all.
                int before = matches.FindFirstSegmentWithStartAfter(selection.Start) is null
                    ? selection.Start
                    : int.MaxValue;
                int known = last.StartOffset;
                Seek(
                    result,
                    selection,
                    wrapToFirst: false,
                    (snapshot, scopes, cancellation) =>
                        TextSearch.FindPrevious(snapshot, result.Query, scopes, known, before, cancellation)
                );
                break;
            case Move.First or Move.Next:
                Select(matches.FirstSegment ?? last);
                break;
            case Move.Previous:
                Select(last);
                break;
        }

        // The first match after the range; an empty match at the caret is the current one.
        SearchMatch? After(TextRange range)
        {
            SearchMatch? next = matches.FindFirstSegmentWithStartAfter(range.End);
            if (next is not null && next.StartOffset == range.Start && next.Length == range.Length)
                next = matches.GetNextSegment(next);
            return next;
        }
    }

    /// <summary>
    /// Searches the text in the background for the match that <paramref name="find"/> finds, and selects it unless
    /// the result, the text or the selection changed meanwhile; without one, the result's first match if
    /// <paramref name="wrapToFirst"/>.
    /// </summary>
    private void Seek(
        SearchResult result,
        TextRange selection,
        bool wrapToFirst,
        Func<TextSnapshot, IReadOnlyList<TextRange>?, CancellationToken, (TextRange? Match, SearchOutcome Outcome)> find
    )
    {
        if (_shown is not { } shown || shown.Document.CurrentSnapshot is not { } snapshot)
            return;
        if (snapshot.Version != result.Version)
            return;
        List<TextRange>? scopes = Scopes();
        _seeking?.Cancel();
        var seeking = new CancellationTokenSource();
        _seeking = seeking;
        Task<(TextRange? Match, SearchOutcome Outcome)> seek = Task.Run(
            () => find(snapshot, scopes, seeking.Token),
            seeking.Token
        );
        seek.ContinueWith(
            _ => Dispatcher.Post(() => OnSought(seek, seeking, result, selection, wrapToFirst)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private void OnSought(
        Task<(TextRange? Match, SearchOutcome Outcome)> seek,
        CancellationTokenSource seeking,
        SearchResult result,
        TextRange selection,
        bool wrapToFirst
    )
    {
        if (seek.Exception is { } exception)
        {
            Logger
                .TryGet(LogEventLevel.Error, AssetFiles.LogArea)
                ?.Log(this, "The search for the next match failed: {Exception}", exception);
        }
        if (_seeking != seeking)
            return;
        _seeking = null;
        if (
            !seek.IsCompletedSuccessfully
            || _result != result
            || _shown is not { } shown
            || shown.Document.CurrentSnapshot.Version != result.Version
            || shown.Editor.Selection.Range != selection
        )
            return;
        (TextRange? match, SearchOutcome outcome) = seek.Result;
        if (match is { } found)
            Select(found);
        else if (outcome == SearchOutcome.OutOfTime)
            ShowMessage(outcome, null);
        else if (wrapToFirst && result.Matches.FirstSegment is { } first)
            Select(first);
    }

    private void Select(SearchMatch match) => Select(new TextRange(match.StartOffset, match.Length));

    private void Select(TextRange match)
    {
        if (_shown is not { } shown)
            return;
        _selecting = true;
        try
        {
            shown.Editor.Selection = new TextSelection(match.Start, match.End);
        }
        finally
        {
            _selecting = false;
        }
        shown.Editor.ScrollTo(match);
        // Its number, now that the selection is all set.
        UpdateStatus();
    }

    /// <summary>
    /// Replaces the selected match, and selects the next one; without a selected match, selects the next one.
    /// </summary>
    private void ReplaceOne()
    {
        if (_shown is not { } shown || CurrentResult() is not { } result)
            return;
        if (CurrentMatch(result) is not { } match)
        {
            Perform(Move.Next, result);
            return;
        }
        int end = match.StartOffset + match.Replacement.Length;
        shown.Buffer.Replace(match.StartOffset, match.Length, match.Replacement);
        // The next search replaces the result; until then, the rest of it.
        result.Matches.Remove(match);
        if ((result.Matches.FindFirstSegmentWithStartAfter(end) ?? result.Matches.FirstSegment) is { } next)
            Select(next);
    }

    /// <summary>
    /// Replaces every match in one step: those of a complete result, or beyond the matches that a result keeps, all of
    /// them found again, as VS Code does beyond those it highlights. Never only some: a search that ran out of time
    /// replaces nothing.
    /// </summary>
    private void ReplaceAll()
    {
        if (_shown is not { } shown || CurrentResult() is not { Replacement: { } replacement } result)
            return;
        if (result.Outcome == SearchOutcome.TooManyMatches)
        {
            ReplaceAllBeyondTheResult(shown, result.Query, replacement);
            return;
        }
        if (!result.IsComplete)
            return;
        SearchMatch[] matches = [.. result.Matches];
        using (shown.Buffer.RunUpdate())
        {
            // From the end, so that the matches still to replace keep their offsets.
            for (int i = matches.Length - 1; i >= 0; i--)
                shown.Buffer.Replace(matches[i].StartOffset, matches[i].Length, matches[i].Replacement);
        }
    }

    /// <summary>
    /// Replaces every match of the text as it is, however many, as VS Code's Replace All does beyond the matches it
    /// highlights: found on the UI thread, within the search's time limit, and replaced in one change of the text from
    /// the first match to the last, which moves anchors, markers and the selection as separate replacements would.
    /// </summary>
    private void ReplaceAllBeyondTheResult(Shown shown, SearchQuery query, SearchReplacement replacement)
    {
        (SearchEdit? edit, SearchOutcome outcome) = TextSearch.ReplaceAll(
            shown.Document.CurrentSnapshot,
            query,
            replacement,
            Scopes(),
            CancellationToken.None
        );
        if (edit is not null)
            shown.Buffer.Replace(edit.Start, edit.Length, edit.Text, edit.Map);
        else if (outcome == SearchOutcome.OutOfTime)
            ShowMessage(SearchOutcome.OutOfTime, null);
    }

    /// <summary>
    /// The result to replace with: one for the text as it is, the query and the replacement as they are, in an editor
    /// that may change the text. Null while a search is under way.
    /// </summary>
    private SearchResult? CurrentResult()
    {
        if (_result is not { Replacement: { } replacement } result || _searching is not null || !_replaceRow.IsVisible)
            return null;
        return
            _shown is { Editor.IsReadOnly: false } shown
            && result.Version == shown.Document.CurrentSnapshot.Version
            && replacement == Replacement()
            ? result
            : null;
    }

    /// <summary>The match that is selected; null for none.</summary>
    private SearchMatch? CurrentMatch(SearchResult result)
    {
        if (_shown is not { } shown)
            return null;
        TextRange selection = shown.Editor.Selection.Range;
        return
            result.Matches.FindFirstSegmentWithStartAfter(selection.Start) is { } match
            && match.StartOffset == selection.Start
            && match.Length == selection.Length
            ? match
            : null;
    }

    private int Caret() => _shown?.Editor.Selection.Caret ?? 0;

    /// <summary>The ranges that Find in Selection searches, as they are now; null while it is off.</summary>
    private List<TextRange>? Scopes() =>
        _scope?.Select(range => new TextRange(range.StartOffset, range.Length)).ToList();

    /// <summary>The scope of the selection, as Find in Selection takes it; null without a selection.</summary>
    private List<TextRange>? SelectionScope(bool trimEnd) =>
        _shown is { } shown ? FindScope.OfSelection(shown.Buffer, shown.TextArea.Selection.Segments, trimEnd) : null;

    /// <summary>
    /// Searches only <paramref name="ranges"/> from now on, or everywhere for null, and searches again, moving as
    /// <paramref name="move"/> says.
    /// </summary>
    private void SetScope(List<TextRange>? ranges, Move move)
    {
        _scope = null;
        if (ranges is not null)
        {
            _scope = new TextSegmentCollection<TextSegment>();
            foreach (TextRange range in ranges)
                _scope.Add(new TextSegment { StartOffset = range.Start, Length = range.Length });
        }
        _findInSelection.IsChecked = _scope is not null;
        if (IsOpen)
            _shown?.ScopeLayer.Scope = _scope;
        PauseForHistory();
        Search(move);
    }

    /// <summary>A click on the toggle turns Find in Selection on for the selection, or off.</summary>
    private void OnFindInSelectionClicked()
    {
        if (_findInSelection.IsChecked != true)
            SetScope(null, Move.First);
        else if (SelectionScope(trimEnd: true) is { } scope)
            SetScope(scope, Move.First);
        else
            _findInSelection.IsChecked = false;
    }

    /// <summary>
    /// As the query or the replacement takes the keyboard while Find in Selection is on, a selection over several
    /// lines that is not the selected match becomes the scope, as VS Code means to do: its code drops that filter and
    /// takes any selection.
    /// </summary>
    private void FollowTheSelection()
    {
        if (_scope is null || _shown is not { } shown || _result is { } result && CurrentMatch(result) is not null)
            return;
        TextRange selection = shown.Editor.Selection.Range;
        DocumentLine first = shown.Buffer.GetLineByOffset(selection.Start);
        DocumentLine last = shown.Buffer.GetLineByOffset(selection.End);
        // A selection to the start of the next line is within its first line.
        if (first == last || (last.Offset == selection.End && last.PreviousLine == first))
            return;
        if (SelectionScope(trimEnd: true) is { } scope)
            SetScope(scope, Move.First);
    }

    /// <summary>What replaces the matches, by the replace row as it is.</summary>
    private SearchReplacement Replacement() => new(_replace.Text ?? "", _preserveCase.IsChecked == true);

    /// <summary>Shows how many matches there are and which one is selected, and why a result is partial.</summary>
    private void UpdateStatus()
    {
        SearchResult? result = _result;
        int count = result?.Matches.Count ?? 0;
        CultureInfo culture = CultureInfo.CurrentCulture;
        _count.Text = result switch
        {
            null or { Outcome: SearchOutcome.InvalidPattern } => null,
            { Matches.Count: 0 } => Strings.Default.NoResults,
            _ => string.Format(
                culture,
                Strings.Default.MatchCount,
                Index(result) is { } index ? (index + 1).ToString(culture) : "?",
                count,
                result.Outcome == SearchOutcome.TooManyMatches ? "+" : ""
            ),
        };
        // As in VS Code, the tip of the count says that only some matches are highlighted.
        ToolTip.SetTip(
            _count,
            result?.Outcome == SearchOutcome.TooManyMatches
                ? string.Format(culture, Strings.Default.TooManyMatches, TextSearch.MaxMatches)
                : null
        );
        ShowMessage(result?.Outcome, result?.Error);
        // As in VS Code: to turn it on, there must be a selection.
        _findInSelection.IsEnabled = _scope is not null || _shown?.TextArea.Selection.IsEmpty == false;
        _replaceAll.IsEnabled =
            CurrentResult() is { Outcome: SearchOutcome.Complete or SearchOutcome.TooManyMatches, Matches.Count: > 0 };

        int? Index(SearchResult searched)
        {
            if (CurrentMatch(searched) is not { } current)
                return null;
            int index = 0;
            for (
                SearchMatch? match = searched.Matches.FirstSegment;
                match != current;
                match = searched.Matches.GetNextSegment(match)
            )
                index++;
            return index;
        }
    }

    /// <summary>Says why a search stopped early, or why its regular expression is invalid; nothing otherwise.</summary>
    private void ShowMessage(SearchOutcome? outcome, string? error)
    {
        _message.Text = outcome switch
        {
            SearchOutcome.OutOfTime => string.Format(
                CultureInfo.CurrentCulture,
                Strings.Default.SearchTimedOut,
                TextSearch.TimeLimit.TotalSeconds
            ),
            SearchOutcome.InvalidPattern => error,
            _ => null,
        };
        _message.IsVisible = _message.Text is not null;
    }

    /// <summary>Keeps the matches and the scope on their text while the text changes.</summary>
    private void OnBufferChanged(object? sender, DocumentChangeEventArgs e)
    {
        _result?.Matches.UpdateOffsets(e);
        _scope?.UpdateOffsets(e);
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        _from = Caret();
        if (IsOpen)
            Search(Move.None);
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (!_selecting)
            _from = Caret();
        if (IsOpen)
            UpdateStatus();
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (!_selecting)
            _from = Caret();
    }

    /// <summary>
    /// Enter selects the next match, and Shift+Enter the previous one; Ctrl+Enter, also on macOS, starts a new line in
    /// the query, as in VS Code. Tab goes on to the replacement.
    /// </summary>
    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None && _replaceRow.IsVisible)
            _replace.Focus(NavigationMethod.Tab);
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
            _find.SelectedText = "\n";
        else if (e.Key == Key.Enter && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
            Go(e.KeyModifiers == KeyModifiers.Shift ? Move.Previous : Move.Next, null);
        // Other keys with Enter don't break the line either.
        else if (e.Key != Key.Enter)
            return;
        e.Handled = true;
    }

    /// <summary>
    /// Enter replaces the selected match; Ctrl+Enter, also on macOS, and Shift+Enter start a new line in the
    /// replacement, as in VS Code. Shift+Tab goes back to the query.
    /// </summary>
    private void OnReplaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.Shift)
            _find.Focus(NavigationMethod.Tab);
        else if (e.Key == Key.Enter && e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Shift)
            _replace.SelectedText = "\n";
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            ReplaceOne();
        else if (e.Key != Key.Enter)
            return;
        e.Handled = true;
    }

    /// <summary>
    /// Turns <paramref name="option"/> on or off, and keeps the move that a search under way still has to make.
    /// </summary>
    private void Toggle(ToggleButton option)
    {
        Move move = _move;
        option.IsChecked = option.IsChecked != true;
        _move = move;
    }

    private void FindNext() => Go(Move.Next, null);

    private void FindPrevious() => Go(Move.Previous, null);

    /// <summary>
    /// A text box of the query or the replacement, as VS Code's: it holds line breaks, as <c>\n</c>, and grows with
    /// them up to about six lines, VS Code's 118 pixels; long lines scroll rather than wrap.
    /// </summary>
    private static TextBox QueryBox() =>
        new()
        {
            Width = 220,
            AcceptsReturn = true,
            NewLine = "\n",
            MaxLines = 6,
        };

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.AddRange(children);
        return row;
    }

    /// <summary>The bar's texts in the UI culture, as it opens: the app may switch its language while it runs.</summary>
    private void ShowTexts()
    {
        _find.PlaceholderText = Strings.Default.Find;
        _replace.PlaceholderText = Strings.Default.Replace;
        _replaceOne.Content = Strings.Default.Replace;
        _replaceAll.Content = Strings.Default.ReplaceAll;
        ToolTip.SetTip(_matchCase, WithKeys(Strings.Default.MatchCase, CodeEditorCommands.ToggleFindCaseSensitive));
        ToolTip.SetTip(_wholeWord, WithKeys(Strings.Default.MatchWholeWord, CodeEditorCommands.ToggleFindWholeWord));
        ToolTip.SetTip(_regex, WithKeys(Strings.Default.UseRegularExpression, CodeEditorCommands.ToggleFindRegex));
        ToolTip.SetTip(_preserveCase, WithKeys(Strings.Default.PreserveCase, CodeEditorCommands.TogglePreserveCase));
        ToolTip.SetTip(
            _findInSelection,
            WithKeys(Strings.Default.FindInSelection, CodeEditorCommands.ToggleFindInSelection)
        );
        ToolTip.SetTip(_previous, Strings.Default.PreviousMatch);
        ToolTip.SetTip(_next, Strings.Default.NextMatch);
        ToolTip.SetTip(_close, Strings.Default.CloseSearch);
    }

    /// <summary>
    /// A toggle's text with the keys that the editor's keymap binds to its command, as VS Code shows them.
    /// </summary>
    private string WithKeys(string text, string command) =>
        _shown?.Editor.Keymap.GesturesOf(command) is { Count: > 0 } keys
            ? $"{text} ({string.Join(' ', keys.Select(key => key.ToString("p", null)))})"
            : text;

    private static Button IconButton(Geometry icon, Action action)
    {
        var button = new Button { Content = Icon(icon) };
        button.Click += (_, _) => action();
        return button;
    }

    private static PathIcon Icon(Geometry icon) =>
        new()
        {
            Data = icon,
            Width = 12,
            Height = 12,
        };

    /// <summary>
    /// The editor, its text area, the document it shows, the document's buffer, the layer that highlights the matches,
    /// and the one that highlights the lines that Find in Selection searches.
    /// </summary>
    private sealed record Shown(
        CodeEditor Editor,
        TextArea TextArea,
        CodeDocument Document,
        TextDocument Buffer,
        SearchMatchLayer Layer,
        SearchScopeLayer ScopeLayer
    );
}

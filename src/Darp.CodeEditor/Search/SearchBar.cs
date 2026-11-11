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

    private readonly TextBox _find = new() { Width = 220 };
    private readonly ToggleButton _matchCase = new() { Content = "Aa" };
    private readonly ToggleButton _wholeWord = new() { Content = "ab" };
    private readonly ToggleButton _regex = new() { Content = ".*" };
    private readonly TextBlock _count = new() { MinWidth = 80, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _close;
    private readonly TextBox _replace = new() { Width = 220 };
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
    /// Where to move once the search under way has its result. A newer search that takes over keeps it: it replaces
    /// the search, not what the search was for.
    /// </summary>
    private Move _move;

    /// <summary>Where a search for a new query looks for its first match.</summary>
    private int _from;

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
        _replaceRow = Row(_replace, _replaceOne, _replaceAll);
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
                    Row(_find, _matchCase, _wholeWord, _regex, _count, _previous, _next, _close),
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
        _replace.TextChanging += (_, _) => Search(Move.None);
        _replaceOne.Click += (_, _) => ReplaceOne();
        _replaceAll.Click += (_, _) => ReplaceAll();
        _find.KeyDown += OnFindKeyDown;
        _replace.KeyDown += OnReplaceKeyDown;
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

    /// <summary>The colors of the color theme, once it is loaded.</summary>
    public TextAreaColors? Colors
    {
        set
        {
            _colors = value;
            _shown?.Layer.Colors = value;
        }
    }

    internal TextBox FindBox => _find;

    internal TextBox ReplaceBox => _replace;

    internal ToggleButton MatchCaseToggle => _matchCase;

    internal ToggleButton RegexToggle => _regex;

    internal Button ReplaceAllButton => _replaceAll;

    /// <summary>The number of matches, and which one is selected.</summary>
    internal string? Count => _count.Text;

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
            new SearchMatchLayer(textArea) { Colors = _colors }
        );
        _shown = shown;
        shown.Buffer.Changed += OnBufferChanged;
        shown.Buffer.TextChanged += OnTextChanged;
        textArea.SelectionChanged += OnSelectionChanged;
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
        shown.Buffer.Changed -= OnBufferChanged;
        shown.Buffer.TextChanged -= OnTextChanged;
        shown.TextArea.SelectionChanged -= OnSelectionChanged;
        shown.Layer.Dispose();
        _shown = null;
        _result = null;
        UpdateStatus();
    }

    /// <summary>
    /// Opens the bar, or the bar with its replace row, with <paramref name="text"/> as the query if given, and puts
    /// the keyboard in the query.
    /// </summary>
    public void Open(bool replace, string? text) => Open(replace, text, focus: true, Move.None);

    /// <summary>Selects the next match, opening the bar if it is closed, without taking the keyboard.</summary>
    public void FindNext(string? text) => Go(Move.Next, text);

    /// <summary>Selects the previous match, opening the bar if it is closed, without taking the keyboard.</summary>
    public void FindPrevious(string? text) => Go(Move.Previous, text);

    /// <summary>Closes the bar, and gives the keyboard back to the text.</summary>
    public void Close()
    {
        IsVisible = false;
        CancelSearch();
        _move = Move.None;
        _result = null;
        _shown?.Layer.Matches = null;
        UpdateStatus();
        _shown?.TextArea.Focus();
    }

    /// <summary>
    /// Shows the replace row in replace mode, unless the editor is read-only, and searches again for what replaces
    /// the matches.
    /// </summary>
    public void UpdateReplaceRow()
    {
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

    private void Open(bool replace, string? text, bool focus, Move move)
    {
        bool wasOpen = IsOpen;
        if (!wasOpen)
            ShowTexts();
        _replaceMode = replace;
        bool rowChanged = ShowReplaceRow();
        IsVisible = true;
        if (text is not null && text != _find.Text)
        {
            // Searches from the selection, as for any new query; the next or previous match from there if asked.
            _find.Text = text;
            if (move != Move.None)
                _move = move;
        }
        else if (!wasOpen || rowChanged)
            Search(move);
        else if (move != Move.None)
            Go(move, null);
        if (focus)
        {
            // A bar shown for the first time has no text box in the visual tree until it is laid out.
            UpdateLayout();
            _find.Focus();
            _find.SelectAll();
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
        if (!IsOpen)
        {
            Open(false, text, focus: false, move);
            return;
        }
        if (_searching is null && _result is not null)
            Perform(move, _result);
        else
            _move = move;
    }

    private void OnQueryChanged()
    {
        _from = SelectionStart();
        Search(Move.First);
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
            text,
            _matchCase.IsChecked == true,
            _wholeWord.IsChecked == true,
            _regex.IsChecked == true
        );
        string? replacement = _replaceRow.IsVisible ? _replace.Text ?? "" : null;
        TextSnapshot snapshot = shown.Document.CurrentSnapshot;
        var searching = new CancellationTokenSource();
        _searching = searching;
        // Cancelled, the task ends as cancelled, not as failed.
        Task<SearchResult> search = Task.Run(
            () => TextSearch.Find(snapshot, query, replacement, searching.Token),
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
    }

    /// <summary>Selects a match as <paramref name="move"/> says, and scrolls it into view.</summary>
    private void Perform(Move move, SearchResult result)
    {
        if (_shown is not { } shown || result.Matches.Count == 0)
            return;
        TextSegmentCollection<SearchMatch> matches = result.Matches;
        TextRange selection = shown.Editor.Selection.Range;
        SearchMatch? match = move switch
        {
            Move.First => matches.FindFirstSegmentWithStartAfter(_from) ?? matches.FirstSegment,
            Move.Next => After(selection),
            Move.Previous => matches.FindFirstSegmentWithStartAfter(selection.Start) is { } after
                ? matches.GetPreviousSegment(after) ?? matches.LastSegment
                : matches.LastSegment,
            _ => null,
        };
        if (match is not null)
            Select(match);

        SearchMatch After(TextRange range)
        {
            SearchMatch? next = matches.FindFirstSegmentWithStartAfter(range.End);
            // An empty match at the caret is the current one.
            if (next is not null && next.StartOffset == range.Start && next.Length == range.Length)
                next = matches.GetNextSegment(next);
            return next ?? matches.FirstSegment;
        }
    }

    private void Select(SearchMatch match)
    {
        if (_shown is not { } shown)
            return;
        shown.Editor.Selection = new TextSelection(match.StartOffset, match.EndOffset);
        shown.Editor.ScrollTo(new TextRange(match.StartOffset, match.Length));
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

    /// <summary>Replaces every match in one step, if the result has them all: never a partial result.</summary>
    private void ReplaceAll()
    {
        if (_shown is not { } shown || CurrentResult() is not { IsComplete: true } result)
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
            && replacement == (_replace.Text ?? "")
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

    private int SelectionStart() => _shown?.Editor.Selection.Range.Start ?? 0;

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
        _message.Text = result?.Outcome switch
        {
            SearchOutcome.TooManyMatches => string.Format(
                culture,
                Strings.Default.TooManyMatches,
                TextSearch.MaxMatches
            ),
            SearchOutcome.OutOfTime => string.Format(
                culture,
                Strings.Default.SearchTimedOut,
                TextSearch.TimeLimit.TotalSeconds
            ),
            SearchOutcome.InvalidPattern => result.Error,
            _ => null,
        };
        _message.IsVisible = _message.Text is not null;
        _replaceAll.IsEnabled = CurrentResult() is { IsComplete: true, Matches.Count: > 0 };

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

    /// <summary>Keeps the matches on their text while the text changes.</summary>
    private void OnBufferChanged(object? sender, DocumentChangeEventArgs e) => _result?.Matches.UpdateOffsets(e);

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (IsOpen)
            Search(Move.None);
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (IsOpen)
            UpdateStatus();
    }

    /// <summary>
    /// Enter selects the next match, and Shift+Enter the previous one. Tab goes on to the replacement, as in VS Code.
    /// </summary>
    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None && _replaceRow.IsVisible)
            _replace.Focus(NavigationMethod.Tab);
        else if (e.Key == Key.Enter)
            Go(e.KeyModifiers == KeyModifiers.Shift ? Move.Previous : Move.Next, null);
        else
            return;
        e.Handled = true;
    }

    /// <summary>Enter replaces the selected match. Shift+Tab goes back to the query, as in VS Code.</summary>
    private void OnReplaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.Shift)
            _find.Focus(NavigationMethod.Tab);
        else if (e.Key == Key.Enter)
            ReplaceOne();
        else
            return;
        e.Handled = true;
    }

    private void FindNext() => Go(Move.Next, null);

    private void FindPrevious() => Go(Move.Previous, null);

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
        ToolTip.SetTip(_matchCase, Strings.Default.MatchCase);
        ToolTip.SetTip(_wholeWord, Strings.Default.MatchWholeWord);
        ToolTip.SetTip(_regex, Strings.Default.UseRegularExpression);
        ToolTip.SetTip(_previous, Strings.Default.PreviousMatch);
        ToolTip.SetTip(_next, Strings.Default.NextMatch);
        ToolTip.SetTip(_close, Strings.Default.CloseSearch);
    }

    private static Button IconButton(Geometry icon, Action action)
    {
        var button = new Button
        {
            Content = new PathIcon
            {
                Data = icon,
                Width = 12,
                Height = 12,
            },
        };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>
    /// The editor, its text area, the document it shows, the document's buffer, and the layer that highlights the
    /// matches.
    /// </summary>
    private sealed record Shown(
        CodeEditor Editor,
        TextArea TextArea,
        CodeDocument Document,
        TextDocument Buffer,
        SearchMatchLayer Layer
    );
}

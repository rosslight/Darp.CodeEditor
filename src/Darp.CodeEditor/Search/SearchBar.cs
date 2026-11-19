using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Darp.CodeEditor.Localization;

namespace Darp.CodeEditor.Search;

/// <summary>Which text box of the search bar takes the keyboard as it opens.</summary>
internal enum BarFocus
{
    /// <summary>None: the keyboard stays where it is, as for F3.</summary>
    None,
    Query,
    Replacement,
}

/// <summary>One of the search bar's toggles that its keys turn on and off.</summary>
internal enum SearchOption
{
    MatchCase,
    WholeWord,
    Regex,
    PreserveCase,
}

/// <summary>
/// The editor's search and replace bar, as VS Code's find widget, at the top right of the text: the app's text boxes,
/// toggle buttons and buttons, in the look of its flyouts and in its font, not the editor's, with its text in the UI
/// culture's language and the keymap's keys in its tips.
/// </summary>
/// <remarks>
/// The editor keeps one bar across the documents it shows: the query, the options and the replacement stay. The
/// <see cref="DocumentSearch"/> of the shown document searches for what the bar holds and shows the result in it;
/// the bar tells it what the user changed.
/// </remarks>
internal sealed class SearchBar : UserControl
{
    private static readonly Geometry s_previous = Geometry.Parse("M8,2 L14,9 L10,9 L10,14 L6,14 L6,9 L2,9 Z");
    private static readonly Geometry s_next = Geometry.Parse("M8,14 L14,7 L10,7 L10,2 L6,2 L6,7 L2,7 Z");
    private static readonly Geometry s_close = Geometry.Parse(
        "M3,2 L8,7 L13,2 L14,3 L9,8 L14,13 L13,14 L8,9 L3,14 L2,13 L7,8 L2,3 Z"
    );

    /// <summary>Lines in a box, the icon of Find in Selection.</summary>
    private static readonly Geometry s_selection = Geometry.Parse(
        "F0 M2,2 L14,2 L14,14 L2,14 Z M3,3 L3,13 L13,13 L13,3 Z M5,5 L11,5 L11,6 L5,6 Z M5,7.5 L11,7.5 L11,8.5 L5,8.5 Z "
            + "M5,10 L9,10 L9,11 L5,11 Z"
    );

    private readonly TextBox _find = QueryBox();
    private readonly ToggleButton _matchCase = new() { Content = "Aa" };
    private readonly ToggleButton _wholeWord = new() { Content = "ab" };
    private readonly ToggleButton _regex = new() { Content = ".*" };
    private readonly TextBlock _count = new() { MinWidth = 80, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _previous = IconButton(s_previous);
    private readonly Button _next = IconButton(s_next);
    private readonly ToggleButton _findInSelection = new() { Content = Icon(s_selection) };
    private readonly Button _close = IconButton(s_close);
    private readonly TextBox _replace = QueryBox();
    private readonly ToggleButton _preserveCase = new() { Content = "AB" };
    private readonly Button _replaceOne = new();
    private readonly Button _replaceAll = new();
    private readonly StackPanel _replaceRow;
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };

    /// <summary>Whether the code, not the user, changes the bar: the search knows already.</summary>
    private bool _setting;

    /// <summary>The bindings to the app's font where the editor is, while the bar is in the visual tree.</summary>
    private IDisposable[] _font = [];

    public SearchBar()
    {
        IsVisible = false;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        // Clear of the vertical scroll bar.
        Margin = new Thickness(0, 0, 24, 0);
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
        _find.TextChanging += (_, _) => OnUserChange(search => search.OnQueryChanged());
        foreach (ToggleButton option in (ToggleButton[])[_matchCase, _wholeWord, _regex])
            option.IsCheckedChanged += (_, _) => OnUserChange(search => search.OnQueryChanged());
        _replace.TextChanging += (_, _) => OnUserChange(search => search.OnReplacementChanged());
        _preserveCase.IsCheckedChanged += (_, _) => OnUserChange(search => search.OnReplacementChanged());
        _findInSelection.Click += (_, _) => Search?.FindInSelection(_findInSelection.IsChecked == true);
        _previous.Click += (_, _) => Search?.Go(SearchMove.Previous);
        _next.Click += (_, _) => Search?.Go(SearchMove.Next);
        _close.Click += (_, _) => Close();
        _replaceOne.Click += (_, _) => Search?.ReplaceOne();
        _replaceAll.Click += (_, _) => Search?.ReplaceAll();
        // Ahead of the text boxes' own keys: Enter goes to the next match rather than into the text.
        _find.AddHandler(KeyDownEvent, OnFindKeyDown, RoutingStrategies.Tunnel);
        _replace.AddHandler(KeyDownEvent, OnReplaceKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// The search of the document that the editor shows, from the bar's first use; null while it shows none.
    /// </summary>
    public DocumentSearch? Search { get; set; }

    /// <summary>Whether the bar is open.</summary>
    public bool IsOpen => IsVisible;

    /// <summary>
    /// What the bar searches for, also while it is closed; null for an empty query. A text box takes line breaks as
    /// they are pasted: the query's are <c>\n</c>.
    /// </summary>
    public SearchQuery? Query =>
        _find.Text is { Length: > 0 } text
            ? new SearchQuery(
                text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'),
                _matchCase.IsChecked == true,
                _wholeWord.IsChecked == true,
                _regex.IsChecked == true
            )
            : null;

    /// <summary>Whether the query is a regular expression.</summary>
    public bool UsesRegex => _regex.IsChecked == true;

    /// <summary>What replaces the matches; null while the replace row is hidden.</summary>
    public SearchReplacement? Replacement =>
        _replaceRow.IsVisible ? new SearchReplacement(_replace.Text ?? "", _preserveCase.IsChecked == true) : null;

    /// <summary>Whether the query has the keyboard.</summary>
    public bool IsQueryFocused => _find.IsKeyboardFocusWithin;

    /// <summary>Shows Find in Selection on or off.</summary>
    public bool FindInSelection
    {
        set => _findInSelection.IsChecked = value;
    }

    /// <summary>The latest search, also of a document shown before; tests wait for it.</summary>
    internal Task LatestSearch => Search?.LatestSearch ?? Task.CompletedTask;

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

    /// <summary>
    /// Opens the bar, with its replace row if <paramref name="replace"/>, and otherwise with the row as it was in an
    /// open bar. <paramref name="query"/>, if given, becomes the query. The keyboard goes to
    /// <paramref name="focus"/>, into the replacement only where the row shows.
    /// </summary>
    /// <param name="replace">Whether the replace row shows.</param>
    /// <param name="query">The new query; null keeps it.</param>
    /// <param name="focus">Where the keyboard goes.</param>
    /// <param name="keymap">The editor's keymap, whose keys the tips name.</param>
    /// <returns>Whether that changed what to search for: the bar opened, or its query or replace row changed.</returns>
    public bool Open(bool replace, string? query, BarFocus focus, CodeEditorKeymap keymap)
    {
        bool opened = !IsOpen;
        if (opened)
            ShowTexts(keymap);
        bool row = replace || (!opened && _replaceRow.IsVisible);
        bool changed = opened || row != _replaceRow.IsVisible || (query is not null && query != _find.Text);
        _replaceRow.IsVisible = row;
        _setting = true;
        if (query is not null)
            _find.Text = query;
        _setting = false;
        IsVisible = true;
        TextBox? box = focus switch
        {
            BarFocus.Replacement when _replaceRow.IsVisible => _replace,
            BarFocus.None => null,
            _ => _find,
        };
        if (box is not null)
        {
            // A bar shown for the first time has no text box in the visual tree until it is laid out.
            UpdateLayout();
            box.Focus();
            box.SelectAll();
        }
        return changed;
    }

    /// <summary>Closes the bar, which ends the search and gives the keyboard back to the text.</summary>
    public void Close()
    {
        if (!IsOpen)
            return;
        IsVisible = false;
        Search?.OnClosed();
    }

    /// <summary>Turns <paramref name="option"/> on or off, as its key does, without searching again.</summary>
    public void Toggle(SearchOption option)
    {
        ToggleButton toggle = option switch
        {
            SearchOption.MatchCase => _matchCase,
            SearchOption.WholeWord => _wholeWord,
            SearchOption.Regex => _regex,
            _ => _preserveCase,
        };
        _setting = true;
        toggle.IsChecked = toggle.IsChecked != true;
        _setting = false;
    }

    /// <summary>Hides the replace row, as an editor that became read-only does, until Ctrl+H shows it again.</summary>
    public void HideReplaceRow() => _replaceRow.IsVisible = false;

    /// <summary>
    /// Shows how many matches there are, which one is selected, and why the result is partial; and whether Replace All
    /// and Find in Selection can act.
    /// </summary>
    /// <param name="result">The result shown; null for none.</param>
    /// <param name="index">The selected match's place in the result; null for none.</param>
    /// <param name="canReplaceAll">Whether Replace All can act.</param>
    /// <param name="canFindInSelection">Whether Find in Selection can turn on or off: with a selection.</param>
    public void ShowResult(SearchResult? result, int? index, bool canReplaceAll, bool canFindInSelection)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        _count.Text = result switch
        {
            null or { Outcome: SearchOutcome.InvalidPattern } => null,
            { Matches.Count: 0 } => Strings.NoResults,
            _ => string.Format(
                culture,
                Strings.MatchCount,
                index is { } i ? (i + 1).ToString(culture) : "?",
                result.Matches.Count,
                result.Outcome == SearchOutcome.TooManyMatches ? "+" : ""
            ),
        };
        ToolTip.SetTip(
            _count,
            result?.Outcome == SearchOutcome.TooManyMatches
                ? string.Format(culture, Strings.TooManyMatches, TextSearch.MaxMatches)
                : null
        );
        ShowMessage(result?.Outcome == SearchOutcome.InvalidPattern ? result.Error : null, result?.Outcome);
        _replaceAll.IsEnabled = canReplaceAll;
        _findInSelection.IsEnabled = canFindInSelection;
    }

    /// <summary>Says that a search for a match beyond those kept, or Replace All's, stopped after its time.</summary>
    public void ShowTimedOut() => ShowMessage(null, SearchOutcome.OutOfTime);

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

    private void OnUserChange(Action<DocumentSearch> tell)
    {
        if (!_setting && Search is { } search)
            tell(search);
    }

    private void ShowMessage(string? error, SearchOutcome? outcome)
    {
        _message.Text =
            outcome == SearchOutcome.OutOfTime
                ? string.Format(CultureInfo.CurrentCulture, Strings.SearchTimedOut, TextSearch.TimeLimit.TotalSeconds)
                : error;
        _message.IsVisible = _message.Text is not null;
    }

    /// <summary>
    /// Enter selects the next match, and Shift+Enter the previous one; Ctrl+Enter, also on macOS, starts a new line in
    /// the query. Tab goes on to the replacement.
    /// </summary>
    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None && _replaceRow.IsVisible)
            _replace.Focus(NavigationMethod.Tab);
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
            _find.SelectedText = "\n";
        else if (e.Key == Key.Enter && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
            Search?.Go(e.KeyModifiers == KeyModifiers.Shift ? SearchMove.Previous : SearchMove.Next);
        // Other keys with Enter don't break the line either.
        else if (e.Key != Key.Enter)
            return;
        e.Handled = true;
    }

    /// <summary>
    /// Enter replaces the selected match; Ctrl+Enter, also on macOS, and Shift+Enter start a new line in the
    /// replacement. Shift+Tab goes back to the query.
    /// </summary>
    private void OnReplaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.Shift)
            _find.Focus(NavigationMethod.Tab);
        else if (e.Key == Key.Enter && e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Shift)
            _replace.SelectedText = "\n";
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            Search?.ReplaceOne();
        else if (e.Key != Key.Enter)
            return;
        e.Handled = true;
    }

    /// <summary>The bar's texts in the UI culture, as it opens, and the keymap's keys in the tips.</summary>
    private void ShowTexts(CodeEditorKeymap keymap)
    {
        _find.PlaceholderText = Strings.Find;
        _replace.PlaceholderText = Strings.Replace;
        _replaceOne.Content = Strings.Replace;
        _replaceAll.Content = Strings.ReplaceAll;
        ToolTip.SetTip(_matchCase, WithKeys(Strings.MatchCase, CodeEditorCommands.ToggleFindCaseSensitive));
        ToolTip.SetTip(_wholeWord, WithKeys(Strings.MatchWholeWord, CodeEditorCommands.ToggleFindWholeWord));
        ToolTip.SetTip(_regex, WithKeys(Strings.UseRegularExpression, CodeEditorCommands.ToggleFindRegex));
        ToolTip.SetTip(_preserveCase, WithKeys(Strings.PreserveCase, CodeEditorCommands.TogglePreserveCase));
        ToolTip.SetTip(_findInSelection, WithKeys(Strings.FindInSelection, CodeEditorCommands.ToggleFindInSelection));
        ToolTip.SetTip(_previous, WithKeys(Strings.PreviousMatch, CodeEditorCommands.FindPrevious));
        ToolTip.SetTip(_next, WithKeys(Strings.NextMatch, CodeEditorCommands.FindNext));
        ToolTip.SetTip(_close, Strings.CloseSearch);

        string WithKeys(string text, string command) =>
            keymap.GesturesOf(command) is { Count: > 0 } keys
                ? $"{text} ({string.Join(' ', keys.Select(key => key.ToString("p", null)))})"
                : text;
    }

    /// <summary>
    /// A text box of the query or the replacement: it holds line breaks, as <c>\n</c>, and grows with them up to six
    /// lines, about the 118 pixels of VS Code's; long lines scroll rather than wrap.
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

    private static Button IconButton(Geometry icon) => new() { Content = Icon(icon) };

    private static PathIcon Icon(Geometry icon) =>
        new()
        {
            Data = icon,
            Width = 12,
            Height = 12,
        };
}

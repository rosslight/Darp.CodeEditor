using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Search;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Highlights the other occurrences of the selected text, a port of VS Code's <c>SelectionHighlighter</c>: in the color
/// theme's <c>editor.selectionHighlightBackground</c>, at half its opacity while the text doesn't have the keyboard, as
/// <see cref="CodeEditor.SelectionHighlight"/> and the options with it say.
/// </summary>
/// <remarks>
/// As in VS Code, a selection over several lines, of spaces and tabs only, or longer than
/// <see cref="CodeEditor.SelectionHighlightMaxLength"/> highlights nothing, and neither does a caret without a
/// selection, which the occurrence highlights are for, nor the search bar's own query, which the matches show. The search
/// runs on the UI thread, as VS Code's does: at most <see cref="MaxHighlights"/> occurrences, and none in a document
/// too large to color.
/// </remarks>
internal sealed class SelectionHighlighter : IBackgroundRenderer, IDisposable
{
    /// <summary>The most occurrences highlighted, as VS Code's search for them finds.</summary>
    public const int MaxHighlights = 999;

    /// <summary>VS Code's limits of a document too large to color, and to search for the selection in.</summary>
    private const int MaxLength = 20 * 1024 * 1024;

    private const int MaxLineCount = 300_000;

    /// <summary>The pause after a change of the text, or a selection that went away, before the highlights follow.</summary>
    private static readonly TimeSpan s_delay = TimeSpan.FromMilliseconds(300);

    private readonly CodeEditor _editor;
    private readonly TextArea _textArea;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private HighlightColors? _colors;
    private IBrush? _faded;

    /// <summary>The occurrences highlighted, which follow the edits until the next update; null for none.</summary>
    private TextSegmentCollection<TextSegment>? _highlights;

    /// <summary>The occurrences found last, as VS Code keeps them while the text and what it searches stay.</summary>
    private Found? _found;

    private ITimer? _updateSoon;

    public SelectionHighlighter(CodeEditor editor, TextArea textArea, CodeDocument document)
    {
        _editor = editor;
        _textArea = textArea;
        _buffer = textArea.Document;
        _document = document;
        textArea.SelectionChanged += OnSelectionChanged;
        textArea.GotFocus += OnFocusChanged;
        textArea.LostFocus += OnFocusChanged;
        _buffer.Changed += OnBufferChanged;
        _buffer.TextChanged += OnTextChanged;
        textArea.TextView.BackgroundRenderers.Add(this);
        UpdateSoon();
    }

    /// <summary>Behind the selection and the text.</summary>
    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>The colors of the color theme, once it is loaded.</summary>
    public HighlightColors? Colors
    {
        set
        {
            _colors = value;
            _faded = value is null ? null : TextHighlights.Faded(value.SelectionHighlight);
            _textArea.TextView.InvalidateLayer(Layer);
        }
    }

    /// <summary>The editor's search bar, once it has one: its query and options take part.</summary>
    public SearchBar? SearchBar { get; set; }

    /// <summary>The occurrences highlighted, for tests.</summary>
    internal IReadOnlyList<TextRange> Highlights =>
        _highlights?.Select(range => new TextRange(range.StartOffset, range.Length)).ToList() ?? [];

    /// <summary>Finds the occurrences again, at once, as VS Code does after a change of the selection or of the search.</summary>
    public void Update()
    {
        CancelUpdate();
        SetHighlights(Find());
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_highlights is not { } highlights || _colors is not { } colors)
            return;
        // As VS Code: the full color only while the text has the keyboard.
        IBrush? brush = _textArea.IsFocused ? colors.SelectionHighlight : _faded;
        TextHighlights.Draw(textView, drawingContext, highlights, _ => (brush, colors.SelectionHighlightBorder));
    }

    public void Dispose()
    {
        CancelUpdate();
        _textArea.SelectionChanged -= OnSelectionChanged;
        _textArea.GotFocus -= OnFocusChanged;
        _textArea.LostFocus -= OnFocusChanged;
        _buffer.Changed -= OnBufferChanged;
        _buffer.TextChanged -= OnTextChanged;
        _textArea.TextView.BackgroundRenderers.Remove(this);
    }

    /// <summary>The occurrences to highlight now, as VS Code's <c>_createState</c> decides; null for none.</summary>
    private List<TextRange>? Find()
    {
        if (!_editor.SelectionHighlight)
            return null;
        Selection selection = _textArea.Selection;
        if (selection.IsMultiline && !_editor.SelectionHighlightMultiline)
            return null;
        if (What(selection) is not { } searched)
            return null;
        (string text, bool matchCase, bool wholeWord) = searched;
        if (text.AsSpan().IndexOfAnyExcept(" \t") < 0)
            return null;
        int maxLength = _editor.SelectionHighlightMaxLength;
        if (maxLength > 0 && text.Length > maxLength)
            return null;
        // The search bar's matches show the same.
        if (
            SearchBar is { IsOpen: true, UseRegex: false } bar
            && string.Equals(
                bar.Query,
                text,
                bar.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase
            )
            && bar.MatchCase == matchCase
            && bar.WholeWord == wholeWord
        )
        {
            return null;
        }
        if (_buffer.TextLength > MaxLength || _buffer.LineCount > MaxLineCount)
            return null;
        List<TextRange> found = Occurrences(text, matchCase, wholeWord);
        TextRange[] selected = selection.IsEmpty
            ? [new TextRange(_textArea.Caret.Offset, 0)]
            : [.. selection.Segments.Select(segment => new TextRange(segment.StartOffset, segment.Length))];
        return ApartFromTheSelection(found, selected);
    }

    /// <summary>
    /// The occurrences but the selection's own, and those that overlap it from before, as VS Code's
    /// <c>SelectionHighlighter</c> leaves them out (issues #64 and #512): both lists in order.
    /// </summary>
    private static List<TextRange> ApartFromTheSelection(List<TextRange> found, TextRange[] selected)
    {
        Array.Sort(selected, Compare);
        var apart = new List<TextRange>(found.Count);
        for (int i = 0, j = 0; i < found.Count; )
        {
            TextRange match = found[i];
            if (j >= selected.Length)
            {
                apart.Add(match);
                i++;
                continue;
            }
            int comparison = Compare(match, selected[j]);
            if (comparison < 0)
            {
                bool overlaps = match.End > selected[j].Start && selected[j].End > match.Start;
                if (selected[j].Length == 0 || !overlaps)
                    apart.Add(match);
                i++;
            }
            else if (comparison > 0)
                j++;
            else
            {
                i++;
                j++;
            }
        }
        return apart;

        static int Compare(TextRange a, TextRange b) =>
            a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End);
    }

    /// <summary>
    /// What to search for, as VS Code's <c>MultiCursorSession.create</c> decides: the search bar's query while it has the
    /// keyboard, else the selected text, with Match Case and Match Whole Word as
    /// <see cref="CodeEditor.SelectedTextMatchMode"/> says. Null for a caret without a selection, and for a rectangle
    /// whose lines differ.
    /// </summary>
    private (string Text, bool MatchCase, bool WholeWord)? What(Selection selection)
    {
        SearchBar? bar = SearchBar;
        if (!_textArea.IsFocused && bar is { IsOpen: true, Query.Length: > 0 })
            return (bar.Query, bar.MatchCase, bar.WholeWord);
        if (selection.IsEmpty)
            return null;
        (bool matchCase, bool wholeWord) = _editor.SelectedTextMatchMode switch
        {
            SelectedTextMatchMode.CaseSensitive => (true, false),
            SelectedTextMatchMode.CaseInsensitive => (false, false),
            _ => (bar?.MatchCase ?? false, bar?.WholeWord ?? false),
        };
        string[] texts = [.. selection.Segments.Select(segment => _buffer.GetText(segment))];
        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (texts.Any(text => text.Length == 0 || !string.Equals(text, texts[0], comparison)))
            return null;
        return (texts[0].Replace("\r\n", "\n", StringComparison.Ordinal), matchCase, wholeWord);
    }

    /// <summary>The occurrences of a text, found again only once the text or what is searched changed.</summary>
    private List<TextRange> Occurrences(string text, bool matchCase, bool wholeWord)
    {
        int version = _document.CurrentSnapshot.Version;
        if (_found is { } found && found.Matches(version, text, matchCase, wholeWord))
            return found.Ranges;
        // The text once per version, not once per selection, as a drag selects anew with each move.
        TextSnapshot snapshot =
            _found is { } last && last.Version == version
                ? last.Text
                : TextSnapshot.FromString(_document.CurrentSnapshot.GetText());
        SearchResult result = TextSearch.Find(
            snapshot,
            new SearchQuery(text, matchCase, wholeWord, UseRegex: false),
            null,
            null,
            CancellationToken.None,
            MaxHighlights
        );
        List<TextRange> ranges = [.. result.Matches.Select(match => new TextRange(match.StartOffset, match.Length))];
        _found = new Found(version, snapshot, text, matchCase, wholeWord, ranges);
        return ranges;
    }

    private void SetHighlights(List<TextRange>? ranges)
    {
        if (ranges is not { Count: > 0 })
        {
            if (_highlights is null)
                return;
            _highlights = null;
        }
        else
        {
            _highlights = new TextSegmentCollection<TextSegment>();
            foreach (TextRange range in ranges)
                _highlights.Add(new TextSegment { StartOffset = range.Start, Length = range.Length });
        }
        _textArea.TextView.InvalidateLayer(Layer);
    }

    /// <summary>
    /// A selection updates at once; without one, the highlights go, and come back after the pause, as in VS Code.
    /// </summary>
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (!_textArea.Selection.IsEmpty)
        {
            Update();
            return;
        }
        SetHighlights(null);
        UpdateSoon();
    }

    private void OnFocusChanged(object? sender, EventArgs e) => _textArea.TextView.InvalidateLayer(Layer);

    private void OnBufferChanged(object? sender, DocumentChangeEventArgs e) => _highlights?.UpdateOffsets(e);

    private void OnTextChanged(object? sender, EventArgs e) => UpdateSoon();

    private void UpdateSoon()
    {
        CancelUpdate();
        ITimer? timer = null;
        timer = _document.TimeProvider.CreateTimer(
            _ =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (_updateSoon != timer)
                        return;
                    _updateSoon = null;
                    timer?.Dispose();
                    SetHighlights(Find());
                }),
            null,
            s_delay,
            Timeout.InfiniteTimeSpan
        );
        _updateSoon = timer;
    }

    private void CancelUpdate()
    {
        _updateSoon?.Dispose();
        _updateSoon = null;
    }

    /// <summary>The occurrences of a text in a version of the document, and that version's text.</summary>
    private sealed record Found(
        int Version,
        TextSnapshot Text,
        string Searched,
        bool MatchCase,
        bool WholeWord,
        List<TextRange> Ranges
    )
    {
        public bool Matches(int version, string searched, bool matchCase, bool wholeWord) =>
            Version == version && Searched == searched && MatchCase == matchCase && WholeWord == wholeWord;
    }
}

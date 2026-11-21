using Avalonia.Logging;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Highlights the occurrences of the symbol at the caret, as VS Code's word highlighter
/// (<c>editor.occurrencesHighlight</c>): the server's <c>textDocument/documentHighlight</c>, reads and writes apart, or
/// without its answer the word's, as whole words with case, found off the UI thread.
/// </summary>
/// <remarks>
/// As in VS Code: a caret that the user moves asks after a pause, unless the selection stays inside an occurrence;
/// typing, any edit and the keyboard leaving the editor clear them; a selection over several lines, or beyond the word
/// at the caret, shows none.
/// </remarks>
internal sealed class OccurrenceHighlighter : IBackgroundRenderer, IDisposable
{
    /// <summary>The most occurrences of a word without a server, as VS Code's search for them finds.</summary>
    private const int MaxWords = 999;

    /// <summary>The pause after a caret move before the editor asks, VS Code's.</summary>
    private static readonly TimeSpan s_pause = TimeSpan.FromMilliseconds(50);

    private readonly TextArea _area;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly TextChanges _changes;

    private TextAreaColors? _colors;

    /// <summary>The occurrences highlighted, in order, in the current text.</summary>
    private Occurrence[] _highlights = [];

    /// <summary>The request under way, and the word it asks about; null for none.</summary>
    private (CancellationTokenSource Cancellation, TextRange Word)? _request;

    /// <summary>The pause after the last caret move; null for none.</summary>
    private ITimer? _pause;

    /// <summary>Counts the pauses, so that only the last one's end asks, and none once it is cancelled.</summary>
    private int _pauses;

    /// <summary>Whether the text changes, until the work in progress is done: its caret moves are no user's.</summary>
    private bool _editing;

    public OccurrenceHighlighter(TextArea area, CodeDocument document, TextChanges changes)
    {
        _area = area;
        _buffer = area.Document;
        _document = document;
        _changes = changes;
        area.Caret.PositionChanged += OnCaretMoved;
        area.SelectionChanged += OnSelectionChanged;
        area.GotFocus += OnFocused;
        _buffer.Changing += OnChanging;
        area.TextView.BackgroundRenderers.Add(this);
        // A document shown in an editor that has the keyboard gets no focus event, and its caret is already placed.
        if (area.IsFocused)
            Pause();
    }

    /// <summary>Behind the selection and the text.</summary>
    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>The colors of the color theme, once it is loaded.</summary>
    public TextAreaColors? Colors
    {
        set
        {
            _colors = value;
            _area.TextView.InvalidateVisual();
        }
    }

    /// <summary>The occurrences highlighted, for tests.</summary>
    internal IReadOnlyList<Occurrence> Highlights => _highlights;

    /// <summary>Whether a request is under way, for tests.</summary>
    internal bool IsAsking => _request is not null;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_highlights.Length == 0 || _colors is not { } colors || textView.VisualLines is not [VisualLine first, ..])
            return;
        int start = first.FirstDocumentLine.Offset;
        int end = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        foreach (Occurrence occurrence in _highlights)
        {
            if (occurrence.Range.End < start)
                continue;
            if (occurrence.Range.Start > end)
                break;
            (IBrush background, IPen? border) = occurrence.Kind switch
            {
                OccurrenceKind.Write => (colors.WordHighlightStrong, colors.WordHighlightStrongBorder),
                OccurrenceKind.Read => (colors.WordHighlight, colors.WordHighlightBorder),
                _ => (colors.WordHighlightText, colors.WordHighlightTextBorder),
            };
            var builder = new BackgroundGeometryBuilder
            {
                AlignToWholePixels = true,
                BorderThickness = border?.Thickness ?? 0,
            };
            builder.AddSegment(
                textView,
                new TextSegment { StartOffset = occurrence.Range.Start, Length = occurrence.Range.Length }
            );
            if (builder.CreateGeometry() is { } geometry)
                drawingContext.DrawGeometry(background, border, geometry);
        }
    }

    /// <summary>Clears the occurrences and ends the request under way: the keyboard left the editor.</summary>
    public void Clear()
    {
        _request?.Cancellation.Cancel();
        _request = null;
        if (_highlights.Length == 0)
            return;
        _highlights = [];
        _area.TextView.InvalidateVisual();
    }

    public void Dispose()
    {
        Clear();
        EndPause();
        _area.Caret.PositionChanged -= OnCaretMoved;
        _area.SelectionChanged -= OnSelectionChanged;
        _area.GotFocus -= OnFocused;
        _buffer.Changing -= OnChanging;
        _area.TextView.BackgroundRenderers.Remove(this);
    }

    /// <summary>
    /// Asks for the occurrences at the caret, as VS Code's word highlighter runs: while the text has the keyboard, for a
    /// selection inside the word at the caret, unless it stays inside an occurrence.
    /// </summary>
    private void Run()
    {
        if (!_area.IsFocused)
            return;
        if (WordAtSelection() is not { } word)
        {
            Clear();
            return;
        }
        TextRange selected = _changes.Selection.Range;
        if (
            _highlights.Any(occurrence =>
                occurrence.Range.Start <= selected.Start && selected.End <= occurrence.Range.End
            )
        )
            return;
        Clear();
        Ask(word);
    }

    /// <summary>
    /// Shows the server's occurrences of the symbol in <paramref name="word"/>, or without its answer those of the word,
    /// unless an edit, a newer request, the selection leaving the word or the view's end ended the request meanwhile.
    /// Failures go to the log.
    /// </summary>
    private async void Ask(TextRange word)
    {
        var request = new CancellationTokenSource();
        _request = (request, word);
        CancellationToken cancellation = request.Token;
        TextSnapshot snapshot = _document.CurrentSnapshot;
        try
        {
            string text = snapshot.GetText(word);
            IReadOnlyList<Occurrence> found =
                await _document.DocumentHighlightsAsync(snapshot, _area.Caret.Offset, cancellation)
                ?? await Task.Run(() => Words(snapshot, text), cancellation);
            if (_request?.Cancellation != request || _document.CurrentSnapshot != snapshot || WordAtSelection() != word)
                return;
            _request = null;
            _highlights = [.. found.OrderBy(occurrence => occurrence.Range.Start)];
            _area.TextView.InvalidateVisual();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e)
        {
            Logger
                .TryGet(LogEventLevel.Error, AssetFiles.LogArea)
                ?.Log(this, "Highlighting the occurrences failed: {Exception}", e);
        }
    }

    /// <summary>The occurrences of <paramref name="word"/> in <paramref name="snapshot"/>, as VS Code finds them.</summary>
    private static IReadOnlyList<Occurrence> Words(TextSnapshot snapshot, string word)
    {
        using TextReader text = snapshot.CreateReader();
        return
        [
            .. WordRules.Occurrences(text, word, MaxWords).Select(range => new Occurrence(range, OccurrenceKind.Text)),
        ];
    }

    /// <summary>The word at the caret, when the selection is inside it; null otherwise.</summary>
    private TextRange? WordAtSelection()
    {
        TextRange selected = _changes.Selection.Range;
        return
            WordRules.WordAt(_buffer, _area.Caret.Offset) is { } word
            && word.Start <= selected.Start
            && selected.End <= word.End
            ? word
            : null;
    }

    /// <summary>
    /// A move of the user's asks again after the pause; one that an edit makes clears the occurrences, as VS Code's does
    /// for a move that is not explicit.
    /// </summary>
    private void OnCaretMoved(object? sender, EventArgs e)
    {
        if (_editing)
            Clear();
        else
        {
            EndLeftRequest();
            Pause();
        }
    }

    /// <summary>
    /// A selection that changes without the caret, as when its anchor moves, may leave the word that the request under
    /// way asks about, or every word.
    /// </summary>
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        EndLeftRequest();
        if (_highlights.Length > 0 && WordAtSelection() is null)
            Clear();
    }

    /// <summary>
    /// Ends the request under way once the selection has left the word it asks about: its answer is about another
    /// symbol, and the pause asks again for this one.
    /// </summary>
    private void EndLeftRequest()
    {
        if (_request is not var (cancellation, word) || WordAtSelection() == word)
            return;
        cancellation.Cancel();
        _request = null;
    }

    /// <summary>As the text takes the keyboard, it asks, unless a request is under way, as in VS Code.</summary>
    private void OnFocused(object? sender, EventArgs e)
    {
        if (_request is null)
            Pause();
    }

    /// <summary>
    /// An edit clears the occurrences, which are of the text before it, and the caret moves until the work in progress
    /// is done are the edit's.
    /// </summary>
    private void OnChanging(object? sender, DocumentChangeEventArgs e)
    {
        Clear();
        EndPause();
        if (_editing)
            return;
        _editing = true;
        _area.Dispatcher.Post(() => _editing = false);
    }

    private void Pause()
    {
        EndPause();
        int pause = _pauses;
        _pause = _document.TimeProvider.CreateTimer(
            _ =>
                _area.Dispatcher.Post(() =>
                {
                    if (pause == _pauses)
                        Run();
                }),
            null,
            s_pause,
            Timeout.InfiniteTimeSpan
        );
    }

    /// <summary>Ends the pause under way, also once its end is posted.</summary>
    private void EndPause()
    {
        _pauses++;
        _pause?.Dispose();
        _pause = null;
    }
}

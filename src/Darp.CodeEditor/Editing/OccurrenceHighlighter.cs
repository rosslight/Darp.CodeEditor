using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Search;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Highlights the occurrences of the symbol at the caret, a port of VS Code's <c>WordHighlighter</c> for one document:
/// the server's, from <c>textDocument/documentHighlight</c>, with reads in <c>editor.wordHighlightBackground</c> and
/// writes in <c>editor.wordHighlightStrongBackground</c>; without a server, or when it has no answer, the occurrences of
/// the word at the caret, in <c>editor.wordHighlightTextBackground</c>.
/// </summary>
/// <remarks>
/// As in VS Code: a caret that the user moves asks again after 50 ms, unless it stays inside a highlight; typing, an
/// edit and the keyboard leaving the editor clear the highlights; a selection over several lines, or one that is not
/// inside a word, highlights nothing, also one that changes without the caret moving. The highlights show <see cref="CodeEditor.OccurrencesHighlightDelay"/> after the
/// request at the earliest.
/// </remarks>
internal sealed class OccurrenceHighlighter : IBackgroundRenderer, IDisposable
{
    /// <summary>The most occurrences of a word without a server, as VS Code's search for them finds.</summary>
    public const int MaxWords = 999;

    /// <summary>The pause after a caret move before the highlights follow it, as VS Code's.</summary>
    private static readonly TimeSpan s_runDelay = TimeSpan.FromMilliseconds(50);

    private readonly CodeEditor _editor;
    private readonly TextArea _textArea;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private HighlightColors? _colors;

    /// <summary>The occurrences highlighted, which follow the edits until they clear; null for none.</summary>
    private TextSegmentCollection<OccurrenceSegment>? _highlights;

    /// <summary>The request under way; null for none.</summary>
    private CancellationTokenSource? _request;

    /// <summary>The pause after a caret move; null for none.</summary>
    private ITimer? _runSoon;

    /// <summary>The wait for <see cref="CodeEditor.OccurrencesHighlightDelay"/> before an answer shows; null for none.</summary>
    private ITimer? _showLater;

    /// <summary>Whether a change of the text is under way, which the caret follows: typing, not a move of the user's.</summary>
    private bool _editing;

    /// <summary>Whether F7 or Shift+F7 moves the caret, which keeps the highlights.</summary>
    private bool _moving;

    /// <summary>The text of the version last searched for a word, kept while the text stays.</summary>
    private TextSnapshot? _text;

    private int _textVersion = -1;

    public OccurrenceHighlighter(CodeEditor editor, TextArea textArea, CodeDocument document)
    {
        _editor = editor;
        _textArea = textArea;
        _buffer = textArea.Document;
        _document = document;
        textArea.Caret.PositionChanged += OnCaretPositionChanged;
        textArea.SelectionChanged += OnSelectionChanged;
        textArea.GotFocus += OnTextFocused;
        _buffer.Changing += OnBufferChanging;
        _buffer.Changed += OnBufferChanged;
        _buffer.TextChanged += OnTextChanged;
        textArea.TextView.BackgroundRenderers.Add(this);
    }

    /// <summary>Behind the selection and the text.</summary>
    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>The colors of the color theme, once it is loaded.</summary>
    public HighlightColors? Colors
    {
        set
        {
            _colors = value;
            _textArea.TextView.InvalidateLayer(Layer);
        }
    }

    /// <summary>The occurrences highlighted, for tests.</summary>
    internal IReadOnlyList<Occurrence> Highlights =>
        _highlights
            ?.Select(segment => new Occurrence(new TextRange(segment.StartOffset, segment.Length), segment.Kind))
            .ToList()
        ?? [];

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_highlights is not { } highlights || _colors is not { } colors)
            return;
        TextHighlights.Draw(
            textView,
            drawingContext,
            highlights,
            segment =>
                segment.Kind switch
                {
                    OccurrenceKind.Write => (colors.WordHighlightStrong, colors.WordHighlightStrongBorder),
                    OccurrenceKind.Text => (colors.WordHighlightText, colors.WordHighlightTextBorder),
                    _ => (colors.WordHighlight, colors.WordHighlightBorder),
                }
        );
    }

    /// <summary>
    /// Clears the highlights and ends the request under way, as VS Code does when the text changes, the keyboard leaves
    /// the editor, or the option turns off.
    /// </summary>
    public void Stop()
    {
        _request?.Cancel();
        _request = null;
        _showLater?.Dispose();
        _showLater = null;
        if (_highlights is null)
            return;
        _highlights = null;
        _textArea.TextView.InvalidateLayer(Layer);
    }

    /// <summary>
    /// Moves the caret to the start of the next or the previous highlight, after the last to the first, as VS Code's F7
    /// and Shift+F7 do, keeping the highlights.
    /// </summary>
    /// <returns>Whether there were highlights to move to.</returns>
    public bool Move(bool next)
    {
        if (_highlights is not { Count: > 0 } highlights)
            return false;
        OccurrenceSegment[] sorted = [.. highlights.OrderBy(segment => segment.StartOffset)];
        int caret = _textArea.Caret.Offset;
        int index = Array.FindIndex(sorted, segment => segment.StartOffset <= caret && caret <= segment.EndOffset);
        OccurrenceSegment target = sorted[
            next ? (index + 1) % sorted.Length : (index - 1 + sorted.Length) % sorted.Length
        ];
        _moving = true;
        try
        {
            _editor.Selection = new TextSelection(target.StartOffset);
        }
        finally
        {
            _moving = false;
        }
        _editor.ScrollTo(new TextRange(target.StartOffset, target.Length));
        return true;
    }

    public void Dispose()
    {
        Stop();
        _runSoon?.Dispose();
        _runSoon = null;
        _textArea.Caret.PositionChanged -= OnCaretPositionChanged;
        _textArea.SelectionChanged -= OnSelectionChanged;
        _textArea.GotFocus -= OnTextFocused;
        _buffer.Changing -= OnBufferChanging;
        _buffer.Changed -= OnBufferChanged;
        _buffer.TextChanged -= OnTextChanged;
        _textArea.TextView.BackgroundRenderers.Remove(this);
    }

    /// <summary>
    /// Asks for the occurrences at the caret, as VS Code's <c>_run</c>: only while the text has the keyboard, for a
    /// caret or a selection inside a word, and not while the caret stays inside a highlight.
    /// </summary>
    private void Run()
    {
        if (_editor.OccurrencesHighlight == OccurrencesHighlight.Off)
        {
            Stop();
            return;
        }
        // Without the keyboard, VS Code asks again for the symbol it asked for last: the same highlights.
        if (!_textArea.IsFocused)
            return;
        if (!IsInsideWord())
        {
            Stop();
            return;
        }
        int caret = _textArea.Caret.Offset;
        if (_highlights?.FindSegmentsContaining(caret).Count > 0)
            return;
        Stop();
        var request = new CancellationTokenSource();
        _request = request;
        TextSnapshot snapshot = _document.CurrentSnapshot;
        long askedAt = _document.TimeProvider.GetTimestamp();
        Task<IReadOnlyList<Occurrence>?> answer = _document.DocumentHighlightsAsync(
            snapshot,
            snapshot.GetPosition(caret),
            request.Token
        );
        answer.ContinueWith(
            _ => Dispatcher.UIThread.Post(() => OnAnswered(answer, request, snapshot, caret, askedAt)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    /// <summary>Whether the caret, or the selection, is inside a word on one line: only then are there occurrences.</summary>
    private bool IsInsideWord()
    {
        Selection selection = _textArea.Selection;
        int caret = _textArea.Caret.Offset;
        (int start, int end) = selection.IsEmpty
            ? (caret, caret)
            : (selection.SurroundingSegment.Offset, selection.SurroundingSegment.EndOffset);
        DocumentLine line = _buffer.GetLineByOffset(start);
        return !selection.IsMultiline
            && WordRules.WordAtPosition(_buffer.GetText(line), start - line.Offset) is var (wordStart, wordEnd)
            && line.Offset + wordStart <= start
            && end <= line.Offset + wordEnd;
    }

    /// <summary>
    /// Shows the server's occurrences, or else those of the word at the caret, unless the request was replaced or the
    /// text changed meanwhile; not before the delay after the request.
    /// </summary>
    private void OnAnswered(
        Task<IReadOnlyList<Occurrence>?> answer,
        CancellationTokenSource request,
        TextSnapshot snapshot,
        int caret,
        long askedAt
    )
    {
        if (_request != request)
            return;
        _request = null;
        if (snapshot.Version != _document.CurrentSnapshot.Version)
            return;
        IReadOnlyList<Occurrence> occurrences =
            (answer.IsCompletedSuccessfully ? answer.Result : null) ?? Words(snapshot, caret);
        TimeSpan wait = _editor.OccurrencesHighlightDelay - _document.TimeProvider.GetElapsedTime(askedAt);
        if (wait <= TimeSpan.Zero)
        {
            Show(occurrences);
            return;
        }
        ITimer? later = null;
        later = _document.TimeProvider.CreateTimer(
            _ =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (_showLater != later)
                        return;
                    _showLater = null;
                    later?.Dispose();
                    if (snapshot.Version == _document.CurrentSnapshot.Version)
                        Show(occurrences);
                }),
            null,
            wait,
            Timeout.InfiniteTimeSpan
        );
        _showLater = later;
    }

    /// <summary>
    /// The occurrences of the word at <paramref name="caret"/>, as VS Code's <c>TextualDocumentHighlightProvider</c>
    /// finds them without a server: as whole words, with case, at most <see cref="MaxWords"/>.
    /// </summary>
    private IReadOnlyList<Occurrence> Words(TextSnapshot snapshot, int caret)
    {
        DocumentLine line = _buffer.GetLineByOffset(caret);
        string lineText = _buffer.GetText(line);
        if (WordRules.WordAtPosition(lineText, caret - line.Offset) is not var (start, end))
            return [];
        if (_textVersion != snapshot.Version || _text is null)
        {
            _text = TextSnapshot.FromString(snapshot.GetText());
            _textVersion = snapshot.Version;
        }
        SearchResult result = TextSearch.Find(
            _text,
            new SearchQuery(lineText[start..end], MatchCase: true, WholeWord: true, UseRegex: false),
            null,
            null,
            CancellationToken.None,
            MaxWords
        );
        return
        [
            .. result.Matches.Select(match => new Occurrence(
                new TextRange(match.StartOffset, match.Length),
                OccurrenceKind.Text
            )),
        ];
    }

    private void Show(IReadOnlyList<Occurrence> occurrences)
    {
        _highlights = null;
        if (occurrences.Count > 0)
        {
            _highlights = new TextSegmentCollection<OccurrenceSegment>();
            foreach (Occurrence occurrence in occurrences)
            {
                _highlights.Add(
                    new OccurrenceSegment(occurrence.Kind)
                    {
                        StartOffset = occurrence.Range.Start,
                        Length = occurrence.Range.Length,
                    }
                );
            }
        }
        _textArea.TextView.InvalidateLayer(Layer);
    }

    /// <summary>
    /// A move of the user's asks again after the pause; one that follows typing or another edit clears the highlights,
    /// as VS Code's does for a move that is not explicit.
    /// </summary>
    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_moving || _editor.OccurrencesHighlight == OccurrencesHighlight.Off)
            return;
        if (_editing)
        {
            Stop();
            return;
        }
        RunSoon();
    }

    /// <summary>
    /// A selection that changes without the caret moving, as when only its anchor moves, may no longer be inside a
    /// word: then nothing is highlighted, and the request under way ends.
    /// </summary>
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_editor.OccurrencesHighlight != OccurrencesHighlight.Off && !IsInsideWord())
            Stop();
    }

    /// <summary>As the text takes the keyboard, it asks unless a request is under way, as in VS Code.</summary>
    private void OnTextFocused(object? sender, EventArgs e)
    {
        if (_editor.OccurrencesHighlight != OccurrencesHighlight.Off && _request is null)
            RunSoon();
    }

    /// <summary>A change of the text moves the caret within it: the moves until the dispatcher goes on are the edit's.</summary>
    private void OnBufferChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (_editing)
            return;
        _editing = true;
        Dispatcher.UIThread.Post(() => _editing = false);
    }

    private void OnBufferChanged(object? sender, DocumentChangeEventArgs e) => _highlights?.UpdateOffsets(e);

    private void OnTextChanged(object? sender, EventArgs e) => Stop();

    private void RunSoon()
    {
        _runSoon?.Dispose();
        ITimer? timer = null;
        timer = _document.TimeProvider.CreateTimer(
            _ =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (_runSoon != timer)
                        return;
                    _runSoon = null;
                    timer?.Dispose();
                    Run();
                }),
            null,
            s_runDelay,
            Timeout.InfiniteTimeSpan
        );
        _runSoon = timer;
    }

    /// <summary>An occurrence highlighted, and how it uses the symbol.</summary>
    private sealed class OccurrenceSegment(OccurrenceKind kind) : TextSegment
    {
        public OccurrenceKind Kind { get; } = kind;
    }
}

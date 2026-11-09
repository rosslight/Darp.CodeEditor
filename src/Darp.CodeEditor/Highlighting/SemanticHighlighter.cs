using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Highlighting;

/// <summary>
/// Colors a language server's semantic tokens over the TextMate colors, in one editor's view of a document: it asks for
/// the tokens of the lines in view, keeps them line by line, and draws them in the color theme's colors
/// (<see cref="SemanticTokenStyles"/>).
/// </summary>
/// <remarks>
/// <para>
/// It asks when the view first shows lines, again after each edit once the user pauses, when scrolling shows lines
/// whose tokens are missing or older than the last edit, and when the document's server started or stopped serving
/// it. A new request cancels the one still under way, and so does an edit: its answer would be about older text. An
/// answer is applied only to the text it was asked for; one that comes after an edit is dropped.
/// </para>
/// <para>
/// Tokens stay with their line while the user types, and never color text that changed since they came: an edit
/// inside a line moves the tokens after it and drops the tokens it touches; an edit across lines keeps the tokens
/// before it and drops the rest of the lines it changed. After an edit every line's tokens count as outdated, as an
/// edit can change the meaning of text elsewhere, and are asked for again; until the answer comes, they still color
/// their unchanged text.
/// </para>
/// </remarks>
internal sealed class SemanticHighlighter : DocumentColorizingTransformer, IDisposable
{
    /// <summary>The pause after the last edit before the tokens are asked for again: VS Code's shortest.</summary>
    private static readonly TimeSpan s_afterEdit = TimeSpan.FromMilliseconds(300);

    /// <summary>The pause after scrolling shows lines without current tokens: VS Code's shortest for the view.</summary>
    private static readonly TimeSpan s_afterScroll = TimeSpan.FromMilliseconds(100);

    private readonly TextView _textView;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    /// <summary>One entry per line: its tokens and the text they came for; null while none came.</summary>
    private readonly List<LineTokens?> _lines = [];

    /// <summary>Counts the edits and the changes of the server: tokens of an older count are outdated.</summary>
    private int _generation;

    /// <summary>The request under way; null for none.</summary>
    private Request? _request;

    /// <summary>Whether the last answer covered every line, as answers of a server without ranges do.</summary>
    private bool _lastAnsweredAll;

    /// <summary>
    /// Whether the document had no tokens to give at this generation, as a document without a server: no more
    /// requests until it changes.
    /// </summary>
    private bool _unavailable;

    private ColorThemeFile? _theme;
    private SemanticTokensLegend? _legend;
    private SemanticTokenStyles? _styles;

    public SemanticHighlighter(TextView textView, CodeDocument document, Dispatcher dispatcher)
    {
        _textView = textView;
        _buffer = textView.Document;
        _document = document;
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += OnTimerTick;
        _lines.AddRange(new LineTokens?[_buffer.LineCount]);
        _buffer.Changed += OnBufferChanged;
        textView.VisualLinesChanged += OnVisualLinesChanged;
        document.LanguageFeaturesChanged += OnLanguageFeaturesChanged;
        // After the TextMate colors, so that it draws over them.
        textView.LineTransformers.Add(this);
    }

    /// <summary>The color theme whose colors the tokens take; null while it loads.</summary>
    public ColorThemeFile? Theme
    {
        set
        {
            if (value == _theme)
                return;
            _theme = value;
            _styles = null;
            _textView.Redraw();
        }
    }

    /// <summary>
    /// Shows <paramref name="tokens"/>, which are about the current text: the lines they cover take them, and lines
    /// whose tokens changed draw again.
    /// </summary>
    public void Show(SemanticTokenLines tokens)
    {
        if (tokens.Legend != _legend)
        {
            // Another server's numbers name other types: the tokens of the old one go.
            _legend = tokens.Legend;
            _styles = null;
            ClearLines();
        }
        for (int i = 0; i < tokens.Lines.Length && tokens.FirstLine + i < _lines.Count; i++)
        {
            int index = tokens.FirstLine + i;
            int[] lineTokens = tokens.Lines[i];
            bool changed = !(_lines[index]?.Tokens.AsSpan().SequenceEqual(lineTokens) ?? lineTokens.Length == 0);
            _lines[index] = new LineTokens(lineTokens, _generation);
            if (changed && _textView.GetVisualLine(index + 1) is { } visualLine)
                _textView.Redraw(visualLine);
        }
        _lastAnsweredAll = tokens.FirstLine == 0 && tokens.Lines.Length >= _lines.Count;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _request?.Cancellation.Cancel();
        _request = null;
        _buffer.Changed -= OnBufferChanged;
        _textView.VisualLinesChanged -= OnVisualLinesChanged;
        _document.LanguageFeaturesChanged -= OnLanguageFeaturesChanged;
        _textView.LineTransformers.Remove(this);
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        int index = line.LineNumber - 1;
        if (
            index >= _lines.Count
            || _lines[index] is not { Tokens: { Length: > 0 } tokens }
            || Styles() is not { } styles
        )
            return;
        int length = line.Length;
        for (int i = 0; i < tokens.Length; i += SemanticTokenLines.Stride)
        {
            int start = tokens[i];
            if (start >= length)
                break;
            int end = (int)Math.Min((long)start + tokens[i + 1], length);
            if (styles.Foreground(tokens[i + 2], tokens[i + 3]) is { } brush)
                ChangeLinePart(
                    line.Offset + start,
                    line.Offset + end,
                    e => e.TextRunProperties.SetForegroundBrush(brush)
                );
        }
    }

    private SemanticTokenStyles? Styles() =>
        _styles ??=
            _theme is { } theme && _legend is { } legend
                ? new SemanticTokenStyles(theme, _document.Language.Id, legend)
                : null;

    /// <summary>
    /// Keeps the tokens on their text: one entry per line, the tokens after an edit inside a line moved, and none on
    /// changed text. Then asks again once the user pauses.
    /// </summary>
    private void OnBufferChanged(object? sender, DocumentChangeEventArgs e)
    {
        DocumentLine firstLine = _buffer.GetLineByOffset(e.Offset);
        int first = firstLine.LineNumber - 1;
        int column = e.Offset - firstLine.Offset;
        int lastInserted = _buffer.GetLineByOffset(e.Offset + e.InsertionLength).LineNumber - 1;
        int delta = _buffer.LineCount - _lines.Count;
        // The entries right after the changed line come and go, so the entries of the lines after keep their tokens.
        if (delta > 0)
            _lines.InsertRange(first + 1, new LineTokens?[delta]);
        else if (delta < 0)
            _lines.RemoveRange(first + 1, -delta);
        if (_lines[first] is { } changed)
        {
            _lines[first] =
                lastInserted == first && !ContainsLineBreak(e.RemovedText)
                    ? changed.Moved(column, e.RemovalLength, e.InsertionLength)
                    : changed.Before(column);
        }
        for (int i = first + 1; i <= lastInserted; i++)
            _lines[i] = null;
        // A handler of the change that ran before this one may have built the changed lines from the old tokens.
        _textView.Redraw(e.Offset, e.InsertionLength);
        Outdate(s_afterEdit);
    }

    /// <summary>The view shows other lines: asks for their tokens once scrolling pauses, if they need them.</summary>
    private void OnVisualLinesChanged(object? sender, EventArgs e)
    {
        if (!_timer.IsEnabled && NeedsTokens())
            Schedule(s_afterScroll);
    }

    /// <summary>A server started or stopped serving the document: its tokens go, and the new server's are asked for.</summary>
    private void OnLanguageFeaturesChanged(object? sender, EventArgs e)
    {
        ClearLines();
        Outdate(s_afterScroll);
    }

    /// <summary>The tokens are outdated: the request under way too. New ones are asked for after a pause.</summary>
    private void Outdate(TimeSpan pause)
    {
        _generation++;
        _unavailable = false;
        _request?.Cancellation.Cancel();
        _request = null;
        Schedule(pause);
    }

    private void Schedule(TimeSpan pause)
    {
        _timer.Stop();
        _timer.Interval = pause;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (NeedsTokens())
            RequestTokens();
    }

    /// <summary>Whether a line in view lacks current tokens, and no request under way asks for it.</summary>
    private bool NeedsTokens()
    {
        if (_unavailable || !_textView.VisualLinesValid || _textView.VisualLines.Count == 0)
            return false;
        int first = _textView.VisualLines[0].FirstDocumentLine.LineNumber - 1;
        int last = _textView.VisualLines[^1].LastDocumentLine.LineNumber - 1;
        for (int i = first; i <= last; i++)
        {
            if (_lines[i]?.Generation != _generation && _request?.Covers(i) != true)
                return true;
        }
        return false;
    }

    /// <summary>Asks for the tokens of the lines in view, or of all lines from a server without ranges.</summary>
    private void RequestTokens()
    {
        DocumentLine first = _textView.VisualLines[0].FirstDocumentLine;
        DocumentLine last = _textView.VisualLines[^1].LastDocumentLine;
        _request?.Cancellation.Cancel();
        var cancellation = new CancellationTokenSource();
        Task<SemanticTokenLines?> answer = _document.SemanticTokensAsync(
            new TextPosition(first.LineNumber, 1),
            new TextPosition(last.LineNumber, last.Length + 1),
            _buffer.LineCount,
            cancellation.Token
        );
        var request = new Request(
            _generation,
            _lastAnsweredAll ? 0 : first.LineNumber - 1,
            _lastAnsweredAll ? int.MaxValue : last.LineNumber - 1,
            cancellation,
            answer
        );
        _request = request;
        answer.ContinueWith(
            _ => _dispatcher.Post(() => OnAnswered(request)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    /// <summary>
    /// Shows an answer, unless a newer request replaced it, or the text or the server changed since it was asked for.
    /// </summary>
    private void OnAnswered(Request request)
    {
        request.Cancellation.Dispose();
        if (_request != request)
            return;
        _request = null;
        if (request.Answer.Exception is { } exception)
        {
            Logger
                .TryGet(LogEventLevel.Error, AssetFiles.LogArea)
                ?.Log(this, "Reading semantic tokens failed: {Exception}", exception);
            return;
        }
        if (request.Generation != _generation)
            return;
        if (request.Answer.Result is not { } tokens)
        {
            _unavailable = true;
            return;
        }
        Show(tokens);
        // The view may have moved on meanwhile.
        if (!_timer.IsEnabled && NeedsTokens())
            Schedule(s_afterScroll);
    }

    private void ClearLines()
    {
        for (int i = 0; i < _lines.Count; i++)
            _lines[i] = null;
        _textView.Redraw();
    }

    private static bool ContainsLineBreak(ITextSource text) => text.IndexOfAny(['\r', '\n'], 0, text.TextLength) >= 0;

    /// <summary>A request for tokens, of the lines from <paramref name="First"/> to <paramref name="Last"/> or more.</summary>
    /// <param name="Generation">The count of edits and server changes when it was made.</param>
    /// <param name="First">The first line it asks for, from 0.</param>
    /// <param name="Last">The last line it asks for.</param>
    /// <param name="Cancellation">Cancels it.</param>
    /// <param name="Answer">The tokens, once the server answered; null for none.</param>
    private sealed record Request(
        int Generation,
        int First,
        int Last,
        CancellationTokenSource Cancellation,
        Task<SemanticTokenLines?> Answer
    )
    {
        public bool Covers(int line) => line >= First && line <= Last;
    }

    /// <summary>A line's tokens, as <see cref="SemanticTokenLines"/> has them.</summary>
    /// <param name="Tokens">Its tokens.</param>
    /// <param name="Generation">The count of edits and server changes that they are current for.</param>
    private sealed record LineTokens(int[] Tokens, int Generation)
    {
        /// <summary>
        /// The tokens after an edit inside the line, which replaced <paramref name="removed"/> characters at
        /// <paramref name="column"/> with <paramref name="inserted"/> ones: those before it stay, those after it move,
        /// and those it touches go.
        /// </summary>
        public LineTokens Moved(int column, int removed, int inserted)
        {
            var moved = new List<int>(Tokens.Length);
            for (int i = 0; i < Tokens.Length; i += SemanticTokenLines.Stride)
            {
                int start = Tokens[i];
                if ((long)start + Tokens[i + 1] <= column)
                    moved.AddRange(Tokens.AsSpan(i, SemanticTokenLines.Stride));
                else if (start >= column + removed)
                    moved.AddRange([start - removed + inserted, Tokens[i + 1], Tokens[i + 2], Tokens[i + 3]]);
            }
            return this with { Tokens = [.. moved] };
        }

        /// <summary>The tokens that end before <paramref name="column"/>, where an edit across lines starts.</summary>
        public LineTokens Before(int column)
        {
            var before = new List<int>(Tokens.Length);
            for (int i = 0; i < Tokens.Length; i += SemanticTokenLines.Stride)
            {
                if ((long)Tokens[i] + Tokens[i + 1] <= column)
                    before.AddRange(Tokens.AsSpan(i, SemanticTokenLines.Stride));
            }
            return this with { Tokens = [.. before] };
        }
    }
}

using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Editing;
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

    /// <summary>
    /// One entry per line: its tokens and the text they came for; null while none came. An answer for the whole text
    /// replaces it, built off the UI thread.
    /// </summary>
    private List<LineTokens?> _lines = [];

    /// <summary>Counts the edits and the changes of the server: tokens of an older count are outdated.</summary>
    private int _generation;

    /// <summary>The request under way; null for none.</summary>
    private Request? _request;

    /// <summary>Whether the last answer covered every line, as answers of a server without ranges do.</summary>
    private bool _lastAnsweredAll;

    /// <summary>The first line in view at the last layout, from 0: when it changes, the view scrolled.</summary>
    private int _firstShown = -1;

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
    /// Off the UI thread: the line entries of an answer that is about the text of <paramref name="generation"/>.
    /// </summary>
    public static Answer Prepare(SemanticTokenLines tokens, int generation) =>
        new(tokens.Legend, tokens.FirstLine, [.. tokens.Lines.Select(line => new LineTokens(line, generation))]);

    /// <summary>
    /// Shows an answer about the current text: the lines it covers take its tokens, and those in view whose tokens
    /// changed draw again. An answer for the whole text takes the place of all entries at once, so the UI thread's work
    /// grows with the lines in view, not with the text.
    /// </summary>
    public void Show(Answer answer)
    {
        if (answer.Legend != _legend)
        {
            // Another server's numbers name other types: the tokens of the old one go.
            _legend = answer.Legend;
            _styles = null;
            ClearLines();
        }
        (int firstShown, int lastShown) = Shown();
        var changed = new List<VisualLine>();
        for (int index = Math.Max(firstShown, answer.FirstLine); index <= lastShown; index++)
        {
            if (index - answer.FirstLine >= answer.Lines.Count || index >= _lines.Count)
                break;
            int[] tokens = answer.Lines[index - answer.FirstLine]?.Tokens ?? [];
            if (
                !(_lines[index]?.Tokens.AsSpan().SequenceEqual(tokens) ?? tokens.Length == 0)
                && _textView.GetVisualLine(index + 1) is { } visualLine
            )
            {
                changed.Add(visualLine);
            }
        }
        _lastAnsweredAll = answer.FirstLine == 0 && answer.Lines.Count == _lines.Count;
        if (_lastAnsweredAll)
            _lines = answer.Lines;
        else
        {
            // An answer for a range covers the lines in view.
            for (int i = 0; i < answer.Lines.Count && answer.FirstLine + i < _lines.Count; i++)
                _lines[answer.FirstLine + i] = answer.Lines[i];
        }
        foreach (VisualLine visualLine in changed)
            _textView.Redraw(visualLine);
    }

    /// <summary>The tokens kept for <paramref name="line"/>, from 0, as <see cref="SemanticTokenLines"/> has them.</summary>
    internal IReadOnlyList<int> TokensOf(int line) => _lines[line]?.Tokens ?? [];

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
        // The shown part of the line: tokens in a long line's hidden tail are not drawn.
        int length = Math.Min(line.Length, VisibleText.LineLimit);
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
    /// Keeps the tokens on their text: one entry per line, the tokens before the edit where they were, those after it
    /// moved with their text, also to another line, and none on changed text. Then asks again once the user pauses.
    /// </summary>
    private void OnBufferChanged(object? sender, DocumentChangeEventArgs e)
    {
        DocumentLine firstLine = _buffer.GetLineByOffset(e.Offset);
        DocumentLine lastLine = _buffer.GetLineByOffset(e.Offset + e.InsertionLength);
        int first = firstLine.LineNumber - 1;
        int last = lastLine.LineNumber - 1;
        int column = e.Offset - firstLine.Offset;
        int delta = _buffer.LineCount - _lines.Count;
        // Where the text after the edit starts: in the old text on the line where the removed text ended, now on the
        // line where the inserted text ends.
        int removedBreaks = last - first - delta;
        LineTokens? head = _lines[first];
        LineTokens? end = _lines[first + removedBreaks];
        int oldEnd = removedBreaks == 0 ? column + e.RemovalLength : ShownLastLineLength(e.RemovedText);
        int newEnd = e.Offset + e.InsertionLength - lastLine.Offset;
        // The entries right after the changed line come and go, so the entries of the lines after keep their tokens.
        if (delta > 0)
            _lines.InsertRange(first + 1, new LineTokens?[delta]);
        else if (delta < 0)
            _lines.RemoveRange(first + 1, -delta);
        for (int i = first; i <= last; i++)
            _lines[i] = null;
        int[] before = head?.Before(column) ?? [];
        int[] after = end?.After(oldEnd, newEnd) ?? [];
        if (first == last)
            _lines[first] = head is null && end is null ? null : new LineTokens([.. before, .. after], _generation);
        else
        {
            _lines[first] = head is null ? null : new LineTokens(before, _generation);
            _lines[last] = end is null ? null : new LineTokens(after, _generation);
        }
        // A handler of the change that ran before this one may have built the changed lines from the old tokens.
        _textView.Redraw(e.Offset, e.InsertionLength);
        Outdate(s_afterEdit);
    }

    /// <summary>
    /// The view shows other lines: asks for their tokens once scrolling pauses, if they need them. While the view
    /// moves, the pause starts again; an edit's longer pause stays.
    /// </summary>
    private void OnVisualLinesChanged(object? sender, EventArgs e)
    {
        int firstShown = Shown().First;
        bool scrolled = firstShown != _firstShown;
        _firstShown = firstShown;
        if (NeedsTokens() && (!_timer.IsEnabled || (scrolled && _timer.Interval == s_afterScroll)))
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
        (int first, int last) = Shown();
        for (int i = first; i <= last; i++)
        {
            if (_lines[i]?.Generation != _generation && _request?.Covers(i) != true)
                return true;
        }
        return false;
    }

    /// <summary>The first and the last line in view, from 0; none while the view has no lines.</summary>
    private (int First, int Last) Shown() =>
        _textView.VisualLinesValid && _textView.VisualLines.Count > 0
            ? (
                _textView.VisualLines[0].FirstDocumentLine.LineNumber - 1,
                _textView.VisualLines[^1].LastDocumentLine.LineNumber - 1
            )
            : (0, -1);

    /// <summary>Asks for the tokens of the lines in view, or of all lines from a server without ranges.</summary>
    private void RequestTokens()
    {
        DocumentLine first = _textView.VisualLines[0].FirstDocumentLine;
        DocumentLine last = _textView.VisualLines[^1].LastDocumentLine;
        _request?.Cancellation.Cancel();
        var cancellation = new CancellationTokenSource();
        int generation = _generation;
        // The answer's line entries are built off the UI thread, also for the whole text.
        Task<Answer?> answer = _document
            .SemanticTokensAsync(
                new TextPosition(first.LineNumber, 1),
                new TextPosition(last.LineNumber, last.Length + 1),
                _buffer.LineCount,
                cancellation.Token
            )
            .ContinueWith(
                tokens => tokens.Result is { } read ? Prepare(read, generation) : null,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default
            );
        var request = new Request(
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
    /// Shows an answer, unless a newer request replaced it, or the text or the server changed since it was asked for:
    /// either cancelled it. Without tokens, from a document without a server or a server that failed, the lines in view
    /// keep what they have until the next edit.
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
        if (request.Answer.Result is not { } answer)
        {
            (int first, int last) = Shown();
            for (int i = first; i <= last; i++)
                _lines[i] = new LineTokens(_lines[i]?.Tokens ?? [], _generation);
            return;
        }
        Show(answer);
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

    /// <summary>The length of the last line of <paramref name="text"/>, after its last line break.</summary>
    /// <summary>
    /// The length of the last line of <paramref name="text"/>, up to <see cref="VisibleText.LineLimit"/>: a line's tokens
    /// start in its shown part, so none starts after a longer stretch. Reads at most that many characters.
    /// </summary>
    internal static int ShownLastLineLength(ITextSource text)
    {
        int length = 0;
        while (
            length < Math.Min(text.TextLength, VisibleText.LineLimit)
            && text.GetCharAt(text.TextLength - 1 - length) is not ('\r' or '\n')
        )
        {
            length++;
        }
        return length;
    }

    /// <summary>
    /// A request for tokens, of the lines from <paramref name="First"/> to <paramref name="Last"/> or more. An edit or a
    /// change of the server cancels it, so that an answer it gets is about the current text.
    /// </summary>
    /// <param name="First">The first line it asks for, from 0.</param>
    /// <param name="Last">The last line it asks for.</param>
    /// <param name="Cancellation">Cancels it.</param>
    /// <param name="Answer">The tokens, once the server answered; null for none.</param>
    private sealed record Request(int First, int Last, CancellationTokenSource Cancellation, Task<Answer?> Answer)
    {
        public bool Covers(int line) => line >= First && line <= Last;
    }

    /// <summary>An answer's line entries, from <paramref name="FirstLine"/> on.</summary>
    /// <param name="Legend">What the tokens' numbers name.</param>
    /// <param name="FirstLine">The first line it covers, from 0.</param>
    /// <param name="Lines">The entries of the lines it covers.</param>
    internal sealed record Answer(SemanticTokensLegend Legend, int FirstLine, List<LineTokens?> Lines);

    /// <summary>A line's tokens, as <see cref="SemanticTokenLines"/> has them.</summary>
    /// <param name="Tokens">Its tokens.</param>
    /// <param name="Generation">The count of edits and server changes that they are current for.</param>
    internal sealed record LineTokens(int[] Tokens, int Generation)
    {
        /// <summary>The tokens that end at or before <paramref name="column"/>, where an edit starts.</summary>
        public int[] Before(int column)
        {
            var before = new List<int>(Tokens.Length);
            for (int i = 0; i < Tokens.Length; i += SemanticTokenLines.Stride)
            {
                if ((long)Tokens[i] + Tokens[i + 1] <= column)
                    before.AddRange(Tokens.AsSpan(i, SemanticTokenLines.Stride));
            }
            return [.. before];
        }

        /// <summary>
        /// The tokens that start at or after <paramref name="column"/>, where an edit ends, moved to start from
        /// <paramref name="newColumn"/> as their text does; those moved out of the shown part of the line go, so that a
        /// line keeps at most one token per character of it, also when lines join.
        /// </summary>
        public int[] After(int column, int newColumn)
        {
            var after = new List<int>(Tokens.Length);
            for (int i = 0; i < Tokens.Length; i += SemanticTokenLines.Stride)
            {
                long moved = (long)Tokens[i] - column + newColumn;
                if (Tokens[i] >= column && moved < VisibleText.LineLimit)
                    after.AddRange([(int)moved, Tokens[i + 1], Tokens[i + 2], Tokens[i + 3]]);
            }
            return [.. after];
        }
    }
}

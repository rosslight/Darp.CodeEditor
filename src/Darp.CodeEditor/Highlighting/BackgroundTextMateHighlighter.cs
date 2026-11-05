using System.Diagnostics;
using Avalonia.Logging;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Loading;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars;

namespace Darp.CodeEditor.Highlighting;

/// <summary>
/// TextMate highlighting of one editor's view of a document: it colors the visible lines in the frame that shows
/// them, and tokenizes the rest in the background, as VS Code does.
/// </summary>
/// <remarks>
/// <para>
/// The UI thread owns a cache with one entry per line: the TextMate state at the end of the line and the line's
/// tokens. Rendering reads that cache. A layout pass tokenizes the invalid lines it shows on the UI thread within a
/// budget of a few milliseconds: exactly when the state at the start of the line is known, otherwise from a guessed
/// start state (the start of the grammar), which the background corrects later. Lines the budget does not reach keep
/// their old colors, or none, until the background reaches them.
/// </para>
/// <para>
/// When lines in or near the view are invalid, the UI thread copies their text and the state before them, and a
/// background task tokenizes them. A change of the text stops it between lines; its results are applied only to the
/// lines above the change. A line is invalid after it was edited, or when the line above ended in a different state
/// than before. Lines below the view are tokenized once they scroll into view, so an edit costs work in proportion to
/// the view. Lines longer than <see cref="MaxTokenizedLineLength"/> are not tokenized, as in VS Code: TextMate's
/// regex matching is quadratic on long lines, and a 20,000-character line takes seconds. The state at their end is
/// unknown, so the line after them starts from the start of the grammar.
/// </para>
/// </remarks>
internal sealed class BackgroundTextMateHighlighter : DocumentColorizingTransformer, IDisposable
{
    public const int MaxTokenizedLineLength = 5_000;

    /// <summary>Lines tokenized while a grammar loads: about what a screen shows.</summary>
    public const int FirstScreenLines = 60;

    /// <summary>Longer lines are left to the background: one regex search over them can exceed the frame budget.</summary>
    private const int MaxSyncLineLength = 1_000;

    private const int MaxLinesPerRequest = 400;
    private const int LinesBeyondView = 40;

    /// <summary>Id 1 is the theme's default foreground: such text keeps the editor's foreground.</summary>
    private const int DefaultForegroundId = 1;

    private static readonly TimeSpan s_backgroundLineTimeLimit = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan s_frameBudget = TimeSpan.FromMilliseconds(4);
    private static readonly long s_frameBudgetTicks = (long)(s_frameBudget.TotalSeconds * Stopwatch.Frequency);

    /// <summary>Lines that compile the most common expressions of a grammar, for a document without text.</summary>
    private static readonly string[] s_warmUpLines = ["x = f(1, \"s\") // c", "{ [ ] } /* c */ 'c' 0.5"];

    private readonly TextView _textView;
    private readonly TextDocument _document;
    private readonly Dispatcher _dispatcher;
    private readonly List<LineEntry> _lines = [];
    private TextMateGrammar? _grammar;

    /// <summary>Counts grammar changes: results of requests made with another grammar are dropped.</summary>
    private int _generation;

    private int _firstInvalid;
    private Request? _request;
    private bool _scheduled;
    private int _firstEditedSinceRequest = int.MaxValue;
    private long _frameDeadline;
    private bool _disposed;

    /// <summary>
    /// Starts highlighting <paramref name="document"/> in <paramref name="textView"/>, with <paramref name="grammar"/>
    /// if one is loaded already, and posts its work to <paramref name="dispatcher"/>.
    /// </summary>
    public BackgroundTextMateHighlighter(
        TextView textView,
        TextDocument document,
        Dispatcher dispatcher,
        TextMateGrammar? grammar
    )
    {
        _textView = textView;
        _document = document;
        _dispatcher = dispatcher;
        _grammar = grammar;
        for (int i = 0; i < document.LineCount; i++)
            _lines.Add(new LineEntry());
        document.Changed += OnDocumentChanged;
        textView.VisualLineConstructionStarting += OnLayoutStarting;
        textView.VisualLinesChanged += OnVisualLinesChanged;
        textView.LineTransformers.Insert(0, this);
        Schedule();
    }

    /// <summary>The grammar the tokens come from; null for none.</summary>
    public TextMateGrammar? Grammar => _grammar;

    /// <summary>
    /// Switches to <paramref name="grammar"/>, or to none, and takes the lines of <paramref name="firstScreen"/> that
    /// <see cref="Prepare"/> tokenized with it, if the text is unchanged since. Lines tokenized with another grammar
    /// lose their tokens: their color ids mean other colors now.
    /// </summary>
    public void SetGrammar(TextMateGrammar? grammar, FirstScreen? firstScreen)
    {
        if (grammar != _grammar)
        {
            _grammar = grammar;
            _generation++;
            _request?.Cancel();
            foreach (LineEntry entry in _lines)
                entry.Tokenized(null, null, exact: false);
            _firstInvalid = 0;
        }
        if (
            grammar is not null
            && firstScreen is { Version: { } version }
            && version.BelongsToSameDocumentAs(_document.Version)
            && version.CompareAge(_document.Version) == 0
        )
        {
            // Lines from the start of the text are exact; lines further down started from a guessed state.
            for (int i = 0; i < firstScreen.Lines.Length && firstScreen.FirstLine + i < _lines.Count; i++)
            {
                LineResult line = firstScreen.Lines[i];
                _lines[firstScreen.FirstLine + i].Tokenized(line.Tokens, line.EndState, firstScreen.FirstLine == 0);
            }
        }
        _textView.Redraw();
        Schedule();
    }

    /// <summary>
    /// Tokenizes the lines of <paramref name="text"/> from <paramref name="firstLine"/>, with a grammar just loaded and
    /// in the background: this compiles the expressions they need there instead of on the UI thread.
    /// </summary>
    public static FirstScreen Prepare(
        TextMateGrammar grammar,
        ITextSource text,
        int firstLine,
        CancellationToken cancellationToken
    )
    {
        var texts = new List<string>(FirstScreenLines);
        using (TextReader reader = text.CreateReader())
        {
            for (int skipped = 0; skipped < firstLine && reader.ReadLine() is not null; skipped++) { }
            while (texts.Count < FirstScreenLines && reader.ReadLine() is { } line)
                texts.Add(line);
        }
        // Without text yet, compile at least the expressions that most lines need.
        if (texts.Count == 0)
            Tokenize(grammar, null, s_warmUpLines, cancellationToken);
        return new FirstScreen(firstLine, Tokenize(grammar, null, [.. texts], cancellationToken), text.Version);
    }

    /// <summary>
    /// Tokenizes <paramref name="texts"/>, one line after another from <paramref name="state"/>, and stops between
    /// lines once <paramref name="cancellationToken"/> is cancelled: the results are the lines done by then.
    /// </summary>
    public static LineResult[] Tokenize(
        TextMateGrammar grammar,
        IStateStack? state,
        string[] texts,
        CancellationToken cancellationToken
    )
    {
        var results = new List<LineResult>(texts.Length);
        foreach (string text in texts)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            // Not tokenized, or out of time: the line keeps the default color, and the state at its end is unknown.
            if (
                text.Length > MaxTokenizedLineLength
                || grammar.TokenizeInBackground(text, state, s_backgroundLineTimeLimit) is not { } result
            )
            {
                state = null;
                results.Add(new LineResult(null, null));
                continue;
            }
            state = result.RuleStack;
            results.Add(new LineResult(state, result.Tokens));
        }
        return [.. results];
    }

    /// <summary>Stops highlighting: no more tokenizing, and the view no longer reads the cache.</summary>
    public void Dispose()
    {
        _disposed = true;
        _request?.Cancel();
        _request = null;
        _document.Changed -= OnDocumentChanged;
        _textView.VisualLineConstructionStarting -= OnLayoutStarting;
        _textView.VisualLinesChanged -= OnVisualLinesChanged;
        _textView.LineTransformers.Remove(this);
        _lines.Clear();
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        int index = line.LineNumber - 1;
        if (_grammar is not { } grammar || index >= _lines.Count)
            return;
        LineEntry entry = _lines[index];
        if (!entry.IsValid)
            TokenizeNow(grammar, line, index, entry);
        if (entry.Tokens is not { } tokens)
            return;
        // Tokens may be from before an edit: clamp them to the line as it is now.
        int length = line.Length;
        for (int i = 0; i < tokens.Length; i += 2)
        {
            int start = Math.Min(tokens[i], length);
            int end = Math.Min(i + 2 < tokens.Length ? tokens[i + 2] : length, length);
            int foreground = EncodedTokenAttributes.GetForeground(tokens[i + 1]);
            if (
                end > start
                && foreground > DefaultForegroundId
                && foreground < grammar.Brushes.Length
                && grammar.Brushes[foreground] is { } brush
            )
            {
                ChangeLinePart(
                    line.Offset + start,
                    line.Offset + end,
                    e => e.TextRunProperties.SetForegroundBrush(brush)
                );
            }
        }
    }

    /// <summary>Starts the UI thread's tokenizing budget for a layout pass.</summary>
    private void OnLayoutStarting(object? sender, VisualLineConstructionStartEventArgs e) =>
        _frameDeadline = Stopwatch.GetTimestamp() + s_frameBudgetTicks;

    private void OnVisualLinesChanged(object? sender, EventArgs e) => Schedule();

    /// <summary>
    /// Tokenizes an invalid line on the UI thread while the layout pass has budget left, so it shows colors in this
    /// frame. Without a known start state, only a line that has no colors at all is tokenized, from a guessed state.
    /// </summary>
    private void TokenizeNow(TextMateGrammar grammar, DocumentLine line, int index, LineEntry entry)
    {
        if (line.Length > MaxSyncLineLength || grammar.TooSlowForUiThread)
            return;
        LineEntry? previous = index > 0 ? _lines[index - 1] : null;
        bool exact = previous is null || previous.IsValid;
        if (!exact && entry.Tokens is not null)
            return;
        long now = Stopwatch.GetTimestamp();
        if (now >= _frameDeadline)
            return;
        // The background holds the grammar for one line at a time, and lets the UI thread go next.
        if (!grammar.TryEnterOnUiThread(Stopwatch.GetElapsedTime(now, _frameDeadline)))
        {
            _frameDeadline = 0;
            return;
        }
        ITokenizeLineResult2 result;
        long start = Stopwatch.GetTimestamp();
        TimeSpan limit = Stopwatch.GetElapsedTime(start, _frameDeadline);
        try
        {
            result = grammar.Grammar.TokenizeLine2(
                new LineText(_document.GetText(line).AsMemory()),
                previous?.EndState,
                limit
            );
        }
        finally
        {
            grammar.ExitOnUiThread();
        }
        // TextMate stops at the time limit without saying so: a result that took that long may be incomplete.
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        if (elapsed >= limit)
        {
            _frameDeadline = 0;
            // It checks the limit only between regex searches; one search ran far past it.
            if (elapsed >= limit + s_frameBudget)
                grammar.TooSlowForUiThread = true;
            return;
        }
        bool endStateChanged = !Equals(entry.EndState, result.RuleStack);
        entry.Tokenized(result.Tokens, result.RuleStack, exact);
        if (!endStateChanged || index + 1 >= _lines.Count)
            return;
        // The next line starts in a different state. It may be built already, from tokens for the old state; it has
        // not been reached in this pass yet, so it can be rebuilt.
        _lines[index + 1].Invalidate();
        _firstInvalid = Math.Min(_firstInvalid, index + 1);
        if (_textView.GetVisualLine(line.LineNumber + 1) is { } next)
            _textView.Redraw(next);
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        // Outdated work stops between lines; the lines it finished above the change still apply.
        _request?.Cancel();
        // Keep one entry per line: entries right after the changed line are inserted or removed, so the entries of
        // all following lines keep their tokens and states.
        DocumentLine firstLine = _document.GetLineByOffset(e.Offset);
        int first = firstLine.LineNumber - 1;
        int lastInserted = _document.GetLineByOffset(e.Offset + e.InsertionLength).LineNumber - 1;
        int delta = _document.LineCount - _lines.Count;
        if (delta > 0)
            _lines.InsertRange(first + 1, Enumerable.Range(0, delta).Select(_ => new LineEntry()));
        else if (delta < 0)
            _lines.RemoveRange(first + 1, -delta);
        for (int i = first; i <= lastInserted; i++)
            _lines[i].Invalidate();
        _firstInvalid = Math.Min(_firstInvalid, first);
        _firstEditedSinceRequest = Math.Min(_firstEditedSinceRequest, first);
        Schedule();
    }

    /// <summary>Requests tokenizing once the current input and layout are done; repeated calls coalesce.</summary>
    private void Schedule()
    {
        if (_scheduled)
            return;
        _scheduled = true;
        _dispatcher.Post(
            () =>
            {
                _scheduled = false;
                if (!_disposed)
                    RequestTokenizing();
            },
            DispatcherPriority.Normal
        );
    }

    private void RequestTokenizing()
    {
        if (_request is not null || _grammar is not { } grammar)
            return;
        while (_firstInvalid < _lines.Count && _lines[_firstInvalid].IsValid)
            _firstInvalid++;
        // Before the first layout, the first screen.
        int lastVisible =
            _textView.VisualLinesValid && _textView.VisualLines.Count > 0
                ? _textView.VisualLines[^1].LastDocumentLine.LineNumber - 1
                : FirstScreenLines - 1;
        int last = Math.Min(_lines.Count - 1, lastVisible + LinesBeyondView);
        if (_firstInvalid > last)
            return;

        int first = _firstInvalid;
        last = Math.Min(last, first + MaxLinesPerRequest - 1);
        IStateStack? startState = first == 0 ? null : _lines[first - 1].EndState;
        var texts = new string[last - first + 1];
        for (int i = 0; i < texts.Length; i++)
            texts[i] = _document.GetText(_document.GetLineByNumber(first + i + 1));
        _firstEditedSinceRequest = int.MaxValue;
        var cancellation = new CancellationTokenSource();
        var request = new Request(
            _generation,
            first,
            cancellation,
            Task.Run(() => Tokenize(grammar, startState, texts, cancellation.Token))
        );
        _request = request;
        request.Results.ContinueWith(
            _ => _dispatcher.Post(() => Apply(request)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    /// <summary>
    /// Applies the results of a finished request, unless a newer one or <see cref="Dispose"/> replaced it. Its failure
    /// goes to the log either way.
    /// </summary>
    private void Apply(Request request)
    {
        if (request.Results.Exception is { } exception)
        {
            // A grammar that fails once fails again on the same lines: the text stays as it is.
            Logger
                .TryGet(LogEventLevel.Error, AssetFiles.LogArea)
                ?.Log(this, "TextMate tokenizing failed: {Exception}", exception);
            if (_request == request)
                _request = null;
            return;
        }
        if (_request != request)
            return;
        _request = null;
        if (request.Generation != _generation)
        {
            Schedule();
            return;
        }

        // Lines at or after a change made meanwhile may have moved or changed: drop their results.
        LineResult[] results = request.Results.Result;
        int first = request.First;
        int applicable = Math.Min(results.Length, _firstEditedSinceRequest - first);
        for (int i = 0; i < applicable; i++)
        {
            LineEntry line = _lines[first + i];
            bool endStateChanged = !Equals(line.EndState, results[i].EndState);
            line.Tokenized(results[i].Tokens, results[i].EndState, exact: true);
            // The next line starts in a different state, so its tokens may be wrong too.
            if (endStateChanged && first + i + 1 < _lines.Count)
                _lines[first + i + 1].Invalidate();
        }
        if (applicable > 0)
        {
            int start = _document.GetLineByNumber(first + 1).Offset;
            DocumentLine lastLine = _document.GetLineByNumber(first + applicable);
            _textView.Redraw(start, lastLine.EndOffset - start);
        }
        Schedule();
    }

    /// <summary>
    /// Lines from <paramref name="First"/> on, tokenized in the background with the grammar of
    /// <paramref name="Generation"/>.
    /// </summary>
    private sealed record Request(
        int Generation,
        int First,
        CancellationTokenSource Cancellation,
        Task<LineResult[]> Results
    )
    {
        public void Cancel() => Cancellation.Cancel();
    }

    private sealed class LineEntry
    {
        public IStateStack? EndState { get; private set; }

        public int[]? Tokens { get; private set; }

        /// <summary>
        /// True when the tokens are exact for the line as it is. Invalid lines keep tokens from before an edit or from
        /// a guessed start state, and their end state, until they are tokenized again.
        /// </summary>
        public bool IsValid { get; private set; }

        public void Tokenized(int[]? tokens, IStateStack? endState, bool exact)
        {
            Tokens = tokens;
            EndState = endState;
            IsValid = exact;
        }

        public void Invalidate() => IsValid = false;
    }
}

/// <summary>One line, tokenized.</summary>
/// <param name="EndState">
/// The state at the end of the line; null when it is unknown, and the next line starts from the start of the grammar.
/// </param>
/// <param name="Tokens">Pairs of start index and encoded attributes, as TextMate's binary tokens; null for none.</param>
internal readonly record struct LineResult(IStateStack? EndState, int[]? Tokens);

/// <summary>The lines from <paramref name="FirstLine"/> of the text at <paramref name="Version"/>, tokenized.</summary>
internal sealed record FirstScreen(int FirstLine, LineResult[] Lines, ITextSourceVersion? Version);

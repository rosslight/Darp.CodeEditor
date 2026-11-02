using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Theming;

namespace Darp.CodeEditor.Highlighting;

/// <summary>
/// TextMate highlighting that colors the visible lines in the frame that shows them, and tokenizes everything else on
/// the thread pool, as VS Code does.
/// </summary>
/// <remarks>
/// <para>
/// The UI thread owns a cache with one entry per document line: the TextMate state at the end of the line and the
/// line's tokens. Rendering reads that cache. A layout pass tokenizes the invalid lines it shows on the UI thread
/// within a budget of a few milliseconds: exactly when the state at the start of the line is known, otherwise from a
/// guessed start state (the start of the grammar), which the background pass corrects later. Lines the budget does not
/// reach keep their old colors, or none, until the background pass reaches them.
/// </para>
/// <para>
/// When lines in or near the viewport are invalid, the UI thread copies their text and the state before them, and a
/// thread-pool task tokenizes them. Results are applied only to lines that no edit touched since the request; edits
/// keep the old tokens in place until new ones arrive. A line is invalid after it was edited, or when the line above
/// ended in a different state than before. Lines below the viewport are tokenized once they scroll into view, so an
/// edit costs work proportional to the viewport. Lines longer than <see cref="MaxTokenizedLineLength"/> are not
/// tokenized (as in VS Code): TextMate's regex matching is quadratic on long lines, and a 20,000 character line takes
/// seconds.
/// </para>
/// <para>
/// A grammar compiles its regular expressions on first use, which takes 10-30 ms for the first screen of a large
/// grammar. Loading therefore also tokenizes the first screen of the document on the thread pool, so the first layout
/// finds its tokens ready and the UI thread only ever tokenizes with compiled expressions.
/// </para>
/// </remarks>
internal sealed class BackgroundTextMateHighlighter : DocumentColorizingTransformer
{
    public const int MaxTokenizedLineLength = 5_000;

    /// <summary>Longer lines are left to the background pass: one regex search over them can exceed the frame budget.</summary>
    private const int MaxSyncLineLength = 1_000;

    /// <summary>Lines tokenized while loading the grammar, about what a screen shows.</summary>
    private const int FirstScreenLines = 60;

    private const int MaxLinesPerRequest = 400;
    private const int LinesBeyondViewport = 40;
    private const int DefaultForegroundId = 1;
    private static readonly TimeSpan s_lineTimeLimit = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan s_frameBudget = TimeSpan.FromMilliseconds(4);
    private static readonly long s_frameBudgetTicks = (long)(s_frameBudget.TotalSeconds * Stopwatch.Frequency);

    /// <summary>Lines that compile the most common expressions when the document is still empty while loading.</summary>
    private static readonly string[] s_warmUpLines = ["x = f(1, \"s\") // c", "{ [ ] } /* c */ 'c' 0.5"];

    private readonly CodeEditor _editor;
    private readonly List<LineEntry> _lines = [];
    private TextDocument? _document;
    private GrammarSource? _source;
    private Task<LoadedTheme>? _theme;
    private LoadedTheme? _appliedTheme;
    private TextMateGrammar? _grammar;
    private Task<LoadedGrammar>? _loading;
    private ITextSource? _loadingText;
    private int _generation;
    private int _firstInvalid;
    private Request? _request;
    private bool _scheduled;
    private int _firstEditedSinceRequest = int.MaxValue;
    private long _frameDeadline;

    /// <summary>
    /// The editor's text color after a theme was applied at the start of this layout pass: AvaloniaEdit read the
    /// previous one for the pass already, so lines built in it get the new one explicitly.
    /// </summary>
    private IBrush? _foregroundOverride;

    public BackgroundTextMateHighlighter(CodeEditor editor)
    {
        _editor = editor;
        TextView textView = editor.TextArea.TextView;
        textView.VisualLineConstructionStarting += (_, _) => OnLayoutStarting();
        textView.VisualLinesChanged += (_, _) => Schedule();
        editor.DocumentChanged += (_, _) => AttachDocument();
        AttachDocument();
    }

    /// <summary>
    /// Raised on the UI thread when tokens start using a theme's colors, so the editor shows the theme's other colors
    /// in the same frame.
    /// </summary>
    public event EventHandler<LoadedTheme>? ThemeApplied;

    /// <summary>
    /// Loads the grammar on the thread pool, with the theme from <see cref="SetTheme"/>; highlighting starts once it
    /// is loaded.
    /// </summary>
    public void SetGrammar(GrammarSource source)
    {
        _source = source;
        // Results of requests with the previous grammar are dropped.
        _generation++;
        _grammar = null;
        Invalidate();
        if (_theme is { } theme)
            Load(source, theme);
    }

    /// <summary>
    /// Switches to the colors of a theme, which may still be loading. The current colors stay until the grammar has
    /// loaded with the new theme, then tokens and <see cref="ThemeApplied"/> switch in the same frame.
    /// </summary>
    public async void SetTheme(Task<LoadedTheme> theme)
    {
        _theme = theme;
        if (_source is { } source)
        {
            Load(source, theme);
            return;
        }
        try
        {
            LoadedTheme loaded = await theme;
            if (_theme == theme)
                ApplyTheme(loaded);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Loading the theme failed: {exception}");
        }
    }

    /// <summary>Loads theme, grammar and the first lines' tokens on the thread pool, without returning in between.</summary>
    private async void Load(GrammarSource source, Task<LoadedTheme> pendingTheme)
    {
        long start = Stopwatch.GetTimestamp();
        // A grammar another editor loaded applies at once; the next layout tokenizes the lines it shows.
        if (
            pendingTheme.IsCompletedSuccessfully
            && TextMateCatalog.TryGetLoadedGrammar(source, pendingTheme.Result, out TextMateGrammar? loadedGrammar)
        )
        {
            Adopt(new LoadedGrammar(loadedGrammar, pendingTheme.Result, 0, [], null));
            _editor.TextArea.TextView.Redraw();
            return;
        }
        // The lines in view, or the first screen, are tokenized with the text as it is when the grammar has loaded.
        _loadingText = _document?.CreateSnapshot();
        TextView textView = _editor.TextArea.TextView;
        int firstLine =
            textView.VisualLinesValid && textView.VisualLines.Count > 0
                ? textView.VisualLines[0].FirstDocumentLine.LineNumber - 1
                : 0;
        Task<LoadedGrammar> loading = Task.Run(async () =>
        {
            LoadedTheme theme = await pendingTheme.ConfigureAwait(false);
            TextMateGrammar? grammar = await TextMateCatalog.GetGrammarAsync(source, theme).ConfigureAwait(false);
            LoadedGrammar loaded = Prepare(grammar, theme, firstLine, () => Volatile.Read(ref _loadingText));
            _editor.ReportTimingFromAnyThread("textmate-grammar-load-thread-pool", Stopwatch.GetElapsedTime(start));
            return loaded;
        });
        _loading = loading;
        try
        {
            LoadedGrammar loaded = await loading;
            // A newer grammar or theme replaced this one, or a layout took it already.
            if (_loading != loading)
                return;
            Adopt(loaded);
            _editor.TextArea.TextView.Redraw();
            _editor.ReportTiming("textmate-grammar-loaded", Stopwatch.GetElapsedTime(start));
        }
        catch (Exception exception)
        {
            if (_loading == loading)
                _loading = null;
            Trace.TraceWarning($"Loading the TextMate grammar failed: {exception}");
        }
    }

    /// <summary>How many of the rendered lines have no tokens yet and show without colors.</summary>
    public int CountVisibleLinesWithoutTokens()
    {
        TextView textView = _editor.TextArea.TextView;
        if (!textView.VisualLinesValid || _source is null)
            return 0;
        int count = 0;
        foreach (VisualLine line in textView.VisualLines)
        {
            int index = line.FirstDocumentLine.LineNumber - 1;
            bool colored = _grammar is not null && index < _lines.Count && _lines[index].Tokens is not null;
            if (!colored && line.FirstDocumentLine.Length > 0)
                count++;
        }
        return count;
    }

    /// <summary>Whether the character at <paramref name="offset"/> is code, comment or string, as last tokenized.</summary>
    public TokenKind TokenKindAt(int offset)
    {
        if (_grammar is null || _document is not { } document || offset < 0 || offset >= document.TextLength)
            return TokenKind.Code;
        DocumentLine line = document.GetLineByOffset(offset);
        if (line.LineNumber - 1 >= _lines.Count || _lines[line.LineNumber - 1].Tokens is not { } tokens)
            return TokenKind.Code;
        int column = offset - line.Offset;
        int i = 0;
        while (i + 2 < tokens.Length && tokens[i + 2] <= column)
            i += 2;
        return EncodedTokenAttributes.GetTokenType(tokens[i + 1]) switch
        {
            1 => TokenKind.Comment,
            2 or 3 => TokenKind.String,
            _ => TokenKind.Code,
        };
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        int index = line.LineNumber - 1;
        if (_grammar is not { } grammar || index >= _lines.Count)
            return;
        LineEntry entry = _lines[index];
        if (_foregroundOverride is { } textColor && line.Length > 0)
            ChangeLinePart(line.Offset, line.EndOffset, e => e.TextRunProperties.SetForegroundBrush(textColor));
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
            // Id 1 is the theme's default foreground: keep the editor's foreground, as AvaloniaEdit.TextMate does.
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

    /// <summary>Starts the budget of a layout pass, and takes a grammar that finished loading meanwhile.</summary>
    private void OnLayoutStarting()
    {
        _frameDeadline = Stopwatch.GetTimestamp() + s_frameBudgetTicks;
        // Results that are ready are used in this frame, without waiting for their continuations to be scheduled. No
        // line of this pass is built yet, so lines built earlier can be rebuilt.
        LoadedTheme? themeBefore = _appliedTheme;
        AdoptIfLoaded();
        _foregroundOverride = _appliedTheme != themeBefore ? _editor.Foreground : null;
        if (_request is { Results.IsCompletedSuccessfully: true } request)
            Apply(request);
    }

    /// <summary>Takes a grammar that finished loading, without waiting for its continuation.</summary>
    private void AdoptIfLoaded()
    {
        if (_loading is not { IsCompletedSuccessfully: true } loading)
            return;
        Adopt(loading.Result);
        _editor.TextArea.TextView.Redraw();
    }

    private void Adopt(LoadedGrammar loaded)
    {
        _loading = null;
        _loadingText = null;
        if (_grammar is not null && _grammar != loaded.Grammar)
        {
            // Tokens of another grammar or theme: their color ids mean other colors now.
            _generation++;
            foreach (LineEntry entry in _lines)
            {
                entry.Tokens = null;
                entry.EndState = null;
                entry.IsValid = false;
            }
            _firstInvalid = 0;
        }
        _grammar = loaded.Grammar;
        // The lines tokenized while loading still apply if the text has not changed since. Lines from the start of the
        // document are exact; lines further down started from a guessed state.
        if (
            loaded.Version is { } version
            && _document?.Version is { } current
            && version.BelongsToSameDocumentAs(current)
            && version.CompareAge(current) == 0
        )
        {
            for (int i = 0; i < loaded.Lines.Length && loaded.FirstLine + i < _lines.Count; i++)
            {
                LineEntry entry = _lines[loaded.FirstLine + i];
                entry.Tokens = loaded.Lines[i].Tokens;
                entry.EndState = loaded.Lines[i].EndState;
                entry.IsValid = loaded.FirstLine == 0;
            }
        }
        ApplyTheme(loaded.Theme);
        Schedule();
    }

    private void ApplyTheme(LoadedTheme theme)
    {
        if (theme == _appliedTheme)
            return;
        _appliedTheme = theme;
        ThemeApplied?.Invoke(this, theme);
    }

    /// <summary>
    /// Tokenizes an invalid line on the UI thread while the layout pass has budget left, so it shows colors in this
    /// frame. Without a known start state, only a line that has no colors at all is tokenized, from a guessed state.
    /// </summary>
    private void TokenizeNow(TextMateGrammar grammar, DocumentLine line, int index, LineEntry entry)
    {
        if (line.Length > MaxSyncLineLength || grammar.TooSlowForUiThread || _document is not { } document)
            return;
        LineEntry? previous = index > 0 ? _lines[index - 1] : null;
        bool exact = previous is null || previous.IsValid;
        if (!exact && entry.Tokens is not null)
            return;
        long now = Stopwatch.GetTimestamp();
        if (now >= _frameDeadline)
            return;
        // The background pass holds the grammar for one line at a time, and lets the UI thread go next.
        if (!grammar.TryEnterOnUiThread(Stopwatch.GetElapsedTime(now, _frameDeadline)))
        {
            _frameDeadline = 0;
            return;
        }
        ITokenizeLineResult2 result;
        TimeSpan limit = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), _frameDeadline);
        long tokenizeStart = Stopwatch.GetTimestamp();
        try
        {
            result = grammar.Grammar.TokenizeLine2(
                new LineText(document.GetText(line).AsMemory()),
                previous?.EndState,
                limit
            );
        }
        finally
        {
            grammar.ExitOnUiThread();
        }
        // TextMate stops at the time limit without saying so: a result that took that long may be incomplete.
        TimeSpan elapsed = Stopwatch.GetElapsedTime(tokenizeStart);
        if (elapsed >= limit)
        {
            _frameDeadline = 0;
            // It checks the limit only between regex searches; one search ran far past it.
            if (elapsed >= limit + s_frameBudget)
                grammar.TooSlowForUiThread = true;
            return;
        }
        bool endStateChanged = !Equals(entry.EndState, result.RuleStack);
        entry.Tokens = result.Tokens;
        entry.EndState = result.RuleStack;
        entry.IsValid = exact;
        if (!endStateChanged || index + 1 >= _lines.Count)
            return;
        // The next line starts in a different state. It may be built already, from tokens for the old state; it has
        // not been reached in this pass yet, so it can be rebuilt.
        _lines[index + 1].IsValid = false;
        _firstInvalid = Math.Min(_firstInvalid, index + 1);
        TextView textView = _editor.TextArea.TextView;
        if (textView.GetVisualLine(line.LineNumber + 1) is { } next)
            textView.Redraw(next);
    }

    private void AttachDocument()
    {
        _document?.Changed -= OnDocumentChanged;
        _document = _editor.Document;
        _document?.Changed += OnDocumentChanged;
        _generation++;
        if (_loading is not null)
            _loadingText = _document?.CreateSnapshot();
        Invalidate();
    }

    private void Invalidate()
    {
        _lines.Clear();
        for (int i = 0; i < (_document?.LineCount ?? 0); i++)
            _lines.Add(new LineEntry());
        _firstInvalid = 0;
        _firstEditedSinceRequest = 0;
        _editor.TextArea.TextView.Redraw();
        Schedule();
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_document is not { } document)
            return;
        // A grammar that finished loading before the text arrived starts tokenizing it now, before the first layout.
        AdoptIfLoaded();
        if (_loading is not null)
            _loadingText = document.CreateSnapshot();
        // Keep one entry per line: entries right after the changed line are inserted or removed, so the entries of
        // all following lines keep their tokens and states.
        int first = document.GetLineByOffset(e.Offset).LineNumber - 1;
        int lastInserted = document.GetLineByOffset(e.Offset + e.InsertionLength).LineNumber - 1;
        int delta = document.LineCount - _lines.Count;
        if (delta > 0)
            _lines.InsertRange(first + 1, Enumerable.Range(0, delta).Select(_ => new LineEntry()));
        else if (delta < 0)
            _lines.RemoveRange(first + 1, -delta);
        for (int i = first; i <= lastInserted; i++)
            _lines[i].IsValid = false;
        _firstInvalid = Math.Min(_firstInvalid, first);
        _firstEditedSinceRequest = Math.Min(_firstEditedSinceRequest, first);
        // New text, such as a file just opened: start now. The first layout of a new window runs in the same
        // dispatcher job, before anything posted, and can show results that are ready by then.
        if (e.Offset == 0 && e.InsertionLength == document.TextLength)
            RequestTokenization();
        else
            Schedule();
    }

    /// <summary>Requests tokenization once the current input and layout are done; repeated calls coalesce.</summary>
    private void Schedule()
    {
        if (_scheduled)
            return;
        _scheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _scheduled = false;
                RequestTokenization();
            },
            DispatcherPriority.Normal
        );
    }

    private void RequestTokenization()
    {
        AdoptIfLoaded();
        if (_request is not null || _grammar is not { } grammar || _document is not { } document)
            return;
        while (_firstInvalid < _lines.Count && _lines[_firstInvalid].IsValid)
            _firstInvalid++;
        // Before the first layout, the first screen.
        TextView textView = _editor.TextArea.TextView;
        int lastVisible =
            textView.VisualLinesValid && textView.VisualLines.Count > 0
                ? textView.VisualLines[^1].LastDocumentLine.LineNumber - 1
                : FirstScreenLines - 1;
        int last = Math.Min(_lines.Count - 1, lastVisible + LinesBeyondViewport);
        if (_firstInvalid > last)
            return;

        int first = _firstInvalid;
        last = Math.Min(last, first + MaxLinesPerRequest - 1);
        IStateStack? startState = first == 0 ? null : _lines[first - 1].EndState;
        var texts = new string[last - first + 1];
        for (int i = 0; i < texts.Length; i++)
            texts[i] = document.GetText(document.GetLineByNumber(first + i + 1));
        _firstEditedSinceRequest = int.MaxValue;
        var request = new Request(_generation, first, Task.Run(() => Tokenize(grammar, startState, texts)));
        _request = request;
        _ = CompleteAsync(request);
    }

    private async Task CompleteAsync(Request request)
    {
        try
        {
            await request.Results;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"TextMate tokenization failed: {exception}");
            if (_request == request)
                _request = null;
            return;
        }
        Apply(request);
    }

    /// <summary>Applies the results of a finished request, once: at the next layout or when its task completes.</summary>
    private void Apply(Request request)
    {
        if (_request != request)
            return;
        _request = null;
        if (request.Generation != _generation)
        {
            Schedule();
            return;
        }

        // Lines at or after an edit made during the request may have moved or changed: drop their results.
        LineResult[] results = request.Results.Result;
        int first = request.First;
        int applicable = Math.Min(results.Length, _firstEditedSinceRequest - first);
        for (int i = 0; i < applicable; i++)
        {
            LineEntry line = _lines[first + i];
            bool endStateChanged = !Equals(line.EndState, results[i].EndState);
            line.EndState = results[i].EndState;
            line.Tokens = results[i].Tokens;
            line.IsValid = true;
            // The next line starts in a different state, so its tokens may be wrong too.
            if (endStateChanged && first + i + 1 < _lines.Count)
                _lines[first + i + 1].IsValid = false;
        }
        if (applicable > 0 && _document is { } document)
        {
            int start = document.GetLineByNumber(first + 1).Offset;
            DocumentLine lastLine = document.GetLineByNumber(first + applicable);
            _editor.TextArea.TextView.Redraw(start, lastLine.EndOffset - start);
        }
        Schedule();
    }

    private static LineResult[] Tokenize(TextMateGrammar grammar, IStateStack? state, string[] texts)
    {
        var results = new LineResult[texts.Length];
        for (int i = 0; i < texts.Length; i++)
        {
            if (texts[i].Length > MaxTokenizedLineLength)
            {
                // Not tokenized: the line shows in the default color and the state passes through unchanged.
                results[i] = new LineResult(state, null);
                continue;
            }
            ITokenizeLineResult2 result = grammar.TokenizeInBackground(texts[i], state, s_lineTimeLimit);
            state = result.RuleStack;
            results[i] = new LineResult(state, result.Tokens);
        }
        return results;
    }

    /// <summary>
    /// Tokenizes the lines in view, from <paramref name="firstLine"/>, with a grammar just loaded: this compiles the
    /// expressions they need here instead of on the UI thread.
    /// </summary>
    private static LoadedGrammar Prepare(
        TextMateGrammar? grammar,
        LoadedTheme theme,
        int firstLine,
        Func<ITextSource?> currentText
    )
    {
        if (grammar is null)
            return new LoadedGrammar(null, theme, 0, [], null);
        // The text may change meanwhile, typically from empty to a whole file: then once more.
        ITextSource? text = null;
        LineResult[] lines = [];
        for (int attempt = 0; attempt < 3 && currentText() is { } current && current != text; attempt++)
        {
            text = current;
            var texts = new List<string>(FirstScreenLines);
            using TextReader reader = text.CreateReader();
            for (int skipped = 0; skipped < firstLine && reader.ReadLine() is not null; skipped++) { }
            while (texts.Count < FirstScreenLines && reader.ReadLine() is { } line)
                texts.Add(line);
            lines = Tokenize(grammar, null, [.. texts]);
        }
        // Without text yet, compile at least the expressions most lines need.
        if (lines.Length == 0)
        {
            foreach (string line in s_warmUpLines)
                Tokenize(grammar, null, [line]);
        }
        return new LoadedGrammar(grammar, theme, firstLine, lines, text?.Version);
    }

    /// <param name="Grammar">Null when there is no grammar for the language.</param>
    /// <param name="Lines">Lines from <paramref name="FirstLine"/> of the text at <paramref name="Version"/>.</param>
    private sealed record LoadedGrammar(
        TextMateGrammar? Grammar,
        LoadedTheme Theme,
        int FirstLine,
        LineResult[] Lines,
        ITextSourceVersion? Version
    );

    private readonly record struct LineResult(IStateStack? EndState, int[]? Tokens);

    /// <param name="First">The index of the first line tokenized.</param>
    private sealed record Request(int Generation, int First, Task<LineResult[]> Results);

    private sealed class LineEntry
    {
        public IStateStack? EndState { get; set; }

        /// <summary>Pairs of start index and encoded attributes, as TextMate's binary tokens.</summary>
        public int[]? Tokens { get; set; }

        /// <summary>
        /// True when the tokens are exact for the line as it is. Invalid lines keep tokens from before an edit or from
        /// a guessed start state, and their end state, until they are tokenized again.
        /// </summary>
        public bool IsValid { get; set; }
    }
}

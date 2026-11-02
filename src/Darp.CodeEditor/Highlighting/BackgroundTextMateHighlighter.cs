using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

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
    private static readonly long s_frameBudgetTicks = Stopwatch.Frequency * 4 / 1000;

    /// <summary>Lines that compile the most common expressions when the document is still empty while loading.</summary>
    private static readonly string[] s_warmUpLines = ["x = f(1, \"s\") // c", "{ [ ] } /* c */ 'c' 0.5"];

    private readonly CodeEditor _editor;
    private readonly IRegistryOptions _registryOptions;
    private readonly List<LineEntry> _lines = [];
    private TextDocument? _document;
    private Grammar? _grammar;
    private Task<LoadedGrammar>? _loading;
    private ITextSource? _loadingText;
    private int _generation;
    private int _firstInvalid;
    private Request? _request;
    private bool _scheduled;
    private int _firstEditedSinceRequest = int.MaxValue;
    private long _frameDeadline;

    public BackgroundTextMateHighlighter(CodeEditor editor, IRegistryOptions registryOptions)
    {
        _editor = editor;
        _registryOptions = registryOptions;
        TextView textView = editor.TextArea.TextView;
        textView.VisualLineConstructionStarting += (_, _) => OnLayoutStarting();
        textView.VisualLinesChanged += (_, _) => Schedule();
        editor.DocumentChanged += (_, _) => AttachDocument();
        AttachDocument();
    }

    /// <summary>Loads the grammar on the thread pool; highlighting starts once it is loaded.</summary>
    /// <param name="scopeName">A scope from the registry options, such as "source.cs".</param>
    /// <param name="grammarFile">Or a .tmLanguage.json file.</param>
    public async void SetGrammar(string? scopeName, string? grammarFile)
    {
        // Results of requests with the previous grammar are dropped.
        _generation++;
        _grammar = null;
        Invalidate();
        long start = Stopwatch.GetTimestamp();
        // The first screen is tokenized with the text as it is when the grammar has loaded.
        _loadingText = _document?.CreateSnapshot();
        IRegistryOptions options = _registryOptions;
        Task<LoadedGrammar> loading = Task.Run(() =>
        {
            LoadedGrammar loaded = Load(options, scopeName, grammarFile, () => Volatile.Read(ref _loadingText));
            _editor.ReportTimingFromAnyThread("textmate-grammar-load-thread-pool", Stopwatch.GetElapsedTime(start));
            return loaded;
        });
        _loading = loading;
        try
        {
            LoadedGrammar loaded = await loading;
            // A newer grammar replaced this one, or a layout took it already.
            if (_loading != loading)
                return;
            Adopt(loaded);
            _editor.TextArea.TextView.Redraw();
            _editor.ReportTiming("textmate-grammar-loaded", Stopwatch.GetElapsedTime(start));
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Loading the TextMate grammar failed: {exception}");
        }
    }

    /// <summary>How many of the rendered lines have no tokens yet and show without colors.</summary>
    public int CountVisibleLinesWithoutTokens()
    {
        TextView textView = _editor.TextArea.TextView;
        if (!textView.VisualLinesValid || _grammar is null)
            return 0;
        int count = 0;
        foreach (VisualLine line in textView.VisualLines)
        {
            int index = line.FirstDocumentLine.LineNumber - 1;
            if (index < _lines.Count && _lines[index].Tokens is null && line.FirstDocumentLine.Length > 0)
                count++;
        }
        return count;
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
        if (_grammar is null && _loading is { IsCompletedSuccessfully: true } loading)
        {
            Adopt(loading.Result);
            _editor.TextArea.TextView.Redraw();
        }
        if (_request is { Results.IsCompletedSuccessfully: true } request)
            Apply(request);
    }

    private void Adopt(LoadedGrammar loaded)
    {
        _loading = null;
        _loadingText = null;
        _grammar = loaded.Grammar;
        // The first screen tokenized while loading still applies if the text has not changed since.
        if (
            loaded.Version is { } version
            && _document?.Version is { } current
            && version.BelongsToSameDocumentAs(current)
            && version.CompareAge(current) == 0
        )
        {
            for (int i = 0; i < loaded.FirstLines.Length && i < _lines.Count; i++)
            {
                LineEntry entry = _lines[i];
                entry.Tokens = loaded.FirstLines[i].Tokens;
                entry.EndState = loaded.FirstLines[i].EndState;
                entry.IsValid = true;
            }
        }
        Schedule();
    }

    /// <summary>
    /// Tokenizes an invalid line on the UI thread while the layout pass has budget left, so it shows colors in this
    /// frame. Without a known start state, only a line that has no colors at all is tokenized, from a guessed state.
    /// </summary>
    private void TokenizeNow(Grammar grammar, DocumentLine line, int index, LineEntry entry)
    {
        if (line.Length > MaxSyncLineLength || _document is not { } document)
            return;
        LineEntry? previous = index > 0 ? _lines[index - 1] : null;
        bool exact = previous is null || previous.IsValid;
        if (!exact && entry.Tokens is not null)
            return;
        long now = Stopwatch.GetTimestamp();
        if (now >= _frameDeadline)
            return;
        // The background pass holds the grammar for one line at a time.
        if (!Monitor.TryEnter(grammar.Gate, Stopwatch.GetElapsedTime(now, _frameDeadline)))
        {
            _frameDeadline = 0;
            return;
        }
        ITokenizeLineResult2 result;
        TimeSpan limit = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), _frameDeadline);
        long tokenizeStart = Stopwatch.GetTimestamp();
        try
        {
            result = grammar.TextMateGrammar.TokenizeLine2(
                new LineText(document.GetText(line).AsMemory()),
                previous?.EndState,
                limit
            );
        }
        finally
        {
            Monitor.Exit(grammar.Gate);
        }
        // TextMate stops at the time limit without saying so: a result that took that long may be incomplete.
        if (Stopwatch.GetElapsedTime(tokenizeStart) >= limit)
        {
            _frameDeadline = 0;
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
        if (_loading is { IsCompletedSuccessfully: true } loaded)
            Adopt(loaded.Result);
        else if (_loading is not null)
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
        if (_grammar is null && _loading is { IsCompletedSuccessfully: true } loading)
            Adopt(loading.Result);
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

    private static LineResult[] Tokenize(Grammar grammar, IStateStack? state, string[] texts)
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
            ITokenizeLineResult2 result;
            lock (grammar.Gate)
                result = grammar.TextMateGrammar.TokenizeLine2(new LineText(texts[i].AsMemory()), state, s_lineTimeLimit);
            state = result.RuleStack;
            results[i] = new LineResult(state, result.Tokens);
        }
        return results;
    }

    private static LoadedGrammar Load(
        IRegistryOptions options,
        string? scopeName,
        string? grammarFile,
        Func<ITextSource?> currentText
    )
    {
        var registry = new Registry(options);
        IGrammar textMateGrammar = grammarFile is not null
            ? registry.LoadGrammarFromPathSync(grammarFile, 0, null)
            : registry.LoadGrammar(scopeName);
        Theme theme = registry.GetTheme();
        ICollection<string> colors = theme.GetColorMap();
        var brushes = new IBrush?[colors.Count + 1];
        foreach (string color in colors)
        {
            int id = theme.GetColorId(color);
            if (id >= 0 && id < brushes.Length)
                brushes[id] = new ImmutableSolidColorBrush(CodeEditor.ParseThemeColor(color));
        }
        var grammar = new Grammar(textMateGrammar, brushes);

        // Tokenizing the first screen compiles the expressions it needs, here instead of on the UI thread. The text may
        // change meanwhile, typically from empty to a whole file: then once more.
        ITextSource? text = null;
        LineResult[] firstLines = [];
        for (int attempt = 0; attempt < 3 && currentText() is { } current && current != text; attempt++)
        {
            text = current;
            var lines = new List<string>(FirstScreenLines);
            using TextReader reader = text.CreateReader();
            while (lines.Count < FirstScreenLines && reader.ReadLine() is { } line)
                lines.Add(line);
            firstLines = Tokenize(grammar, null, [.. lines]);
        }
        // Without text yet, compile at least the expressions most lines need.
        if (firstLines.Length == 0)
        {
            foreach (string line in s_warmUpLines)
                Tokenize(grammar, null, [line]);
        }
        return new LoadedGrammar(grammar, firstLines, text?.Version);
    }

    /// <param name="Gate">Held while tokenizing: a grammar compiles and caches its expressions as it goes.</param>
    private sealed record Grammar(IGrammar TextMateGrammar, IBrush?[] Brushes)
    {
        public object Gate { get; } = new();
    }

    /// <param name="FirstLines">The first lines of the text at <paramref name="Version"/>.</param>
    private sealed record LoadedGrammar(Grammar Grammar, LineResult[] FirstLines, ITextSourceVersion? Version);

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

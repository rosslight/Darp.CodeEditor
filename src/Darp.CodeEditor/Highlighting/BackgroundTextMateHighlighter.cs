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
/// TextMate highlighting that never tokenizes or loads grammars on the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// The UI thread owns a cache with one entry per document line: the TextMate state at the end of the line and the
/// line's tokens. Rendering only reads that cache. When lines in or near the viewport are invalid, the UI thread copies
/// their text and the state before them, and a thread-pool task tokenizes them. Results are applied only to lines that
/// no edit touched since the request; edits keep the old tokens in place until new ones arrive.
/// </para>
/// <para>
/// A line is invalid after it was edited, or when the line above ended in a different state than before. Lines below
/// the viewport are tokenized once they scroll into view, so an edit costs work proportional to the viewport.
/// Lines longer than <see cref="MaxTokenizedLineLength"/> are not tokenized (as in VS Code): TextMate's regex matching
/// is quadratic on long lines, and a 20,000 character line takes seconds.
/// </para>
/// </remarks>
internal sealed class BackgroundTextMateHighlighter : DocumentColorizingTransformer
{
    public const int MaxTokenizedLineLength = 5_000;
    private const int MaxLinesPerRequest = 400;
    private const int LinesBeyondViewport = 40;
    private const int DefaultForegroundId = 1;
    private static readonly TimeSpan s_lineTimeLimit = TimeSpan.FromMilliseconds(200);

    private readonly CodeEditor _editor;
    private readonly IRegistryOptions _registryOptions;
    private readonly List<LineEntry> _lines = [];
    private TextDocument? _document;
    private Grammar? _grammar;
    private int _generation;
    private int _firstInvalid;
    private bool _requestInFlight;
    private bool _scheduled;
    private int _firstEditedSinceRequest = int.MaxValue;

    public BackgroundTextMateHighlighter(CodeEditor editor, IRegistryOptions registryOptions)
    {
        _editor = editor;
        _registryOptions = registryOptions;
        editor.TextArea.TextView.VisualLinesChanged += (_, _) => Schedule();
        editor.DocumentChanged += (_, _) => AttachDocument();
        AttachDocument();
    }

    /// <summary>Loads the grammar on the thread pool; highlighting starts once it is loaded.</summary>
    /// <param name="scopeName">A scope from the registry options, such as "source.cs".</param>
    /// <param name="grammarFile">Or a .tmLanguage.json file.</param>
    public async void SetGrammar(string? scopeName, string? grammarFile)
    {
        int generation = ++_generation;
        _grammar = null;
        Invalidate();
        long start = Stopwatch.GetTimestamp();
        try
        {
            Grammar grammar = await Task.Run(() =>
            {
                Grammar loaded = Load(_registryOptions, scopeName, grammarFile);
                _editor.ReportTimingFromAnyThread("textmate-grammar-load-thread-pool", Stopwatch.GetElapsedTime(start));
                return loaded;
            });
            if (generation != _generation)
                return;
            _grammar = grammar;
            _editor.ReportTiming("textmate-grammar-loaded", Stopwatch.GetElapsedTime(start));
            Schedule();
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
        if (_grammar is not { } grammar || index >= _lines.Count || _lines[index].Tokens is not { } tokens)
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

    private void AttachDocument()
    {
        _document?.Changed -= OnDocumentChanged;
        _document = _editor.Document;
        _document?.Changed += OnDocumentChanged;
        _generation++;
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
        TextView textView = _editor.TextArea.TextView;
        if (
            _requestInFlight
            || _grammar is not { } grammar
            || _document is not { } document
            || !textView.VisualLinesValid
            || textView.VisualLines.Count == 0
        )
            return;
        while (_firstInvalid < _lines.Count && _lines[_firstInvalid].IsValid)
            _firstInvalid++;
        int last = Math.Min(
            _lines.Count - 1,
            textView.VisualLines[^1].LastDocumentLine.LineNumber - 1 + LinesBeyondViewport
        );
        if (_firstInvalid > last)
            return;

        int first = _firstInvalid;
        last = Math.Min(last, first + MaxLinesPerRequest - 1);
        IStateStack? startState = first == 0 ? null : _lines[first - 1].EndState;
        var texts = new string[last - first + 1];
        for (int i = 0; i < texts.Length; i++)
            texts[i] = document.GetText(document.GetLineByNumber(first + i + 1));
        _requestInFlight = true;
        _firstEditedSinceRequest = int.MaxValue;
        _ = TokenizeAsync(grammar, _generation, first, startState, texts);
    }

    private async Task TokenizeAsync(
        Grammar grammar,
        int generation,
        int first,
        IStateStack? startState,
        string[] texts
    )
    {
        LineResult[] results;
        try
        {
            results = await Task.Run(() => Tokenize(grammar.TextMateGrammar, startState, texts));
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"TextMate tokenization failed: {exception}");
            return;
        }
        finally
        {
            _requestInFlight = false;
        }
        if (generation != _generation)
        {
            Schedule();
            return;
        }

        // Lines at or after an edit made during the request may have moved or changed: drop their results.
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

    private static LineResult[] Tokenize(IGrammar grammar, IStateStack? state, string[] texts)
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
            ITokenizeLineResult2 result = grammar.TokenizeLine2(
                new LineText(texts[i].AsMemory()),
                state,
                s_lineTimeLimit
            );
            state = result.RuleStack;
            results[i] = new LineResult(state, result.Tokens);
        }
        return results;
    }

    private static Grammar Load(IRegistryOptions options, string? scopeName, string? grammarFile)
    {
        var registry = new Registry(options);
        IGrammar grammar = grammarFile is not null
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
        return new Grammar(grammar, brushes);
    }

    private sealed record Grammar(IGrammar TextMateGrammar, IBrush?[] Brushes);

    private readonly record struct LineResult(IStateStack? EndState, int[]? Tokens);

    private sealed class LineEntry
    {
        public IStateStack? EndState { get; set; }

        /// <summary>Pairs of start index and encoded attributes, as TextMate's binary tokens.</summary>
        public int[]? Tokens { get; set; }

        public bool IsValid { get; set; }
    }
}

// Left out of the baseline's build while the target branch has no highlighter: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using BenchmarkDotNet.Attributes;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;
using TextMateSharp.Grammars;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// TextMate highlighting of 5,000 lines of JSON with a grammar whose expressions are compiled: the background's
/// throughput, and the UI thread's work for one screen.
/// </summary>
[MemoryDiagnoser]
[NotInBaseline]
[WarmupCount(2)]
[IterationCount(10)]
public class HighlightingBenchmarks
{
    private const int Lines = 5000;

    /// <summary>About what an editor shows at once.</summary>
    private const int ScreenLines = 50;

    private static readonly TimeSpan s_frameBudget = TimeSpan.FromMilliseconds(4);

    private TextMateGrammar? _grammar;
    private string[] _lines = [];

    /// <summary>The state at the end of the line above the screen, in the middle of the text.</summary>
    private IStateStack? _screenStart;

    [GlobalSetup]
    public void LoadGrammar()
    {
        ColorThemeFile theme =
            CodeEditorColorTheme.LightPlus.Loaded.GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("Light+ did not load.");
        TextMateGrammar grammar = TextMateGrammar.Load(
            theme,
            CodeLanguage.Json.Loaded.GetAwaiter().GetResult().Grammar
                ?? throw new InvalidOperationException("JSON did not load.")
        );
        _lines = Json().Split('\n');
        // Compiles the grammar's expressions, as the first screen does when a grammar loads.
        LineResult[] all = BackgroundTextMateHighlighter.Tokenize(grammar, null, _lines, CancellationToken.None);
        if (all.Length != _lines.Length || all.Any(line => line.Tokens is null))
            throw new InvalidOperationException("JSON did not tokenize every line.");
        _screenStart = all[Lines / 2 - 1].EndState;
        _grammar = grammar;
    }

    private TextMateGrammar Grammar => _grammar ?? throw new InvalidOperationException("Not set up.");

    /// <summary>
    /// The background's throughput with compiled expressions: all 5,000 lines, one after another, as requests tokenize
    /// them. Fails if a line ran out of time.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("CI")]
    public int TokenizeAllLinesWarm()
    {
        LineResult[] lines = BackgroundTextMateHighlighter.Tokenize(Grammar, null, _lines, CancellationToken.None);
        if (lines.Length != _lines.Length || Array.Exists(lines, line => line.Tokens is null))
            throw new InvalidOperationException("A line ran out of time.");
        return lines.Length;
    }

    /// <summary>
    /// The UI thread's tokenizing for one screen of invalid lines, each from the state the line above ends in, without
    /// a time limit, so that every line is complete. A layout pass may spend 4 ms on it; with a limit, one pause of the
    /// process would cut a measured run short.
    /// </summary>
    [Benchmark]
    public int TokenizeScreenWarm()
    {
        TextMateGrammar grammar = Grammar;
        IStateStack? state = _screenStart;
        int tokens = 0;
        for (int i = Lines / 2; i < Lines / 2 + ScreenLines; i++)
        {
            if (!grammar.TryEnterOnUiThread(s_frameBudget))
                throw new InvalidOperationException("The grammar was busy.");
            try
            {
                ITokenizeLineResult2 result = grammar.Grammar.TokenizeLine2(
                    new LineText(_lines[i].AsMemory()),
                    state,
                    TimeSpan.MaxValue
                );
                state = result.RuleStack;
                tokens += result.Tokens.Length;
            }
            finally
            {
                grammar.ExitOnUiThread();
            }
        }
        return tokens;
    }

    private static string Json() =>
        "{\n"
        + string.Concat(Enumerable.Range(2, Lines - 3).Select(i => $"  \"key{i}\": [\"value {i}\", {i}, true],\n"))
        + "  \"end\": null\n}";
}
#endif

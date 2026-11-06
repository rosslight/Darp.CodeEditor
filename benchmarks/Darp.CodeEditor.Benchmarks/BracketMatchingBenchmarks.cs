// Left out of the baseline's build while the target branch has no editing helpers: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using AvaloniaEdit.Document;
using BenchmarkDotNet.Attributes;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// The longest search for a matching bracket, which runs on the UI thread when the caret moves next to a bracket: from
/// the opening bracket of 5,000 lines of JSON, with brackets in strings, through as many characters as the search reads
/// (<see cref="BracketMatcher.MaxDistance"/>), without finding the match at the end.
/// </summary>
[MemoryDiagnoser]
[NotInBaseline]
[WarmupCount(2)]
[IterationCount(10)]
public class BracketMatchingBenchmarks
{
    private const int Lines = 5000;

    private TextDocument? _document;
    private int[]?[] _tokens = [];
    private IReadOnlyList<CharacterPair> _brackets = [];

    [GlobalSetup]
    public void SetUp()
    {
        ColorThemeFile theme =
            CodeEditorColorTheme.LightPlus.Loaded.GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("Light+ did not load.");
        LoadedLanguage json = CodeLanguage.Json.Loaded.GetAwaiter().GetResult();
        TextMateGrammar grammar = TextMateGrammar.Load(
            theme,
            json.Grammar ?? throw new InvalidOperationException("JSON did not load.")
        );
        string[] lines =
        [
            "[",
            .. Enumerable.Range(1, Lines - 2).Select(i => $"  [\"key{i} (]\", {i}, {{}}],"),
            "  []",
            "]",
        ];
        if (lines.Sum(line => line.Length + 1) < BracketMatcher.MaxDistance)
            throw new InvalidOperationException("The match must be further away than the search reads.");
        _document = new TextDocument(string.Join('\n', lines));
        _tokens =
        [
            .. BackgroundTextMateHighlighter
                .Tokenize(grammar, null, lines, CancellationToken.None)
                .Select(l => l.Tokens),
        ];
        _brackets = json.Configuration.Brackets;
    }

    [Benchmark]
    public (int, int)? SearchAsFarAsTheLimit()
    {
        TextDocument document = _document ?? throw new InvalidOperationException("Not set up.");
        return BracketMatcher.FindPair(document, 0, _brackets, line => _tokens[line - 1]);
    }
}
#endif

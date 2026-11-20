using BenchmarkDotNet.Attributes;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// Ranking a list off the UI thread in the worst case of matching that tolerates typos: as many items as are matched
/// so, 2,000, of 128 characters each, and a typed word of 63 characters that every label holds in order, so that the
/// tables would fill far more cells than a ranking's budget, and the ranking finds that out and scores the cheaper way.
/// Not in the CI category: the list shows the previous result meanwhile, and <see cref="CompletionLongLabelBenchmarks"/>
/// measures the same up to a frame.
/// </summary>
[WarmupCount(3)]
[IterationCount(10)]
public class CompletionMatcherBenchmarks
{
    private const int Items = 2000;

    private readonly CompletionMatcher _matcher = new();

    private CompletionCandidate[] _candidates = [];

    /// <summary>The most that a ranking scores by tables: labels of 40 characters, as the fake server makes them.</summary>
    private CompletionCandidate[] _byTables = [];

    /// <summary>Filter texts that are what was typed, and labels of 128 characters that lack its first character.</summary>
    private CompletionCandidate[] _otherLabels = [];

    [GlobalSetup]
    public void SetUp()
    {
        string typedX = "x" + string.Concat(Enumerable.Repeat("ab", 31));
        _otherLabels =
        [
            .. Enumerable
                .Range(0, Items)
                .Select(i =>
                {
                    string label = $"y{i}";
                    label += string.Concat(Enumerable.Range(0, 128 - label.Length).Select(c => c % 2 == 0 ? 'a' : 'b'));
                    return new CompletionCandidate(typedX, label, typedX, FilterTextIsLabel: false);
                }),
        ];
        string[] words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta"];
        _byTables =
        [
            .. Enumerable
                .Range(0, Items)
                .Select(i =>
                {
                    string label = $"{words[i % words.Length]}{i}";
                    label += string.Concat(Enumerable.Range(0, 40 - label.Length).Select(c => c % 2 == 0 ? 'a' : 'b'));
                    return new CompletionCandidate("aabbabaa", label, label, FilterTextIsLabel: true);
                }),
        ];
        string typed = string.Concat(Enumerable.Repeat("ab", 32))[..63];
        _candidates =
        [
            .. Enumerable
                .Range(0, Items)
                .Select(i =>
                {
                    string label = $"alpha{i}";
                    label += string.Concat(Enumerable.Range(0, 128 - label.Length).Select(c => c % 2 == 0 ? 'a' : 'b'));
                    return new CompletionCandidate(typed, label, label, FilterTextIsLabel: true);
                }),
        ];
    }

    [Benchmark]
    public int RankTheWorstCaseOfTheMostItemsMatchedWithTyposTolerated() => _matcher.Rank(_candidates).Count;

    /// <summary>
    /// The most that a ranking scores by tables, as in <see cref="CompletionTableBudgetBenchmarks"/>: 2,000
    /// labels of 40 characters against 8 typed characters, with their swaps, just within the budget of cells.
    /// </summary>
    [Benchmark]
    public int RankTheMostThatTablesScore() => _matcher.Rank(_byTables).Count;

    /// <summary>
    /// 2,000 items whose filter texts, which the typed text matches whole, differ from their labels of 128 characters,
    /// as a server's may: the label shows its matched characters, found from the second typed character on, as the
    /// first is not in it, so that its table is long.
    /// </summary>
    [Benchmark]
    public int RankFilterTextsThatDifferFromTheirLongLabels() => _matcher.Rank(_otherLabels).Count;
}

using BenchmarkDotNet.Attributes;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// Ranking a list off the UI thread in the worst case of matching that tolerates typos: as many items as are matched
/// so, 2,000, of 128 characters each, and a typed word of 63 characters that every label holds in order, also with the
/// neighbouring characters that matching swaps, so that each match runs fully, for the word and for each swap. Against
/// a target branch whose matching is strict, it measures what tolerating typos costs. Not in the CI category: no budget
/// names this case, and the list shows the previous result meanwhile.
/// </summary>
[WarmupCount(3)]
[IterationCount(10)]
public class CompletionMatcherBenchmarks
{
    private const int Items = 2000;

    private readonly CompletionMatcher _matcher = new();

    private CompletionCandidate[] _candidates = [];

    [GlobalSetup]
    public void SetUp()
    {
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
}

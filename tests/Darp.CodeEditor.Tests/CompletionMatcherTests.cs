using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The ranking of completion items that the editor and the C# script server share: VS Code's fuzzy matching, its order,
/// and the label's characters that matched.
/// </summary>
public sealed class CompletionMatcherTests
{
    private readonly CompletionMatcher _matcher = new();

    [Fact]
    public void Typed_characters_match_in_order_ignoring_case_and_the_first_at_a_word_start()
    {
        _matcher.Match("fb", "FooBar", "FooBar", true).ShouldNotBeNull().Highlights.ShouldBe([0, 3]);
        _matcher.Match("FOOB", "fooBar", "fooBar", true).ShouldNotBeNull().Highlights.ShouldBe([0, 1, 2, 3]);
        _matcher.Match("lin", "write_line", "write_line", true).ShouldNotBeNull().Highlights.ShouldBe([6, 7, 8]);
        // Out of order, missing, or starting inside a word.
        _matcher.Match("bf", "FooBar", "FooBar", true).ShouldBeNull();
        _matcher.Match("fx", "FooBar", "FooBar", true).ShouldBeNull();
        _matcher.Match("oo", "FooBar", "FooBar", true).ShouldBeNull();
    }

    [Fact]
    public void Prefixes_rank_first_then_word_starts_and_equal_scores_keep_the_items_order()
    {
        string[] labels = ["readConsole", "Console", "console", "consoleKey", "ToConsoleString", "cos"];

        List<RankedCompletion> ranked = _matcher.Rank(
            [.. labels.Select(l => new CompletionCandidate("Cons", l, l, true))],
            TestContext.Current.CancellationToken
        );

        // A prefix in the typed case first; then in the other case, where the longer word keeps its place; then capitals
        // inside a word, the fewer characters skipped before them the better. "cos" lacks the "n".
        ranked
            .Select(r => labels[r.Index])
            .ShouldBe(["Console", "console", "consoleKey", "ToConsoleString", "readConsole"]);
        _matcher
            .Rank([new("x", "x1", "x1", true), new("x", "x2", "x2", true)], TestContext.Current.CancellationToken)
            .Select(r => r.Index)
            .ShouldBe([0, 1]);
    }

    [Fact]
    public void A_filter_text_decides_the_match_and_the_label_shows_what_matched_in_it()
    {
        // As a server offers "Console.WriteLine" for "wl", filtering by the method's name.
        (int score, int[] highlights) = _matcher.Match("wl", "Console.WriteLine", "WriteLine", false).ShouldNotBeNull();

        highlights.ShouldBe([8, 13]);
        score.ShouldBe(_matcher.Match("wl", "WriteLine", "WriteLine", true).ShouldNotBeNull().Score);
        _matcher.Match("co", "Console.WriteLine", "WriteLine", false).ShouldBeNull();
    }

    [Fact]
    public void A_label_shows_the_filter_texts_matches_only_when_the_whole_filter_text_is_the_label()
    {
        // Alike in their first 128 characters, not in their last: VS Code's highlights come from the label itself.
        string label = "BacaA" + new string('x', 123) + "2";
        string filterText = "BACAA" + new string('x', 123) + "1";

        (_, int[] highlights) = _matcher.Match("BAa", label, filterText, filterTextIsLabel: false).ShouldNotBeNull();

        highlights.ShouldBe([0, 1, 4]);
        _matcher
            .Match("BAa", label, label.ToUpperInvariant(), filterTextIsLabel: true)
            .ShouldNotBeNull()
            .Highlights.ShouldBe([0, 1, 3]);
    }

    [Fact]
    public void Matching_a_label_of_millions_of_characters_reads_only_its_first_128()
    {
        // A server may send labels of any length; committing matches one on the UI thread.
        string label = "Console" + new string('x', 10_000_000);
        string filterText = "Console" + new string('y', 10_000_000);
        _matcher.Match("co", label, label, true);
        _matcher.Match("co", label, filterText, false);

        long before = GC.GetAllocatedBytesForCurrentThread();
        (_, int[] highlights) = _matcher.Match("co", label, label, true).ShouldNotBeNull();
        (_, int[] filtered) = _matcher.Match("co", label, filterText, false).ShouldNotBeNull();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        highlights.ShouldBe([0, 1]);
        filtered.ShouldBe([0, 1]);
        // The labels alone take 20 MB each; matching them takes a few hundred bytes.
        allocated.ShouldBeLessThan(10_000);
    }

    [Fact]
    public void Nothing_typed_matches_every_item_in_their_order()
    {
        CompletionCandidate[] items = [new("", "b", "b", true), new("", "a", "a", true), new(" \t", "c", "c", true)];

        List<RankedCompletion> ranked = _matcher.Rank(items, TestContext.Current.CancellationToken);

        ranked.Select(r => r.Index).ShouldBe([0, 1, 2]);
        ranked.ShouldAllBe(r => r.Highlights.Length == 0);
    }

    [Fact]
    public void Ranking_stops_once_cancelled()
    {
        CompletionCandidate[] items =
        [
            .. Enumerable.Range(0, 1000).Select(i => new CompletionCandidate("a", $"a{i}", $"a{i}", true)),
        ];

        Should.Throw<OperationCanceledException>(() => _matcher.Rank(items, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Items_sort_by_sort_text_ignoring_case_then_by_label_then_by_kind()
    {
        CompletionSortKey[] keys = [new("b", "a", 1), new("A", "z", 1), new("a", "y", 2), new("a", "y", 1)];

        int[] order = [.. Enumerable.Range(0, keys.Length).OrderBy(i => keys[i])];

        order.ShouldBe([3, 2, 1, 0]);
    }

    [Fact]
    public void Matching_gracefully_tolerates_two_neighbouring_characters_swapped_at_a_cost_of_3()
    {
        _matcher.Match("prnit", "print", "print", true).ShouldBeNull();

        (int score, int[] highlights) = _matcher
            .Match("prnit", "print", "print", true, graceful: true)
            .ShouldNotBeNull();

        highlights.ShouldBe([0, 1, 2, 3, 4]);
        score.ShouldBe(_matcher.Match("print", "print", "print", true).ShouldNotBeNull().Score - 3);
    }

    [Fact]
    public void Matching_gracefully_compares_the_swaps_with_the_text_as_typed_as_VS_Code_does()
    {
        // "cno" matches "console" as typed, at -8; swapped to "con", it matches better, at 18 less 3, as in VS Code.
        _matcher.Match("cno", "console", "console", true).ShouldNotBeNull().Score.ShouldBe(-8);
        (int score, int[] highlights) = _matcher
            .Match("cno", "console", "console", true, graceful: true)
            .ShouldNotBeNull();

        score.ShouldBe(15);
        highlights.ShouldBe([0, 1, 2]);
    }

    [Fact]
    public void Past_its_budget_a_ranking_swaps_only_for_an_item_that_the_text_as_typed_does_not_match()
    {
        var spent = new CompletionMatcher(cellsPerRank: 0);

        List<RankedCompletion> ranked = spent.Rank(
            [new("cno", "console", "console", true), new("prnit", "print", "print", true)],
            TestContext.Current.CancellationToken
        );

        ranked.Single(r => r.Index == 0).Highlights.ShouldBe([0, 2, 4]);
        ranked.Single(r => r.Index == 0).Score.ShouldBe(-8);
        ranked.Single(r => r.Index == 1).Highlights.ShouldBe([0, 1, 2, 3, 4]);
    }

    [Fact]
    public void A_ranking_scores_alike_items_alike_whatever_its_budget()
    {
        // Long, as a server's names may be: the table finds a better way than one pass does.
        string typed = "aabb" + string.Concat(Enumerable.Repeat("ab", 29));
        string label = "alpha0" + string.Concat(Enumerable.Range(0, 122).Select(c => c % 2 == 0 ? 'a' : 'b'));
        CompletionCandidate[] items = [.. Enumerable.Repeat(new CompletionCandidate(typed, label, label, true), 100)];
        int exact = _matcher.Rank(items[..1], TestContext.Current.CancellationToken).Single().Score;
        int cheaper = new CompletionMatcher(cellsPerRank: 0)
            .Rank(items[..1], TestContext.Current.CancellationToken)
            .Single()
            .Score;
        exact.ShouldNotBe(cheaper);
        long one = new FuzzyScorer().Cells(typed, 0, label);

        // A budget for half of them: the whole ranking is scored the cheaper way.
        List<RankedCompletion> ranked = new CompletionMatcher(cellsPerRank: 50 * one).Rank(
            items,
            TestContext.Current.CancellationToken
        );

        ranked.Select(r => r.Score).ShouldAllBe(score => score == cheaper);
        ranked.Select(r => r.Index).ShouldBe(Enumerable.Range(0, 100));
    }

    [Theory]
    [InlineData("print", "print")]
    [InlineData("pri", "print")]
    [InlineData("Pri", "print")]
    [InlineData("fb", "FooBar")]
    [InlineData("lin", "write_line")]
    [InlineData("ab", "x_ab_cd")]
    public void Past_its_budget_a_ranking_scores_the_way_that_one_pass_finds_as_the_table_scores_it(
        string typed,
        string label
    )
    {
        var spent = new CompletionMatcher(cellsPerRank: 0);
        CompletionCandidate[] items = [new(typed, label, label, true)];

        List<RankedCompletion> onePass = spent.Rank(items, TestContext.Current.CancellationToken);
        List<RankedCompletion> table = _matcher.Rank(items, TestContext.Current.CancellationToken);

        onePass.Single().Score.ShouldBe(table.Single().Score);
        onePass.Single().Highlights.ShouldBe(table.Single().Highlights);
    }

    [Fact]
    public void Past_its_budget_a_ranking_still_tolerates_a_typo_and_finds_no_more_than_the_table()
    {
        var spent = new CompletionMatcher(cellsPerRank: 0);
        CompletionCandidate[] items =
        [
            new("prnit", "print", "print", true),
            new("cno", "console", "console", true),
            new("oo", "FooBar", "FooBar", true),
        ];

        List<RankedCompletion> onePass = spent.Rank(items, TestContext.Current.CancellationToken);
        List<RankedCompletion> table = _matcher.Rank(items, TestContext.Current.CancellationToken);

        onePass.Select(r => r.Index).Order().ShouldBe(table.Select(r => r.Index).Order());
        foreach (RankedCompletion ranked in onePass)
            ranked.Score.ShouldBeLessThanOrEqualTo(table.Single(r => r.Index == ranked.Index).Score);
    }

    [Fact]
    public void A_rankings_budget_counts_the_table_that_finds_a_labels_matched_characters()
    {
        // The label lacks the first typed character: its matched characters are found from the second on.
        CompletionCandidate[] items = [new("xab", "yazab", "xab", FilterTextIsLabel: false)];
        int[] exact = _matcher.Rank(items, TestContext.Current.CancellationToken).Single().Highlights;
        int[] cheaper = new CompletionMatcher(cellsPerRank: 0)
            .Rank(items, TestContext.Current.CancellationToken)
            .Single()
            .Highlights;
        exact.ShouldNotBe(cheaper);
        long filterText = new FuzzyScorer().Cells("xab", 0, "xab");

        // A budget for the filter text's table alone: with the label's, the ranking is past it.
        new CompletionMatcher(cellsPerRank: filterText)
            .Rank(items, TestContext.Current.CancellationToken)
            .Single()
            .Highlights.ShouldBe(cheaper);
    }

    [Fact]
    public void Matching_gracefully_swaps_neither_the_first_character_nor_after_the_eighth()
    {
        _matcher.Match("rpint", "print", "print", true, graceful: true).ShouldBeNull();
        _matcher.Match("abcdefhgij", "abcdefghij", "abcdefghij", true, graceful: true).ShouldNotBeNull();
        _matcher.Match("abcdefghji", "abcdefghij", "abcdefghij", true, graceful: true).ShouldBeNull();
    }

    [Fact]
    public void Lists_of_up_to_2000_items_rank_gracefully_and_longer_ones_strictly()
    {
        CompletionCandidate[] items =
        [
            .. Enumerable.Range(0, 1999).Select(i => new CompletionCandidate("prnit", $"x{i}", $"x{i}", true)),
            new("prnit", "print", "print", true),
        ];

        _matcher.Rank(items, TestContext.Current.CancellationToken).Select(r => r.Index).ShouldBe([1999]);
        _matcher.Rank([.. items, new("prnit", "x", "x", true)], TestContext.Current.CancellationToken).ShouldBeEmpty();
    }
}

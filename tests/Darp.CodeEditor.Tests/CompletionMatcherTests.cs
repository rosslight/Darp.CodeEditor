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
        _matcher.Match("fb", "FooBar", "FooBar").ShouldNotBeNull().Highlights.ShouldBe([0, 3]);
        _matcher.Match("FOOB", "fooBar", "fooBar").ShouldNotBeNull().Highlights.ShouldBe([0, 1, 2, 3]);
        _matcher.Match("lin", "write_line", "write_line").ShouldNotBeNull().Highlights.ShouldBe([6, 7, 8]);
        // Out of order, missing, or starting inside a word.
        _matcher.Match("bf", "FooBar", "FooBar").ShouldBeNull();
        _matcher.Match("fx", "FooBar", "FooBar").ShouldBeNull();
        _matcher.Match("oo", "FooBar", "FooBar").ShouldBeNull();
    }

    [Fact]
    public void Prefixes_rank_first_then_word_starts_and_equal_scores_keep_the_items_order()
    {
        string[] labels = ["readConsole", "Console", "console", "consoleKey", "ToConsoleString", "cos"];

        List<RankedCompletion> ranked = _matcher.Rank(
            [.. labels.Select(l => new CompletionCandidate("Cons", l, l))],
            TestContext.Current.CancellationToken
        );

        // A prefix in the typed case first; then in the other case, where the longer word keeps its place; then capitals
        // inside a word, the fewer characters skipped before them the better. "cos" lacks the "n".
        ranked
            .Select(r => labels[r.Index])
            .ShouldBe(["Console", "console", "consoleKey", "ToConsoleString", "readConsole"]);
        _matcher
            .Rank([new("x", "x1", "x1"), new("x", "x2", "x2")], TestContext.Current.CancellationToken)
            .Select(r => r.Index)
            .ShouldBe([0, 1]);
    }

    [Fact]
    public void A_filter_text_decides_the_match_and_the_label_shows_what_matched_in_it()
    {
        // As a server offers "Console.WriteLine" for "wl", filtering by the method's name.
        (int score, int[] highlights) = _matcher.Match("wl", "Console.WriteLine", "WriteLine").ShouldNotBeNull();

        highlights.ShouldBe([8, 13]);
        score.ShouldBe(_matcher.Match("wl", "WriteLine", "WriteLine").ShouldNotBeNull().Score);
        _matcher.Match("co", "Console.WriteLine", "WriteLine").ShouldBeNull();
    }

    [Fact]
    public void Nothing_typed_matches_every_item_in_their_order()
    {
        CompletionCandidate[] items = [new("", "b", "b"), new("", "a", "a"), new(" \t", "c", "c")];

        List<RankedCompletion> ranked = _matcher.Rank(items, TestContext.Current.CancellationToken);

        ranked.Select(r => r.Index).ShouldBe([0, 1, 2]);
        ranked.ShouldAllBe(r => r.Highlights.Length == 0);
    }

    [Fact]
    public void Ranking_stops_once_cancelled()
    {
        CompletionCandidate[] items =
        [
            .. Enumerable.Range(0, 1000).Select(i => new CompletionCandidate("a", $"a{i}", $"a{i}")),
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
}

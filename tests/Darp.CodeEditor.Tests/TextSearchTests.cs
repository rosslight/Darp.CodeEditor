using Darp.CodeEditor.Search;

namespace Darp.CodeEditor.Tests;

/// <summary>The search takes its query literally, within lines, and stops when cancelled.</summary>
public sealed class TextSearchTests
{
    [Theory]
    [InlineData("a.c abc a.c", "a.c", 2)]
    [InlineData("(a+)+$ aaa", "(a+)+$", 1)]
    [InlineData(@"\d 1", @"\d", 1)]
    public void A_query_is_literal_text(string text, string query, int matches) =>
        Find(text, new SearchQuery(query, MatchCase: true, WholeWord: false)).Matches.Count.ShouldBe(matches);

    [Fact]
    public void A_query_does_not_match_across_lines() =>
        Find("a\nb", new SearchQuery("a\nb", MatchCase: true, WholeWord: false)).Matches.Count.ShouldBe(0);

    [Fact]
    public void A_cancelled_search_stops()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Should.Throw<OperationCanceledException>(() =>
            TextSearch.Find(
                TextSnapshot.FromString("a"),
                new SearchQuery("a", MatchCase: true, WholeWord: false),
                cancelled.Token
            )
        );
    }

    private static SearchResult Find(string text, SearchQuery query) =>
        TextSearch.Find(TextSnapshot.FromString(text), query, CancellationToken.None);
}

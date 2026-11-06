using Darp.CodeEditor.Search;

namespace Darp.CodeEditor.Tests;

/// <summary>The search's limits hold for every match it tries, also those it rejects, and it stops when cancelled.</summary>
public sealed class TextSearchTests
{
    /// <summary>
    /// A line on which <c>(a+)+$</c> backtracks at every position, and the alternative <c>a</c> then matches inside a
    /// word: Match Whole Word rejects every match, each one after milliseconds.
    /// </summary>
    private static readonly TextSnapshot s_slowForEveryMatch = TextSnapshot.FromString(
        string.Join(' ', Enumerable.Repeat(new string('a', 18) + "!", 100))
    );

    private static readonly SearchQuery s_slowQuery = new("(a+)+$|a", MatchCase: true, WholeWord: true, UseRegex: true);

    [Fact]
    public void Rejected_matches_count_against_the_time_limit()
    {
        SearchResult result = TextSearch.Find(s_slowForEveryMatch, s_slowQuery, null, CancellationToken.None);

        result.Outcome.ShouldBe(SearchOutcome.OutOfTime);
    }

    [Fact]
    public void A_cancelled_search_stops()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Should.Throw<OperationCanceledException>(() =>
            TextSearch.Find(s_slowForEveryMatch, s_slowQuery, null, cancelled.Token)
        );
    }
}

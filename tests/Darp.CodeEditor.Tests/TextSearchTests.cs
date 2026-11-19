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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_replacement_goes_in_with_the_documents_line_breaks(bool useRegex)
    {
        TextSnapshot snapshot = TextSnapshot.FromString("a=1\r\nb=2");

        SearchResult result = TextSearch.Find(
            snapshot,
            new SearchQuery("=", MatchCase: true, WholeWord: false, UseRegex: useRegex),
            "\n=\r",
            CancellationToken.None
        );

        result.Matches.Select(match => match.Replacement).ShouldBe(["\r\n=\r\n", "\r\n=\r\n"]);
    }

    /// <summary><c>$_</c> is the whole text, with its line breaks of every kind.</summary>
    [Fact]
    public void A_replacement_takes_the_documents_line_breaks_also_from_its_substitutions()
    {
        TextSnapshot snapshot = TextSnapshot.FromString("x\r\ny\nz");

        SearchResult result = TextSearch.Find(
            snapshot,
            new SearchQuery("y", MatchCase: true, WholeWord: false, UseRegex: true),
            "$_",
            CancellationToken.None
        );

        result.Matches.Single().Replacement.ShouldBe("x\ny\nz");
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

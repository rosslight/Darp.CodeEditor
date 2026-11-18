using AvaloniaEdit.Document;
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
        SearchResult result = TextSearch.Find(s_slowForEveryMatch, s_slowQuery, null, null, CancellationToken.None);

        result.Outcome.ShouldBe(SearchOutcome.OutOfTime);
    }

    [Fact]
    public void Only_a_regular_expression_reads_its_replacement_and_its_line_breaks_are_the_lines()
    {
        TextSnapshot snapshot = TextSnapshot.FromString("foo\r\nfoo");
        const string replacement = @"\U$1\n";
        var replace = new SearchReplacement(replacement, PreserveCase: false);

        SearchResult regex = TextSearch.Find(
            snapshot,
            new SearchQuery("(foo)", MatchCase: false, WholeWord: false, UseRegex: true),
            replace,
            null,
            CancellationToken.None
        );
        SearchResult text = TextSearch.Find(
            snapshot,
            new SearchQuery("foo", MatchCase: false, WholeWord: false, UseRegex: false),
            replace,
            null,
            CancellationToken.None
        );

        // The last line has no line break of its own: it takes the one before.
        regex.Matches.Select(match => match.Replacement).ShouldBe(["FOO\r\n", "FOO\r\n"]);
        text.Matches.Select(match => match.Replacement).ShouldBe([replacement, replacement]);
    }

    [Fact]
    public void A_cancelled_search_stops()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Should.Throw<OperationCanceledException>(() =>
            TextSearch.Find(s_slowForEveryMatch, s_slowQuery, null, null, cancelled.Token)
        );
    }

    /// <summary>
    /// A query that can match a line break finds matches across lines, by VS Code's own tests: each text with its
    /// lines apart by <c>\n</c> and by <c>\r\n</c>, which <c>\n</c> matches. Matches are from line and column to line
    /// and column.
    /// </summary>
    [Theory]
    [InlineData(
        "Just some text text|Just some text text|some text again|again some text",
        @"text\n",
        true,
        "1:16-2:1 2:16-3:1"
    )]
    [InlineData(
        "Just some text text|Just some text text|some text again|again some text",
        @"text\nJust",
        true,
        "1:16-2:5"
    )]
    [InlineData(
        "Just some text text|Just some text text|some text again|again some text",
        @"\nagain",
        true,
        "3:16-4:6"
    )]
    [InlineData(
        "Just some text text|Just some text text|some text again|again some text",
        @".*\nJust.*\n",
        true,
        "1:1-3:1"
    )]
    [InlineData("if|else||if|else", @"^if\nelse", true, "1:1-2:5 4:1-5:5")]
    [InlineData("if||else|  |if| |else", @"^\s*$\n", true, "2:1-3:1 4:1-5:1 6:1-7:1")]
    [InlineData("a if b|a|ab|eb", "^a.*b$", true, "1:1-1:7 3:1-3:3")]
    [InlineData("if|else||if|elseif|else", @"if\nelse$", true, "1:1-2:5 5:5-6:5")]
    [InlineData(
        "Just some text text||some text again||again some text",
        "^.*$",
        true,
        "1:1-1:20 2:1-2:1 3:1-3:16 4:1-4:1 5:1-5:16"
    )]
    [InlineData(
        "Just some text text|some text text|some text again|again some text|but not some",
        "text\nsome",
        false,
        "1:16-2:5 2:11-3:5"
    )]
    [InlineData("text|180702-|180703-180704", @"\d{6}-\W", true, "2:1-3:1")]
    [InlineData("Just some text||Just", @"\W", true, "1:5-1:6 1:10-1:11 1:15-2:1 2:1-3:1")]
    [InlineData("Just some text|\tJust|Just", @"\W", true, "1:5-1:6 1:10-1:11 1:15-2:1 2:1-2:2 2:6-3:1")]
    // A match that ends at the end of the text is the last, also before an empty match there.
    [InlineData("10.243.30.10", @"\d*", true, "1:1-1:3 1:3-1:3 1:4-1:7 1:7-1:7 1:8-1:10 1:10-1:10 1:11-1:13")]
    public void A_query_finds_the_matches_that_VS_Code_finds(string lines, string query, bool regex, string expected)
    {
        foreach (string lineBreak in (string[])["\n", "\r\n"])
        {
            var snapshot = TextSnapshot.FromString(lines.Replace("|", lineBreak, StringComparison.Ordinal));
            SearchResult result = TextSearch.Find(
                snapshot,
                new SearchQuery(query, MatchCase: false, WholeWord: false, UseRegex: regex),
                null,
                null,
                CancellationToken.None
            );

            result.Outcome.ShouldBe(SearchOutcome.Complete);
            string.Join(' ', result.Matches.Select(match => Format(snapshot, match)))
                .ShouldBe(expected, $"with {(lineBreak == "\n" ? @"\n" : @"\r\n")}");
        }

        static string Format(TextSnapshot snapshot, SearchMatch match)
        {
            TextPosition start = snapshot.GetPosition(match.StartOffset);
            TextPosition end = snapshot.GetPosition(match.EndOffset);
            return $"{start.Line}:{start.Column}-{end.Line}:{end.Column}";
        }
    }

    [Theory]
    [InlineData("foo", false)]
    [InlineData("", false)]
    [InlineData(@"foo\sbar", false)]
    [InlineData(@"\\notnewline", false)]
    [InlineData(@"MARK:\s*(?<label>.*)$", false)]
    [InlineData(@"foo\nbar", true)]
    [InlineData(@"foo\r\n", true)]
    [InlineData(@"foo\W", true)]
    [InlineData("foo\n", true)]
    public void A_regular_expression_can_match_a_line_break_as_VS_Code_decides(string pattern, bool multiline) =>
        new SearchQuery(pattern, MatchCase: false, WholeWord: false, UseRegex: true).IsMultiline.ShouldBe(multiline);

    [Fact]
    public void A_replacement_across_lines_takes_the_line_break_of_its_line()
    {
        var snapshot = TextSnapshot.FromString("foo\r\nbar\nfoo");

        SearchResult result = TextSearch.Find(
            snapshot,
            new SearchQuery(@"o\n", MatchCase: false, WholeWord: false, UseRegex: true),
            new SearchReplacement(@"\n", PreserveCase: false),
            null,
            CancellationToken.None
        );

        result.Matches.Select(match => (match.StartOffset, match.Length, match.Replacement)).ShouldBe([(2, 3, "\r\n")]);
    }

    /// <summary>
    /// Replace All beyond the matches that a search keeps: one change, which moves what follows the text as separate
    /// replacements do.
    /// </summary>
    [Fact]
    public void Replace_All_is_one_change_that_moves_anchors_as_each_replacement_does()
    {
        string text = "x " + string.Concat(Enumerable.Repeat("ab ", TextSearch.MaxMatches + 1)) + "y";
        var document = new TextDocument(text);
        TextAnchor y = document.CreateAnchor(text.Length - 1);
        TextAnchor second = document.CreateAnchor(5);

        (SearchEdit? edit, SearchOutcome outcome) = TextSearch.ReplaceAll(
            TextSnapshot.FromString(text),
            new SearchQuery("ab", MatchCase: true, WholeWord: false, UseRegex: false),
            new SearchReplacement("c", PreserveCase: false),
            null,
            CancellationToken.None
        );
        outcome.ShouldBe(SearchOutcome.Complete);
        edit.ShouldNotBeNull().Count.ShouldBe(TextSearch.MaxMatches + 1);
        document.Replace(edit.Start, edit.Length, edit.Text, edit.Map);

        document.Text.ShouldBe("x " + string.Concat(Enumerable.Repeat("c ", TextSearch.MaxMatches + 1)) + "y");
        y.Offset.ShouldBe(document.TextLength - 1);
        second.Offset.ShouldBe(4);
        document.UndoStack.Undo();
        document.Text.ShouldBe(text);
        second.Offset.ShouldBe(5);
    }
}

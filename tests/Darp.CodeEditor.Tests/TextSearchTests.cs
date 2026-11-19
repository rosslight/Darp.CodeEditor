using AvaloniaEdit.Document;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Search;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The search's matches, as VS Code finds them, also across lines and never inside a surrogate pair; what replaces
/// them; and its limits, which hold for every match it tries, also those it rejects.
/// </summary>
public sealed class TextSearchTests
{
    /// <summary>A line of 100 matches of <c>a</c> that Match Whole Word rejects, each inside a word.</summary>
    private static readonly TextSnapshot s_rejectedOnOneLine = TextSnapshot.FromString(
        string.Join(' ', Enumerable.Repeat("ba", 100))
    );

    private static readonly SearchQuery s_wholeWordA = new("a", MatchCase: true, WholeWord: true, UseRegex: false);

    /// <summary>Each match may have taken long to find, also one that Match Whole Word rejects.</summary>
    [Fact]
    public void Rejected_matches_count_against_the_time_limit()
    {
        var clock = new SteppingTime { Step = TextSearch.TimeLimit / 50 };

        SearchResult result = TextSearch.Find(
            s_rejectedOnOneLine,
            s_wholeWordA,
            null,
            null,
            CancellationToken.None,
            clock
        );

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
            new SearchReplacement("\n=\r", PreserveCase: false),
            null,
            CancellationToken.None
        );

        result.Matches.Select(match => match.Replacement).ShouldBe(["\r\n=\r\n", "\r\n=\r\n"]);
    }

    /// <summary>A match across lines is searched with its line breaks as <c>\n</c>, which <c>$0</c> brings along.</summary>
    [Fact]
    public void A_replacement_takes_the_documents_line_breaks_also_from_its_substitutions()
    {
        TextSnapshot snapshot = TextSnapshot.FromString("a\r\nb\r\nc\nd");

        SearchResult result = TextSearch.Find(
            snapshot,
            new SearchQuery(@"c\nd", MatchCase: true, WholeWord: false, UseRegex: true),
            new SearchReplacement("$0", PreserveCase: false),
            null,
            CancellationToken.None
        );

        result.Matches.Single().Replacement.ShouldBe("c\r\nd");
    }

    /// <summary>A line break written as <c>\r</c> and <c>\n</c> around an empty group is one line break.</summary>
    [Fact]
    public void A_replacement_takes_the_documents_line_breaks_once_it_is_expanded()
    {
        TextSnapshot snapshot = TextSnapshot.FromString("ab\ncd");

        SearchResult result = TextSearch.Find(
            snapshot,
            new SearchQuery("b()", MatchCase: true, WholeWord: false, UseRegex: true),
            new SearchReplacement("x\r$1\ny", PreserveCase: false),
            null,
            CancellationToken.None
        );

        result.Matches.Single().Replacement.ShouldBe("x\ny");
    }

    [Fact]
    public void A_cancelled_search_stops()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Should.Throw<OperationCanceledException>(() =>
            TextSearch.Find(s_rejectedOnOneLine, s_wholeWordA, null, null, cancelled.Token)
        );
    }

    [Fact]
    public void Only_a_regular_expression_reads_its_replacement_and_its_line_breaks_are_the_documents()
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

        // Also on the last line, which has no line break of its own.
        regex.Matches.Select(match => match.Replacement).ShouldBe(["FOO\r\n", "FOO\r\n"]);
        text.Matches.Select(match => match.Replacement).ShouldBe([replacement, replacement]);
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
    public void A_query_that_can_match_a_line_break_finds_matches_across_lines(
        string lines,
        string query,
        bool regex,
        string expected
    )
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

    /// <summary>
    /// No match or group starts or ends inside a surrogate pair, so that a replacement never writes half of one, and an
    /// empty match steps past a pair whole. Classes, <c>.</c> and quantifiers work on UTF-16 units, as in .NET: where
    /// they take half of a pair, the match is refused.
    /// </summary>
    [Theory]
    [InlineData("\U0001F600", "(?=.)", "X", "X\U0001F600")]
    [InlineData("\U0001F600", "(?:)", "X", "X\U0001F600X")]
    [InlineData("a\U0001F600b", ".", "<$0>", "<a>\U0001F600<b>")]
    [InlineData("\U0001F600\U0001F601", "(?<=.)", "|", "\U0001F600|\U0001F601|")]
    [InlineData("\U0001F600", "[^b]", "X", "\U0001F600")]
    [InlineData("a\U0001F600b", @"\W", "<$0>", "a\U0001F600b")]
    [InlineData("\U0001F600", "(.)(.)", "$2$1", "\U0001F600")]
    [InlineData("\U0001F600", @"(\uD83D)(\uDE00)", "$2$1", "\U0001F600")]
    [InlineData("\U0001F600", @"\uD83D\uDE00", "X", "X")]
    public void No_match_splits_a_surrogate_pair(string text, string query, string replacement, string expected)
    {
        var snapshot = TextSnapshot.FromString(text);
        var search = new SearchQuery(query, MatchCase: true, WholeWord: false, UseRegex: true);
        var replace = new SearchReplacement(replacement, PreserveCase: false);

        SearchResult found = TextSearch.Find(snapshot, search, replace, null, CancellationToken.None);
        (CombinedEdit? edit, SearchOutcome outcome) = TextSearch.ReplaceAll(
            snapshot,
            search,
            replace,
            null,
            CancellationToken.None
        );

        TextEdit[] edits =
        [
            .. found.Matches.Select(match => new TextEdit(match.StartOffset, match.Length, match.Replacement)),
        ];
        Replaced(
                text,
                edits.Length > 0 ? CombinedEdit.Of(new StringTextSource(text), edits, CancellationToken.None) : null
            )
            .ShouldBe(expected);
        outcome.ShouldBe(SearchOutcome.Complete);
        Replaced(text, edit).ShouldBe(expected);
    }

    /// <summary>
    /// A scope that starts or ends between a <c>\r</c> and its <c>\n</c>, as an edit can leave one, starts after the
    /// line break or ends before it.
    /// </summary>
    [Theory]
    [InlineData(0, 2, "a", "0-1")]
    [InlineData(0, 2, "^", "0-0")]
    [InlineData(2, 4, "[ab]", "3-4")]
    [InlineData(2, 2, "^", "3-3")]
    public void A_scope_inside_a_line_break_leaves_it_out(int start, int end, string query, string expected)
    {
        SearchResult result = TextSearch.Find(
            TextSnapshot.FromString("a\r\nb"),
            new SearchQuery(query, MatchCase: true, WholeWord: false, UseRegex: true),
            null,
            [TextRange.FromBounds(start, end)],
            CancellationToken.None
        );

        result.Outcome.ShouldBe(SearchOutcome.Complete);
        string.Join(' ', result.Matches.Select(match => $"{match.StartOffset}-{match.EndOffset}")).ShouldBe(expected);
    }

    /// <summary>
    /// A scope that starts or ends inside a surrogate pair leaves the pair out, also across lines, where the search
    /// reads the scope alone and would not see the other half.
    /// </summary>
    [Fact]
    public void A_scope_inside_a_surrogate_pair_leaves_it_out()
    {
        const string Text = "\U0001F600\r\nz";

        (CombinedEdit? edit, SearchOutcome outcome) = TextSearch.ReplaceAll(
            TextSnapshot.FromString(Text),
            new SearchQuery(@"\W", MatchCase: true, WholeWord: false, UseRegex: true),
            new SearchReplacement("X", PreserveCase: false),
            [TextRange.FromBounds(1, 5)],
            CancellationToken.None
        );

        outcome.ShouldBe(SearchOutcome.Complete);
        Replaced(Text, edit).ShouldBe("\U0001F600Xz");
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
    public void A_regular_expression_can_match_a_line_break_with_one_or_with_n_r_or_W(string pattern, bool multiline) =>
        new SearchQuery(pattern, MatchCase: false, WholeWord: false, UseRegex: true).IsMultiline.ShouldBe(multiline);

    /// <summary>As VS Code's text model makes the line breaks of the text that goes in its own.</summary>
    [Fact]
    public void A_replacement_takes_the_documents_line_break_also_on_a_line_with_another()
    {
        var snapshot = TextSnapshot.FromString("foo\r\nbar\r\nfoo\nbar");

        SearchResult result = TextSearch.Find(
            snapshot,
            new SearchQuery(@"o\n", MatchCase: false, WholeWord: false, UseRegex: true),
            new SearchReplacement(@"\n", PreserveCase: false),
            null,
            CancellationToken.None
        );

        result
            .Matches.Select(match => (match.StartOffset, match.Length, match.Replacement))
            .ShouldBe([(2, 3, "\r\n"), (12, 2, "\r\n")]);
    }

    /// <summary>
    /// Replace All beyond the matches that a search keeps: one change, which moves anchors as separate replacements do,
    /// and which undo takes back whole.
    /// </summary>
    [Fact]
    public void Replace_All_is_one_change_beyond_the_matches_kept_that_moves_anchors_as_each_replacement_does()
    {
        string text = "x " + string.Concat(Enumerable.Repeat("ab ", TextSearch.MaxMatches + 1)) + "y";
        var snapshot = TextSnapshot.FromString(text);
        var query = new SearchQuery("ab", MatchCase: true, WholeWord: false, UseRegex: false);
        var replacement = new SearchReplacement("c", PreserveCase: false);
        var document = new TextDocument(text);
        TextAnchor y = document.CreateAnchor(text.Length - 1);
        TextAnchor second = document.CreateAnchor(5);

        SearchResult found = TextSearch.Find(snapshot, query, replacement, null, CancellationToken.None);
        (CombinedEdit? edit, SearchOutcome outcome) = TextSearch.ReplaceAll(
            snapshot,
            query,
            replacement,
            null,
            CancellationToken.None
        );
        found.Outcome.ShouldBe(SearchOutcome.TooManyMatches);
        outcome.ShouldBe(SearchOutcome.Complete);
        edit = edit.ShouldNotBeNull();
        document.Replace(edit.Offset, edit.Length, edit.Text, edit.Map);

        document.Text.ShouldBe("x " + string.Concat(Enumerable.Repeat("c ", TextSearch.MaxMatches + 1)) + "y");
        y.Offset.ShouldBe(document.TextLength - 1);
        second.Offset.ShouldBe(4);
        document.UndoStack.Undo();
        document.Text.ShouldBe(text);
        second.Offset.ShouldBe(5);
    }

    /// <summary>The text with <paramref name="edit"/> made, if any.</summary>
    private static string Replaced(string text, CombinedEdit? edit)
    {
        var document = new TextDocument(text);
        if (edit is not null)
            document.Replace(edit.Offset, edit.Length, edit.Text, edit.Map);
        return document.Text;
    }
}

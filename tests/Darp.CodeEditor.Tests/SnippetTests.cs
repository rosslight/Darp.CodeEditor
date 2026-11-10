using Darp.CodeEditor.Completion;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The LSP snippet syntax of completion items, as VS Code parses it: the text that goes in, its stops and mirrors,
/// choices, variables, escapes, text that does not parse, the indentation of the line it goes in, and the limits that
/// keep a server's snippet from costing the UI thread more than its length.
/// </summary>
public sealed class SnippetTests
{
    [Theory]
    [InlineData("foo$1bar", "foobar", "1:|0:")]
    [InlineData("foo${1}bar$0baz", "foobarbaz", "1:|0:")]
    [InlineData("${1:value}", "value", "1:value|0:")]
    [InlineData("foo${1:bar}}", "foobar}", "1:bar|0:")]
    [InlineData(@"foo${1:bar\}${2:foo}}", "foobar}foo", "1:bar}foo|2:foo|0:")]
    [InlineData("${1:}", "", "1:|0:")]
    [InlineData("$1 then $2, at last ${0:end}", " then , at last end", "1:|2:|0:end")]
    public void Stops_placeholders_and_the_final_stop_parse_as_in_VS_Code(string snippet, string text, string stops)
    {
        RenderedSnippet rendered = Render(snippet);

        rendered.Text.ShouldBe(text);
        Stops(rendered).ShouldBe(stops);
    }

    [Fact]
    public void Placeholders_nest_and_know_the_placeholder_they_are_in()
    {
        RenderedSnippet rendered = Render("${3:nest1 ${1:nest2 ${2:nest3}}} $3");

        rendered.Text.ShouldBe("nest1 nest2 nest3 nest1 nest2 nest3");
        Stops(rendered).ShouldBe("3:nest1 nest2 nest3|1:nest2 nest3|2:nest3|3:nest1 nest2 nest3|0:");
        rendered.Stops.Select(s => s.Parent).ShouldBe([-1, 0, 1, -1, -1]);
    }

    [Theory]
    // Mirrors take the text of the first placeholder of their number with text, also one written before it.
    [InlineData("${1:foo}-abc-$1", "foo-abc-foo")]
    [InlineData("${1:foo}-abc-${1}", "foo-abc-foo")]
    [InlineData("${1:foo}-abc-${1:bar}", "foo-abc-foo")]
    [InlineData("${1}-abc-${1:foo}", "foo-abc-foo")]
    // Nested stops in a mirror's text are the text of their own number.
    [InlineData("${1:a ${2:b}} $1 $2", "a b a b b")]
    // Mirrors of each other end, rather than fill each other forever.
    [InlineData("${1:a$2} ${2:b$1}", "aba bab")]
    [InlineData("$1 $1", " ")]
    public void Mirrors_take_the_text_of_the_first_placeholder_of_their_number(string snippet, string text) =>
        Render(snippet).Text.ShouldBe(text);

    [Theory]
    [InlineData("${1|one,two,three|}", "one")]
    [InlineData("${1|one|}", "one")]
    [InlineData(@"${1|one1\,two2|}", "one1,two2")]
    [InlineData(@"${1|one1\|two2|}", "one1|two2")]
    [InlineData(@"${1|one1\atwo2|}", @"one1\atwo2")]
    [InlineData(@"console.log(${1|not\, not, five, 5, 1   23|});", "console.log(not, not);")]
    [InlineData(@"${1|aaa$aaa|}bbb\$bbb", "aaa$aaabbb$bbb")]
    public void Choices_insert_their_first_option_as_a_placeholder(string snippet, string text)
    {
        RenderedSnippet rendered = Render(snippet);

        rendered.Text.ShouldBe(text);
        rendered.Stops[0].Index.ShouldBe(1);
    }

    [Theory]
    [InlineData("$", "$")]
    [InlineData(@"\\$", @"\$")]
    [InlineData("{", "{")]
    [InlineData(@"\}", "}")]
    [InlineData(@"\abc", @"\abc")]
    [InlineData(@"\{", @"\{")]
    [InlineData(@"I need \\\$", @"I need \$")]
    [InlineData(@"\", @"\")]
    [InlineData("far{{id:bern {{id2:basel}}}}boo", "far{{id:bern {{id2:basel}}}}boo")]
    [InlineData("far`123`boo", "far`123`boo")]
    [InlineData(@"foo${f:\}}bar", "foo}bar")]
    [InlineData(@"\$far-boo", "$far-boo")]
    [InlineData("${name:value", "${name:value")]
    [InlineData("${foo///", "${foo///")]
    [InlineData("${1/regex/format/options", "${1/regex/format/options")]
    [InlineData("${foo/([A-Z][a-z])/format/GMI}", "${foo/([A-Z][a-z])/format/GMI}")]
    [InlineData("${1|one,two,three,|}", "${1|one,two,three,|}")]
    [InlineData("${1|one,", "${1|one,")]
    [InlineData("${1||}", "${1||}")]
    [InlineData("${0|foo,bar|}", "${0|foo,bar|}")]
    [InlineData(@"\${1|one,two,three|}", "${1|one,two,three|}")]
    [InlineData(@"${1\|one,two,three|}", @"${1\|one,two,three|}")]
    [InlineData("$99999999999", "$99999999999")]
    public void Escapes_and_what_does_not_parse_are_text_as_in_VS_Code(string snippet, string text)
    {
        RenderedSnippet rendered = Render(snippet);

        rendered.Text.ShouldBe(text);
        rendered.Stops.ShouldBeEmpty();
    }

    [Fact]
    public void An_unclosed_placeholder_is_text_and_keeps_the_stops_inside_it() =>
        Stops(Render("${1:bar${2:foobar}")).ShouldBe("2:foobar|0:");

    [Fact]
    public void Variables_take_their_value_or_else_their_default_and_unknown_ones_become_placeholders()
    {
        Func<string, string?> values = name => name == "TM_FILENAME" ? "a.txt" : null;

        Render("$TM_FILENAME ${TM_FILENAME:default}", values).Text.ShouldBe("a.txt a.txt");
        // Known and unset: the default, or nothing.
        RenderedSnippet unset = Render("[$TM_SELECTED_TEXT] [${TM_SELECTED_TEXT:${1:default}}]", values);
        unset.Text.ShouldBe("[] [default]");
        Stops(unset).ShouldBe("1:default|0:");
        // A placeholder in a default that a value replaces is no stop.
        Stops(Render("${TM_FILENAME:${1:x}}", values)).ShouldBe("0:");
        // Unknown without a default: a placeholder with its name, after the highest stop, one number per name; level
        // by level, as VS Code finds them. With a default: the default.
        RenderedSnippet unknown = Render("${1:$FOO} $BAR ${FOO} ${UNKNOWN:default}", values);
        unknown.Text.ShouldBe("FOO BAR FOO default");
        Stops(unknown).ShouldBe("1:FOO|3:FOO|2:BAR|3:FOO|0:");
    }

    [Fact]
    public void Transformations_are_read_and_not_applied()
    {
        Render(@"${TM_FILENAME/(.*)\..+$/$1/}", name => name == "TM_FILENAME" ? "a.txt" : null).Text.ShouldBe("a.txt");
        RenderedSnippet mirror = Render("${1:foo} ${1/(.*)/${1:/upcase}/} ${1/(.)/${1:?yes:no}/gi}");
        mirror.Text.ShouldBe("foo foo foo");
        Stops(mirror).ShouldBe("1:foo|1:foo|1:foo|0:");
    }

    [Fact]
    public void Lines_after_the_first_take_the_indentation_of_the_line_in_the_documents_indentation()
    {
        // Spaces: a tab is a level of four.
        RenderedSnippet spaces = Render("if (${1:x}) {\n\t$0\r\n}", lineIndentation: "  \t", lineBreak: "\r\n");
        spaces.Text.ShouldBe("if (x) {\r\n        \r\n    }");
        Stops(spaces).ShouldBe("1:x|0:");
        spaces.Stops[1].Start.ShouldBe("if (x) {\r\n        ".Length);

        // Tabs, and the snippet's own indentation at its start in them too.
        Render("    a\n  b", lineIndentation: "\t", indentation: Indentation.Tabs(4)).Text.ShouldBe("\ta\n\t  b");

        // A line that a stop, a choice or a variable starts has the line's indentation; their own text stays as it is,
        // as in VS Code.
        Render("{\n${1:\tx}\n${2|  y|}\n$TM_FILENAME", name => "  f", lineIndentation: "  ")
            .Text.ShouldBe("{\n  \tx\n    y\n    f");
    }

    [Fact]
    public void A_snippet_beyond_the_limits_goes_in_as_written()
    {
        // As many stops as tracked: 999 and the final one.
        string most = string.Concat(Enumerable.Repeat("$1", Snippet.MaxStops - 1));
        Render(most).Stops.Count.ShouldBe(Snippet.MaxStops);
        AsWritten(most + "$1");

        // As deep as placeholders nest.
        string nested =
            string.Concat(Enumerable.Range(1, Snippet.MaxNesting).Select(i => $"${{{i}:"))
            + new string('}', Snippet.MaxNesting);
        Render(nested).Stops.Count.ShouldBe(Snippet.MaxNesting + 1);
        AsWritten("${0:" + nested + "}");

        // Mirrors that would double the text twenty times over.
        AsWritten("${1:x}" + string.Concat(Enumerable.Range(2, 20).Select(i => $"${{{i}:${i - 1}${i - 1}}}")));

        // Text crafted so that each "${1/" reads to its end, if parsing went on.
        AsWritten(string.Concat(Enumerable.Repeat("${1/a/b/", 20_000)));

        static void AsWritten(string snippet)
        {
            RenderedSnippet rendered = Render(snippet);
            rendered.Text.ShouldBe(snippet);
            rendered.Stops.ShouldBeEmpty();
        }
    }

    [Fact]
    public void Variables_add_at_most_10000_characters_and_later_ones_take_their_default()
    {
        string line = new('a', 3_000);

        RenderedSnippet rendered = Render(
            "$TM_CURRENT_LINE$TM_CURRENT_LINE$TM_CURRENT_LINE${TM_CURRENT_LINE:default}",
            name => name == "TM_CURRENT_LINE" ? line : null
        );

        rendered.Text.ShouldBe(line + line + line + "default");
    }

    [Fact]
    public void File_variables_come_from_the_documents_path()
    {
        string directory = Path.Combine(Path.GetTempPath(), "scripts");
        string path = Path.Combine(directory, "settings.local.json");

        SnippetVariables.OfFile("TM_FILENAME", path).ShouldBe("settings.local.json");
        SnippetVariables.OfFile("TM_FILENAME_BASE", path).ShouldBe("settings.local");
        SnippetVariables.OfFile("TM_DIRECTORY", path).ShouldBe(directory);
        SnippetVariables.OfFile("TM_DIRECTORY_BASE", path).ShouldBe("scripts");
        SnippetVariables.OfFile("TM_FILEPATH", path).ShouldBe(path);
        // Without a workspace, as in VS Code.
        SnippetVariables.OfFile("RELATIVE_FILEPATH", path).ShouldBe(path);
        SnippetVariables.OfFile("TM_FILENAME_BASE", Path.Combine(directory, ".gitignore")).ShouldBe(".gitignore");
        // Without a file, unset: a default or nothing goes in.
        SnippetVariables.OfFile("TM_FILENAME", null).ShouldBeNull();
        SnippetVariables.OfFile("TM_CURRENT_LINE", path).ShouldBeNull();
    }

    [Fact]
    public void Time_variables_are_VS_Codes_in_English()
    {
        var now = new DateTimeOffset(2026, 3, 7, 5, 4, 3, 21, TimeSpan.FromMinutes(-150));

        string?[] values =
        [
            .. new[]
            {
                "CURRENT_YEAR",
                "CURRENT_YEAR_SHORT",
                "CURRENT_MONTH",
                "CURRENT_DATE",
                "CURRENT_HOUR",
                "CURRENT_MINUTE",
                "CURRENT_SECOND",
                "CURRENT_MILLISECOND",
                "CURRENT_DAY_NAME",
                "CURRENT_DAY_NAME_SHORT",
                "CURRENT_MONTH_NAME",
                "CURRENT_MONTH_NAME_SHORT",
                "CURRENT_SECONDS_UNIX",
                "CURRENT_MILLISECONDS_UNIX",
                "CURRENT_TIMEZONE_OFFSET",
            }.Select(name => SnippetVariables.OfTime(name, now, TimeZoneInfo.Utc)),
        ];

        values.ShouldBe([
            "2026",
            "26",
            "03",
            "07",
            "05",
            "04",
            "03",
            "021",
            "Saturday",
            "Sat",
            "March",
            "Mar",
            "1772868843",
            "1772868843021",
            "-02:30",
        ]);
        SnippetVariables.OfTime("CURRENT_TIMEZONE_NAME", now, TimeZoneInfo.Utc).ShouldNotBeNullOrEmpty();
        SnippetVariables.OfTime("TM_FILENAME", now, TimeZoneInfo.Utc).ShouldBeNull();
    }

    [Fact]
    public void Random_variables_are_six_digits_and_a_UUID()
    {
        SnippetVariables.Random("RANDOM").ShouldNotBeNull().ShouldMatch("^[0-9]{6}$");
        SnippetVariables.Random("RANDOM_HEX").ShouldNotBeNull().ShouldMatch("^[0-9a-f]{6}$");
        Guid.TryParse(SnippetVariables.Random("UUID"), out _).ShouldBeTrue();
        SnippetVariables.Random("TM_FILENAME").ShouldBeNull();
    }

    /// <summary>A snippet as it goes in, by default without variables at the start of an unindented line.</summary>
    private static RenderedSnippet Render(
        string snippet,
        Func<string, string?>? variable = null,
        string lineIndentation = "",
        Indentation? indentation = null,
        string lineBreak = "\n"
    ) =>
        Snippet
            .Parse(snippet)
            .Render(
                new SnippetContext(
                    lineIndentation,
                    indentation ?? Indentation.Spaces(4),
                    lineBreak,
                    variable ?? (_ => null)
                )
            );

    /// <summary>The stops as "number:text", in order, separated by "|".</summary>
    private static string Stops(RenderedSnippet rendered) =>
        string.Join("|", rendered.Stops.Select(s => $"{s.Index}:{rendered.Text[s.Start..s.End]}"));
}

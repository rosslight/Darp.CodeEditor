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
    public void Variables_take_their_value_or_else_their_default_or_else_nothing_also_unknown_ones()
    {
        Func<string, string?> values = name => name == "TM_FILENAME" ? "a.txt" : null;

        Render("$TM_FILENAME ${TM_FILENAME:default}", values).Text.ShouldBe("a.txt a.txt");
        // Known and unset: the default, or nothing.
        RenderedSnippet unset = Render("[$TM_SELECTED_TEXT] [${TM_SELECTED_TEXT:${1:default}}]", values);
        unset.Text.ShouldBe("[] [default]");
        Stops(unset).ShouldBe("1:default|0:");
        // A placeholder in a default that a value replaces is no stop.
        Stops(Render("${TM_FILENAME:${1:x}}", values)).ShouldBe("0:");
        // Unknown: the same, as VS Code's completion inserts them.
        RenderedSnippet unknown = Render("before-$UNRECOGNIZED-after ${1:$FOO} ${UNKNOWN:default}$0", values);
        unknown.Text.ShouldBe("before--after  default");
        Stops(unknown).ShouldBe("1:|0:");
        Stops(Render("${TM_SELECTED_TEXT:$FOO}", values)).ShouldBeEmpty();
    }

    [Fact]
    public void Transformations_apply_to_variables_as_they_go_in_and_wait_on_stops_for_the_session()
    {
        Func<string, string?> values = name => name == "TM_FILENAME" ? "a.txt" : null;

        Render(@"const fileName = ""${TM_FILENAME/(.*)\..+$/$1/}""", values).Text.ShouldBe(@"const fileName = ""a""");
        // A variable without a value is transformed as empty text: here, its else branch.
        Render("${TM_SELECTED_TEXT/^$/${0:?some:none}/}", values).Text.ShouldBe("none");
        // A stop's goes in as its mirror's text: the session applies it.
        RenderedSnippet mirror = Render("${1:foo} ${1/(.*)/${1:/upcase}/} ${1/(.)/${1:?yes:no}/gi}");
        mirror.Text.ShouldBe("foo foo foo");
        Stops(mirror).ShouldBe("1:foo|1:foo|1:foo|0:");
        mirror.Stops[0].Transform.ShouldBeNull();
        mirror.Stops[1].Transform.ShouldNotBeNull().Resolve("bar").ShouldBe("BAR");
        mirror.Stops[2].Transform.ShouldNotBeNull().Resolve("bar").ShouldBe("yesyesyes");
    }

    [Theory]
    [InlineData(@"${1/foo/bar/}", "foo", "bar")]
    // The first match, or each one with g; the rest of the text stays.
    [InlineData(@"${1/o/0/}", "foo", "f0o")]
    [InlineData(@"${1/o/0/g}", "foo", "f00")]
    [InlineData(@"${1/O/0/gi}", "foo", "f00")]
    [InlineData(@"${1/^(.)|-(.)/${1:/upcase}${2:/upcase}/g}", "my-file-name", "MyFileName")]
    [InlineData(@"${1/\s:=(.*)/${1:+ :=}${1}/}", " := '1'", " := '1'")]
    // An else branch is the result when nothing matches.
    [InlineData(@"${1/(x)/${1:?yes:no}/}", "abc", "no")]
    [InlineData(@"${1/(x)/${1:-none}/}", "abc", "none")]
    [InlineData(@"${1/(x)/${1:none}/}", "abc", "none")]
    [InlineData(@"${1/(x)/${1:+some}/}", "abc", "abc")]
    // Groups that did not take part, or that the regular expression does not have, are empty.
    [InlineData(@"${1/(a)|(b)/[$1|$2|$3]/}", "b", "[|b|]")]
    [InlineData(@"${1/.*/[$0]/}", "abc", "[abc]")]
    [InlineData(@"${1/.*/[$0]/g}", "abc", "[abc][]")]
    // Escapes in the format: \/ and \\ are text, as is a backslash before anything else.
    [InlineData(@"${1/[\/]/\\/g}", "a/b", @"a\b")]
    [InlineData(@"${1/a/\//}", "a", "/")]
    [InlineData(@"${1/a/\n/}", "a", @"\n")]
    [InlineData(@"${1/(.+)/${1:+import { hello \} from world}/}", "x", "import { hello } from world")]
    [InlineData(@"${1/(.+)/${1:+\\}/}", "x", @"\")]
    // Options: m for lines, s for line breaks, y where the text starts; d, u and v change nothing.
    [InlineData(@"${1/^b/B/gm}", "a\nb", "a\nB")]
    [InlineData(@"${1/a.b/x/s}", "a\nb", "x")]
    [InlineData(@"${1/a.b/x/}", "a\nb", "a\nb")]
    [InlineData(@"${1/b/x/y}", "ab", "ab")]
    [InlineData(@"${1/a/x/gy}", "aab", "xxb")]
    [InlineData(@"${1/a/x/duv}", "a", "${1/a/x/duv}")]
    [InlineData(@"${1/a/x/du}", "a", "x")]
    public void A_transformation_replaces_its_matches_with_its_format(string snippet, string value, string result)
    {
        RenderedSnippet rendered = Render(snippet);

        if (rendered.Stops.FirstOrDefault().Transform is { } transform)
            transform.Resolve(value).ShouldBe(result);
        else
            rendered.Text.ShouldBe(result);
    }

    [Theory]
    [InlineData("upcase", "foo", "FOO")]
    [InlineData("downcase", "FOO", "foo")]
    [InlineData("capitalize", "bar", "Bar")]
    [InlineData("capitalize", "bar no repeat", "Bar no repeat")]
    [InlineData("pascalcase", "bar-foo", "BarFoo")]
    [InlineData("pascalcase", "bar-42-foo", "Bar42Foo")]
    [InlineData("pascalcase", "snake_AndPascalCase", "SnakeAndPascalCase")]
    [InlineData("pascalcase", "kebab-AndPascalCase", "KebabAndPascalCase")]
    [InlineData("pascalcase", "_justPascalCase", "JustPascalCase")]
    [InlineData("camelcase", "bar-foo", "barFoo")]
    [InlineData("camelcase", "bar-42-foo", "bar42Foo")]
    [InlineData("camelcase", "snake_AndCamelCase", "snakeAndCamelCase")]
    [InlineData("camelcase", "kebab-AndCamelCase", "kebabAndCamelCase")]
    [InlineData("camelcase", "_JustCamelCase", "justCamelCase")]
    [InlineData("kebabcase", "barFoo", "bar-foo")]
    [InlineData("kebabcase", "BarFoo", "bar-foo")]
    [InlineData("kebabcase", "ABarFoo", "a-bar-foo")]
    [InlineData("kebabcase", "bar42Foo", "bar42-foo")]
    [InlineData("kebabcase", "snake_AndPascalCase", "snake-and-pascal-case")]
    [InlineData("kebabcase", "kebab-AndCamelCase", "kebab-and-camel-case")]
    [InlineData("kebabcase", "_justPascalCase", "just-pascal-case")]
    [InlineData("kebabcase", "__UPCASE__", "upcase")]
    [InlineData("kebabcase", "__BAR_FOO__", "bar-foo")]
    [InlineData("snakecase", "bar-foo", "bar_foo")]
    [InlineData("snakecase", "bar-42-foo", "bar_42_foo")]
    [InlineData("snakecase", "snake_AndPascalCase", "snake_and_pascal_case")]
    [InlineData("snakecase", "kebab-AndPascalCase", "kebab_and_pascal_case")]
    [InlineData("snakecase", "_justPascalCase", "_just_pascal_case")]
    [InlineData("notKnown", "input", "input")]
    [InlineData("kebabcase", "одинДва", "один-два")]
    [InlineData("kebabcase", "έναςΔύο", "ένας-δύο")]
    [InlineData("snakecase", "одинДва", "один_два")]
    [InlineData("snakecase", "έναςΔύο", "ένας_δύο")]
    [InlineData("camelcase", "одинДва", "одинДва")]
    [InlineData("camelcase", "έναςΔύο", "έναςΔύο")]
    [InlineData("pascalcase", "одинДва", "ОдинДва")]
    [InlineData("pascalcase", "έναςΔύο", "ΈναςΔύο")]
    [InlineData("upcase", "одинДва", "ОДИНДВА")]
    [InlineData("downcase", "одинДва", "одиндва")]
    [InlineData("kebabcase", "istanbulLı", "istanbul-lı")]
    [InlineData("pascalcase", "istanbulLı", "IstanbulLı")]
    [InlineData("upcase", "こんにちは", "こんにちは")]
    [InlineData("kebabcase", "こんにちは", "こんにちは")]
    public void Format_references_change_the_case_of_their_group_as_in_VS_Code(
        string shorthand,
        string value,
        string result
    ) => Render($"${{VALUE/(.*)/${{1:/{shorthand}}}/}}", _ => value).Text.ShouldBe(result);

    [Theory]
    [InlineData("+foo", "", "")]
    [InlineData("+foo", "bar", "foo")]
    [InlineData("-foo", "", "foo")]
    [InlineData("-foo", "bar", "bar")]
    [InlineData("foo", "", "foo")]
    [InlineData("foo", "bar", "bar")]
    [InlineData("?bar:foo", "", "foo")]
    [InlineData("?bar:foo", "baz", "bar")]
    public void Format_references_go_in_as_their_if_or_else_branch_as_in_VS_Code(
        string branch,
        string value,
        string result
    ) => Render($"${{VALUE/(.*)/${{1:{branch}}}/}}", _ => value).Text.ShouldBe(result);

    [Theory]
    // Options that JavaScript rejects, and a regular expression that .NET rejects: the snippet as written.
    [InlineData("${TM_FILENAME/a/b/x}")]
    [InlineData("${TM_FILENAME/a/b/gg}")]
    [InlineData("${TM_FILENAME/(a/b/}")]
    [InlineData("${1/a/b/uv}")]
    public void A_transformation_that_is_invalid_is_text(string snippet)
    {
        RenderedSnippet rendered = Render(snippet);

        rendered.Text.ShouldBe(snippet);
        rendered.Stops.ShouldBeEmpty();
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
    public void A_snippet_has_as_many_stops_as_it_likes()
    {
        string snippet = string.Concat(Enumerable.Range(1, 100).Select(i => $"${{{i}:{i}}} $1 "));

        RenderedSnippet rendered = Render(snippet);

        rendered.Stops.Count.ShouldBe(201);
        rendered.Stops.Select(stop => stop.Index).Distinct().Count().ShouldBe(101);
    }

    [Fact]
    public void A_snippet_beyond_the_parsing_limits_goes_in_as_written()
    {
        // As deep as placeholders and variables nest.
        string nested =
            string.Concat(Enumerable.Repeat("${TM_SELECTED_TEXT:", Snippet.MaxNesting - 1))
            + "${1:x}"
            + new string('}', Snippet.MaxNesting - 1);
        Stops(Render(nested)).ShouldBe("1:x|0:");
        AsWritten("${TM_SELECTED_TEXT:" + nested + "}");

        // Mirrors that would double the text twenty times over.
        AsWritten("${1:x}" + string.Concat(Enumerable.Range(2, 20).Select(i => $"${{{i}:${i - 1}${i - 1}}}")));

        // Text crafted so that each "${1/" reads to its end, if parsing went on: in many tokens, or in few long ones.
        // The stop at the end would parse, if parsing went on.
        AsWritten(string.Concat(Enumerable.Repeat("${1/a/b/", 20_000)) + "${1:x}");
        AsWritten(string.Concat(Enumerable.Repeat("${1/" + new string('a', 1_000), 200)) + "${1:x}");
    }

    [Fact]
    public void A_snippet_whose_text_would_pass_5000_characters_goes_in_as_written()
    {
        // Lines that the line's indentation of 3,000 columns would make 30 million characters long.
        string lines = "${1:a}" + new string('\n', 10_000);
        AsWritten(lines, lineIndentation: new string(' ', 3_000));
        // Wide tabs, which would make one line's indentation long, are counted before it is made.
        AsWritten("a\n\t$1", lineIndentation: new string('\t', 20), indentation: Indentation.Spaces(1_000));
        Render("a\n\t$1", lineIndentation: "\t", indentation: Indentation.Spaces(100)).Text.Length.ShouldBe(202);

        // Variables' values.
        string line = new('a', 1_000);
        AsWritten(string.Concat(Enumerable.Repeat("$TM_CURRENT_LINE", 6)) + "$1", _ => line);
        Render(string.Concat(Enumerable.Repeat("$TM_CURRENT_LINE", 4)) + "$1", _ => line).Text.Length.ShouldBe(4_000);

        // Parts count too: variables without a value are asked for at most that often.
        int asked = 0;
        AsWritten(
            "$1" + string.Concat(Enumerable.Repeat("$CLIPBOARD", 2 * Snippet.MaxText)),
            _ =>
            {
                asked++;
                return null;
            }
        );
        asked.ShouldBeLessThanOrEqualTo(Snippet.MaxText);
    }

    [Fact]
    public void A_mirror_has_the_values_of_the_variables_it_copies()
    {
        int drawn = 0;

        RenderedSnippet rendered = Render("${1:$UUID} $1 $UUID", _ => $"v{++drawn}");

        rendered.Text.ShouldBe("v1 v1 v2");
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

    /// <summary>Checks that <paramref name="snippet"/> goes in as written, without stops.</summary>
    private static void AsWritten(
        string snippet,
        Func<string, string?>? variable = null,
        string lineIndentation = "",
        Indentation? indentation = null
    )
    {
        RenderedSnippet rendered = Render(snippet, variable, lineIndentation, indentation);
        rendered.Text.ShouldBe(snippet);
        rendered.Stops.ShouldBeEmpty();
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

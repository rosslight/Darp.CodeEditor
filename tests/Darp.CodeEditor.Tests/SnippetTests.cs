using Darp.CodeEditor.Completion;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The LSP snippet syntax as far as the editor uses it: stops, placeholders and the final stop; everything else as the
/// text it stands for, without a stop; the indentation of the line it goes in; and the limits.
/// </summary>
public sealed class SnippetTests
{
    [Theory]
    [InlineData("foo$1bar", "foobar", "1:|0:")]
    [InlineData("foo${1}bar$0baz", "foobarbaz", "1:|0:")]
    [InlineData("${1:value}", "value", "1:value|0:")]
    [InlineData("foo${1:bar}}", "foobar}", "1:bar|0:")]
    [InlineData("${1:}", "", "1:|0:")]
    [InlineData("$1 then $2, at last ${0:end}", " then , at last end", "1:|2:|0:end")]
    // Stops go by number in the session, and keep the order of the text here.
    [InlineData("${2:b} ${1:a}", "b a", "2:b|1:a|0:")]
    public void Stops_placeholders_and_the_final_stop(string snippet, string text, string stops)
    {
        RenderedSnippet rendered = Render(snippet);

        rendered.Text.ShouldBe(text);
        Stops(rendered).ShouldBe(stops);
    }

    [Theory]
    // A repeated stop: its text, without a stop of its own.
    [InlineData("${1:foo}-abc-$1", "foo-abc-foo", "1:foo|0:")]
    [InlineData("${1:foo}-abc-${1:bar}", "foo-abc-foo", "1:foo|0:")]
    [InlineData("${1}-abc-${1:foo}", "foo-abc-foo", "1:foo|0:")]
    [InlineData("$1 $1", " ", "1:|0:")]
    // A nested placeholder: its text.
    [InlineData("${1:a ${2:b}} c", "a b c", "1:a b|0:")]
    [InlineData(@"foo${1:bar\}${2:foo}}", "foobar}foo", "1:bar}foo|0:")]
    // A choice: its first option.
    [InlineData("${1|one,two,three|}", "one", "")]
    [InlineData(@"${1|one1\,two2,x|}", "one1,two2", "")]
    [InlineData(@"${1|one1\|two2|}", "one1|two2", "")]
    [InlineData("a ${1|b,c|} ${2:d}", "a b d", "2:d|0:")]
    [InlineData("${1|a,b|} $1", "a a", "1:a|0:")]
    // A variable: its default, or nothing.
    [InlineData("[$TM_FILENAME]", "[]", "")]
    [InlineData("[${TM_FILENAME}]", "[]", "")]
    [InlineData("${TM_FILENAME:default}", "default", "")]
    [InlineData("${TM_SELECTED_TEXT:${1:x}}", "x", "")]
    // A transformation: its stop's text.
    [InlineData("${1:foo} ${1/(.*)/${1:/upcase}/}", "foo foo", "1:foo|0:")]
    [InlineData(@"${1:a} ${1/\d{3}/x/g} b", "a a b", "1:a|0:")]
    [InlineData(@"[${TM_FILENAME/(.*)\..+$/$1/}]", "[]", "")]
    public void Anything_else_goes_in_as_the_text_it_stands_for_without_a_stop(
        string snippet,
        string text,
        string stops
    )
    {
        RenderedSnippet rendered = Render(snippet);

        rendered.Text.ShouldBe(text);
        Stops(rendered).ShouldBe(stops);
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
    [InlineData(@"foo${f:\}}bar", "foo}bar")]
    [InlineData(@"\$far-boo", "$far-boo")]
    [InlineData("${name:value", "${name:value")]
    [InlineData("${foo///", "${foo///")]
    [InlineData("${1/regex/format/options", "${1/regex/format/options")]
    [InlineData("${1|one,", "${1|one,")]
    [InlineData("${1||}", "${1||}")]
    [InlineData(@"\${1|one,two,three|}", "${1|one,two,three|}")]
    [InlineData(@"${1\|one,two,three|}", @"${1\|one,two,three|}")]
    [InlineData("$99999999999", "$99999999999")]
    public void Escapes_and_what_does_not_parse_are_text(string snippet, string text)
    {
        RenderedSnippet rendered = Render(snippet);

        rendered.Text.ShouldBe(text);
        rendered.Stops.ShouldBeEmpty();
    }

    [Fact]
    public void An_unclosed_placeholder_is_text_and_keeps_the_stops_after_it() =>
        Stops(Render("${1:bar${2:foobar}")).ShouldBe("2:foobar|0:");

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

        // A line that a stop or a choice starts has the line's indentation; their own text stays as it is.
        Render("{\n${1:\tx}\n${2|  y|}", lineIndentation: "  ").Text.ShouldBe("{\n  \tx\n    y");
    }

    [Fact]
    public void A_snippet_over_the_limits_is_plain_text()
    {
        // As many stops as tracked, the final one included.
        string most = string.Concat(Enumerable.Range(1, Snippet.MaxStops - 1).Select(i => $"${i}"));
        Render(most).Stops.Count.ShouldBe(Snippet.MaxStops);
        Snippet.Parse(most + $"${Snippet.MaxStops}").ShouldBeNull();

        Snippet.Parse(new string('a', Snippet.MaxText + 1)).ShouldBeNull();

        // Its text as it goes in counts: the line's indentation, and a repeated stop.
        Snippet
            .Parse("${1:a}" + new string('\n', 1_000))
            .ShouldNotBeNull()
            .Render(new SnippetContext(new string(' ', 3_000), Indentation.Spaces(4), "\n"))
            .ShouldBeNull();
        Snippet
            .Parse("${1:" + new string('a', 1_000) + "}" + string.Concat(Enumerable.Repeat("$1", 5)))
            .ShouldNotBeNull()
            .Render(new SnippetContext("", Indentation.Spaces(4), "\n"))
            .ShouldBeNull();
    }

    [Fact]
    public void Text_nested_or_unclosed_as_deep_as_the_limit_allows_parses()
    {
        const int levels = (Snippet.MaxText - 1) / 5;
        string nested = string.Concat(Enumerable.Repeat("${1:", levels)) + "x" + new string('}', levels);
        Stops(Render(nested)).ShouldBe("1:x|0:");

        string unclosed = string.Concat(Enumerable.Repeat("${1:", Snippet.MaxText / 4));
        Render(unclosed).Text.ShouldBe(unclosed);
    }

    /// <summary>A snippet as it goes in, by default at the start of an unindented line.</summary>
    private static RenderedSnippet Render(
        string snippet,
        string lineIndentation = "",
        Indentation? indentation = null,
        string lineBreak = "\n"
    ) =>
        Snippet
            .Parse(snippet)
            .ShouldNotBeNull()
            .Render(new SnippetContext(lineIndentation, indentation ?? Indentation.Spaces(4), lineBreak))
            .ShouldNotBeNull();

    /// <summary>The stops as "number:text", in order, separated by "|".</summary>
    private static string Stops(RenderedSnippet rendered) =>
        string.Join("|", rendered.Stops.Select(s => $"{s.Index}:{rendered.Text[s.Start..s.End]}"));
}

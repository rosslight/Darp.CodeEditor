using System.Text.Json;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The Markdown of hovers: the subset that servers write, read into blocks, and the hover results it comes in.
/// </summary>
public sealed class MarkdownTests
{
    [Fact]
    public void Emphasis_inline_code_and_escapes_style_their_text()
    {
        Spans("*a* **b** ***c*** _d_ snake_case 2 * 3 `x*y` ``a`b`` \\*e\\*")
            .ShouldBe([
                new("a", SpanStyle.Italic),
                new(" "),
                new("b", SpanStyle.Bold),
                new(" "),
                new("c", SpanStyle.Bold | SpanStyle.Italic),
                new(" "),
                new("d", SpanStyle.Italic),
                new(" snake_case 2 * 3 "),
                new("x*y", SpanStyle.Code),
                new(" "),
                new("a`b", SpanStyle.Code),
                new(" *e*"),
            ]);
        Spans("*a **b** c*")
            .ShouldBe([
                new("a ", SpanStyle.Italic),
                new("b", SpanStyle.Bold | SpanStyle.Italic),
                new(" c", SpanStyle.Italic),
            ]);
    }

    [Fact]
    public async Task Emphasis_that_never_closes_stays_text_however_much_there_is()
    {
        // Each delimiter could open, and none can close: pairing them must not try every combination.
        string text = string.Concat(Enumerable.Repeat("*a _b **c ", 5_000));

        IReadOnlyList<HoverSpan> spans = await Wait.ForAsync(Task.Run(() => Spans(text)));

        spans.ShouldBe([new(text)]);
    }

    [Fact]
    public void Links_open_web_pages_only()
    {
        var page = new Uri("https://example.com/a_(b)");
        Spans("[the **page**](https://example.com/a_(b) \"title\") [a file](file:///etc/hosts) <http://example.org>")
            .ShouldBe([
                new("the ", Link: page),
                new("page", SpanStyle.Bold, page),
                new(" a file "),
                new("http://example.org", Link: new Uri("http://example.org")),
            ]);
    }

    [Fact]
    public void Lists_nest_by_their_indentation_and_paragraphs_join_their_lines()
    {
        IReadOnlyList<HoverBlock> blocks = Parse(
            "- one\n  - two\n    more\n- three\n\n1. first\n2) second\n\nafter  \nbreak\\\nend"
        );

        blocks.ShouldBe(
            [
                new HoverParagraph([new("one")], 1, "•"),
                new HoverParagraph([new("two more")], 2, "•"),
                new HoverParagraph([new("three")], 1, "•"),
                new HoverParagraph([new("first")], 1, "1."),
                new HoverParagraph([new("second")], 1, "2."),
                new HoverParagraph([new("after\nbreak\nend")]),
            ],
            new BlockComparer()
        );
    }

    [Fact]
    public void Code_blocks_headings_and_rules_are_blocks_of_their_own()
    {
        IReadOnlyList<HoverBlock> blocks = Parse("# Title\n```json\n{ \"a\": 1 }\n\n  x\n```\ntext\n---\n~~~\nopen");

        blocks.ShouldBe(
            [
                new HoverParagraph([new("Title", SpanStyle.Bold)]),
                Code("{ \"a\": 1 }\n\n  x"),
                new HoverParagraph([new("text")]),
                new HoverRule(),
                Code("open"),
            ],
            new BlockComparer()
        );
    }

    [Fact]
    public void A_hover_reads_Markdown_plain_text_and_marked_strings()
    {
        TextSnapshot snapshot = TextSnapshot.FromString("ab\ncd");

        HoverInfo? markdown = Read(
            """{ "contents": { "kind": "markdown", "value": "**a**" }, "range": { "start": { "line": 1, "character": 0 }, "end": { "line": 1, "character": 2 } } }""",
            snapshot
        );
        HoverInfo? plain = Read("""{ "contents": { "kind": "plaintext", "value": "*a*" } }""", snapshot);
        HoverInfo? marked = Read("""{ "contents": ["a", { "language": "json", "value": "x```y" }, ""] }""", snapshot);

        markdown.ShouldNotBeNull().Contents.ShouldBe([new HoverText("**a**", true)]);
        markdown.Range.ShouldBe(new TextRange(3, 2));
        plain.ShouldNotBeNull().Contents.ShouldBe([new HoverText("*a*", false)]);
        marked
            .ShouldNotBeNull()
            .Contents.ShouldBe([new HoverText("a", true), new HoverText("````json\nx```y\n````", true)]);
        Read("null", snapshot).ShouldBeNull();
        Read("""{ "contents": [] }""", snapshot).ShouldBeNull();
        Should.Throw<JsonException>(() => Read("""{ "contents": 1 }""", snapshot));
    }

    private static IReadOnlyList<HoverSpan> Spans(string text) => Markdown.Inline(text);

    private static IReadOnlyList<HoverBlock> Parse(string markdown) => Markdown.Parse(markdown, code => Code(code));

    private static HoverCode Code(string code) => new([new CodeRun(code, null)]);

    private static HoverInfo? Read(string json, TextSnapshot snapshot) =>
        HoverInfo.Read(JsonElement.Parse(json), snapshot);

    /// <summary>Compares blocks by their content: records with lists compare the lists by reference.</summary>
    private sealed class BlockComparer : IEqualityComparer<HoverBlock>
    {
        public bool Equals(HoverBlock? x, HoverBlock? y) =>
            (x, y) switch
            {
                (HoverParagraph a, HoverParagraph b) => a.Level == b.Level
                    && a.Marker == b.Marker
                    && a.Spans.SequenceEqual(b.Spans),
                (HoverCode a, HoverCode b) => a.Runs.SequenceEqual(b.Runs),
                (HoverRule, HoverRule) => true,
                _ => false,
            };

        public int GetHashCode(HoverBlock block) => 0;
    }
}

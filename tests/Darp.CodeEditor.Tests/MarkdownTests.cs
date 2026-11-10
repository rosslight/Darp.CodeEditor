using System.Text.Json;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;
using Darp.CodeEditor.Popups;
using TextMateSharp.Internal.Types;

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
    public void Reading_a_hovers_Markdown_stops_part_way_once_the_hover_ends()
    {
        using var ended = new CancellationTokenSource();
        int codeBlocks = 0;

        // The hover ends while its first code block is read.
        Should.Throw<OperationCanceledException>(() =>
            Markdown.Parse(
                "first\n\n```\na\n```\n\nsecond\n\n```\nb\n```",
                lines =>
                {
                    codeBlocks++;
                    ended.Cancel();
                    return Code(lines);
                },
                ended.Token
            )
        );

        codeBlocks.ShouldBe(1);
        // A hover's prose stops too.
        Should.Throw<OperationCanceledException>(() =>
            HoverBlocks.Of(Hover("first\n\nsecond", markdown: true), null, new HoverBudget(), ended.Token)
        );
    }

    [Fact]
    public async Task Highlighting_a_code_block_stops_part_way_once_the_hover_ends()
    {
        TextMateGrammar grammar = await JsonGrammarAsync();
        using var ended = new CancellationTokenSource();
        int read = 0;

        // The hover ends once the first line is highlighted.
        Should.Throw<OperationCanceledException>(() => HoverBlocks.Highlight(Lines(), grammar, ended.Token));

        read.ShouldBe(2);

        IEnumerable<string> Lines()
        {
            read++;
            yield return "{";
            ended.Cancel();
            read++;
            yield return "  \"a\": 1";
            read++;
            yield return "}";
        }
    }

    [Fact]
    public void A_hover_shows_at_most_its_characters_lines_and_runs_cut_with_an_ellipsis()
    {
        CancellationToken going = TestContext.Current.CancellationToken;
        var diagnostic = new Diagnostic(
            TextSnapshot.FromString(""),
            default,
            DiagnosticSeverity.Error,
            null,
            new string('m', 100_000)
        );
        // Emphasis that alternates: two runs every four characters, 800 runs on one line.
        string emphasis = string.Concat(Enumerable.Repeat("*x* ", 400));

        string characters = TextOf(HoverBlocks.Of([diagnostic], new HoverBudget()));
        string lines = TextOf(
            HoverBlocks.Of(
                Hover(string.Join('\n', Enumerable.Repeat("x", 1_000)), false),
                null,
                new HoverBudget(),
                going
            )
        );
        IReadOnlyList<HoverBlock> runs = HoverBlocks.Of(Hover(emphasis, true), null, new HoverBudget(), going);
        // The diagnostics' messages come first, and leave nothing to the server's hover.
        var shared = new HoverBudget();
        HoverBlocks.Of([diagnostic], shared);
        IReadOnlyList<HoverBlock> after = HoverBlocks.Of(Hover("more", true), null, shared, going);

        characters.Length.ShouldBe(HoverBudget.MaxCharacters + 1);
        characters.ShouldEndWith("…");
        lines.Count(c => c == '\n').ShouldBe(HoverBudget.MaxLines - 1);
        lines.ShouldEndWith("…");
        HoverParagraph paragraph = runs.ShouldHaveSingleItem().ShouldBeOfType<HoverParagraph>();
        paragraph.Spans.Count.ShouldBe(HoverBudget.MaxRuns);
        paragraph.Spans[^1].Text.ShouldBe("…");
        after.ShouldBeEmpty();
    }

    [Fact]
    public void A_diagnostic_is_cut_to_the_budget_before_its_message_and_code_are_put_together()
    {
        var diagnostic = new Diagnostic(
            TextSnapshot.FromString(""),
            default,
            DiagnosticSeverity.Error,
            "CS0001",
            new string('m', 10_000_000)
        );

        long before = GC.GetAllocatedBytesForCurrentThread();
        string shown = TextOf(HoverBlocks.Of([diagnostic], new HoverBudget()));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        shown.ShouldBe(new string('m', HoverBudget.MaxCharacters) + "…");
        // The message alone takes 20 MB: putting it together with its code would copy all of it.
        allocated.ShouldBeLessThan(1_000_000);
        TextOf(HoverBlocks.Of([diagnostic with { Message = "short" }], new HoverBudget())).ShouldBe("short (CS0001)");
    }

    /// <summary>The line breaks of the library, and one where only the layout of text breaks lines.</summary>
    public static TheoryData<string> LineBreaks => ["\r\n", "\n", "\r", "\u2028"];

    [Theory]
    [MemberData(nameof(LineBreaks))]
    public void A_hovers_lines_are_counted_at_every_line_break(string lineBreak)
    {
        // Two spaces end each line of Markdown with a hard break; plain text keeps its line breaks.
        string text = string.Join(lineBreak, Enumerable.Repeat("x  ", 400));
        var diagnostic = new Diagnostic(TextSnapshot.FromString(""), default, DiagnosticSeverity.Error, null, text);

        string markdown = TextOf(
            HoverBlocks.Of(Hover(text, true), null, new HoverBudget(), TestContext.Current.CancellationToken)
        );
        string message = TextOf(HoverBlocks.Of([diagnostic], new HoverBudget()));

        LineCount(markdown).ShouldBe(HoverBudget.MaxLines);
        markdown.ShouldEndWith("…");
        LineCount(message).ShouldBe(HoverBudget.MaxLines);
        message.ShouldEndWith("…");
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        // The budget ends between the two halves of the emoji.
        string text = new string('x', HoverBudget.MaxCharacters - 1) + "😀tail";
        var diagnostic = new Diagnostic(TextSnapshot.FromString(""), default, DiagnosticSeverity.Error, null, text);
        string expected = new string('x', HoverBudget.MaxCharacters - 1) + "…";

        TextOf(HoverBlocks.Of([diagnostic], new HoverBudget())).ShouldBe(expected);
        TextOf(HoverBlocks.Of(Hover(text, false), null, new HoverBudget(), TestContext.Current.CancellationToken))
            .ShouldBe(expected);
        TextOf(HoverBlocks.Of(Hover(text, true), null, new HoverBudget(), TestContext.Current.CancellationToken))
            .ShouldBe(expected);
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

    private static IReadOnlyList<HoverBlock> Parse(string markdown) =>
        Markdown.Parse(markdown, Code, TestContext.Current.CancellationToken);

    private static HoverCode Code(string code) => new([new CodeRun(code, null)]);

    private static HoverCode Code(IReadOnlyList<string> lines) => Code(string.Join('\n', lines));

    private static HoverInfo Hover(string text, bool markdown) => new([new HoverText(text, markdown)], null);

    /// <summary>The text that blocks show, paragraphs on lines of their own.</summary>
    private static string TextOf(IEnumerable<HoverBlock> blocks) =>
        string.Join(
            '\n',
            blocks.Select(block =>
                block switch
                {
                    HoverParagraph paragraph => string.Concat(paragraph.Spans.Select(span => span.Text)),
                    HoverCode code => string.Concat(code.Runs.Select(run => run.Text)),
                    _ => "",
                }
            )
        );

    /// <summary>The lines of a text as the layout of text breaks them.</summary>
    private static int LineCount(string text) =>
        1
        + text.Replace("\r\n", "\n").Count(c => c is '\n' or '\r' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029');

    private static async Task<TextMateGrammar> JsonGrammarAsync()
    {
        IRawGrammar source = (await CodeLanguage.Json.Loaded).Grammar.ShouldNotBeNull();
        ColorThemeFile theme = (await CodeEditorColorTheme.LightPlus.Loaded).ShouldNotBeNull();
        return TextMateGrammar.Load(theme, source);
    }

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

using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Guessing a text's indentation as VS Code's <c>editor.detectIndentation</c> does, with cases from VS Code's own tests
/// of its guesser.
/// </summary>
public sealed class IndentationGuesserTests
{
    /// <summary>A fallback whose size no text gives, so that a test sees which parts were guessed.</summary>
    private static readonly Indentation s_spaces = Indentation.Spaces(13);

    private static readonly Indentation s_tabs = Indentation.Tabs(13);

    public static TheoryData<string, string[], Indentation, Indentation> Cases =>
        new()
        {
            { "no indented lines", ["x", "x", "x"], s_spaces, s_spaces },
            { "no indented lines, tabs by default", ["x", "x", "x"], s_tabs, s_tabs },
            { "one line with a tab", ["\tx", "x", "x", "x"], s_spaces, Indentation.Tabs(13) },
            { "one line with 2 spaces", ["  x", "x", "x", "x"], s_tabs, Indentation.Spaces(2) },
            {
                "as many tabs as spaces: the default decides",
                ["\tx", "  x", "\tx", "  x"],
                s_spaces,
                Indentation.Spaces(2)
            },
            { "as many tabs as spaces, tabs by default", ["\tx", "  x", "\tx", "  x"], s_tabs, s_tabs },
            {
                "steps of 1, 3 and 4",
                ["x", " x", " x", " x", " x", " x", "x", "   x", "    x", "    x"],
                s_spaces,
                Indentation.Spaces(3)
            },
            {
                "alignment after a comma is no hint",
                [
                    "/* REQUIRE */",
                    "",
                    "const foo = require ( 'foo' ),",
                    "      bar = require ( 'bar' );",
                    "",
                    "function myFn () {",
                    "",
                    "  const asd = 1,",
                    "        dsa = 2;",
                    "",
                    "  return bar ( foo ( asd ) );",
                    "",
                    "}",
                ],
                s_spaces,
                Indentation.Spaces(2)
            },
            { "an inline space is no indentation", ["x x", "  x", "  x", "    x"], s_spaces, Indentation.Spaces(2) },
            {
                "2 wins over 4 with two thirds of its steps, as in YAML",
                ["a:", "    b: 1", "c:", "    d:", "      e: 1", "f:", "  g: 1"],
                Indentation.Spaces(4),
                Indentation.Spaces(2)
            },
            {
                "4 wins where it is the most",
                [
                    "function foo() {",
                    "    let a = 1;",
                    "    if (true) {",
                    "        log(a);",
                    "    }",
                    "    const obj = {",
                    "      x: 1,",
                    "      y: 2",
                    "    };",
                    "}",
                ],
                Indentation.Spaces(2),
                Indentation.Spaces(4)
            },
        };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_text_decides_what_it_can_and_the_fallback_the_rest(
        string because,
        string[] lines,
        Indentation fallback,
        Indentation expected
    ) => IndentationGuesser.Guess(string.Join('\n', lines), fallback).ShouldBe(expected, because);

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Every_kind_of_line_break_separates_lines(string lineBreak) =>
        IndentationGuesser
            .Guess(string.Join(lineBreak, "{", "  \"a\": {", "    \"b\": 1", "  }", "}"), s_spaces)
            .ShouldBe(Indentation.Spaces(2));

    [Fact]
    public void Only_the_first_10000_lines_count()
    {
        string text = string.Concat(Enumerable.Repeat("x\n", 10_000)) + "  x\n  x\n";

        IndentationGuesser.Guess(text, s_spaces).ShouldBe(s_spaces);
    }
}

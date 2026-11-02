using AvaloniaEdit.Document;
using Darp.CodeEditor.Editing;
using Shouldly;

namespace Darp.CodeEditor.Tests;

public sealed class BracketMatcherTests
{
    private static readonly IReadOnlyList<BracketPair> s_brackets = LanguageConfiguration.Default.Brackets;

    [Theory]
    [InlineData("f(a, (b))|", 1, 8)]
    [InlineData("f|(a, (b))", 1, 8)]
    [InlineData("f(a, |(b))", 5, 7)]
    [InlineData("{ [ ] }|", 0, 6)]
    public void Finds_the_bracket_next_to_the_caret_and_its_match(string marked, int open, int close)
    {
        var document = new TextDocument(marked.Replace("|", ""));
        BracketMatcher
            .FindPairAt(document, marked.IndexOf('|'), s_brackets, _ => TokenKind.Code)
            .ShouldBe((open, close));
    }

    [Fact]
    public void Ignores_brackets_in_strings_and_comments()
    {
        const string text = "f(\")\", x) // (";
        var document = new TextDocument(text);
        // The quoted bracket and the comment, as highlighting reports them.
        TokenKind KindAt(int offset) =>
            offset is >= 2 and <= 4 ? TokenKind.String
            : offset >= 10 ? TokenKind.Comment
            : TokenKind.Code;

        BracketMatcher.FindPairAt(document, 2, s_brackets, KindAt).ShouldBe((1, 8));
        BracketMatcher.FindPairAt(document, text.Length, s_brackets, KindAt).ShouldBeNull();
    }

    [Fact]
    public void Finds_no_pair_away_from_brackets_or_for_an_unmatched_one()
    {
        var document = new TextDocument("a (b c");
        BracketMatcher.FindPairAt(document, 1, s_brackets, _ => TokenKind.Code).ShouldBeNull();
        BracketMatcher.FindPairAt(document, 3, s_brackets, _ => TokenKind.Code).ShouldBeNull();
    }
}

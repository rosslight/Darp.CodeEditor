using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Tests;

/// <summary>The word at the caret and its whole-word occurrences.</summary>
public sealed class WordRulesTests
{
    [Theory]
    [InlineData("fo|o bar", "«foo» bar")]
    [InlineData("foo| bar", "«foo» bar")]
    [InlineData("foo |bar", "foo «bar»")]
    [InlineData("foo.|bar", "foo.«bar»")]
    // Only a word, not a run of separators or spaces.
    [InlineData("a =|= b", null)]
    [InlineData("foo | bar", null)]
    [InlineData("|", null)]
    public void WordAt_is_the_run_of_word_characters_that_the_caret_touches(string marked, string? selected)
    {
        (string line, TextSelection caret) = MarkedText.Parse(marked);
        TextRange? word = WordRules.WordAt(line, caret.Caret);
        (word is { } found ? MarkedText.Format(line, new TextSelection(found.Start, found.End)) : null).ShouldBe(
            selected
        );
    }

    /// <summary>The scan reads the text in blocks, so a word across two of them, or right after one, counts once.</summary>
    [Theory]
    [InlineData(4094)]
    [InlineData(4095)]
    [InlineData(4096)]
    public void Occurrences_are_whole_words_also_across_the_scans_blocks(int padding)
    {
        string text = new string(' ', padding) + "word words word";

        List<TextRange> found = WordRules.Occurrences(new StringReader(text), "word", 10);

        found.ShouldBe([new TextRange(padding, 4), new TextRange(padding + 11, 4)]);
    }

    [Fact]
    public void Occurrences_stop_at_the_most_asked_for() =>
        WordRules.Occurrences(new StringReader("a a a"), "a", 2).ShouldBe([new TextRange(0, 1), new TextRange(2, 1)]);
}

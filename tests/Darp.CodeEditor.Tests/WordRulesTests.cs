using AvaloniaEdit.Document;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Words as runs of word characters and runs of separators, with spaces and tabs between them: what double-click,
/// Ctrl+D, moving by words and deleting by words take.
/// </summary>
public sealed class WordRulesTests
{
    [Theory]
    // The word that the click touches, the one on the left first.
    [InlineData("fo|o bar", "«foo» bar")]
    [InlineData("foo| bar", "«foo» bar")]
    [InlineData("foo |bar", "foo «bar»")]
    [InlineData("foo|.bar", "«foo».bar")]
    [InlineData("foo.|bar", "foo.«bar»")]
    [InlineData("$|foo", "$«foo»")]
    [InlineData("grö|ße", "«größe»")]
    // A run of separators only from inside it.
    [InlineData("a =|= b", "a «==» b")]
    [InlineData("a |== b", "a «==» b")]
    [InlineData("a ==| b", "a ==« »b")]
    // Otherwise what lies between the neighbors, up to the line's ends.
    [InlineData("foo  | bar", "foo«   »bar")]
    [InlineData("  |  foo", "«    »foo")]
    [InlineData("foo  |", "foo«  »")]
    [InlineData("|", "|")]
    public void A_double_click_selects_the_word_it_touches_or_else_what_lies_between_words(
        string clicked,
        string selected
    )
    {
        (string line, TextSelection click) = MarkedText.Parse(clicked);
        TextRange word = WordRules.DoubleClicked(line, click.Caret);
        MarkedText.Format(line, new TextSelection(word.Start, word.End)).ShouldBe(selected);
    }

    [Theory]
    [InlineData("foo bar|", "foo |")]
    [InlineData("foo |", "|")]
    // Two or more spaces go alone, back to the line's start on a line of whitespace only.
    [InlineData("foo   |", "foo|")]
    [InlineData("a\n    |", "a\n|")]
    // At the line's start, the line break.
    [InlineData("a\n|b", "a|b")]
    [InlineData("foo.bar|", "foo.|")]
    [InlineData("foo.|", "foo|")]
    [InlineData("a == |", "a |")]
    [InlineData("|foo", "|foo")]
    public void Ctrl_Backspace_deletes_back_to_the_start_of_a_word_or_a_run_of_spaces(string before, string after) =>
        Delete(before, WordRules.DeleteLeft).ShouldBe(after);

    [Theory]
    [InlineData("|foo bar", "| bar")]
    // Spaces after the caret go alone, even one.
    [InlineData("| bar", "|bar")]
    [InlineData("|.bar", "|bar")]
    // At the line's end, the line break and the next line's indentation.
    [InlineData("foo|\n  bar", "foo|bar")]
    [InlineData("foo|\n", "foo|")]
    [InlineData("foo|", "foo|")]
    public void Ctrl_Delete_deletes_up_to_the_end_of_a_word_or_the_spaces_after_the_caret(
        string before,
        string after
    ) => Delete(before, WordRules.DeleteRight).ShouldBe(after);

    [Theory]
    // Inside the word it started with, the selection stays that word.
    [InlineData("one two three", 4, 7, 5, 4, 7)]
    // To the right, up to the end of the word under the pointer, from the start word's start.
    [InlineData("one two three", 4, 7, 10, 4, 13)]
    // To the left, from the start word's end.
    [InlineData("one two three", 4, 7, 1, 7, 0)]
    // Between words, up to the pointer.
    [InlineData("one two  three", 0, 3, 8, 0, 8)]
    public void A_drag_after_a_double_click_grows_by_words(
        string line,
        int startWordStart,
        int startWordEnd,
        int column,
        int anchor,
        int caret
    ) =>
        WordRules
            .DragTo(TextRange.FromBounds(startWordStart, startWordEnd), line, 0, column)
            .ShouldBe(new TextSelection(anchor, caret));

    [Theory]
    [InlineData("fo|o bar", "«foo» bar")]
    [InlineData("foo| bar", "«foo» bar")]
    [InlineData("foo |bar", "foo «bar»")]
    [InlineData("foo.|bar", "foo.«bar»")]
    // Only a word, not a run of separators or spaces.
    [InlineData("a =|= b", null)]
    [InlineData("foo | bar", null)]
    [InlineData("|", null)]
    public void Ctrl_D_selects_the_word_of_word_characters_that_the_caret_touches(string marked, string? selected)
    {
        (string line, TextSelection caret) = MarkedText.Parse(marked);
        TextRange? word = WordRules.WordAt(line, caret.Caret);
        (word is { } found ? MarkedText.Format(line, new TextSelection(found.Start, found.End)) : null).ShouldBe(
            selected
        );
    }

    [Theory]
    [InlineData("|alpha beta", "alpha| beta")]
    [InlineData("alpha| beta", "alpha beta|")]
    [InlineData("|foo.bar", "foo|.bar")]
    // A single separator before a word goes with the word.
    [InlineData("foo|.bar", "foo.bar|")]
    [InlineData("foo|..bar", "foo..|bar")]
    [InlineData("a|  ", "a  |")]
    // At a line's end, to the end of the next line's first word; at the text's end, nowhere.
    [InlineData("foo|\n  bar", "foo\n  bar|")]
    [InlineData("foo  |", "foo  |")]
    public void Ctrl_Right_moves_to_the_end_of_the_next_word(string before, string after) =>
        Move(before, WordRules.MoveRight).ShouldBe(after);

    [Theory]
    [InlineData("alpha beta|", "alpha |beta")]
    [InlineData("alpha |beta", "|alpha beta")]
    [InlineData("foo.bar|", "foo.|bar")]
    // A single separator right after a word character goes with that word, whatever comes after it.
    [InlineData("foo.|bar", "|foo.bar")]
    [InlineData("foo .|bar", "foo |.bar")]
    [InlineData("foo. |", "|foo. ")]
    [InlineData("3 +|5", "3 |+5")]
    [InlineData("foo..|bar", "foo|..bar")]
    [InlineData("(|foo", "|(foo")]
    [InlineData("  |foo", "|  foo")]
    // At a line's start, to the start of the line before's last word; at the text's start, nowhere.
    [InlineData("a foo\n|bar", "a |foo\nbar")]
    [InlineData("|foo", "|foo")]
    public void Ctrl_Left_moves_to_the_start_of_the_word_before(string before, string after) =>
        Move(before, WordRules.MoveLeft).ShouldBe(after);

    private static string Delete(string marked, Func<TextDocument, int, TextRange?> rule)
    {
        (string text, TextSelection caret) = MarkedText.Parse(marked);
        var document = new TextDocument(text);
        if (rule(document, caret.Caret) is not { } range)
            return marked;
        document.Remove(range.Start, range.Length);
        return MarkedText.Format(document.Text, new TextSelection(range.Start));
    }

    private static string Move(string marked, Func<TextDocument, int, int> rule)
    {
        (string text, TextSelection caret) = MarkedText.Parse(marked);
        return MarkedText.Format(text, new TextSelection(rule(new TextDocument(text), caret.Caret)));
    }
}

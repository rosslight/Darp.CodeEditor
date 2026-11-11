using AvaloniaEdit.Document;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Tests;

/// <summary>VS Code's words for double-click and for deleting by words: the cases of its <c>cursorWordOperations.ts</c>.</summary>
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
    public void A_double_click_selects_as_in_VS_Code(string clicked, string selected)
    {
        (string line, TextSelection click) = MarkedText.Parse(clicked);
        (int start, int end) = WordRules.WordAt(line, click.Caret);
        MarkedText.Format(line, new TextSelection(start, end)).ShouldBe(selected);
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
    public void Ctrl_Backspace_deletes_as_VS_Codes_deleteWordLeft(string before, string after) =>
        Delete(before, WordRules.DeleteWordLeft).ShouldBe(after);

    [Theory]
    [InlineData("|foo bar", "| bar")]
    // Spaces after the caret go alone, even one.
    [InlineData("| bar", "|bar")]
    [InlineData("|.bar", "|bar")]
    // At the line's end, the line break and the next line's indentation.
    [InlineData("foo|\n  bar", "foo|bar")]
    [InlineData("foo|\n", "foo|")]
    [InlineData("foo|", "foo|")]
    public void Ctrl_Delete_deletes_as_VS_Codes_deleteWordRight(string before, string after) =>
        Delete(before, WordRules.DeleteWordRight).ShouldBe(after);

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
        WordRules.DragTo(TextRange.FromBounds(startWordStart, startWordEnd), line, 0, column).ShouldBe((anchor, caret));

    [Theory]
    [InlineData("fo|o bar", "«foo» bar")]
    [InlineData("foo| bar", "«foo» bar")]
    [InlineData("foo |bar", "foo «bar»")]
    [InlineData("foo.|bar", "foo.«bar»")]
    // Only a word, not a run of separators or spaces.
    [InlineData("a =|= b", null)]
    [InlineData("foo | bar", null)]
    [InlineData("|", null)]
    public void Ctrl_D_selects_VS_Codes_word_at_the_caret(string marked, string? selected)
    {
        (string line, TextSelection caret) = MarkedText.Parse(marked);
        (int Start, int End)? word = WordRules.WordAtCaret(line, caret.Caret);
        (word is { } found ? MarkedText.Format(line, new TextSelection(found.Start, found.End)) : null).ShouldBe(
            selected
        );
    }

    private static string Delete(string marked, Func<TextDocument, int, TextRange?> rule)
    {
        (string text, TextSelection caret) = MarkedText.Parse(marked);
        var document = new TextDocument(text);
        if (rule(document, caret.Caret) is not { } range)
            return marked;
        document.Remove(range.Start, range.Length);
        return MarkedText.Format(document.Text, new TextSelection(range.Start));
    }
}

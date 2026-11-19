using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Tests;

/// <summary>The part of each line that the editor edits, as AvaloniaEdit's own edits leave a long line's hidden tail.</summary>
[Collection(UiFixture.Collection)]
public sealed class VisibleTextTests(UiFixture ui)
{
    /// <summary>Where the long line of the text starts.</summary>
    private const int LongLine = 6;

    private readonly UiFixture _ui = ui;

    /// <summary>
    /// The editor's check before a delete stands in for AvaloniaEdit's, which is internal: a selection's text that
    /// AvaloniaEdit replaces goes as a whole exactly where the check says it can, so that a change of its rule fails here.
    /// </summary>
    [Fact]
    public Task The_check_before_a_delete_answers_as_AvaloniaEdits_own_deletes() =>
        _ui.Run(() =>
        {
            string text = "short\n" + new string('a', 3_500) + "\nend";
            int tail = LongLine + VisibleText.LineLimit;
            int lineEnd = LongLine + 3_500;
            (int Offset, int Length)[] ranges =
            [
                // Read-only text: the second to the fourth character.
                (0, 1),
                (1, 1),
                (3, 2),
                (4, 1),
                // The edge of the long line's hidden tail.
                (tail - 1, 1),
                (tail - 2, 2),
                (tail, 1),
                (tail - 1, 2),
                // The line break after the hidden tail, and the line after it.
                (lineEnd, 1),
                (lineEnd + 1, 3),
                // Across lines.
                (4, 3),
                (LongLine, VisibleText.LineLimit),
                (LongLine, VisibleText.LineLimit + 1),
                (5, lineEnd - 5 + 2),
            ];
            var answers = new List<bool>();

            foreach ((int offset, int length) in ranges)
            {
                TextArea textArea = AreaOf(text);
                bool check = VisibleText.CanDelete(textArea, offset, length);
                textArea.Selection = Selection.Create(textArea, offset, offset + length);
                textArea.Selection.ReplaceSelectionWithText("");
                bool avaloniaEdit = textArea.Document.TextLength == text.Length - length;
                check.ShouldBe(avaloniaEdit, $"{length} at {offset}");
                answers.Add(avaloniaEdit);
            }

            // Both answers occur, so that the comparison means something.
            answers.ShouldContain(true);
            answers.ShouldContain(false);
        });

    /// <summary>A text area of <paramref name="text"/>, with the editor's long-line limit and read-only text.</summary>
    private static TextArea AreaOf(string text)
    {
        var buffer = new TextDocument(text);
        var textArea = new TextArea { Document = buffer };
        textArea.Options.LongLineLimit = VisibleText.LineLimit;
        var protectedText = new TextSegmentReadOnlySectionProvider<TextSegment>(buffer);
        protectedText.Segments.Add(new TextSegment { StartOffset = 1, Length = 3 });
        textArea.ReadOnlySectionProvider = protectedText;
        return textArea;
    }
}

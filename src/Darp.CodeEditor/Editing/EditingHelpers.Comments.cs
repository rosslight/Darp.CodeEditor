using AvaloniaEdit.Document;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

// The block comment, as VS Code's blockCommentCommand does it, and Ctrl+/ with it in languages without a line comment,
// as VS Code's lineCommentCommand falls back to it. Both share the edits that add and remove the comment.
internal sealed partial class EditingHelpers
{
    /// <summary>
    /// Whether comments get a space inside their tokens, as VS Code's <c>editor.comments.insertSpace</c> does at its
    /// default. The editor has no such option yet: Ctrl+/ puts a space after the line comment too.
    /// </summary>
    private static bool CommentsInsertSpace => true;

    /// <summary>
    /// Toggles the language's block comment around the selection, as VS Code's <c>blockComment</c> does, in a step of
    /// its own: removes the comment that the selection is in, from the start token before its start to the end token
    /// after its end, or else puts one around it, with a space inside each token, and keeps the commented text
    /// selected. Without a selection, an empty comment goes in at the caret, and the caret inside it. Nothing for a
    /// language without a block comment.
    /// </summary>
    public void BlockComment()
    {
        if (Configuration.BlockComment is not { } block)
            return;
        _undo.Edit(EditKind.Other, stopBefore: true, () => DoBlockComment(block), stopAfter: true);
    }

    private void DoBlockComment(CharacterPair block)
    {
        (int anchor, int caret) = SelectionEnds();
        int start = Math.Min(anchor, caret);
        int end = Math.Max(anchor, caret);
        DocumentLine startLine = Buffer.GetLineByOffset(start);
        DocumentLine endLine = Buffer.GetLineByOffset(end);
        string startText = Buffer.GetText(startLine);
        string endText = Buffer.GetText(endLine);
        (string open, string close) = block;
        int startColumn = start - startLine.Offset;
        int endColumn = end - endLine.Offset;
        int openIndex = LastIndexOf(startText, open, startColumn + open.Length);
        int closeIndex = IndexOf(endText, close, endColumn - close.Length);
        if (openIndex != -1 && closeIndex != -1)
        {
            // Another end token between them: the selection is not in one comment, and gets one.
            bool between =
                startLine == endLine
                    ? Substring(startText, openIndex + open.Length, closeIndex)
                        .Contains(close, StringComparison.Ordinal)
                    : startText[(openIndex + open.Length)..].Contains(close, StringComparison.Ordinal)
                        || endText[..closeIndex].Contains(close, StringComparison.Ordinal);
            if (between)
                openIndex = closeIndex = -1;
        }
        List<(int Offset, int Length, string Text)> edits;
        if (openIndex != -1 && closeIndex != -1)
        {
            string openToken = open;
            string closeToken = close;
            if (
                CommentsInsertSpace
                && openIndex + open.Length < startText.Length
                && startText[openIndex + open.Length] == ' '
            )
                openToken += " ";
            if (CommentsInsertSpace && closeIndex > 0 && endText[closeIndex - 1] == ' ')
            {
                closeToken = " " + closeToken;
                closeIndex--;
            }
            edits = RemoveBlockCommentEdits(
                startLine.Offset + openIndex,
                openToken.Length,
                endLine.Offset + closeIndex,
                closeToken.Length
            );
        }
        else
            edits = AddBlockCommentEdits(start, end, open, close, CommentsInsertSpace);
        if (!IsWithinText(edits))
            return;
        ReplaceInOneChange(edits);
        // As VS Code takes the selection from the inverse edits: between the tokens' places, or after an empty
        // comment's start token and space.
        (int newAnchor, int newCaret) = edits switch
        {
            [(_, 0, string first), (int second, 0, _)] => (edits[0].Offset + first.Length, second + first.Length),
            [(int first, int removed, ""), (int second, _, "")] => (first, second - removed),
            [(int offset, 0, string inserted)] => (
                offset + inserted.Length - close.Length - 1,
                offset + inserted.Length - close.Length - 1
            ),
            _ => (edits[0].Offset, edits[0].Offset),
        };
        Select(newAnchor, newCaret);
    }

    /// <summary>
    /// Ctrl+/ in a language without a line comment, as VS Code's line comment falls back to the block comment: removes
    /// the block comment that the selection's lines start or end in, or that the caret's line has; otherwise puts one
    /// around the lines, from the first one's text to the end of the last, or on a line of whitespace only, an empty
    /// one with the caret inside. A last line that the selection touches only at its start takes no part.
    /// </summary>
    private void ToggleBlockCommentOnLines(CharacterPair block)
    {
        (int anchor, int caret) = SelectionEnds();
        int start = Math.Min(anchor, caret);
        int end = Math.Max(anchor, caret);
        DocumentLine startLine = Buffer.GetLineByOffset(start);
        DocumentLine endLine = Buffer.GetLineByOffset(end);
        bool moveEndDown = false;
        if (startLine != endLine && end == endLine.Offset)
        {
            moveEndDown = true;
            endLine = endLine.PreviousLine;
            end = endLine.EndOffset;
        }
        bool reversed = anchor > caret;
        (int trackedAnchor, int trackedCaret) = reversed ? (end, start) : (start, end);
        (string open, string close) = block;
        int deltaColumn = 0;
        List<(int Offset, int Length, string Text)> edits =
            RemoveBlockCommentOnLines(start, end, startLine, endLine, open, close) ?? [];
        if (edits.Count == 0)
        {
            string startText = Buffer.GetText(startLine);
            int firstText = LeadingWhitespace(startText);
            edits =
                start == end
                    ? AddBlockCommentEdits(
                        startLine.Offset + firstText,
                        startLine.EndOffset,
                        open,
                        close,
                        CommentsInsertSpace
                    )
                    // VS Code's first text column of a line of whitespace only is none, which counts as its start.
                    : AddBlockCommentEdits(
                        startLine.Offset + (firstText == startText.Length ? 0 : firstText),
                        endLine.EndOffset,
                        open,
                        close,
                        CommentsInsertSpace
                    );
            // After an empty comment's start token and space.
            if (edits.Count == 1)
                deltaColumn = open.Length + 1;
        }
        if (!IsWithinText(edits))
            return;
        bool atLineEnd = start == end && end == endLine.EndOffset;
        (int newAnchor, int newCaret) = TrackedSelection.Map(trackedAnchor, trackedCaret, atLineEnd, edits);
        ReplaceInOneChange(edits);
        if (moveEndDown)
            SetEnd(ref newAnchor, ref newCaret, Buffer.GetLineByOffset(Math.Max(newAnchor, newCaret)).NextLine.Offset);
        Select(AddColumns(newAnchor, deltaColumn), AddColumns(newCaret, deltaColumn));
    }

    /// <summary>
    /// The edits that remove the block comment around the selection's lines, as VS Code's line comment finds it
    /// (<c>_attemptRemoveBlockComment</c>): its start token before the selection's start, past the first line's text by
    /// the end token's length at most, and its end token from just before the selection's end on; one of them on the
    /// line of the other when that line has only one; and without a selection, a comment anywhere on the caret's line.
    /// Null when there is none.
    /// </summary>
    private List<(int Offset, int Length, string Text)>? RemoveBlockCommentOnLines(
        int start,
        int end,
        DocumentLine startLine,
        DocumentLine endLine,
        string open,
        string close
    )
    {
        string startText = Buffer.GetText(startLine);
        string endText = Buffer.GetText(endLine);
        int startColumn = start - startLine.Offset;
        int endColumn = end - endLine.Offset;
        int firstText = LeadingWhitespace(startText);
        // VS Code's first text column, from 1: none for a line of whitespace only.
        int firstTextColumn = firstText == startText.Length ? 0 : firstText + 1;
        int openIndex = LastIndexOf(startText, open, close.Length + Math.Max(firstTextColumn, startColumn + 1) - 1);
        int closeIndex = IndexOf(endText, close, endColumn - open.Length);
        if (openIndex != -1 && closeIndex == -1)
        {
            closeIndex = IndexOf(startText, close, openIndex + open.Length);
            (endLine, endText) = (startLine, startText);
        }
        if (openIndex == -1 && closeIndex != -1)
        {
            openIndex = LastIndexOf(endText, open, closeIndex);
            (startLine, startText) = (endLine, endText);
        }
        if (start == end && (openIndex == -1 || closeIndex == -1))
        {
            openIndex = startText.IndexOf(open, StringComparison.Ordinal);
            if (openIndex != -1)
                closeIndex = IndexOf(startText, close, openIndex + open.Length);
        }
        string openToken = open;
        string closeToken = close;
        // The spaces inside the tokens go with them.
        if (openIndex != -1 && openIndex + open.Length < startText.Length && startText[openIndex + open.Length] == ' ')
            openToken += " ";
        if (closeIndex > 0 && endText[closeIndex - 1] == ' ')
        {
            closeToken = " " + closeToken;
            closeIndex--;
        }
        if (openIndex == -1 || closeIndex == -1)
            return null;
        return RemoveBlockCommentEdits(
            startLine.Offset + openIndex,
            openToken.Length,
            endLine.Offset + closeIndex,
            closeToken.Length
        );
    }

    /// <summary>
    /// The edits that put a block comment around the text from <paramref name="start"/> to <paramref name="end"/>, with
    /// a space inside each token if <paramref name="insertSpace"/>; for no text, an empty comment with two spaces inside:
    /// VS Code's <c>_createAddBlockCommentOperations</c>.
    /// </summary>
    private static List<(int Offset, int Length, string Text)> AddBlockCommentEdits(
        int start,
        int end,
        string open,
        string close,
        bool insertSpace
    )
    {
        string space = insertSpace ? " " : "";
        return start == end ? [(start, 0, open + "  " + close)] : [(start, 0, open + space), (end, 0, space + close)];
    }

    /// <summary>
    /// The edits that remove the start token at <paramref name="open"/> and the end token at <paramref name="close"/>,
    /// each with the spaces that go with it, as VS Code's <c>_createRemoveBlockCommentOperations</c>: tokens that meet
    /// or overlap, as in an empty comment or one whose only space both count, go as one.
    /// </summary>
    private static List<(int Offset, int Length, string Text)> RemoveBlockCommentEdits(
        int open,
        int openLength,
        int close,
        int closeLength
    )
    {
        // In their order in the text.
        if (close < open)
            (open, openLength, close, closeLength) = (close, closeLength, open, openLength);
        return close > open + openLength
            ? [(open, openLength, ""), (close, closeLength, "")]
            : [(open, Math.Max(open + openLength, close + closeLength) - open, "")];
    }

    /// <summary>Whether <paramref name="edits"/> are within the text, apart and in order, and none is read-only.</summary>
    private bool IsWithinText(List<(int Offset, int Length, string Text)> edits)
    {
        int previousEnd = 0;
        foreach ((int offset, int length, _) in edits)
        {
            if (offset < previousEnd || offset + length > Buffer.TextLength || !CanChange(offset, length))
                return false;
            previousEnd = offset + length;
        }
        return true;
    }

    /// <summary>The offset <paramref name="columns"/> to the right of <paramref name="offset"/>, within its line.</summary>
    private int AddColumns(int offset, int columns) =>
        columns == 0 ? offset : Math.Min(offset + columns, Buffer.GetLineByOffset(offset).EndOffset);

    /// <summary>
    /// JavaScript's <c>lastIndexOf</c>: the last <paramref name="value"/> in <paramref name="text"/> that starts at
    /// <paramref name="from"/> or before; -1 for none.
    /// </summary>
    private static int LastIndexOf(string text, string value, int from)
    {
        for (int index = Math.Min(Math.Max(from, 0), text.Length - value.Length); index >= 0; index--)
        {
            if (string.CompareOrdinal(text, index, value, 0, value.Length) == 0)
                return index;
        }
        return -1;
    }

    /// <summary>
    /// JavaScript's <c>indexOf</c>: the first <paramref name="value"/> in <paramref name="text"/> that starts at
    /// <paramref name="from"/> or after; -1 for none.
    /// </summary>
    private static int IndexOf(string text, string value, int from) =>
        text.IndexOf(value, Math.Clamp(from, 0, text.Length), StringComparison.Ordinal);

    /// <summary>JavaScript's <c>substring</c>: between two indexes in either order, within the text.</summary>
    private static string Substring(string text, int from, int to)
    {
        (from, to) = (Math.Clamp(Math.Min(from, to), 0, text.Length), Math.Clamp(Math.Max(from, to), 0, text.Length));
        return text[from..to];
    }
}

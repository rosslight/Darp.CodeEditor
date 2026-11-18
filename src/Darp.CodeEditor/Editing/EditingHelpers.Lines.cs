using System.Text.RegularExpressions;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

// The commands on whole lines, as VS Code's linesOperations does them. Each is a step of its own for undo.
internal sealed partial class EditingHelpers
{
    /// <summary>
    /// Whether moved lines take the indentation that the language's rules give them, as VS Code's
    /// <c>editor.autoIndent</c> does at its default, <c>full</c>. The editor has no such option yet: Enter follows the
    /// rules too.
    /// </summary>
    private static bool AutoIndentsMovedLines => true;

    /// <summary>
    /// Moves the selected lines, or the caret's, past the line above or below them, as VS Code's
    /// <c>moveLinesUpAction</c> and <c>moveLinesDownAction</c> do: with the language's indentation rules, the lines take
    /// the indentation that the lines around them give, as a block, and a line moved past them takes its own. A last
    /// line that the selection touches only at its start stays. The selection moves with the lines.
    /// </summary>
    public void MoveLines(bool down) =>
        _undo.Edit(EditKind.Other, stopBefore: true, () => DoMoveLines(down), stopAfter: true);

    /// <summary>
    /// Copies the selected lines, or the caret's, above or below them, as VS Code's <c>copyLinesUpAction</c> and
    /// <c>copyLinesDownAction</c> do: the selection goes with the copy below when copying down, and stays above when
    /// copying up.
    /// </summary>
    public void CopyLines(bool down) =>
        _undo.Edit(EditKind.Other, stopBefore: true, () => DoCopyLines(down), stopAfter: true);

    private void DoMoveLines(bool down)
    {
        (int anchor, int caret) = SelectionEnds();
        int start = Math.Min(anchor, caret);
        int end = Math.Max(anchor, caret);
        DocumentLine first = Buffer.GetLineByOffset(start);
        DocumentLine last = Buffer.GetLineByOffset(end);
        if (down ? last.NextLine is null : first.PreviousLine is null)
            return;
        bool moveEndDown = false;
        if (first != last && end == last.Offset)
        {
            moveEndDown = true;
            last = last.PreviousLine;
            end = last.EndOffset;
        }
        bool reversed = anchor > caret;
        (int trackedAnchor, int trackedCaret) = reversed ? (end, start) : (start, end);
        string lineBreak = TextUtilities.GetNewLineFromDocument(Buffer, first.LineNumber);
        var edits = new List<(int Offset, int Length, string Text)>();
        bool shrinkEnd = false;
        if (first == last && first.Length == 0)
        {
            // An empty line takes the other line's text, which leaves that line empty.
            DocumentLine other = down ? first.NextLine : first.PreviousLine;
            if (other.Length > 0)
            {
                edits.Add((first.Offset, 0, Buffer.GetText(other)));
                edits.Add((other.Offset, other.Length, ""));
            }
            trackedAnchor = trackedCaret = other.Offset;
        }
        else if (down)
        {
            DocumentLine moving = last.NextLine;
            string movingText = Buffer.GetText(moving);
            edits.Add((last.EndOffset, moving.EndOffset - last.EndOffset, ""));
            if (!AddMovedDown(edits, first, last, end, moving, movingText, lineBreak, ref shrinkEnd))
            {
                edits.RemoveRange(1, edits.Count - 1);
                edits.Add((first.Offset, 0, movingText + lineBreak));
            }
        }
        else
        {
            DocumentLine moving = first.PreviousLine;
            edits.Add((moving.Offset, first.Offset - moving.Offset, ""));
            edits.Add((last.EndOffset, 0, lineBreak + Buffer.GetText(moving)));
            if (!AddMovedUp(edits, first, last, end, moving, ref shrinkEnd))
                edits.RemoveRange(2, edits.Count - 2);
        }
        List<(int Offset, int Length, string Text)> sorted = [.. edits.OrderBy(edit => edit.Offset)];
        if (!sorted.TrueForAll(edit => CanChange(edit.Offset, edit.Length)))
            return;
        bool atLineEnd =
            trackedAnchor == trackedCaret && trackedCaret == Buffer.GetLineByOffset(trackedCaret).EndOffset;
        (int newAnchor, int newCaret) = TrackedSelection.Map(trackedAnchor, trackedCaret, atLineEnd, sorted);
        if (sorted.Count > 0)
            ReplaceInOneChange(sorted);
        if (moveEndDown)
            SetEnd(ref newAnchor, ref newCaret, Buffer.GetLineByOffset(Math.Max(newAnchor, newCaret)).NextLine.Offset);
        // As VS Code: an end in the indentation of a last line that lost its indentation goes to that line's column 2.
        DocumentLine endLine = Buffer.GetLineByOffset(Math.Max(newAnchor, newCaret));
        if (shrinkEnd && endLine != Buffer.GetLineByOffset(Math.Min(newAnchor, newCaret)))
            SetEnd(ref newAnchor, ref newCaret, Math.Min(endLine.Offset + 1, endLine.EndOffset));
        Select(newAnchor, newCaret);
    }

    /// <summary>
    /// The edits that move <paramref name="moving"/> above the lines from <paramref name="first"/> to
    /// <paramref name="last"/> with the language's indentation rules, after the edit that removes it: VS Code's
    /// <c>MoveLinesCommand</c> going down. The moving line takes the indentation of the line before the block, and the
    /// block the indentation that the moving line gives it. <paramref name="end"/> is the end of the selection in the
    /// block.
    /// </summary>
    /// <returns>False when the rules do not apply or ran out of time: the lines then move as they are.</returns>
    private bool AddMovedDown(
        List<(int Offset, int Length, string Text)> edits,
        DocumentLine first,
        DocumentLine last,
        int end,
        DocumentLine moving,
        string movingText,
        string lineBreak,
        ref bool shrinkEnd
    )
    {
        if (!AutoIndentsMovedLines || Configuration.IndentationRules is not { } rules)
            return false;
        IndentationMatching matching = rules.StartMatching();
        try
        {
            string inserting = movingText;
            string oldIndentation = Leading(movingText);
            if (MatchEnterRule(matching, moving.LineNumber, first.LineNumber - 1) is { } enterDelta)
                inserting = GenerateIndent(enterDelta + SpaceCount(oldIndentation)) + movingText.TrimStart();
            else
            {
                // The moving line in the block's first line.
                string? indentation = GoodIndentForLine(
                    matching,
                    first.LineNumber,
                    number => number == first.LineNumber ? movingText : LineText(number)
                );
                if (indentation is not null && SpaceCount(indentation) != SpaceCount(oldIndentation))
                    inserting = GenerateIndent(SpaceCount(indentation)) + movingText.TrimStart();
            }
            // Before the block's indentation, which goes after it.
            edits.Add((first.Offset, 0, inserting + lineBreak));
            int? offset = MatchEnterRuleMovingDown(matching, first.LineNumber, moving.LineNumber, inserting);
            if (offset is null)
            {
                // The block one line down, below the moved line.
                string? indentation = GoodIndentForLine(
                    matching,
                    first.LineNumber + 1,
                    number =>
                        number == first.LineNumber ? inserting
                        : number > first.LineNumber && number <= last.LineNumber + 1 ? LineText(number - 1)
                        : LineText(number)
                );
                if (indentation is not null)
                    offset = SpaceCount(indentation) - SpaceCount(Leading(LineText(first.LineNumber)));
            }
            if (offset is { } delta and not 0)
                AddBlockIndentation(edits, first, last, end, delta, ref shrinkEnd);
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// The edits that indent the lines from <paramref name="first"/> to <paramref name="last"/> as they move above
    /// <paramref name="moving"/> with the language's indentation rules: VS Code's <c>MoveLinesCommand</c> going up.
    /// </summary>
    /// <returns>False when the rules do not apply or ran out of time: the lines then move as they are.</returns>
    private bool AddMovedUp(
        List<(int Offset, int Length, string Text)> edits,
        DocumentLine first,
        DocumentLine last,
        int end,
        DocumentLine moving,
        ref bool shrinkEnd
    )
    {
        if (!AutoIndentsMovedLines || Configuration.IndentationRules is not { } rules)
            return false;
        IndentationMatching matching = rules.StartMatching();
        try
        {
            int? offset = MatchEnterRule(matching, first.LineNumber, first.LineNumber - 2);
            if (offset is null)
            {
                // The block's first line in the moving line's place.
                string? indentation = GoodIndentForLine(
                    matching,
                    moving.LineNumber,
                    number => LineText(number == moving.LineNumber ? first.LineNumber : number)
                );
                if (indentation is not null)
                    offset = SpaceCount(indentation) - SpaceCount(Leading(LineText(first.LineNumber)));
            }
            if (offset is { } delta and not 0)
                AddBlockIndentation(edits, first, last, end, delta, ref shrinkEnd);
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Changes the indentation of the lines from <paramref name="first"/> to <paramref name="last"/> by
    /// <paramref name="delta"/> columns: VS Code's <c>getIndentEditsOfMovingBlock</c>. The end of the selection,
    /// <paramref name="end"/>, in the indentation of a last line that loses all of it makes <paramref name="shrinkEnd"/>.
    /// </summary>
    private void AddBlockIndentation(
        List<(int Offset, int Length, string Text)> edits,
        DocumentLine first,
        DocumentLine last,
        int end,
        int delta,
        ref bool shrinkEnd
    )
    {
        for (DocumentLine line = first; ; line = line.NextLine)
        {
            string original = Leading(Buffer.GetText(line));
            string indentation = GenerateIndent(SpaceCount(original) + delta);
            if (indentation != original)
            {
                edits.Add((line.Offset, original.Length, indentation));
                if (line == last && end - line.Offset <= original.Length && indentation.Length == 0)
                    shrinkEnd = true;
            }
            if (line == last)
                return;
        }
    }

    /// <summary>
    /// How many columns the line before <paramref name="lineAbove"/>, or the nearest one above with text, gives line
    /// <paramref name="lineNumber"/> more than it has, as Enter after it would: VS Code's <c>matchEnterRule</c>. Null
    /// when Enter after it does nothing special.
    /// </summary>
    private int? MatchEnterRule(IndentationMatching matching, int lineNumber, int lineAbove)
    {
        int valid = lineAbove;
        while (valid >= 1 && string.IsNullOrWhiteSpace(matching.Read(LineText, valid)))
            valid--;
        if (valid < 1 || lineNumber > Buffer.LineCount)
            return null;
        return EnterIndentDelta(matching, lineNumber, LineText(valid));
    }

    /// <summary>
    /// <see cref="MatchEnterRule"/> for the block's first line, <paramref name="lineNumber"/>, below the line that
    /// moved down past it: VS Code's <c>matchEnterRuleMovingDown</c>, which reads the moved line as it was.
    /// </summary>
    private int? MatchEnterRuleMovingDown(
        IndentationMatching matching,
        int lineNumber,
        int movedLine,
        string movedText
    ) =>
        string.IsNullOrWhiteSpace(movedText)
            ? MatchEnterRule(matching, lineNumber, lineNumber - 1)
            : EnterIndentDelta(matching, lineNumber, LineText(movedLine));

    /// <summary>
    /// How many columns Enter at the end of <paramref name="before"/> gives line <paramref name="lineNumber"/> more than
    /// it has: one level more after an opening bracket, one less for a line that matches the decrease pattern; VS
    /// Code's <c>parseEnterResult</c>. Null when Enter there does nothing special.
    /// </summary>
    private int? EnterIndentDelta(IndentationMatching matching, int lineNumber, string before)
    {
        if (!IndentsAfter(before))
            return null;
        // VS Code's appendText of an indenting Enter: a tab, as wide as a tab stop.
        string indentation = Leading(before) + "\t";
        string text = LineText(lineNumber);
        if (matching.Decreases(text))
            indentation = new Indenting(_document.Indentation).Unshift(indentation);
        return SpaceCount(indentation) - SpaceCount(Leading(text));
    }

    /// <summary>
    /// The indentation that the lines above give line <paramref name="lineNumber"/> by the indentation rules, as read
    /// by <paramref name="lineText"/>: VS Code's <c>getGoodIndentForLine</c>. Null when no line above has text.
    /// </summary>
    private string? GoodIndentForLine(IndentationMatching matching, int lineNumber, Func<int, string> lineText)
    {
        if (InheritedIndentation(matching, lineNumber, honorIntentional: true, lineText) is not { } inherited)
            return null;
        var indenting = new Indenting(_document.Indentation);
        bool decreases = matching.Decreases(lineText(lineNumber));
        if (InheritedLine(matching, lineNumber, lineText) is { } line)
        {
            // Enter after the line applies while only whitespace lines are between.
            bool applies = true;
            for (int between = line; between < lineNumber - 1 && applies; between++)
                applies = string.IsNullOrWhiteSpace(lineText(between));
            if (applies && IndentsAfter(lineText(line)))
            {
                string indentation = indenting.Shift(Leading(lineText(line)));
                return decreases ? indenting.Unshift(indentation) : indentation;
            }
        }
        if (decreases)
            return inherited.Increases ? inherited.Indentation : indenting.Unshift(inherited.Indentation);
        return inherited.Increases ? indenting.Shift(inherited.Indentation) : inherited.Indentation;
    }

    /// <summary>
    /// The line that <see cref="InheritedIndentation"/> takes the indentation of, honoring intentional indentation:
    /// the nearest line above with text; null for the first line, and below empty lines only.
    /// </summary>
    private static int? InheritedLine(IndentationMatching matching, int lineNumber, Func<int, string> lineText)
    {
        if (lineNumber <= 1)
            return null;
        for (int prior = lineNumber - 1; prior > 0; prior--)
        {
            if (matching.Read(lineText, prior).Length > 0)
                break;
            if (prior == 1)
                return null;
        }
        int preceding = PrecedingLineWithText(lineNumber, number => matching.Read(lineText, number));
        return preceding >= 1 ? preceding : null;
    }

    /// <summary>
    /// Whether Enter at the end of <paramref name="text"/> indents the next line, by the language's brackets: VS Code's
    /// enter action at a line's end, without the language's own enter rules.
    /// </summary>
    private bool IndentsAfter(string text) =>
        text.Length > 0 && Configuration.Brackets.Any(bracket => EndsWithOpening(text, bracket.Open));

    private void DoCopyLines(bool down)
    {
        (int anchor, int caret) = SelectionEnds();
        int start = Math.Min(anchor, caret);
        int end = Math.Max(anchor, caret);
        DocumentLine first = Buffer.GetLineByOffset(start);
        DocumentLine last = Buffer.GetLineByOffset(end);
        int startLineDelta = 0;
        int endLineDelta = 0;
        if (first != last && end == last.Offset)
        {
            endLineDelta = 1;
            last = last.PreviousLine;
            end = last.EndOffset;
        }
        string source = Buffer.GetText(first.Offset, last.EndOffset - first.Offset);
        // A copy of an empty line goes above it, and the caret down to the copy.
        if (source.Length == 0 && down)
        {
            startLineDelta++;
            endLineDelta++;
        }
        string lineBreak = TextUtilities.GetNewLineFromDocument(Buffer, first.LineNumber);
        (int Offset, int Length, string Text) edit = down
            ? (first.Offset, 0, source + lineBreak)
            : (last.EndOffset, 0, lineBreak + source);
        if (!CanInsert(edit.Offset))
            return;
        bool reversed = anchor > caret;
        (int trackedAnchor, int trackedCaret) = reversed ? (end, start) : (start, end);
        bool atLineEnd = start == end && end == last.EndOffset;
        (int newAnchor, int newCaret) = TrackedSelection.Map(trackedAnchor, trackedCaret, atLineEnd, [edit]);
        Buffer.Insert(edit.Offset, edit.Text);
        if (startLineDelta == 0 && endLineDelta == 0)
        {
            Select(newAnchor, newCaret);
            return;
        }
        int newStart = Math.Min(newAnchor, newCaret);
        int newEnd = Math.Max(newAnchor, newCaret);
        if (startLineDelta != 0)
            newStart = Buffer.GetLineByNumber(Buffer.GetLineByOffset(newStart).LineNumber + startLineDelta).Offset;
        if (endLineDelta != 0)
            newEnd = Buffer.GetLineByNumber(Buffer.GetLineByOffset(newEnd).LineNumber + endLineDelta).Offset;
        if (reversed)
            Select(newEnd, newStart);
        else
            Select(newStart, newEnd);
    }

    /// <summary>
    /// The selection's anchor and caret, as offsets; for a rectangle selection, its corners. VS Code's line commands
    /// take the lines between them.
    /// </summary>
    private (int Anchor, int Caret) SelectionEnds()
    {
        int caret = _textArea.Caret.Offset;
        Selection selection = _textArea.Selection;
        return selection.IsEmpty ? (caret, caret) : (Buffer.GetOffset(selection.StartPosition.Location), caret);
    }

    /// <summary>Selects from <paramref name="anchor"/> to <paramref name="caret"/>, and brings the caret into view.</summary>
    private void Select(int anchor, int caret)
    {
        _textArea.Caret.Offset = caret;
        if (anchor == caret)
            _textArea.ClearSelection();
        else
            _textArea.Selection = Selection.Create(_textArea, anchor, caret);
        _textArea.Caret.BringCaretToView();
    }

    /// <summary>Moves the end of the selection, the later one of its anchor and caret, to <paramref name="offset"/>.</summary>
    private static void SetEnd(ref int anchor, ref int caret, int offset)
    {
        if (anchor <= caret)
            caret = offset;
        else
            anchor = offset;
    }

    /// <summary>VS Code's <c>getSpaceCnt</c>: the columns of indentation, each tab as wide as a tab stop.</summary>
    private int SpaceCount(string indentation)
    {
        int size = _document.Indentation.Size;
        int count = 0;
        foreach (char c in indentation)
            count += c == '\t' ? size : 1;
        return count;
    }

    /// <summary>VS Code's <c>generateIndent</c>: indentation <paramref name="columns"/> wide, as the document indents.</summary>
    private string GenerateIndent(int columns) => new Indenting(_document.Indentation).Whitespace(Math.Max(0, columns));

    private static string Leading(string line) => line[..LeadingWhitespace(line)];
}

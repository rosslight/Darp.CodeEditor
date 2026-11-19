using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The commands on whole lines in one editor's view, as VS Code's <c>linesOperations.ts</c>, <c>moveLinesCommand.ts</c>
/// and <c>copyLinesCommand.ts</c> run them, with their tracked selections: moving, copying, deleting and inserting
/// lines, indenting and outdenting them, and selecting them whole. They take the selected lines,
/// or the caret's, without a last line that the selection touches only at its start. Each edit is a step of its own
/// for undo. Moved lines take the indentation of their new place from <see cref="IndentRules"/>, as far as the
/// language's <see cref="AutoIndent"/> goes, and inserted lines the indentation that Enter gives them.
/// </summary>
internal sealed class LineCommands : IEditorFeature
{
    private readonly TextArea _area;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly TextChanges _changes;

    public LineCommands(
        TextArea area,
        CodeDocument document,
        BackgroundTextMateHighlighter highlighter,
        TextChanges changes
    )
    {
        _area = area;
        _document = document;
        _highlighter = highlighter;
        _changes = changes;
    }

    private TextDocument Buffer => _area.Document;

    private LanguageConfiguration Configuration => LanguageConfiguration.Of(_document.Language);

    private IndentRules Rules =>
        new(Configuration, _document.Language.AutoIndent, _document.Indentation, _document.LineBreak);

    private RuleLines Lines => RuleLines.Of(Buffer, _highlighter, Configuration.BracketPattern);

    /// <summary>
    /// The lines as the rules see them for a move: each by its exact tokens, tokenized now if need be, as typing's are;
    /// without a grammar, all code. Where the tokens of a line with brackets stay unknown, the lines keep their
    /// indentation.
    /// </summary>
    private RuleLines MoveLines =>
        new(
            number => Buffer.GetText(Buffer.GetLineByNumber(number)),
            (number, _) =>
                _document.Language.Grammar is null ? [] : _highlighter.ExactTokensOf(number, tokenizeNow: true),
            Configuration.BracketPattern
        );

    private Indenting Indenting => new(_document.Indentation);

    public void Dispose() { }

    public void AddCommands(CommandTable commands)
    {
        const CommandNeeds writable = CommandNeeds.TextFocus | CommandNeeds.Writable;
        commands.Add(CodeEditorCommands.MoveLinesUp, () => Move(down: false), writable);
        commands.Add(CodeEditorCommands.MoveLinesDown, () => Move(down: true), writable);
        commands.Add(CodeEditorCommands.CopyLinesUp, () => Copy(down: false), writable);
        commands.Add(CodeEditorCommands.CopyLinesDown, () => Copy(down: true), writable);
        commands.Add(CodeEditorCommands.DeleteLines, Delete, writable);
        commands.Add(CodeEditorCommands.InsertLineAfter, () => Insert(above: false), writable);
        commands.Add(CodeEditorCommands.InsertLineBefore, () => Insert(above: true), writable);
        commands.Add(CodeEditorCommands.IndentLines, () => Shift(outdent: false), writable);
        commands.Add(CodeEditorCommands.OutdentLines, () => Shift(outdent: true), writable);
        commands.Add(CodeEditorCommands.ExpandLineSelection, ExpandSelection);
    }

    /// <summary>
    /// Moves the lines past the line above or below them, with the selection: a single empty line swaps texts with that
    /// line. By the language's rules, the line moved past them takes the indentation of its new place, and they the
    /// indentation of theirs, all by as many columns as their first line.
    /// </summary>
    /// <returns>Whether the lines moved: not at the text's start or end, nor into read-only text.</returns>
    private bool Move(bool down)
    {
        TextSelection selection = _changes.Selection;
        (DocumentLine first, DocumentLine last, bool endMovesDown) = LinesOf(selection.Range);
        // The last line as the selection's end has it, also when the selection only touches it.
        if (down ? Buffer.GetLineByOffset(selection.Range.End).NextLine is null : first.PreviousLine is null)
            return false;
        DocumentLine moving = down ? last.NextLine : first.PreviousLine;
        if (first == last && first.Length == 0)
        {
            string other = Buffer.GetText(moving);
            int otherLine = moving.LineNumber;
            List<TextEdit> swap = down
                ? [new TextEdit(first.Offset, 0, other), new TextEdit(moving.Offset, moving.Length, "")]
                : [new TextEdit(moving.Offset, moving.Length, ""), new TextEdit(first.Offset, 0, other)];
            bool swapped = false;
            _changes.Run(
                EditStep.Own,
                () =>
                {
                    if (swapped = _changes.Apply(other.Length > 0 ? swap : []))
                        Select(selection, new Place(otherLine, 0), new Place(otherLine, 0), endMovesDown, false);
                }
            );
            return swapped;
        }
        (List<TextEdit> edits, int delta) = down ? MovedDown(first, last, moving) : MovedUp(first, last, moving);
        // A selection never grows at its ends, and a caret stays before text that goes in at it only at its line's end.
        int start = selection.Range.Start;
        int end = endMovesDown ? last.EndOffset : selection.Range.End;
        bool atLineEnd = start == end && end == Buffer.GetLineByOffset(end).EndOffset;
        int lines = down ? 1 : -1;
        Place newStart = Track(start, start == end && atLineEnd);
        Place newEnd = Track(end, start != end || atLineEnd);
        // The end in the indentation of a last line that loses all of it goes to that line's second column.
        DocumentLine endLine = Buffer.GetLineByOffset(end);
        string endIndentation = endLine.Length == 0 ? "" : Buffer.GetText(endLine.Offset, Leading(endLine));
        bool shrinks =
            NewIndentation(endIndentation, delta).Length == 0
            && endIndentation.Length > 0
            && end - endLine.Offset <= endIndentation.Length;
        bool moved = false;
        _changes.Run(
            EditStep.Own,
            () =>
            {
                if (moved = _changes.Apply(edits))
                    Select(selection, newStart, newEnd, endMovesDown, shrinks);
            }
        );
        return moved;

        Place Track(int offset, bool sticks)
        {
            DocumentLine line = Buffer.GetLineByOffset(offset);
            string indentation = Buffer.GetText(line.Offset, Leading(line));
            return new Place(
                line.LineNumber + lines,
                Column(offset - line.Offset, indentation.Length, NewIndentation(indentation, delta).Length, sticks)
            );
        }
    }

    /// <summary>
    /// The edits that move <paramref name="moving"/> above the lines from <paramref name="first"/> to
    /// <paramref name="last"/>: it takes the indentation that the rules for Enter at the end of the line above them
    /// give it, or else the indentation rules, in their first line's place; they take the indentation that Enter at its
    /// end gives their first line, or else the indentation rules, below it.
    /// </summary>
    /// <returns>The edits, and the columns that the lines' indentation changes by.</returns>
    private (List<TextEdit> Edits, int Delta) MovedDown(DocumentLine first, DocumentLine last, DocumentLine moving)
    {
        IndentRules rules = Rules;
        RuleLines lines = MoveLines;
        int top = first.LineNumber;
        string text = Buffer.GetText(moving);
        string? indentation =
            rules.EnteredIndentation(lines, moving.LineNumber, top - 1)
            ?? rules.IndentationInPlace(lines.Moved(n => n == top ? moving.LineNumber : n), top);
        string inserting = Indented(text, indentation);
        RuleLines afterMove = lines
            .Moved(n =>
                n == top ? moving.LineNumber
                : n > top && n <= moving.LineNumber ? n - 1
                : n
            )
            .Reindented(top, inserting[..IndentRules.Leading(inserting)]);
        int delta = Delta(
            first,
            rules.EnteredIndentation(lines, top, string.IsNullOrWhiteSpace(inserting) ? top - 1 : moving.LineNumber)
                ?? rules.IndentationInPlace(afterMove, top + 1)
        );
        List<TextEdit> edits = [new TextEdit(first.Offset, 0, inserting + _document.LineBreak)];
        AddIndentation(edits, first, last, delta);
        edits.Add(new TextEdit(last.EndOffset, moving.EndOffset - last.EndOffset, ""));
        return (edits, delta);
    }

    /// <summary>
    /// The edits that move <paramref name="moving"/> below the lines from <paramref name="first"/> to
    /// <paramref name="last"/>, as it is: they take the indentation that the rules for Enter at the end of the line
    /// above it give their first line, or else the indentation rules, in its place.
    /// </summary>
    /// <returns>The edits, and the columns that the lines' indentation changes by.</returns>
    private (List<TextEdit> Edits, int Delta) MovedUp(DocumentLine first, DocumentLine last, DocumentLine moving)
    {
        IndentRules rules = Rules;
        RuleLines lines = MoveLines;
        int delta = Delta(
            first,
            rules.EnteredIndentation(lines, first.LineNumber, first.LineNumber - 2)
                ?? rules.IndentationInPlace(
                    lines.Moved(n => n == moving.LineNumber ? first.LineNumber : n),
                    moving.LineNumber
                )
        );
        List<TextEdit> edits = [new TextEdit(moving.Offset, first.Offset - moving.Offset, "")];
        AddIndentation(edits, first, last, delta);
        // After the indentation of an empty last line, which goes in at the same offset.
        edits.Add(new TextEdit(last.EndOffset, 0, _document.LineBreak + Buffer.GetText(moving)));
        return (edits, delta);
    }

    /// <summary>
    /// Copies the lines above or below themselves, with the document's line breaks; the selection keeps its lines and
    /// columns on the lines above, so it goes with the copy when that is below. Not in a rectangle selection, whose
    /// keys go on to AvaloniaEdit's: Shift+Alt+Up and Shift+Alt+Down extend it on Windows.
    /// </summary>
    /// <returns>Whether the lines were copied.</returns>
    private bool Copy(bool down)
    {
        if (_area.Selection is RectangleSelection)
            return false;
        TextSelection selection = _changes.Selection;
        (DocumentLine first, DocumentLine last, _) = LinesOf(selection.Range);
        string lineBreak = _document.LineBreak;
        string lines = TextUtilities.NormalizeNewLines(
            Buffer.GetText(first.Offset, last.EndOffset - first.Offset),
            lineBreak
        );
        TextEdit copy = down
            ? new TextEdit(first.Offset, 0, lines + lineBreak)
            : new TextEdit(last.EndOffset, 0, lineBreak + lines);
        int moved = down ? last.LineNumber - first.LineNumber + 1 : 0;
        (Place anchor, Place caret) = (PlaceOf(selection.Anchor), PlaceOf(selection.Caret));
        bool copied = false;
        _changes.Run(
            EditStep.Own,
            () =>
            {
                if (copied = _changes.Apply([copy]))
                {
                    _changes.Select(
                        new TextSelection(
                            OffsetOf(anchor with { Line = anchor.Line + moved }),
                            OffsetOf(caret with { Line = caret.Line + moved })
                        )
                    );
                }
            }
        );
        return copied;
    }

    /// <summary>
    /// Deletes the lines with the line break after them, or for the last lines the one before them; the caret keeps
    /// its column, as far as the line that comes up in their place, or the one above them, is long.
    /// </summary>
    /// <returns>Whether there was text, and it was deleted.</returns>
    private bool Delete()
    {
        if (Buffer.TextLength == 0)
            return false;
        TextSelection selection = _changes.Selection;
        (DocumentLine first, DocumentLine last, _) = LinesOf(selection.Range);
        int column = selection.Caret - Buffer.GetLineByOffset(selection.Caret).Offset;
        int lineNumber = first.LineNumber;
        TextRange deleted;
        if (last.NextLine is { } next)
            deleted = TextRange.FromBounds(first.Offset, next.Offset);
        else if (first.PreviousLine is { } previous)
        {
            deleted = TextRange.FromBounds(previous.EndOffset, last.EndOffset);
            lineNumber--;
        }
        else
            deleted = TextRange.FromBounds(first.Offset, last.EndOffset);
        bool applied = false;
        _changes.Run(
            EditStep.Own,
            () =>
            {
                if (applied = _changes.Apply([new TextEdit(deleted.Start, deleted.Length, "")]))
                    _changes.Select(new TextSelection(OffsetOf(new Place(lineNumber, column))));
            }
        );
        return applied;
    }

    /// <summary>
    /// Inserts a line below the caret's, or above it, as Enter at the end of the line above it does, and puts the caret
    /// on it; above the first line, an empty line.
    /// </summary>
    /// <returns>Whether the line went in.</returns>
    private bool Insert(bool above)
    {
        DocumentLine caretLine = Buffer.GetLineByOffset(_area.Caret.Offset);
        string lineBreak = _document.LineBreak;
        bool inserted = false;
        _changes.Run(
            EditStep.Own,
            () =>
            {
                if (above && caretLine.PreviousLine is null)
                {
                    inserted = _changes.Apply([new TextEdit(0, 0, lineBreak)], new TextSelection(0));
                    return;
                }
                DocumentLine line = above ? caretLine.PreviousLine : caretLine;
                NewLine newLine = Rules.Enter(Lines, line.LineNumber, line.Length);
                inserted = _changes.Apply(
                    [new TextEdit(line.EndOffset, newLine.Replaced, newLine.Text)],
                    new TextSelection(line.EndOffset + newLine.Caret)
                );
            }
        );
        return inserted;
    }

    /// <summary>Indents or outdents the lines by the tab stops, as Tab and Shift+Tab do whole lines.</summary>
    /// <returns>
    /// Whether a line's indentation changed: not in a rectangle selection, which AvaloniaEdit's own commands keep, nor
    /// for lines without indentation to outdent.
    /// </returns>
    private bool Shift(bool outdent)
    {
        if (_area.Selection is RectangleSelection)
            return false;
        TabEdit edit = outdent
            ? TabStops.Outdent(Buffer, _changes.Selection, _document.Indentation)
            : TabStops.Indent(Buffer, _changes.Selection, _document.Indentation);
        if (edit.Edits.Count == 0)
            return false;
        bool shifted = false;
        _changes.Run(EditStep.Own, () => shifted = _changes.Apply(edit.Edits, edit.Selection));
        return shifted;
    }

    /// <summary>
    /// Selects the lines of the selection whole, from the start of the first to the start of the line after the last,
    /// or to the text's end: again, it takes the next line too.
    /// </summary>
    private void ExpandSelection()
    {
        TextRange range = _changes.Selection.Range;
        DocumentLine first = Buffer.GetLineByOffset(range.Start);
        DocumentLine last = Buffer.GetLineByOffset(range.End);
        _changes.Select(new TextSelection(first.Offset, last.NextLine?.Offset ?? last.EndOffset));
    }

    /// <summary>
    /// The lines of <paramref name="range"/>, without a last line that it touches only at its start, and whether it
    /// does: its end then goes to the start of the line after them again.
    /// </summary>
    private (DocumentLine First, DocumentLine Last, bool EndMovesDown) LinesOf(TextRange range)
    {
        DocumentLine first = Buffer.GetLineByOffset(range.Start);
        DocumentLine last = Buffer.GetLineByOffset(range.End);
        return last != first && range.End == last.Offset ? (first, last.PreviousLine, true) : (first, last, false);
    }

    /// <summary>
    /// Selects the moved selection's ends, <paramref name="start"/> and <paramref name="end"/>, in the direction of
    /// <paramref name="before"/>: the end at the start of the line after its own if it <paramref name="movesDown"/>, and
    /// in the second column of its line if it <paramref name="shrinks"/> and the selection spans lines.
    /// </summary>
    private void Select(TextSelection before, Place start, Place end, bool movesDown, bool shrinks)
    {
        int startOffset = OffsetOf(start);
        DocumentLine endLine = Buffer.GetLineByNumber(end.Line + (movesDown ? 1 : 0));
        int endOffset = movesDown ? endLine.Offset : OffsetOf(end);
        if (shrinks && endLine.LineNumber > start.Line)
            endOffset = Math.Min(endLine.Offset + 1, endLine.EndOffset);
        _changes.Select(
            before.Anchor <= before.Caret
                ? new TextSelection(startOffset, endOffset)
                : new TextSelection(endOffset, startOffset)
        );
    }

    private Place PlaceOf(int offset)
    {
        DocumentLine line = Buffer.GetLineByOffset(offset);
        return new Place(line.LineNumber, offset - line.Offset);
    }

    /// <summary>The offset of <paramref name="place"/>, within its line.</summary>
    private int OffsetOf(Place place)
    {
        DocumentLine line = Buffer.GetLineByNumber(place.Line);
        return line.Offset + Math.Min(place.Column, line.Length);
    }

    /// <summary>
    /// The edits that indent the lines from <paramref name="first"/> to <paramref name="last"/> by
    /// <paramref name="delta"/> columns more, at least to none.
    /// </summary>
    private void AddIndentation(List<TextEdit> edits, DocumentLine first, DocumentLine last, int delta)
    {
        if (delta == 0)
            return;
        for (DocumentLine line = first; ; line = line.NextLine)
        {
            string indentation = Buffer.GetText(line.Offset, Leading(line));
            string indented = NewIndentation(indentation, delta);
            if (indented != indentation)
                edits.Add(new TextEdit(line.Offset, indentation.Length, indented));
            if (line == last)
                return;
        }
    }

    /// <summary><paramref name="text"/> with <paramref name="indentation"/>'s width, if any.</summary>
    private string Indented(string text, string? indentation)
    {
        int leading = IndentRules.Leading(text);
        return indentation is null || Indenting.Width(indentation) == Indenting.Width(text.AsSpan(0, leading))
            ? text
            : Indenting.Whitespace(Indenting.Width(indentation)) + text[leading..];
    }

    /// <summary>The columns that <paramref name="indentation"/>, if any, adds to <paramref name="line"/>'s indentation.</summary>
    private int Delta(DocumentLine line, string? indentation) =>
        indentation is null
            ? 0
            : Indenting.Width(indentation) - Indenting.Width(Buffer.GetText(line.Offset, Leading(line)));

    private string NewIndentation(string indentation, int delta) =>
        delta == 0 ? indentation : Indenting.Whitespace(Math.Max(0, Indenting.Width(indentation) + delta));

    private int Leading(DocumentLine line) => IndentRules.Leading(Buffer.GetText(line));

    /// <summary>
    /// Where a caret or a selection's end at <paramref name="column"/> goes as indentation of <paramref name="old"/>
    /// characters becomes <paramref name="now"/> characters, as a tracked selection moves: within the part that stays it
    /// stays, and at the edge of new text it stays before it only if it <paramref name="sticks"/>.
    /// </summary>
    private static int Column(int column, int old, int now, bool sticks)
    {
        int common = Math.Min(old, now);
        if (column < common || (column == common && (old > now || sticks)))
            return column;
        if (column < old || (column == old && sticks))
            return now;
        return column + now - old;
    }

    /// <summary>A line by its number, and a column in it, from 0.</summary>
    private readonly record struct Place(int Line, int Column);
}

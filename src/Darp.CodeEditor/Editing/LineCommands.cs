using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The commands on whole lines in one editor's view, as VS Code's <c>linesOperations.ts</c>, <c>moveLinesCommand.ts</c>
/// and <c>copyLinesCommand.ts</c> run them, with their tracked selections: moving, copying, deleting and inserting
/// lines, indenting and outdenting them, and selecting them whole. They take the selected lines, or the caret's, without
/// a last line that the selection touches only at its start. Each edit is a step of its own for undo. Moved lines keep
/// their indentation, and inserted lines take the indentation that Enter gives them (<see cref="IndentRules"/>).
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

    /// <summary>Rectangle selections keep their keys: Shift+Alt+Up and Shift+Alt+Down extend them on Windows.</summary>
    private bool IsNotRectangle => _area.Selection is not RectangleSelection;

    public void Dispose() { }

    public void AddCommands(CommandTable commands)
    {
        const CommandNeeds writable = CommandNeeds.TextFocus | CommandNeeds.Writable;
        commands.Add(CodeEditorCommands.MoveLinesUp, () => Move(down: false), writable, () => CanMove(down: false));
        commands.Add(CodeEditorCommands.MoveLinesDown, () => Move(down: true), writable, () => CanMove(down: true));
        commands.Add(CodeEditorCommands.CopyLinesUp, () => Copy(down: false), writable, () => IsNotRectangle);
        commands.Add(CodeEditorCommands.CopyLinesDown, () => Copy(down: true), writable, () => IsNotRectangle);
        commands.Add(CodeEditorCommands.DeleteLines, Delete, writable, () => Buffer.TextLength > 0);
        commands.Add(CodeEditorCommands.InsertLineAfter, () => Insert(above: false), writable);
        commands.Add(CodeEditorCommands.InsertLineBefore, () => Insert(above: true), writable);
        commands.Add(CodeEditorCommands.IndentLines, () => Shift(TabStops.Indent), writable, () => IsNotRectangle);
        commands.Add(CodeEditorCommands.OutdentLines, () => Shift(TabStops.Outdent), writable, CanOutdent);
        commands.Add(CodeEditorCommands.ExpandLineSelection, ExpandSelection);
    }

    /// <summary>Whether a line is there to move past; the last line as the selection's end has it, also when it only touches it.</summary>
    private bool CanMove(bool down)
    {
        TextRange range = _changes.Selection.Range;
        return down
            ? Buffer.GetLineByOffset(range.End).NextLine is not null
            : Buffer.GetLineByOffset(range.Start).PreviousLine is not null;
    }

    /// <summary>Moves the lines past the line above or below them, with the selection.</summary>
    private void Move(bool down)
    {
        TextSelection selection = _changes.Selection;
        (DocumentLine first, DocumentLine last, bool endMovesDown) = LinesOf(selection.Range);
        DocumentLine moving = down ? last.NextLine : first.PreviousLine;
        string text = Buffer.GetText(moving);
        string lineBreak = _document.LineBreak;
        List<TextEdit> edits = down
            ?
            [
                new TextEdit(first.Offset, 0, text + lineBreak),
                new TextEdit(last.EndOffset, moving.EndOffset - last.EndOffset, ""),
            ]
            :
            [
                new TextEdit(moving.Offset, first.Offset - moving.Offset, ""),
                new TextEdit(last.EndOffset, 0, lineBreak + text),
            ];
        int lines = down ? 1 : -1;
        Place start = PlaceOf(selection.Range.Start);
        Place end = PlaceOf(endMovesDown ? last.EndOffset : selection.Range.End);
        (start, end) = (start with { Line = start.Line + lines }, end with { Line = end.Line + lines });
        _changes.Run(
            EditStep.Own,
            () =>
                Apply(
                    edits,
                    () =>
                    {
                        int from = OffsetOf(start);
                        int to = endMovesDown ? Buffer.GetLineByNumber(end.Line + 1).Offset : OffsetOf(end);
                        return selection.Anchor <= selection.Caret
                            ? new TextSelection(from, to)
                            : new TextSelection(to, from);
                    }
                )
        );
    }

    /// <summary>
    /// Copies the lines above or below themselves, with the document's line breaks; the selection keeps its lines and
    /// columns on the lines above, so it goes with the copy when that is below.
    /// </summary>
    private void Copy(bool down)
    {
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
        _changes.Run(
            EditStep.Own,
            () =>
                Apply(
                    [copy],
                    () =>
                        new TextSelection(
                            OffsetOf(anchor with { Line = anchor.Line + moved }),
                            OffsetOf(caret with { Line = caret.Line + moved })
                        )
                )
        );
    }

    /// <summary>
    /// Deletes the lines with the line break after them, or for the last lines the one before them; the caret keeps
    /// its column, as far as the line that comes up in their place, or the one above them, is long.
    /// </summary>
    private void Delete()
    {
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
        _changes.Run(
            EditStep.Own,
            () =>
                Apply(
                    [new TextEdit(deleted.Start, deleted.Length, "")],
                    () => new TextSelection(OffsetOf(new Place(lineNumber, column)))
                )
        );
    }

    /// <summary>
    /// Inserts a line below the caret's, or above it, as Enter at the end of the line above it does, and puts the caret
    /// on it; above the first line, an empty line.
    /// </summary>
    private void Insert(bool above)
    {
        DocumentLine caretLine = Buffer.GetLineByOffset(_area.Caret.Offset);
        _changes.Run(
            EditStep.Own,
            () =>
            {
                if (above && caretLine.PreviousLine is null)
                {
                    _changes.Apply([new TextEdit(0, 0, _document.LineBreak)], new TextSelection(0));
                    return;
                }
                DocumentLine line = above ? caretLine.PreviousLine : caretLine;
                NewLine newLine = Rules.Enter(Lines, line.LineNumber, line.Length);
                _changes.Apply(
                    [new TextEdit(line.EndOffset, newLine.Replaced, newLine.Text)],
                    new TextSelection(line.EndOffset + newLine.Caret)
                );
            }
        );
    }

    /// <summary>Indents or outdents the lines by the tab stops, as Tab and Shift+Tab do whole lines.</summary>
    private void Shift(Func<TextDocument, TextSelection, Indentation, TabEdit> shift)
    {
        TabEdit edit = shift(Buffer, _changes.Selection, _document.Indentation);
        _changes.Run(EditStep.Own, () => _changes.Apply(edit.Edits, edit.Selection));
    }

    /// <summary>Whether a line has indentation to outdent; otherwise the key goes on.</summary>
    private bool CanOutdent() =>
        IsNotRectangle && TabStops.Outdent(Buffer, _changes.Selection, _document.Indentation).Edits.Count > 0;

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
    /// Makes <paramref name="edits"/>, apart from the line breaks around them, then selects what
    /// <paramref name="selection"/> finds in the new text.
    /// </summary>
    private void Apply(IReadOnlyList<TextEdit> edits, Func<TextSelection> selection)
    {
        if (_changes.Apply(LineBreaks.Apart(Buffer, edits, _document.LineBreak)))
            _changes.Select(selection());
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

    /// <summary>A line by its number, and a column in it, from 0.</summary>
    private readonly record struct Place(int Line, int Column);
}

using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The commands on whole lines in one editor's view, as VS Code's <c>linesOperations.ts</c> and
/// <c>copyLinesCommand.ts</c> run them: copying, deleting and inserting lines, indenting and outdenting them, and
/// selecting them whole. They take the selected lines, or the caret's, without a last line that the selection touches
/// only at its start. Each edit is a step of its own for undo. Inserted lines take the indentation that Enter gives
/// them.
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

    public void Dispose() { }

    public void AddCommands(CommandTable commands)
    {
        const CommandNeeds writable = CommandNeeds.TextFocus | CommandNeeds.Writable;
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
    /// Copies the lines above or below themselves, with the document's line breaks; the selection stays on the lines
    /// above, so it goes with the copy when that is below.
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
        int moved = down ? copy.Text.Length : 0;
        _changes.Run(
            EditStep.Own,
            () => _changes.Apply([copy], new TextSelection(selection.Anchor + moved, selection.Caret + moved))
        );
    }

    /// <summary>
    /// Deletes the lines with the line break after them, or for the last lines the one before them; the caret keeps
    /// its column, as far as the line that comes up in their place, or the one above them, is long.
    /// </summary>
    /// <returns>Whether there was text.</returns>
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
        _changes.Run(
            EditStep.Own,
            () =>
            {
                if (!_changes.Apply([new TextEdit(deleted.Start, deleted.Length, "")]))
                    return;
                DocumentLine line = Buffer.GetLineByNumber(lineNumber);
                _changes.Select(new TextSelection(line.Offset + Math.Min(column, line.Length)));
            }
        );
        return true;
    }

    /// <summary>
    /// Inserts a line below the caret's, or above it, as Enter at the end of the line above it does, and puts the caret
    /// on it; above the first line, an empty line.
    /// </summary>
    private void Insert(bool above)
    {
        DocumentLine caretLine = Buffer.GetLineByOffset(_area.Caret.Offset);
        string lineBreak = _document.LineBreak;
        _changes.Run(
            EditStep.Own,
            () =>
            {
                if (above && caretLine.PreviousLine is null)
                {
                    _changes.Apply([new TextEdit(0, 0, lineBreak)], new TextSelection(0));
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
    /// <returns>Whether the selection is no rectangle, which AvaloniaEdit's own commands keep.</returns>
    private bool Shift(bool outdent)
    {
        if (_area.Selection is RectangleSelection)
            return false;
        TabEdit edit = outdent
            ? TabStops.Outdent(Buffer, _changes.Selection, _document.Indentation)
            : TabStops.Indent(Buffer, _changes.Selection, _document.Indentation);
        _changes.Run(EditStep.Own, () => _changes.Apply(edit.Edits, edit.Selection));
        return true;
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
}

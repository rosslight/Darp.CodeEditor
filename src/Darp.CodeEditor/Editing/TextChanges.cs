using System.Text;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using EditorSelection = AvaloniaEdit.Editing.Selection;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// How one editor's view edits its document: each edit goes in the undo step that <see cref="UndoSteps"/> gives it,
/// and the edits of a list go in as one change of the text, with the selection carried through them. Edits that touch
/// read-only text don't go in.
/// </summary>
internal sealed class TextChanges : IDisposable
{
    private readonly TextArea _area;
    private readonly UndoSteps _steps;

    public TextChanges(TextArea area)
    {
        _area = area;
        _steps = new UndoSteps(area);
    }

    private TextDocument Buffer => _area.Document;

    /// <summary>The selection's anchor and caret; the anchor of a rectangle selection is the corner it started from.</summary>
    public TextSelection Selection =>
        _area.Selection.IsEmpty
            ? new TextSelection(_area.Caret.Offset)
            : new TextSelection(Buffer.GetOffset(_area.Selection.StartPosition.Location), _area.Caret.Offset);

    public void Dispose() => _steps.Dispose();

    /// <inheritdoc cref="UndoSteps.Typing"/>
    public EditStep Typing(string text) => _steps.Typing(text);

    /// <inheritdoc cref="UndoSteps.Deleting"/>
    public EditStep Deleting(bool left) => _steps.Deleting(left);

    /// <summary>Runs <paramref name="edit"/>, AvaloniaEdit's own or this one's, in <paramref name="step"/>.</summary>
    public void Run(EditStep step, Action edit) => _steps.Run(step, edit);

    /// <summary>
    /// Makes <paramref name="edits"/>, in order and apart, as one change, then selects <paramref name="select"/> and
    /// brings the caret into view; without it, AvaloniaEdit's anchors carry the selection, and the view stays, as
    /// bringing the caret into view lays out its line again, and after Ctrl+/ on every line a screen elsewhere.
    /// </summary>
    /// <returns>Whether they went in: not when one touches read-only text.</returns>
    public bool Apply(IReadOnlyList<TextEdit> edits, TextSelection? select = null)
    {
        TextDocument edited = Buffer;
        foreach (TextEdit edit in edits)
        {
            if (!CanChange(edit.Offset, edit.Length))
                return false;
        }
        if (edits.Count == 1)
            Buffer.Replace(edits[0].Offset, edits[0].Length, edits[0].Text);
        else if (edits.Count > 1)
            ReplaceInOneChange(edits);
        // A handler of the change may show another document, which the offsets are not of.
        if (select is { } selection && Buffer == edited)
            Select(selection);
        return true;
    }

    /// <summary>Selects <paramref name="selection"/>, by offsets, and brings the caret into view.</summary>
    public void Select(TextSelection selection)
    {
        _area.Selection = EditorSelection.Create(_area, selection.Anchor, selection.Caret);
        _area.Caret.Offset = selection.Caret;
        _area.Caret.BringCaretToView();
    }

    /// <summary>
    /// Makes <paramref name="edits"/>, then puts back the selection as it was, <paramref name="moved"/> characters
    /// further, also each line of a rectangle selection; in view when it moved forward. Moved back, as over a deleted
    /// pair, it stays in view, and bringing it there laid out the view again: Backspace on a pair took 22 ms, not 13.
    /// </summary>
    public bool Apply(IReadOnlyList<TextEdit> edits, int moved)
    {
        EditorSelection selection = _area.Selection;
        int caret = _area.Caret.Offset;
        int anchor = selection.IsEmpty ? caret : Buffer.GetOffset(selection.StartPosition.Location);
        TextDocument edited = Buffer;
        if (!Apply(edits))
            return false;
        if (Buffer != edited)
            return true;
        if (selection is RectangleSelection rectangle)
        {
            TextViewPosition start = rectangle.StartPosition;
            TextViewPosition end = rectangle.EndPosition;
            var newEnd = new TextViewPosition(end.Line, end.Column + moved);
            _area.Selection = new RectangleSelection(
                _area,
                new TextViewPosition(start.Line, start.Column + moved),
                newEnd
            );
            _area.Caret.Position = newEnd;
        }
        else
        {
            _area.Selection = EditorSelection.Create(_area, anchor + moved, caret + moved);
            _area.Caret.Offset = caret + moved;
        }
        if (moved > 0)
            _area.Caret.BringCaretToView();
        return true;
    }

    /// <summary>
    /// Whether the text from <paramref name="offset"/> on, <paramref name="length"/> long, may change: not read-only,
    /// nor in the hidden tail of a long line.
    /// </summary>
    public bool CanChange(int offset, int length) =>
        length == 0 ? VisibleText.CanInsert(_area, offset) : VisibleText.CanDelete(_area, offset, length);

    /// <summary>
    /// One change from the first edit to the last, with a map of them for the anchors: AvaloniaEdit raises a change per
    /// edit also within an update, and the editor's work per change, such as the highlighter's, would run each time.
    /// </summary>
    private void ReplaceInOneChange(IReadOnlyList<TextEdit> edits)
    {
        int start = edits[0].Offset;
        string text = Buffer.GetText(start, edits[^1].Offset + edits[^1].Length - start);
        var replaced = new StringBuilder(text.Length);
        var map = new OffsetChangeMap();
        int copied = 0;
        int delta = 0;
        foreach ((int offset, int length, string inserted) in edits)
        {
            replaced.Append(text, copied, offset - start - copied).Append(inserted);
            map.Add(new OffsetChangeMapEntry(offset + delta, length, inserted.Length));
            delta += inserted.Length - length;
            copied = offset - start + length;
        }
        replaced.Append(text, copied, text.Length - copied);
        Buffer.Replace(start, text.Length, new StringTextSource(replaced.ToString()), map);
    }
}

/// <summary><paramref name="Length"/> characters from <paramref name="Offset"/> on replaced by <paramref name="Text"/>.</summary>
internal readonly record struct TextEdit(int Offset, int Length, string Text);

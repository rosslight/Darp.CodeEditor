using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Commands;
using EditorSelection = AvaloniaEdit.Editing.Selection;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// VS Code's words (<see cref="WordRules"/>) in one editor's view: a double-click selects a word, also past a line's end,
/// and a drag that follows grows the selection by words, also over text that scrolls under the pointer; Ctrl+Left and
/// Ctrl+Right move by words, with Shift to select; Ctrl+Backspace and Ctrl+Delete delete them, each in a step of its
/// own, and Ctrl+Backspace an empty pair that the editor closed as Backspace does; Ctrl+D selects the word at the caret.
/// AvaloniaEdit keeps the other clicks.
/// </summary>
internal sealed class WordEditing : IEditorFeature
{
    private readonly TextArea _area;
    private readonly TextChanges _changes;
    private readonly AutoClosingPairs _pairs;

    /// <summary>The word that a double-click selected, while its button stays down.</summary>
    private TextRange? _dragStart;

    /// <summary>The pointer's last place over the text view during that drag, which the view may scroll under.</summary>
    private Point _pointer;

    /// <summary>Whether the view scrolled during the drag since the selection last followed the pointer.</summary>
    private bool _scrolled;

    public WordEditing(TextArea area, TextChanges changes, AutoClosingPairs pairs)
    {
        _area = area;
        _changes = changes;
        _pairs = pairs;
        // Ahead of AvaloniaEdit's mouse selection, which handles the text area's events as they bubble.
        area.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        area.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        area.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        area.PointerCaptureLost += OnCaptureLost;
        area.TextView.ScrollOffsetChanged += OnScrolled;
        area.TextView.VisualLinesChanged += OnVisualLinesChanged;
    }

    private TextDocument Buffer => _area.Document;

    public void AddCommands(CommandTable commands)
    {
        commands.Add(CodeEditorCommands.CursorWordLeft, () => Move(WordRules.MoveLeft, select: false));
        commands.Add(CodeEditorCommands.CursorWordLeftSelect, () => Move(WordRules.MoveLeft, select: true));
        commands.Add(CodeEditorCommands.CursorWordEndRight, () => Move(WordRules.MoveRight, select: false));
        commands.Add(CodeEditorCommands.CursorWordEndRightSelect, () => Move(WordRules.MoveRight, select: true));
        commands.Add(CodeEditorCommands.AddSelectionToNextFindMatch, SelectWord);
        const CommandNeeds writable = CommandNeeds.TextFocus | CommandNeeds.Writable;
        commands.Add(CodeEditorCommands.DeleteWordLeft, () => Delete(left: true), writable);
        commands.Add(CodeEditorCommands.DeleteWordRight, () => Delete(left: false), writable);
    }

    public void Dispose()
    {
        _area.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
        _area.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
        _area.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
        _area.PointerCaptureLost -= OnCaptureLost;
        _area.TextView.ScrollOffsetChanged -= OnScrolled;
        _area.TextView.VisualLinesChanged -= OnVisualLinesChanged;
        // Also for a selection that follows the view still, which would be of the document shown next.
        _dragStart = null;
    }

    /// <summary>
    /// Moves the caret to where <paramref name="rule"/> puts it from the caret, ending the selection, or with
    /// <paramref name="select"/> extending it from its anchor.
    /// </summary>
    /// <returns>Whether it moved: not to extend a rectangle selection, which AvaloniaEdit's keys extend.</returns>
    private bool Move(Func<TextDocument, int, int> rule, bool select)
    {
        if (select && _area.Selection is RectangleSelection)
            return false;
        TextSelection selection = _changes.Selection;
        int caret = rule(Buffer, selection.Caret);
        _changes.Select(new TextSelection(select ? selection.Anchor : caret, caret));
        return true;
    }

    /// <summary>
    /// Selects the word at the caret, the first step of <c>addSelectionToNextFindMatch</c> without a selection. Its next
    /// steps add a caret at each next match; the editor has one caret, so it stops at the word.
    /// </summary>
    /// <returns>Whether nothing was selected and the caret touched a word.</returns>
    private bool SelectWord()
    {
        int caret = _area.Caret.Offset;
        DocumentLine line = Buffer.GetLineByOffset(caret);
        if (!_area.Selection.IsEmpty || WordRules.WordAt(Buffer.GetText(line), caret - line.Offset) is not { } word)
            return false;
        _changes.Select(new TextSelection(line.Offset + word.Start, line.Offset + word.End));
        return true;
    }

    /// <summary>
    /// Deletes the selection, or else the word to the <paramref name="left"/> or right of the caret, in a step of its
    /// own; to the left, an empty pair that the editor closed first goes as a whole, as Backspace deletes it.
    /// </summary>
    /// <returns>Whether there was something to delete, and it went.</returns>
    private bool Delete(bool left)
    {
        if (!_area.Selection.IsEmpty)
        {
            // AvaloniaEdit leaves read-only parts of the selection; a handler of the change may show another document.
            TextDocument edited = Buffer;
            int length = edited.TextLength;
            _changes.Run(EditStep.Own, () => _area.Selection.ReplaceSelectionWithText(""));
            return edited.TextLength != length;
        }
        if (left && _pairs.DeletePair())
            return true;
        int caret = _area.Caret.Offset;
        if ((left ? WordRules.DeleteLeft(Buffer, caret) : WordRules.DeleteRight(Buffer, caret)) is not { } range)
            return false;
        bool deleted = false;
        _changes.Run(
            EditStep.Own,
            () =>
                deleted = _changes.Apply([new TextEdit(range.Start, range.Length, "")], new TextSelection(range.Start))
        );
        return deleted;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        TextView view = _area.TextView;
        Point pointer = e.GetPosition(view);
        if (
            e.ClickCount != 2
            || e.KeyModifiers != KeyModifiers.None
            || !e.GetCurrentPoint(view).Properties.IsLeftButtonPressed
            || e.Source is not Visual source
            || (source != view && !view.IsVisualAncestorOf(source))
            || LineAt(pointer) is not (var line, var text, var column)
        )
            return;
        _pointer = pointer;
        TextRange word = WordRules.DoubleClicked(text, column);
        _dragStart = new TextRange(line.Offset + word.Start, word.Length);
        _changes.Select(new TextSelection(line.Offset + word.Start, line.Offset + word.End));
        e.Pointer.Capture(_area);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || !e.GetCurrentPoint(_area).Properties.IsLeftButtonPressed)
            return;
        _pointer = e.GetPosition(_area.TextView);
        if (DragTo(start) is { } selection)
        {
            _changes.Select(selection);
            e.Handled = true;
        }
    }

    private void OnScrolled(object? sender, EventArgs e) => _scrolled |= _dragStart is not null;

    /// <summary>
    /// Once the view has the lines that it scrolled to during the drag, the selection follows the pointer over them, as
    /// AvaloniaEdit's own drag does: after this layout pass, as a new selection redraws lines, and without bringing the
    /// caret into view, which is under the pointer or at the view's edge nearest it, and would only scroll on.
    /// </summary>
    private void OnVisualLinesChanged(object? sender, EventArgs e)
    {
        if (!_scrolled)
            return;
        _scrolled = false;
        _area.Dispatcher.Post(() =>
        {
            if (_dragStart is { } start && DragTo(start) is { } selection)
            {
                _area.Selection = EditorSelection.Create(_area, selection.Anchor, selection.Caret);
                _area.Caret.Offset = selection.Caret;
            }
        });
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStart is null)
            return;
        _dragStart = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _dragStart = null;

    /// <summary>The selection from the drag's <paramref name="start"/> by words to the pointer, if over a line.</summary>
    private TextSelection? DragTo(TextRange start) =>
        LineAt(_pointer) is (var line, var text, var column)
            ? WordRules.DragTo(start, text, line.Offset, column)
            : null;

    /// <summary>
    /// The line at <paramref name="point"/> of the text view, its text and the nearest column; above or below the text,
    /// the nearest line.
    /// </summary>
    private (DocumentLine Line, string Text, int Column)? LineAt(Point point)
    {
        TextView view = _area.TextView;
        point = point.WithY(Math.Clamp(point.Y, 0, view.Bounds.Height)) + view.ScrollOffset;
        if (view.GetPosition(point) is not { } position)
            return null;
        DocumentLine line = Buffer.GetLineByNumber(position.Line);
        string text = Buffer.GetText(line);
        return (line, text, Math.Clamp(position.Column - 1, 0, text.Length));
    }
}

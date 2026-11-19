using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Where one editor's edits start a new undo step, as VS Code puts its undo stops (its cursor's
/// <c>_executeEditOperation</c> with <c>shouldPushStackElementBetween</c>): typing goes on in the step of the typing
/// before it; a space after other typing starts a step that the word after it goes on in, while a word after several
/// spaces starts one; Backspace goes on in the step of the Backspace before it, and Delete likewise, unless it joins
/// lines. A caret move, and a change of the text from elsewhere, such as an undo, end the step.
/// </summary>
/// <remarks>
/// A step is one of AvaloniaEdit's undo groups, continued while the next edit goes on in it and no other group came
/// after it. Undo puts back the selection from before the step, as AvaloniaEdit's undo does; redo puts back the
/// selection after the step's last edit.
/// </remarks>
internal sealed class UndoSteps : IDisposable
{
    private readonly TextArea _area;
    private readonly TextDocument _buffer;

    /// <summary>The undo group that the next edit may go on in; null once something ended it.</summary>
    private object? _step;

    /// <summary>The kind of this editor's last edit.</summary>
    private EditKind _previous = EditKind.Other;

    /// <summary>Whether an edit runs: its changes and caret moves end no step.</summary>
    private bool _editing;

    public UndoSteps(TextArea area)
    {
        _area = area;
        _buffer = area.Document;
        area.Caret.PositionChanged += OnCaretMoved;
        area.SelectionChanged += OnCaretMoved;
        _buffer.Changed += OnChanged;
    }

    public void Dispose()
    {
        _area.Caret.PositionChanged -= OnCaretMoved;
        _area.SelectionChanged -= OnCaretMoved;
        _buffer.Changed -= OnChanged;
    }

    /// <summary>The step of typing <paramref name="text"/>, after the edits before it.</summary>
    public EditStep Typing(string text)
    {
        EditKind kind =
            text != " " ? EditKind.TypingOther
            : _previous is EditKind.TypingFirstSpace or EditKind.TypingConsecutiveSpace
                ? EditKind.TypingConsecutiveSpace
            : EditKind.TypingFirstSpace;
        // After typing, any other kind of edit starts a step; after a first space, typing goes on; otherwise an edit of
        // another kind does, with spaces as one kind.
        bool starts =
            IsTyping(_previous) && !IsTyping(kind) ? true
            : _previous == EditKind.TypingFirstSpace ? false
            : Spaces(_previous) != Spaces(kind);
        return new EditStep(kind, starts);
    }

    /// <summary>The step of Backspace, to the <paramref name="left"/>, or Delete, at the caret or the selection.</summary>
    public EditStep Deleting(bool left)
    {
        EditKind kind = left ? EditKind.DeletingLeft : EditKind.DeletingRight;
        int caret = _area.Caret.Offset;
        Selection selection = _area.Selection;
        bool joinsLines = selection.IsEmpty
            ? caret == (left ? Line().Offset : Line().EndOffset) && caret != (left ? 0 : _buffer.TextLength)
            : selection is not RectangleSelection && selection.IsMultiline;
        return new EditStep(kind, StartsStep: _previous != kind || joinsLines);

        DocumentLine Line() => _buffer.GetLineByOffset(caret);
    }

    /// <summary>Runs <paramref name="edit"/> in <paramref name="step"/>.</summary>
    public void Run(EditStep step, Action edit)
    {
        UndoStack undo = _buffer.UndoStack;
        // Only while no other group came after the step.
        object group = !step.StartsStep && _step is { } open && undo.LastGroupDescriptor == open ? open : new object();
        if (group == _step)
            undo.StartContinuedUndoGroup(group);
        else
            undo.StartUndoGroup(group);
        _editing = true;
        try
        {
            edit();
            undo.PushOptional(new RedoSelection(_area));
        }
        finally
        {
            _editing = false;
            undo.EndUndoGroup();
        }
        _step = step.EndsStep ? null : group;
        _previous = step.Kind;
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        if (!_editing)
            _step = null;
    }

    private void OnChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_editing)
            return;
        _step = null;
        _previous = EditKind.Other;
    }

    private static bool IsTyping(EditKind kind) =>
        kind is EditKind.TypingOther or EditKind.TypingFirstSpace or EditKind.TypingConsecutiveSpace;

    private static EditKind Spaces(EditKind kind) =>
        kind == EditKind.TypingConsecutiveSpace ? EditKind.TypingFirstSpace : kind;

    /// <summary>
    /// Puts back the caret and the selection after an edit when the edit is redone; nothing when it is undone, where
    /// AvaloniaEdit puts back those from before it. Like AvaloniaEdit's, it keeps no text area alive.
    /// </summary>
    private sealed class RedoSelection : IUndoableOperation
    {
        private readonly WeakReference<TextArea> _area;
        private readonly TextDocument _document;
        private readonly TextLocation _caret;
        private readonly TextViewPosition _start;
        private readonly TextViewPosition _end;
        private readonly bool _isRectangle;
        private readonly bool _isEmpty;

        public RedoSelection(TextArea area)
        {
            _area = new WeakReference<TextArea>(area);
            _document = area.Document;
            // Not the caret's position, whose visual column would lay out its line in the middle of the change.
            _caret = area.Caret.Location;
            Selection selection = area.Selection;
            _isRectangle = selection is RectangleSelection;
            _isEmpty = selection.IsEmpty && !_isRectangle;
            _start = selection.StartPosition;
            _end = selection.EndPosition;
        }

        public void Undo() { }

        public void Redo()
        {
            if (!_area.TryGetTarget(out TextArea? area) || area.Document != _document)
                return;
            // A rectangle's caret is at its end, also in virtual space.
            area.Caret.Position = _isRectangle ? _end : new TextViewPosition(_caret);
            if (_isEmpty)
                area.ClearSelection();
            else if (_isRectangle)
                area.Selection = new RectangleSelection(area, _start, _end);
            else
                area.Selection = Selection.Create(
                    area,
                    _document.GetOffset(_start.Location),
                    _document.GetOffset(_end.Location)
                );
        }
    }
}

/// <summary>The kinds of edits that the undo steps tell apart.</summary>
internal enum EditKind
{
    Other,
    DeletingLeft,
    DeletingRight,
    TypingOther,
    TypingFirstSpace,
    TypingConsecutiveSpace,
}

/// <summary>An edit of <paramref name="Kind"/>, in the step before it or a new one, which may end after it.</summary>
internal readonly record struct EditStep(EditKind Kind, bool StartsStep, bool EndsStep = false)
{
    /// <summary>An edit in a step of its own, such as a Tab or a surrounded selection.</summary>
    public static EditStep Own { get; } = new(EditKind.Other, StartsStep: true, EndsStep: true);
}

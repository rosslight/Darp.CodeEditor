using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Where one editor's edits start a new undo step, as VS Code puts its undo stops (its cursor's
/// <c>_executeEditOperation</c> with <c>shouldPushStackElementBetween</c>): typing goes on in the step of the typing
/// before it; a space after other typing starts a step and the word after it goes on in that step, while a word after
/// several spaces starts one; Backspace goes on in the step of the Backspace before it, and Delete likewise, unless
/// it joins lines. A caret move, and a change of the text from elsewhere, such as an undo, end the step.
/// </summary>
/// <remarks>
/// A step is one of AvaloniaEdit's undo groups, continued while the next edit goes on in it and no other group came
/// after it. Undo puts back the selection from before the step, as AvaloniaEdit's undo does; redo puts back the
/// selection after the step's last edit, as VS Code's does.
/// </remarks>
internal sealed class UndoStops : IDisposable
{
    private readonly TextArea _textArea;
    private readonly TextDocument _buffer;

    /// <summary>The undo group that the next edit may go on in; null once something ended it.</summary>
    private object? _step;

    /// <summary>Whether an edit of <see cref="Edit"/> runs: its changes and caret moves end no step.</summary>
    private bool _editing;

    public UndoStops(TextArea textArea)
    {
        _textArea = textArea;
        _buffer = textArea.Document;
        textArea.Caret.PositionChanged += OnCaretMoved;
        textArea.SelectionChanged += OnCaretMoved;
        _buffer.Changed += OnChanged;
    }

    /// <summary>The kind of this editor's last edit, VS Code's <c>prevEditOperationType</c>.</summary>
    public EditKind Previous { get; private set; } = EditKind.Other;

    public void Dispose()
    {
        _textArea.Caret.PositionChanged -= OnCaretMoved;
        _textArea.SelectionChanged -= OnCaretMoved;
        _buffer.Changed -= OnChanged;
    }

    /// <summary>
    /// The kind of typing <paramref name="text"/> after an edit of <paramref name="previous"/>: VS Code's
    /// <c>getTypingOperation</c>.
    /// </summary>
    public static EditKind Typing(string text, EditKind previous) =>
        text != " " ? EditKind.TypingOther
        : previous is EditKind.TypingFirstSpace or EditKind.TypingConsecutiveSpace ? EditKind.TypingConsecutiveSpace
        : EditKind.TypingFirstSpace;

    /// <summary>
    /// Whether an edit of <paramref name="next"/> after one of <paramref name="previous"/> starts a step: VS Code's
    /// <c>shouldPushStackElementBetween</c>. After typing, any other kind of edit does; after a first space, typing goes
    /// on; otherwise an edit of another kind does, with spaces as one kind.
    /// </summary>
    public static bool StopsBetween(EditKind previous, EditKind next) =>
        IsTyping(previous) && !IsTyping(next) ? true
        : previous == EditKind.TypingFirstSpace ? false
        : Spaces(previous) != Spaces(next);

    /// <summary>
    /// Runs <paramref name="edit"/>, an edit of <paramref name="kind"/>: in the step of the edits before it, or after
    /// <paramref name="stopBefore"/> in a step of its own, which <paramref name="stopAfter"/> ends.
    /// </summary>
    public void Edit(EditKind kind, bool stopBefore, Action edit, bool stopAfter = false)
    {
        UndoStack undo = _buffer.UndoStack;
        // Only while no other group came after the step.
        object step = !stopBefore && _step is { } open && undo.LastGroupDescriptor == open ? open : new object();
        if (step == _step)
            undo.StartContinuedUndoGroup(step);
        else
            undo.StartUndoGroup(step);
        bool editing = _editing;
        _editing = true;
        try
        {
            edit();
            undo.PushOptional(new RedoSelection(_textArea));
        }
        finally
        {
            _editing = editing;
            undo.EndUndoGroup();
        }
        _step = stopAfter ? null : step;
        Previous = kind;
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        if (!_editing)
            _step = null;
    }

    /// <summary>A change of the text from elsewhere, as VS Code's cursor takes one: an edit of another kind.</summary>
    private void OnChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_editing)
            return;
        _step = null;
        Previous = EditKind.Other;
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
        private readonly WeakReference<TextArea> _textArea;
        private readonly TextDocument _document;
        private readonly TextLocation _caret;
        private readonly TextViewPosition _start;
        private readonly TextViewPosition _end;
        private readonly bool _isRectangle;
        private readonly bool _isEmpty;

        public RedoSelection(TextArea textArea)
        {
            _textArea = new WeakReference<TextArea>(textArea);
            _document = textArea.Document;
            // Not the caret's position, whose visual column would lay out its line in the middle of the change.
            _caret = textArea.Caret.Location;
            Selection selection = textArea.Selection;
            _isRectangle = selection is RectangleSelection;
            _isEmpty = selection.IsEmpty && !_isRectangle;
            _start = selection.StartPosition;
            _end = selection.EndPosition;
        }

        public void Undo() { }

        public void Redo()
        {
            if (!_textArea.TryGetTarget(out TextArea? textArea) || textArea.Document != _document)
                return;
            // A rectangle's caret is at its end, also in virtual space.
            textArea.Caret.Position = _isRectangle ? _end : new TextViewPosition(_caret);
            if (_isEmpty)
                textArea.ClearSelection();
            else if (_isRectangle)
                textArea.Selection = new RectangleSelection(textArea, _start, _end);
            else
                textArea.Selection = Selection.Create(
                    textArea,
                    _document.GetOffset(_start.Location),
                    _document.GetOffset(_end.Location)
                );
        }
    }
}

/// <summary>The kinds of edits that VS Code tells apart for its undo stops, its <c>EditOperationType</c>.</summary>
internal enum EditKind
{
    Other,
    DeletingLeft,
    DeletingRight,
    TypingOther,
    TypingFirstSpace,
    TypingConsecutiveSpace,
}

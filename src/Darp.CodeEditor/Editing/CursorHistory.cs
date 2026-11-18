using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The selections that the caret left, for VS Code's <c>cursorUndo</c> and <c>cursorRedo</c>: each move of the caret or
/// change of the selection keeps the one before it, with the scroll position, up to 50 of them, and any change of the
/// text forgets them all.
/// </summary>
/// <remarks>
/// AvaloniaEdit raises a change of the caret and one of the selection apart, also for one move; the changes up to the
/// dispatcher's next turn count as one, as VS Code's cursor raises one event per move.
/// </remarks>
internal sealed class CursorHistory : IDisposable
{
    /// <summary>How many selections the history keeps, as VS Code's.</summary>
    public const int Limit = 50;

    private readonly TextArea _textArea;
    private readonly TextDocument _buffer;
    private readonly Dispatcher _dispatcher;
    private readonly List<State> _undo = [];
    private readonly List<State> _redo = [];

    /// <summary>The selection that the next move leaves.</summary>
    private State _current;

    /// <summary>Whether a move waits for the dispatcher's next turn to be kept.</summary>
    private bool _pending;

    /// <summary>Whether the text changed since the move that waits began: the move then keeps nothing.</summary>
    private bool _textChanged;

    /// <summary>Where the view was scrolled when the move that waits began.</summary>
    private Vector _scrollBefore;

    /// <summary>Whether the history puts back a selection: that is no move of its own.</summary>
    private bool _applying;

    private bool _disposed;

    public CursorHistory(TextArea textArea, Dispatcher dispatcher)
    {
        _textArea = textArea;
        _buffer = textArea.Document;
        _dispatcher = dispatcher;
        _current = Capture();
        textArea.Caret.PositionChanged += OnMoved;
        textArea.SelectionChanged += OnMoved;
        _buffer.Changed += OnTextChanged;
    }

    public void Dispose()
    {
        _disposed = true;
        _textArea.Caret.PositionChanged -= OnMoved;
        _textArea.SelectionChanged -= OnMoved;
        _buffer.Changed -= OnTextChanged;
    }

    /// <summary>Puts back the selection that the last move left, and keeps the current one for <see cref="Redo"/>.</summary>
    public void Undo() => Move(_undo, _redo);

    /// <summary>Puts back the selection that <see cref="Undo"/> left.</summary>
    public void Redo() => Move(_redo, _undo);

    private void Move(List<State> from, List<State> to)
    {
        // A move that waits counts first.
        if (_pending)
            Keep();
        if (from.Count == 0)
            return;
        to.Add(Capture());
        State state = from[^1];
        from.RemoveAt(from.Count - 1);
        _applying = true;
        try
        {
            _textArea.Selection = state.Selection;
            _textArea.Caret.Position = state.Caret;
            if (ScrollViewer is { } scrollViewer)
                scrollViewer.Offset = state.Scroll;
        }
        finally
        {
            _applying = false;
        }
        _current = Capture();
    }

    private void OnMoved(object? sender, EventArgs e)
    {
        if (_applying || _pending)
            return;
        _pending = true;
        _scrollBefore = ScrollViewer?.Offset ?? default;
        _dispatcher.Post(() =>
        {
            if (_pending && !_disposed)
                Keep();
        });
    }

    /// <summary>VS Code forgets the history with each change of the text.</summary>
    private void OnTextChanged(object? sender, DocumentChangeEventArgs e)
    {
        _undo.Clear();
        _redo.Clear();
        _textChanged = true;
    }

    /// <summary>Keeps the selection that the move left, unless it changed nothing or came with a change of the text.</summary>
    private void Keep()
    {
        _pending = false;
        State now = Capture();
        if (!_textChanged && !now.SameAs(_current) && (_undo.Count == 0 || !_undo[^1].SameAs(_current)))
        {
            _undo.Add(_current with { Scroll = _scrollBefore });
            if (_undo.Count > Limit)
                _undo.RemoveAt(0);
            _redo.Clear();
        }
        _textChanged = false;
        _current = now;
    }

    private State Capture() => new(_textArea.Selection, _textArea.Caret.Position, ScrollViewer?.Offset ?? default);

    private ScrollViewer? ScrollViewer => _textArea.FindAncestorOfType<ScrollViewer>();

    /// <summary>A selection, its caret, and the scroll position.</summary>
    private readonly record struct State(Selection Selection, TextViewPosition Caret, Vector Scroll)
    {
        /// <summary>
        /// Whether the selection and the caret are the same as <paramref name="other"/>'s, by their places in the text:
        /// a caret's visual column may be found later.
        /// </summary>
        public bool SameAs(State other) =>
            Caret.Location == other.Caret.Location
            && Selection.GetType() == other.Selection.GetType()
            && Selection.StartPosition.Location == other.Selection.StartPosition.Location
            && Selection.EndPosition.Location == other.Selection.EndPosition.Location
            && (
                Selection is not RectangleSelection
                || Selection.StartPosition.VisualColumn == other.Selection.StartPosition.VisualColumn
                    && Selection.EndPosition.VisualColumn == other.Selection.EndPosition.VisualColumn
            );
    }
}

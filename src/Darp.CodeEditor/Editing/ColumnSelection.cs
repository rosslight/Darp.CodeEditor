using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Column selection, as VS Code's <c>cursorColumnSelect</c> commands and Shift+Alt+drag make it: a rectangle from the
/// selection's anchor to a line and a column, which the commands move by a column, a line or a page, and the drag to
/// the pointer, also past the ends of lines. Columns count as VS Code's visible columns, a tab to its next stop.
/// </summary>
/// <remarks>
/// The rectangle is AvaloniaEdit's rectangle selection, which takes every line between, with virtual space where a line
/// is short; VS Code puts a caret only on the lines that reach into the rectangle.
/// </remarks>
internal sealed class ColumnSelection : IDisposable
{
    private readonly TextArea _textArea;

    /// <summary>The rectangle that the last column selection made, and its selection; null before one.</summary>
    private (Rectangle Rectangle, Selection Selection)? _last;

    /// <summary>Whether a Shift+Alt+drag selects columns: from its press until its release.</summary>
    private bool _dragging;

    public ColumnSelection(TextArea textArea)
    {
        _textArea = textArea;
        textArea.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        textArea.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        textArea.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        textArea.PointerCaptureLost += OnCaptureLost;
    }

    public void Dispose()
    {
        _textArea.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
        _textArea.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
        _textArea.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
        _textArea.PointerCaptureLost -= OnCaptureLost;
        _dragging = false;
    }

    /// <summary>
    /// Moves the rectangle's corner at the caret, as VS Code's <c>cursorColumnSelectLeft</c>, <c>Right</c>, <c>Up</c>,
    /// <c>Down</c>, <c>PageUp</c> and <c>PageDown</c> do: by a column, at most to the end of the rectangle's longest
    /// line, or by a line or a page, within the text.
    /// </summary>
    public void Move(ColumnMove move)
    {
        Rectangle rectangle = Current();
        TextDocument buffer = _textArea.Document;
        int page = Math.Max(1, (int)(_textArea.TextView.Bounds.Height / _textArea.TextView.DefaultLineHeight) - 2);
        rectangle = move switch
        {
            ColumnMove.Left => rectangle with { ToColumn = Math.Max(0, rectangle.ToColumn - 1) },
            ColumnMove.Right => rectangle with
            {
                ToColumn = rectangle.ToColumn < LongestLine(rectangle) ? rectangle.ToColumn + 1 : rectangle.ToColumn,
            },
            ColumnMove.Up or ColumnMove.PageUp => rectangle with
            {
                ToLine = Math.Max(1, rectangle.ToLine - (move == ColumnMove.Up ? 1 : page)),
            },
            _ => rectangle with
            {
                ToLine = Math.Min(buffer.LineCount, rectangle.ToLine + (move == ColumnMove.Down ? 1 : page)),
            },
        };
        Select(rectangle);
    }

    /// <summary>
    /// The rectangle that the selection is: the last column selection's while the selection is still the one it made,
    /// or else from the selection's anchor to the caret.
    /// </summary>
    private Rectangle Current()
    {
        if (_last is var (rectangle, selection) && ReferenceEquals(_textArea.Selection, selection))
            return rectangle;
        TextViewPosition caret = _textArea.Caret.Position;
        TextViewPosition anchor = _textArea.Selection.IsEmpty ? caret : _textArea.Selection.StartPosition;
        return new Rectangle(anchor.Line, VisibleColumn(anchor), caret.Line, VisibleColumn(caret));
    }

    /// <summary>Selects <paramref name="rectangle"/>, with the caret at its corner, and brings that into view.</summary>
    private void Select(Rectangle rectangle)
    {
        TextViewPosition from = PositionAt(rectangle.FromLine, rectangle.FromColumn);
        TextViewPosition to = PositionAt(rectangle.ToLine, rectangle.ToColumn);
        var selection = new RectangleSelection(_textArea, from, to);
        _textArea.Selection = selection;
        _textArea.Caret.Position = to;
        _textArea.Caret.BringCaretToView();
        _last = (rectangle, selection);
    }

    /// <summary>The widest line of the rectangle, in visible columns: VS Code's limit for moving right.</summary>
    private int LongestLine(Rectangle rectangle)
    {
        int widest = 0;
        for (
            int line = Math.Min(rectangle.FromLine, rectangle.ToLine);
            line <= Math.Max(rectangle.FromLine, rectangle.ToLine);
            line++
        )
            widest = Math.Max(widest, Width(LineText(line)));
        return widest;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        TextView view = _textArea.TextView;
        if (
            e.ClickCount != 1
            || e.KeyModifiers != (KeyModifiers.Shift | KeyModifiers.Alt)
            || !e.GetCurrentPoint(view).Properties.IsLeftButtonPressed
            || e.Source is not Visual source
            || source != view && !view.IsVisualAncestorOf(source)
            || PointerAt(e) is not var (line, column)
        )
            return;
        Rectangle rectangle = Current();
        Select(rectangle with { ToLine = line, ToColumn = column });
        _dragging = true;
        e.Pointer.Capture(_textArea);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (
            !_dragging
            || !e.GetCurrentPoint(_textArea).Properties.IsLeftButtonPressed
            || _last is not var (rectangle, _)
            || PointerAt(e) is not var (line, column)
        )
            return;
        Select(rectangle with { ToLine = line, ToColumn = column });
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _dragging = false;

    /// <summary>
    /// The line under the pointer and the visible column nearest to it, also past the line's end; above or below the
    /// text, the nearest line.
    /// </summary>
    private (int Line, int Column)? PointerAt(PointerEventArgs e)
    {
        TextView view = _textArea.TextView;
        view.EnsureVisualLines();
        Point point = e.GetPosition(view);
        point = point.WithY(Math.Clamp(point.Y, 0, Math.Max(0, view.Bounds.Height - 1))) + view.ScrollOffset;
        if ((view.GetVisualLineFromVisualTop(point.Y) ?? view.VisualLines.LastOrDefault()) is not { } visualLine)
            return null;
        int visualColumn = visualLine.GetVisualColumn(point, allowVirtualSpace: true);
        string text = LineText(visualLine.FirstDocumentLine.LineNumber);
        int column =
            visualColumn <= visualLine.VisualLength
                ? Width(text.AsSpan(0, Math.Min(text.Length, visualLine.GetRelativeOffset(visualColumn))))
                : Width(text) + visualColumn - visualLine.VisualLength;
        return (visualLine.FirstDocumentLine.LineNumber, column);
    }

    /// <summary>
    /// The visible column of <paramref name="position"/>: of its offset in its line, a tab to its next stop, or past the
    /// line's end in virtual space.
    /// </summary>
    private int VisibleColumn(TextViewPosition position)
    {
        string text = LineText(position.Line);
        int offset = Math.Clamp(position.Column - 1, 0, text.Length);
        return position.VisualColumn > text.Length && offset == text.Length
            ? Width(text) + position.VisualColumn - text.Length
            : Width(text.AsSpan(0, offset));
    }

    /// <summary>
    /// The position at <paramref name="column"/>, a visible column, of line <paramref name="line"/>: before the
    /// character nearest to it, as VS Code's <c>columnFromVisibleColumn</c>, or past the line's end in virtual space.
    /// </summary>
    private TextViewPosition PositionAt(int line, int column)
    {
        string text = LineText(line);
        int width = Width(text);
        if (column >= width)
            return new TextViewPosition(line, text.Length + 1, text.Length + column - width);
        int size = _textArea.Options.IndentationSize;
        int before = 0;
        for (int offset = 0; offset < text.Length; offset++)
        {
            int after = text[offset] == '\t' ? (before / size + 1) * size : before + 1;
            if (after >= column)
            {
                int nearest = after - column < column - before ? offset + 1 : offset;
                return new TextViewPosition(line, nearest + 1, nearest);
            }
            before = after;
        }
        return new TextViewPosition(line, text.Length + 1, text.Length);
    }

    /// <summary>The visible columns of <paramref name="text"/> from a line's start, a tab to its next stop.</summary>
    private int Width(ReadOnlySpan<char> text)
    {
        int size = _textArea.Options.IndentationSize;
        int columns = 0;
        foreach (char c in text)
            columns = c == '\t' ? (columns / size + 1) * size : columns + 1;
        return columns;
    }

    private string LineText(int line) => _textArea.Document.GetText(_textArea.Document.GetLineByNumber(line));

    /// <summary>
    /// Removes AvaloniaEdit's keys for rectangle selection, Shift+Alt with the arrows, Home and End, which are VS Code's
    /// keys for other commands; the editor's column selection takes VS Code's keys.
    /// </summary>
    public static void RemoveBoxSelectionKeys(TextArea textArea)
    {
        HashSet<object> boxSelection =
        [
            RectangleSelection.BoxSelectLeftByCharacter,
            RectangleSelection.BoxSelectRightByCharacter,
            RectangleSelection.BoxSelectLeftByWord,
            RectangleSelection.BoxSelectRightByWord,
            RectangleSelection.BoxSelectUpByLine,
            RectangleSelection.BoxSelectDownByLine,
            RectangleSelection.BoxSelectToLineStart,
            RectangleSelection.BoxSelectToLineEnd,
        ];
        ICollection<KeyBinding> bindings = textArea.DefaultInputHandler.CaretNavigation.KeyBindings;
        foreach (
            KeyBinding binding in bindings
                .Where(b => b.Command is { } command && boxSelection.Contains(command))
                .ToList()
        )
            bindings.Remove(binding);
    }

    /// <summary>VS Code's column select data: from a line and visible column, the anchor, to another, the caret.</summary>
    private readonly record struct Rectangle(int FromLine, int FromColumn, int ToLine, int ToColumn);
}

/// <summary>How a column selection's corner at the caret moves.</summary>
internal enum ColumnMove
{
    Left,
    Right,
    Up,
    Down,
    PageUp,
    PageDown,
}

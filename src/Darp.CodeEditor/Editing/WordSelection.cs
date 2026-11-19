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
/// A double-click selects a word as in VS Code (<see cref="WordRules.WordAt"/>), with a click past a line's end at the
/// line's end, and a drag that follows grows the selection by whole words. AvaloniaEdit keeps the other clicks.
/// </summary>
internal sealed class WordSelection : IDisposable
{
    private readonly TextArea _textArea;

    /// <summary>The word that the double-click selected, while its button is down.</summary>
    private TextRange? _start;

    public WordSelection(TextArea textArea)
    {
        _textArea = textArea;
        // Ahead of AvaloniaEdit's mouse selection, which handles the text area's events as they bubble.
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
        _start = null;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        TextView view = _textArea.TextView;
        if (
            e.ClickCount != 2
            || e.KeyModifiers != KeyModifiers.None
            || !e.GetCurrentPoint(view).Properties.IsLeftButtonPressed
            || e.Source is not Visual source
            || source != view && !view.IsVisualAncestorOf(source)
            || LineAt(e) is not (var line, var text, var column)
        )
            return;
        (int start, int end) = WordRules.WordAt(text, column);
        _start = TextRange.FromBounds(line.Offset + start, line.Offset + end);
        Select(line.Offset + start, line.Offset + end);
        e.Pointer.Capture(_textArea);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (
            _start is not { } start
            || !e.GetCurrentPoint(_textArea).Properties.IsLeftButtonPressed
            || LineAt(e) is not (var line, var text, var column)
        )
            return;
        (int anchor, int caret) = WordRules.DragTo(start, text, line.Offset, column);
        Select(anchor, caret);
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_start is null)
            return;
        _start = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _start = null;

    /// <summary>The line under the pointer, its text, and the column nearest to it; above or below the text, the nearest line.</summary>
    private (DocumentLine Line, string Text, int Column)? LineAt(PointerEventArgs e)
    {
        TextView view = _textArea.TextView;
        Point point = e.GetPosition(view);
        point = point.WithY(Math.Clamp(point.Y, 0, view.Bounds.Height)) + view.ScrollOffset;
        if (view.GetPosition(point) is not { } position)
            return null;
        DocumentLine line = _textArea.Document.GetLineByNumber(position.Line);
        string text = _textArea.Document.GetText(line);
        return (line, text, Math.Clamp(position.Column - 1, 0, text.Length));
    }

    private void Select(int anchor, int caret)
    {
        _textArea.Caret.Offset = caret;
        _textArea.Selection = Selection.Create(_textArea, anchor, caret);
        _textArea.Caret.BringCaretToView();
    }
}

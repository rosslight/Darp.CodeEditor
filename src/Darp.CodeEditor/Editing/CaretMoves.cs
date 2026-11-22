using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.Editing;

namespace Darp.CodeEditor.Editing;

/// <summary>What moves the caret of an editor's view.</summary>
internal enum CaretMover
{
    /// <summary>
    /// Anything but a key or the pointer in the text: code, a command, an undo, or another editor.
    /// </summary>
    Other,

    /// <summary>A key that goes to the text, or typed text.</summary>
    Keyboard,

    /// <summary>The pointer in the text area, as by a click or a drag.</summary>
    Pointer,
}

/// <summary>
/// Tells what moves the caret of one editor's view, as VS Code's caret events tell their source: popups end or go on by
/// it. The keyboard moves it while a key that goes to the text, or typed text, is handled; the pointer while a pointer
/// event in the text area is; anything else otherwise.
/// </summary>
internal sealed class CaretMoves : IDisposable
{
    private readonly Control _editor;
    private readonly TextArea _textArea;
    private readonly EventHandler<RoutedEventArgs> _onPointer;
    private readonly EventHandler<RoutedEventArgs> _onHandled;

    /// <param name="editor">The editor: input events bubble up to it once the text area has handled them.</param>
    /// <param name="textArea">The text area.</param>
    public CaretMoves(Control editor, TextArea textArea)
    {
        _editor = editor;
        _textArea = textArea;
        _onPointer = (_, _) => Mover = CaretMover.Pointer;
        _onHandled = (_, _) => Mover = CaretMover.Other;
        foreach (RoutedEvent pointerEvent in PointerEvents)
        {
            // Tunneling, a pointer event reaches the text area before the text area handles it.
            textArea.AddHandler(pointerEvent, _onPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
            editor.AddHandler(pointerEvent, _onHandled, RoutingStrategies.Bubble, handledEventsToo: true);
        }
        editor.AddHandler(InputElement.KeyDownEvent, _onHandled, RoutingStrategies.Bubble, handledEventsToo: true);
        editor.AddHandler(InputElement.TextInputEvent, _onHandled, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>What moves the caret now.</summary>
    public CaretMover Mover { get; private set; }

    private static RoutedEvent[] PointerEvents =>
        [InputElement.PointerPressedEvent, InputElement.PointerMovedEvent, InputElement.PointerReleasedEvent];

    /// <summary>A key goes to the text, or text is typed: the keyboard moves the caret until that is handled.</summary>
    public void OnKeyboard() => Mover = CaretMover.Keyboard;

    public void Dispose()
    {
        foreach (RoutedEvent pointerEvent in PointerEvents)
        {
            _textArea.RemoveHandler(pointerEvent, _onPointer);
            _editor.RemoveHandler(pointerEvent, _onHandled);
        }
        _editor.RemoveHandler(InputElement.KeyDownEvent, _onHandled);
        _editor.RemoveHandler(InputElement.TextInputEvent, _onHandled);
    }
}

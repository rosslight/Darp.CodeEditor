using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The popups of one editor's view, which count as the editor's own for the keyboard: while it is in one of them, as in
/// a hover that the user clicked into, the editor keeps its popups. Once the keyboard has left the editor and its
/// popups, its owner closes them.
/// </summary>
/// <remarks>
/// The popups live in the window's overlay layer, outside the editor, so the window tells where the keyboard went:
/// each element that loses it raises its loss up to the window, also in a popup. The check is posted, as the keyboard
/// leaves an element before it reaches the next.
/// </remarks>
internal sealed class EditorPopups : IDisposable
{
    /// <summary>The editor that a popup's root belongs to.</summary>
    private static readonly AttachedProperty<Visual?> s_ownerProperty = AvaloniaProperty.RegisterAttached<
        EditorPopups,
        Visual,
        Visual?
    >("Owner");

    private readonly CodeEditor _editor;
    private readonly Action _focusLeft;

    /// <summary>The window the editor is in as it shows the document; null once disposed.</summary>
    private TopLevel? _window;

    /// <param name="editor">The editor, which its popups' roots name as theirs (<see cref="SetOwner"/>).</param>
    /// <param name="focusLeft">Closes the popups.</param>
    public EditorPopups(CodeEditor editor, Action focusLeft)
    {
        _editor = editor;
        _focusLeft = focusLeft;
        _window = TopLevel.GetTopLevel(editor);
        _window?.AddHandler(InputElement.LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Names <paramref name="editor"/> as the owner of a popup's <paramref name="root"/>.</summary>
    public static void SetOwner(Visual root, Visual editor) => root.SetValue(s_ownerProperty, editor);

    /// <summary>Whether the keyboard is in the editor or in one of its popups.</summary>
    public bool HasFocus =>
        _editor.IsKeyboardFocusWithin
        || TopLevel.GetTopLevel(_editor)?.FocusManager?.GetFocusedElement() is Visual focused
            && focused.GetSelfAndVisualAncestors().Any(visual => visual.GetValue(s_ownerProperty) == _editor);

    public void Dispose()
    {
        _window?.RemoveHandler(InputElement.LostFocusEvent, OnLostFocus);
        _window = null;
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e) =>
        _editor.Dispatcher.Post(() =>
        {
            if (_window is not null && !HasFocus)
                _focusLeft();
        });
}

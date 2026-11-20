using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The hover's popup, as VS Code's: in the color theme's hover colors, with selectable text, as large as its content up
/// to two thirds of the editor's width and a quarter of its height, or 750 by 250 pixels where that is more, and up to
/// the size the user last resized a hover to; beyond, it scrolls. Its edges away from the word resize it.
/// </summary>
/// <remarks>
/// <para>
/// Clicked into, as to select its text, or with the keyboard put into it, it is held: it stays until Escape, a click
/// outside it or the window losing the focus, which it watches meanwhile, as in VS Code.
/// </para>
/// <para>
/// With the keyboard in it, the arrows, Page Up, Page Down, Home and End scroll it, Escape ends the hover, and typed
/// text goes into the editor's text, as typing ends the hover. Hiding it gives the keyboard back to the text.
/// </para>
/// </remarks>
internal sealed class HoverPopup
{
    /// <summary>The narrowest the hover's most width gets, VS Code's: wider in wider editors.</summary>
    private const double MinMaxWidth = 750;

    /// <summary>The lowest the hover's most height gets, VS Code's: higher in higher editors.</summary>
    private const double MinMaxHeight = 250;

    /// <summary>The narrowest the user resizes the hover to, VS Code's.</summary>
    private const double MinResizedWidth = 150;

    private readonly TemplatedControl _editor;
    private readonly TextArea _textArea;
    private readonly TextChanges _changes;
    private readonly TextView _textView;
    private readonly Action _close;
    private readonly PopupScroller _content = new();
    private readonly EditorPopup _popup;

    /// <summary>The word the popup is next to.</summary>
    private TextRange _word;

    /// <summary>Whether the user resized the hover that shows.</summary>
    private bool _resized;

    /// <summary>The window whose clicks and deactivation a held popup watches, as they end it; null otherwise.</summary>
    private TopLevel? _watched;

    /// <param name="editor">The editor whose hover it is.</param>
    /// <param name="textArea">The text, which the keyboard goes back to.</param>
    /// <param name="changes">Tells whether the text area still shows the view's document.</param>
    /// <param name="close">Ends the hover, as Escape and typing in the popup do.</param>
    public HoverPopup(TemplatedControl editor, TextArea textArea, TextChanges changes, Action close)
    {
        _editor = editor;
        _textArea = textArea;
        _changes = changes;
        _textView = textArea.TextView;
        _close = close;
        _popup = new EditorPopup(editor, _content, "Hover") { IsResizable = true };
        // VS Code's padding.
        _popup.Frame.Padding = new Thickness(8, 4);
        _popup.Resized += OnResized;
        _popup.Root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _popup.Root.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        // Also when its text handles the press, to select; not on an edge, which resizes it.
        _popup.Root.AddHandler(
            InputElement.PointerPressedEvent,
            (_, _) =>
            {
                if (!IsDragging)
                    Hold();
            },
            handledEventsToo: true
        );
    }

    /// <summary>
    /// The size the user resized a hover to last, for the app's run, as VS Code keeps it: later hovers grow up to it.
    /// Empty before.
    /// </summary>
    internal static Size LastResizedSize { get; set; }

    /// <summary>The popup's outermost control, for the pointer over it.</summary>
    public Control Root => _popup.Root;

    public bool IsOpen => _popup.IsOpen;

    /// <summary>Whether the user drags an edge.</summary>
    public bool IsDragging => _popup.Frame.IsDragging;

    /// <summary>Whether the keyboard is in the popup.</summary>
    public bool HasKeyboard => _popup.Root.IsKeyboardFocusWithin;

    /// <summary>Whether the user clicked into the popup, or put the keyboard into it: it stays wherever the pointer goes.</summary>
    public bool IsHeld => _watched is not null;

    /// <summary>Starts a new hover: the popup takes the size of its content, within its limits.</summary>
    public void Reset()
    {
        _popup.Frame.Width = double.NaN;
        _resized = false;
    }

    /// <summary>
    /// Shows <paramref name="blocks"/> in <paramref name="colors"/> above <paramref name="word"/>, where the pointer
    /// comes from, or below it where only that side has room; the keyboard stays in the popup if it was.
    /// </summary>
    public void Show(IEnumerable<HoverBlock> blocks, PopupColors colors, TextRange word)
    {
        _popup.Frame.Paint(
            colors.HoverBackground,
            colors.HoverBorder,
            colors.HoverForeground,
            colors.Shadow,
            colors.FocusBorder
        );
        _content.SliderBrushes = colors.Slider;
        if (!_resized)
        {
            Size editor = _editor.Bounds.Size;
            _popup.Frame.MaxWidth = Math.Max(
                Math.Max(MinMaxWidth, Math.Floor(editor.Width * 0.66)),
                LastResizedSize.Width
            );
            _popup.Frame.MaxHeight = Math.Max(
                Math.Max(MinMaxHeight, Math.Floor(editor.Height / 4)),
                LastResizedSize.Height
            );
        }
        bool hadKeyboard = HasKeyboard;
        _content.Child = HoverView.Create(
            blocks,
            PopupLook.Of(_editor, colors, colors.HoverForeground) with
            {
                Selectable = true,
            }
        );
        // The text that had the keyboard went with the old content: the scroller keeps it.
        if (hadKeyboard)
            TakeKeyboard();
        _word = word;
        Place();
    }

    /// <summary>Puts the keyboard into the popup, where its keys scroll it.</summary>
    public void TakeKeyboard()
    {
        _content.Focusable = true;
        _content.Focus(NavigationMethod.Tab);
    }

    /// <summary>Holds the popup: it stays until Escape, a click outside it or the window losing the focus.</summary>
    public void Hold()
    {
        if (_watched is not null || TopLevel.GetTopLevel(_editor) is not { } window)
            return;
        _watched = window;
        window.AddHandler(InputElement.PointerPressedEvent, OnPressedInWindow, RoutingStrategies.Tunnel, true);
        if (window is WindowBase deactivates)
            deactivates.Deactivated += OnDeactivated;
    }

    /// <summary>Closes the popup; the keyboard goes back to the text if it was in the popup.</summary>
    public void Hide()
    {
        if (_watched is { } window)
        {
            _watched = null;
            window.RemoveHandler(InputElement.PointerPressedEvent, OnPressedInWindow);
            if (window is WindowBase deactivates)
                deactivates.Deactivated -= OnDeactivated;
        }
        if (HasKeyboard)
            _textArea.Focus();
        _content.Focusable = false;
        _popup.Hide();
    }

    private void Place() => _popup.Show(_textView, () => TextViewBoxes.CharacterAt(_textView, _word.Start));

    /// <summary>
    /// The user drags an edge: the popup takes the size dragged to, at least VS Code's narrowest and a line high, and
    /// later hovers grow up to it.
    /// </summary>
    private void OnResized(Size size, Edges edges)
    {
        _resized = true;
        PopupFrame frame = _popup.Frame;
        double minHeight = _textView.DefaultLineHeight + frame.Padding.Top + frame.Padding.Bottom;
        if ((edges & (Edges.Left | Edges.Right)) != 0)
            frame.Width = frame.MaxWidth = Math.Max(MinResizedWidth, size.Width);
        if ((edges & (Edges.Top | Edges.Bottom)) != 0)
            frame.MaxHeight = Math.Max(minHeight, size.Height);
        LastResizedSize = new Size(
            double.IsNaN(frame.Width) ? frame.Bounds.Width : frame.Width,
            Math.Max(minHeight, size.Height)
        );
        Place();
    }

    /// <summary>
    /// A click outside the held popup ends the hover, once the click is done: what it clicked takes the keyboard, or the
    /// text gets it back from the popup.
    /// </summary>
    private void OnPressedInWindow(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && _popup.Root.IsVisualAncestorOf(source))
            return;
        TopLevel? window = _watched;
        _editor.Dispatcher.Post(() =>
        {
            if (_watched == window)
                _close();
        });
    }

    private void OnDeactivated(object? sender, EventArgs e) => _close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None)
            return;
        _close();
        e.Handled = true;
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        e.Handled = true;
        _close();
        // The text taking the keyboard back may have shown another document, which the text was not typed into.
        if (!_changes.IsShown)
            return;
        _textArea.RaiseEvent(
            new TextInputEventArgs
            {
                RoutedEvent = InputElement.TextInputEvent,
                Source = _textArea,
                Text = e.Text,
            }
        );
    }
}

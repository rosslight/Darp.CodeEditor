using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The hover's popup, as VS Code's: in the color theme's hover colors, with selectable text, as large as its content up
/// to two thirds of the editor's width and a quarter of its height, or 750 by 250 pixels where that is more; beyond, it
/// scrolls. Its edges away from the word resize it.
/// </summary>
/// <remarks>
/// Clicked into, or with the keyboard put into it, it is held: it stays until Escape or a click outside it. With the
/// keyboard in it, the arrows, Page Up, Page Down, Home and End scroll it, and Escape ends the hover. It is outside the
/// editor's key route, so the editor's keys don't run there. Hiding it gives the keyboard back to the text.
/// </remarks>
internal sealed class HoverPopup
{
    // VS Code's: the least of the hover's most width and height, which grow with the editor, and its least width.
    private const double MinMaxWidth = 750;
    private const double MinMaxHeight = 250;
    private const double MinResizedWidth = 150;

    private readonly CodeEditor _editor;
    private readonly TextArea _textArea;
    private readonly TextView _textView;
    private readonly Action _close;
    private readonly PopupScrollViewer _content = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
    private readonly PopupFrame _frame;
    private readonly EditorPopup _popup;

    /// <summary>The blocks that show, which the server's hover goes below; null while nothing shows.</summary>
    private PopupStack? _shown;

    private TextRange _word;
    private bool _resized;

    /// <param name="editor">The editor whose hover it is.</param>
    /// <param name="textArea">The text, which the keyboard goes back to.</param>
    /// <param name="close">Ends the hover, as Escape in the popup does.</param>
    public HoverPopup(CodeEditor editor, TextArea textArea, Action close)
    {
        _editor = editor;
        _textArea = textArea;
        _textView = textArea.TextView;
        _close = close;
        _frame = new PopupFrame
        {
            Name = "Hover",
            Child = _content,
            Padding = new Thickness(8, 4),
        };
        _popup = new EditorPopup(editor, _frame) { IsResizable = true };
        _popup.Resized += OnResized;
        _popup.LeftView += (_, _) => _close();
        _popup.Root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>The popup's outermost control, for the pointer over it.</summary>
    public Control Root => _popup.Root;

    public bool IsOpen => _popup.IsOpen;

    /// <summary>Whether the user clicked into the popup, or put the keyboard into it: it stays wherever the pointer goes.</summary>
    public bool IsHeld { get; private set; }

    public void Paint(PopupColors colors) =>
        _frame.Paint(colors.HoverBackground, colors.HoverBorder, colors.HoverForeground, colors);

    /// <summary>Shows <paramref name="blocks"/> above <paramref name="word"/>, within the hover's limits.</summary>
    public void Show(IReadOnlyList<HoverBlock> blocks, CodeLook look, TextRange word)
    {
        if (!_resized)
        {
            _frame.MaxWidth = Math.Max(MinMaxWidth, Math.Floor(_editor.Bounds.Width * 0.66));
            _frame.MaxHeight = Math.Max(MinMaxHeight, Math.Floor(_editor.Bounds.Height / 4));
        }
        _shown = HoverView.Create(blocks, look, look.Colors.HoverBorder, selectable: true);
        _content.Content = _shown;
        _word = word;
        Place();
    }

    /// <summary>
    /// Adds <paramref name="blocks"/> below what shows: what shows keeps its selection, the popup its scroll position.
    /// </summary>
    public void Append(IReadOnlyList<HoverBlock> blocks, CodeLook look)
    {
        if (_shown is not { } shown)
            return;
        HoverView.Append(shown, blocks, look, look.Colors.HoverBorder, selectable: true);
        Place();
    }

    /// <summary>Holds the popup, and puts the keyboard into it.</summary>
    public void TakeKeyboard()
    {
        Hold();
        _content.Focusable = true;
        _content.Focus();
    }

    /// <summary>Holds the popup: it stays until Escape or a click outside it.</summary>
    public void Hold() => IsHeld = true;

    /// <summary>Closes the popup; the keyboard goes back to the text if it was in the popup.</summary>
    public void Hide()
    {
        if (_popup.Root.IsKeyboardFocusWithin)
            _textArea.Focus();
        _content.Focusable = false;
        IsHeld = false;
        _shown = null;
        _content.Content = null;
        _popup.Hide();
        _popup.Left = null;
        _resized = false;
        (_frame.Width, _frame.Height) = (double.NaN, double.NaN);
    }

    private void Place() => _popup.Show(_textView, () => TextViewBoxes.CharacterAt(_textView, _word.Start));

    /// <summary>
    /// The user drags an edge: the popup takes the size dragged to, at least VS Code's narrowest and a line high. The
    /// edge across from the one dragged stays where it shows, also where the window's edge moved the popup.
    /// </summary>
    private void OnResized(Size size, Edges edges)
    {
        _resized = true;
        _popup.Left ??= _popup.Placed?.Bounds.Left;
        if ((edges & (Edges.Left | Edges.Right)) != 0)
            _frame.Width = _frame.MaxWidth = Math.Max(MinResizedWidth, size.Width);
        if ((edges & (Edges.Top | Edges.Bottom)) != 0)
        {
            double line = _textView.DefaultLineHeight + _frame.Padding.Top + _frame.Padding.Bottom;
            _frame.Height = _frame.MaxHeight = Math.Max(line, size.Height);
        }
        Place();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
            return;
        switch (e.Key)
        {
            case Key.Escape:
                _close();
                break;
            case Key.Up:
                _content.LineUp();
                break;
            case Key.Down:
                _content.LineDown();
                break;
            case Key.PageUp:
                _content.PageUp();
                break;
            case Key.PageDown:
                _content.PageDown();
                break;
            case Key.Home:
                _content.ScrollToHome();
                break;
            case Key.End:
                _content.ScrollToEnd();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// Content next to a place in an editor's text, in the window's overlay layer: it stays inside the window, and opens
/// without an OS window. It takes the editor's theme variant, so that a <c>ThemeVariantScope</c> around the editor
/// reaches it, and the app's font where the editor is, not the editor's monospace one.
/// </summary>
/// <remarks>
/// <para>
/// It opens above the place's line, or below it when only that side has room; a popup made for below, such as a list
/// of choices, opens below, or above when only that side has room. When neither side has room, it takes the larger
/// side and is limited to its height. Above the line it keeps its bottom edge there, so it stays next to the line when
/// its content grows. It moves left at the window's right edge.
/// </para>
/// <para>
/// While open, it is placed again after each layout pass in which the window or its place moved, as when the window is
/// resized or the text reflows, and it closes when its place's line leaves the view it is in, which
/// <see cref="LeftView"/> reports.
/// </para>
/// <para>
/// Its text can take the keyboard, to select and copy. Meanwhile the editor counts it as its own: the editor's other
/// popups stay. Escape gives the keyboard back to the editor, which <see cref="Escaped"/> reports, and typed text goes
/// back into the editor's text.
/// </para>
/// </remarks>
internal sealed class EditorPopup
{
    /// <summary>The editor that a popup's root belongs to.</summary>
    private static readonly AttachedProperty<CodeEditor?> s_ownerProperty = AvaloniaProperty.RegisterAttached<
        EditorPopup,
        Visual,
        CodeEditor?
    >("Owner");

    private readonly CodeEditor _editor;
    private readonly ThemeVariantScope _root;
    private readonly bool _below;
    private OverlayLayer? _layer;

    /// <summary>The bindings to the editor's theme variant and to the font around it, while open.</summary>
    private IDisposable[] _bindings = [];

    /// <summary>The visual of the place, and the place: a character's box in it, read again at each placing.</summary>
    private (Visual RelativeTo, Func<Rect> Anchor)? _place;

    /// <summary>The window's size and the place's line in it at the last placing; null to place again.</summary>
    private (Size Space, Point Top, Point Bottom)? _placed;

    /// <param name="editor">The editor whose text the popup is next to.</param>
    /// <param name="content">What the popup shows.</param>
    /// <param name="below">Whether it opens below its place's line when both sides have room.</param>
    public EditorPopup(CodeEditor editor, Control content, bool below = false)
    {
        _editor = editor;
        _root = new ThemeVariantScope { Child = content };
        _root.SetValue(s_ownerProperty, editor);
        _root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _root.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        _root.LostFocus += (_, _) => editor.CheckFocus();
        _below = below;
    }

    /// <summary>Raised when the popup closed because its place's line left the view.</summary>
    public event EventHandler? LeftView;

    /// <summary>Raised when Escape gave the keyboard back to the editor from the popup.</summary>
    public event EventHandler? Escaped;

    /// <summary>The editor whose popup <paramref name="visual"/> is in; null outside the editors' popups.</summary>
    public static CodeEditor? OwnerOf(Visual visual) =>
        visual.GetSelfAndVisualAncestors().Select(v => v.GetValue(s_ownerProperty)).FirstOrDefault(o => o is not null);

    /// <summary>The popup's outermost control, for the pointer over it.</summary>
    public Control Root => _root;

    public bool IsOpen => _layer is not null;

    /// <summary>
    /// Opens the popup next to <paramref name="anchor"/>, a character's box in <paramref name="relativeTo"/>, or moves
    /// it there; also after its content changed.
    /// </summary>
    public void Show(Visual relativeTo, Func<Rect> anchor)
    {
        _place = (relativeTo, anchor);
        _placed = null;
        if (_layer is null)
        {
            if (OverlayLayer.GetOverlayLayer(_editor) is not { } layer)
                return;
            _layer = layer;
            var bindings = new List<IDisposable>
            {
                _root.Bind(
                    ThemeVariantScope.RequestedThemeVariantProperty,
                    _editor.GetObservable(ThemeVariantScope.ActualThemeVariantProperty)
                ),
            };
            if (_editor.GetVisualParent() is AvaloniaObject around)
            {
                bindings.Add(
                    _root.Bind(TextElement.FontFamilyProperty, around.GetObservable(TextElement.FontFamilyProperty))
                );
                bindings.Add(
                    _root.Bind(TextElement.FontSizeProperty, around.GetObservable(TextElement.FontSizeProperty))
                );
            }
            _bindings = [.. bindings];
            layer.Children.Add(_root);
            layer.LayoutUpdated += OnLayoutUpdated;
        }
        Place();
    }

    public void Hide()
    {
        if (_layer is not { } layer)
            return;
        _layer = null;
        _place = null;
        layer.LayoutUpdated -= OnLayoutUpdated;
        layer.Children.Remove(_root);
        foreach (IDisposable binding in _bindings)
            binding.Dispose();
        _bindings = [];
    }

    /// <summary>Whether the popup or something in it has the keyboard.</summary>
    public bool IsFocusWithin => _root.IsKeyboardFocusWithin;

    /// <summary>Puts the keyboard into the popup, for scrolling it with the keys.</summary>
    public void Focus(InputElement target) => target.Focus(NavigationMethod.Tab);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None)
            return;
        e.Handled = true;
        _editor.FocusText();
        Escaped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Typed text goes into the editor's text, with the keyboard.</summary>
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: > 0 } text)
            return;
        e.Handled = true;
        _editor.TypeText(text);
    }

    /// <summary>A layout pass ended: the window or the place may have moved.</summary>
    private void OnLayoutUpdated(object? sender, EventArgs e) => Place();

    /// <summary>
    /// Places the popup next to its place as the window shows it now, unless neither moved since the last placing;
    /// closes it when the place's line is out of its view.
    /// </summary>
    private void Place()
    {
        if (_layer is not { } layer || _place is not var (relativeTo, getAnchor))
            return;
        Rect anchor = getAnchor();
        if (
            anchor.Bottom <= 0
            || anchor.Top >= relativeTo.Bounds.Height
            || relativeTo.TranslatePoint(anchor.TopLeft, layer) is not { } top
            || relativeTo.TranslatePoint(anchor.BottomLeft, layer) is not { } bottom
        )
        {
            Hide();
            LeftView?.Invoke(this, EventArgs.Empty);
            return;
        }
        Size space = layer.Bounds.Size;
        // Placing changes the layout, and the next layout pass finds nothing moved.
        if (_placed == (space, top, bottom))
            return;
        _placed = (space, top, bottom);
        _root.MaxHeight = double.PositiveInfinity;
        _root.Measure(new Size(space.Width, double.PositiveInfinity));
        Size size = _root.DesiredSize;
        double above = top.Y;
        double below = space.Height - bottom.Y;
        bool placeAbove = _below
            ? size.Height > below && above > below
            : size.Height <= above || (size.Height > below && above >= below);
        // In whole pixels, so that rounding the layout cannot take it past the window's edge.
        _root.MaxHeight = Math.Max(0, Math.Floor(placeAbove ? above : below));
        Canvas.SetLeft(_root, Math.Clamp(top.X, 0, Math.Max(0, space.Width - size.Width)));
        if (placeAbove)
        {
            _root.ClearValue(Canvas.TopProperty);
            Canvas.SetBottom(_root, space.Height - top.Y);
        }
        else
        {
            _root.ClearValue(Canvas.BottomProperty);
            Canvas.SetTop(_root, bottom.Y);
        }
    }
}

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

    /// <summary>The narrowest a popup beside another gets; with less room, it goes below or above it.</summary>
    private const double MinBesideWidth = 200;

    private readonly CodeEditor _editor;
    private readonly ThemeVariantScope _root;
    private readonly bool _below;
    private OverlayLayer? _layer;

    /// <summary>The bindings to the editor's theme variant and to the font around it, while open.</summary>
    private IDisposable[] _bindings = [];

    /// <summary>The visual of the place, and the place: a character's box in it, read again at each placing.</summary>
    private (Visual RelativeTo, Func<Rect> Anchor)? _place;

    /// <summary>The popup this one is beside, and the width it would like; null while it is next to a line.</summary>
    private (EditorPopup Other, double Width)? _beside;

    /// <summary>What the last placing went by; null to place again.</summary>
    private object? _placed;

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
    /// How far right of its place the popup's left edge goes; negative moves it left, as completion's list does to
    /// line its labels up with the word.
    /// </summary>
    public double OffsetX { get; set; }

    /// <summary>
    /// Whether the popup stays within the editor's bounds where it fits there, rather than reaching over the app's
    /// controls around the editor, as the hover does.
    /// </summary>
    public bool PrefersEditorBounds { get; set; }

    /// <summary>Where the popup was placed last, in the overlay layer, and whether above its line; null while closed.</summary>
    public (Rect Bounds, bool Above)? LastPlacement { get; private set; }

    /// <summary>Whether the popup or something in it has the keyboard.</summary>
    public bool IsFocusWithin => _root.IsKeyboardFocusWithin;

    /// <summary>
    /// Opens the popup next to <paramref name="anchor"/>, a character's box in <paramref name="relativeTo"/>, or moves
    /// it there; also after its content changed.
    /// </summary>
    public void Show(Visual relativeTo, Func<Rect> anchor)
    {
        _place = (relativeTo, anchor);
        _placed = null;
        if (Open())
            Place();
    }

    /// <summary>
    /// Opens the popup beside <paramref name="other"/>, as completion's details beside its list, <paramref name="width"/>
    /// wide where there is room: right of it, else left of it, else narrower on the side with more room, else below or
    /// above it, away from its line. Beside it, its top edge goes with the other's top below the line, and its bottom
    /// edge with the other's bottom above the line, so that neither moves away from the line. It follows the other.
    /// </summary>
    public void ShowBeside(EditorPopup other, double width)
    {
        _beside = (other, width);
        _placed = null;
        if (Open())
            PlaceBeside();
    }

    public void Hide()
    {
        _beside = null;
        if (_layer is not { } layer)
            return;
        _layer = null;
        _place = null;
        LastPlacement = null;
        layer.LayoutUpdated -= OnLayoutUpdated;
        layer.Children.Remove(_root);
        foreach (IDisposable binding in _bindings)
            binding.Dispose();
        _bindings = [];
    }

    /// <summary>Puts the keyboard into the popup, for scrolling it with the keys.</summary>
    public void Focus(InputElement target) => target.Focus(NavigationMethod.Tab);

    /// <summary>Adds the root to the overlay layer, unless it is there.</summary>
    /// <returns>Whether the popup is in the layer.</returns>
    private bool Open()
    {
        if (_layer is not null)
            return true;
        if (OverlayLayer.GetOverlayLayer(_editor) is not { } layer)
            return false;
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
            bindings.Add(_root.Bind(TextElement.FontSizeProperty, around.GetObservable(TextElement.FontSizeProperty)));
        }
        _bindings = [.. bindings];
        layer.Children.Add(_root);
        layer.LayoutUpdated += OnLayoutUpdated;
        return true;
    }

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
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_beside is not null)
            PlaceBeside();
        else
            Place();
    }

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
        Rect editor =
            PrefersEditorBounds && _editor.TranslatePoint(default, layer) is { } at
                ? new Rect(at, _editor.Bounds.Size).Intersect(new Rect(space))
                : new Rect(space);
        // Placing changes the layout, and the next layout pass finds nothing moved.
        var placed = (space, top, bottom, editor);
        if (Equals(_placed, placed))
            return;
        _placed = placed;
        _root.MaxHeight = double.PositiveInfinity;
        _root.Measure(new Size(space.Width, double.PositiveInfinity));
        Size size = _root.DesiredSize;
        // Within the editor where it fits above or below the line there, else within the window.
        Rect area = editor;
        if (size.Height > top.Y - editor.Top && size.Height > editor.Bottom - bottom.Y)
            area = new Rect(space);
        double above = top.Y - area.Top;
        double below = area.Bottom - bottom.Y;
        bool placeAbove = _below
            ? size.Height > below && above > below
            : size.Height <= above || (size.Height > below && above >= below);
        // In whole pixels, so that rounding the layout cannot take it past the window's edge.
        double maxHeight = Math.Max(0, Math.Floor(placeAbove ? above : below));
        _root.MaxHeight = maxHeight;
        double width = Math.Min(size.Width, space.Width);
        double left = Math.Clamp(
            top.X + OffsetX,
            area.Width >= width ? area.Left : 0,
            Math.Max(0, (area.Width >= width ? area.Right : space.Width) - width)
        );
        Canvas.SetLeft(_root, left);
        double height = Math.Min(size.Height, maxHeight);
        if (placeAbove)
        {
            _root.ClearValue(Canvas.TopProperty);
            Canvas.SetBottom(_root, space.Height - top.Y);
            LastPlacement = (new Rect(left, top.Y - height, width, height), true);
        }
        else
        {
            _root.ClearValue(Canvas.BottomProperty);
            Canvas.SetTop(_root, bottom.Y);
            LastPlacement = (new Rect(left, bottom.Y, width, height), false);
        }
    }

    /// <summary>
    /// Places the popup beside the other one, as <see cref="ShowBeside"/> says, by where the other is laid out now,
    /// unless nothing moved since the last placing.
    /// </summary>
    private void PlaceBeside()
    {
        if (_layer is not { } layer || _beside is not var (other, preferred) || _root.Child is not { } content)
            return;
        if (other.LastPlacement is not var (_, above) || other._root.Bounds is not { Width: > 0 } list)
        {
            _root.IsVisible = false;
            return;
        }
        Size space = layer.Bounds.Size;
        // Placing changes the layout, and the next layout pass finds nothing moved.
        var placed = (space, list, above, preferred);
        if (Equals(_placed, placed))
            return;
        _placed = placed;
        double right = space.Width - list.Right;
        double left = list.Left;
        double width = preferred;
        double x;
        bool stacked = false;
        if (right >= width)
            x = list.Right;
        else if (left >= width)
            x = list.Left - width;
        else if (Math.Max(right, left) >= MinBesideWidth)
        {
            width = Math.Floor(Math.Max(right, left));
            x = right >= left ? list.Right : 0;
        }
        else
        {
            stacked = true;
            width = Math.Min(width, space.Width);
            x = Math.Clamp(list.Left, 0, Math.Max(0, space.Width - width));
        }
        content.Measure(new Size(width, double.PositiveInfinity));
        double height = content.DesiredSize.Height;
        double y;
        if (stacked)
        {
            double room = above ? list.Top : space.Height - list.Bottom;
            height = Math.Min(height, Math.Max(0, Math.Floor(room)));
            y = above ? list.Top - height : list.Bottom;
        }
        else
        {
            height = Math.Min(height, space.Height);
            y = Math.Clamp(above ? list.Bottom - height : list.Top, 0, Math.Max(0, space.Height - height));
        }
        _root.IsVisible = true;
        _root.Width = width;
        _root.MaxHeight = height;
        Canvas.SetLeft(_root, x);
        Canvas.SetTop(_root, y);
    }
}

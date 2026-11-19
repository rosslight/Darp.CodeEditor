using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// What completion shows: the items in a list below the word, each with its icon and its label in the editor's font,
/// the characters that matched in bold and deprecated items struck through; and in a popup of its own the selected
/// item's details, beside the list, as <see cref="PopupPlacement.Beside"/> places them. Both are in the color theme's
/// suggest colors. Neither takes the keyboard focus, so the editor keeps it; clicking an item commits it.
/// </summary>
internal sealed class CompletionView
{
    /// <summary>How many items show at once, as in VS Code.</summary>
    public const int VisibleItems = 12;

    private const double ListWidth = 300;
    private const double DetailsWidth = 360;

    /// <summary>
    /// The most of a label that a row shows: more than any list is wide, so that the row still trims it with "…", while
    /// a server's long label costs the UI thread no more.
    /// </summary>
    private const int MaxLabelLength = 256;

    private readonly ListBox _list;
    private readonly PopupScroller _details = new();
    private readonly EditorPopup _listPopup;
    private readonly EditorPopup _detailsPopup;

    private PopupLook? _look;
    private SymbolIconColors? _icons;
    private double _rowHeight;

    public CompletionView(TemplatedControl editor)
    {
        _list = new ListBox
        {
            Focusable = false,
            Width = ListWidth,
            Background = Brushes.Transparent,
            // Each row is reused for the items it shows as the list changes: only its icon and text change.
            ItemTemplate = new FuncDataTemplate<ShownItem>((_, _) => new Row(this), supportsRecycling: true),
        };
        // Rows as dense as the editor's lines, as in VS Code, in the app's look of list items otherwise.
        _list.Styles.Add(
            new Style(x => x.OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 0)),
                    new Setter(Layoutable.MinHeightProperty, 0.0),
                },
            }
        );
        _list.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        _detailsPopup = new EditorPopup(editor, _details, "CompletionDetails");
        _detailsPopup.Frame.Padding = new Thickness(6, 4, 2, 4);
        _listPopup = new EditorPopup(editor, _list, "CompletionList", PopupSide.Below);
        _listPopup.LeftView += (_, _) =>
        {
            _detailsPopup.Hide();
            LeftView?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Raised when an item is clicked, with its index.</summary>
    public event Action<int>? Clicked;

    /// <summary>Raised when the list closed because its line left the view.</summary>
    public event EventHandler? LeftView;

    /// <summary>Shows <paramref name="items"/>, with <paramref name="selected"/> selected and in view.</summary>
    /// <param name="items">The items.</param>
    /// <param name="selected">The selected item's index.</param>
    /// <param name="look">The editor's font, for the labels, and the popups' colors.</param>
    /// <param name="icons">The icons' colors; none while the color theme loads.</param>
    /// <param name="rowHeight">The height of an item.</param>
    public void Show(
        IReadOnlyList<ShownItem> items,
        int selected,
        PopupLook look,
        SymbolIconColors? icons,
        double rowHeight
    )
    {
        (_look, _icons, _rowHeight) = (look, icons, rowHeight);
        PopupColors colors = look.Colors;
        _listPopup.Frame.Paint(colors.SuggestBackground, colors.SuggestBorder, colors.SuggestForeground, colors.Shadow);
        _detailsPopup.Frame.Paint(
            colors.SuggestBackground,
            colors.SuggestBorder,
            colors.WidgetForeground,
            colors.Shadow
        );
        _details.SliderBrushes = colors.Slider;
        _list.MaxHeight = rowHeight * VisibleItems;
        _details.MaxHeight = rowHeight * VisibleItems;
        _list.ItemsSource = items;
        Select(selected);
    }

    /// <summary>Opens the list at <paramref name="place"/>, the box of the word's first character, or moves it there.</summary>
    public void Open(Visual relativeTo, Func<Rect> place)
    {
        _listPopup.Show(relativeTo, place);
        if (_details.Child is not null)
            _detailsPopup.ShowBeside(_listPopup, DetailsWidth);
    }

    public void Hide()
    {
        _detailsPopup.Hide();
        _listPopup.Hide();
    }

    /// <summary>Selects the item at <paramref name="index"/>, and scrolls it into view.</summary>
    public void Select(int index)
    {
        _list.SelectedIndex = index;
        _list.ScrollIntoView(index);
    }

    /// <summary>Shows the selected item's details beside the list; null hides them.</summary>
    public void ShowDetails(Control? details)
    {
        _details.Child = details;
        if (details is null)
            _detailsPopup.Hide();
        else if (_listPopup.IsOpen)
            _detailsPopup.ShowBeside(_listPopup, DetailsWidth);
    }

    /// <summary>
    /// <paramref name="label"/>, or its start with "…" when it is longer than a row shows; a cut never splits a surrogate
    /// pair. Matched characters are all within the start: matching reads 128 characters.
    /// </summary>
    private static string Shortened(string label)
    {
        if (label.Length <= MaxLabelLength)
            return label;
        int end = char.IsHighSurrogate(label[MaxLabelLength - 1]) ? MaxLabelLength - 1 : MaxLabelLength;
        return string.Concat(label.AsSpan(0, end), "…");
    }

    /// <summary>The label, with the characters at <paramref name="highlights"/> in bold.</summary>
    private static InlineCollection Label(string label, int[] highlights)
    {
        var inlines = new InlineCollection();
        int next = 0;
        for (int start = 0; start < label.Length; )
        {
            int end = start;
            bool bold = next < highlights.Length && highlights[next] == start;
            if (bold)
            {
                while (next < highlights.Length && highlights[next] == end)
                    (next, end) = (next + 1, end + 1);
            }
            else
            {
                end = next < highlights.Length ? Math.Min(highlights[next], label.Length) : label.Length;
            }
            inlines.Add(new Run(label[start..end]) { FontWeight = bold ? FontWeight.Bold : FontWeight.Normal });
            start = end;
        }
        return inlines;
    }

    /// <summary>
    /// A row of the list: the item's icon, and its label in the editor's font with the characters that matched in bold,
    /// struck through when it is deprecated. The list reuses it for other items.
    /// </summary>
    private sealed class Row : DockPanel
    {
        private readonly CompletionView _view;

        private readonly CompletionIcon _icon = new()
        {
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        private readonly TextBlock _label = new()
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        public Row(CompletionView view)
        {
            _view = view;
            SetDock(_icon, Dock.Left);
            Children.Add(_icon);
            Children.Add(_label);
        }

        /// <summary>The list gives the row another item.</summary>
        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is not ShownItem { Item: var item } shown)
                return;
            Height = _view._rowHeight;
            _label.FontFamily = _view._look?.CodeFont ?? FontFamily.Default;
            _label.FontSize = _view._look?.CodeSize ?? 12;
            _label.Inlines = Label(Shortened(item.Label), shown.Highlights);
            _label.TextDecorations = item.IsDeprecated ? TextDecorations.Strikethrough : null;
            _icon.IsVisible = _view._icons is not null;
            if (_view._icons is { } icons)
                _icon.Show(item.Kind, icons.Of(item.Kind));
        }
    }

    /// <summary>
    /// A click on an item commits it. The list does not handle it, so that it takes no focus: the editor keeps the
    /// keyboard.
    /// </summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed)
            return;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } container)
        {
            e.Handled = true;
            Clicked?.Invoke(_list.IndexFromContainer(container));
        }
    }
}

/// <summary>An item that shows: the item, and the characters of its label that matched.</summary>
internal sealed record ShownItem(CompletionItem Item, int[] Highlights);

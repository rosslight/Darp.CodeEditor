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
/// What the completion popup shows: the items in a list in the app's flyout look, each with its icon and its label in
/// the editor's font, the characters that matched in bold and deprecated items struck through; and in a flyout of its
/// own the selected item's details, beside the list where the window has room for them, narrower if need be, and below
/// it otherwise, as VS Code places them. Neither takes the keyboard focus, so the editor keeps it; clicking an item
/// commits it.
/// </summary>
internal sealed class CompletionView
{
    /// <summary>How many items show at once, as in VS Code.</summary>
    public const int VisibleItems = 12;

    private const double ListWidth = 300;
    private const double DetailsWidth = 360;

    /// <summary>The narrowest the details get beside the list; with less room, they go below it.</summary>
    private const double MinDetailsWidth = 200;

    private const double Gap = 2;

    private readonly ListBox _list;
    private readonly FlyoutPresenter _details;
    private readonly Layout _layout;

    private readonly ScrollViewer _detailsContent = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        MaxWidth = DetailsWidth,
    };

    private CodeLook? _look;
    private SymbolIconColors? _icons;
    private double _rowHeight;

    public CompletionView()
    {
        _list = new ListBox
        {
            Focusable = false,
            Width = ListWidth,
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
        _details = new FlyoutPresenter { Content = _detailsContent, IsVisible = false };
        var listFlyout = new FlyoutPresenter { Content = _list };
        // A theme's flyout may scroll across, as Fluent's does: its content then takes no width from it, and the
        // details would not wrap.
        ScrollViewer.SetHorizontalScrollBarVisibility(listFlyout, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_details, ScrollBarVisibility.Disabled);
        _layout = new Layout(listFlyout, _details);
    }

    /// <summary>The view's outermost control.</summary>
    public Control Root => _layout;

    /// <summary>
    /// The width from where the popup starts to the window's right edge, as it is placed now: the room for the list and
    /// the details beside it. By default, the width the popup is measured with.
    /// </summary>
    public Func<double> Room
    {
        set => _layout.Room = value;
    }

    /// <summary>Raised when an item is clicked, with its index.</summary>
    public event Action<int>? Clicked;

    /// <summary>Shows <paramref name="items"/>, with <paramref name="selected"/> selected and in view.</summary>
    /// <param name="items">The items.</param>
    /// <param name="selected">The selected item's index.</param>
    /// <param name="look">The editor's font, for the labels.</param>
    /// <param name="icons">The icons' colors; none while the color theme loads.</param>
    /// <param name="rowHeight">The height of an item.</param>
    public void Show(
        IReadOnlyList<ShownItem> items,
        int selected,
        CodeLook look,
        SymbolIconColors? icons,
        double rowHeight
    )
    {
        (_look, _icons, _rowHeight) = (look, icons, rowHeight);
        _list.MaxHeight = rowHeight * VisibleItems;
        _detailsContent.MaxHeight = rowHeight * VisibleItems;
        _list.ItemsSource = items;
        Select(selected);
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
        _detailsContent.Content = details;
        _details.IsVisible = details is not null;
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
    /// Places the details beside the list when the room right of where the popup starts takes them at least
    /// <see cref="MinDetailsWidth"/> wide, narrowed to that room; otherwise below the list, at least as wide as it.
    /// </summary>
    private sealed class Layout : Panel
    {
        private readonly Control _list;
        private readonly Control _details;
        private bool _beside;

        public Layout(Control list, Control details)
        {
            _list = list;
            _details = details;
            Children.Add(list);
            Children.Add(details);
        }

        public Func<double>? Room { get; set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            _list.Measure(availableSize);
            Size list = _list.DesiredSize;
            if (!_details.IsVisible)
                return list;
            double room = Math.Min(availableSize.Width, Room?.Invoke() ?? double.PositiveInfinity);
            double beside = room - list.Width - Gap;
            _beside = beside >= MinDetailsWidth;
            if (_beside)
            {
                _details.Measure(availableSize.WithWidth(beside));
                Size details = _details.DesiredSize;
                return new Size(list.Width + Gap + details.Width, Math.Max(list.Height, details.Height));
            }
            double below = Math.Max(0, availableSize.Height - list.Height - Gap);
            _details.Measure(new Size(Math.Max(list.Width, Math.Min(room, DetailsWidth)), below));
            Size under = _details.DesiredSize;
            return new Size(Math.Max(list.Width, under.Width), list.Height + Gap + under.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Size list = _list.DesiredSize;
            _list.Arrange(new Rect(list));
            if (!_details.IsVisible)
                return finalSize;
            Size details = _details.DesiredSize;
            _details.Arrange(
                _beside
                    ? new Rect(list.Width + Gap, 0, details.Width, details.Height)
                    : new Rect(0, list.Height + Gap, details.Width, details.Height)
            );
            return finalSize;
        }
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
            _label.FontFamily = _view._look?.FontFamily ?? FontFamily.Default;
            _label.FontSize = _view._look?.FontSize ?? 12;
            _label.Inlines = Label(item.Label, shown.Highlights);
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

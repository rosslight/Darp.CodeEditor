using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// Completion's list, as VS Code's suggest widget draws it: rows exactly as high as the editor's lines, at most
/// <see cref="MaxRows"/> of them, and no more than fit the height that the list gets. The rows that show, the
/// selection, paging and scrolling all go by that height, so that the selected row is always one the window shows. A
/// row is the item's icon and its label in the editor's font, the characters that matched in bold and the highlight
/// color, struck through when deprecated, and its label details as VS Code shows them; and, while the details beside
/// the list are closed, the detail of the selected item without label details on the right. It takes no keyboard
/// focus: the editor keeps it. A click commits a row's item; the wheel and the scroll bar scroll.
/// </summary>
internal sealed class CompletionListBox : Control
{
    /// <summary>How far a row's label starts from the list's left edge: its padding, its icon and the gap after it.</summary>
    public const double LabelInset = RowPaddingLeft + IconSize + IconGap;

    private const double RowPaddingLeft = 2;
    private const double IconSize = 16;
    private const double IconGap = 4;

    /// <summary>The scroll bar's gutter, which a row's text stays out of.</summary>
    private const double BarWidth = 10;

    /// <summary>How many rows a notch of the wheel scrolls.</summary>
    private const int WheelRows = 3;

    private readonly List<Row> _rows = [];
    private readonly PopupScrollBar _bar = new()
    {
        Orientation = Orientation.Vertical,
        Visibility = ScrollBarVisibility.Auto,
        AllowAutoHide = false,
        SmallChange = 1,
    };
    private CodeLook? _look;
    private PopupColors _colors = PopupColors.Default;
    private SymbolIconColors? _icons;
    private bool _detailsOpen;

    /// <summary>The index of the item under the pointer; -1 for none.</summary>
    private int _hovered = -1;

    /// <summary>The part of a row that the wheel has scrolled, until it adds up to a whole one.</summary>
    private double _wheel;

    /// <summary>How many whole rows fit the height that the list got when it was last laid out within a limit.</summary>
    private int _fitRows = int.MaxValue;

    public CompletionListBox()
    {
        ClipToBounds = true;
        VisualChildren.Add(_bar);
        LogicalChildren.Add(_bar);
        _bar.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                ScrollTo((int)Math.Round(_bar.Value));
        };
    }

    /// <summary>Raised when an item is clicked, with its index.</summary>
    public event Action<int>? Clicked;

    /// <summary>The items that the list shows, in order.</summary>
    public IReadOnlyList<ShownItem> Items { get; private set; } = [];

    /// <summary>The selected item's index.</summary>
    public int SelectedIndex { get; private set; }

    /// <summary>The index of the item in the first row that shows.</summary>
    public int FirstRow { get; private set; }

    /// <summary>The message that shows instead of items, such as "No suggestions."; null while items show.</summary>
    public string? Message { get; private set; }

    /// <summary>The height of a row: the editor's line height.</summary>
    public double RowHeight { get; private set; } = 1;

    /// <summary>The most rows that show at once; the list scrolls beyond.</summary>
    public int MaxRows
    {
        get;
        set
        {
            field = value;
            InvalidateMeasure();
            Refresh();
        }
    } = 1;

    /// <summary>
    /// How many rows show: the items, up to <see cref="MaxRows"/>, and no more than fit the height that the list gets;
    /// one for a message.
    /// </summary>
    public int VisibleRows => Math.Min(PreferredRows, Math.Max(1, _fitRows));

    /// <summary>How many rows show where the list has room for them all.</summary>
    private int PreferredRows => Message is not null ? 1 : Math.Min(Items.Count, MaxRows);

    /// <summary>Shows <paramref name="items"/>, with <paramref name="selected"/> selected and in view.</summary>
    /// <param name="items">The items.</param>
    /// <param name="selected">The selected item's index.</param>
    /// <param name="look">The editor's font, for the labels.</param>
    /// <param name="colors">The popups' colors.</param>
    /// <param name="icons">The icons' colors; none while the color theme loads.</param>
    /// <param name="rowHeight">The height of a row.</param>
    public void Show(
        IReadOnlyList<ShownItem> items,
        int selected,
        CodeLook look,
        PopupColors colors,
        SymbolIconColors? icons,
        double rowHeight
    )
    {
        (Items, Message, _look, _colors, _icons, RowHeight) = (items, null, look, colors, icons, rowHeight);
        (FirstRow, _hovered, SelectedIndex) = (0, -1, selected);
        ScrollIntoView(selected);
        InvalidateMeasure();
        Refresh();
    }

    /// <summary>Shows <paramref name="message"/> in one row, instead of items.</summary>
    public void ShowMessage(string message, CodeLook look, PopupColors colors, double rowHeight)
    {
        (Items, Message, _look, _colors, RowHeight) = ([], message, look, colors, rowHeight);
        (FirstRow, _hovered) = (0, -1);
        InvalidateMeasure();
        Refresh();
    }

    /// <summary>Selects the item at <paramref name="index"/>, and scrolls it into view.</summary>
    public void Select(int index)
    {
        SelectedIndex = index;
        ScrollIntoView(index);
        Refresh();
    }

    /// <summary>Whether the selected item's details show beside the list: its row shows its detail only while not.</summary>
    public void SetDetailsOpen(bool open)
    {
        _detailsOpen = open;
        Refresh();
    }

    /// <summary>The row that shows the item at <paramref name="index"/>; null while it is out of view.</summary>
    public Row? RowOf(int index) =>
        Message is null && index >= FirstRow && index < FirstRow + VisibleRows ? _rows[index - FirstRow] : null;

    /// <summary>
    /// As tall as its rows: all that it would show, without a limit, as the popup first asks to place it; within one,
    /// the whole rows that fit, with the selected item among them.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        bool limited = !double.IsPositiveInfinity(availableSize.Height);
        // Within half a pixel, as rounding the layout may take that much off.
        int fit = limited ? (int)Math.Floor((availableSize.Height + 0.5) / RowHeight) : _fitRows;
        if (fit != _fitRows)
        {
            _fitRows = fit;
            ScrollIntoView(SelectedIndex);
            Refresh();
        }
        var rowSize = new Size(availableSize.Width, RowHeight);
        foreach (Row row in _rows)
            row.Measure(rowSize);
        double height = (limited ? VisibleRows : PreferredRows) * RowHeight;
        _bar.Measure(new Size(BarWidth, height));
        return new Size(0, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].Arrange(new Rect(0, i * RowHeight, finalSize.Width, RowHeight));
        _bar.Arrange(new Rect(finalSize.Width - BarWidth, 0, BarWidth, finalSize.Height));
        return finalSize;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SetHovered(IndexAt(e.GetPosition(this)));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHovered(-1);
    }

    /// <summary>A click on an item commits it. Handled, so that the list takes no focus: the editor keeps the keyboard.</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        if (
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && IndexAt(e.GetPosition(this)) is >= 0 and var index
        )
            Clicked?.Invoke(index);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Items.Count <= VisibleRows)
            return;
        e.Handled = true;
        _wheel -= e.Delta.Y * WheelRows;
        int rows = (int)_wheel;
        _wheel -= rows;
        ScrollTo(FirstRow + rows);
    }

    private void ScrollIntoView(int index)
    {
        int visible = VisibleRows;
        if (index < FirstRow)
            FirstRow = index;
        else if (index >= FirstRow + visible)
            FirstRow = index - visible + 1;
        FirstRow = Math.Clamp(FirstRow, 0, Math.Max(0, Items.Count - visible));
    }

    private void ScrollTo(int top)
    {
        top = Math.Clamp(top, 0, Math.Max(0, Items.Count - VisibleRows));
        if (top == FirstRow)
            return;
        FirstRow = top;
        Refresh();
    }

    private void SetHovered(int index)
    {
        if (index == _hovered)
            return;
        _hovered = index;
        Refresh();
    }

    /// <summary>The index of the item at <paramref name="point"/>; -1 outside the rows.</summary>
    private int IndexAt(Point point)
    {
        int index = FirstRow + (int)Math.Floor(point.Y / RowHeight);
        return Message is null && point.Y >= 0 && index < Math.Min(Items.Count, FirstRow + VisibleRows) ? index : -1;
    }

    /// <summary>Gives the rows in view their items again, as the items, the selection or the scrolling changed.</summary>
    private void Refresh()
    {
        int visible = VisibleRows;
        // More rows than before, as the user made the list taller at its last row, start further up.
        FirstRow = Math.Clamp(FirstRow, 0, Math.Max(0, Items.Count - visible));
        while (_rows.Count < visible)
        {
            var row = new Row(this);
            _rows.Add(row);
            // Under the scroll bar.
            VisualChildren.Insert(_rows.Count - 1, row);
            LogicalChildren.Add(row);
        }
        for (int i = 0; i < _rows.Count; i++)
        {
            Row row = _rows[i];
            row.IsVisible = i < visible;
            if (!row.IsVisible || _look is not { } look)
                continue;
            if (Message is { } message)
                row.ShowMessage(message, look, _colors);
            else
            {
                int index = FirstRow + i;
                row.Show(Items[index], look, _colors, index == SelectedIndex, index == _hovered);
            }
        }
        _bar.Maximum = Math.Max(0, Items.Count - visible);
        _bar.ViewportSize = visible;
        _bar.LargeChange = visible;
        // A drag keeps its fraction of a row, so that the thumb stays under the pointer.
        if ((int)Math.Round(_bar.Value) != FirstRow)
            _bar.Value = FirstRow;
        InvalidateArrange();
    }

    /// <summary>
    /// A row: the item's icon, its label, and the detail of its label details right after it, dimmed. On the right,
    /// dimmed, the description of its label details, or for an item without label details, the selected item's detail
    /// while the details beside the list are closed, as in VS Code. The list reuses it for other items.
    /// </summary>
    internal sealed class Row : Control
    {
        /// <summary>The gap between the label and the detail on the right.</summary>
        private const double Gap = 12;

        /// <summary>The most of a label or a detail that a row shows; more costs the UI thread for nothing.</summary>
        private const int MaxLength = 256;

        private readonly CompletionListBox _list;
        private readonly CompletionIcon _icon = new() { VerticalAlignment = VerticalAlignment.Center };

        private IBrush? _background;

        public Row(CompletionListBox list)
        {
            _list = list;
            VisualChildren.Add(_icon);
            VisualChildren.Add(Label);
            VisualChildren.Add(Right);
            LogicalChildren.Add(Label);
            LogicalChildren.Add(Right);
        }

        /// <summary>The item the row shows; null for a message.</summary>
        public ShownItem? Item { get; private set; }

        /// <summary>The label with its label details' detail, or the message.</summary>
        public TextBlock Label { get; } = Text();

        /// <summary>The text on the right: the label details' description, or the selected item's detail.</summary>
        public TextBlock Right { get; } = Text();

        public void Show(ShownItem shown, CodeLook look, PopupColors colors, bool selected, bool hovered)
        {
            Item = shown;
            CompletionItem item = shown.Item;
            IBrush foreground = selected ? colors.SuggestSelectedForeground : colors.SuggestForeground;
            _background =
                selected ? colors.SuggestSelectedBackground
                : hovered ? colors.ListHoverBackground
                : null;
            Label.FontFamily = Right.FontFamily = look.FontFamily;
            Label.FontSize = look.FontSize;
            Label.Foreground = foreground;
            Label.Inlines = LabelRuns(
                Shortened(item.Label),
                shown.Highlights,
                selected ? colors.SuggestFocusHighlight : colors.SuggestHighlight
            );
            if (item.LabelDetails?.Detail is { Length: > 0 } labelDetail)
                Label.Inlines.Add(new Run(OneLine(labelDetail)) { Foreground = Dimmed(foreground) });
            // As VS Code shows deprecated items.
            Label.TextDecorations = item.IsDeprecated ? TextDecorations.Strikethrough : null;
            Label.Opacity = item.IsDeprecated ? 0.66 : 1;
            string? right =
                item.LabelDetails is { } details ? details.Description
                : selected && !_list._detailsOpen ? item.Detail
                : null;
            Right.IsVisible = !string.IsNullOrEmpty(right);
            Right.Text = Right.IsVisible ? OneLine(right ?? "") : null;
            Right.FontSize = Math.Round(look.FontSize * 0.85);
            Right.Foreground = Dimmed(foreground);
            _icon.IsVisible = _list._icons is not null;
            if (_list._icons is { } icons)
                _icon.Show(
                    item.Kind,
                    selected ? colors.SuggestSelectedIconForeground ?? icons.Of(item.Kind) : icons.Of(item.Kind)
                );
            InvalidateMeasure();
            InvalidateVisual();
        }

        public void ShowMessage(string message, CodeLook look, PopupColors colors)
        {
            Item = null;
            _background = null;
            _icon.IsVisible = Right.IsVisible = false;
            Label.Inlines = null;
            Label.Text = message;
            (Label.TextDecorations, Label.Opacity) = (null, 1);
            (Label.FontFamily, Label.FontSize) = (look.FontFamily, look.FontSize);
            Label.Foreground = Dimmed(colors.SuggestForeground);
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _icon.Measure(new Size(IconSize, IconSize));
            Label.Measure(Size.Infinity);
            Right.Measure(Size.Infinity);
            return new Size(0, _list.RowHeight);
        }

        /// <summary>
        /// The label comes first; the detail takes what it leaves, past the scroll bar's gutter from the right edge.
        /// Text that does not fit ends with "…".
        /// </summary>
        protected override Size ArrangeOverride(Size finalSize)
        {
            double height = finalSize.Height;
            _icon.Arrange(new Rect(RowPaddingLeft, (height - IconSize) / 2, IconSize, IconSize));
            double left = _icon.IsVisible ? LabelInset : RowPaddingLeft;
            double end = Math.Max(left, finalSize.Width - BarWidth);
            double right = Right.IsVisible
                ? Math.Min(Right.DesiredSize.Width, Math.Max(0, end - left - Label.DesiredSize.Width - Gap))
                : 0;
            double label = Math.Max(0, end - left - (right > 0 ? right + Gap : 0));
            Label.Arrange(new Rect(left, (height - Label.DesiredSize.Height) / 2, label, Label.DesiredSize.Height));
            Right.Arrange(
                new Rect(end - right, (height - Right.DesiredSize.Height) / 2, right, Right.DesiredSize.Height)
            );
            return finalSize;
        }

        public override void Render(DrawingContext context) =>
            // Transparent where it has no background: the pointer hits the whole row.
            context.FillRectangle(_background ?? Brushes.Transparent, new Rect(Bounds.Size));

        private static PopupText Text() =>
            new()
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

        /// <summary>The label, with the characters at <paramref name="highlights"/> in bold and the highlight color.</summary>
        private static InlineCollection LabelRuns(string label, int[] highlights, IBrush highlight)
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
                var run = new Run(label[start..end]);
                if (bold)
                    (run.FontWeight, run.Foreground) = (FontWeight.Bold, highlight);
                inlines.Add(run);
                start = end;
            }
            return inlines;
        }

        /// <summary>
        /// <paramref name="text"/>, or its start with "…" when it is longer than a row shows; a cut never splits a
        /// surrogate pair. Matched characters are all within the start: matching reads 128 characters.
        /// </summary>
        private static string Shortened(string text)
        {
            if (text.Length <= MaxLength)
                return text;
            int end = char.IsHighSurrogate(text[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
            return string.Concat(text.AsSpan(0, end), "…");
        }

        /// <summary><paramref name="text"/> as <see cref="Shortened"/> leaves it, on one line.</summary>
        private static string OneLine(string text) => Shortened(text).ReplaceLineEndings(" ");

        /// <summary>Text dimmed as VS Code dims a row's detail and its messages.</summary>
        private static IBrush Dimmed(IBrush brush) =>
            brush is ISolidColorBrush solid ? new ImmutableSolidColorBrush(solid.Color, solid.Opacity * 0.7) : brush;
    }
}

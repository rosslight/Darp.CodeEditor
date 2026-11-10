using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// Completion's list, as VS Code's suggest widget draws it: rows exactly as high as the editor's lines with no gaps,
/// at most <see cref="MaxRows"/> of them at once, each <c>[icon][label][labelDetails.detail]</c> on the left and
/// <c>labelDetails.description</c> right-aligned, in the editor's font and the color theme's suggest colors. The
/// selected row shows the item's detail on the right while the details beside the list are closed. It takes no
/// keyboard focus: the editor keeps it. It is the editor's own control, so app themes don't restyle it.
/// </summary>
internal sealed class CompletionListBox : Control
{
    /// <summary>How far a row's icon is from its left edge.</summary>
    public const double RowPaddingLeft = 2;

    /// <summary>The gap between a row's icon and its label.</summary>
    public const double IconGap = 4;

    /// <summary>How far a row's text stays from its right edge: the slider goes there.</summary>
    private const double RowPaddingRight = PopupScroller.SliderWidth;

    /// <summary>How far a row's label starts from the list's left edge.</summary>
    public const double LabelInset = RowPaddingLeft + CompletionIconSize + IconGap;

    private const double CompletionIconSize = 16;

    private const double MinSliderLength = 20;

    private readonly List<Row> _rows = [];
    private readonly Slider _slider;
    private IReadOnlyList<ShownItem> _items = [];
    private int _selected;
    private int _top;
    private int _hovered = -1;
    private double _wheel;
    private string? _message;

    /// <summary>Where a drag of the slider started: the pointer's y and the first row then; null while none runs.</summary>
    private (double Pointer, int Top)? _drag;

    public CompletionListBox()
    {
        _slider = new Slider(this);
        ClipToBounds = true;
    }

    /// <summary>Raised when an item is clicked, with its index.</summary>
    public event Action<int>? Clicked;

    /// <summary>The items that the list shows, in order.</summary>
    public IReadOnlyList<ShownItem> Items => _items;

    /// <summary>The number of items, for tests and the slider.</summary>
    public int ItemCount => _items.Count;

    /// <summary>The selected item's index.</summary>
    public int SelectedIndex => _selected;

    /// <summary>The message that shows instead of items, such as "No suggestions"; null while items show.</summary>
    public string? Message => _message;

    /// <summary>The most rows that show at once; the list scrolls beyond.</summary>
    public int MaxRows { get; set; } = CompletionView.DefaultRows;

    /// <summary>The list's width where there is room for it.</summary>
    public double PreferredWidth
    {
        get;
        set
        {
            field = value;
            InvalidateMeasure();
        }
    } = CompletionView.DefaultWidth - 2;

    /// <summary>The height of a row: the editor's line height.</summary>
    public double RowHeight { get; private set; } = 18;

    /// <summary>How the rows look.</summary>
    public PopupLook? Look { get; private set; }

    /// <summary>The icons' colors; null while the color theme loads.</summary>
    public SymbolIconColors? Icons { get; private set; }

    /// <summary>Whether the selected item's details show beside the list: its row shows its detail only while not.</summary>
    public bool DetailsOpen
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Refresh();
        }
    }

    /// <summary>How many rows show: the items up to <see cref="MaxRows"/>, or one for a message.</summary>
    public int VisibleRows => _message is not null ? 1 : Math.Min(_items.Count, Math.Max(1, MaxRows));

    /// <summary>Shows <paramref name="items"/> in the look of <paramref name="look"/>, with <paramref name="selected"/> selected and in view.</summary>
    public void Show(
        IReadOnlyList<ShownItem> items,
        int selected,
        PopupLook look,
        SymbolIconColors? icons,
        double rowHeight
    )
    {
        (_items, _message, Look, Icons, RowHeight) = (items, null, look, icons, rowHeight);
        _hovered = -1;
        _top = 0;
        _selected = Math.Clamp(selected, 0, Math.Max(0, items.Count - 1));
        ScrollIntoView(_selected);
        InvalidateMeasure();
        Refresh();
    }

    /// <summary>Shows <paramref name="message"/> in one row, instead of items.</summary>
    public void ShowMessage(string message, PopupLook look, double rowHeight)
    {
        (_items, _message, Look, RowHeight) = (ItemsNone, message, look, rowHeight);
        _hovered = -1;
        _top = 0;
        InvalidateMeasure();
        Refresh();
    }

    /// <summary>Selects the item at <paramref name="index"/>, and scrolls it into view.</summary>
    public void Select(int index)
    {
        _selected = index;
        ScrollIntoView(index);
        Refresh();
    }

    /// <summary>The row that shows the item at <paramref name="index"/>; null while it is out of view.</summary>
    internal Row? RowOf(int index) =>
        index >= _top && index < _top + VisibleRows && index - _top < _rows.Count ? _rows[index - _top] : null;

    /// <summary>The bounds of the row of the item at <paramref name="index"/>; null while it is out of view.</summary>
    public Rect? RowBounds(int index) =>
        index >= _top && index < _top + VisibleRows
            ? new Rect(0, (index - _top) * RowHeight, Bounds.Width, RowHeight)
            : null;

    private static readonly IReadOnlyList<ShownItem> ItemsNone = [];

    private void ScrollIntoView(int index)
    {
        int visible = VisibleRows;
        if (index < _top)
            _top = index;
        else if (index >= _top + visible)
            _top = index - visible + 1;
        _top = Math.Clamp(_top, 0, Math.Max(0, _items.Count - visible));
    }

    /// <summary>Gives the rows in view their items again, as the items, the selection or the scrolling changed.</summary>
    private void Refresh()
    {
        int visible = VisibleRows;
        while (_rows.Count < visible)
        {
            var row = new Row(this);
            _rows.Add(row);
            VisualChildren.Insert(VisualChildren.Count - (VisualChildren.Contains(_slider) ? 1 : 0), row);
            LogicalChildren.Add(row);
        }
        if (!VisualChildren.Contains(_slider))
            VisualChildren.Add(_slider);
        for (int i = 0; i < _rows.Count; i++)
        {
            Row row = _rows[i];
            row.IsVisible = i < visible;
            if (i >= visible)
                continue;
            if (_message is { } message)
                row.ShowMessage(message);
            else
            {
                int index = _top + i;
                row.Show(_items[index], index == _selected, index == _hovered);
            }
        }
        _slider.InvalidateVisual();
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = Math.Min(PreferredWidth, availableSize.Width);
        var rowSize = new Size(width, RowHeight);
        foreach (Row row in _rows)
            row.Measure(rowSize);
        _slider.Measure(availableSize);
        return new Size(width, VisibleRows * RowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].Arrange(new Rect(0, i * RowHeight, finalSize.Width, RowHeight));
        _slider.Arrange(new Rect(finalSize));
        return finalSize;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point point = e.GetPosition(this);
        if (_drag is var (start, top))
        {
            double track = Bounds.Height - SliderRect().Height;
            int hidden = _items.Count - VisibleRows;
            if (track > 0 && hidden > 0)
                ScrollTo(top + (int)Math.Round((point.Y - start) / track * hidden));
            return;
        }
        int hovered = _message is null && point.X < Bounds.Width - RowPaddingRight ? IndexAt(point) : -1;
        if (hovered != _hovered)
        {
            _hovered = hovered;
            Refresh();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hovered < 0)
            return;
        _hovered = -1;
        Refresh();
    }

    /// <summary>
    /// A click on an item commits it, and one on the slider drags it. The list handles the press, so that it takes no
    /// focus: the editor keeps the keyboard.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Point point = e.GetPosition(this);
        if (CanScroll && point.X >= Bounds.Width - RowPaddingRight)
        {
            Rect slider = SliderRect();
            if (point.Y < slider.Top)
                ScrollTo(_top - VisibleRows);
            else if (point.Y > slider.Bottom)
                ScrollTo(_top + VisibleRows);
            else
            {
                _drag = (point.Y, _top);
                e.Pointer.Capture(this);
                _slider.InvalidateVisual();
            }
            return;
        }
        if (_message is null && IndexAt(point) is var index and >= 0)
            Clicked?.Invoke(index);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null)
            return;
        _drag = null;
        e.Pointer.Capture(null);
        _slider.InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!CanScroll)
            return;
        e.Handled = true;
        _wheel -= e.Delta.Y * 3;
        int rows = (int)_wheel;
        _wheel -= rows;
        ScrollTo(_top + rows);
    }

    private bool CanScroll => _message is null && _items.Count > VisibleRows;

    private void ScrollTo(int top)
    {
        top = Math.Clamp(top, 0, Math.Max(0, _items.Count - VisibleRows));
        if (top == _top)
            return;
        _top = top;
        Refresh();
    }

    private int IndexAt(Point point)
    {
        int index = _top + (int)(point.Y / RowHeight);
        return point.Y >= 0 && index < Math.Min(_items.Count, _top + VisibleRows) ? index : -1;
    }

    private Rect SliderRect()
    {
        double height = Bounds.Height;
        int count = Math.Max(1, _items.Count);
        double length = Math.Max(MinSliderLength, height * VisibleRows / count);
        int hidden = Math.Max(1, _items.Count - VisibleRows);
        double top = (height - length) * _top / hidden;
        return new Rect(Bounds.Width - PopupScroller.SliderWidth, top, PopupScroller.SliderWidth, length);
    }

    /// <summary>The slider, over the rows' right padding.</summary>
    private sealed class Slider(CompletionListBox list) : Control
    {
        private readonly CompletionListBox _list = list;

        public override void Render(DrawingContext context)
        {
            if (!_list.CanScroll || _list.Look?.Colors is not { } colors)
                return;
            context.FillRectangle(_list._drag is null ? colors.Slider : colors.SliderActive, _list.SliderRect());
        }
    }

    /// <summary>
    /// A row: the item's icon, its label in the editor's font with the characters that matched in bold and the
    /// highlight color, struck through when it is deprecated, its label's detail dimmed after it, and its description,
    /// or its detail when selected, smaller on the right. The list reuses it for other items.
    /// </summary>
    internal sealed class Row : Control
    {
        /// <summary>The gap between the label and the text on the right.</summary>
        private const double Gap = 12;

        /// <summary>The narrowest the text on the right shows; with less room, it hides.</summary>
        private const double MinRightWidth = 24;

        private readonly CompletionListBox _list;
        private readonly CompletionIcon _icon = new() { VerticalAlignment = VerticalAlignment.Center };

        private readonly TextBlock _label = new()
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        private readonly TextBlock _right = new()
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        private IBrush? _background;
        private IBrush? _outline;

        public Row(CompletionListBox list)
        {
            _list = list;
            VisualChildren.Add(_icon);
            VisualChildren.Add(_label);
            VisualChildren.Add(_right);
            LogicalChildren.Add(_label);
            LogicalChildren.Add(_right);
        }

        /// <summary>The item the row shows, for tests.</summary>
        public ShownItem? Item { get; private set; }

        /// <summary>The label, for tests.</summary>
        public TextBlock Label => _label;

        /// <summary>The text on the right, for tests.</summary>
        public TextBlock Right => _right;

        public void Show(ShownItem shown, bool selected, bool hovered)
        {
            Item = shown;
            if (_list.Look is not { } look)
                return;
            PopupColors colors = look.Colors;
            CompletionItem item = shown.Item;
            IBrush foreground = selected
                ? colors.SuggestSelectedForeground ?? colors.SuggestForeground
                : colors.SuggestForeground;
            IBrush highlight = selected ? colors.SuggestFocusHighlight : colors.SuggestHighlight;
            _background =
                selected ? colors.SuggestSelectedBackground
                : hovered ? colors.ListHoverBackground
                : null;
            _outline = selected ? colors.SuggestFocusOutline : null;

            _label.FontFamily = look.CodeFont;
            _label.FontSize = look.CodeSize;
            _label.Foreground = foreground;
            InlineCollection inlines = LabelRuns(Shortened(item.Label), shown.Highlights, highlight);
            if (item.LabelDetail is { } detail)
                inlines.Add(new Run(detail) { Foreground = Dim(foreground, 0.6) });
            _label.Inlines = inlines;
            _label.TextDecorations = item.IsDeprecated ? TextDecorations.Strikethrough : null;
            _label.Opacity = item.IsDeprecated ? 0.66 : 1;

            string? right = item.LabelDescription ?? (selected && !_list.DetailsOpen ? OneLine(item.Detail) : null);
            _right.IsVisible = right is not null;
            _right.Text = right;
            _right.FontFamily = look.CodeFont;
            _right.FontSize = Math.Round(look.CodeSize * 0.85);
            _right.Foreground = Dim(foreground, 0.7);

            _icon.IsVisible = _list.Icons is not null;
            if (_list.Icons is { } icons)
            {
                _icon.Show(
                    item.Kind,
                    selected && colors.SuggestSelectedIconForeground is { } own ? own : icons.Of(item.Kind)
                );
            }
            InvalidateMeasure();
            InvalidateVisual();
        }

        public void ShowMessage(string message)
        {
            Item = null;
            if (_list.Look is not { } look)
                return;
            _background = null;
            _outline = null;
            _icon.IsVisible = false;
            _right.IsVisible = false;
            _label.Inlines = null;
            _label.Text = message;
            _label.TextDecorations = null;
            _label.Opacity = 1;
            _label.FontFamily = look.CodeFont;
            _label.FontSize = look.CodeSize;
            _label.Foreground = Dim(look.Colors.SuggestForeground, 0.7);
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _icon.Measure(availableSize);
            _label.Measure(Size.Infinity);
            _right.Measure(Size.Infinity);
            return new Size(availableSize.Width, _list.RowHeight);
        }

        /// <summary>
        /// The label and its detail come first; the text on the right takes what they leave, and hides below
        /// <see cref="MinRightWidth"/>. Text that does not fit ends with "…".
        /// </summary>
        protected override Size ArrangeOverride(Size finalSize)
        {
            double height = finalSize.Height;
            _icon.Arrange(
                new Rect(RowPaddingLeft, (height - CompletionIconSize) / 2, CompletionIconSize, CompletionIconSize)
            );
            double left = LabelInset;
            double end = Math.Max(left, finalSize.Width - RowPaddingRight);
            double label = _label.DesiredSize.Height;
            double rightWidth = 0;
            if (_right.IsVisible)
            {
                double room = end - left - _label.DesiredSize.Width - Gap;
                rightWidth = Math.Min(_right.DesiredSize.Width, Math.Max(0, room));
                if (rightWidth < MinRightWidth)
                    rightWidth = Math.Min(_right.DesiredSize.Width, Math.Max(0, (end - left) / 3));
            }
            double labelWidth = Math.Max(0, end - left - (rightWidth > 0 ? rightWidth + Gap : 0));
            _label.Arrange(new Rect(left, (height - label) / 2, labelWidth, label));
            double right = _right.DesiredSize.Height;
            _right.Arrange(new Rect(end - rightWidth, (height - right) / 2, rightWidth, right));
            return finalSize;
        }

        public override void Render(DrawingContext context)
        {
            // Transparent where it has no background: the pointer hits the whole row.
            context.FillRectangle(_background ?? Brushes.Transparent, new Rect(Bounds.Size));
            if (_outline is { } outline)
                context.DrawRectangle(new ImmutablePen(outline.ToImmutable()), new Rect(Bounds.Size).Deflate(0.5));
        }

        /// <summary>The label, with the characters at <paramref name="highlights"/> in bold, in the highlight color.</summary>
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
                {
                    run.FontWeight = FontWeight.Bold;
                    run.Foreground = highlight;
                }
                inlines.Add(run);
                start = end;
            }
            return inlines;
        }

        /// <summary>
        /// <paramref name="label"/>, or its start with "…" when it is longer than a row shows; a cut never splits a
        /// surrogate pair. Matched characters are all within the start: matching reads 128 characters.
        /// </summary>
        private static string Shortened(string label)
        {
            const int MaxLabelLength = 256;
            if (label.Length <= MaxLabelLength)
                return label;
            int end = char.IsHighSurrogate(label[MaxLabelLength - 1]) ? MaxLabelLength - 1 : MaxLabelLength;
            return string.Concat(label.AsSpan(0, end), "…");
        }

        /// <summary>An item's detail on one line, at most as long as a row shows.</summary>
        private static string? OneLine(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            const int MaxLength = 256;
            string line = text.Length > MaxLength ? string.Concat(text.AsSpan(0, MaxLength), "…") : text;
            return line.ReplaceLineEndings(" ");
        }

        private static IBrush Dim(IBrush brush, double opacity) =>
            brush is ISolidColorBrush solid
                ? new ImmutableSolidColorBrush(solid.Color, solid.Opacity * opacity)
                : brush;
    }
}

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Darp.CodeEditor.LanguageServices;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// Draws the visible rows of the completion list itself. Changing the matches only invalidates the visual, so
/// filtering costs no control creation; a ListBox rebuilt its item containers on every keystroke (5-20 ms).
/// </summary>
internal sealed class CompletionListView : Control
{
    private const int MaxVisibleRows = 12;
    private const double ListWidth = 380;

    private static readonly IBrush s_background = new ImmutableSolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush s_selection = new ImmutableSolidColorBrush(Color.Parse("#04395E"));
    private static readonly IBrush s_foreground = new ImmutableSolidColorBrush(Color.Parse("#D4D4D4"));
    private static readonly IBrush s_detail = new ImmutableSolidColorBrush(Color.Parse("#8C8C8C"));
    private static readonly IBrush s_method = new ImmutableSolidColorBrush(Color.Parse("#B180D7"));
    private static readonly IBrush s_type = new ImmutableSolidColorBrush(Color.Parse("#EE9D28"));
    private static readonly IBrush s_keyword = new ImmutableSolidColorBrush(Color.Parse("#569CD6"));
    private static readonly IBrush s_variable = new ImmutableSolidColorBrush(Color.Parse("#75BEFF"));

    private IReadOnlyList<CompletionItem> _items;
    private readonly Typeface _typeface;
    private readonly double _fontSize;
    private readonly double _rowHeight;
    private int[] _matches = [];
    private int _selected = -1;
    private int _firstVisible;

    public CompletionListView(IReadOnlyList<CompletionItem> items, FontFamily fontFamily, double fontSize)
    {
        _items = items;
        _typeface = new Typeface(fontFamily);
        _fontSize = fontSize;
        _rowHeight = Math.Ceiling(fontSize * 1.45);
        ClipToBounds = true;
    }

    public event EventHandler? SelectionChanged;

    public event EventHandler? ItemActivated;

    /// <summary>The index into the items of the selected match, or null.</summary>
    public int? SelectedItem => _selected >= 0 && _selected < _matches.Length ? _matches[_selected] : null;

    /// <summary>Draws other items; <see cref="SetMatches"/> follows.</summary>
    public void SetItems(IReadOnlyList<CompletionItem> items)
    {
        _items = items;
        _matches = [];
    }

    public void SetMatches(int[] matches)
    {
        bool sizeChanged = Math.Min(matches.Length, MaxVisibleRows) != Math.Min(_matches.Length, MaxVisibleRows);
        _matches = matches;
        _firstVisible = 0;
        Select(matches.Length == 0 ? -1 : 0);
        if (sizeChanged)
            InvalidateMeasure();
        InvalidateVisual();
    }

    public void Move(int delta)
    {
        if (_matches.Length > 0)
            Select(Math.Clamp(_selected + delta, 0, _matches.Length - 1));
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(ListWidth, Math.Max(1, Math.Min(_matches.Length, MaxVisibleRows)) * _rowHeight);

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(s_background, new Rect(Bounds.Size));
        int last = Math.Min(_matches.Length, _firstVisible + MaxVisibleRows);
        for (int row = _firstVisible; row < last; row++)
        {
            double y = (row - _firstVisible) * _rowHeight;
            if (row == _selected)
                context.FillRectangle(s_selection, new Rect(0, y, Bounds.Width, _rowHeight));
            CompletionItem item = _items[_matches[row]];
            double x = 6;
            x += DrawText(context, KindGlyph(item.Kind), KindBrush(item.Kind), x, y) + 8;
            x += DrawText(context, item.DisplayText, s_foreground, x, y) + 8;
            if (item.InlineDescription is { } detail)
                DrawText(context, detail, s_detail, x, y);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        int row = _firstVisible + (int)(e.GetPosition(this).Y / _rowHeight);
        if (row < _matches.Length)
            Select(row);
        if (e.ClickCount == 2)
            ItemActivated?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        int maxFirst = Math.Max(0, _matches.Length - MaxVisibleRows);
        _firstVisible = Math.Clamp(_firstVisible - (int)Math.Round(e.Delta.Y * 3), 0, maxFirst);
        InvalidateVisual();
        e.Handled = true;
    }

    private void Select(int row)
    {
        _selected = row;
        if (row >= 0)
        {
            if (row < _firstVisible)
                _firstVisible = row;
            else if (row >= _firstVisible + MaxVisibleRows)
                _firstVisible = row - MaxVisibleRows + 1;
        }
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private double DrawText(DrawingContext context, string text, IBrush brush, double x, double y)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            _fontSize,
            brush
        );
        context.DrawText(formatted, new Point(x, y + (_rowHeight - formatted.Height) / 2));
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private static string KindGlyph(CompletionItemKind kind) =>
        kind switch
        {
            CompletionItemKind.Method or CompletionItemKind.ExtensionMethod => "m",
            CompletionItemKind.Property => "p",
            CompletionItemKind.Field or CompletionItemKind.Local or CompletionItemKind.Parameter => "v",
            CompletionItemKind.Event => "e",
            CompletionItemKind.Class or CompletionItemKind.Struct or CompletionItemKind.Delegate => "C",
            CompletionItemKind.Interface => "I",
            CompletionItemKind.Enum or CompletionItemKind.EnumMember => "E",
            CompletionItemKind.Namespace => "N",
            CompletionItemKind.Keyword => "k",
            CompletionItemKind.Constant => "c",
            _ => "·",
        };

    private static IBrush KindBrush(CompletionItemKind kind) =>
        kind switch
        {
            CompletionItemKind.Method or CompletionItemKind.ExtensionMethod => s_method,
            CompletionItemKind.Class
            or CompletionItemKind.Struct
            or CompletionItemKind.Delegate
            or CompletionItemKind.Interface
            or CompletionItemKind.Enum => s_type,
            CompletionItemKind.Keyword => s_keyword,
            _ => s_variable,
        };
}

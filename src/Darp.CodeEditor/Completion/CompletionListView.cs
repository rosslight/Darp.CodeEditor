using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Theming;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// Draws the visible rows of the completion list itself. Changing the matches only invalidates the visual, so
/// filtering costs no control creation; a ListBox rebuilt its item containers on every keystroke (5-20 ms).
/// </summary>
internal sealed class CompletionListView : Control
{
    private const int MaxVisibleRows = 12;
    private const double ListWidth = 380;
    private const double GlyphLeft = 6;
    private const double Gap = 8;

    private readonly Typeface _typeface;
    private readonly double _fontSize;
    private readonly double _rowHeight;
    private readonly double _glyphWidth;
    private IReadOnlyList<CompletionItem> _items = [];
    private EditorTheme _theme = EditorTheme.Default;
    private int[] _matches = [];
    private int _selected = -1;
    private int _firstVisible;

    public CompletionListView(FontFamily fontFamily, double fontSize)
    {
        _typeface = new Typeface(fontFamily);
        _fontSize = fontSize;
        _rowHeight = Math.Ceiling(fontSize * 1.45);
        _glyphWidth = CreateText("M", Brushes.Black).WidthIncludingTrailingWhitespace;
        Width = ListWidth;
        MaxHeight = MaxVisibleRows * _rowHeight;
        ClipToBounds = true;
    }

    public event EventHandler? SelectionChanged;

    public event EventHandler? ItemActivated;

    /// <summary>Where item text starts, from the left edge: the list moves left by this to align it with the word.</summary>
    public double TextLeft => GlyphLeft + _glyphWidth + Gap;

    /// <summary>The index into the items of the selected match, or null.</summary>
    public int? SelectedItem => _selected >= 0 && _selected < _matches.Length ? _matches[_selected] : null;

    public EditorTheme EditorTheme
    {
        get => _theme;
        set
        {
            _theme = value;
            InvalidateVisual();
        }
    }

    /// <summary>Rows that fit: fewer than the maximum when the window is too small for the whole list.</summary>
    private int VisibleRows =>
        Math.Max(1, Math.Min(MaxVisibleRows, Bounds.Height > 0 ? (int)(Bounds.Height / _rowHeight) : MaxVisibleRows));

    public void SetItems(IReadOnlyList<CompletionItem> items)
    {
        _items = items;
        SetMatches([]);
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
        context.FillRectangle(_theme.WidgetBackground, new Rect(Bounds.Size));
        int last = Math.Min(_matches.Length, _firstVisible + VisibleRows);
        for (int row = _firstVisible; row < last; row++)
        {
            double y = (row - _firstVisible) * _rowHeight;
            bool selected = row == _selected;
            if (selected)
                context.FillRectangle(_theme.ListSelectionBackground, new Rect(0, y, Bounds.Width, _rowHeight));
            CompletionItem item = _items[_matches[row]];
            DrawText(context, KindGlyph(item.Kind), KindBrush(item.Kind), GlyphLeft, y);
            IBrush foreground = selected ? _theme.ListSelectionForeground : _theme.WidgetForeground;
            double x = TextLeft + DrawText(context, item.DisplayText, foreground, TextLeft, y) + Gap;
            if (item.InlineDescription is { } detail)
                DrawText(context, detail, _theme.WidgetSecondaryForeground, x, y);
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
        int maxFirst = Math.Max(0, _matches.Length - VisibleRows);
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
            else if (row >= _firstVisible + VisibleRows)
                _firstVisible = row - VisibleRows + 1;
        }
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private double DrawText(DrawingContext context, string text, IBrush brush, double x, double y)
    {
        FormattedText formatted = CreateText(text, brush);
        context.DrawText(formatted, new Point(x, y + (_rowHeight - formatted.Height) / 2));
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private FormattedText CreateText(string text, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, _fontSize, brush);

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

    private IBrush KindBrush(CompletionItemKind kind) =>
        kind switch
        {
            CompletionItemKind.Method or CompletionItemKind.ExtensionMethod => _theme.SymbolMethod,
            CompletionItemKind.Class
            or CompletionItemKind.Struct
            or CompletionItemKind.Delegate
            or CompletionItemKind.Interface
            or CompletionItemKind.Enum => _theme.SymbolType,
            CompletionItemKind.Keyword => _theme.SymbolKeyword,
            _ => _theme.SymbolVariable,
        };
}

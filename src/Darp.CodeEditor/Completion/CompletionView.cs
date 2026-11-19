using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// What completion shows, as VS Code's suggest widget: the list below the word, or above it with its bottom at the
/// line, its labels lined up with the word; and the selected item's details in a popup of their own beside it, as
/// <see cref="PopupPlacement.Beside"/> places them. Both are in the color theme's suggest colors, and neither takes the
/// keyboard focus. Their edges away from the word resize them: the list to whole rows, and both keep the size the user
/// gave them while the editor shows the document.
/// </summary>
internal sealed class CompletionView
{
    /// <summary>How many rows show at once by default, as in VS Code.</summary>
    public const int DefaultRows = 12;

    /// <summary>The list's width by default, its border included, as in VS Code.</summary>
    public const double DefaultWidth = 430;

    /// <summary>The narrowest the list gets, as in VS Code.</summary>
    private const double MinWidth = 220;

    /// <summary>The details' width by default, as in VS Code.</summary>
    private const double DefaultDetailsWidth = 330;

    /// <summary>The frames' border, on each side.</summary>
    private const double Border = 1;

    private readonly CompletionListBox _list = new();
    private readonly PopupScroller _details = new();
    private readonly EditorPopup _listPopup;
    private readonly EditorPopup _detailsPopup;
    private double _width = DefaultWidth;
    private int _rows = DefaultRows;
    private double _detailsWidth = DefaultDetailsWidth;

    /// <summary>The details' most height, as the user resized them; null for as tall as the list's rows.</summary>
    private double? _detailsHeight;

    /// <summary>Where the list is: the text view, and the box of the word's first character in it; null while closed.</summary>
    private (Visual RelativeTo, Func<Rect> Word)? _place;

    public CompletionView(TemplatedControl editor)
    {
        _listPopup = new EditorPopup(editor, _list, "CompletionList", PopupSide.Below);
        _detailsPopup = new EditorPopup(editor, _details, "CompletionDetails") { IsResizable = true };
        _detailsPopup.Frame.Padding = new Thickness(6, 4, 2, 4);
        _list.Clicked += index => Clicked?.Invoke(index);
        _listPopup.Resized += OnListResized;
        _detailsPopup.Resized += OnDetailsResized;
        _listPopup.LeftView += (_, _) =>
        {
            Hide();
            LeftView?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Raised when an item is clicked, with its index.</summary>
    public event Action<int>? Clicked;

    /// <summary>Raised when the list closed because its line left the view.</summary>
    public event EventHandler? LeftView;

    /// <summary>The list, for the rows that show.</summary>
    public CompletionListBox List => _list;

    /// <summary>Shows <paramref name="items"/>, with <paramref name="selected"/> selected and in view.</summary>
    /// <param name="items">The items.</param>
    /// <param name="selected">The selected item's index.</param>
    /// <param name="look">The editor's font, for the labels, and the popups' colors.</param>
    /// <param name="icons">The icons' colors; none while the color theme loads.</param>
    /// <param name="rowHeight">The height of a row: the editor's line height.</param>
    public void Show(
        IReadOnlyList<ShownItem> items,
        int selected,
        PopupLook look,
        SymbolIconColors? icons,
        double rowHeight
    )
    {
        Paint(look.Colors);
        _listPopup.IsResizable = true;
        _list.Width = _width - 2 * Border;
        _list.MaxRows = _rows;
        _list.Show(items, selected, look, icons, rowHeight);
    }

    /// <summary>Shows <paramref name="message"/>, such as "No suggestions.", half as wide as the list, without details.</summary>
    public void ShowMessage(string message, PopupLook look, double rowHeight)
    {
        Paint(look.Colors);
        // A message keeps its size, as in VS Code.
        _listPopup.IsResizable = false;
        _list.Width = DefaultWidth / 2 - 2 * Border;
        _list.ShowMessage(message, look, rowHeight);
        ShowDetails(null);
    }

    /// <summary>Opens the list at <paramref name="word"/>, the box of the word's first character, or moves it there.</summary>
    public void Open(Visual relativeTo, Func<Rect> word)
    {
        _place = (relativeTo, word);
        // Left of the word by the frame's border and a row's icon, so that the labels line up with the word.
        _listPopup.Show(relativeTo, () => word().Translate(new Vector(-(Border + CompletionListBox.LabelInset), 0)));
        if (_details.Child is not null)
            _detailsPopup.ShowBeside(_listPopup, _detailsWidth);
    }

    public void Hide()
    {
        _place = null;
        _detailsPopup.Hide();
        _listPopup.Hide();
    }

    /// <summary>Selects the item at <paramref name="index"/>, and scrolls it into view.</summary>
    public void Select(int index) => _list.Select(index);

    /// <summary>Shows the selected item's details beside the list; null hides them.</summary>
    public void ShowDetails(Control? details)
    {
        _details.Child = details;
        _list.SetDetailsOpen(details is not null);
        if (details is null)
        {
            _detailsPopup.Hide();
            return;
        }
        // As tall as the list's rows at most, so that it stays next to the list, unless the user resized it.
        _detailsPopup.Frame.MaxHeight = _detailsHeight ?? _list.RowHeight * Math.Max(_rows, DefaultRows) + 2 * Border;
        if (_listPopup.IsOpen)
            _detailsPopup.ShowBeside(_listPopup, _detailsWidth);
    }

    /// <summary>The list takes the width dragged to, and as many whole rows as fit the height.</summary>
    private void OnListResized(Size size)
    {
        if (_place is not var (relativeTo, word))
            return;
        _width = Math.Max(MinWidth, size.Width);
        _rows = Math.Max(1, (int)Math.Round((size.Height - 2 * Border) / _list.RowHeight));
        _list.Width = _width - 2 * Border;
        _list.MaxRows = _rows;
        Open(relativeTo, word);
    }

    private void OnDetailsResized(Size size)
    {
        _detailsWidth = Math.Max(PopupPlacement.MinBesideWidth, size.Width);
        _detailsHeight = Math.Max(3 * _list.RowHeight, size.Height);
        _detailsPopup.Frame.MaxHeight = _detailsHeight.Value;
        _detailsPopup.ShowBeside(_listPopup, _detailsWidth);
    }

    private void Paint(PopupColors colors)
    {
        _listPopup.Frame.Paint(colors.SuggestBackground, colors.SuggestBorder, colors.SuggestForeground, colors.Shadow);
        _detailsPopup.Frame.Paint(
            colors.SuggestBackground,
            colors.SuggestBorder,
            colors.WidgetForeground,
            colors.Shadow
        );
        _details.SliderBrushes = colors.Slider;
    }
}

/// <summary>An item that shows: the item, and the characters of its label that matched.</summary>
internal sealed record ShownItem(CompletionItem Item, int[] Highlights);

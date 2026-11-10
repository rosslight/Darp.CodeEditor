using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// What the completion popup shows, as VS Code's suggest widget: the list below the word, or above it with its bottom at
/// the line, with the labels lined up with the word; and the selected item's details in a popup of their own, beside
/// the list, as <see cref="EditorPopup.ShowBeside"/> places them. Both are the editor's own controls, in its color
/// theme's suggest colors. Neither takes the keyboard focus while the user types, so the editor keeps it; clicking an
/// item commits it, and the details' text can be selected and copied.
/// </summary>
internal sealed class CompletionView
{
    /// <summary>How many items show at once by default, as in VS Code.</summary>
    public const int DefaultRows = 12;

    /// <summary>The list's width by default, its border included, as in VS Code.</summary>
    public const double DefaultWidth = 430;

    /// <summary>The narrowest the list gets, as in VS Code.</summary>
    public const double MinWidth = 220;

    /// <summary>The details' width by default, as in VS Code.</summary>
    public const double DefaultDetailsWidth = 330;

    /// <summary>The frame's border, on each side.</summary>
    private const double Border = 1;

    private readonly CompletionListBox _list = new();
    private readonly PopupFrame _listFrame;
    private readonly PopupFrame _detailsFrame;
    private readonly PopupScroller _detailsContent = new();
    private readonly EditorPopup _listPopup;
    private readonly EditorPopup _detailsPopup;

    public CompletionView(CodeEditor editor)
    {
        _listFrame = new PopupFrame
        {
            Name = "CompletionList",
            Child = _list,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _detailsFrame = new PopupFrame
        {
            Name = "CompletionDetails",
            Child = _detailsContent,
            Padding = new Thickness(6, 4, 2, 4),
        };
        // Below the word, with the labels lined up with it.
        _listPopup = new EditorPopup(editor, _listFrame, below: true)
        {
            OffsetX = -(Border + CompletionListBox.LabelInset),
        };
        _detailsPopup = new EditorPopup(editor, _detailsFrame);
        _list.Clicked += index => Clicked?.Invoke(index);
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

    /// <summary>The list, for tests.</summary>
    public CompletionListBox List => _list;

    /// <summary>Whether the details show beside the list.</summary>
    public bool ShowsDetails => _detailsPopup.IsOpen;

    /// <summary>Shows <paramref name="items"/>, with <paramref name="selected"/> selected and in view.</summary>
    /// <param name="items">The items.</param>
    /// <param name="selected">The selected item's index.</param>
    /// <param name="look">The editor's font, for the labels, and the popups' colors.</param>
    /// <param name="icons">The icons' colors; none while the color theme loads.</param>
    /// <param name="rowHeight">The height of an item: the editor's line height.</param>
    public void Show(
        IReadOnlyList<ShownItem> items,
        int selected,
        PopupLook look,
        SymbolIconColors? icons,
        double rowHeight
    )
    {
        Paint(look.Colors);
        _list.Show(items, selected, look, icons, rowHeight);
    }

    /// <summary>Shows <paramref name="message"/> in the list, such as "No suggestions.", without items or details.</summary>
    public void ShowMessage(string message, PopupLook look, double rowHeight)
    {
        Paint(look.Colors);
        _list.ShowMessage(message, look, rowHeight);
        ShowDetails(null);
    }

    /// <summary>Opens the list at <paramref name="place"/>, the box of the word's first character, or moves it there.</summary>
    public void Open(Visual relativeTo, Func<Rect> place) => _listPopup.Show(relativeTo, place);

    public void Hide()
    {
        _detailsPopup.Hide();
        _listPopup.Hide();
    }

    /// <summary>Selects the item at <paramref name="index"/>, and scrolls it into view.</summary>
    public void Select(int index) => _list.Select(index);

    /// <summary>Shows the selected item's details beside the list; null hides them.</summary>
    public void ShowDetails(Control? details)
    {
        _detailsContent.Child = details;
        _list.DetailsOpen = details is not null;
        if (details is null)
        {
            _detailsPopup.Hide();
            return;
        }
        // At most as tall as the list's most rows, so that it stays next to the list.
        _detailsFrame.MaxHeight = Math.Max(_list.RowHeight * _list.MaxRows, _list.RowHeight * DefaultRows) + 2 * Border;
        if (_listPopup.IsOpen)
            _detailsPopup.ShowBeside(_listPopup, DefaultDetailsWidth);
    }

    private void Paint(PopupColors colors)
    {
        _listFrame.Paint(colors.SuggestBackground, colors.SuggestBorder, colors.SuggestForeground, colors.Shadow);
        _detailsFrame.Paint(colors.SuggestBackground, colors.SuggestBorder, colors.WidgetForeground, colors.Shadow);
        _detailsContent.SliderBrushes = (colors.Slider, colors.SliderHover, colors.SliderActive);
    }
}

/// <summary>An item that shows: the item, and the characters of its label that matched.</summary>
internal sealed record ShownItem(CompletionItem Item, int[] Highlights);

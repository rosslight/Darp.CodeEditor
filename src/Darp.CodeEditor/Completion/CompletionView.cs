using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// What completion shows, as VS Code's suggest widget: the list below the word, or above it with its bottom at the
/// line, its labels lined up with the word; and the selected item's details in a popup of their own beside it, as
/// <see cref="PopupPlacement.Beside"/> places them. The list is in the color theme's suggest colors, the details look as
/// every popup of code and docs does (<see cref="HoverView"/>), and neither takes the keyboard focus. Their edges away from the word resize them: the list to whole rows, and both keep the size the user
/// gave them while the editor shows the document. Rows are as high as the text view's lines, and labels and code take
/// the editor's font.
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

    private readonly CodeEditor _editor;
    private readonly TextView _textView;
    private readonly CompletionListBox _list = new();
    private readonly PopupScrollViewer _details = new();
    private readonly EditorPopup _listPopup;
    private readonly EditorPopup _detailsPopup;
    private double _width = DefaultWidth;
    private int _rows = DefaultRows;
    private double _detailsWidth = DefaultDetailsWidth;

    /// <summary>The details' most height, as the user resized them; null for as tall as the list's rows.</summary>
    private double? _detailsHeight;

    /// <summary>Where the word that the list completes starts; null while the list is closed.</summary>
    private int? _wordStart;

    /// <summary>Whether "No suggestions." shows, for an empty answer to a request by key.</summary>
    private bool _showsNoSuggestions;

    public CompletionView(CodeEditor editor, TextView textView)
    {
        _editor = editor;
        _textView = textView;
        // Left of the word by the frame's border and a row's icon, so that the labels line up with the word.
        _listPopup = new EditorPopup(editor, new PopupFrame { Name = "CompletionList", Child = _list }, PopupSide.Below)
        {
            Inset = Border + CompletionListBox.LabelInset,
        };
        _detailsPopup = new EditorPopup(editor, new PopupFrame { Name = "CompletionDetails", Child = _details })
        {
            IsResizable = true,
        };
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

    /// <summary>The popups' colors, from the color theme once it is loaded.</summary>
    public PopupColors Colors { get; set; } = PopupColors.Default;

    /// <summary>The icons' colors; none while the color theme loads.</summary>
    public SymbolIconColors? Icons { get; set; }

    /// <summary>How code looks in the popups: the editor's font, on the text area's colors.</summary>
    public CodeLook Look => new(_editor.FontFamily, _editor.FontSize, Colors);

    /// <summary>
    /// Shows <paramref name="items"/>, with <paramref name="selected"/> selected and in view, at the word that starts at
    /// <paramref name="wordStart"/>.
    /// </summary>
    public void Show(IReadOnlyList<ShownItem> items, int selected, int wordStart)
    {
        Paint();
        _listPopup.IsResizable = true;
        _list.Width = _width - 2 * Border;
        _list.MaxRows = _rows;
        _list.Show(items, selected, Look, Colors, Icons, _textView.DefaultLineHeight);
        Open(wordStart);
    }

    /// <summary>
    /// Says "No suggestions." in the list, half as wide as it, without details, at the word that starts at
    /// <paramref name="wordStart"/>.
    /// </summary>
    public void ShowNoSuggestions(int wordStart)
    {
        Paint();
        // A message keeps its size, as in VS Code.
        _listPopup.IsResizable = false;
        _list.Width = DefaultWidth / 2 - 2 * Border;
        _list.ShowMessage("No suggestions.", Look, Colors, _textView.DefaultLineHeight);
        ShowDetails(null);
        Open(wordStart);
        _showsNoSuggestions = true;
    }

    /// <summary>Hides "No suggestions.", if it shows.</summary>
    /// <returns>Whether it showed.</returns>
    public bool HideNoSuggestions()
    {
        if (!_showsNoSuggestions)
            return false;
        Hide();
        return true;
    }

    public void Hide()
    {
        _showsNoSuggestions = false;
        _wordStart = null;
        _detailsPopup.Hide();
        _listPopup.Hide();
    }

    /// <summary>Selects the item at <paramref name="index"/>, and scrolls it into view.</summary>
    public void Select(int index) => _list.Select(index);

    /// <summary>Shows the selected item's details beside the list; null hides them.</summary>
    public void ShowDetails(Control? details)
    {
        _details.Content = details;
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

    /// <summary>Opens the list at the word that starts at <paramref name="wordStart"/>, or moves it there.</summary>
    private void Open(int wordStart)
    {
        _wordStart = wordStart;
        _listPopup.Show(
            _textView,
            () => TextViewBoxes.CharacterAt(_textView, Math.Min(wordStart, _textView.Document.TextLength))
        );
        if (_details.Content is not null)
            _detailsPopup.ShowBeside(_listPopup, _detailsWidth);
    }

    /// <summary>
    /// The list takes the width dragged to, and as many whole rows as fit the height, each only when the user dragged an
    /// edge that changes it: a list of one row that is made wider still shows more rows when it has more items.
    /// </summary>
    private void OnListResized(Size size, Edges edges)
    {
        if (_wordStart is not { } wordStart)
            return;
        if ((edges & (Edges.Left | Edges.Right)) != 0)
            _width = Math.Max(MinWidth, size.Width);
        if ((edges & (Edges.Top | Edges.Bottom)) != 0)
            _rows = Math.Max(1, (int)Math.Round((size.Height - 2 * Border) / _list.RowHeight));
        _list.Width = _width - 2 * Border;
        _list.MaxRows = _rows;
        Open(wordStart);
    }

    private void OnDetailsResized(Size size, Edges edges)
    {
        if ((edges & (Edges.Left | Edges.Right)) != 0)
            _detailsWidth = Math.Max(PopupPlacement.MinBesideWidth, size.Width);
        if ((edges & (Edges.Top | Edges.Bottom)) != 0)
        {
            _detailsHeight = Math.Max(3 * _list.RowHeight, size.Height);
            _detailsPopup.Frame.MaxHeight = _detailsHeight.Value;
        }
        _detailsPopup.ShowBeside(_listPopup, _detailsWidth);
    }

    private void Paint()
    {
        _listPopup.Frame.Paint(Colors.SuggestBackground, Colors.SuggestBorder, Colors.SuggestForeground, Colors);
        _detailsPopup.Frame.PaintDocs(Colors);
    }
}

/// <summary>An item that shows: the item, and the characters of its label that matched.</summary>
internal sealed record ShownItem(CompletionItem Item, int[] Highlights);

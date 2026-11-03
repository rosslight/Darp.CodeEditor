using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.LanguageServices;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// The completion popup. It filters the items it was created with whenever the typed word changes, with ordinal
/// matching (well under a millisecond for thousands of items), and hands the selected item to its owner to commit.
/// </summary>
/// <remarks>
/// Positioning, keyboard routing and offset tracking come from AvaloniaEdit's <see cref="CompletionWindowBase"/>.
/// </remarks>
internal sealed class CompletionPopup : CompletionWindowBase
{
    private static readonly IBrush s_background = new ImmutableSolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush s_border = new ImmutableSolidColorBrush(Color.Parse("#454545"));

    private IReadOnlyList<CompletionItem> _items;
    private Func<int, Control> _createDescription;
    private Action<int, int, int> _commit;
    private readonly Dictionary<int, Control> _descriptions = [];
    private readonly CompletionListView _view;
    private readonly Popup _descriptionPopup;
    private readonly Border _descriptionBorder;
    private int[] _matches = [];
    private string? _query;

    /// <param name="createDescription">Creates the description view for the item at an index, loading lazily.</param>
    /// <param name="commit">Commits the item at an index over the word from a start to an end offset.</param>
    public CompletionPopup(
        TextArea textArea,
        IReadOnlyList<CompletionItem> items,
        Func<int, Control> createDescription,
        Action<int, int, int> commit
    )
        : base(textArea)
    {
        _items = items;
        _createDescription = createDescription;
        _commit = commit;
        _view = new CompletionListView(items, textArea.FontFamily, textArea.FontSize);
        _view.SelectionChanged += (_, _) => ShowDescription();
        _view.ItemActivated += (_, _) => Commit();
        Child = new Border
        {
            BorderBrush = s_border,
            BorderThickness = new Thickness(1),
            Child = _view,
        };

        _descriptionBorder = new Border
        {
            Background = s_background,
            BorderBrush = s_border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4),
            MaxWidth = 500,
        };
        _descriptionPopup = new Popup
        {
            PlacementTarget = _view,
            Placement = PlacementMode.RightEdgeAlignedTop,
            Child = _descriptionBorder,
        };
        LogicalChildren.Add(_descriptionPopup);
        // The description is placed next to the list, which needs to be open first.
        Opened += (_, _) => ShowDescription();
        TextArea.Caret.PositionChanged += OnCaretPositionChanged;
    }

    /// <summary>The selected item, which typing a commit character commits.</summary>
    public CompletionItem? SelectedItem => _view.SelectedItem is { } index ? _items[index] : null;

    /// <summary>Closes the popup when the caret moves back to <see cref="CompletionWindowBase.StartOffset"/>.</summary>
    public bool CloseWhenCaretAtBeginning { get; init; }

    /// <summary>
    /// Shows other items in place of the current ones, such as the next list of an incomplete one, and filters them.
    /// Returns false if none match.
    /// </summary>
    public bool ReplaceItems(
        IReadOnlyList<CompletionItem> items,
        Func<int, Control> createDescription,
        Action<int, int, int> commit
    )
    {
        _items = items;
        _createDescription = createDescription;
        _commit = commit;
        _descriptions.Clear();
        _query = null;
        _view.SetItems(items);
        bool matched = Filter();
        ShowDescription();
        return matched;
    }

    /// <summary>Filters the items by the text between the start offset and the caret. Returns false if none match.</summary>
    public bool Filter()
    {
        string query = TextArea.Document.GetText(StartOffset, TextArea.Caret.Offset - StartOffset);
        if (query == _query)
            return _matches.Length > 0;
        // Typing on narrows the previous matches; anything else starts from all items.
        IEnumerable<int> candidates =
            _query is { Length: > 0 } previous && query.StartsWith(previous, StringComparison.Ordinal)
                ? _matches
                : Enumerable.Range(0, _items.Count);
        _matches =
        [
            .. candidates
                .Select(index => (Index: index, Score: CompletionMatcher.Score(_items[index].FilterText, query)))
                .Where(m => m.Score > 0)
                .OrderByDescending(m => m.Score)
                .ThenBy(m => m.Index)
                .Select(m => m.Index),
        ];
        _query = query;
        _view.SetMatches(_matches);
        return _matches.Length > 0;
    }

    public void Commit()
    {
        if (_view.SelectedItem is not { } index)
            return;
        int start = StartOffset;
        int end = EndOffset;
        Hide();
        _commit(index, start, end);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _view.Move(1);
                break;
            case Key.Up:
                _view.Move(-1);
                break;
            case Key.PageDown:
                _view.Move(10);
                break;
            case Key.PageUp:
                _view.Move(-10);
                break;
            case Key.Enter or Key.Tab:
                Commit();
                break;
            case Key.Escape:
                Hide();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    protected override void DetachEvents()
    {
        TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
        _descriptionPopup.IsOpen = false;
        base.DetachEvents();
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        int caret = TextArea.Caret.Offset;
        if (
            caret < StartOffset
            || caret > EndOffset
            || (caret == StartOffset && CloseWhenCaretAtBeginning)
            || !Filter()
        )
            Hide();
    }

    private void ShowDescription()
    {
        if (!IsOpen || _view.SelectedItem is not { } index)
        {
            _descriptionPopup.IsOpen = false;
            return;
        }
        if (!_descriptions.TryGetValue(index, out Control? description))
        {
            description = _createDescription(index);
            _descriptions[index] = description;
        }
        _descriptionBorder.Child = description;
        _descriptionPopup.IsOpen = true;
    }
}
